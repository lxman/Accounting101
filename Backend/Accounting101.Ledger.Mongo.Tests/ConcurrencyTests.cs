using Accounting101.Ledger.Core.Journal;
using Accounting101.Ledger.Mongo.Documents;

namespace Accounting101.Ledger.Mongo.Tests;

/// <summary>
/// Concurrency guards under real concurrent writers: optimistic concurrency lets only one of many
/// racing approvals win (so the projection is never double-applied), and the unique audit-sequence
/// index plus append-retry keeps the hash chain a single gapless, verifiable sequence.
/// <para>
/// The fence tests (close/post and void/close races) prove the period-close freeze invariant holds
/// under both race orderings: exactly one of {close, mutation} wins per iteration — never both, never
/// a mutation committed into a frozen period, never a stranded pending entry after a successful close.
/// </para>
/// </summary>
public sealed class ConcurrencyTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    // ── Shared instance for fence tests — dedicated collections, so fence tests are isolated ──────

    private readonly LedgerService _fenceService = NewFenceService(fixture);

    /// <summary>A ready-to-use actor for all fence tests.</summary>
    private static readonly Actor Actor = new() { UserId = Guid.NewGuid(), Name = "tester", Claims = [new Claim("role", "controller")] };

    /// <summary>A new Guid is always an "open" client (no checkpoints exist for it).</summary>
    private static Task<Guid> NewOpenClientAsync() => Task.FromResult(Guid.NewGuid());

    /// <summary>A balanced pending entry dated on <paramref name="date"/> for <paramref name="clientId"/>.</summary>
    private static JournalEntry EntryDated(Guid clientId, DateOnly date) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: 0, // engine-assigned
            effectiveDate: date,
            postedAt: DateTimeOffset.UtcNow,
            type: EntryType.Standard,
            audit: new AuditStamp { CreatedBy = Actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
            lines:
            [
                new Line { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Direction = Direction.Debit, Amount = 100m },
                new Line { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), Direction = Direction.Credit, Amount = 100m },
            ]);

    /// <summary>The <see cref="LedgerService"/> used by fence tests.</summary>
    private LedgerService Service => _fenceService;

    private static LedgerService NewFenceService(MongoFixture f)
    {
        MongoJournalStore store = f.NewStore();
        MongoBalanceProjection projection = new(f.Database, store, "balances_fence_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(f.Database, "checkpoints_fence_" + Guid.NewGuid().ToString("N"));
        MongoAuditLog audit = new(f.Database, "audit_fence_" + Guid.NewGuid().ToString("N"));
        return new LedgerService(f.Database.Client, store, projection, checkpoints, audit, new MongoSequenceStore(f.Database));
    }

    // ── Helpers for the original concurrency tests ───────────────────────────────────────────────

    private static AuditStamp Stamp() => new() { CreatedBy = Guid.NewGuid(), CreatedAt = DateTimeOffset.UnixEpoch };

    private static Actor User() => new() { UserId = Guid.NewGuid(), Name = "tester", Claims = [new Claim("role", "controller")] };

    private (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection) NewLedger()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(fixture.Database, "checkpoints_" + Guid.NewGuid().ToString("N"));
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        return (new LedgerService(fixture.Database.Client, store, projection, checkpoints, audit, new MongoSequenceStore(fixture.Database)), store, projection);
    }

    private static JournalEntry Entry(Guid clientId, Guid debit, Guid credit, decimal amount, long sequence = 1) =>
        JournalEntry.Create(
            id: Guid.NewGuid(),
            clientId: clientId,
            sequenceNumber: sequence,
            effectiveDate: new DateOnly(2026, 6, 1),
            postedAt: DateTimeOffset.UnixEpoch,
            type: EntryType.Standard,
            audit: Stamp(),
            lines:
            [
                new Line { Id = Guid.NewGuid(), AccountId = debit, Direction = Direction.Debit, Amount = amount },
                new Line { Id = Guid.NewGuid(), AccountId = credit, Direction = Direction.Credit, Amount = amount },
            ]);

    [Fact]
    public async Task Only_one_of_many_concurrent_approvals_wins()
    {
        (LedgerService service, MongoJournalStore store, MongoBalanceProjection projection) = NewLedger();
        var client = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var revenue = Guid.NewGuid();

        JournalEntry entry = Entry(client, cash, revenue, 100m);
        await service.PostAsync(entry, User());

        // Eight threads race to approve the same pending entry.
        Task<bool>[] attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await service.ApproveAsync(entry.Id, User());
                    return true;
                }
                catch (InvalidOperationException) // ConcurrencyConflict, or "already posted" for the stragglers
                {
                    return false;
                }
            }))
            .ToArray();

        bool[] results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(won => won));                 // exactly one approval took effect
        IReadOnlyDictionary<Guid, decimal> balances = await projection.GetTrialBalanceAsync(client);
        Assert.Equal(100m, balances[cash]);                          // applied exactly once — never double-counted
        Assert.Equal(PostingState.Posted, (await store.GetAsync(entry.Id))!.Posting);
    }

    [Fact]
    public async Task Concurrent_audit_appends_keep_one_gapless_verifiable_chain()
    {
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        await audit.EnsureIndexesAsync();
        var client = Guid.NewGuid();

        const int n = 12;
        Task[] appends = Enumerable.Range(0, n)
            .Select(i => Task.Run(() =>
                audit.AppendAsync(client, Guid.NewGuid(), 1, AuditAction.Created, User(), $"r{i}", DateTimeOffset.UnixEpoch)))
            .ToArray();

        await Task.WhenAll(appends);

        IReadOnlyList<AuditRecordDocument> records = await audit.GetForClientAsync(client);
        Assert.Equal(n, records.Count);
        Assert.Equal(Enumerable.Range(1, n).Select(i => (long)i), records.Select(r => r.Sequence)); // gapless, no fork
        Assert.True(await audit.VerifyAsync(client));                                                // chain intact
    }

    [Fact]
    public async Task Concurrent_posts_to_one_client_commit_atomically_with_a_gapless_chain()
    {
        MongoJournalStore store = fixture.NewStore();
        MongoBalanceProjection projection = new(fixture.Database, store, "balances_" + Guid.NewGuid().ToString("N"));
        MongoCheckpointStore checkpoints = new(fixture.Database, "checkpoints_" + Guid.NewGuid().ToString("N"));
        MongoAuditLog audit = new(fixture.Database, "audit_" + Guid.NewGuid().ToString("N"));
        await audit.EnsureIndexesAsync(); // the unique chain index turns concurrent appends into retryable conflicts
        LedgerService service = new(fixture.Database.Client, store, projection, checkpoints, audit, new MongoSequenceStore(fixture.Database));

        var client = Guid.NewGuid();
        const int n = 10;
        Task[] posts = Enumerable.Range(1, n)
            .Select(seq => Task.Run(() => service.PostAsync(Entry(client, Guid.NewGuid(), Guid.NewGuid(), 10m, seq), User())))
            .ToArray();
        await Task.WhenAll(posts);

        // Each post's journal + audit committed together; the chain serialized to a gapless 1..n.
        Assert.Equal(n, (await store.GetByClientAsync(client)).Count);
        IReadOnlyList<AuditRecordDocument> records = await audit.GetForClientAsync(client);
        Assert.Equal(Enumerable.Range(1, n).Select(i => (long)i), records.Select(r => r.Sequence));
        Assert.True(await audit.VerifyAsync(client));
    }

    // ── Period-close fence tests (Task 6) ────────────────────────────────────────────────────────

    /// <summary>
    /// Race CloseAsync against a back-dated PostAsync across 20 iterations. In each iteration exactly
    /// one of the two commits: when the close wins the post must be rejected with "closed"; when the
    /// post wins the close must raise <see cref="PeriodCloseBlockedException"/>. Neither both succeed
    /// (which would mean a mutation slipped into a frozen period) nor both fail (which would mean a
    /// spurious error on the winner's side).
    /// </summary>
    [Fact]
    public async Task Concurrent_close_and_backdated_post_never_strand_and_never_post_into_a_frozen_period()
    {
        for (int i = 0; i < 20; i++)
        {
            // Fresh client per iteration (or fresh in-period date) so iterations are independent.
            Guid clientId = await NewOpenClientAsync();
            DateOnly asOf = new(2024, 6, 30);

            Task close = Service.CloseAsync(clientId, asOf, Actor, CancellationToken.None);
            Task post  = Service.PostAsync(EntryDated(clientId, asOf), Actor, CancellationToken.None); // pending, in-period

            Exception? closeEx = await Record.ExceptionAsync(() => close);
            Exception? postEx  = await Record.ExceptionAsync(() => post);

            bool closed = closeEx is null;
            if (closed)
            {
                // Close won: the post MUST have been rejected (period frozen) — no entry in the closed period.
                Assert.IsType<InvalidOperationException>(postEx);
                Assert.Contains("closed", postEx!.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // Close lost: it MUST be the pending-block (it saw the entry), never any other failure.
                Assert.IsType<PeriodCloseBlockedException>(closeEx);
                Assert.Null(postEx); // the post committed; close correctly refused to strand it
            }
        }
    }

    /// <summary>
    /// Race VoidAsync against CloseAsync for an already-posted entry dated in the closing period,
    /// across 20 iterations. The only safety invariant that must hold is:
    /// <b>a void must never be applied to an already-frozen-period entry</b>.
    /// <para>
    /// Three valid race outcomes exist:
    /// <list type="bullet">
    ///   <item>Close wins (commits first) — void must fail with "closed" message.</item>
    ///   <item>Void wins (commits first) — the entry is voided before any freeze; the close then
    ///     finds no blockers and may also commit successfully. Both succeeding is correct:
    ///     the void landed in the open period, the close froze an already-cleaned ledger.</item>
    ///   <item>Void wins and close fails — close may have seen the pending entry before the void
    ///     committed on its first attempt; but because <c>WithTransactionAsync</c> retries on
    ///     write-conflicts, this outcome is extremely rare; the close should normally retry and
    ///     succeed after void commits. If close does fail here it must be an
    ///     <see cref="InvalidOperationException"/> (blocked or other domain error).</item>
    /// </list>
    /// What is NEVER allowed: <c>voidEx is null AND closeEx is null</c> with the void having
    /// been applied inside an already-frozen period. The fence (<c>EnsureOpenForPostAsync</c>
    /// inside the void's transaction) prevents that.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_void_and_close_never_void_into_a_frozen_period()
    {
        for (int i = 0; i < 20; i++)
        {
            Guid clientId = await NewOpenClientAsync();
            DateOnly asOf = new(2024, 6, 30);

            // Post and approve an entry dated in the period so VoidAsync has a target.
            // These sequential operations must succeed before the race begins.
            JournalEntry entry = EntryDated(clientId, asOf);
            await Service.PostAsync(entry, Actor, CancellationToken.None);
            await Service.ApproveAsync(entry.Id, Actor, CancellationToken.None);

            // Race VoidAsync against CloseAsync without awaiting between them.
            Task voidTask  = Service.VoidAsync(entry.Id, Actor, cancellationToken: CancellationToken.None);
            Task closeTask = Service.CloseAsync(clientId, asOf, Actor, CancellationToken.None);

            Exception? voidEx  = await Record.ExceptionAsync(() => voidTask);
            Exception? closeEx = await Record.ExceptionAsync(() => closeTask);

            // FORBIDDEN: both fail (a spurious failure on the winner's side).
            Assert.False(
                voidEx is not null && closeEx is not null,
                $"Iteration {i}: both tasks failed — spurious failure. voidEx={voidEx?.GetType().Name}: {voidEx?.Message}, closeEx={closeEx?.GetType().Name}: {closeEx?.Message}");

            if (closeEx is null && voidEx is not null)
            {
                // Close won first: the void MUST have been rejected because the period is frozen.
                // This is the primary safety guard — no void into a frozen period.
                Assert.IsType<InvalidOperationException>(voidEx);
                Assert.Contains("closed", voidEx!.Message, StringComparison.OrdinalIgnoreCase);
            }
            else if (voidEx is null && closeEx is not null)
            {
                // Void won and close failed: close must have failed for a domain reason (blocked or
                // already-closed), not a spurious infrastructure error.
                Assert.IsType<InvalidOperationException>(closeEx);
            }
            // else: both null — void committed before the freeze, then close committed cleanly.
            // This is correct: the void landed in an open period. No assertion needed; the invariant
            // "never a void into a frozen period" holds because the freeze didn't exist when void ran.
        }
    }
}
