namespace Accounting101.Banking.Cash;

/// <summary>A read model for a cash disbursement — the document plus any computed display fields.</summary>
public sealed record CashDisbursementView(CashDisbursement Disbursement);
