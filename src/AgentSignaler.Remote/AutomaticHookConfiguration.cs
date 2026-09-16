namespace AgentSignaler.Remote;

public static class AutomaticHookConfiguration
{
    public static IntegrationTarget Custom(string kind, string path)
    {
        path = IntegrationScope.CanonicalDirectory(path);
        var directory = kind == "vscode" ? Path.Combine(path, "agent-signaler-hooks") : path;
        var identity = kind == "copilot-cli" && string.Equals(Path.GetFileName(directory), "hooks", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory) ?? directory : path;
        var id = IntegrationScope.Id(kind, identity);
        var target = new IntegrationTarget
        {
            Id = IntegrationScope.Id("custom-" + kind, path),
            Kind = kind,
            DisplayName = kind switch
            {
                "copilot-cli" => "Custom Copilot CLI",
                "visual-studio" => "Custom Visual Studio Copilot",
                "vscode" => "Custom VS Code profile",
                _ => throw new InvalidDataException("Select a supported Copilot integration.")
            },
            InstallationId = id, ScopeId = id, HookDirectory = directory,
            SettingsPath = kind == "vscode" ? Path.Combine(path, "settings.json") : null,
            IsCustom = true, Provenance = "user-selected local path"
        };
        target.Validate();
        return target;
    }

    public static IntegrationTarget Prepare(IntegrationTarget target, string configPath)
    {
        target.Validate();
        var resolved = HookVerification.Resolve(target, configPath);
        if (resolved.Capability is IntegrationCapability.NotInstalled or IntegrationCapability.DiscoveryFailed or
            IntegrationCapability.BlockedByPolicy or IntegrationCapability.VerificationFailed)
            throw new InvalidOperationException($"{target.DisplayName}: {resolved.Reason}");
        if (resolved.Capability is IntegrationCapability.Verified or IntegrationCapability.PartiallySupported)
            return resolved;
        return target with
        {
            Capability = IntegrationCapability.Configured,
            SupportedEvents = HookAdapters.Events(target.Kind),
            Reason = "Hooks are configured automatically. Actual host event delivery is not verified; policy, trust and host support still apply."
        };
    }

    public static void RequireInstallable(IntegrationTarget target, string configPath)
    {
        if (target.Capability != IntegrationCapability.Configured)
        {
            HookVerification.RequireVerified(target, configPath);
            return;
        }
        var prepared = Prepare(target, configPath);
        if (!prepared.SupportedEvents.SequenceEqual(target.SupportedEvents, StringComparer.Ordinal))
            throw new InvalidOperationException("Hook capabilities changed. Refresh the locations and apply a new preview.");
    }
}
