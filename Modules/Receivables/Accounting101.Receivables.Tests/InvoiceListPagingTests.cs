using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Receivables.Api;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Focused paging tests for the ListInvoices endpoint (computed in-memory paging after the service
/// materializes the full filtered list). Proves: skip/limit pages correctly, Total is the full count
/// regardless of page, and an invalid 'order' value returns a clean 400. The service call and its
/// underlying store reads remain unbounded — paging happens only at the endpoint.
/// </summary>
public sealed class InvoiceListPagingTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    [Fact]
    public async Task Invalid_order_value_returns_400()
    {
        (Guid clientId, HttpClient http, _, _) = await fixture.SeedSodClientAsync();

        HttpResponseMessage resp = await http.GetAsync(
            $"/clients/{clientId}/invoices?customerId={Guid.NewGuid()}&order=invalid");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("problem+json", resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Page_1_returns_limit_items_and_correct_total()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SettlementScenario.SetUpChartAsync(controller, clientId, fixture);
        Guid customerId = await SettlementScenario.CreateCustomerAsync(clerk, clientId);

        // Issue 3 invoices (> limit=2 so pagination splits them across pages).
        await IssueAsync(clerk, clientId, customerId);
        await IssueAsync(clerk, clientId, customerId);
        await IssueAsync(clerk, clientId, customerId);

        PagedResponse<InvoiceView> page = (await clerk.GetFromJsonAsync<PagedResponse<InvoiceView>>(
            $"/clients/{clientId}/invoices?customerId={customerId}&limit=2"))!;

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(0, page.Skip);
        Assert.Equal(2, page.Limit);
    }

    [Fact]
    public async Task Page_2_returns_remaining_item()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SettlementScenario.SetUpChartAsync(controller, clientId, fixture);
        Guid customerId = await SettlementScenario.CreateCustomerAsync(clerk, clientId);

        await IssueAsync(clerk, clientId, customerId);
        await IssueAsync(clerk, clientId, customerId);
        await IssueAsync(clerk, clientId, customerId);

        PagedResponse<InvoiceView> page1 = (await clerk.GetFromJsonAsync<PagedResponse<InvoiceView>>(
            $"/clients/{clientId}/invoices?customerId={customerId}&limit=2&skip=0"))!;
        PagedResponse<InvoiceView> page2 = (await clerk.GetFromJsonAsync<PagedResponse<InvoiceView>>(
            $"/clients/{clientId}/invoices?customerId={customerId}&limit=2&skip=2"))!;

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(3, page2.Total);
        Assert.Single(page2.Items);
        // Items on page 2 are distinct from page 1.
        Assert.DoesNotContain(page2.Items[0].Invoice.Id, page1.Items.Select(v => v.Invoice.Id));
    }

    [Fact]
    public async Task Over_request_limit_envelope_echoes_effective_clamp()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SettlementScenario.SetUpChartAsync(controller, clientId, fixture);
        Guid customerId = await SettlementScenario.CreateCustomerAsync(clerk, clientId);

        await IssueAsync(clerk, clientId, customerId);

        PagedResponse<InvoiceView> page = (await clerk.GetFromJsonAsync<PagedResponse<InvoiceView>>(
            $"/clients/{clientId}/invoices?customerId={customerId}&limit=500&skip=0"))!;

        Assert.Equal(200, page.Limit);
        Assert.Equal(0, page.Skip);
    }

    private static async Task IssueAsync(HttpClient clerk, Guid clientId, Guid customerId)
    {
        DraftInvoiceRequest req = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = 100m, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", req))
            .Content.ReadFromJsonAsync<Invoice>())!;
        (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null)).EnsureSuccessStatusCode();
    }
}
