using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Journal;
using MongoDB.Driver;

namespace Accounting101.Ledger.Mongo;

/// <summary>
/// Coordinates the journal (source of truth), the balance projection (read model), period-close
/// checkpoints, and the audit log across the entry lifecycle. Each mutation's writes commit together
/// in a replica-set transaction, so the journal, projection, and audit move atomically — a crash mid
/// way leaves nothing partially applied. The engine enforces only integrity invariants (balance, the
/// closed-period freeze, optimistic concurrency) — it does not authenticate or authorize. The host
/// does that and hands in an authenticated <see cref="Actor"/>.
/// </summary>
public sealed class LedgerService
{
    private readonly IMongoClient _client;
    private readonly MongoJournalStore _journal;
    private readonly MongoBalanceProjection _projection;
    private readonly MongoCheckpointStore _checkpoints;
    private readonly MongoAuditLog _audit;
    private readonly MongoSequenceStore _sequences;

    public LedgerService(
        IMongoClient client,
        MongoJournalStore journal,
        MongoBalanceProjection projection,
        MongoCheckpointStore checkpoints,
        MongoAuditLog audit,
        MongoSequenceStore sequences)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _sequences = sequences ?? throw new ArgumentNullException(nameof(sequences));
    }

    /// <summary>
    /// Append an entry, assigning the gapless per-client sequence number when the caller left it
    /// unset (0). An explicit non-zero number is honored — the seam the block-allocating bulk importer
    /// uses. The increment joins the caller's transaction, so number and entry commit together.
    /// </summary>
    private async Task<JournalEntry> AppendSequencedAsync(
        JournalEntry entry, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        JournalEntry sequenced = entry.SequenceNumber == 0
            ? entry.WithSequenceNumber(await _sequences.NextJournalAsync(entry.ClientId, session, cancellationToken))
            : entry;
        await _journal.AppendAsync(sequenced, session, cancellationToken);
        return sequenced;
    }

    /// <summary>Record a new entry. Durable immediately, but not on the books until approved.</summary>
    public async Task PostAsync(JournalEntry entry, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(actor);
        await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await InTransactionAsync(async session =>
        {
            JournalEntry posted = await AppendSequencedAsync(entry, session, cancellationToken);
            await _audit.AppendAsync(posted.ClientId, posted.Id, posted.Version, AuditAction.Created, actor, null, now, session, cancellationToken);
        }, cancellationToken);
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
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await InTransactionAsync(async session =>
        {
            await _journal.ReplaceAsync(approved, session, cancellationToken);
            await _projection.ApplyAsync(approved, session, cancellationToken);
            await _audit.AppendAsync(approved.ClientId, approved.Id, approved.Version, AuditAction.Approved, actor, null, now, session, cancellationToken);

            if (original is not null)
            {
                JournalEntry superseded = original.Supersede(approved.Id);
                await _journal.ReplaceAsync(superseded, session, cancellationToken);
                await _projection.ReverseAsync(original, session, cancellationToken); // pre-flip: reverses iff it was on the books
                await _audit.AppendAsync(superseded.ClientId, superseded.Id, superseded.Version, AuditAction.Superseded, actor, "superseded by approved revision", now, session, cancellationToken);
            }
        }, cancellationToken);

        return approved;
    }

    /// <summary>Void an active entry; reverses its effect from the projection if it was on the books.</summary>
    public async Task<JournalEntry> VoidAsync(Guid entryId, Actor actor, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        JournalEntry entry = await RequireAsync(entryId, cancellationToken);
        await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);

        JournalEntry voided = entry.Void();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await InTransactionAsync(async session =>
        {
            await _journal.ReplaceAsync(voided, session, cancellationToken);
            await _projection.ReverseAsync(entry, session, cancellationToken); // pre-flip entry: reverses iff it was on the books
            await _audit.AppendAsync(voided.ClientId, voided.Id, voided.Version, AuditAction.Voided, actor, reason, now, session, cancellationToken);
        }, cancellationToken);

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
        DateTimeOffset now = DateTimeOffset.UtcNow;
        JournalEntry recorded = replacement;
        await InTransactionAsync(async session =>
        {
            recorded = await AppendSequencedAsync(replacement, session, cancellationToken);
            await _audit.AppendAsync(recorded.ClientId, recorded.Id, recorded.Version, AuditAction.Created, actor, reason, now, session, cancellationToken);
        }, cancellationToken);

        return recorded;
    }

    /// <summary>
    /// Reverse a posted entry by booking a new <see cref="EntryType.Reversing"/> entry that negates it
    /// (every line's direction flipped), linked via <see cref="JournalEntry.ReversalOf"/>. Both entries
    /// stay on the books — nothing is removed — so this is how a <em>closed</em> period is corrected: the
    /// original is left frozen and the reversal lands in an open period. The reversal is pending until
    /// approved, like any entry. The original must be active and posted.
    /// </summary>
    public async Task<JournalEntry> ReverseAsync(
        Guid originalId, DateOnly reversalDate, Actor actor, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        JournalEntry original = await RequireAsync(originalId, cancellationToken);
        if (original.Status != LifecycleStatus.Active || original.Posting != PostingState.Posted)
            throw new InvalidOperationException(
                $"Only an active, posted entry can be reversed; entry {originalId} is {original.Status}/{original.Posting}.");

        // The reversal lands in an open period; the original may be in a closed one — that is the point.
        await EnsureOpenAsync(original.ClientId, reversalDate, cancellationToken);

        List<Line> reversedLines = original.Lines
            .Select(line => new Line
            {
                Id = Guid.NewGuid(),
                AccountId = line.AccountId,
                Direction = line.Direction == Direction.Debit ? Direction.Credit : Direction.Debit,
                Amount = line.Amount,
                Dimensions = line.Dimensions,
                LineMemo = line.LineMemo,
            })
            .ToList();

        JournalEntry reversal = JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: original.ClientId,
            sequenceNumber: 0, // engine-assigned at append
            effectiveDate: reversalDate,
            postedAt: DateTimeOffset.UtcNow,
            type: EntryType.Reversing,
            audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
            lines: reversedLines,
            reversalOf: originalId,
            sourceRef: original.SourceRef,   // a reversal stays linked to the same source document...
            sourceType: original.SourceType, // ...so drill-down from that document still finds it
            reference: original.Reference,
            memo: reason ?? $"Reversal of entry {originalId}");

        JournalEntry recorded = reversal;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await InTransactionAsync(async session =>
        {
            recorded = await AppendSequencedAsync(reversal, session, cancellationToken);
            await _audit.AppendAsync(recorded.ClientId, recorded.Id, recorded.Version, AuditAction.Created, actor, reason, now, session, cancellationToken);
        }, cancellationToken);

        return recorded;
    }

    /// <summary>
    /// Establish a client's opening balances — the position carried in from a prior system at the
    /// cutover date — as a single balanced <see cref="EntryType.Opening"/> entry, posted and approved
    /// so it lands on the books. It is an ordinary journal entry (the source of truth), so it flows
    /// into every balance, report, and replay, and can be corrected like any other. Throws if the
    /// lines do not balance — an opening trial balance must balance. Lock it afterward with a close if desired.
    /// </summary>
    public async Task<JournalEntry> OpenAsync(
        Guid clientId, DateOnly asOf, IReadOnlyList<Line> lines, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(lines);

        JournalEntry opening = JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: 0, // engine-assigned at append
            effectiveDate: asOf,
            postedAt: DateTimeOffset.UtcNow,
            type: EntryType.Opening,
            audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
            lines: lines,
            memo: "Opening balances");

        await PostAsync(opening, actor, cancellationToken);
        return await ApproveAsync(opening.Id, actor, cancellationToken);
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

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await InTransactionAsync(async session =>
        {
            await _checkpoints.SaveAsync(clientId, asOf, balances, actor.UserId, now, session, cancellationToken);
            await _audit.AppendAsync(clientId, null, 0, AuditAction.PeriodClosed, actor, $"closed through {asOf:yyyy-MM-dd}", now, session, cancellationToken);
        }, cancellationToken);

        return balances;
    }

    /// <summary>
    /// Reopen a closed period: either move the freeze pointer back to an earlier date (recomputing the
    /// checkpoint there) or, when <paramref name="reopenThrough"/> is null, clear the checkpoint entirely
    /// so nothing is frozen. The most privileged period operation — the host gates it on the admin role
    /// plus step-up re-auth. Note most closed-period corrections need no reopen: a reversing entry handles
    /// them in the open period. Recorded as <see cref="AuditAction.PeriodReopened"/>.
    /// </summary>
    public async Task ReopenAsync(Guid clientId, DateOnly? reopenThrough, Actor actor, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        DateOnly? closedThrough = await _checkpoints.GetClosedThroughAsync(clientId, cancellationToken);
        if (closedThrough is not { } current)
            throw new InvalidOperationException("There is no closed period to reopen.");
        if (reopenThrough is { } target && target >= current)
            throw new InvalidOperationException(
                $"Reopen must move the freeze earlier than the current close ({current:yyyy-MM-dd}).");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string detail = reopenThrough is { } t ? $"reopened to {t:yyyy-MM-dd}" : "fully reopened";

        if (reopenThrough is { } newAsOf)
        {
            IReadOnlyDictionary<Guid, decimal> balances = await _journal.AggregateBalancesAsync(clientId, newAsOf, cancellationToken);
            await InTransactionAsync(async session =>
            {
                await _checkpoints.SaveAsync(clientId, newAsOf, balances, actor.UserId, now, session, cancellationToken);
                await _audit.AppendAsync(clientId, null, 0, AuditAction.PeriodReopened, actor, reason ?? detail, now, session, cancellationToken);
            }, cancellationToken);
        }
        else
        {
            await InTransactionAsync(async session =>
            {
                await _checkpoints.DeleteAsync(clientId, session, cancellationToken);
                await _audit.AppendAsync(clientId, null, 0, AuditAction.PeriodReopened, actor, reason ?? detail, now, session, cancellationToken);
            }, cancellationToken);
        }
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
                sequenceNumber: 0, // engine-assigned at append
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

    /// <summary>
    /// Runs a coordinator's multi-document write atomically in a replica-set transaction.
    /// <c>WithTransactionAsync</c> commits on success and retries the body on transient errors —
    /// including the write-conflict that serializes concurrent appends to a client's audit chain —
    /// so journal + projection + audit move together or not at all. A
    /// <see cref="ConcurrencyConflictException"/> (a real version clash) is not transient and propagates.
    /// </summary>
    private async Task InTransactionAsync(Func<IClientSessionHandle, Task> work, CancellationToken cancellationToken)
    {
        using IClientSessionHandle session = await _client.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync(
            async (s, _) =>
            {
                await work(s);
                return true;
            },
            cancellationToken: cancellationToken);
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
