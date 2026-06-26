using Accounting101.Receivables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Receivables.Tests;

public sealed class ConfiguredPaymentAccountsProviderTests
{
    [Fact]
    public async Task Reads_the_five_payment_accounts_from_configuration()
    {
        Guid ar = Guid.NewGuid(), cash = Guid.NewGuid(), credits = Guid.NewGuid(),
             badDebt = Guid.NewGuid(), salesReturns = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Receivables:Accounts:Receivable"] = ar.ToString(),
            ["Receivables:Accounts:Cash"] = cash.ToString(),
            ["Receivables:Accounts:CustomerCredits"] = credits.ToString(),
            ["Receivables:Accounts:BadDebtExpense"] = badDebt.ToString(),
            ["Receivables:Accounts:SalesReturns"] = salesReturns.ToString(),
        }).Build();

        PaymentPostingAccounts accounts = await new ConfiguredPaymentAccountsProvider(config).GetAsync(Guid.NewGuid());

        Assert.Equal(ar, accounts.ReceivableAccountId);
        Assert.Equal(cash, accounts.CashAccountId);
        Assert.Equal(credits, accounts.CustomerCreditsAccountId);
        Assert.Equal(badDebt, accounts.BadDebtExpenseAccountId);
        Assert.Equal(salesReturns, accounts.SalesReturnsAccountId);
    }

    [Fact]
    public async Task Throws_when_an_account_is_not_configured()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ConfiguredPaymentAccountsProvider(config).GetAsync(Guid.NewGuid()));
    }
}
