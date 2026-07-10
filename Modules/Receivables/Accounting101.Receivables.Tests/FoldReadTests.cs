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
        // The genuine divergence this test pins: an unapproved payment's AR-relief line is not yet on the
        // books, so the fold-derived open balance must still read the full 100 until the entry is approved.
        // Historically (pre-Task-7) the module recorded an allocation into Payment.Allocations the instant
        // RecordPaymentAsync returned and reads folded that array immediately, showing the invoice relieved
        // before anything had actually hit the books — that made this test RED pre-change: it asserted 100
        // before approval where the old code already reported 70. Task 8 deleted the array entirely; reads
        // now have no other source to fall back to.
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

    /// <summary>
    /// IMPORTANT read-default bug (Task 7 finding 2): the invoice-view read used to default a missing fold
    /// entry's <c>open</c> to <c>0m</c>, so <c>applied = Total − 0 = Total</c> — a freshly issued but
    /// unapproved invoice (its own AR-debit line still PendingApproval, not yet on the books) read as
    /// fully Paid / OpenBalance 0 via <c>GET /invoices/{id}</c>. The fix defaults the missing-fold
    /// <c>open</c> to <c>invoice.Total</c> (applied 0), matching how <see cref="CustomerAccountService"/>
    /// already treats an absent fold entry.
    /// </summary>
    [Fact]
    public async Task Unapproved_invoice_reads_fully_open_not_paid()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid customerId = await CreateCustomerAsync(http, clientId);

        // Issue but deliberately do NOT approve — the invoice's own AR-debit line is still PendingApproval,
        // so it carries no on-the-books line at all in the Posted-only fold.
        Invoice invoice = await IssueInvoiceAsync(http, clientId, customerId, 100m);

        InvoiceView view = (await http.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice.Id}"))!;

        Assert.Equal(100m, view.OpenBalance);
        Assert.NotEqual(SettlementStatus.Paid, view.SettlementStatus);
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

    /// <summary>
    /// Task 8: the persisted payment document carries no allocation array. That is now a compile-time
    /// guarantee — <see cref="Payment"/> has no <c>Allocations</c> member to deserialize into — so this test
    /// proves the runtime half of the claim: a fresh GET of the payment still round-trips correctly, and the
    /// invoice's Invoice-axis fold (the only place the per-invoice split now lives) still reads the correct
    /// open balance. Reads never needed the array.
    /// </summary>
    [Fact]
    public async Task Payment_persists_no_allocation_array_yet_folds_correctly()
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

        // Re-read the payment via GET — the persisted document surfaces no allocation array (Payment has no
        // Allocations member) yet round-trips its real fields correctly.
        Payment[] rePersisted = (await http.GetFromJsonAsync<Payment[]>(
            $"/clients/{clientId}/payments?customerId={customerId}"))!;
        Payment reread = Assert.Single(rePersisted);
        Assert.Equal(payment.Id, reread.Id);
        Assert.Equal(30m, reread.Amount);
        Assert.False(reread.Voided);

        // The invoice's fold-derived open balance still reflects the payment's relief — reads rely only on
        // the ledger, never the deleted array.
        InvoiceView invoiceView = (await http.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice.Id}"))!;
        Assert.Equal(70m, invoiceView.OpenBalance);

        decimal invoiceFold = (await http.GetFromJsonAsync<SubledgerResponse>(
                $"/clients/{clientId}/subledger?dimension=Invoice"))!
            .Lines.Single(l => l.AccountId == fixture.ReceivableAccountId && l.DimensionValue == invoice.Id).Balance;
        Assert.Equal(70m, invoiceFold);
    }
}
