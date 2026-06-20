using Accounting101.Ledger.Core.Accounts;

namespace Accounting101.Ledger.Core.Reporting;

/// <summary>
/// The income statement for a period: revenue less expenses, each presented on its natural side, and
/// the resulting net income. A flow over a window — built from the temporary accounts' activity within
/// [<see cref="From"/>, <see cref="To"/>], not a balance at an instant. Pure, like the balance sheet.
/// </summary>
public sealed record IncomeStatement
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public required StatementSection Revenue { get; init; }
    public required StatementSection Expenses { get; init; }

    /// <summary>Net income = Revenue − Expenses (a positive figure is a profit).</summary>
    public decimal NetIncome => Revenue.Total - Expenses.Total;

    /// <summary>
    /// Arrange a chart and the accounts' debit-positive activity over
    /// [<paramref name="from"/>, <paramref name="to"/>] into an income statement. Only the temporary
    /// (revenue/expense) accounts are read; any permanent-account activity in the map is ignored.
    /// </summary>
    public static IncomeStatement Build(
        ChartOfAccounts chart, IReadOnlyDictionary<Guid, decimal> activity, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(activity);

        return new IncomeStatement
        {
            From = from,
            To = to,
            Revenue = StatementPresentation.Section("Revenue", chart, AccountType.Revenue, activity),
            Expenses = StatementPresentation.Section("Expenses", chart, AccountType.Expense, activity),
        };
    }
}
