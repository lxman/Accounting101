using System.Net;
using System.Net.Http.Json;
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Accounting101.Inventory.Tests;

/// <summary>A fold-on-read refusal is relayed with the engine's real status (a 4xx), not an opaque 500.
/// Exercised via the {Item} value fold: the Inventory Asset account is configured WITHOUT the "Item"
/// required dimension, so the engine's subledger validation refuses the fold read (422,
/// LedgerEndpoints.cs:554). The list and detail item reads must relay that 422, not let the bare
/// EnsureSuccessStatusCode 500 escape — the exact /assets-style smoke crash, pinned for Inventory.</summary>
public sealed class LedgerErrorRelayE2eTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
{
    [Fact]
    public async Task Listing_items_when_the_inventory_account_lacks_its_dimension_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpMisconfiguredChartAsync(http, clientId);
        await CreateItemAsync(http, clientId);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/items");

        await AssertRelayed422(resp);
    }

    [Fact]
    public async Task Getting_one_item_when_the_inventory_account_lacks_its_dimension_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpMisconfiguredChartAsync(http, clientId);
        Guid itemId = await CreateItemAsync(http, clientId);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/items/{itemId}");

        await AssertRelayed422(resp);
    }

    private static async Task AssertRelayed422(HttpResponseMessage resp)
    {
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.InRange((int)resp.StatusCode, 400, 499);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Detail));
    }

    /// <summary>Every posting account is configured, but Inventory Asset is a PLAIN Asset account
    /// (no RequiredDimensions), so the "Item"-dimensioned value fold refuses with 422.</summary>
    private async Task SetUpMisconfiguredChartAsync(HttpClient http, Guid clientId)
    {
        await PutAccountAsync(http, clientId, fixture.InventoryAssetAccountId,      "1400", "Inventory Asset",     "Asset"); // NO RequiredDimensions — the misconfig
        await PutAccountAsync(http, clientId, fixture.CogsAccountId,                "5000", "Cost of Goods Sold",  "Expense");
        await PutAccountAsync(http, clientId, fixture.GrniClearingAccountId,        "2100", "GRNI Clearing",       "Liability");
        await PutAccountAsync(http, clientId, fixture.InventoryAdjustmentAccountId, "5100", "Inventory Adjustment","Expense");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type })).EnsureSuccessStatusCode();

    private static async Task<Guid> CreateItemAsync(HttpClient http, Guid clientId)
    {
        // SaveItemRequest is (string Sku, string Name, string? Description, string UnitOfMeasure) —
        // matching InventoryLedgerFirstProofTests. ItemView is (Item Item), so the id is view.Item.Id.
        ItemView view = (await (await http.PostAsJsonAsync($"/clients/{clientId}/items",
            new SaveItemRequest("SKU1", "Widget", null, "each")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ItemView>())!;
        return view.Item.Id;
    }
}
