using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Reporting;

namespace Accounting101.Ledger.Mongo.Reporting;

/// <summary>
/// Derives a client's financial statements by fetching the inputs the pure Core arrangers need and
/// handing them over: debit-positive balances as of a date (for the balance sheet) or activity over a
/// window (for the income statement), plus the chart of accounts. No arrangement, classification, or
/// presentation logic lives here — that is Core's. This layer is the I/O seam, nothing more.
/// </summary>
public sealed class FinancialStatementService(MongoJournalStore journal, MongoAccountStore accounts)
{
    public async Task<BalanceSheet> BalanceSheetAsync(
        Guid clientId, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<Guid, decimal> balances =
            await journal.AggregateBalancesAsync(clientId, asOf, cancellationToken);
        ChartOfAccounts chart = await accounts.GetChartAsync(clientId, cancellationToken);
        return BalanceSheet.Build(chart, balances, asOf);
    }

    public async Task<IncomeStatement> IncomeStatementAsync(
        Guid clientId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<Guid, decimal> activity =
            await journal.AggregateActivityAsync(clientId, from, to, cancellationToken);
        ChartOfAccounts chart = await accounts.GetChartAsync(clientId, cancellationToken);
        return IncomeStatement.Build(chart, activity, from, to);
    }
}
