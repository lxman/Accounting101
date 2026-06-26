using Accounting101.Receivables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Receivables.Tests;

public sealed class ConfiguredInvoiceAccountsProviderTests
{
    [Fact]
    public async Task Reads_the_three_posting_accounts_from_configuration()
    {
        Guid ar = Guid.NewGuid(), rev = Guid.NewGuid(), tax = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Receivables:Accounts:Receivable"] = ar.ToString(),
            ["Receivables:Accounts:Revenue"] = rev.ToString(),
            ["Receivables:Accounts:SalesTaxPayable"] = tax.ToString(),
        }).Build();

        InvoicePostingAccounts accounts = await new ConfiguredInvoiceAccountsProvider(config).GetAsync(Guid.NewGuid());

        Assert.Equal(ar, accounts.ReceivableAccountId);
        Assert.Equal(rev, accounts.DefaultRevenueAccountId);
        Assert.Equal(tax, accounts.SalesTaxPayableAccountId);
        Assert.Empty(accounts.RevenueAccountsByCategory);   // no category section configured → empty map
    }

    [Fact]
    public async Task Reads_the_revenue_category_map_from_configuration()
    {
        Guid ar = Guid.NewGuid(), rev = Guid.NewGuid(), tax = Guid.NewGuid(), license = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Receivables:Accounts:Receivable"] = ar.ToString(),
            ["Receivables:Accounts:Revenue"] = rev.ToString(),
            ["Receivables:Accounts:SalesTaxPayable"] = tax.ToString(),
            ["Receivables:Accounts:RevenueByCategory:License"] = license.ToString(),
        }).Build();

        InvoicePostingAccounts accounts = await new ConfiguredInvoiceAccountsProvider(config).GetAsync(Guid.NewGuid());

        Assert.Equal(license, accounts.RevenueAccountsByCategory["License"]);
        Assert.Single(accounts.RevenueAccountsByCategory);
    }

    [Fact]
    public async Task Throws_when_an_account_is_not_configured()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ConfiguredInvoiceAccountsProvider(config).GetAsync(Guid.NewGuid()));
    }
}
