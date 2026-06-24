using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

/// <summary>The cash-application recipes: a payment or credit application composes into one balanced
/// journal entry. Pure — request in, wire DTO out — leaving sequencing, approval, and persistence to the engine.</summary>
public static class PaymentPosting
{
    public const string PaymentSourceType = "Payment";
    public const string CreditApplicationSourceType = "CreditApplication";
    public const string CustomerDimension = "Customer";

    public static PostEntryRequest ComposePayment(Guid paymentId, PaymentBody body, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal allocated = body.Allocations.Sum(a => a.Amount);
        decimal remainder = body.Amount - allocated;
        Dictionary<string, Guid> dim = new() { [CustomerDimension] = body.CustomerId };

        List<PostLineRequest> lines = [new(accounts.CashAccountId, "Debit", body.Amount)];
        if (allocated != 0m)
            lines.Add(new(accounts.ReceivableAccountId, "Credit", allocated, Dimensions: dim));
        if (remainder != 0m)
            lines.Add(new(accounts.CustomerCreditsAccountId, "Credit", remainder, Dimensions: dim));

        return new PostEntryRequest(
            Id: null, EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: paymentId, SourceType: PaymentSourceType);
    }
}
