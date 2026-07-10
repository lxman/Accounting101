namespace Accounting101.Payables;

/// <summary>A recorded payment to a vendor. Voided is derived from the document lifecycle. The per-bill
/// split is no longer stored here — it lives only as ledger dimensions on the payment's entry; callers that
/// need the applied/unapplied amount fold it from that entry (see <see cref="SettlementRelief"/>).</summary>
public sealed record BillPayment
{
    public required Guid Id { get; init; }
    public required Guid VendorId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public string? Method { get; init; }
    public bool Voided { get; init; }
}

/// <summary>An application of existing vendor credit to bills. The per-bill split is no longer stored
/// here — it lives only as ledger dimensions on this document's entry.</summary>
public sealed record VendorCreditApplication
{
    public required Guid Id { get; init; }
    public required Guid VendorId { get; init; }
    public required DateOnly Date { get; init; }
    public bool Voided { get; init; }
}
