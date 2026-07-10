using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>Assembles the read-only <see cref="VendorAccountView"/> for one vendor by reading its
/// documents and folding the ledger for every A/P-derived figure — the Bill-axis and Vendor-axis
/// subledgers are the single source of truth for what's actually open (on the books). Read-only; computes,
/// never stores.</summary>
public sealed class VendorAccountService(
    IVendorStore vendors, IBillStore bills, IBillPaymentStore payments,
    IBillAccountsProvider accountsProvider, ILedgerClient ledger)
{
    public async Task<VendorAccountView?> GetAccountAsync(
        Guid clientId, Guid vendorId, DateOnly asOf, CancellationToken ct = default)
    {
        Vendor? vendor = await vendors.GetAsync(clientId, vendorId, ct);
        if (vendor is null) return null;

        IReadOnlyList<Bill> vendorBills = await bills.GetByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<VendorCreditApplication> cs = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);

        BillPaymentPostingAccounts accounts = await accountsProvider.GetPaymentAccountsAsync(clientId, ct);

        // Bill open balances from the Bill-axis A/P fold. A/P is credit-normal → the debit-positive fold reads
        // the outstanding payable NEGATIVE, so open = −fold.
        IReadOnlyList<SubledgerLineResponse> apByBill =
            await ledger.GetSubledgerAsync(clientId, accounts.PayableAccountId, "Bill", asOf, ct);
        Dictionary<Guid, decimal> openByBill = apByBill.ToDictionary(l => l.DimensionValue, l => -l.Balance);
        // applied = total − open; OpenBills keeps its (total, applied) contract. A bill absent from the fold
        // (its own A/P line not yet on the books) defaults to fully open.
        Dictionary<Guid, decimal> applied = vendorBills.ToDictionary(
            b => b.Id, b => b.Total - openByBill.GetValueOrDefault(b.Id, b.Total));

        IReadOnlyList<OpenBillLine> open = VendorAccountBuilder.OpenBills(vendorBills, applied, asOf);

        // Vendor Credits is an ASSET (debit-normal): the fold reads available credit POSITIVE — NO negation.
        decimal credit = (await ledger.GetSubledgerAsync(clientId, accounts.VendorCreditsAccountId, "Vendor", asOf, ct))
            .Where(l => l.DimensionValue == vendorId).Sum(l => l.Balance);

        // Per-document A/P relief — what each settlement document actually applied to bills — folded from
        // its own ledger entry now that the module stores no allocation array. Feeds Statement/CreditActivity,
        // both READ surfaces: postedOnly so a document's relief never shows before its own posting does,
        // matching the Posted-only ApBalance computed above.
        Dictionary<Guid, decimal> reliefByDocument = new();
        foreach (BillPayment p in ps.Where(p => !p.Voided))
            reliefByDocument[p.Id] = await SettlementRelief.ForSourceAsync(ledger, clientId, p.Id, accounts.PayableAccountId, ct, postedOnly: true);
        foreach (VendorCreditApplication c in cs.Where(c => !c.Voided))
            reliefByDocument[c.Id] = await SettlementRelief.ForSourceAsync(ledger, clientId, c.Id, accounts.PayableAccountId, ct, postedOnly: true);

        return new VendorAccountView(
            vendor,
            VendorAccountBuilder.ApBalance(open),
            credit,
            VendorAccountBuilder.Aging(open),
            open,
            VendorAccountBuilder.Statement(vendorBills, ps, cs, reliefByDocument),
            VendorAccountBuilder.CreditActivity(ps, cs, reliefByDocument));
    }
}
