namespace Accounting101.Banking.Cash;

/// <summary>A single line on a cash voucher: which account is affected and by how much.</summary>
public record CashLine(Guid AccountId, decimal Amount);
