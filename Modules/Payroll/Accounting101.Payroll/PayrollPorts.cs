using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll;

/// <summary>The module's payroll run store — evidentiary documents backed by the engine's document
/// store. One-step record (finalize immediately) / void lifecycle; no draft phase.</summary>
public interface IPayrollRunStore
{
    /// <summary>Records the payroll run under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create; under two concurrent callers racing the SAME id
    /// exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document but must
    /// report its own call as a replay. A caller cannot infer this from an earlier existence check — that
    /// check is TOCTOU under concurrency.</summary>
    Task<(PayrollRun Run, bool Created)> RecordAsync(Guid clientId, Guid id, PayrollRunBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default);
    Task<PayrollRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<PayrollRun>> GetByClientAsync(Guid clientId, CancellationToken ct = default);
    Task<PagedResponse<PayrollRun>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);
}

/// <summary>The module's tax remittance store — evidentiary documents backed by the engine's document
/// store. One-step record / void lifecycle; no draft phase.</summary>
public interface ITaxRemittanceStore
{
    /// <summary>Records the tax remittance under <paramref name="id"/>. <c>Created</c> is <c>true</c> only
    /// for the caller that won the underlying atomic create; under two concurrent callers racing the SAME id
    /// exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document but must
    /// report its own call as a replay. A caller cannot infer this from an earlier existence check — that
    /// check is TOCTOU under concurrency.</summary>
    Task<(TaxRemittance Remittance, bool Created)> RecordAsync(Guid clientId, Guid id, TaxRemittanceBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default);
    Task<TaxRemittance?> GetAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default);
    Task<IReadOnlyList<TaxRemittance>> GetByClientAsync(Guid clientId, CancellationToken ct = default);
    Task<PagedResponse<TaxRemittance>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);
}

/// <summary>Resolves the chart accounts the payroll recipes post to for a given client.</summary>
public interface IPayrollAccountsProvider
{
    Task<PayrollPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default);
}
