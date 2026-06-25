using Accounting101.Payables;
using Accounting101.Payables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Payables.Tests;

public sealed class ConfiguredBillAccountsProviderTests
{
    [Fact]
    public async Task Reads_the_payable_cash_and_vendor_credits_accounts()
    {
        Guid ap = Guid.NewGuid(), cash = Guid.NewGuid(), credits = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payables:Accounts:Payable"] = ap.ToString(),
            ["Payables:Accounts:Cash"] = cash.ToString(),
            ["Payables:Accounts:VendorCredits"] = credits.ToString(),
        }).Build();

        var provider = new ConfiguredBillAccountsProvider(config);
        Assert.Equal(ap, (await provider.GetBillAccountsAsync(Guid.NewGuid())).PayableAccountId);
        BillPaymentPostingAccounts pay = await provider.GetPaymentAccountsAsync(Guid.NewGuid());
        Assert.Equal(ap, pay.PayableAccountId);
        Assert.Equal(cash, pay.CashAccountId);
        Assert.Equal(credits, pay.VendorCreditsAccountId);
    }

    [Fact]
    public async Task Throws_when_an_account_is_not_configured()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ConfiguredBillAccountsProvider(config).GetBillAccountsAsync(Guid.NewGuid()));
    }
}
