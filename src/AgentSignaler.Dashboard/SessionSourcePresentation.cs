using AgentSignaler.Contracts;

namespace AgentSignaler.Dashboard;

internal static class SessionSourcePresentation
{
    public static string Describe(SourceDescriptor? source)
    {
        source ??= SourceDescriptor.LegacyCli;
        return $"{Name(source.Kind)} · scope {source.ScopeId} · {source.Version}";
    }

    public static string Summary(IEnumerable<SessionSnapshot> sessions)
    {
        var names = sessions.Select(s => Name((s.Source ?? SourceDescriptor.LegacyCli).Kind))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return names.Length == 0 ? "None observed" : string.Join(", ", names);
    }

    internal static string Name(string kind) => kind switch
    {
        "copilot-cli" => "Copilot CLI",
        "visual-studio" => "Visual Studio",
        "vscode" => "VS Code",
        _ => kind
    };
}
