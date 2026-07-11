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

        List<AccountRequirement> requirements =
        [
            new(inv.ReceivableAccountId,       "Accounts Receivable", "Asset",     ["Customer", "Invoice"]),
            new(pay.CustomerCreditsAccountId,  "Customer Credits",    "Liability", ["Customer"]),
            new(inv.DefaultRevenueAccountId,   "Revenue",             "Revenue",   []),
            new(inv.SalesTaxPayableAccountId,  "Sales Tax Payable",   "Liability", []),
            new(pay.CashAccountId,             "Cash",                "Asset",     []),
            new(pay.BadDebtExpenseAccountId,   "Bad Debt Expense",    "Expense",   []),
            new(pay.SalesReturnsAccountId,     "Sales Returns",       "Revenue",   []),
        ];

        // Per-category revenue accounts an invoice line may post to (configured RevenueByCategory map). Each
        // must be a real, correctly-typed Revenue account, or a line tagged with that category would fail to post.
        foreach ((string category, Guid accountId) in inv.RevenueAccountsByCategory)
            requirements.Add(new(accountId, $"Revenue: {category}", "Revenue", []));

        return requirements;
    }
}
