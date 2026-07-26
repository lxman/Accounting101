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
        (Payment recorded, bool _) = await store.RecordPaymentAsync(fixture.ClientId, Guid.NewGuid(), body);

        Assert.Equal(500m, recorded.Amount);
        Assert.False(recorded.Voided);

        Payment? byId = await store.GetPaymentAsync(fixture.ClientId, recorded.Id);
        Assert.NotNull(byId);
        Assert.Equal(500m, byId!.Amount);
        Assert.Single((await store.GetPaymentsByCustomerAsync(fixture.ClientId, customer)));

        await store.VoidAsync(fixture.ClientId, recorded.Id);
        Assert.True((await store.GetPaymentAsync(fixture.ClientId, recorded.Id))!.Voided);
    }

    /// <summary>
    /// <see cref="DocumentPaymentStore.GetPaymentsByCustomerAsync"/> passes <c>includeVoided: true</c>
    /// to the underlying query — a real, user-visible behaviour (the Payments list greys out voided
    /// payments rather than hiding them; see
    /// <c>PaymentService.GetPaymentsWithAllocationsByCustomerAsync</c>'s own doc comment). This proves
    /// it against the real engine-backed document store, not a fake: record a payment, void it through
    /// the store's own module-level <see cref="IPaymentStore.VoidAsync"/>, then confirm it still comes
    /// back from the by-customer list instead of vanishing.
    ///
    /// DISCRIMINATOR: temporarily remove <c>includeVoided: true</c> from
    /// <c>DocumentPaymentStore.GetPaymentsByCustomerAsync</c> (falling back to the <c>QueryAsync</c>
    /// default of <c>false</c>) and this test goes red — the voided payment disappears from the list
    /// instead of appearing greyed.
    /// </summary>
    [Fact]
    public async Task A_voided_payment_still_appears_in_the_customer_list()
    {
        Guid customer = Guid.NewGuid();
        IPaymentStore store = new DocumentPaymentStore(fixture.Store);

        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, "check");
        (Payment recorded, bool created) = await store.RecordPaymentAsync(fixture.ClientId, Guid.NewGuid(), body);
        Assert.True(created);
        Assert.False(recorded.Voided);

        await store.VoidAsync(fixture.ClientId, recorded.Id);

        IReadOnlyList<Payment> byCustomer = await store.GetPaymentsByCustomerAsync(fixture.ClientId, customer);
        Payment voided = Assert.Single(byCustomer);
        Assert.True(voided.Voided);
    }
}
