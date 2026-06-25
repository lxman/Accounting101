namespace Accounting101.Settlement;

/// <summary>Pure settlement math: a document's open balance and status given the total applied to it.</summary>
public static class Settlement
{
    public static decimal OpenBalance(decimal total, decimal applied) => total - applied;

    public static SettlementStatus Status(decimal total, decimal applied) =>
        applied <= 0m ? SettlementStatus.Open
        : applied >= total ? SettlementStatus.Paid
        : SettlementStatus.PartiallyPaid;
}
