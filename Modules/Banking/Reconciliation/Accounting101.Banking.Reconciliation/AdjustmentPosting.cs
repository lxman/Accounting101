using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>The bank-adjustment recipe: composes one balanced journal entry. Charge (a fee) debits the
/// offset account and credits cash; Credit (interest) debits cash and credits the offset. Pure.</summary>
public static class AdjustmentPosting
{
    public const string SourceType = "BankAdjustment";

    public static PostEntryRequest Compose(Guid adjustmentId, BankAdjustmentBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Amount <= 0m)
            throw new ArgumentException($"Adjustment amount must be positive; got {body.Amount}.", nameof(body));
        if (body.OffsetAccountId == body.CashAccountId)
            throw new ArgumentException("The offset account must differ from the cash account.", nameof(body));

        (Guid debit, Guid credit) = body.Kind == AdjustmentKind.Charge
            ? (body.OffsetAccountId, body.CashAccountId)   // fee: Dr offset / Cr cash
            : (body.CashAccountId, body.OffsetAccountId);  // interest: Dr cash / Cr offset

        List<PostLineRequest> lines =
        [
            new PostLineRequest(debit, "Debit", body.Amount),
            new PostLineRequest(credit, "Credit", body.Amount),
        ];

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(SourceType, adjustmentId),
            EffectiveDate: body.Date,
            Reference: "ADJ",
            Memo: body.Memo,
            Lines: lines,
            SourceRef: adjustmentId,
            SourceType: SourceType);
    }
}
