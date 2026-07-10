using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the A/R clerk's cash-side dispositions end-to-end through the real host: a write-off
/// settles a partially-paid invoice, a credit note reduces an invoice's open balance, and a refund draws
/// down a customer's credit balance. All dispositions are entered by the Clerk and approved by the
/// Approver (SoD), exercising the new endpoints + manifest collections.</summary>
public sealed class ReceivablesDispositionsE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", null, ["Customer", "Invoice"]);
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
        await PutAccountAsync(controller, clientId, fixture.BadDebtExpenseAccountId, "6000", "Bad Debt Expense", "Expense", null);
        await PutAccountAsync(controller, clientId, fixture.SalesReturnsAccountId, "4900", "Sales Returns", "Revenue", null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension, string[]? requiredDimensions = null)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest
            {
                Number = number, Name = name, Type = type,
                RequiredDimension = requiredDimension, RequiredDimensions = requiredDimensions,
            }))
            .EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient clerk, Guid clientId)
    {
        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;
        return customer.Id;
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

    private sealed record CreditBalanceResponse(Guid CustomerId, decimal CreditBalance);

    [Fact]
    public async Task WriteOff_settles_remaining_balance_through_host()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 1000m);

        // partial payment of 600, approved
        HttpResponseMessage pay = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 600m, "wire", [new Allocation(invoice, 600m)]));
        Payment payment = (await pay.Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        // write off the remaining 400, approved
        HttpResponseMessage wo = await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice, 400m)], "uncollectible"));
        Assert.Equal(HttpStatusCode.Created, wo.StatusCode);
        WriteOff writeOff = (await wo.Content.ReadFromJsonAsync<WriteOff>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, writeOff.Id);

        InvoiceView view = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice}"))!;
        Assert.Equal(0m, view.OpenBalance);
        Assert.Equal(SettlementStatus.Paid, view.SettlementStatus);
    }

    [Fact]
    public async Task CreditNote_reduces_invoice_balance_through_host()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 1000m);

        // credit note of 250 against the invoice, approved
        HttpResponseMessage cn = await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice, 250m)], "returned goods"));
        Assert.Equal(HttpStatusCode.Created, cn.StatusCode);
        CreditNote creditNote = (await cn.Content.ReadFromJsonAsync<CreditNote>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditNote.Id);

        InvoiceView view = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice}"))!;
        Assert.Equal(750m, view.OpenBalance);
        Assert.Equal(SettlementStatus.PartiallyPaid, view.SettlementStatus);
    }

    [Fact]
    public async Task Refund_of_overpayment_credit_through_host()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 500m);

        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 700m, "wire", [new Allocation(invoice, 500m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        HttpResponseMessage rf = await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer, new DateOnly(2026, 3, 6), 200m, "returned overpayment"));
        Assert.Equal(HttpStatusCode.Created, rf.StatusCode);
        Refund refund = (await rf.Content.ReadFromJsonAsync<Refund>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, refund.Id);

        CreditBalanceResponse bal = (await clerk.GetFromJsonAsync<CreditBalanceResponse>(
            $"/clients/{clientId}/customers/{customer}/credit-balance"))!;
        Assert.Equal(0m, bal.CreditBalance);
    }
}
