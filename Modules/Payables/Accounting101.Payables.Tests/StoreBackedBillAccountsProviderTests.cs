using Accounting101.Ledger.Api.Control;
using Accounting101.Payables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Payables.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedBillAccountsProviderTests
{
    private static readonly string[] Keys = ["Payable", "Cash", "VendorCredits"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Payables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedBillAccountsProvider(new FakeSource(new() { ["Cash"] = stored }), AllConfigured());
        BillPaymentPostingAccounts got = await provider.GetPaymentAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid cash = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Payables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Payables:Accounts:Cash"] = cash.ToString();
        var provider = new StoreBackedBillAccountsProvider(new FakeSource(new()), Config(cfg));
        BillPaymentPostingAccounts got = await provider.GetPaymentAccountsAsync(Guid.NewGuid());
        Assert.Equal(cash, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_stored_slot_is_empty_guid()
    {
        Guid cash = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Payables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Payables:Accounts:Cash"] = cash.ToString();
        var provider = new StoreBackedBillAccountsProvider(new FakeSource(new() { ["Cash"] = Guid.Empty }), Config(cfg));
        BillPaymentPostingAccounts got = await provider.GetPaymentAccountsAsync(Guid.NewGuid());
        Assert.Equal(cash, got.CashAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedBillAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetBillAccountsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_three_slots_from_the_store_into_both_records()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedBillAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));

        BillPostingAccounts bill = await provider.GetBillAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["Payable"], bill.PayableAccountId);

        BillPaymentPostingAccounts pay = await provider.GetPaymentAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["Payable"], pay.PayableAccountId);
        Assert.Equal(map["Cash"], pay.CashAccountId);
        Assert.Equal(map["VendorCredits"], pay.VendorCreditsAccountId);
    }
}
