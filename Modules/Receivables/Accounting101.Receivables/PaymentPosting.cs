using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>The cash-application recipes: a payment or credit application composes into one balanced
/// journal entry. Pure — request in, wire DTO out — leaving sequencing, approval, and persistence to the engine.</summary>
public static class PaymentPosting
{
    public const string PaymentSourceType = "Payment";
    public const string CreditApplicationSourceType = "CreditApplication";
    public const string WriteOffSourceType = "WriteOff";
    public const string CreditNoteSourceType = "CreditNote";
    public const string RefundSourceType = "Refund";
    public const string CustomerDimension = "Customer";

    /// <summary>The per-invoice dimension the recipe tags on each A/R credit line (additive; not yet required).</summary>
    private const string InvoiceDimension = "Invoice";

    public static PostEntryRequest ComposePayment(
        Guid paymentId, PaymentBody body, IReadOnlyList<Allocation> allocations, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal allocated = allocations.Sum(a => a.Amount);
        decimal remainder = body.Amount - allocated;
        Dictionary<string, Guid> dim = new() { [CustomerDimension] = body.CustomerId };

        List<PostLineRequest> lines = [new(accounts.CashAccountId, "Debit", body.Amount)];
        foreach (Allocation a in allocations)
        {
            if (a.Amount == 0m)
                continue;
            lines.Add(new(accounts.ReceivableAccountId, "Credit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [CustomerDimension] = body.CustomerId,
                    [InvoiceDimension] = a.TargetId,
                }));
        }
        if (remainder != 0m)
            lines.Add(new(accounts.CustomerCreditsAccountId, "Credit", remainder, Dimensions: dim));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(PaymentSourceType, paymentId), EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: paymentId, SourceType: PaymentSourceType);
    }

    public static PostEntryRequest ComposeCreditApplication(
        Guid id, CreditApplicationBody body, IReadOnlyList<Allocation> allocations, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal applied = allocations.Sum(a => a.Amount);
        Dictionary<string, Guid> dim = new() { [CustomerDimension] = body.CustomerId };

        List<PostLineRequest> lines = [new(accounts.CustomerCreditsAccountId, "Debit", applied, Dimensions: dim)];
        foreach (Allocation a in allocations)
        {
            if (a.Amount == 0m)
                continue;
            lines.Add(new(accounts.ReceivableAccountId, "Credit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [CustomerDimension] = body.CustomerId,
                    [InvoiceDimension] = a.TargetId,
                }));
        }

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(CreditApplicationSourceType, id), EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: id, SourceType: CreditApplicationSourceType);
    }

    public static PostEntryRequest ComposeWriteOff(
        Guid writeOffId, WriteOffBody body, IReadOnlyList<Allocation> allocations, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(accounts);
        decimal allocated = allocations.Sum(a => a.Amount);
        List<PostLineRequest> lines = [new(accounts.BadDebtExpenseAccountId, "Debit", allocated)];
        foreach (Allocation a in allocations)
        {
            if (a.Amount == 0m)
                continue;
            lines.Add(new(accounts.ReceivableAccountId, "Credit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [CustomerDimension] = body.CustomerId,
                    [InvoiceDimension] = a.TargetId,
                }));
        }
        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(WriteOffSourceType, writeOffId), EffectiveDate: body.Date,
            Reference: null, Memo: body.Memo, Lines: lines, SourceRef: writeOffId, SourceType: WriteOffSourceType);
    }

    public static PostEntryRequest ComposeCreditNote(
        Guid creditNoteId, CreditNoteBody body, IReadOnlyList<Allocation> allocations, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(accounts);
        decimal allocated = allocations.Sum(a => a.Amount);
        List<PostLineRequest> lines = [new(accounts.SalesReturnsAccountId, "Debit", allocated)];
        foreach (Allocation a in allocations)
        {
            if (a.Amount == 0m)
                continue;
            lines.Add(new(accounts.ReceivableAccountId, "Credit", a.Amount,
                Dimensions: new Dictionary<string, Guid>
                {
                    [CustomerDimension] = body.CustomerId,
                    [InvoiceDimension] = a.TargetId,
                }));
        }
        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(CreditNoteSourceType, creditNoteId), EffectiveDate: body.Date,
            Reference: null, Memo: body.Memo, Lines: lines, SourceRef: creditNoteId, SourceType: CreditNoteSourceType);
    }

    public static PostEntryRequest ComposeRefund(Guid refundId, RefundBody body, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);
        Dictionary<string, Guid> dim = new() { [CustomerDimension] = body.CustomerId };
        List<PostLineRequest> lines =
        [
            new(accounts.CustomerCreditsAccountId, "Debit", body.Amount, Dimensions: dim),
            new(accounts.CashAccountId, "Credit", body.Amount),
        ];
        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(RefundSourceType, refundId), EffectiveDate: body.Date,
            Reference: null, Memo: body.Memo, Lines: lines, SourceRef: refundId, SourceType: RefundSourceType);
    }
}
