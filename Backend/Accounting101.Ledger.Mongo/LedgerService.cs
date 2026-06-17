using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Coordinates the journal (source of truth), the balance projection (read model), period-close
/// checkpoints, and the audit log across the entry lifecycle. The journal is written first; the
/// projection follows; every mutation is recorded in the audit log with the acting principal.
/// The engine enforces only integrity invariants (balance, the closed-period freeze) — it does
/// not authenticate or authorize. The host does that and hands in an authenticated <see cref="Actor"/>.
/// </summary>
public sealed class LedgerService
{
    private readonly MongoJournalStore _journal;
    private readonly MongoBalanceProjection _projection;
    private readonly MongoCheckpointStore _checkpoints;
    private readonly MongoAuditLog _audit;

    public LedgerService(
        MongoJournalStore journal,
        MongoBalanceProjection projection,
        MongoCheckpointStore checkpoints,
        MongoAuditLog audit)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Record a new entry. Durable immediately, but not on the books until approved.</summary>
    public async Task PostAsync(JournalEntry entry, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(actor);
        await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);

        await _journal.AppendAsync(entry, cancellationToken);
        await _audit.AppendAsync(entry.ClientId, entry.Id, entry.Version, AuditAction.Created, actor, null, DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>
    /// Approve a pending entry — it goes on the books and updates the projection. If the entry is a
    /// correction (it supersedes another), the swap happens here, atomically: the superseded original
    /// is retired and its effect reversed at the same moment the replacement goes on the books. The
    /// original must still be active to approve its replacement, so a correction cannot double-count.
    /// </summary>
    public async Task<JournalEntry> ApproveAsync(Guid entryId, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        JournalEntry entry = await RequireAsync(entryId, cancellationToken);
        await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);

        // A revision only takes effect on approval. Verify the original is still revisable before
        // mutating anything, so we never half-apply a correction.
        JournalEntry? original = null;
        if (entry.Supersedes is { } originalId)
        {
            original = await RequireAsync(originalId, cancellationToken);
            if (original.Status != LifecycleStatus.Active)
                throw new InvalidOperationException(
                    $"Cannot approve this revision: the entry it supersedes ({originalId}) is no longer {LifecycleStatus.Active} (it is {original.Status}).");
        }

        JournalEntry approved = entry.Approve(actor.UserId);
        await _journal.ReplaceAsync(approved, cancellationToken);
        await _projection.ApplyAsync(approved, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _audit.AppendAsync(approved.ClientId, approved.Id, approved.Version, AuditAction.Approved, actor, null, now, cancellationToken);

        if (original is not null)
        {
            JournalEntry superseded = original.Supersede(approved.Id);
            await _journal.ReplaceAsync(superseded, cancellationToken);
            await _projection.ReverseAsync(original, cancellationToken); // pre-flip: reverses iff it was on the books
            await _audit.AppendAsync(superseded.ClientId, superseded.Id, superseded.Version, AuditAction.Superseded, actor, "superseded by approved revision", now, cancellationToken);
        }

        return approved;
    }

    /// <summary>Void an active entry; reverses its effect from the projection if it was on the books.</summary>
    public async Task<JournalEntry> VoidAsync(Guid entryId, Actor actor, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        JournalEntry entry = await RequireAsync(entryId, cancellationToken);
        await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);

        JournalEntry voided = entry.Void();
        await _journal.ReplaceAsync(voided, cancellationToken);
        await _projection.ReverseAsync(entry, cancellationToken); // pre-flip entry: reverses iff it was on the books
        await _audit.AppendAsync(voided.ClientId, voided.Id, voided.Version, AuditAction.Voided, actor, reason, DateTimeOffset.UtcNow, cancellationToken);
        return voided;
    }

