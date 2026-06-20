using Accounting101.Ledger.Core.Journal;

namespace Accounting101.Ledger.Core.Accounts;

/// <summary>The five fundamental account classifications. Drives normal balance and statement placement.</summary>
public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense,
}

/// <summary>
/// Which statement-of-cash-flows activity an account's movements represent. <see cref="Cash"/> marks the
/// cash pool the statement explains (not bucketed); the other three are the activity sections. Account
/// type cannot decide this — A/R and equipment are both assets but one is operating, the other investing —
/// so it is classified on the account, defaulting from type where the common case is unambiguous.
/// </summary>
public enum CashFlowActivity
{
    Cash,
    Operating,
    Investing,
    Financing,
}

public static class AccountTypeExtensions
{
    extension(AccountType type)
    {
        /// <summary>The side that increases the account: Asset/Expense are debit-normal, the rest credit-normal.</summary>
        public Direction NormalSide() =>
            type is AccountType.Asset or AccountType.Expense ? Direction.Debit : Direction.Credit;

        /// <summary>Revenue and Expense are temporary — reset into retained earnings at year-end.</summary>
        public bool IsTemporary() =>
            type is AccountType.Revenue or AccountType.Expense;

        /// <summary>Asset, Liability, and Equity carry forward across fiscal years.</summary>
        public bool IsPermanent() => !type.IsTemporary();

        /// <summary>
        /// The cash-flow bucket to assume when an account is not explicitly classified. Equity (stock,
        /// dividends through retained earnings) defaults to Financing; everything else — the temporaries
        /// that net to operating income, plus the typical current asset/liability — defaults to Operating.
        /// The exceptions that this gets wrong (fixed assets → Investing, loans → Financing) and the cash
        /// accounts themselves are tagged on the account. A wrong default only mis-buckets a line; it never
        /// unbalances the statement, since the buckets sum to the cash change by double entry regardless.
        /// </summary>
        public CashFlowActivity DefaultCashFlowActivity() =>
            type is AccountType.Equity ? CashFlowActivity.Financing : CashFlowActivity.Operating;
    }
}
