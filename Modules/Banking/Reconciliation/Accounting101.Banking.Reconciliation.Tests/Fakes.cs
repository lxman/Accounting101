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
