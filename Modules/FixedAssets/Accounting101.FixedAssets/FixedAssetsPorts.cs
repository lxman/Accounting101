using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The module's asset register store — reference documents backed by the engine's document store.
/// Create/update/deactivate lifecycle; the module owns no database connection.</summary>
public interface IAssetStore
{
    Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default);
    Task<Asset?> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default);
    Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<PagedResponse<Asset>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default);
}

/// <summary>Outcome of a deactivate: the asset was not found, was already inactive, or was deactivated now.</summary>
public enum DeactivateResult
{
    NotFound,
    AlreadyInactive,
    Deactivated,
}
