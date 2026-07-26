using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Receivables.Tests;

/// <summary>The retry-key contract on POST /write-offs, POST /credit-notes and POST /refunds: absent key
/// behaves as before, a replayed key returns the stored document without creating a second one, and a key
/// reused for different financial content is a 422 relayed from the ledger. Mirrors
/// <see cref="PaymentRetryKeyTests"/> for the Payments/CreditApplications pair.</summary>
public sealed class DispositionRetryKeyTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
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

    /// <summary>Records a payment whose amount exceeds its allocation, leaving the remainder as customer
    /// credit — the only way to have credit available for a refund. Approves the payment's own entry: the
    /// credit balance is folded Posted-only, so an unapproved overpayment would read as zero available credit.</summary>
    private static async Task GiveCustomerCreditAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId, Guid overpaidInvoice, decimal overpaidInvoiceAmount, decimal overpayBy)
    {
        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customerId, new DateOnly(2026, 3, 1), overpaidInvoiceAmount + overpayBy, "check",
                [new Allocation(overpaidInvoice, overpaidInvoiceAmount)]));
        resp.EnsureSuccessStatusCode();
        Payment payment = (await resp.Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);
    }

    // ── POST /write-offs ─────────────────────────────────────────────────────

    [Fact]
    public async Task WriteOff_without_a_key_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 250m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], "uncollectible"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task WriteOff_replayed_key_returns_the_stored_write_off_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 250m);

        Guid key = Guid.NewGuid();
        WriteOffRequest req = new(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], "uncollectible", key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        WriteOff a = (await first.Content.ReadFromJsonAsync<WriteOff>())!;
        WriteOff b = (await second.Content.ReadFromJsonAsync<WriteOff>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        CreditDocument[] all = (await clerk.GetFromJsonAsync<CreditDocument[]>(
            $"/clients/{clientId}/credits?customerId={customer}"))!;
        Assert.Single(all);
    }

    [Fact]
    public async Task WriteOff_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 250m);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], "uncollectible", key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 100m)], "uncollectible", key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }

    // ── POST /credit-notes ───────────────────────────────────────────────────

    [Fact]
    public async Task CreditNote_without_a_key_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 30m)], "partial return"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task CreditNote_replayed_key_returns_the_stored_credit_note_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Guid key = Guid.NewGuid();
        CreditNoteRequest req = new(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 30m)], "partial return", key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        CreditNote a = (await first.Content.ReadFromJsonAsync<CreditNote>())!;
        CreditNote b = (await second.Content.ReadFromJsonAsync<CreditNote>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        CreditDocument[] all = (await clerk.GetFromJsonAsync<CreditDocument[]>(
            $"/clients/{clientId}/credits?customerId={customer}"))!;
        Assert.Single(all);
    }

    [Fact]
    public async Task CreditNote_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 30m)], "partial return", key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 20m)], "partial return", key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }

    // ── POST /refunds ────────────────────────────────────────────────────────

    [Fact]
    public async Task Refund_without_a_key_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid overpaidInvoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);
        await GiveCustomerCreditAsync(clerk, approver, clientId, customer, overpaidInvoice, 100m, 40m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer, new DateOnly(2026, 3, 2), 40m, "returned"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Refund_replayed_key_returns_the_stored_refund_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid overpaidInvoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);
        await GiveCustomerCreditAsync(clerk, approver, clientId, customer, overpaidInvoice, 100m, 40m);

        Guid key = Guid.NewGuid();
        RefundRequest req = new(customer, new DateOnly(2026, 3, 2), 40m, "returned", key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        Refund a = (await first.Content.ReadFromJsonAsync<Refund>())!;
        Refund b = (await second.Content.ReadFromJsonAsync<Refund>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        Refund[] all = (await clerk.GetFromJsonAsync<Refund[]>(
            $"/clients/{clientId}/refunds?customerId={customer}"))!;
        Assert.Single(all);
    }

    [Fact]
    public async Task Refund_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid overpaidInvoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);
        await GiveCustomerCreditAsync(clerk, approver, clientId, customer, overpaidInvoice, 100m, 40m);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer, new DateOnly(2026, 3, 2), 40m, "returned", key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer, new DateOnly(2026, 3, 2), 25m, "returned", key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }
}
