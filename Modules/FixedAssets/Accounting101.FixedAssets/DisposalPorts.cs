using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The disposal store — evidentiary documents backed by the engine's document store. Numbered +
/// finalized on record, voidable. Adds the by-asset lookup the disposal service uses to guard re-disposal
/// and to locate a disposal to void.</summary>
public interface IDisposalStore
{
    Task<Disposal> RecordAsync(Guid clientId, DisposalBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid disposalId, CancellationToken ct = default);
    Task<Disposal?> GetAsync(Guid clientId, Guid disposalId, CancellationToken ct = default);
    Task<PagedResponse<Disposal>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);

    /// <summary>The non-voided disposal for an asset, if one exists.</summary>
    Task<Disposal?> GetActiveByAssetAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
}
