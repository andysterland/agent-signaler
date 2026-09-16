using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSignaler.Remote;

public sealed record DiscoveryResult(string CopilotHome, string? CliPath, string CliVersion,
    bool CliSupported, bool VsCodeDetected, bool VsCodeCopilotDetected, bool VisualStudioDetected, bool VisualStudioCopilotDetected)
{
    public string Summary => $"Standalone Copilot CLI: {CliPath ?? "not found"} ({CliVersion}); home: {CopilotHome}\n" +
        "Visual Studio and local VS Code profiles require independent IDE-originated hook verification.";
}

public sealed record InstallationProbeResult(bool Success, string Output, string Diagnostic);

public interface IInstallationProbe
{
    Task<InstallationProbeResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class BoundedInstallationProbe : IInstallationProbe
{
    public async Task<InstallationProbeResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            if (Path.GetExtension(executable) is ".cmd" or ".bat")
            {
                if (executable.IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0 ||
                    !arguments.SequenceEqual(new[] { "--version" }))
                    return new(false, "", "The CLI command shim path cannot be safely probed.");
                start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                start.Arguments = $"/d /s /c \"\"{executable}\" --version\"";
            }
            else foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start.");
            try
            {
                var output = ReadBoundedAsync(process.StandardOutput, budget.Token);
                var errors = ReadBoundedAsync(process.StandardError, budget.Token);
                await process.WaitForExitAsync(budget.Token);
                var text = await output;
                await errors;
                return process.ExitCode == 0 ? new(true, text, "") : new(false, "", "Version/inventory probe returned a nonzero exit code.");
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                cancellationToken.ThrowIfCancellationRequested();
                return new(false, "", "Version/inventory probe timed out after three seconds.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return new(false, "", $"Version/inventory probe failed ({ex.GetType().Name}).");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer, token)) != 0)
        {
            if (text.Length + read > 65536) throw new InvalidDataException("Installation probe output exceeded its bound.");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
}

public sealed record DiscoveryEnvironment(string UserProfile, string LocalAppData, string AppData,
    string ProgramFiles, string ProgramFilesX86, string PathVariable, string? CopilotHome)
{
    public static DiscoveryEnvironment Current => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetEnvironmentVariable("PATH") ?? "", Environment.GetEnvironmentVariable("COPILOT_HOME"));
}

public static class Discovery
{
    private static readonly ConcurrentDictionary<string, InstallationProbeResult> VersionCache = new(StringComparer.OrdinalIgnoreCase);
    public static string VisualStudioCopilotHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio", "CopilotCli");

    public static async Task<DiscoveryResult> InspectAsync()
    {
        var targets = await InspectTargetsAsync();
        var cli = targets.First(t => t.Kind == "copilot-cli");
        return new(Path.GetDirectoryName(cli.HookDirectory)!, cli.ExecutablePath, cli.HostVersion, cli.CanInstall,
            targets.Any(t => t.Kind == "vscode"), false, targets.Any(t => t.Kind == "visual-studio"),
            targets.Any(t => t.Kind == "visual-studio" &&
                string.Equals(Path.GetFileName(t.ExecutablePath), "copilot.exe", StringComparison.OrdinalIgnoreCase)));
    }

    public static Task<IReadOnlyList<IntegrationTarget>> InspectTargetsAsync(string? configPath = null,
        IReadOnlyList<string>? customProfileRoots = null, CancellationToken cancellationToken = default) =>
        InspectTargetsAsync(DiscoveryEnvironment.Current, new BoundedInstallationProbe(), configPath, customProfileRoots, cancellationToken);

    public static async Task<IReadOnlyList<IntegrationTarget>> InspectTargetsAsync(DiscoveryEnvironment environment,
        IInstallationProbe probe, string? configPath = null, IReadOnlyList<string>? customProfileRoots = null,
        CancellationToken cancellationToken = default)
    {
        using var refreshBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshBudget.CancelAfter(TimeSpan.FromSeconds(20));
        cancellationToken = refreshBudget.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var targets = new List<IntegrationTarget>();
        var savedTargets = configPath is not null && File.Exists(configPath)
            ? RemoteConfiguration.Load(configPath).Integrations : [];
        var defaultHome = Path.Combine(environment.UserProfile, ".copilot");
        var home = defaultHome;
        string? homeError = null;
        try
        {
            home = IntegrationScope.CanonicalDirectory(string.IsNullOrWhiteSpace(environment.CopilotHome)
                ? defaultHome : environment.CopilotHome);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            homeError = "Effective COPILOT_HOME is invalid or inaccessible. Standalone CLI setup is blocked; IDE discovery remains independent.";
        }
        var cli = FindOnPath(environment.PathVariable, "copilot.exe") ??
            FindOnPath(environment.PathVariable, "copilot.cmd") ?? FindOnPath(environment.PathVariable, "copilot.bat");
        var version = cli is null ? new InstallationProbeResult(false, "", "Standalone copilot.exe is not on PATH.")
            : await VersionAsync(cli, probe, cancellationToken);
        var cliVersion = VersionText(version.Output);
        var supported = homeError is null && version.Success && Version.TryParse(cliVersion, out var parsed) && parsed >= new Version(1, 0, 80);
        targets.Add(new IntegrationTarget
        {
            Id = IntegrationScope.Id("copilot-cli", home), Kind = "copilot-cli", DisplayName = "Standalone Copilot CLI",
            InstallationId = cli ?? "standalone-cli", ExecutablePath = cli, HostVersion = cliVersion,
            ScopeId = IntegrationScope.Id("copilot-cli", home), HookDirectory = Path.Combine(home, "hooks"),
            Capability = supported ? IntegrationCapability.Verified : homeError is not null ? IntegrationCapability.DiscoveryFailed :
                cli is null ? IntegrationCapability.NotInstalled : IntegrationCapability.DiscoveryFailed,
            SupportedEvents = supported ? HookAdapters.Events("copilot-cli") : [],
            Provenance = "documented COPILOT_HOME", Reason = homeError ?? (supported ? "Standalone CLI direct-exec user hooks; independent of installed IDEs." :
                version.Success ? "Requires CLI 1.0.80 or later." : version.Diagnostic)
        });
        var vswhere = Path.Combine(environment.ProgramFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            var inventory = await probe.RunAsync(vswhere, ["-all", "-prerelease", "-format", "json", "-utf8"], cancellationToken);
            if (!inventory.Success) targets.Add(FailedInventory(environment, inventory.Diagnostic));
            else
            {
                try
                {
                    using var instances = JsonDocument.Parse(inventory.Output);
                    if (instances.RootElement.GetArrayLength() > 64)
                        targets.Add(FailedInventory(environment, "More than 64 Visual Studio instances were returned; this bounded inventory is incomplete."));
                    foreach (var instance in instances.RootElement.EnumerateArray().Take(64))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = IntegrationScope.CanonicalDirectory(instance.GetProperty("installationPath").GetString()!);
                        var id = instance.GetProperty("instanceId").GetString()!;
                        var ideVersion = instance.GetProperty("installationVersion").GetString()!;
                        var host = Path.Combine(path, "Common7", "IDE", "CopilotCli", "copilot.exe");
                        var hostProbe = File.Exists(host) ? await VersionAsync(host, probe, cancellationToken) : null;
                        var hookHome = Path.Combine(environment.LocalAppData, "Microsoft", "VisualStudio", "CopilotCli", "hooks");
                        var launchable = instance.TryGetProperty("isLaunchable", out var launch) && launch.ValueKind == JsonValueKind.True;
                        targets.Add(new IntegrationTarget
                        {
                            Id = IntegrationScope.Id("visual-studio-instance", id), Kind = "visual-studio",
                            DisplayName = $"Visual Studio {Path.GetFileName(path)} ({id})", InstallationId = id,
                            ExecutablePath = File.Exists(host) ? host : Path.Combine(path, "Common7", "IDE", "devenv.exe"),
                            HostVersion = ideVersion + "/" + (hostProbe is null ? "unknown" : VersionText(hostProbe.Output)),
                            ScopeId = IntegrationScope.Id("visual-studio", hookHome), HookDirectory = hookHome,
                            Capability = launchable ? IntegrationCapability.VerificationRequired : IntegrationCapability.DiscoveryFailed,
                            Reason = !launchable ? "Installer does not report this instance as launchable." :
                                "Candidate shared hook home only; bundled CLI does not prove IDE hook support. " +
                                (hostProbe is { Success: false } ? hostProbe.Diagnostic : "Verify this instance in its actual agent experience."),
                            Provenance = "candidate (Visual Studio Installer)"
                        });
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
                {
                    targets.Add(FailedInventory(environment, "Visual Studio inventory is invalid; discovery is incomplete."));
                }
            }
        }
        else targets.Add(FailedInventory(environment, "Visual Studio Installer vswhere is unavailable; instances were not inferred from directory scans."));

        foreach (var channel in new[] { ("Code", "Microsoft VS Code", "Code.exe"), ("Code - Insiders", "Microsoft VS Code Insiders", "Code - Insiders.exe") })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shim = FindOnPath(environment.PathVariable, channel.Item1 == "Code" ? "code.cmd" : "code-insiders.cmd");
            var pathInstallation = shim is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(shim));
            var executable = new[]
            {
                Path.Combine(environment.ProgramFiles, channel.Item2, channel.Item3),
                Path.Combine(environment.LocalAppData, "Programs", channel.Item2, channel.Item3),
                pathInstallation is null ? "" : Path.Combine(pathInstallation, channel.Item3)
            }.FirstOrDefault(File.Exists);
            var root = Path.Combine(environment.AppData, channel.Item1, "User");
            if (!Directory.Exists(root) && executable is null) continue;
            var hostVersion = ReadIdeVersion(executable);
            AddProfile(targets, root, channel.Item1 + " default profile", executable, hostVersion, "documented user-data candidate");
            var profiles = Path.Combine(root, "profiles");
            try
            {
                if (Directory.Exists(profiles))
                {
                    var profileDirectories = Directory.EnumerateDirectories(profiles).Take(65).ToArray();
                    foreach (var profile in profileDirectories.Take(64))
                        AddProfile(targets, profile, channel.Item1 + " profile " + Path.GetFileName(profile), executable, hostVersion, "profile directory candidate");
                    if (profileDirectories.Length > 64)
                        targets.Add(targets.Last() with { Id = IntegrationScope.Id("vscode-limit", profiles),
                            Capability = IntegrationCapability.DiscoveryFailed, Reason = "More than 64 local profiles exist. Inventory is incomplete; select additional profile roots explicitly." });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                targets.Add(targets.Last() with { Id = IntegrationScope.Id("vscode-error", profiles),
                    Capability = IntegrationCapability.DiscoveryFailed, Reason = "Profile enumeration failed; select a local profile explicitly." });
            }
        }
        var selectedProfiles = (customProfileRoots ?? []).Concat(savedTargets.Where(t => t.Kind == "vscode" && !t.IsCustom && t.SettingsPath is not null)
            .Select(t => Path.GetDirectoryName(t.SettingsPath!)!)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in selectedProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var saved = savedTargets.FirstOrDefault(t => t.Kind == "vscode" && t.SettingsPath is not null &&
                string.Equals(Path.GetDirectoryName(t.SettingsPath), root, StringComparison.OrdinalIgnoreCase));
            var executable = saved?.ExecutablePath is { } path && File.Exists(path) ? path : null;
            var profileVersion = ReadIdeVersion(executable);
            AddProfile(targets, root, "VS Code selected local profile", executable, profileVersion, "user-selected profile; select its actual Code executable before verification");
        }
        foreach (var custom in savedTargets.Where(t => t.IsCustom))
        {
            var index = targets.FindIndex(t => t.Id == custom.Id);
            if (index >= 0) targets[index] = custom;
            else targets.Add(custom);
        }
        var distinct = targets.DistinctBy(t => t.Id).ToList();
        if (configPath is not null)
            for (var i = 0; i < distinct.Count; i++)
            {
                var saved = savedTargets.FirstOrDefault(t => t.Id == distinct[i].Id);
                if (saved is { Kind: "visual-studio" })
                    distinct[i] = distinct[i] with { HookDirectory = saved.HookDirectory, ScopeId = saved.ScopeId };
                distinct[i] = HookVerification.Resolve(distinct[i], configPath);
            }
        foreach (var missing in savedTargets.Where(t => distinct.All(found => found.Id != t.Id)))
            distinct.Add(missing with { Capability = IntegrationCapability.NotInstalled, SupportedEvents = [],
                Reason = "Previously configured installation/profile was not discovered. Keep its ownership for removal; reverify before repair." });
        return distinct;
    }

    private static void AddProfile(List<IntegrationTarget> targets, string root, string display, string? executable, string version, string provenance)
    {
        try { root = IntegrationScope.CanonicalDirectory(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            targets.Add(new IntegrationTarget
            {
                Id = IntegrationScope.Id("vscode", root), Kind = "vscode", DisplayName = display, InstallationId = root,
                ScopeId = IntegrationScope.Id("vscode", root), HookDirectory = root,
                Capability = IntegrationCapability.DiscoveryFailed,
                Reason = "Profile scope is inaccessible or is not an isolated physical local Windows directory. Other targets are still discovered."
            });
            return;
        }
        var scope = IntegrationScope.Id("vscode", root);
        targets.Add(new IntegrationTarget
        {
            Id = scope, Kind = "vscode", DisplayName = display, InstallationId = root,
            ExecutablePath = executable, HostVersion = version.Length <= 64 ? version : version[..64], ScopeId = scope,
            HookDirectory = Path.Combine(root, "agent-signaler-hooks"), SettingsPath = Path.Combine(root, "settings.json"),
            Capability = !Directory.Exists(root) ? IntegrationCapability.NotInstalled :
                executable is not null && version == "unknown" ? IntegrationCapability.DiscoveryFailed : IntegrationCapability.VerificationRequired,
            Provenance = provenance,
            Reason = !Directory.Exists(root) ? "Profile directory does not exist. Open/create the intended local profile in VS Code, then refresh." :
                executable is not null && version == "unknown" ? "Installed IDE version could not be read; select the correct executable or repair the installation." :
                "Local Windows only. Verify enabled agent implementation, policy, workspace trust, effective hook locations and cmd.exe shell in this profile. Extension files are not proof. Stop is last-observed Waiting, not success or session close."
        });
    }

    private static IntegrationTarget FailedInventory(DiscoveryEnvironment environment, string reason) => new()
    {
        Id = "visual-studio-discovery", Kind = "visual-studio", InstallationId = "installer",
        DisplayName = "Visual Studio inventory unavailable", ScopeId = "visual-studio-discovery",
        HookDirectory = Path.Combine(environment.LocalAppData, "Microsoft", "VisualStudio", "CopilotCli", "hooks"),
        Capability = IntegrationCapability.DiscoveryFailed, Reason = reason
    };

    private static string VersionText(string output)
    {
        var match = Regex.Match(output, @"\b\d+\.\d+\.\d+\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? match.Value : "unknown";
    }

    private static async Task<InstallationProbeResult> VersionAsync(string executable, IInstallationProbe probe, CancellationToken token)
    {
        try
        {
            var file = new FileInfo(executable);
            var key = executable + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            if (VersionCache.TryGetValue(key, out var cached)) return cached;
            var result = await probe.RunAsync(executable, ["--version"], token);
            if (result.Success)
            {
                if (VersionCache.Count >= 128) VersionCache.Clear();
                VersionCache[key] = result;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(false, "", "Version probe failed because the executable is unavailable or inaccessible.");
        }
    }

    private static string ReadIdeVersion(string? executable)
    {
        if (executable is null) return "unknown";
        try { return FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "unknown"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // The profile row explicitly reports this as a discovery failure, never as supported or absent.
            return "unknown";
        }
    }

    private static string? FindOnPath(string path, string file)
    {
        foreach (var entry in path.Split(Path.PathSeparator))
        {
            var trimmed = entry.Trim('"');
            if (!Path.IsPathFullyQualified(trimmed)) continue;
            var candidate = Path.Combine(trimmed, file);
            if (File.Exists(candidate) && !candidate.Contains(@"\Common7\IDE\CopilotCli\", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(candidate);
        }
        return null;
    }
}
