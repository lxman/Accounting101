using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Core.Tests;

public class AccountTypeTests
{
    [Theory]
    [InlineData(AccountType.Asset, Direction.Debit)]
    [InlineData(AccountType.Expense, Direction.Debit)]
    [InlineData(AccountType.Liability, Direction.Credit)]
    [InlineData(AccountType.Equity, Direction.Credit)]
    [InlineData(AccountType.Revenue, Direction.Credit)]
    public void Normal_side_follows_the_type(AccountType type, Direction expected) =>
        Assert.Equal(expected, type.NormalSide());

    [Theory]
    [InlineData(AccountType.Revenue, true)]
    [InlineData(AccountType.Expense, true)]
    [InlineData(AccountType.Asset, false)]
    [InlineData(AccountType.Liability, false)]
    [InlineData(AccountType.Equity, false)]
    public void Temporary_accounts_are_revenue_and_expense(AccountType type, bool temporary)
    {
        Assert.Equal(temporary, type.IsTemporary());
        Assert.Equal(!temporary, type.IsPermanent());
    }
}
