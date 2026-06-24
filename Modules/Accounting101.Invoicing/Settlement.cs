namespace Accounting101.Invoicing;

/// <summary>Pure settlement math: an invoice's open balance and status given the total applied to it.</summary>
public static class Settlement
{
    public static decimal OpenBalance(decimal invoiceTotal, decimal applied) => invoiceTotal - applied;

    public static SettlementStatus Status(decimal invoiceTotal, decimal applied) =>
        applied <= 0m ? SettlementStatus.Open
        : applied >= invoiceTotal ? SettlementStatus.Paid
        : SettlementStatus.PartiallyPaid;
}
