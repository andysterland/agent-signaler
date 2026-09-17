namespace AgentSignaler.Service;

/// <summary>Internal-test ceilings may be lowered, never increased past the prototype budget.</summary>
public sealed record TranscriptStoreOptions
{
    public int RetainedBytes { get; init; } = 48 * 1024 * 1024;
    public int MachineBytes { get; init; } = 8 * 1024 * 1024;
    public int SessionBytes { get; init; } = 2 * 1024 * 1024;
    public int Events { get; init; } = 2048;
    public int MachineEvents { get; init; } = 256;
    public int SessionEvents { get; init; } = 128;
    public int Machines { get; init; } = 25;
    public int Streams { get; init; } = 64;
    public int MachineStreams { get; init; } = 16;
    public int Sessions { get; init; } = 128;
    public int MachineSessions { get; init; } = 32;
    public TimeSpan Retention { get; init; } = TimeSpan.FromMinutes(30);

    internal void Validate()
    {
        Check(RetainedBytes, 48 * 1024 * 1024);
        Check(MachineBytes, 8 * 1024 * 1024);
        Check(SessionBytes, 2 * 1024 * 1024);
        Check(Events, 2048); Check(MachineEvents, 256); Check(SessionEvents, 128);
        Check(Machines, 25); Check(Streams, 64); Check(MachineStreams, 16);
        Check(Sessions, 128); Check(MachineSessions, 32);
        if (Retention <= TimeSpan.Zero || Retention > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(Retention));
    }

    private static void Check(int value, int maximum)
    {
        if (value < 1 || value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
    }
}
