using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Reporting;

namespace Accounting101.Ledger.Core.Tests;

/// <summary>
/// The cash-flow arranger by the indirect method: net income leads, working-capital and non-cash items
/// adjust, investing/financing bucket by activity, and the three sections sum to the actual cash movement.
/// The defining property — a misclassified account mis-buckets a line but never breaks the tie-out — is
/// asserted directly, because it is the whole reason the classification is safe to carry on the account.
/// </summary>
public class CashFlowStatementTests
{
    private static readonly Guid Client = Guid.NewGuid();

    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid AccountsReceivable = Guid.NewGuid();
    private static readonly Guid Equipment = Guid.NewGuid();
    private static readonly Guid AccumulatedDepreciation = Guid.NewGuid();
    private static readonly Guid LoanPayable = Guid.NewGuid();
    private static readonly Guid CommonStock = Guid.NewGuid();
    private static readonly Guid Sales = Guid.NewGuid();
    private static readonly Guid Wages = Guid.NewGuid();
    private static readonly Guid Depreciation = Guid.NewGuid();

    private static Account Acct(
        Guid id, string number, string name, AccountType type, CashFlowActivity? activity = null) => new()
    {
        Id = id,
        ClientId = Client,
        Number = number,
        Name = name,
        Type = type,
        CashFlowActivity = activity,
    };

    // Cash and the two exceptions (equipment → investing, loan → financing) are tagged; everything else
    // takes its type default (current asset/liability + temporaries → operating, equity → financing).
    private static List<Account> Accounts(CashFlowActivity equipmentActivity = CashFlowActivity.Investing) =>
    [
        Acct(Cash, "1000", "Cash", AccountType.Asset, CashFlowActivity.Cash),
        Acct(AccountsReceivable, "1100", "Accounts Receivable", AccountType.Asset),
        Acct(Equipment, "1500", "Equipment", AccountType.Asset, equipmentActivity),
        Acct(AccumulatedDepreciation, "1600", "Accumulated Depreciation", AccountType.Asset),
        Acct(LoanPayable, "2500", "Loan Payable", AccountType.Liability, CashFlowActivity.Financing),
        Acct(CommonStock, "3000", "Common Stock", AccountType.Equity),
        Acct(Sales, "4000", "Sales", AccountType.Revenue),
        Acct(Wages, "5000", "Wages", AccountType.Expense),
        Acct(Depreciation, "5100", "Depreciation Expense", AccountType.Expense),
    ];

    // Period activity (debit-positive movement), nets to zero across all accounts: 1000 credit sales,
    // 700 collected, 300 wages paid, 100 depreciation, 500 equipment bought, 400 borrowed, 200 stock issued.
    private static Dictionary<Guid, decimal> Activity() => new()
    {
        [Cash] = 500m,                     // +700 −300 −500 +400 +200
        [AccountsReceivable] = 300m,       // +1000 billed −700 collected
        [Equipment] = 500m,
        [AccumulatedDepreciation] = -100m,
        [LoanPayable] = -400m,
        [CommonStock] = -200m,
        [Sales] = -1000m,
        [Wages] = 300m,
        [Depreciation] = 100m,
    };

    private static readonly Dictionary<Guid, decimal> BeginningCash = new() { [Cash] = 1000m };

    private static CashFlowStatement Build(CashFlowActivity equipmentActivity = CashFlowActivity.Investing) =>
        CashFlowStatement.Build(
            new ChartOfAccounts(Accounts(equipmentActivity)), Activity(), BeginningCash,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

    [Fact]
    public void Ties_out_and_reconciles_to_the_cash_movement()
    {
        CashFlowStatement cf = Build();

        Assert.Equal(600m, cf.NetIncome);          // 1000 sales − 300 wages − 100 depreciation
        Assert.Equal(400m, cf.OperatingCash);      // 600 − 300 ΔAR + 100 depreciation
        Assert.Equal(-500m, cf.Investing.Total);   // equipment purchase
        Assert.Equal(600m, cf.Financing.Total);    // 400 loan + 200 stock

        Assert.Equal(500m, cf.NetChangeInCash);
        Assert.Equal(500m, cf.CashMovement);
        Assert.True(cf.TiesOut);

        Assert.Equal(1000m, cf.BeginningCash);
        Assert.Equal(1500m, cf.EndingCash);
    }

    [Fact]
    public void Depreciation_is_added_back_and_an_AR_increase_is_a_use_of_cash()
    {
        CashFlowStatement cf = Build();

        Assert.Equal(100m, cf.OperatingAdjustments.Lines.Single(l => l.AccountId == AccumulatedDepreciation).Amount);
        Assert.Equal(-300m, cf.OperatingAdjustments.Lines.Single(l => l.AccountId == AccountsReceivable).Amount);

        // Temporaries collapse into net income — they are never listed as their own adjustment lines.
        Assert.DoesNotContain(cf.OperatingAdjustments.Lines, l => l.AccountId == Sales);
        Assert.DoesNotContain(cf.OperatingAdjustments.Lines, l => l.AccountId == Depreciation);
    }

    [Fact]
    public void Investing_and_financing_are_bucketed_by_activity()
    {
        CashFlowStatement cf = Build();

        Assert.Equal(-500m, cf.Investing.Lines.Single(l => l.AccountId == Equipment).Amount);
        Assert.Equal(400m, cf.Financing.Lines.Single(l => l.AccountId == LoanPayable).Amount);
        Assert.Equal(200m, cf.Financing.Lines.Single(l => l.AccountId == CommonStock).Amount);
    }

    [Fact]
    public void A_misclassified_account_still_ties_out()
    {
        // Equipment wrongly tagged Operating instead of Investing: the purchase lands in the wrong section,
        // yet the statement still ties to the penny, because the sections sum to the cash movement by
        // double entry regardless of how each line is routed.
        CashFlowStatement cf = Build(equipmentActivity: CashFlowActivity.Operating);

        Assert.Empty(cf.Investing.Lines);
        Assert.Contains(cf.OperatingAdjustments.Lines, l => l.AccountId == Equipment);
        Assert.Equal(500m, cf.NetChangeInCash);
        Assert.True(cf.TiesOut);
    }
}
