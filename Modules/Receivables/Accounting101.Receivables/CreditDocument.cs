namespace Accounting101.Receivables;

/// <summary>A unified read view of a customer's allocation-based dispositions — credit note, write-off,
/// or credit application — for the Credits list. Amount is the document's AR relief, folded from its
/// ledger entry (see <see cref="SettlementRelief"/>) since the module stores no allocation array; Memo is
/// null for credit applications (which carry none).</summary>
public sealed record CreditDocument(
    string Type,            // "credit-note" | "write-off" | "credit-application"
    Guid Id, Guid CustomerId, DateOnly Date,
    decimal Amount, string? Memo, bool Voided);
