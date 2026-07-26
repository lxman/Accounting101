using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash;

/// <summary>The module's cash disbursement store — evidentiary documents backed by the engine's document
/// store. One-step record (finalize immediately) / void lifecycle; no draft phase.</summary>
public interface ICashDisbursementStore
{
    /// <summary>Records the disbursement under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create; under two concurrent callers racing the SAME id
    /// exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document but must
    /// report its own call as a replay. A caller cannot infer this from an earlier existence check — that
    /// check is TOCTOU under concurrency.</summary>
    Task<(CashDisbursement Disbursement, bool Created)> RecordAsync(Guid clientId, Guid id, CashDisbursementBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<CashDisbursement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<CashDisbursement>> GetByClientAsync(Guid clientId, CancellationToken ct = default);
    Task<PagedResponse<CashDisbursement>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);
}

/// <summary>The module's cash deposit store — evidentiary documents backed by the engine's document
/// store. One-step record (finalize immediately) / void lifecycle; no draft phase.</summary>
public interface ICashDepositStore
{
    /// <summary>Records the deposit under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for the
    /// caller that won the underlying atomic create; under two concurrent callers racing the SAME id exactly
    /// one gets <c>true</c>, and the loser still gets back the winner's persisted document but must report
    /// its own call as a replay. A caller cannot infer this from an earlier existence check — that check is
    /// TOCTOU under concurrency.</summary>
    Task<(CashDeposit Deposit, bool Created)> RecordAsync(Guid clientId, Guid id, CashDepositBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<CashDeposit?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<CashDeposit>> GetByClientAsync(Guid clientId, CancellationToken ct = default);
    Task<PagedResponse<CashDeposit>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);
}

/// <summary>Resolves the chart accounts the cash recipes post to for a given client.</summary>
public interface ICashAccountsProvider
{
    Task<CashPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default);
}
