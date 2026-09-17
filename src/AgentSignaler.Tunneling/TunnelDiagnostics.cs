// Keep debugger timing output available in Release builds.
#define DEBUG

using System.Diagnostics;

namespace AgentSignaler.Tunneling;

internal sealed class TunnelDiagnostics(Action<string>? output = null)
{
    private static long nextId;

    internal Scope Begin(string stage, long? parentId = null) => new(this, stage, parentId);

    private void Write(string message)
    {
        if (output is not null) output(message);
        else if (Debugger.IsAttached) Debug.WriteLine(message);
    }

    // Only fixed command names are logged, never resource IDs, paths, accounts, or CLI output.
    internal static string CommandName(IReadOnlyList<string> arguments) => arguments switch
    {
        ["--version"] => "--version",
        ["user", "show", ..] => "user show",
        ["user", "logout", ..] => "user logout",
        ["port", "show", ..] => "port show",
        ["port", "create", ..] => "port create",
        ["access", "list", ..] => "access list",
        ["access", "create", ..] => "access create",
        ["show", ..] => "show",
        ["create", ..] => "create",
        ["delete", ..] => "delete",
        ["host", ..] => "host",
        _ => "command"
    };

    internal sealed class Scope : IDisposable
    {
        private readonly TunnelDiagnostics owner;
        private readonly string prefix;
        private readonly long id = Interlocked.Increment(ref nextId);
        private readonly long started = Stopwatch.GetTimestamp();
        private bool finished;

        internal Scope(TunnelDiagnostics owner, string stage, long? parentId)
        {
            this.owner = owner;
            prefix = $"[DevTunnel #{id}{(parentId is { } parent ? $" parent=#{parent}" : "")}] {stage}";
            owner.Write($"{prefix}: started");
        }

        internal Scope BeginPhase(string stage) => owner.Begin(stage, id);

        internal void Complete(int? exitCode = null)
        {
            finished = true;
            owner.Write($"{prefix}: completed; elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms" +
                (exitCode is { } code ? $"; exitCode={code}" : ""));
        }

        public void Dispose()
        {
            if (!finished)
                owner.Write($"{prefix}: incomplete; elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms");
        }
    }
}
