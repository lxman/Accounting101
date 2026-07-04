using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformStoreModuleSecretTests
{
    private static async Task<PlatformStore> FreshStoreAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);
        return new PlatformStore(mongo.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Get_or_create_persists_a_generated_secret_and_returns_it()
    {
        PlatformStore store = await FreshStoreAsync();
        int calls = 0;
        string result = await store.GetOrCreateModuleSecretAsync("receivables", () => { calls++; return "SECRET-ONE"; });

        Assert.Equal("SECRET-ONE", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Get_or_create_is_idempotent_and_does_not_regenerate()
    {
        PlatformStore store = await FreshStoreAsync();
        string first = await store.GetOrCreateModuleSecretAsync("receivables", () => "FIRST");
        string second = await store.GetOrCreateModuleSecretAsync("receivables", () => "SHOULD-NOT-BE-USED");

        Assert.Equal("FIRST", first);
        Assert.Equal("FIRST", second); // the persisted value wins; generate() is not consulted
    }

    [Fact]
    public async Task Concurrent_get_or_create_for_one_key_converges_on_a_single_secret()
    {
        PlatformStore store = await FreshStoreAsync();
        int n = 0;
        string Gen() => "S" + Interlocked.Increment(ref n); // a distinct value each time it is actually called

        string[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => store.GetOrCreateModuleSecretAsync("cash", Gen)));

        Assert.Single(results.Distinct()); // all callers converge on ONE persisted secret, race notwithstanding
    }
}
