namespace Accounting101.Receivables;

/// <summary>Chart accounts the payment recipes post to. Kept separate from InvoicePostingAccounts because
/// the invoice recipe has no Cash account; the two share Receivable by configuration, not by type.</summary>
public sealed record PaymentPostingAccounts
{
    /// <summary>Accounts Receivable — the control account credited as allocations settle invoices (Customer dim).</summary>
    public required Guid ReceivableAccountId { get; init; }

    /// <summary>Cash — debited for the full payment amount.</summary>
    public required Guid CashAccountId { get; init; }

    /// <summary>Customer Credits — a liability control account (Customer dim) holding unapplied over-payment.</summary>
    public required Guid CustomerCreditsAccountId { get; init; }
}
