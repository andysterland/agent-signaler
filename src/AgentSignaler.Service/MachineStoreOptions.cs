namespace AgentSignaler.Service;

/// <summary>New events cannot be persisted without retaining their replay protection.</summary>
public sealed class ReceiptCapacityException() : Exception("Receipt storage is full.");

/// <summary>
/// Retains at most 1,000,000 event IDs globally, indefinitely; there is no age-based expiry.
/// At capacity, new IDs fail without changing state or liveness, while duplicates still
/// succeed without refreshing liveness. This deliberately fails closed rather than
/// trusting caller timestamps or forgetting replay history. Only explicit local machine
/// removal releases its receipts (and resets its replay protection, as before).
/// An existing over-limit database is preserved, but cannot accept additional IDs.
/// SQLite reuses freed pages; removing machines does not promise file shrinkage.
/// </summary>
public sealed record MachineStoreOptions
{
    public const int MaximumReceiptLimit = 1_000_000;
    public int ReceiptLimit { get; init; } = MaximumReceiptLimit;

    internal void Validate()
    {
        if (ReceiptLimit is < 1 or > MaximumReceiptLimit)
            throw new ArgumentOutOfRangeException(nameof(ReceiptLimit));
    }
}
