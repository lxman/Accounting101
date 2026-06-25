using Accounting101.Payables;

namespace Accounting101.Payables.Tests;

public sealed class DocumentBillStoreTests(PayablesDocumentStoreFixture fixture) : IClassFixture<PayablesDocumentStoreFixture>
{
    [Fact]
    public async Task Drafts_then_enters_a_bill_and_reads_it_by_vendor()
    {
        IBillStore store = new DocumentBillStore(fixture.Store);
        Guid vendor = Guid.NewGuid();
        BillBody body = new(vendor, new DateOnly(2026, 3, 1), null, "V-123", null,
            [new BillLineBody("Rent", 6000m, Guid.NewGuid())]);

        Bill draft = await store.CreateDraftAsync(fixture.ClientId, body);
        Assert.Equal(BillStatus.Draft, draft.Status);
        Assert.Null(draft.Number);

        Bill entered = await store.FinalizeAsync(fixture.ClientId, draft.Id);
        Assert.Equal(BillStatus.Entered, entered.Status);
        Assert.NotNull(entered.Number);

        Assert.Single(await store.GetByVendorAsync(fixture.ClientId, vendor));
    }
}
