using Accounting101.Ledger.Core.Accounts;

namespace Accounting101.Ledger.Core.Reporting;

/// <summary>
/// The balance sheet as of a point in time: assets, liabilities, and equity, each presented on its
/// natural side. Current-period earnings — the still-open temporary accounts — are folded into equity
/// as a single synthesized "Net income" line, so the statement articulates with the income statement
/// and balances even before a year-end close has swept the temporaries into retained earnings.
/// Pure: a deterministic function of the chart and a set of balances, with no I/O.
/// </summary>
public sealed record BalanceSheet
{
    public required DateOnly AsOf { get; init; }
    public required StatementSection Assets { get; init; }
    public required StatementSection Liabilities { get; init; }
    public required StatementSection Equity { get; init; }

    public decimal TotalAssets => Assets.Total;
    public decimal TotalLiabilitiesAndEquity => Liabilities.Total + Equity.Total;

    /// <summary>Assets = Liabilities + Equity — holds for any books derived from a balanced journal.</summary>
    public bool IsBalanced => TotalAssets == TotalLiabilitiesAndEquity;

    /// <summary>
    /// Arrange a chart and its debit-positive balances (as of <paramref name="asOf"/>) into a balance
    /// sheet. <paramref name="balances"/> must cover every account that has activity, temporary ones
    /// included — their net is presented as the equity "Net income" line, never as their own section.
    /// </summary>
    public static BalanceSheet Build(
        ChartOfAccounts chart, IReadOnlyDictionary<Guid, decimal> balances, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(balances);

        StatementSection equity = StatementPresentation.Section("Equity", chart, AccountType.Equity, balances);

        decimal netIncome = StatementPresentation.NetIncome(chart, balances);
        if (netIncome != 0m)
            equity = equity with { Lines = [.. equity.Lines, new StatementLine { Name = "Net income", Amount = netIncome }] };

        return new BalanceSheet
        {
            AsOf = asOf,
            Assets = StatementPresentation.Section("Assets", chart, AccountType.Asset, balances),
            Liabilities = StatementPresentation.Section("Liabilities", chart, AccountType.Liability, balances),
            Equity = equity,
        };
    }
}
