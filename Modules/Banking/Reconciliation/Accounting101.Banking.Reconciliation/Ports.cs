using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Bank statements as evidentiary documents (one-step record; immutable).</summary>
public interface IBankStatementStore
{
    Task<BankStatement> RecordAsync(Guid clientId, BankStatementBody body, CancellationToken ct = default);
    Task<BankStatement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<BankStatement>> GetByAccountAsync(Guid clientId, Guid cashAccountId, CancellationToken ct = default);
}

/// <summary>Reconciliations as editable (plain) documents — the cleared set changes until completion.</summary>
public interface IReconciliationStore
{
    Task<Reconciliation> CreateAsync(Guid clientId, Guid cashAccountId, Guid bankStatementId, DateOnly statementDate, CancellationToken ct = default);
    Task SaveAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct = default);
    Task<Reconciliation?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default);
}

/// <summary>The module's READ-ONLY window onto the engine: entries touching a cash account, and that
/// account's as-of balance. Slice 1 never posts.</summary>
public interface IReconciliationLedgerReader
{
    Task<IReadOnlyList<EntryResponse>> GetEntriesTouchingAccountAsync(Guid clientId, Guid accountId, CancellationToken ct = default);
    Task<decimal> GetCashBalanceAsync(Guid clientId, Guid accountId, DateOnly asOf, CancellationToken ct = default);
}
