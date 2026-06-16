using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Coordinates the journal (source of truth), the balance projection (read model), and
/// period-close checkpoints across the entry lifecycle. The journal is written first; the
/// projection follows and is rebuildable from it. Closed periods are frozen: any change
/// dated on or before the latest close is rejected. On a replica set the paired writes can
/// be wrapped in a transaction; the prototype runs them sequentially.
/// </summary>
public sealed class LedgerService
{
    private readonly MongoJournalStore _journal;
    private readonly MongoBalanceProjection _projection;
    private readonly MongoCheckpointStore _checkpoints;

    public LedgerService(MongoJournalStore journal, MongoBalanceProjection projection, MongoCheckpointStore checkpoints)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    }

    /// <summary>Record a new entry. Durable immediately, but not on the books until approved.</summary>
    public async Task PostAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);
        await _journal.AppendAsync(entry, cancellationToken);
    }

    /// <summary>Approve a pending entry — it goes on the books and updates the projection.</summary>
    public async Task<JournalEntry> ApproveAsync(Guid entryId, Guid approvedBy, CancellationToken cancellationToken = default)
    {
        JournalEntry entry = await RequireAsync(entryId, cancellationToken);
        await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);

        JournalEntry approved = entry.Approve(approvedBy);
        await _journal.ReplaceAsync(approved, cancellationToken);
        await _projection.ApplyAsync(approved, cancellationToken);
        return approved;
    }

    /// <summary>Void an active entry; reverses its effect from the projection if it was on the books.</summary>
    public async Task<JournalEntry> VoidAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        JournalEntry entry = await RequireAsync(entryId, cancellationToken);
        await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);

        JournalEntry voided = entry.Void();
        await _journal.ReplaceAsync(voided, cancellationToken);
        await _projection.ReverseAsync(entry, cancellationToken); // pre-flip entry: reverses iff it was on the books
        return voided;
    }

    /// <summary>
    /// Replace an active entry with a corrected one (the edit path): the original is
    /// superseded and kept; the replacement is appended. The projection nets out to the
    /// replacement's effect. Both the original and the replacement must be in open periods.
    /// </summary>
    public async Task<JournalEntry> ReviseAsync(Guid originalId, JournalEntry replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Supersedes != originalId)
            throw new ArgumentException("Replacement must reference the original via Supersedes.", nameof(replacement));

        JournalEntry original = await RequireAsync(originalId, cancellationToken);
        await EnsureOpenAsync(original.ClientId, original.EffectiveDate, cancellationToken);
        await EnsureOpenAsync(replacement.ClientId, replacement.EffectiveDate, cancellationToken);

        JournalEntry superseded = original.Supersede(replacement.Id);
        await _journal.ReplaceAsync(superseded, cancellationToken);
        await _journal.AppendAsync(replacement, cancellationToken);
        await _projection.ReverseAsync(original, cancellationToken);
        await _projection.ApplyAsync(replacement, cancellationToken);
        return replacement;
    }

    /// <summary>
    /// Close a period: snapshot the on-the-books balances effective on or before
    /// <paramref name="asOf"/> as a checkpoint (the opening balance for the next period),
    /// and freeze everything through that date. Returns the snapshot.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> CloseAsync(
        Guid clientId, DateOnly asOf, Guid closedBy, CancellationToken cancellationToken = default)
    {
        DateOnly? closedThrough = await _checkpoints.GetClosedThroughAsync(clientId, cancellationToken);
        if (closedThrough is { } through && asOf <= through)
            throw new InvalidOperationException($"Period is already closed through {through:yyyy-MM-dd}.");

        IReadOnlyDictionary<Guid, decimal> balances = await _journal.AggregateBalancesAsync(clientId, asOf, cancellationToken);
        await _checkpoints.SaveAsync(clientId, asOf, balances, closedBy, DateTimeOffset.UtcNow, cancellationToken);
        return balances;
    }

    private async Task EnsureOpenAsync(Guid clientId, DateOnly effectiveDate, CancellationToken cancellationToken)
    {
        DateOnly? closedThrough = await _checkpoints.GetClosedThroughAsync(clientId, cancellationToken);
        if (closedThrough is { } through && effectiveDate <= through)
            throw new InvalidOperationException(
                $"Period is closed through {through:yyyy-MM-dd}; entry dated {effectiveDate:yyyy-MM-dd} is in a closed period.");
    }

    private async Task<JournalEntry> RequireAsync(Guid entryId, CancellationToken cancellationToken) =>
        await _journal.GetAsync(entryId, cancellationToken)
        ?? throw new InvalidOperationException($"Journal entry {entryId} not found.");
}
