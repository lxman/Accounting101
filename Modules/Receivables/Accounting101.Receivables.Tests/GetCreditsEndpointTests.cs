using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;   // AccountRequest
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the unified read endpoint that powers the UI's Credits list: it returns a customer's
/// credit-notes, write-offs, and credit-applications as one date-descending list (correct type tag,
/// amount = Σ allocations, memo for notes/write-offs and null for credit-applications) and rejects a
/// missing customerId.</summary>
public sealed class GetCreditsEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", "Customer");
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
        await PutAccountAsync(controller, clientId, fixture.BadDebtExpenseAccountId, "6000", "Bad Debt Expense", "Expense", null);
        await PutAccountAsync(controller, clientId, fixture.SalesReturnsAccountId, "4900", "Sales Returns", "Revenue", null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();

    private static async Task<Guid> IssueInvoiceAsync(HttpClient clerk, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draftRequest = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        return issued.Id;
    }

    [Fact]
    public async Task GET_credits_returns_unified_date_descending_list_with_memo()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv1 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);   // → write-off
        Guid inv2 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);   // → credit-note
        Guid inv3 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);   // → overpaid (creates credit)
        Guid inv4 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);   // → credit-application target

        // Overpay inv3 by 50 → 50 of unapplied customer credit.
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 2), 150m, "check",
                [new Allocation(inv3, 100m)]))).EnsureSuccessStatusCode();

        (await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer.Id, new DateOnly(2026, 3, 5), [new Allocation(inv1, 100m)], "uncollectible")))
            .EnsureSuccessStatusCode();
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
            new CreditApplicationRequest(customer.Id, new DateOnly(2026, 3, 8), [new Allocation(inv4, 50m)])))
            .EnsureSuccessStatusCode();
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 10), [new Allocation(inv2, 100m)], "returned goods")))
            .EnsureSuccessStatusCode();

        CreditDocument[] list = (await clerk.GetFromJsonAsync<CreditDocument[]>(
            $"/clients/{clientId}/credits?customerId={customer.Id}"))!;

        Assert.Equal(3, list.Length);                          // payment is not a credit doc
        Assert.Equal("credit-note", list[0].Type);             // 3/10 — newest first
        Assert.Equal(100m, list[0].Amount);
        Assert.Equal("returned goods", list[0].Memo);
        Assert.False(list[0].Voided);

        Assert.Equal("credit-application", list[1].Type);      // 3/8
        Assert.Equal(50m, list[1].Amount);
        Assert.Null(list[1].Memo);

        Assert.Equal("write-off", list[2].Type);               // 3/5
        Assert.Equal(100m, list[2].Amount);
        Assert.Equal("uncollectible", list[2].Memo);
    }

    [Fact]
    public async Task GET_credits_includes_voided_dispositions()
    {
        // Regression test: voided credit-notes must appear in GET /credits with Voided==true.
        // Before the fix, QueryAsync excluded voided docs by default and they vanished.
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);

        // Record a credit-note as clerk.
        CreditNote cn = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 10), [new Allocation(inv, 100m)], "issued in error")))
            .Content.ReadFromJsonAsync<CreditNote>())!;

        // Void requires Approver — Clerk has only Read permission under SoD.
        (await approver.PostAsync($"/clients/{clientId}/credit-notes/{cn.Id}/void", null)).EnsureSuccessStatusCode();

        CreditDocument[] list = (await clerk.GetFromJsonAsync<CreditDocument[]>(
            $"/clients/{clientId}/credits?customerId={customer.Id}"))!;

        // The voided credit-note must be present (not silently dropped).
        CreditDocument? voided = list.FirstOrDefault(d => d.Id == cn.Id);
        Assert.NotNull(voided);
        Assert.Equal("credit-note", voided.Type);
        Assert.True(voided.Voided);
    }

    [Fact]
    public async Task GET_credits_without_customerId_is_400()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/credits");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
