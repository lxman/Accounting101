namespace Accounting101.Banking.Cash;

/// <summary>A read model for a cash deposit — the document plus any computed display fields.</summary>
public sealed record CashDepositView(CashDeposit Deposit);
