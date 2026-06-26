namespace Accounting101.Payables.Tests;

public sealed class DocumentVendorStoreTests(PayablesDocumentStoreFixture fixture) : IClassFixture<PayablesDocumentStoreFixture>
{
    [Fact]
    public async Task Saves_then_reads_a_vendor_back()
    {
        IVendorStore store = new DocumentVendorStore(fixture.Store);
        Vendor v = new() { Id = Guid.NewGuid(), Name = "PropCo", Email = null };
        await store.SaveAsync(fixture.ClientId, v);

        Vendor? read = await store.GetAsync(fixture.ClientId, v.Id);
        Assert.NotNull(read);
        Assert.Equal("PropCo", read!.Name);
    }
}
