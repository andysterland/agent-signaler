using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

/// <summary>
/// A profile must establish UTF-8 LF append framing, session binding, and completed
/// top-level assistant semantics from published evidence. No content-based selection.
/// </summary>
public interface ITranscriptFileAdapter
{
    string ProfileId { get; }
    SourceDescriptor Source { get; }
    string TranscriptRoot { get; }
    string GetExpectedFileName(string sessionId);
    TranscriptFileRecord ParseRecord(ReadOnlySpan<byte> utf8Record, string sessionId);
}

public enum TranscriptFileRecordKind { Ignored, Assistant, Invalid, SessionMismatch }

public sealed record TranscriptFileRecord(
    TranscriptFileRecordKind Kind, string? AssistantText = null, string? NativeMessageId = null)
{
    public override string ToString() => $"{nameof(TranscriptFileRecord)}: {Kind}";
}

public interface ITranscriptFileAdapterRegistry
{
    ITranscriptFileAdapter? Find(SourceDescriptor source);
}

/// <summary>No production file schema/path/session profile has yet been independently verified.</summary>
public sealed class VerifiedTranscriptFileAdapterRegistry : ITranscriptFileAdapterRegistry
{
    public static VerifiedTranscriptFileAdapterRegistry Production { get; } = new();
    private VerifiedTranscriptFileAdapterRegistry() { }
    public ITranscriptFileAdapter? Find(SourceDescriptor source) => null;
    public const string UnavailableReason = "format-unverified";
}

public static class TranscriptReaderCategories
{
    public const string FormatUnverified = "format-unverified";
    public const string InvalidReference = "invalid-reference";
    public const string Unavailable = "file-unavailable";
    public const string PathRejected = "path-rejected";
    public const string IdentityChanged = "file-identity-changed";
    public const string Truncated = "file-truncated";
    public const string FormatChanged = "format-changed";
    public const string SessionMismatch = "session-mismatch";
    public const string Baseline = "baseline-established";
    public const string Budget = "read-budget-exceeded";
    public const string Capacity = "reader-capacity";
    public const string Expired = "read-request-expired";
    public const string Ineligible = "reader-ineligible";
    public const string QueueRejected = "output-dropped";
    public const string Waiting = "waiting-for-next-stop";
    public const string Assistant = "assistant-message";
}

/// <summary>Local callback input, not a network model; project only allowlisted fields.</summary>
public sealed record TranscriptReaderOutput(
    LocalTranscriptReference Reference,
    string FormatProfileId,
    string Category,
    string? AssistantText = null,
    string? NativeMessageId = null,
    long? RecordOffset = null)
{
    public override string ToString() => $"{nameof(TranscriptReaderOutput)}: {Category}";
}

/// <summary>Reservations share the Client's 512 KiB IPC/file-reader scratch partition.</summary>
public sealed class TranscriptScratchBudget
{
    public const int MaximumBytes = 512 * 1024;
    private int _used;
    public int UsedBytes => Volatile.Read(ref _used);

    public IDisposable? TryReserve(int bytes)
    {
        if (bytes <= 0 || bytes > MaximumBytes) return null;
        while (true)
        {
            var used = Volatile.Read(ref _used);
            if (used > MaximumBytes - bytes) return null;
            if (Interlocked.CompareExchange(ref _used, used + bytes, used) == used)
                return new Lease(this, bytes);
        }
    }

    private sealed class Lease(TranscriptScratchBudget owner, int bytes) : IDisposable
    {
        private TranscriptScratchBudget? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is not null) Interlocked.Add(ref value._used, -bytes);
        }
    }
}
