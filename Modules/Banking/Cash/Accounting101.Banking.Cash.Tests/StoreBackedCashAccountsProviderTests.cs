using Accounting101.Banking.Cash;
using Accounting101.Banking.Cash.Api;
using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Banking.Cash.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedCashAccountsProviderTests
{
    private static IConfiguration Config(string? cash) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cash:Accounts:Cash"] = cash }).Build();

    [Fact]
    public async Task Prefers_the_stored_account_over_config()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedCashAccountsProvider(new FakeSource(new() { ["Cash"] = stored }), Config(Guid.NewGuid().ToString()));
        CashPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_unset()
    {
        Guid configured = Guid.NewGuid();
        var provider = new StoreBackedCashAccountsProvider(new FakeSource(new()), Config(configured.ToString()));
        CashPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(configured, got.CashAccountId);
    }

    [Fact]
    public async Task Throws_when_neither_store_nor_config_supplies_it()
    {
        var provider = new StoreBackedCashAccountsProvider(new FakeSource(new()), Config(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccountsAsync(Guid.NewGuid()));
    }
}
