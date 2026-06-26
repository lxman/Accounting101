using System.Net.Http.Json;
using Accounting101.Receivables;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves cash application end-to-end through the real host: a payment settles invoices, an
/// over-payment becomes customer credit, that credit applies to a later invoice, and both subledgers
/// (A/R and Customer Credits) tie out throughout.</summary>
public sealed class CashApplicationTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", "Customer");
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    private static async Task<Guid> IssueInvoiceAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draftRequest = new(
            customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, issued.Id);
        return issued.Id;
    }

    private static async Task ApproveBySourceRefAsync(
        HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Payment_overpayment_credit_application_and_subledgers_tie_out()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        // Issue invoice1 for $100.
        Guid invoice1 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);

        // Over-pay invoice1 by 50 → invoice Paid, customer credit $50.
        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 31), 150m, "check",
                    [new Allocation(invoice1, 100m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        InvoiceView v1 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice1}"))!;
        Assert.Equal(SettlementStatus.Paid, v1.SettlementStatus);
        Assert.Equal(0m, v1.OpenBalance);

        // Issue a second invoice for $100, then apply the $50 credit to it.
        Guid invoice2 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);
        CreditApplication applied = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
                new CreditApplicationRequest(customer.Id, new DateOnly(2026, 4, 2),
                    [new Allocation(invoice2, 50m)])))
            .Content.ReadFromJsonAsync<CreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, applied.Id);

        InvoiceView v2 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice2}"))!;
        Assert.Equal(50m, v2.OpenBalance);
        Assert.Equal(SettlementStatus.PartiallyPaid, v2.SettlementStatus);

        // Both subledgers tie out.
        SubledgerReconciliationResponse ar = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(ar.TiesOut);

        SubledgerReconciliationResponse credits = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.CustomerCreditsAccountId}&dimension=Customer"))!;
        Assert.True(credits.TiesOut);
    }
}
