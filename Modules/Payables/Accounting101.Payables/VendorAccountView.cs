namespace Accounting101.Payables;

/// <summary>The read-only 360 view for one vendor — balances plus the four ledgers.</summary>
public sealed record VendorAccountView(
    Vendor Vendor, decimal ApBalance, decimal CreditBalance, AgingBuckets Aging,
    IReadOnlyList<OpenBillLine> OpenBills, IReadOnlyList<StatementLine> StatementLines,
    IReadOnlyList<CreditActivityLine> CreditLines);

public sealed record AgingBuckets(decimal Current, decimal D1To30, decimal D31To60, decimal D61To90, decimal D90Plus);

public sealed record OpenBillLine(Guid BillId, string? Number, DateOnly BillDate, DateOnly? DueDate, decimal OpenBalance, int DaysOverdue);

public sealed record StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance);

public sealed record CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance);
