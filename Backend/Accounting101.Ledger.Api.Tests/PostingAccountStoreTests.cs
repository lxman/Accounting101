using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PostingAccountStoreTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Set_then_get_round_trips_a_modules_slots()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        Guid clientId = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = cash });

        PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
        Assert.Equal(cash, doc.Accounts["cash"]["Cash"]);
    }

    [Fact]
    public async Task Setting_one_module_does_not_clobber_another()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        Guid clientId = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = Guid.NewGuid() });
        Guid inv = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "inventory", new Dictionary<string, Guid> { ["InventoryAsset"] = inv });

        PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
        Assert.True(doc.Accounts.ContainsKey("cash"));
        Assert.Equal(inv, doc.Accounts["inventory"]["InventoryAsset"]);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unset_client()
    {
        Assert.Null(await fixture.PostingAccounts().GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Source_returns_stored_map_or_empty_when_unset()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        StorePostingAccountsSource source = new(store);
        Guid clientId = Guid.NewGuid();
        Assert.Empty(await source.GetAsync(clientId, "cash"));

        Guid cash = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = cash });
        IReadOnlyDictionary<string, Guid> got = await source.GetAsync(clientId, "cash");
        Assert.Equal(cash, got["Cash"]);
    }

    [Fact]
    public void Registry_contains_the_cash_slot()
    {
        PostingAccountSlot slot = Assert.Single(PostingAccountSlots.ForModule("cash"));
        Assert.Equal("Cash", slot.SlotKey);
        Assert.Equal("Asset", slot.ExpectedType);
        Assert.Contains("cash", PostingAccountSlots.ModuleKeys);
    }

    [Fact]
    public async Task Set_upserts_a_fresh_client()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        Guid clientId = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "payroll", new Dictionary<string, Guid> { ["Cash"] = cash });
        PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
        Assert.Equal(cash, doc.Accounts["payroll"]["Cash"]);
    }

    [Fact]
    public async Task Overwriting_a_module_replaces_only_its_map_and_keeps_others()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        Guid clientId = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = Guid.NewGuid() });
        await store.SetModuleAsync(clientId, "payroll", new Dictionary<string, Guid> { ["Cash"] = Guid.NewGuid() });
        Guid newPay = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "payroll", new Dictionary<string, Guid> { ["Cash"] = newPay });

        PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
        Assert.Equal(newPay, doc.Accounts["payroll"]["Cash"]);   // payroll replaced
        Assert.True(doc.Accounts.ContainsKey("cash"));           // cash preserved by the targeted update
    }
}
