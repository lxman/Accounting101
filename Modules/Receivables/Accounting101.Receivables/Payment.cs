namespace Accounting101.Receivables;

/// <summary>A recorded customer payment. Voided is derived from the document lifecycle. The per-invoice
/// split is no longer stored here — it lives only as ledger dimensions on the payment's entry; callers that
/// need the applied/unapplied amount fold it from that entry (see <see cref="SettlementRelief"/>).</summary>
public sealed record Payment
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public string? Method { get; init; }
    public bool Voided { get; init; }
}

/// <summary>An application of existing customer credit to invoices. The per-invoice split is no longer
/// stored here — it lives only as ledger dimensions on this document's entry.</summary>
public sealed record CreditApplication
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public bool Voided { get; init; }
}
