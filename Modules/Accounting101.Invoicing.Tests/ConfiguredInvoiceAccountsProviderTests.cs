using Accounting101.Invoicing;
using Accounting101.Invoicing.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Invoicing.Tests;

public sealed class ConfiguredInvoiceAccountsProviderTests
{
    [Fact]
    public async Task Reads_the_three_posting_accounts_from_configuration()
    {
        Guid ar = Guid.NewGuid(), rev = Guid.NewGuid(), tax = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Invoicing:Accounts:Receivable"] = ar.ToString(),
            ["Invoicing:Accounts:Revenue"] = rev.ToString(),
            ["Invoicing:Accounts:SalesTaxPayable"] = tax.ToString(),
        }).Build();

        InvoicePostingAccounts accounts = await new ConfiguredInvoiceAccountsProvider(config).GetAsync(Guid.NewGuid());

        Assert.Equal(ar, accounts.ReceivableAccountId);
        Assert.Equal(rev, accounts.RevenueAccountId);
        Assert.Equal(tax, accounts.SalesTaxPayableAccountId);
    }

    [Fact]
    public async Task Throws_when_an_account_is_not_configured()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ConfiguredInvoiceAccountsProvider(config).GetAsync(Guid.NewGuid()));
    }
}
