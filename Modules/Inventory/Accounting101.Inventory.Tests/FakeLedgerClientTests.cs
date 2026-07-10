using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Inventory.Tests;

public class FakeLedgerClientTests
{
    private static readonly Guid Client = Guid.NewGuid();

    [Fact]
    public async Task Subledger_fold_is_debit_positive_grouped_by_dimension_and_gated_by_posting()
    {
        var ledger = new FakeLedgerClient();
        Guid inv = Guid.NewGuid();
        Guid itemA = Guid.NewGuid();
        var dims = new Dictionary<string, Guid> { ["Item"] = itemA };

        await ledger.PostAsync(Client, new PostEntryRequest(
            Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null,
            [new PostLineRequest(inv, "Debit", 100m, dims), new PostLineRequest(Guid.NewGuid(), "Credit", 100m)],
            SourceRef: Guid.NewGuid(), SourceType: "StockMovement"));

        // Pending is invisible to a posted-only fold, visible to a pending-inclusive one.
        Assert.Empty(await ledger.GetSubledgerAsync(Client, inv, "Item", null));
        IReadOnlyList<SubledgerLineResponse> pending =
            await ledger.GetSubledgerAsync(Client, inv, "Item", null, default, includePending: true);
        Assert.Equal(100m, pending.Single(l => l.DimensionValue == itemA).Balance);

        ledger.ApproveAll();
        IReadOnlyList<SubledgerLineResponse> posted = await ledger.GetSubledgerAsync(Client, inv, "Item", null);
        Assert.Equal(100m, posted.Single(l => l.DimensionValue == itemA).Balance);
    }

    [Fact]
    public async Task GetEntriesBySourceRefs_returns_entries_for_all_requested_refs()
    {
        var ledger = new FakeLedgerClient();
        Guid refA = Guid.NewGuid(), refB = Guid.NewGuid();
        await ledger.PostAsync(Client, Req(refA));
        await ledger.PostAsync(Client, Req(refB));

        IReadOnlyList<EntryResponse> got = await ledger.GetEntriesBySourceRefsAsync(Client, [refA, refB]);
        Assert.Equal(2, got.Count);
        Assert.Empty(await ledger.GetEntriesBySourceRefsAsync(Client, []));
    }

    private static PostEntryRequest Req(Guid sourceRef) => new(
        Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null,
        [new PostLineRequest(Guid.NewGuid(), "Debit", 1m), new PostLineRequest(Guid.NewGuid(), "Credit", 1m)],
        SourceRef: sourceRef, SourceType: "StockMovement");
}
