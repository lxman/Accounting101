using Accounting101.Ledger.Api.Control;
using Accounting101.Payroll.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Payroll.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedPayrollAccountsProviderTests
{
    private static readonly string[] Keys =
        ["SalariesExpense", "PayrollTaxExpense", "Cash", "WithholdingsPayable", "PayrollTaxesPayable"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Payroll:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedPayrollAccountsProvider(new FakeSource(new() { ["Cash"] = stored }), AllConfigured());
        PayrollPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid cash = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Payroll:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Payroll:Accounts:Cash"] = cash.ToString();
        var provider = new StoreBackedPayrollAccountsProvider(new FakeSource(new()), Config(cfg));
        PayrollPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(cash, got.CashAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedPayrollAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccountsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_five_slots_from_the_store()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedPayrollAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        PayrollPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["SalariesExpense"], got.SalariesExpenseAccountId);
        Assert.Equal(map["PayrollTaxExpense"], got.PayrollTaxExpenseAccountId);
        Assert.Equal(map["Cash"], got.CashAccountId);
        Assert.Equal(map["WithholdingsPayable"], got.WithholdingsPayableAccountId);
        Assert.Equal(map["PayrollTaxesPayable"], got.PayrollTaxesPayableAccountId);
    }
}
