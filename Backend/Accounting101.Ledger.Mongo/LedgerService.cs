using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Coordinates the journal (source of truth) and the balance projection (read model)
/// across the entry lifecycle. The journal is written first; the projection follows and
/// is rebuildable from it if they ever drift. On a replica-set deployment the paired
/// writes can be wrapped in a transaction; the prototype runs them sequentially.
/// </summary>
public sealed class LedgerService
{
    private readonly MongoJournalStore _journal;
    private readonly MongoBalanceProjection _projection;

    public LedgerService(MongoJournalStore journal, MongoBalanceProjection projection)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
    }

    /// <summary>Record a new entry. Durable immediately, but not on the books until approved.</summary>
    public Task PostAsync(JournalEntry entry, CancellationToken cancellationToken = default) =>
        _journal.AppendAsync(entry, cancellationToken);

    /// <summary>Approve a pending entry — it goes on the books and updates the projection.</summary>
    public async Task<JournalEntry> ApproveAsync(Guid entryId, Guid approvedBy, CancellationToken cancellationToken = default)
    {
        JournalEntry entry = await RequireAsync(entryId, cancellationToken);
        JournalEntry approved = entry.Approve(approvedBy);

        await _journal.ReplaceAsync(approved, cancellationToken);
        await _projection.ApplyAsync(approved, cancellationToken);
        return approved;
    }

    /// <summary>Void an active entry; reverses its effect from the projection if it was on the books.</summary>
    public async Task<JournalEntry> VoidAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        JournalEntry entry = await RequireAsync(entryId, cancellationToken);
        JournalEntry voided = entry.Void();

        await _journal.ReplaceAsync(voided, cancellationToken);
        await _projection.ReverseAsync(entry, cancellationToken); // pre-flip entry: reverses iff it was on the books
        return voided;
    }

    /// <summary>
    /// Replace an active entry with a corrected one (the edit path): the original is
    /// superseded and kept; the replacement is appended. The projection nets out to the
    /// replacement's effect. The replacement must reference the original via Supersedes.
    /// </summary>
    public async Task<JournalEntry> ReviseAsync(Guid originalId, JournalEntry replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Supersedes != originalId)
            throw new ArgumentException("Replacement must reference the original via Supersedes.", nameof(replacement));

        JournalEntry original = await RequireAsync(originalId, cancellationToken);
        JournalEntry superseded = original.Supersede(replacement.Id);

        await _journal.ReplaceAsync(superseded, cancellationToken);
        await _journal.AppendAsync(replacement, cancellationToken);
        await _projection.ReverseAsync(original, cancellationToken);   // undo the original (iff on the books)
        await _projection.ApplyAsync(replacement, cancellationToken);  // apply the replacement (iff on the books)
        return replacement;
    }

    private async Task<JournalEntry> RequireAsync(Guid entryId, CancellationToken cancellationToken) =>
        await _journal.GetAsync(entryId, cancellationToken)
        ?? throw new InvalidOperationException($"Journal entry {entryId} not found.");
}
