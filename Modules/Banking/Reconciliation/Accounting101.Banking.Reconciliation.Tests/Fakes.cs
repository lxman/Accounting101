using System.Collections.Concurrent;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

internal sealed class InMemoryBankStatementStore : IBankStatementStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), BankStatement> _store = new();
    private long _seq;
    public Task<BankStatement> RecordAsync(Guid clientId, BankStatementBody body, CancellationToken ct = default)
    {
        BankStatement s = new()
        {
            Id = Guid.NewGuid(), Number = $"BST-{Interlocked.Increment(ref _seq):D5}",
            CashAccountId = body.CashAccountId, StatementDate = body.StatementDate,
            OpeningBalance = body.OpeningBalance, ClosingBalance = body.ClosingBalance, Lines = body.Lines,
        };
        _store[(clientId, s.Id)] = s;
        return Task.FromResult(s);
    }
    public Task<BankStatement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, id)));
    public Task<IReadOnlyList<BankStatement>> GetByAccountAsync(Guid clientId, Guid cashAccountId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BankStatement>>(_store.Values.Where(s => s.CashAccountId == cashAccountId).ToList());
}

internal sealed class InMemoryReconciliationStore : IReconciliationStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Reconciliation> _store = new();
    private long _seq;
    public Task<Reconciliation> CreateAsync(Guid clientId, Guid cashAccountId, Guid bankStatementId, DateOnly statementDate, CancellationToken ct = default)
    {
        Reconciliation r = new()
        {
            Id = Guid.NewGuid(), Number = $"REC-{Interlocked.Increment(ref _seq):D5}",
            CashAccountId = cashAccountId, BankStatementId = bankStatementId, StatementDate = statementDate,
            Status = ReconciliationStatus.InProgress, ClearedEntryIds = [],
        };
        _store[(clientId, r.Id)] = r;
        return Task.FromResult(r);
    }
    public Task SaveAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct = default)
    { _store[(clientId, reconciliation.Id)] = reconciliation; return Task.CompletedTask; }
    public Task<Reconciliation?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, id)));
}

internal sealed class FakeLedgerReader(IReadOnlyList<EntryResponse> entries, decimal bookBalance) : IReconciliationLedgerReader
{
    public Task<IReadOnlyList<EntryResponse>> GetEntriesTouchingAccountAsync(Guid clientId, Guid accountId, CancellationToken ct = default) =>
        Task.FromResult(entries);
    public Task<decimal> GetCashBalanceAsync(Guid clientId, Guid accountId, DateOnly asOf, CancellationToken ct = default) =>
        Task.FromResult(bookBalance);
}

internal sealed class InMemoryBankAdjustmentStore : IBankAdjustmentStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), BankAdjustment> _store = new();
    private long _seq;
    public Task<(BankAdjustment Adjustment, bool Created)> RecordAsync(Guid clientId, Guid id, BankAdjustmentBody body, CancellationToken ct = default)
    {
        BankAdjustment a = new()
        {
            Id = id, Number = $"ADJ-{Interlocked.Increment(ref _seq):D5}",
            ReconciliationId = body.ReconciliationId, CashAccountId = body.CashAccountId,
            OffsetAccountId = body.OffsetAccountId, Kind = body.Kind, Amount = body.Amount,
            Date = body.Date, Memo = body.Memo, Status = BankAdjustmentStatus.Posted,
        };
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _store.TryAdd((clientId, a.Id), a);
        BankAdjustment stored = created ? a : _store[(clientId, a.Id)];
        return Task.FromResult((stored, created));
    }
    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        if (_store.TryGetValue((clientId, id), out BankAdjustment? a))
            _store[(clientId, id)] = a with { Status = BankAdjustmentStatus.Void };
        return Task.CompletedTask;
    }
    public Task<BankAdjustment?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, id)));
    public Task<IReadOnlyList<BankAdjustment>> GetByReconciliationAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BankAdjustment>>(_store.Values.Where(a => a.ReconciliationId == reconciliationId).ToList());
}

internal sealed class FakePostingLedger : ILedgerClient
{
    public PostEntryRequest? Posted { get; private set; }
    public bool Reversed { get; private set; }
    public bool Voided { get; private set; }
    public string EntryPosting { get; set; } = "PendingApproval";   // set "Posted" to simulate an approved entry
    private readonly Guid _entryId = Guid.NewGuid();

