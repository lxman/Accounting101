namespace Accounting101.Invoicing.Tests;

public sealed class DocumentCustomerStoreTests(DocumentStoreFixture fixture) : IClassFixture<DocumentStoreFixture>
{
    [Fact]
    public async Task Customer_round_trips_and_updates()
    {
        DocumentCustomerStore store = new(fixture.Store);
        Guid id = Guid.NewGuid();

        await store.SaveAsync(fixture.ClientId, new Customer { Id = id, Name = "Acme", Email = "a@acme.test" });
        Customer? read = await store.GetAsync(fixture.ClientId, id);
        Assert.Equal("Acme", read!.Name);
        Assert.Equal("a@acme.test", read.Email);

        await store.SaveAsync(fixture.ClientId, new Customer { Id = id, Name = "Acme Inc", Email = null });
        Customer? updated = await store.GetAsync(fixture.ClientId, id);
        Assert.Equal("Acme Inc", updated!.Name);
        Assert.Null(updated.Email);
    }

    [Fact]
    public async Task Missing_customer_returns_null()
    {
        DocumentCustomerStore store = new(fixture.Store);
        Assert.Null(await store.GetAsync(fixture.ClientId, Guid.NewGuid()));
    }
}
