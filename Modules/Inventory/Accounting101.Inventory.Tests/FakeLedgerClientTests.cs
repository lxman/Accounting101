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

    /// <summary>
    /// Production answers 422 when a retry key is reused for DIFFERENT content; the fake must too.
    /// Without this, a divergent-retry test written against the fake passes vacuously — it looks like
    /// protection and provides none.
    /// </summary>
    [Fact]
    public async Task Posting_the_same_source_ref_with_different_content_is_rejected()
    {
        FakeLedgerClient ledger = new();
        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();

        PostEntryRequest first = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "r", Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ],
            SourceRef: sourceRef, SourceType: "Test");

        await ledger.PostAsync(clientId, first);

        PostEntryRequest divergent = first with { Memo = "changed" };

        await Assert.ThrowsAnyAsync<Exception>(() => ledger.PostAsync(clientId, divergent));
    }

    /// <summary>An identical re-post still dedupes — only DIVERGENT content is rejected.</summary>
    [Fact]
    public async Task Posting_the_same_source_ref_with_identical_content_still_dedupes()
    {
        FakeLedgerClient ledger = new();
        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();

        PostEntryRequest entry = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "r", Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ],
            SourceRef: sourceRef, SourceType: "Test");

        await ledger.PostAsync(clientId, entry);
        await ledger.PostAsync(clientId, entry);

        Assert.Single(ledger.Posted, e => e.SourceRef == sourceRef);
    }
}
