using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>A bill plus its derived settlement facet — what a read endpoint returns.</summary>
public sealed record BillView(Bill Bill, decimal OpenBalance, SettlementStatus SettlementStatus);
