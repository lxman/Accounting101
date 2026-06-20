using Accounting101.Ledger.Core.Accounts;

namespace Accounting101.Ledger.Core.Reporting;

/// <summary>
/// The statement of cash flows for a period, by the indirect method. It rests on one double-entry
/// identity: every entry balances, so the change in cash equals the negative of the change in every
/// other account. Each non-cash account's sign-flipped period movement is therefore a cash-flow
/// contribution, sorted into operating / investing / financing by the account's
/// <see cref="Account.CashFlowActivity"/>; the temporaries collapse into the operating lead line
/// (<see cref="NetIncome"/>); and the three sections sum to the actual cash movement by construction —
/// a mis-classified account only lands in the wrong section, it never unbalances the statement. Pure.
/// </summary>
public sealed record CashFlowStatement
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }

    /// <summary>The income-statement net income for the period — the operating section's lead line.</summary>
    public required decimal NetIncome { get; init; }

    /// <summary>Operating working-capital and non-cash adjustments (e.g. −increase in A/R, +depreciation).</summary>
    public required StatementSection OperatingAdjustments { get; init; }
    public required StatementSection Investing { get; init; }
    public required StatementSection Financing { get; init; }

    public required decimal BeginningCash { get; init; }

    /// <summary>The actual cash movement over the period (Σ of the cash accounts' activity) — the figure
    /// the three sections must explain.</summary>
    public required decimal CashMovement { get; init; }

    public decimal OperatingCash => NetIncome + OperatingAdjustments.Total;

    /// <summary>The cash change explained by the three sections.</summary>
    public decimal NetChangeInCash => OperatingCash + Investing.Total + Financing.Total;

    /// <summary>End-of-period cash, taken from the authoritative cash movement so it always reconciles to
    /// the period-end cash balance, whatever the activity classification.</summary>
    public decimal EndingCash => BeginningCash + CashMovement;

    /// <summary>The explanation reconciles to the direct cash delta. Holds by double entry; a false value
    /// signals a derivation bug, not bad data.</summary>
    public bool TiesOut => NetChangeInCash == CashMovement;

    /// <summary>
    /// Arrange a chart, the period's debit-positive activity per account, and the period-opening balances
    /// into a cash-flow statement. <paramref name="activity"/> is the movement over [<paramref name="from"/>,
    /// <paramref name="to"/>] with year-end closing entries excluded (they are cash-neutral mechanics);
    /// <paramref name="beginningBalances"/> are the balances as of the day before the period, for opening cash.
    /// </summary>
    public static CashFlowStatement Build(
        ChartOfAccounts chart,
        IReadOnlyDictionary<Guid, decimal> activity,
        IReadOnlyDictionary<Guid, decimal> beginningBalances,
        DateOnly from,
        DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(beginningBalances);

        List<StatementLine> operating = [];
        List<StatementLine> investing = [];
        List<StatementLine> financing = [];
        decimal cashMovement = 0m;

        foreach (Account account in chart.Accounts
                     .Where(account => account.Postable)
                     .OrderBy(account => account.Number, StringComparer.Ordinal))
        {
            CashFlowActivity bucket = account.CashFlowActivity ?? account.Type.DefaultCashFlowActivity();
            if (bucket == CashFlowActivity.Cash)
            {
                cashMovement += activity.GetValueOrDefault(account.Id);
                continue;
            }

            // Temporaries are captured wholesale by net income; never list them as adjustments.
            if (account.IsTemporary)
                continue;

            // A non-cash account contributes its sign-flipped movement; an account that didn't move adds nothing.
            decimal contribution = -activity.GetValueOrDefault(account.Id);
            if (contribution == 0m)
                continue;

            StatementLine line = new()
            {
                AccountId = account.Id,
                Number = account.Number,
                Name = account.Name,
                Amount = contribution,
            };

            (bucket switch
            {
                CashFlowActivity.Investing => investing,
                CashFlowActivity.Financing => financing,
                _ => operating,
            }).Add(line);
        }

        decimal beginningCash = chart.Accounts
            .Where(account => (account.CashFlowActivity ?? account.Type.DefaultCashFlowActivity()) == CashFlowActivity.Cash)
            .Sum(account => beginningBalances.GetValueOrDefault(account.Id));

        return new CashFlowStatement
        {
            From = from,
            To = to,
            NetIncome = StatementPresentation.NetIncome(chart, activity),
            OperatingAdjustments = new StatementSection { Title = "Operating adjustments", Lines = operating },
            Investing = new StatementSection { Title = "Investing", Lines = investing },
            Financing = new StatementSection { Title = "Financing", Lines = financing },
            BeginningCash = beginningCash,
            CashMovement = cashMovement,
        };
    }
}
