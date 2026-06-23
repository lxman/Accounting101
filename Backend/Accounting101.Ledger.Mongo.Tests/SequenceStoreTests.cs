namespace Accounting101.Ledger.Mongo.Tests;

public sealed class SequenceStoreTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private MongoSequenceStore Store() =>
        new(fixture.Database, "counters_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Next_is_gapless_and_per_counter()
    {
        MongoSequenceStore store = Store();

        Assert.Equal(1, await store.NextAsync("invoice"));
        Assert.Equal(2, await store.NextAsync("invoice"));
        Assert.Equal(1, await store.NextAsync("creditmemo")); // an independent counter
        Assert.Equal(3, await store.NextAsync("invoice"));
    }
}
