using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Focused paging tests for the ListBills endpoint (computed in-memory paging after the service
/// materializes the full filtered list). Proves: skip/limit pages correctly, Total is the full count
/// regardless of page, and an invalid 'order' value returns a clean 400. The service call and its
/// underlying store reads remain unbounded — paging happens only at the endpoint.
/// </summary>
public sealed class BillListPagingTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Invalid_order_value_returns_400()
    {
        (Guid clientId, HttpClient http, _, _) = await fixture.SeedSodClientAsync();

        HttpResponseMessage resp = await http.GetAsync(
            $"/clients/{clientId}/bills?vendorId={Guid.NewGuid()}&order=invalid");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("problem+json", resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Page_1_returns_limit_items_and_correct_total()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await BillSettlementScenario.SetUpChartAsync(controller, clientId, fixture);
        Guid vendorId = await BillSettlementScenario.CreateVendorAsync(clerk, clientId);

        // Enter 3 bills (> limit=2 so pagination splits them across pages).
        await EnterAsync(clerk, clientId, vendorId, fixture.RentExpenseAccountId);
        await EnterAsync(clerk, clientId, vendorId, fixture.RentExpenseAccountId);
        await EnterAsync(clerk, clientId, vendorId, fixture.RentExpenseAccountId);

        PagedResponse<BillView> page = (await clerk.GetFromJsonAsync<PagedResponse<BillView>>(
            $"/clients/{clientId}/bills?vendorId={vendorId}&limit=2"))!;

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(0, page.Skip);
        Assert.Equal(2, page.Limit);
    }

    [Fact]
    public async Task Page_2_returns_remaining_item()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await BillSettlementScenario.SetUpChartAsync(controller, clientId, fixture);
        Guid vendorId = await BillSettlementScenario.CreateVendorAsync(clerk, clientId);

        await EnterAsync(clerk, clientId, vendorId, fixture.RentExpenseAccountId);
        await EnterAsync(clerk, clientId, vendorId, fixture.RentExpenseAccountId);
        await EnterAsync(clerk, clientId, vendorId, fixture.RentExpenseAccountId);

        PagedResponse<BillView> page1 = (await clerk.GetFromJsonAsync<PagedResponse<BillView>>(
            $"/clients/{clientId}/bills?vendorId={vendorId}&limit=2&skip=0"))!;
        PagedResponse<BillView> page2 = (await clerk.GetFromJsonAsync<PagedResponse<BillView>>(
            $"/clients/{clientId}/bills?vendorId={vendorId}&limit=2&skip=2"))!;

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(3, page2.Total);
        Assert.Single(page2.Items);
        // Items on page 2 are distinct from page 1.
        Assert.DoesNotContain(page2.Items[0].Bill.Id, page1.Items.Select(v => v.Bill.Id));
    }

    private static async Task EnterAsync(HttpClient clerk, Guid clientId, Guid vendorId, Guid expenseAccountId)
    {
        DraftBillRequest req = new(vendorId, new DateOnly(2026, 3, 1), null, "REF", null,
            [new BillLineBody("Rent", 1000m, expenseAccountId)]);
        Bill draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", req))
            .Content.ReadFromJsonAsync<Bill>())!;
        (await clerk.PostAsync($"/clients/{clientId}/bills/{draft.Id}/enter", null)).EnsureSuccessStatusCode();
    }
}
