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

/// <summary>A subledger dimension a control account requires when posting.</summary>
public enum DimensionKind
{
    Customer,
    Vendor,
    Item,
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
    }
}