    /// <summary>
    /// Propose a correction to an active entry (the edit path). The corrected entry is recorded as a
    /// <em>pending</em> replacement linked to the original via <see cref="JournalEntry.Supersedes"/>,
    /// but has no effect on the journal or the books until it is approved — a revision does not exist
    /// until approved. The original stays active and on the books until then, so there is never a gap;
    /// the supersede swap happens in <see cref="ApproveAsync"/>. Both entries must be in open periods.
    /// </summary>
    public async Task<JournalEntry> ReviseAsync(Guid originalId, JournalEntry replacement, Actor actor, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(actor);
        if (replacement.Supersedes != originalId)
            throw new ArgumentException("Replacement must reference the original via Supersedes.", nameof(replacement));
        if (replacement.Posting != PostingState.PendingApproval)
            throw new ArgumentException("A revision must be submitted pending approval.", nameof(replacement));

        JournalEntry original = await RequireAsync(originalId, cancellationToken);
        if (original.Status != LifecycleStatus.Active)
            throw new InvalidOperationException(
                $"Only an {LifecycleStatus.Active} entry can be revised; entry {originalId} is {original.Status}.");

        await EnsureOpenAsync(original.ClientId, original.EffectiveDate, cancellationToken);
        await EnsureOpenAsync(replacement.ClientId, replacement.EffectiveDate, cancellationToken);

        // Record the proposal only. The original is untouched and the projection unchanged until
        // approval — see ApproveAsync, which performs the atomic swap.
        await _journal.AppendAsync(replacement, cancellationToken);
        await _audit.AppendAsync(replacement.ClientId, replacement.Id, replacement.Version, AuditAction.Created, actor, reason, DateTimeOffset.UtcNow, cancellationToken);
        return replacement;
    }

    /// <summary>
    /// Close a period: snapshot the on-the-books balances effective on or before
    /// <paramref name="asOf"/> as a checkpoint (the opening balance for the next period), freeze
    /// everything through that date, and record the close in the audit log. Returns the snapshot.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> CloseAsync(Guid clientId, DateOnly asOf, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        DateOnly? closedThrough = await _checkpoints.GetClosedThroughAsync(clientId, cancellationToken);
        if (closedThrough is { } through && asOf <= through)
            throw new InvalidOperationException($"Period is already closed through {through:yyyy-MM-dd}.");

        IReadOnlyDictionary<Guid, decimal> balances = await _journal.AggregateBalancesAsync(clientId, asOf, cancellationToken);
        await _checkpoints.SaveAsync(clientId, asOf, balances, actor.UserId, DateTimeOffset.UtcNow, cancellationToken);
        await _audit.AppendAsync(clientId, null, 0, AuditAction.PeriodClosed, actor, $"closed through {asOf:yyyy-MM-dd}", DateTimeOffset.UtcNow, cancellationToken);
        return balances;
    }

    /// <summary>
    /// Close a fiscal year: post a balanced <see cref="EntryType.Closing"/> entry that zeros every
    /// temporary account (revenue/expense, per the chart) into the designated retained-earnings
    /// account, then close and freeze the year. The closing entry flows through the normal post +
    /// approve path, so it is audited and reflected in the projection like any other entry. Returns
    /// the closing entry, or null if there was nothing to close.
    /// </summary>
    public async Task<JournalEntry?> CloseYearAsync(
        Guid clientId,
        DateOnly fiscalYearEnd,
        Actor actor,
        ChartOfAccounts chart,
        long closingSequenceNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(chart);

        IReadOnlyDictionary<Guid, decimal> balances = await _journal.AggregateBalancesAsync(clientId, fiscalYearEnd, cancellationToken);

        List<(Guid AccountId, decimal Signed)> temporaries = balances
            .Where(kv => kv.Value != 0m && chart.Find(kv.Key) is { IsTemporary: true })
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        JournalEntry? closing = null;
        if (temporaries.Count > 0)
        {
            Account retainedEarnings = chart.RetainedEarnings
                ?? throw new InvalidOperationException("Year-end close requires a designated retained-earnings account.");

            decimal netTemporary = temporaries.Sum(t => t.Signed);

            List<Line> lines = temporaries.Select(t => SignedLine(t.AccountId, -t.Signed)).ToList();
            if (netTemporary != 0m)
                lines.Add(SignedLine(retainedEarnings.Id, netTemporary));

            closing = JournalEntry.Create(
                id: Guid.NewGuid(),
                clientId: clientId,
                sequenceNumber: closingSequenceNumber,
                effectiveDate: fiscalYearEnd,
                postedAt: DateTimeOffset.UtcNow,
                type: EntryType.Closing,
                audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
                lines: lines);

            await PostAsync(closing, actor, cancellationToken);
            await ApproveAsync(closing.Id, actor, cancellationToken);
        }

        await CloseAsync(clientId, fiscalYearEnd, actor, cancellationToken);
        return closing;
    }

    /// <summary>A single line with the given debit-positive signed effect (normalized to a positive amount).</summary>
    private static Line SignedLine(Guid accountId, decimal signedEffect) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Direction = signedEffect >= 0 ? Direction.Debit : Direction.Credit,
        Amount = Math.Abs(signedEffect),
    };

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
