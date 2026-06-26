using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

public sealed class DocumentPaymentStoreTests(DocumentStoreFixture fixture) : IClassFixture<DocumentStoreFixture>
{
    [Fact]
    public async Task Records_a_payment_then_reads_it_back_by_id_and_customer()
    {
        Guid customer = Guid.NewGuid();
        IPaymentStore store = new DocumentPaymentStore(fixture.Store);

        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, "check",
            [new Allocation(Guid.NewGuid(), 300m)]);
        Payment recorded = await store.RecordPaymentAsync(fixture.ClientId, body);

        Assert.Equal(500m, recorded.Amount);
        Assert.Equal(200m, recorded.Unapplied);
        Assert.False(recorded.Voided);

        Payment? byId = await store.GetPaymentAsync(fixture.ClientId, recorded.Id);
        Assert.NotNull(byId);
        Assert.Single((await store.GetPaymentsByCustomerAsync(fixture.ClientId, customer)));

        await store.VoidAsync(fixture.ClientId, recorded.Id);
        Assert.True((await store.GetPaymentAsync(fixture.ClientId, recorded.Id))!.Voided);
    }
}
