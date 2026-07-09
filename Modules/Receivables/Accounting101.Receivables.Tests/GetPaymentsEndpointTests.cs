using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;   // AccountRequest
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the read endpoint that powers the UI's applied-payments list: it returns a customer's
/// payments and rejects a missing customerId. The persisted payment carries no allocation array (a
/// compile-time guarantee — <c>Payment</c> has no <c>Allocations</c> member); the per-invoice split is
/// proven separately by the ledger fold (see <c>PaymentDimensionTests</c>, <c>FoldReadTests</c>).</summary>
public sealed class GetPaymentsEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", null, ["Customer", "Invoice"]);
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension, string[]? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest
            {
                Number = number, Name = name, Type = type,
                RequiredDimension = requiredDimension, RequiredDimensions = requiredDimensions,
            }))
            .EnsureSuccessStatusCode();

    private static async Task<Guid> IssueInvoiceAsync(HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draftRequest = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, issued.Id);
        return issued.Id;
    }

    /// <summary>Approve every PendingApproval entry sourced from the given document — payment validation
    /// now folds the ledger, which only reflects Posted (approved) entries.</summary>
    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GET_payments_returns_customer_payments_with_no_allocation_array_and_the_invoice_fold_reflects_it()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;
        Guid invoiceId = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);

        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 31), 60m, "check",
                [new Allocation(invoiceId, 60m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        // Read the payment back through the store/endpoint: the persisted document carries no allocation
        // array. `Payment` has no `Allocations` member — a compile-time guarantee, so there is nothing to
        // assert at runtime beyond the shape below. The per-invoice split still took effect: prove it by
        // reading the invoice's fold-derived open balance after this fresh GET.
        Payment[] list = (await clerk.GetFromJsonAsync<Payment[]>(
            $"/clients/{clientId}/payments?customerId={customer.Id}"))!;

        Assert.Single(list);
        Assert.Equal(payment.Id, list[0].Id);
        Assert.Equal(60m, list[0].Amount);

        InvoiceView view = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoiceId}"))!;
        Assert.Equal(40m, view.OpenBalance);   // 100 - 60, sourced only from the ledger fold
    }

    [Fact]
    public async Task GET_payments_without_customerId_is_400()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/payments");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
