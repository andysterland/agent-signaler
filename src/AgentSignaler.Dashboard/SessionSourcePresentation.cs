using AgentSignaler.Contracts;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal static class SessionSourcePresentation
{
    public static string DisplayName(MachineView? machine, TranscriptSelection selection)
    {
        if (machine?.MachineId != selection.MachineId) return selection.SessionId;
        var key = SourceIdentity.SessionKey(selection.Source, selection.SessionId);
        return machine.Sessions.FirstOrDefault(session =>
            SourceIdentity.SessionKey(session.Source ?? SourceDescriptor.LegacyCli, session.SessionId) == key)
            ?.DisplayName ?? selection.SessionId;
    }

    public static string DescribeStream(TranscriptSessionInfo session, string displayName) =>
        $"{displayName} · {Describe(session.Selection.Source)} · Session ID: {session.Selection.SessionId}" +
        $" · stream {session.Selection.StreamId}" + (session.Closed ? " · ended/closed (retained)" : "");

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

    internal static string Name(string kind) => SessionPresentation.SourceLabel(kind);
}
