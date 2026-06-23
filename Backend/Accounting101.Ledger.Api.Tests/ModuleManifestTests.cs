using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Documents;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ModuleManifestTests
{
    [Fact]
    public void Builder_records_policy_and_indexed_tags_per_collection()
    {
        ModuleManifestBuilder builder = new();
        builder.Reference("customers", "Status").Evidentiary("invoices", "Customer", "Status", "Number");
        ModuleManifest manifest = builder.Build();

        Assert.Equal(CollectionPolicy.Reference, manifest.PolicyOf("customers"));
        Assert.Equal(CollectionPolicy.Evidentiary, manifest.PolicyOf("invoices"));
        Assert.Equal(["Customer", "Status", "Number"], manifest.IndexedTags("invoices"));
        Assert.Contains("customers", manifest.Collections);
    }

    [Fact]
    public void PolicyOf_rejects_an_undeclared_collection()
    {
        ModuleManifest manifest = new ModuleManifestBuilder().Plain("scratch").Build();
        Assert.Throws<ModuleDocumentException>(() => manifest.PolicyOf("nope"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("in voicing")]
    [InlineData("invoicing.x")]
    [InlineData("invoicing$x")]
    public void ModuleIdentity_rejects_keys_unsafe_as_a_collection_prefix(string key)
    {
        Assert.Throws<ArgumentException>(() => new ModuleIdentity(key));
    }
}
