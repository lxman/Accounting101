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
    public Task<BankAdjustment> RecordAsync(Guid clientId, BankAdjustmentBody body, CancellationToken ct = default)
    {
        BankAdjustment a = new()
        {
            Id = Guid.NewGuid(), Number = $"ADJ-{Interlocked.Increment(ref _seq):D5}",
            ReconciliationId = body.ReconciliationId, CashAccountId = body.CashAccountId,
            OffsetAccountId = body.OffsetAccountId, Kind = body.Kind, Amount = body.Amount,
            Date = body.Date, Memo = body.Memo, Status = BankAdjustmentStatus.Posted,
        };
        _store[(clientId, a.Id)] = a;
        return Task.FromResult(a);
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

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        Posted = entry;
        return Task.FromResult(new PostEntryResponse(_entryId, "Active", EntryPosting));
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
