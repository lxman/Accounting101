using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>The payables recipes: a bill, a bill payment, or a vendor-credit application each composes into
/// one balanced journal entry. Pure — request in, wire DTO out — leaving sequencing, approval, and
/// persistence to the engine.</summary>
public static class BillPosting
{
    public const string BillSourceType = "Bill";
    public const string BillPaymentSourceType = "BillPayment";
    public const string VendorCreditApplicationSourceType = "VendorCreditApplication";
    public const string VendorDimension = "Vendor";

    public static PostEntryRequest ComposeBill(Bill bill, BillPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(accounts);

        // Debit each line's expense account; lines sharing an account collapse to one debit, ordered by
        // account id for determinism. Credit A/P for the total, tagged by vendor.
        List<PostLineRequest> lines = bill.Lines
            .GroupBy(line => line.ExpenseAccountId)
            .OrderBy(group => group.Key)
            .Select(group => new PostLineRequest(group.Key, "Debit", group.Sum(line => line.Amount)))
            .ToList();

        lines.Add(new(accounts.PayableAccountId, "Credit", bill.Total,
            Dimensions: new Dictionary<string, Guid> { [VendorDimension] = bill.VendorId }));

        return new PostEntryRequest(
            Id: null, EffectiveDate: bill.BillDate, Reference: bill.Number, Memo: bill.Memo,
            Lines: lines, SourceRef: bill.Id, SourceType: BillSourceType);
    }
}
