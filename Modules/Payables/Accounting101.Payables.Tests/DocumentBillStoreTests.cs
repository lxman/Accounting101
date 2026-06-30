using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables.Tests;

public sealed class DocumentBillStoreTests(PayablesDocumentStoreFixture fixture) : IClassFixture<PayablesDocumentStoreFixture>
{
    private static BillBody Body(Guid vendorId) => new(
        vendorId, new DateOnly(2026, 3, 1), null, null, null,
        [new BillLineBody("Rent", 100m, Guid.NewGuid())]);

    [Fact]
    public async Task Draft_lands_in_the_plain_collection_with_no_number()
    {
        Guid vendorId = Guid.NewGuid();
        DocumentBillStore store = new(fixture.Store);

        Bill draft = await store.CreateDraftAsync(fixture.ClientId, Body(vendorId));

        Assert.Equal(BillStatus.Draft, draft.Status);
        Assert.Null(draft.Number);
    }

    [Fact]
    public async Task Promote_creates_a_new_entered_id_and_deletes_the_draft()
    {
        Guid vendorId = Guid.NewGuid();
        DocumentBillStore store = new(fixture.Store);
        Bill draft = await store.CreateDraftAsync(fixture.ClientId, Body(vendorId));

        Bill entered = await store.PromoteDraftAsync(fixture.ClientId, draft.Id);

        Assert.NotEqual(draft.Id, entered.Id);
        Assert.Equal(BillStatus.Entered, entered.Status);
        Assert.StartsWith("BILL-", entered.Number);
        Assert.Null(await store.GetAsync(fixture.ClientId, draft.Id));
        Assert.NotNull(await store.GetAsync(fixture.ClientId, entered.Id));
    }

    [Fact]
    public async Task Update_and_discard_only_act_on_drafts()
    {
        Guid vendorId = Guid.NewGuid();
        DocumentBillStore store = new(fixture.Store);
        Bill draft = await store.CreateDraftAsync(fixture.ClientId, Body(vendorId));

        Bill updated = await store.UpdateDraftAsync(fixture.ClientId, draft.Id, Body(vendorId) with { Memo = "m" });
        Assert.Equal("m", updated.Memo);

        await store.DiscardDraftAsync(fixture.ClientId, draft.Id);
        Assert.Null(await store.GetAsync(fixture.ClientId, draft.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DiscardDraftAsync(fixture.ClientId, draft.Id));
    }
}
