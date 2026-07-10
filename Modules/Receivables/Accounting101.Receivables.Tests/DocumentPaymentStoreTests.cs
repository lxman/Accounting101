namespace Accounting101.Receivables.Tests;

public sealed class DocumentPaymentStoreTests(DocumentStoreFixture fixture) : IClassFixture<DocumentStoreFixture>
{
    [Fact]
    public async Task Records_a_payment_then_reads_it_back_by_id_and_customer()
    {
        // PaymentBody carries no allocation array — the per-invoice split lives only as ledger dimensions
        // on the payment's own entry, which this store never touches. That is now a compile-time guarantee:
        // PaymentBody has no Allocations parameter to pass.
        Guid customer = Guid.NewGuid();
        IPaymentStore store = new DocumentPaymentStore(fixture.Store);

        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, "check");
        Payment recorded = await store.RecordPaymentAsync(fixture.ClientId, body);

        Assert.Equal(500m, recorded.Amount);
        Assert.False(recorded.Voided);

        Payment? byId = await store.GetPaymentAsync(fixture.ClientId, recorded.Id);
        Assert.NotNull(byId);
        Assert.Equal(500m, byId!.Amount);
        Assert.Single((await store.GetPaymentsByCustomerAsync(fixture.ClientId, customer)));

        await store.VoidAsync(fixture.ClientId, recorded.Id);
        Assert.True((await store.GetPaymentAsync(fixture.ClientId, recorded.Id))!.Voided);
    }
}
