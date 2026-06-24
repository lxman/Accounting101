using Accounting101.Invoicing;
using Accounting101.Invoicing.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Invoicing.Tests;

public sealed class ConfiguredPaymentAccountsProviderTests
{
    [Fact]
    public async Task Reads_the_three_payment_accounts_from_configuration()
    {
        Guid ar = Guid.NewGuid(), cash = Guid.NewGuid(), credits = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Invoicing:Accounts:Receivable"] = ar.ToString(),
            ["Invoicing:Accounts:Cash"] = cash.ToString(),
            ["Invoicing:Accounts:CustomerCredits"] = credits.ToString(),
        }).Build();

        PaymentPostingAccounts accounts = await new ConfiguredPaymentAccountsProvider(config).GetAsync(Guid.NewGuid());

        Assert.Equal(ar, accounts.ReceivableAccountId);
        Assert.Equal(cash, accounts.CashAccountId);
        Assert.Equal(credits, accounts.CustomerCreditsAccountId);
    }

    [Fact]
    public async Task Throws_when_an_account_is_not_configured()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ConfiguredPaymentAccountsProvider(config).GetAsync(Guid.NewGuid()));
    }
}