    // Maps a SourceRef to the request that first posted under it — mirrors the real engine's idempotent-
    // post arbitration (LedgerEndpoints.PostEntry / EntryComparison.SameFinancialContent), scaled down to
    // this fake's single-slot design (it has no per-entry history, only the most recent Posted). A second
    // PostAsync carrying the SAME SourceRef is a replay when content matches (silently accepted — there is
    // no separate entry to return, unlike the richer per-module fakes) or a key reused for different
    // content (422 in production; an exception here), so a divergent-retry test written against this fake
    // is not vacuous.
    private readonly Dictionary<Guid, PostEntryRequest> _bySourceRef = new();

    // PostAsync is now a check-then-act over a plain (non-concurrent) Dictionary, and this project's
    // AdjustmentRaceTests genuinely drive two racers through it concurrently past their store-side
    // rendezvous. A lock here is minimal (see the 2026-07-26 toctou-status-code plan's Task 9
    // cross-cutting finding).
    private readonly object _postLock = new();

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        lock (_postLock)
        {
            if (entry.SourceRef is { } sourceRef
                && _bySourceRef.TryGetValue(sourceRef, out PostEntryRequest? seen)
                && !SameContent(seen, entry))
            {
                throw new InvalidOperationException(
                    $"Entry for SourceRef {sourceRef} already exists with different content.");
            }

            if (entry.SourceRef is { } sr) _bySourceRef[sr] = entry;
            Posted = entry;
            return Task.FromResult(new PostEntryResponse(_entryId, "Active", EntryPosting));
        }
    }

    // Matches EntryComparison.SameFinancialContent (Backend/Accounting101.Ledger.Core/Journal/
    // EntryComparison.cs) field-for-field, minus SourceRef (already guaranteed equal — the call site
    // above only reaches this after keying on a matching SourceRef). Reference/Memo are deliberately
    // NOT compared: production ignores them (EntryComparison.cs:5-7's own doc comment calls them
    // "descriptive", and the implementation never reads them), so a Memo/Reference-only retry is a
    // successful replay in production, not a 422 — comparing them here would make this fake STRICTER
    // than production, which is exactly the wrong direction for a fidelity fake.
    private static bool SameContent(PostEntryRequest a, PostEntryRequest b) =>
        a.EffectiveDate == b.EffectiveDate
        && a.Type == b.Type
        && a.SourceType == b.SourceType
        && a.Lines.Count == b.Lines.Count
        && a.Lines.Zip(b.Lines).All(p =>
            p.First.AccountId == p.Second.AccountId
            && p.First.Direction == p.Second.Direction
            && p.First.Amount == p.Second.Amount
            && SameDimensions(p.First.Dimensions, p.Second.Dimensions));

    // Ported from EntryComparison.SameDimensions (EntryComparison.cs:37-45), a value comparison (not
    // reference equality) — extended only for PostLineRequest.Dimensions being nullable, unlike
    // production's non-null Line.Dimensions; null and empty are treated as equivalent.
    private static bool SameDimensions(IReadOnlyDictionary<string, Guid>? x, IReadOnlyDictionary<string, Guid>? y)
    {
        int xCount = x?.Count ?? 0;
        int yCount = y?.Count ?? 0;
        if (xCount != yCount) return false;
        if (xCount == 0) return true;
        foreach (KeyValuePair<string, Guid> kv in x!)
            if (!y!.TryGetValue(kv.Key, out Guid v) || v != kv.Value) return false;
        return true;
    }
    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    { Reversed = true; return Task.FromResult(StubEntry()); }
    public Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    { Voided = true; return Task.FromResult(StubEntry()); }
    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntryResponse>>(Posted is null ? [] : [StubEntry()]);

    private EntryResponse StubEntry() =>
        new(_entryId, 0, Posted!.EffectiveDate, "Standard", "Active", EntryPosting, Posted.Lines.Count,
            null, null, null, null, [], SourceRef: Posted.SourceRef, SourceType: Posted.SourceType, ViaModule: "reconciliation");
}
