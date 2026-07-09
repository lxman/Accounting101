using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class MongoAccountStoreTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private MongoAccountStore NewStore() => new(fixture.Database, "accounts_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Account_round_trips_with_all_fields()
    {
        MongoAccountStore store = NewStore();
        var client = Guid.NewGuid();

        Account receivable = new()
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            Number = "1200",
            Name = "Accounts Receivable",
            Type = AccountType.Asset,
            RequiredDimensions = ["Customer"],
        };

        await store.UpsertAsync(receivable);
        Account? read = await store.GetAsync(receivable.Id);

        Assert.NotNull(read);
        Assert.Equal("1200", read!.Number);
        Assert.Equal("Accounts Receivable", read.Name);
        Assert.Equal(AccountType.Asset, read.Type);
        Assert.Equal("Customer", read.RequiredDimension);
        Assert.True(read.Postable);
        Assert.Equal(Direction.Debit, read.NormalSide); // derived semantics survive the round-trip
    }

    [Fact]
    public async Task GetChart_builds_the_validated_client_chart()
    {
        MongoAccountStore store = NewStore();
        var client = Guid.NewGuid();

        Account assets = new() { Id = Guid.NewGuid(), ClientId = client, Number = "1000", Name = "Assets", Type = AccountType.Asset, Postable = false };
        Account cash = new() { Id = Guid.NewGuid(), ClientId = client, Number = "1100", Name = "Cash", Type = AccountType.Asset, ParentId = assets.Id };

        await store.UpsertAsync(assets);
        await store.UpsertAsync(cash);

        ChartOfAccounts chart = await store.GetChartAsync(client);

        Assert.Equal(2, chart.Accounts.Count);
        Assert.Equal(cash.Id, chart.FindByNumber("1100")!.Id);
        Assert.False(chart.IsLeaf(assets.Id));
        Assert.True(chart.IsLeaf(cash.Id));
    }

    [Fact]
    public async Task Upsert_updates_an_existing_account()
    {
        MongoAccountStore store = NewStore();
        var client = Guid.NewGuid();
        var id = Guid.NewGuid();

        await store.UpsertAsync(new Account { Id = id, ClientId = client, Number = "5000", Name = "Rent", Type = AccountType.Expense });
        await store.UpsertAsync(new Account { Id = id, ClientId = client, Number = "5000", Name = "Rent Expense", Type = AccountType.Expense });

        Account? read = await store.GetAsync(id);
        Assert.Equal("Rent Expense", read!.Name); // renamed, not duplicated
    }
}
