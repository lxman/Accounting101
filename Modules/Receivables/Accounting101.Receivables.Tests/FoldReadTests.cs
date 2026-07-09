using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves the derived AR read paths (<see cref="CustomerAccountService.GetAccountAsync"/> via
/// <c>GET /customers/{id}/account</c> and <c>GET /invoices/{id}</c>; <see cref="PaymentService.GetCustomerCreditBalanceAsync"/>
/// via <c>GET /customers/{id}/credit-balance</c>) now fold the ledger instead of the module's stored
/// <c>Allocation[]</c>. The two sources still agree once every entry is approved (Task 8 deletes the
/// array; that is the definitive proof no read still touches it) — but a genuine divergence is observable
/// RIGHT NOW: the module records an allocation the instant a payment is recorded, while the ledger — the
/// fold's source of truth — only reflects an entry once it is Posted (approved). Before this task's
/// change, the read folded the module's own array and showed the relief immediately, even though nothing
/// had actually hit the books. That is the RED
/// <see cref="Unapproved_payment_does_not_reduce_the_open_balance_until_the_entry_is_approved"/> pins.
/// <para>
/// Driven entirely over HTTP (not by resolving <c>CustomerAccountService</c>/<c>PaymentService</c> from
/// DI directly): this host's multi-firm tenancy plumbing populates <c>FirmScope</c> from
/// <c>FirmResolutionMiddleware</c> during real request dispatch, so the module's document stores can only
/// be constructed inside an actual HTTP request — resolving them from a bare DI scope throws
/// ("Firm control database not resolved for this request"). <c>SubledgerReadTests</c> avoids this because
/// it only resolves the loopback <c>ILedgerClient</c>, whose calls are themselves outbound HTTP requests
/// back into the same test server.
/// </para>
/// </summary>
public sealed class FoldReadTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controllerHttp, Guid clientId)
    {
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.ReceivableAccountId}",
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimensions = ["Customer", "Invoice"] }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.RevenueAccountId}",
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.SalesTaxPayableAccountId}",
            new AccountRequest { Number = "2200", Name = "Sales Tax Payable", Type = "Liability" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.CashAccountId}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.CustomerCreditsAccountId}",
            new AccountRequest { Number = "2300", Name = "Customer Credits", Type = "Liability", RequiredDimension = "Customer" }))
            .EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient http, Guid clientId)
    {
        Customer customer = (await (await http.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne Enterprises", null)))
            .Content.ReadFromJsonAsync<Customer>())!;
        return customer.Id;
    }

    private static async Task<Invoice> IssueInvoiceAsync(HttpClient http, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draftRequest = new(
            customerId,
            [new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await http.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        HttpResponseMessage issueResp = await http.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null);
        issueResp.EnsureSuccessStatusCode();
        return (await issueResp.Content.ReadFromJsonAsync<Invoice>())!;
    }

    /// <summary>Approve every PendingApproval entry sourced from the given document.</summary>
    private static async Task ApproveSourceEntryAsync(HttpClient http, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await http.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    private sealed record CreditBalanceResponse(Guid CustomerId, decimal CreditBalance);

    [Fact]
    public async Task Customer_account_open_balance_follows_the_ledger_fold()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customerId = await CreateCustomerAsync(http, clientId);

        Invoice invoice = await IssueInvoiceAsync(http, clientId, customerId, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoice.Id);

        HttpResponseMessage payResp = await http.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customerId, new DateOnly(2026, 3, 31), 30m, "check", [new Allocation(invoice.Id, 30m)]));
        payResp.EnsureSuccessStatusCode();
        Payment payment = (await payResp.Content.ReadFromJsonAsync<Payment>())!;
        await ApproveSourceEntryAsync(http, clientId, payment.Id);

        CustomerAccountView view = (await http.GetFromJsonAsync<CustomerAccountView>(
            $"/clients/{clientId}/customers/{customerId}/account?asOf=2026-04-01"))!;
        OpenInvoiceLine line = view.OpenInvoices.Single(l => l.InvoiceId == invoice.Id);
        Assert.Equal(70m, line.OpenBalance);

        decimal invoiceFold = (await http.GetFromJsonAsync<SubledgerResponse>(
                $"/clients/{clientId}/subledger?account={fixture.ReceivableAccountId}&dimension=Invoice"))!
            .Lines.Single(l => l.DimensionValue == invoice.Id).Balance;
        Assert.Equal(invoiceFold, line.OpenBalance);
    }

    [Fact]
    public async Task Unapproved_payment_does_not_reduce_the_open_balance_until_the_entry_is_approved()
    {
        // The genuine divergence this task is meant to fix: the module records the allocation the instant
        // RecordPaymentAsync returns (Payment.Allocations, still stored today), but the ledger — the
        // fold's source of truth — only reflects an entry once it is Posted (approved). Before this task
        // the read folded the module's own array, so it showed the invoice relieved immediately even
        // though the payment's AR-relief line was not yet on the books. That made this test RED
        // pre-change: it asserted 100 before approval where the old code already reported 70.
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customerId = await CreateCustomerAsync(http, clientId);

        Invoice invoice = await IssueInvoiceAsync(http, clientId, customerId, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoice.Id);

        HttpResponseMessage payResp = await http.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customerId, new DateOnly(2026, 3, 31), 30m, "check", [new Allocation(invoice.Id, 30m)]));
        payResp.EnsureSuccessStatusCode();
        Payment payment = (await payResp.Content.ReadFromJsonAsync<Payment>())!;

        // Not yet approved: the payment's AR-relief line is not on the books, so the fold-derived open
        // balance must still be the full 100.
        InvoiceView before = (await http.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice.Id}"))!;
        Assert.Equal(100m, before.OpenBalance);

        await ApproveSourceEntryAsync(http, clientId, payment.Id);

        InvoiceView after = (await http.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice.Id}"))!;
        Assert.Equal(70m, after.OpenBalance);
    }

    [Fact]
    public async Task Customer_credit_balance_from_the_fold_is_positive_for_available_credit()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customerId = await CreateCustomerAsync(http, clientId);

        Invoice invoice = await IssueInvoiceAsync(http, clientId, customerId, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoice.Id);

        // Overpay by 40 -> 40 unapplied credit. Customer Credits is a liability; the ledger's debit-positive
        // fold reads this balance NEGATIVE — the service must negate it to present a positive credit.
        HttpResponseMessage payResp = await http.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customerId, new DateOnly(2026, 3, 31), 140m, "check", [new Allocation(invoice.Id, 100m)]));
        payResp.EnsureSuccessStatusCode();
        Payment payment = (await payResp.Content.ReadFromJsonAsync<Payment>())!;
        await ApproveSourceEntryAsync(http, clientId, payment.Id);

        CreditBalanceResponse balance = (await http.GetFromJsonAsync<CreditBalanceResponse>(
            $"/clients/{clientId}/customers/{customerId}/credit-balance"))!;
        Assert.Equal(40m, balance.CreditBalance);

        CustomerAccountView view = (await http.GetFromJsonAsync<CustomerAccountView>(
            $"/clients/{clientId}/customers/{customerId}/account?asOf=2026-04-01"))!;
        Assert.Equal(40m, view.CreditBalance);

        // Prove the sign is actually negated by the service, not accidentally already positive: the raw
        // fold on the liability account reads negative.
        decimal rawFold = (await http.GetFromJsonAsync<SubledgerResponse>(
                $"/clients/{clientId}/subledger?account={fixture.CustomerCreditsAccountId}&dimension=Customer"))!
            .Lines.Single(l => l.DimensionValue == customerId).Balance;
        Assert.Equal(-40m, rawFold);
    }
}
