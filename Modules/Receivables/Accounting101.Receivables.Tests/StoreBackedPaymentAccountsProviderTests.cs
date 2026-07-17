using Accounting101.Ledger.Api.Control;
using Accounting101.Receivables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Receivables.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedPaymentAccountsProviderTests
{
    private static readonly string[] Keys = ["Receivable", "Cash", "CustomerCredits", "BadDebtExpense", "SalesReturns"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(new() { ["Cash"] = stored }), AllConfigured());
        PaymentPostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid cash = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:Cash"] = cash.ToString();
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(new()), Config(cfg));
        PaymentPostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(cash, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_stored_slot_is_empty_guid()
    {
        Guid cash = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:Cash"] = cash.ToString();
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(new() { ["Cash"] = Guid.Empty }), Config(cfg));
        PaymentPostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(cash, got.CashAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_five_slots_from_the_store_including_shared_receivable()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        PaymentPostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(map["Receivable"],      got.ReceivableAccountId);
        Assert.Equal(map["Cash"],            got.CashAccountId);
        Assert.Equal(map["CustomerCredits"], got.CustomerCreditsAccountId);
        Assert.Equal(map["BadDebtExpense"],  got.BadDebtExpenseAccountId);
        Assert.Equal(map["SalesReturns"],    got.SalesReturnsAccountId);
    }
}
