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
    public const string BillDimension = "Bill";

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
            Dimensions: new Dictionary<string, Guid>
            {
                [VendorDimension] = bill.VendorId,
                [BillDimension] = bill.Id,
            }));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(BillSourceType, bill.Id), EffectiveDate: bill.BillDate, Reference: bill.Number, Memo: bill.Memo,
            Lines: lines, SourceRef: bill.Id, SourceType: BillSourceType);
    }

    public static PostEntryRequest ComposeBillPayment(
        Guid paymentId, BillPaymentBody body, IReadOnlyList<Allocation> allocations, BillPaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal allocated = allocations.Sum(a => a.Amount);
        decimal remainder = body.Amount - allocated;

        List<PostLineRequest> lines = [];
        foreach (Allocation a in allocations)
        {
            if (a.Amount == 0m) continue;
            lines.Add(new(accounts.PayableAccountId, "Debit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [VendorDimension] = body.VendorId,
                    [BillDimension] = a.TargetId,
                }));
        }
        if (remainder != 0m)
            lines.Add(new(accounts.VendorCreditsAccountId, "Debit", remainder,
                Dimensions: new Dictionary<string, Guid> { [VendorDimension] = body.VendorId }));
        lines.Add(new(accounts.CashAccountId, "Credit", body.Amount));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(BillPaymentSourceType, paymentId), EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: paymentId, SourceType: BillPaymentSourceType);
    }

    public static PostEntryRequest ComposeVendorCreditApplication(
        Guid id, VendorCreditApplicationBody body, IReadOnlyList<Allocation> allocations, BillPaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal applied = allocations.Sum(a => a.Amount);

        List<PostLineRequest> lines = [];
        foreach (Allocation a in allocations)
        {
            if (a.Amount == 0m) continue;
            lines.Add(new(accounts.PayableAccountId, "Debit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [VendorDimension] = body.VendorId,
                    [BillDimension] = a.TargetId,
                }));
        }
        lines.Add(new(accounts.VendorCreditsAccountId, "Credit", applied,
            Dimensions: new Dictionary<string, Guid> { [VendorDimension] = body.VendorId }));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(VendorCreditApplicationSourceType, id), EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: id, SourceType: VendorCreditApplicationSourceType);
    }
}
