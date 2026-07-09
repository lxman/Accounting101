using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;   // AccountRequest
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the aggregate customer-account endpoint reconciles: AR balance = Σ open, aging sums to
/// it, the statement's running balance ends at AR balance, the credit ledger ends at the credit balance,
/// and an unknown customer is 404.</summary>
public sealed class GetCustomerAccountEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", null, ["Customer", "Invoice"]);
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
        await PutAccountAsync(controller, clientId, fixture.SalesReturnsAccountId, "4900", "Sales Returns", "Revenue", null);
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

    private static async Task<Guid> IssueInvoiceAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId, decimal amount, DateOnly due)
    {
        DraftInvoiceRequest draftRequest = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: due, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, issued.Id);
        return issued.Id;
    }

    /// <summary>Approve every PendingApproval entry sourced from the given document — the account view now
    /// folds the ledger, which only reflects Posted (approved) entries.</summary>
    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GET_account_reconciles_balances_aging_statement_and_credit()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", "stark@x.com")))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv1 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 1000m, new DateOnly(2026, 3, 31));
        Guid inv2 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 1500m, new DateOnly(2026, 4, 30));

        // partial payment 400 on inv1; credit note 200 on inv1 → inv1 open 400, inv2 open 1500. AR = 1900.
        Payment pay1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 15), 400m, "check", [new Allocation(inv1, 400m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay1.Id);
        CreditNote creditNote = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
                new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 20), [new Allocation(inv1, 200m)], "goodwill")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreditNote>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditNote.Id);

        // overpay inv2-unrelated to create credit, then apply 30 and refund 20 → credit balance 50.
        Payment pay2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 18), 100m, "check", [])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;  // 100 unapplied → credit
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay2.Id);
        CreditApplication creditApp = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
                new CreditApplicationRequest(customer.Id, new DateOnly(2026, 3, 22), [new Allocation(inv2, 30m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditApp.Id);
        Refund refund = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
                new RefundRequest(customer.Id, new DateOnly(2026, 3, 25), 20m, "back")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Refund>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, refund.Id);

        CustomerAccountView view = (await clerk.GetFromJsonAsync<CustomerAccountView>(
            $"/clients/{clientId}/customers/{customer.Id}/account?asOf=2026-05-15"))!;

        // inv1 open = 1000 - 400 - 200 = 400; inv2 open = 1500 - 30 = 1470. AR = 1870.
        Assert.Equal(1870m, view.ArBalance);
        Assert.Equal(view.ArBalance, view.Aging.Current + view.Aging.D1To30 + view.Aging.D31To60 + view.Aging.D61To90 + view.Aging.D90Plus);
        Assert.Equal(view.ArBalance, view.StatementLines[^1].Balance);     // statement ends at AR balance
        Assert.Equal(50m, view.CreditBalance);                             // 100 - 30 - 20
        Assert.Equal(view.CreditBalance, view.CreditLines[^1].CreditBalance);
        Assert.Equal("stark@x.com", view.Customer.Email);
        Assert.All(view.OpenInvoices, l => Assert.True(l.OpenBalance > 0m));
    }

    [Fact]
    public async Task GET_account_for_unknown_customer_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/customers/{Guid.NewGuid()}/account");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GET_account_rejects_non_iso_asOf_and_accepts_iso()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        // The account view now folds the ledger's AR/Customer-Credits accounts, so they must exist even
        // though this test never posts a financial document.
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        // Slash-format date: accepted by the old current-culture TryParse (en-US), rejected by strict ISO.
        HttpResponseMessage bad = await clerk.GetAsync(
            $"/clients/{clientId}/customers/{customer.Id}/account?asOf={Uri.EscapeDataString("06/15/2026")}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // ISO parses regardless of server culture.
        HttpResponseMessage ok = await clerk.GetAsync(
            $"/clients/{clientId}/customers/{customer.Id}/account?asOf=2026-06-15");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
