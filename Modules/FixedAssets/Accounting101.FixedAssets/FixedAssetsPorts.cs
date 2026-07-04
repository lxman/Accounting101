using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The module's asset register store — reference documents backed by the engine's document store.
/// Create/update/deactivate lifecycle; the module owns no database connection.</summary>
public interface IAssetStore
{
    Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default);
    Task<UpdateResult> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default);
    Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<PagedResponse<Asset>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default);

    /// <summary>Advance each named asset's AccumulatedDepreciation by its line amount (run post).</summary>
    Task ApplyDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default);

    /// <summary>Roll each named asset's AccumulatedDepreciation back by its line amount (run void).</summary>
    Task ReverseDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default);

    /// <summary>Stamp an Active asset Disposed with its final accumulated depreciation; refuse a missing or
    /// non-Active asset. Returns the prior accumulated (for the disposal's roll-back record).</summary>
    Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, decimal finalAccumulated, CancellationToken ct = default);

    /// <summary>Return a disposed asset to Active with the given accumulated depreciation (disposal void).</summary>
    Task ReinstateAsync(Guid clientId, Guid assetId, decimal restoreAccumulated, CancellationToken ct = default);
}

/// <summary>Outcome of a deactivate: the asset was not found, was already inactive, or was deactivated now.</summary>
public enum DeactivateResult
{
    NotFound,
    AlreadyInactive,
    Deactivated,
    Disposed,
}

public enum ReactivateResult { NotFound, AlreadyActive, Reactivated, Disposed }

public enum UpdateOutcome { NotFound, Inactive, Updated, Disposed }

/// <summary>Outcome of an asset update: not found, refused because inactive (reactivate first), or the
/// updated asset.</summary>
public readonly record struct UpdateResult(UpdateOutcome Outcome, Asset? Asset)
{
    public static readonly UpdateResult NotFound = new(UpdateOutcome.NotFound, null);
    public static readonly UpdateResult Inactive = new(UpdateOutcome.Inactive, null);
    public static readonly UpdateResult Disposed = new(UpdateOutcome.Disposed, null);
    public static UpdateResult Updated(Asset asset) => new(UpdateOutcome.Updated, asset);
}

/// <summary>Outcome of a dispose stamp: asset missing, not in an Active state, or newly disposed.</summary>
public enum DisposeOutcome { NotFound, NotActive, Disposed }

/// <summary>Result of MarkDisposedAsync — the outcome, the stamped asset (when Disposed), and the
/// accumulated depreciation the asset had immediately before disposal.</summary>
public readonly record struct DisposeStamp(DisposeOutcome Outcome, Asset? Asset, decimal PriorAccumulated);
