using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

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
            Id: EntryIdentity.ForSource(BillSourceType, bill.Id), EffectiveDate: bill.BillDate, Reference: bill.Number, Memo: bill.Memo,
            Lines: lines, SourceRef: bill.Id, SourceType: BillSourceType);
    }

    public static PostEntryRequest ComposeBillPayment(Guid paymentId, BillPaymentBody body, BillPaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal allocated = body.Allocations.Sum(a => a.Amount);
        decimal remainder = body.Amount - allocated;
        Dictionary<string, Guid> dim = new() { [VendorDimension] = body.VendorId };

        List<PostLineRequest> lines = [];
        if (allocated != 0m)
            lines.Add(new(accounts.PayableAccountId, "Debit", allocated, Dimensions: dim));
        if (remainder != 0m)
            lines.Add(new(accounts.VendorCreditsAccountId, "Debit", remainder, Dimensions: dim));
        lines.Add(new(accounts.CashAccountId, "Credit", body.Amount));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(BillPaymentSourceType, paymentId), EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: paymentId, SourceType: BillPaymentSourceType);
    }

    public static PostEntryRequest ComposeVendorCreditApplication(Guid id, VendorCreditApplicationBody body, BillPaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal applied = body.Allocations.Sum(a => a.Amount);
        Dictionary<string, Guid> dim = new() { [VendorDimension] = body.VendorId };

        List<PostLineRequest> lines =
        [
            new(accounts.PayableAccountId, "Debit", applied, Dimensions: dim),
            new(accounts.VendorCreditsAccountId, "Credit", applied, Dimensions: dim),
        ];

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(VendorCreditApplicationSourceType, id), EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: id, SourceType: VendorCreditApplicationSourceType);
    }
}
