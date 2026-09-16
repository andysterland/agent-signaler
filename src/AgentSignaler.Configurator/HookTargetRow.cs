using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentSignaler.Remote;

namespace AgentSignaler.Configurator;

public sealed class HookTargetRow : INotifyPropertyChanged
{
    private bool enabled;
    private readonly bool initialEnabled;
    private string kind;
    private string location;

    public HookTargetRow(IntegrationTarget? target = null, bool enabled = false)
    {
        Target = target;
        this.enabled = enabled;
        initialEnabled = enabled;
        kind = target?.Kind ?? "copilot-cli";
        location = target is null ? "" : target.Kind == "vscode"
            ? Path.GetDirectoryName(target.SettingsPath) ?? target.HookDirectory : target.HookDirectory;
    }

    public IntegrationTarget? Target { get; }
    public bool IsDraft => Target is null;
    public bool IsLocationReadOnly => !IsDraft;
    public string DisplayName => Target?.DisplayName ?? "New custom location";
    public IReadOnlyList<string> IntegrationKinds { get; } = ["copilot-cli", "visual-studio", "vscode"];
    public bool HasPendingChange => Enabled != initialEnabled;
    public string Status => HasPendingChange
        ? Enabled ? "Will enable on Apply settings" : "Will disable on Apply settings"
        : Target?.Capability switch
    {
        null => "Enter a path and tick Enable",
        IntegrationCapability.VerificationRequired => "Ready to configure",
        IntegrationCapability.Configured => "Configured (not verified)",
        _ => Target.Capability.ToString()
    };
    public string Details => Target is null
        ? "CLI / Visual Studio: hook directory. VS Code: profile directory containing settings.json."
        : $"{Target.Reason}\nHooks: {Target.HookDirectory}\nSettings: {Target.SettingsPath ?? "(none)"}";

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            Set(ref enabled, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPendingChange)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }
    public string Kind { get => kind; set => Set(ref kind, value); }
    public string Location { get => location; set => Set(ref location, value); }

    public IntegrationTarget Prepare(string configPath) =>
        AutomaticHookConfiguration.Prepare(Target ?? AutomaticHookConfiguration.Custom(Kind, Location.Trim()), configPath);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
