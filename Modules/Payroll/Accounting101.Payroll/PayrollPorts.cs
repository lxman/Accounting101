namespace Accounting101.Payroll;

/// <summary>The module's payroll run store — evidentiary documents backed by the engine's document
/// store. One-step record (finalize immediately) / void lifecycle; no draft phase.</summary>
public interface IPayrollRunStore
{
    Task<PayrollRun> RecordAsync(Guid clientId, PayrollRunBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default);
    Task<PayrollRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<PayrollRun>> GetByClientAsync(Guid clientId, CancellationToken ct = default);
}

/// <summary>The module's tax remittance store — evidentiary documents backed by the engine's document
/// store. One-step record / void lifecycle; no draft phase.</summary>
public interface ITaxRemittanceStore
{
    Task<TaxRemittance> RecordAsync(Guid clientId, TaxRemittanceBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default);
    Task<TaxRemittance?> GetAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default);
    Task<IReadOnlyList<TaxRemittance>> GetByClientAsync(Guid clientId, CancellationToken ct = default);
}

/// <summary>Resolves the chart accounts the payroll recipes post to for a given client.</summary>
public interface IPayrollAccountsProvider
{
    Task<PayrollPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default);
}
