namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Thrown when an optimistic-concurrency check fails: an entry was modified by someone else between
/// the moment it was read and the moment a transition tried to persist. The caller should re-read and
/// retry. It derives from <see cref="InvalidOperationException"/> so the host maps it to 409 Conflict.
/// </summary>
public sealed class ConcurrencyConflictException(Guid entryId, int expectedVersion)
    : InvalidOperationException(
        $"Entry {entryId} was modified concurrently (expected version {expectedVersion}); re-read and retry.")
{
    public Guid EntryId { get; } = entryId;
    public int ExpectedVersion { get; } = expectedVersion;
}
