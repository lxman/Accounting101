namespace Accounting101.Payables;

/// <summary>Assembles the read-only <see cref="VendorAccountView"/> for one vendor by reading its
/// documents and folding them with <see cref="VendorAccountBuilder"/>. Read-only; computes, never stores.</summary>
public sealed class VendorAccountService(IVendorStore vendors, IBillStore bills, IBillPaymentStore payments)
{
    public async Task<VendorAccountView?> GetAccountAsync(
        Guid clientId, Guid vendorId, DateOnly asOf, CancellationToken ct = default)
    {
        Vendor? vendor = await vendors.GetAsync(clientId, vendorId, ct);
        if (vendor is null) return null;

        IReadOnlyList<Bill> vendorBills = await bills.GetByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<VendorCreditApplication> cs = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);

        Dictionary<Guid, decimal> applied = VendorAccountBuilder.AppliedByBill(ps, cs);
        IReadOnlyList<OpenBillLine> open = VendorAccountBuilder.OpenBills(vendorBills, applied, asOf);
        decimal credit = ps.Where(p => !p.Voided).Sum(p => p.Unapplied)
                         - cs.Where(c => !c.Voided).Sum(c => c.Applied);

        return new VendorAccountView(
            vendor,
            VendorAccountBuilder.ApBalance(open),
            credit,
            VendorAccountBuilder.Aging(open),
            open,
            VendorAccountBuilder.Statement(vendorBills, ps, cs),
            VendorAccountBuilder.CreditActivity(ps, cs));
    }
}
