namespace Accounting101.Payables;

/// <summary>Chart accounts the payment recipes post to.</summary>
public sealed record BillPaymentPostingAccounts
{
    /// <summary>Accounts Payable — debited as allocations settle bills (Vendor dim).</summary>
    public required Guid PayableAccountId { get; init; }

    /// <summary>Cash — credited for the full payment amount.</summary>
    public required Guid CashAccountId { get; init; }

    /// <summary>Vendor Credits — a Vendor-dimensioned ASSET control account holding over-payment (a
    /// prepayment the vendor owes back).</summary>
    public required Guid VendorCreditsAccountId { get; init; }
}
