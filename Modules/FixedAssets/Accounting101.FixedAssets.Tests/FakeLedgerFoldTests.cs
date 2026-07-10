using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FakeLedgerFoldTests
{
    [Fact]
    public async Task Fold_groups_dimensioned_lines_by_asset_and_signs_credit_negative()
    {
        var fake = new FakeLedgerClient();
        Guid client = Guid.NewGuid(), accum = Guid.NewGuid(), expense = Guid.NewGuid();
        Guid assetA = Guid.NewGuid(), assetB = Guid.NewGuid();

        await fake.PostAsync(client, new PostEntryRequest(
            null, new DateOnly(2026, 6, 30), null, null,
            [
                new PostLineRequest(expense, "Debit", 300m),
                new PostLineRequest(accum, "Credit", 200m, new Dictionary<string, Guid> { ["Asset"] = assetA }),
                new PostLineRequest(accum, "Credit", 100m, new Dictionary<string, Guid> { ["Asset"] = assetB }),
            ],
            SourceRef: Guid.NewGuid(), SourceType: "DepreciationRun"));

        IReadOnlyList<SubledgerLineResponse> fold = await fake.GetSubledgerAsync(client, accum, "Asset", null);

        // Contra-asset: credit lines read NEGATIVE in the debit-positive fold.
        Assert.Equal(-200m, fold.Single(l => l.DimensionValue == assetA).Balance);
        Assert.Equal(-100m, fold.Single(l => l.DimensionValue == assetB).Balance);
    }
}
