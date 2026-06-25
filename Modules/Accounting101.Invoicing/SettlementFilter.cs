namespace Accounting101.Invoicing;

/// <summary>Filter for listing invoices by settlement: Open = any unpaid balance (Open or PartiallyPaid);
/// Paid = fully settled.</summary>
public enum SettlementFilter { Open, Paid }
