using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Reporting;

namespace Accounting101.Ledger.Core.Tests;

/// <summary>
/// The pure statement arrangers: a balanced journal yields a balanced sheet, credit-normal accounts
/// read positive, current-period earnings fold into equity, and the two statements report the same net
/// income. All driven from a chart plus a debit-positive balance map — no persistence in the loop.
/// </summary>
public class FinancialStatementTests
{
    private static readonly Guid Client = Guid.NewGuid();

    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid AccountsPayable = Guid.NewGuid();
    private static readonly Guid CommonStock = Guid.NewGuid();
    private static readonly Guid RetainedEarnings = Guid.NewGuid();
    private static readonly Guid Sales = Guid.NewGuid();
    private static readonly Guid RentExpense = Guid.NewGuid();

    private static Account Acct(Guid id, string number, string name, AccountType type, bool retained = false) => new()
    {
        Id = id,
        ClientId = Client,
        Number = number,
        Name = name,
        Type = type,
        IsRetainedEarnings = retained,
    };

    private static ChartOfAccounts Chart() => new(
    [
        Acct(Cash, "1000", "Cash", AccountType.Asset),
        Acct(AccountsPayable, "2000", "Accounts Payable", AccountType.Liability),
        Acct(CommonStock, "3000", "Common Stock", AccountType.Equity),
        Acct(RetainedEarnings, "3900", "Retained Earnings", AccountType.Equity, retained: true),
        Acct(Sales, "4000", "Sales", AccountType.Revenue),
        Acct(RentExpense, "5000", "Rent Expense", AccountType.Expense),
    ]);

    // A balanced, mid-year set of debit-positive balances (nets to zero): cash 1000 debit, A/P (400)
    // credit, common stock (400) credit, sales (400) credit, rent 200 debit. Net income is therefore 200.
    private static Dictionary<Guid, decimal> MidYearBalances() => new()
    {
        [Cash] = 1000m,
        [AccountsPayable] = -400m,
        [CommonStock] = -400m,
        [Sales] = -400m,
        [RentExpense] = 200m,
    };

    [Fact]
    public void Balance_sheet_balances_with_credit_normal_accounts_shown_positive()
    {
        BalanceSheet sheet = BalanceSheet.Build(Chart(), MidYearBalances(), new DateOnly(2026, 6, 30));

        Assert.Equal(1000m, sheet.TotalAssets);
        Assert.Equal(400m, sheet.Liabilities.Total);   // a credit-balance liability reads positive
        Assert.Equal(1000m, sheet.TotalLiabilitiesAndEquity);
        Assert.True(sheet.IsBalanced);

        // Common stock (credit-normal) is presented as a positive 400, not −400.
        Assert.Equal(400m, sheet.Equity.Lines.Single(l => l.AccountId == CommonStock).Amount);
    }

    [Fact]
    public void Current_period_net_income_folds_into_equity()
    {
        BalanceSheet sheet = BalanceSheet.Build(Chart(), MidYearBalances(), new DateOnly(2026, 6, 30));

        StatementLine netIncome = sheet.Equity.Lines.Single(l => l.AccountId is null);
        Assert.Equal("Net income", netIncome.Name);
        Assert.Equal(200m, netIncome.Amount);
        Assert.Equal(600m, sheet.Equity.Total); // common stock 400 + net income 200 (retained earnings 0)
    }

    [Fact]
    public void Income_statement_nets_revenue_less_expenses_on_their_natural_side()
    {
        IncomeStatement statement = IncomeStatement.Build(
            Chart(), MidYearBalances(), new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        Assert.Equal(400m, statement.Revenue.Total);   // credit-balance revenue reads positive
        Assert.Equal(200m, statement.Expenses.Total);  // debit-balance expense reads positive
        Assert.Equal(200m, statement.NetIncome);
    }

    [Fact]
    public void The_two_statements_report_the_same_net_income()
    {
        Dictionary<Guid, decimal> balances = MidYearBalances();
        BalanceSheet sheet = BalanceSheet.Build(Chart(), balances, new DateOnly(2026, 6, 30));
        IncomeStatement statement = IncomeStatement.Build(
            Chart(), balances, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        decimal sheetNetIncome = sheet.Equity.Lines.Single(l => l.AccountId is null).Amount;
        Assert.Equal(statement.NetIncome, sheetNetIncome);
    }

    [Fact]
    public void After_close_the_temporaries_are_zero_so_no_net_income_line_appears()
    {
        // Post-year-end: revenue/expense swept into retained earnings, which now carries the 200.
        Dictionary<Guid, decimal> closed = new()
        {
            [Cash] = 1000m,
            [AccountsPayable] = -400m,
            [CommonStock] = -400m,
            [RetainedEarnings] = -200m,
        };

        BalanceSheet sheet = BalanceSheet.Build(Chart(), closed, new DateOnly(2026, 12, 31));

        Assert.DoesNotContain(sheet.Equity.Lines, l => l.AccountId is null);
        Assert.Equal(600m, sheet.Equity.Total); // common stock 400 + retained earnings 200
        Assert.True(sheet.IsBalanced);
    }
}
