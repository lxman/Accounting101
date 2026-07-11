using Accounting101.ModuleKit;

namespace Accounting101.Receivables.Api;

/// <summary>Declares the chart accounts the receivables recipes post to, for readiness checks. Draws from
/// BOTH the invoice and payment posting-account bags; the shared Receivable account is declared once
/// (from the invoice bag) since both bags resolve it to the same chart id.</summary>
public sealed class ReceivablesChartRequirements(
    IInvoiceAccountsProvider invoiceAccounts, IPaymentAccountsProvider paymentAccounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        InvoicePostingAccounts inv = await invoiceAccounts.GetAsync(clientId, ct);
        PaymentPostingAccounts pay = await paymentAccounts.GetAsync(clientId, ct);
        return
        [
            new(inv.ReceivableAccountId,       "Accounts Receivable", "Asset",     ["Customer", "Invoice"]),
            new(pay.CustomerCreditsAccountId,  "Customer Credits",    "Liability", ["Customer"]),
            new(inv.DefaultRevenueAccountId,   "Revenue",             "Revenue",   []),
            new(inv.SalesTaxPayableAccountId,  "Sales Tax Payable",   "Liability", []),
            new(pay.CashAccountId,             "Cash",                "Asset",     []),
            new(pay.BadDebtExpenseAccountId,   "Bad Debt Expense",    "Expense",   []),
            new(pay.SalesReturnsAccountId,     "Sales Returns",       "Revenue",   []),
        ];
    }
}
