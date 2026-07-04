using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The depreciation-run store — evidentiary documents backed by the engine's document store.
/// Numbered + finalized on record, voidable. Adds the period-guard and LIFO-void queries the run
/// service needs.</summary>
public interface IDepreciationRunStore
{
    Task<DepreciationRun> RecordAsync(Guid clientId, DepreciationRunBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default);
    Task<DepreciationRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default);
    Task<PagedResponse<DepreciationRun>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);

    /// <summary>The non-voided run for a period, if one exists (period guard).</summary>
    Task<DepreciationRun?> GetByPeriodAsync(Guid clientId, DepreciationPeriod period, CancellationToken ct = default);

    /// <summary>The most recent non-voided run, if any (LIFO void guard).</summary>
    Task<DepreciationRun?> GetLatestAsync(Guid clientId, CancellationToken ct = default);
}
