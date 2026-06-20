using Accounting101.Ledger.Core.Accounts;
using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Core.Reporting;

/// <summary>
/// One line on a financial statement: an account's balance presented on its natural side
/// (a positive amount means the normal-balance direction), or a synthesized total that belongs to no
/// single account — net income on the balance sheet — in which case <see cref="AccountId"/> is null.
/// </summary>
public sealed record StatementLine
{
    public Guid? AccountId { get; init; }
    public string? Number { get; init; }
    public required string Name { get; init; }
    public required decimal Amount { get; init; }
}

/// <summary>A titled group of lines and their total — one section of a statement (e.g. Assets).</summary>
public sealed record StatementSection
{
    public required string Title { get; init; }
    public required IReadOnlyList<StatementLine> Lines { get; init; }
    public decimal Total => Lines.Sum(line => line.Amount);
}

/// <summary>
/// Shared arrangement helpers: turn debit-positive balances + the chart into presentable sections.
/// Presentation here means accounting-natural sign only (positive = the account's normal side); display
/// formatting — locale, parentheses for negatives, currency symbols — is the edge's concern, not Core's.
/// </summary>
internal static class StatementPresentation
{
    /// <summary>Re-sign a debit-positive balance onto the account's natural side (credit-normal types flip).</summary>
    public static decimal Present(AccountType type, decimal debitPositive) =>
        type.NormalSide() == Direction.Debit ? debitPositive : -debitPositive;

    /// <summary>
    /// Net income over a set of debit-positive figures: −Σ over the temporary accounts. A profitable
    /// (credit-heavy) book yields a positive number. Shared by the balance sheet (folded into equity) and
    /// the cash-flow statement (the operating lead line), so both report the identical figure.
    /// </summary>
    public static decimal NetIncome(ChartOfAccounts chart, IReadOnlyDictionary<Guid, decimal> figures) =>
        -chart.Accounts
            .Where(account => account.IsTemporary && account.Postable)
            .Sum(account => figures.GetValueOrDefault(account.Id));

    /// <summary>
    /// A section of every postable account of one type, each shown on its natural side and ordered by
    /// account number. Accounts with no activity appear at zero, so the section mirrors the chart.
    /// </summary>
    public static StatementSection Section(
        string title, ChartOfAccounts chart, AccountType type, IReadOnlyDictionary<Guid, decimal> balances) =>
        new()
        {
            Title = title,
            Lines = chart.Accounts
                .Where(account => account.Type == type && account.Postable)
                .OrderBy(account => account.Number, StringComparer.Ordinal)
                .Select(account => new StatementLine
                {
                    AccountId = account.Id,
                    Number = account.Number,
                    Name = account.Name,
                    Amount = Present(type, balances.GetValueOrDefault(account.Id)),
                })
                .ToList(),
        };
}
