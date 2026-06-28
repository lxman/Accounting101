using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using Accounting101.Ledger.Contracts;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>Rounding sweep: sales-tax rounds to the cent in both the posted entry and the statements,
/// and an uneven multi-invoice allocation leaves every open balance exact with the subledger tying out.</summary>
public sealed class RoundingE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, clerk, approver);
    }

    [Fact]
    public async Task Sales_tax_rounds_to_the_cent_in_entry_and_statements()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, unitPrice: 100.10m, taxRate: 0.0825m, taxable: true);

        // Invoice total reflects rounded tax: 100.10 + 8.26 = 108.36.
        InvoiceView view = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice}"))!;
        Assert.Equal(108.36m, view.Invoice.Total);
        Assert.Equal(108.36m, view.OpenBalance);

        // Balance sheet: A/R asset line = 108.36, sales-tax-payable liability line = 8.26, and it balances.
        DateOnly asOf = new(2026, 3, 31);
        BalanceSheetResponse sheet = (await clerk.GetFromJsonAsync<BalanceSheetResponse>(
            $"/clients/{clientId}/statements/balance-sheet?asOf={asOf:yyyy-MM-dd}"))!;
        Assert.True(sheet.IsBalanced);
        Assert.Equal(108.36m, sheet.Assets.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId).Amount);
        Assert.Equal(8.26m, sheet.Liabilities.Lines.Single(l => l.AccountId == fixture.SalesTaxPayableAccountId).Amount);

        // Income statement: revenue is the pre-tax subtotal exactly.
        IncomeStatementResponse income = (await clerk.GetFromJsonAsync<IncomeStatementResponse>(
            $"/clients/{clientId}/statements/income-statement?from=2026-01-01&to=2026-03-31"))!;
        Assert.Equal(100.10m, income.Revenue.Total);
    }

    [Fact]
    public async Task Uneven_split_across_invoices_leaves_exact_balances_and_subledger_ties_out()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid inv1 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 33.33m);
        Guid inv2 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 33.33m);
        Guid inv3 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 33.34m);

        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 31), 100.00m, "check",
                    [new Allocation(inv1, 33.33m), new Allocation(inv2, 33.33m), new Allocation(inv3, 33.34m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        foreach (Guid id in new[] { inv1, inv2, inv3 })
        {
            InvoiceView v = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{id}"))!;
            Assert.Equal(0m, v.OpenBalance);
            Assert.Equal(SettlementStatus.Paid, v.SettlementStatus);
        }

        SubledgerReconciliationResponse ar = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(ar.TiesOut);
        await AssertBalancedAsync(clerk, clientId, new DateOnly(2026, 3, 31));
    }
}
