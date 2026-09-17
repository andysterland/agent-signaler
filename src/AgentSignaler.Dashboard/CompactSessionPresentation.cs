using System.Collections.Immutable;
using AgentSignaler.Contracts;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal sealed record CompactSessionIndicator(string Key, AgentState State, string Label, int Left, int Top);
internal sealed record CompactSessionLayout(ImmutableArray<CompactSessionIndicator> Indicators, int IndicatorHeight, int TileHeight);

internal static class CompactSessionPresentation
{
    public const int SquareSize = 4;
    public const int Gap = 1;
    public const int RegionWidth = 50;
    public const int Columns = (RegionWidth + Gap) / (SquareSize + Gap);
    public const int BaseTileHeight = 64;

    public static CompactSessionLayout Project(MachineView machine, DateTimeOffset now)
    {
        var sessions = SessionPresentation.Project(machine, now).Where(session => session.IsConnected).ToArray();
        var indicators = sessions.Select((session, index) => new CompactSessionIndicator(session.SessionKey, session.State,
            $"{session.SourceLabel} · scope {session.Source.ScopeId} · session {session.DisplayName}: " +
            (session.State == AgentState.Waiting ? "Waiting for input" : session.State.ToString()),
            index % Columns * (SquareSize + Gap), index / Columns * (SquareSize + Gap))).ToImmutableArray();
        var rows = (sessions.Length + Columns - 1) / Columns;
        var height = rows == 0 ? 0 : rows * (SquareSize + Gap) - Gap;
        return new(indicators, height, BaseTileHeight + (height == 0 ? 0 : height + 2));
    }
}
