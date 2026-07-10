using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The asset-register lifecycle: validate then create / update, deactivate, get. Validation
/// failures throw ArgumentException (→ 422 at the endpoint). Reads of accumulated depreciation fold the
/// ledger — the per-<c>{Asset}</c> Accumulated Depreciation subledger is the single source of truth —
/// rather than the stored field. The fold is Posted-only (a report shows what is on the books) and negated
/// (Accumulated Depreciation is a contra-asset; the engine's debit-positive fold reads its credit balance
/// NEGATIVE, so <c>accum = −Balance</c> — mirrors <c>CustomerAccountService</c>'s liability negation).</summary>
public sealed class FixedAssetsService(
    IAssetStore store, ILedgerClient ledger, IFixedAssetsAccountsProvider accounts)
{
    public Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default)
    {
        if (AssetValidation.Validate(body) is { } error) throw new ArgumentException(error);
        return store.CreateAsync(clientId, body, ct);
    }

    public Task<UpdateResult> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default)
    {
        if (AssetValidation.Validate(body) is { } error) throw new ArgumentException(error);
        return store.UpdateAsync(clientId, assetId, body, ct);
    }

    public Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default) =>
        store.DeactivateAsync(clientId, assetId, ct);

    public Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default) =>
        store.ReactivateAsync(clientId, assetId, ct);

    public async Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        Asset? asset = await store.GetAsync(clientId, assetId, ct);
        if (asset is null) return null;
        decimal accum = await FoldAccumForAssetAsync(clientId, assetId, includePending: false, ct);
        return asset with { AccumulatedDepreciation = accum };
    }

    public async Task<PagedResponse<Asset>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default)
    {
        PagedResponse<Asset> page = await store.GetByClientPagedAsync(clientId, skip, limit, descending, includeInactive, ct);
        if (page.Items.Count == 0) return page; // nothing to overlay — skip the fold (and its account resolution)
        Dictionary<Guid, decimal> accum = await FoldAccumAsync(clientId, includePending: false, ct);
        List<Asset> overlaid = page.Items.Select(a => a with { AccumulatedDepreciation = accum.GetValueOrDefault(a.Id) }).ToList();
        return new PagedResponse<Asset>(overlaid, page.Total, page.Skip, page.Limit);
    }

    // accum = −Balance (Accumulated Depreciation is a contra-asset; the debit-positive fold reads credits negative).
    private async Task<Dictionary<Guid, decimal>> FoldAccumAsync(Guid clientId, bool includePending, CancellationToken ct)
    {
        FixedAssetsPostingAccounts acc = await accounts.GetAccountsAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> fold =
            await ledger.GetSubledgerAsync(clientId, acc.AccumulatedDepreciationAccountId, "Asset", null, ct, includePending);
        return fold.ToDictionary(l => l.DimensionValue, l => -l.Balance);
    }

    private async Task<decimal> FoldAccumForAssetAsync(Guid clientId, Guid assetId, bool includePending, CancellationToken ct)
    {
        FixedAssetsPostingAccounts acc = await accounts.GetAccountsAsync(clientId, ct);
        return (await ledger.GetSubledgerAsync(clientId, acc.AccumulatedDepreciationAccountId, "Asset", null, ct, includePending))
            .Where(l => l.DimensionValue == assetId).Sum(l => -l.Balance);
    }
}
