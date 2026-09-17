using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AgentSignaler.Remote;

[SupportedOSPlatform("windows")]
public sealed class WindowsIntegrationStartup : IIntegrationStartup
{
    private readonly Func<string> resolveDirectory;
    private string? directory;
    private readonly IStartupRegistry registry;

    public WindowsIntegrationStartup() : this(
        () => Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.DoNotVerify),
        new WindowsStartupRegistry()) { }

    internal WindowsIntegrationStartup(string directory, IStartupRegistry registry) : this(() => directory, registry) { }

    internal WindowsIntegrationStartup(Func<string> resolveDirectory, IStartupRegistry registry)
    {
        this.resolveDirectory = resolveDirectory;
        this.registry = registry;
    }

    private string ShortcutPath(string name)
    {
        if (name.Length != 45 || !name.StartsWith("AgentSignaler-Client-", StringComparison.Ordinal) ||
            name[21..].Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Invalid Client startup identity.");
        if (directory is null)
        {
            var resolved = resolveDirectory();
            if (string.IsNullOrWhiteSpace(resolved))
                throw new InvalidOperationException("The current user's Startup folder is unavailable. Refresh and retry.");
            // Resolve only on access and cache only success, so Configurator can open and retry unavailable folders.
            Interlocked.CompareExchange(ref directory, MultiTargetIntegrationManager.Canonical(resolved), null);
        }
        return MultiTargetIntegrationManager.Canonical(Path.Combine(directory, name + ".lnk"));
    }

    public string? Read(string name) => Capture(name).Command;

    public IntegrationStartupState Capture(string name)
    {
        var path = ShortcutPath(name);
        var bytes = File.Exists(path) ? AtomicFile.ReadBounded(path, 65536) : null;
        var shortcut = bytes is null ? null : WindowsStartupShortcut.Read(name, bytes);
        var legacy = registry.ReadLegacy(name);
        if (legacy is not null) _ = ParseCommand(name, legacy);
        if (shortcut is not null && legacy is not null && shortcut != legacy)
            throw new InvalidDataException("Conflicting Client startup registrations were preserved.");
        return new(shortcut ?? legacy, bytes, legacy,
            registry.ReadApproval(name + ".lnk", false), registry.ReadApproval(name, true));
    }

    public IntegrationStartupState Prepare(string name, IntegrationStartupState before, string? command)
    {
        Validate(name, before);
        var approval = before.ShortcutApproval;
        // Windows' disabled Run preference must follow the shortcut, never turn into a fresh enabled entry.
        if (before.LegacyCommand is not null && Disabled(before.LegacyApproval) &&
            !Disabled(approval))
            approval = before.LegacyApproval;
        return new(command, command is null ? null :
            before.Shortcut is not null && before.Command == command ? before.Shortcut :
            WindowsStartupShortcut.Create(name, command), null, approval, before.LegacyApproval);
    }

    public void Replace(string name, string? expected, string? command)
    {
        var before = Capture(name);
        if (before.Command != expected) throw new InvalidDataException("Startup registration changed.");
        var after = Prepare(name, before, command);
        try { ReplaceState(name, before, after); }
        catch (Exception failure) when (RemoteFailure.IsExpected(failure))
        {
            try { RestoreState(name, before, after); }
            catch (Exception rollback) when (RemoteFailure.IsExpected(rollback))
            { throw new AggregateException("Startup rollback is incomplete.", failure, rollback); }
            throw;
        }
    }

    public void ReplaceState(string name, IntegrationStartupState before, IntegrationStartupState after)
    {
        Validate(name, before);
        Validate(name, after);
        if (!IntegrationStartup.Equivalent(Capture(name), before))
            throw new InvalidDataException("Startup registration changed; unrelated entries will not be overwritten.");
        Change(name, before, after);
        if (!IntegrationStartup.Equivalent(Capture(name), after))
            throw new InvalidOperationException("Client startup registration could not be verified.");
    }

    public void RestoreState(string name, IntegrationStartupState before, IntegrationStartupState after)
    {
        Validate(name, before);
        Validate(name, after);
        var current = Capture(name);
        if (IntegrationStartup.Equivalent(current, before)) return;
        if ((!IntegrationStartup.Equal(current.Shortcut, before.Shortcut) &&
             !IntegrationStartup.Equal(current.Shortcut, after.Shortcut)) ||
            (current.LegacyCommand != before.LegacyCommand && current.LegacyCommand != after.LegacyCommand) ||
            (!IntegrationStartup.Equal(current.ShortcutApproval, before.ShortcutApproval) &&
             !IntegrationStartup.Equal(current.ShortcutApproval, after.ShortcutApproval)) ||
            !IntegrationStartup.Equal(current.LegacyApproval, before.LegacyApproval))
            throw new InvalidDataException("Startup recovery will not overwrite concurrent edits.");
        Change(name, current, before);
        if (!IntegrationStartup.Equivalent(Capture(name), before))
            throw new InvalidOperationException("Client startup recovery could not be verified.");
    }

    public void RestoreLegacyState(string name, string? original, string? applied)
    {
        var current = Capture(name);
        if (current.Shortcut is not null)
            throw new InvalidDataException("A shortcut appeared during legacy startup recovery; it was preserved.");
        if (current.LegacyCommand == original) return;
        if (current.LegacyCommand != applied)
            throw new InvalidDataException("Legacy startup recovery will not overwrite concurrent edits.");
        // Journals without snapshots predate shortcuts and must restore the original Run source and its disabled state.
        ReplaceState(name, current, current with { Command = original, LegacyCommand = original });
    }

    private void Change(string name, IntegrationStartupState before, IntegrationStartupState after)
    {
        if (!IntegrationStartup.Equal(before.LegacyApproval, after.LegacyApproval))
            throw new InvalidDataException("Legacy Startup Apps preferences cannot be changed.");
        var path = ShortcutPath(name);
        // Remove the old launch source first. The enclosing journal can recover this intermediate state.
        if (before.LegacyCommand is not null && before.LegacyCommand != after.LegacyCommand)
            registry.ReplaceLegacy(name, before.LegacyCommand, null);
        void ReplaceShortcut()
        {
            var current = File.Exists(path) ? AtomicFile.ReadBounded(path, 65536) : null;
            if (!IntegrationStartup.Equal(current, before.Shortcut))
                throw new InvalidDataException("Startup shortcut changed; it will not be overwritten.");
            if (after.Shortcut is null) File.Delete(path);
            else AtomicFile.Write(path, after.Shortcut);
        }
        var shortcutChanged = !IntegrationStartup.Equal(before.Shortcut, after.Shortcut);
        if (shortcutChanged && after.Shortcut is null) ReplaceShortcut();
        if (!IntegrationStartup.Equal(before.ShortcutApproval, after.ShortcutApproval))
            registry.ReplaceApproval(name + ".lnk", before.ShortcutApproval, after.ShortcutApproval);
        if (shortcutChanged && after.Shortcut is not null) ReplaceShortcut();
        if (after.LegacyCommand is not null && before.LegacyCommand != after.LegacyCommand)
            registry.ReplaceLegacy(name, null, after.LegacyCommand);
    }

    private static void Validate(string name, IntegrationStartupState state)
    {
        IntegrationStartup.ValidateJournalStates(state, state, state.Command, state.Command);
        if (state.Command is not null) _ = ParseCommand(name, state.Command);
        var shortcut = state.Shortcut is null ? null : WindowsStartupShortcut.Read(name, state.Shortcut);
        if ((shortcut ?? state.LegacyCommand) != state.Command ||
            (shortcut is not null && shortcut != state.Command))
            throw new InvalidDataException("Invalid owned startup snapshot.");
    }

    public bool IsDisabled(string name)
    {
        var state = Capture(name);
        return state.Shortcut is not null ? Disabled(state.ShortcutApproval) :
            state.LegacyCommand is not null && Disabled(state.LegacyApproval);
    }

    private static bool Disabled(byte[]? value) => value is { Length: >= 4 } && value[0] is 3 or 7;

    internal static (string Client, string Config, string Arguments) ParseCommand(string name, string command)
    {
        if (command.Length > 32768 || command.Any(char.IsControl))
            throw new InvalidDataException("Invalid startup command.");
        var argv = CommandLineToArgvW(command, out var count);
        if (argv == IntPtr.Zero) throw new InvalidDataException("Invalid startup command.");
        try
        {
            if (count != 4) throw new InvalidDataException("Unrelated startup command was preserved.");
            var args = Enumerable.Range(0, count)
                .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
            if (args[1] != "--background" || args[2] != "--config" ||
                IntegrationStartup.Name(args[3]) != name || IntegrationStartup.Command(args[0], args[3]) != command)
                throw new InvalidDataException("Unrelated startup command was preserved.");
            return (args[0], args[3], "--background --config " + ScheduledTaskDefinition.QuoteArgument(args[3]));
        }
        finally { _ = LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal interface IStartupRegistry
{
    string? ReadLegacy(string name);
    byte[]? ReadApproval(string name, bool legacy);
    void ReplaceLegacy(string name, string? expected, string? command);
    void ReplaceApproval(string name, byte[]? expected, byte[]? value);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsStartupRegistry : IStartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public string? ReadLegacy(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        if (value is not string command || key!.GetValueKind(name) != RegistryValueKind.String)
            throw new InvalidDataException("An unrelated startup value occupies the Client registration.");
        return command;
    }

    public byte[]? ReadApproval(string name, bool legacy)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Approved + (legacy ? "Run" : "StartupFolder"));
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        if (value is not byte[] bytes || bytes.Length is < 4 or > 256 ||
            key!.GetValueKind(name) != RegistryValueKind.Binary)
            throw new InvalidDataException("An unrelated Startup Apps preference was preserved.");
        return bytes;
    }

    public void ReplaceLegacy(string name, string? expected, string? command)
    {
        if (ReadLegacy(name) != expected) throw new InvalidDataException("Legacy startup registration changed.");
        if (expected == command) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (command is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, command, RegistryValueKind.String);
    }

    public void ReplaceApproval(string name, byte[]? expected, byte[]? value)
    {
        if (!IntegrationStartup.Equal(ReadApproval(name, false), expected))
            throw new InvalidDataException("Windows Startup Apps preference changed.");
        using var key = Registry.CurrentUser.CreateSubKey(Approved + "StartupFolder", writable: true);
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.Binary);
    }
}
