namespace Accounting101.Invoicing.Tests;

public sealed class DocumentInvoiceStoreTests(DocumentStoreFixture fixture) : IClassFixture<DocumentStoreFixture>
{
    private static InvoiceBody Body(Guid customerId) =>
        new(customerId, new DateOnly(2026, 3, 31), null, 0.07m, "thanks",
            [new LineBody("Work", 1m, 100m, true)]);

    [Fact]
    public async Task Draft_has_no_number_then_finalize_assigns_consecutive_gapless_numbers()
    {
        DocumentInvoiceStore store = new(fixture.Store);
        Guid customer = Guid.NewGuid();

        Invoice draftA = await store.CreateDraftAsync(fixture.ClientId, Body(customer));
        Assert.Equal(InvoiceStatus.Draft, draftA.Status);
        Assert.Null(draftA.Number);                                  // a draft has no number

        Invoice issuedA = await store.FinalizeAsync(fixture.ClientId, draftA.Id);
        Assert.Equal(InvoiceStatus.Issued, issuedA.Status);
        Assert.NotNull(issuedA.Number);
        Assert.StartsWith("INV-", issuedA.Number);

        Invoice draftB = await store.CreateDraftAsync(fixture.ClientId, Body(customer));
        Invoice issuedB = await store.FinalizeAsync(fixture.ClientId, draftB.Id);

        Assert.Equal(Seq(issuedA.Number!) + 1, Seq(issuedB.Number!)); // gapless: consecutive, order-independent

        static int Seq(string number) => int.Parse(number["INV-".Length..]);
    }

    [Fact]
    public async Task Get_reflects_derived_status_and_void_hides_from_customer_query()
    {
        DocumentInvoiceStore store = new(fixture.Store);
        Guid customer = Guid.NewGuid();
        Invoice draft = await store.CreateDraftAsync(fixture.ClientId, Body(customer));
        await store.FinalizeAsync(fixture.ClientId, draft.Id);

        Assert.Equal(InvoiceStatus.Issued, (await store.GetAsync(fixture.ClientId, draft.Id))!.Status);
        Assert.Single(await store.GetByCustomerAsync(fixture.ClientId, customer));

        await store.VoidAsync(fixture.ClientId, draft.Id);

        Assert.Equal(InvoiceStatus.Void, (await store.GetAsync(fixture.ClientId, draft.Id))!.Status); // still gettable by id
        Assert.Empty(await store.GetByCustomerAsync(fixture.ClientId, customer));                      // hidden from query
    }
}
