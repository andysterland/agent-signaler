using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public interface IHookConfigurationAdapter
{
    string Kind { get; }
    IReadOnlyList<string> Events { get; }
    byte[] Generate(IntegrationTarget target, string relayPath, string configPath, Guid? probeId);
}

public static class HookAdapters
{
    public const string Version = "1";
    private static readonly IHookConfigurationAdapter[] Adapters =
        [new CliHookConfigurationAdapter(), new VisualStudioHookConfigurationAdapter(), new VsCodeHookConfigurationAdapter()];
    public static IReadOnlyList<string> Events(string kind) => Adapter(kind).Events;
    public static byte[] Generate(IntegrationTarget target, string relayPath, string configPath, Guid? probeId = null)
    {
        target.Validate();
        RemotePaths.ValidateRelayPath(relayPath);
        IntegrationScope.CanonicalDirectory(Path.GetDirectoryName(configPath)!);
        if (probeId == Guid.Empty) throw new InvalidDataException("Invalid probe identifier.");
        return Adapter(target.Kind).Generate(target, relayPath, configPath, probeId);
    }

    private static IHookConfigurationAdapter Adapter(string kind) =>
        Adapters.SingleOrDefault(a => a.Kind == kind) ?? throw new InvalidDataException("Unsupported hook adapter.");

    internal static string[] Arguments(IntegrationTarget target, string configPath, string kind, Guid? probeId)
    {
        var args = new List<string>
        {
            "hook", "--event", kind, "--config", configPath, "--adapter", target.Kind,
            "--source", target.Kind, "--scope", target.ScopeId, "--source-version",
            target.Kind == "visual-studio" && !probeId.HasValue ? "shared" : target.HostVersion
        };
        if (probeId is { } id) { args.Add("--probe"); args.Add(id.ToString("D")); }
        return args.ToArray();
    }

    internal static byte[] Serialize(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true });
}

public sealed class CliHookConfigurationAdapter : IHookConfigurationAdapter
{
    public string Kind => "copilot-cli";
    public IReadOnlyList<string> Events { get; } = Array.AsReadOnly(new[]
    {
        "sessionStart", "userPromptSubmitted", "preToolUse", "postToolUse", "permissionRequest",
        "agentStop", "errorOccurred", "sessionEnd", "postToolUseFailure"
    });
    public byte[] Generate(IntegrationTarget target, string relayPath, string configPath, Guid? probeId) =>
        DirectExecution(target, relayPath, configPath, probeId);

    internal static byte[] DirectExecution(IntegrationTarget target, string relayPath, string configPath, Guid? probeId)
    {
        var events = probeId.HasValue ? HookAdapters.Events(target.Kind) : target.SupportedEvents;
        var hooks = events.ToDictionary(e => e, e => new[]
        {
            new { type = "command", exec = relayPath, args = HookAdapters.Arguments(target, configPath, e, probeId), timeoutSec = 3 }
        }, StringComparer.Ordinal);
        return HookAdapters.Serialize(new { version = 1, hooks });
    }
}

public sealed class VisualStudioHookConfigurationAdapter : IHookConfigurationAdapter
{
    public string Kind => "visual-studio";
    public IReadOnlyList<string> Events { get; } = new CliHookConfigurationAdapter().Events;
    public byte[] Generate(IntegrationTarget target, string relayPath, string configPath, Guid? probeId) =>
        CliHookConfigurationAdapter.DirectExecution(target, relayPath, configPath, probeId);
}

public sealed class VsCodeHookConfigurationAdapter : IHookConfigurationAdapter
{
    public string Kind => "vscode";
    public IReadOnlyList<string> Events { get; } = Array.AsReadOnly(new[]
        { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop" });
    public byte[] Generate(IntegrationTarget target, string relayPath, string configPath, Guid? probeId)
    {
        var events = probeId.HasValue ? Events : target.SupportedEvents;
        var hooks = events.ToDictionary(e => e, e => new[]
        {
            new { type = "command", windows = WindowsHookCommand.Encode(relayPath,
                HookAdapters.Arguments(target, configPath, e, probeId)), timeout = 3 }
        }, StringComparer.Ordinal);
        return HookAdapters.Serialize(new { hooks });
    }
}

public static class WindowsHookCommand
{
    // This contract is deliberately cmd.exe-only. Hosts selecting PowerShell must not be verified with it.
    public static string Encode(string executable, IReadOnlyList<string> arguments)
    {
        RemotePaths.ValidateRelayPath(executable);
        return string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote));
    }

    private static string Quote(string value)
    {
        if (value.Any(char.IsControl) || value.IndexOfAny(['"', '%', '!']) >= 0)
            throw new InvalidDataException("The verified cmd.exe adapter cannot safely encode quotes, percent expansion or delayed-expansion characters. Choose a different installation/configuration path.");
        // Every argument is quoted, including shell metacharacters; double terminal backslashes for CommandLineToArgvW.
        var trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }
}
