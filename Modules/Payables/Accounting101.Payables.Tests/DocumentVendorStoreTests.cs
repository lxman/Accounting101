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

    [Fact]
    public async Task Lists_vendors_ordered_by_name_ascending()
    {
        // The fixture uses a shared clientId; other tests may have already saved vendors under it,
        // so we assert relative ordering rather than a fixed count.
        IVendorStore store = new DocumentVendorStore(fixture.Store);
        await store.SaveAsync(fixture.ClientId, new Vendor { Id = Guid.NewGuid(), Name = "Zeta Supplies", Email = null });
        await store.SaveAsync(fixture.ClientId, new Vendor { Id = Guid.NewGuid(), Name = "Acme Parts", Email = "a@x.com" });

        IReadOnlyList<Vendor> vendors = await store.ListAsync(fixture.ClientId);

        int acmeIdx = vendors.ToList().FindIndex(v => v.Name == "Acme Parts");
        int zetaIdx = vendors.ToList().FindIndex(v => v.Name == "Zeta Supplies");
        Assert.True(acmeIdx >= 0, "Acme Parts should be present");
        Assert.True(zetaIdx >= 0, "Zeta Supplies should be present");
        Assert.True(acmeIdx < zetaIdx, "Acme Parts should sort before Zeta Supplies");
    }
}
