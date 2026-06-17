using Accounting101.Ledger.Core.Accounts;

namespace Accounting101.Ledger.Core.Tests;

public class ChartOfAccountsTests
{
    private static readonly Guid Client = Guid.NewGuid();

    private static Account Acct(
        string number, string name, AccountType type,
        Guid? parent = null, bool postable = true, bool retainedEarnings = false, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ClientId = Client,
        Number = number,
        Name = name,
        Type = type,
        ParentId = parent,
        Postable = postable,
        IsRetainedEarnings = retainedEarnings,
    };

    [Fact]
    public void Builds_and_navigates_the_hierarchy()
    {
        Account assets = Acct("1000", "Assets", AccountType.Asset, postable: false);
        Account cash = Acct("1100", "Cash", AccountType.Asset, parent: assets.Id, postable: false);
        Account checking = Acct("1110", "Checking", AccountType.Asset, parent: cash.Id);
        Account savings = Acct("1120", "Savings", AccountType.Asset, parent: cash.Id);

        ChartOfAccounts chart = new([assets, cash, checking, savings]);

        Assert.Equal("Cash", chart.Find(cash.Id)!.Name);
        Assert.Equal(checking.Id, chart.FindByNumber("1110")!.Id);
        Assert.Equal([assets.Id], chart.Roots.Select(a => a.Id).ToArray());
        Assert.Equal(2, chart.Children(cash.Id).Count());
        Assert.True(chart.IsLeaf(checking.Id));
        Assert.False(chart.IsLeaf(cash.Id));
        Assert.Equal(3, chart.Descendants(assets.Id).Count()); // cash, checking, savings
    }

    [Fact]
    public void Retained_earnings_is_discoverable()
    {
        Account equity = Acct("3000", "Equity", AccountType.Equity, postable: false);
        Account re = Acct("3900", "Retained Earnings", AccountType.Equity, parent: equity.Id, retainedEarnings: true);

        ChartOfAccounts chart = new([equity, re]);

        Assert.Equal(re.Id, chart.RetainedEarnings!.Id);
    }

    [Fact]
    public void Rejects_a_missing_parent()
    {
        Account orphan = Acct("1110", "Checking", AccountType.Asset, parent: Guid.NewGuid());
        Assert.Throws<InvalidChartOfAccountsException>(() => new ChartOfAccounts([orphan]));
    }

    [Fact]
    public void Rejects_a_child_whose_type_differs_from_its_parent()
    {
        Account assets = Acct("1000", "Assets", AccountType.Asset, postable: false);
        Account wrong = Acct("4000", "Misfiled", AccountType.Revenue, parent: assets.Id);
        Assert.Throws<InvalidChartOfAccountsException>(() => new ChartOfAccounts([assets, wrong]));
    }

    [Fact]
    public void Rejects_duplicate_account_numbers()
    {
        Account a = Acct("1000", "A", AccountType.Asset);
        Account b = Acct("1000", "B", AccountType.Asset);
        Assert.Throws<InvalidChartOfAccountsException>(() => new ChartOfAccounts([a, b]));
    }

    [Fact]
    public void Rejects_more_than_one_retained_earnings_account()
    {
        Account re1 = Acct("3900", "RE 1", AccountType.Equity, retainedEarnings: true);
        Account re2 = Acct("3901", "RE 2", AccountType.Equity, retainedEarnings: true);
        Assert.Throws<InvalidChartOfAccountsException>(() => new ChartOfAccounts([re1, re2]));
    }

    [Fact]
    public void Rejects_a_cycle()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        Account a = Acct("1000", "A", AccountType.Asset, parent: bId, id: aId);
        Account b = Acct("1001", "B", AccountType.Asset, parent: aId, id: bId);
        Assert.Throws<InvalidChartOfAccountsException>(() => new ChartOfAccounts([a, b]));
    }
}
