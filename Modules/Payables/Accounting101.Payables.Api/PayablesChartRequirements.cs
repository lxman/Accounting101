using Accounting101.ModuleKit;

namespace Accounting101.Payables.Api;

/// <summary>Declares the chart accounts the payables recipes post to, for readiness checks. Simpler than
/// Receivables — a single provider method covers all three accounts. Vendor Credits is deliberately
/// <c>Asset</c> (debit-normal — the module holds it as a prepayment the vendor owes back), NOT
/// <c>Liability</c> like AR's symmetric-looking Customer Credits.</summary>
public sealed class PayablesChartRequirements(IBillAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        BillPaymentPostingAccounts a = await accounts.GetPaymentAccountsAsync(clientId, ct);
        return
        [
            new(a.PayableAccountId,       "Accounts Payable", "Liability", ["Vendor", "Bill"]),
            new(a.VendorCreditsAccountId, "Vendor Credits",   "Asset",     ["Vendor"]), // debit-normal — Asset, not Liability
            new(a.CashAccountId,          "Cash",             "Asset",     []),
        ];
    }
}
