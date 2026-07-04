namespace Accounting101.FixedAssets;

/// <summary>The asset-register lifecycle: validate then create / update, deactivate, get. Validation
/// failures throw ArgumentException (→ 422 at the endpoint). No ledger dependency — FA-1 does not post.</summary>
public sealed class FixedAssetsService(IAssetStore store)
{
    public Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default)
    {
        if (AssetValidation.Validate(body) is { } error) throw new ArgumentException(error);
        return store.CreateAsync(clientId, body, ct);
    }

    public Task<Asset?> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default)
    {
        if (AssetValidation.Validate(body) is { } error) throw new ArgumentException(error);
        return store.UpdateAsync(clientId, assetId, body, ct);
    }

    public Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default) =>
        store.DeactivateAsync(clientId, assetId, ct);

    public Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default) =>
        store.GetAsync(clientId, assetId, ct);
}
