using System.Net;
using System.Net.Http.Json;
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

public sealed class InventoryEndpointsTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
{
    private static SaveItemRequest Widget(string sku = "SKU1") => new(sku, "Widget", "A widget", "each");

    // Reads now fold through the ledger (ItemValuationService), so any test that reads/deactivates an item
    // needs the Inventory posting accounts to actually exist in the client's chart — otherwise the
    // subledger query 500s on the unconfigured account (a known, already-accepted staging-era gap; see the
    // Fixed Assets ledger-first conversion, which hit the same "fold-on-read 500 on unconfigured chart").
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        await PutAccountAsync(http, clientId, fixture.InventoryAssetAccountId, "1400", "Inventory Asset", "Asset", ["Item"]);
        await PutAccountAsync(http, clientId, fixture.CogsAccountId, "5000", "Cost of Goods Sold", "Expense");
        await PutAccountAsync(http, clientId, fixture.GrniClearingAccountId, "2100", "GRNI Clearing", "Liability");
        await PutAccountAsync(http, clientId, fixture.InventoryAdjustmentAccountId, "5100", "Inventory Adjustment", "Expense");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name,
        string type, IReadOnlyList<string>? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new { Number = number, Name = name, Type = type, RequiredDimensions = requiredDimensions }))
            .EnsureSuccessStatusCode();

    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Create_list_get_update_deactivate_lifecycle()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/items", Widget());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        ItemView view = (await created.Content.ReadFromJsonAsync<ItemView>())!;
        Guid itemId = view.Item.Id;
        Assert.Equal(ItemStatus.Active, view.Item.Status);
        Assert.Equal(0m, view.Item.OnHandQuantity);

        PagedResponse<ItemView> list = (await http.GetFromJsonAsync<PagedResponse<ItemView>>($"/clients/{clientId}/items"))!;
        Assert.Contains(list.Items, i => i.Item.Id == itemId);

        ItemView got = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{itemId}"))!;
        Assert.Equal("Widget", got.Item.Name);

        HttpResponseMessage updated = await http.PutAsJsonAsync(
            $"/clients/{clientId}/items/{itemId}", Widget() with { Name = "Widget (renamed)" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Widget (renamed)", (await updated.Content.ReadFromJsonAsync<ItemView>())!.Item.Name);

        HttpResponseMessage deactivated = await http.PostAsync($"/clients/{clientId}/items/{itemId}/deactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        PagedResponse<ItemView> afterList = (await http.GetFromJsonAsync<PagedResponse<ItemView>>($"/clients/{clientId}/items"))!;
        Assert.DoesNotContain(afterList.Items, i => i.Item.Id == itemId);
        PagedResponse<ItemView> withInactive = (await http.GetFromJsonAsync<PagedResponse<ItemView>>($"/clients/{clientId}/items?includeInactive=true"))!;
        Assert.Contains(withInactive.Items, i => i.Item.Id == itemId);
    }

    [Fact]
    public async Task Invalid_item_is_422()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        HttpResponseMessage response = await http.PostAsJsonAsync(
            $"/clients/{clientId}/items", Widget() with { Name = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_sku_on_create_is_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsJsonAsync($"/clients/{clientId}/items", Widget("DUP"))).StatusCode);
        HttpResponseMessage second = await http.PostAsJsonAsync($"/clients/{clientId}/items", Widget("DUP"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_missing_item_is_404_and_repeat_is_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsync($"/clients/{clientId}/items/{Guid.NewGuid()}/deactivate", null)).StatusCode);

        ItemView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/items", Widget())).Content.ReadFromJsonAsync<ItemView>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/clients/{clientId}/items/{created.Item.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/clients/{clientId}/items/{created.Item.Id}/deactivate", null)).StatusCode);
    }

    /// <summary>T5 cut-over: the has-stock deactivate guard now lives in InventoryService.DeactivateAsync,
    /// reading the posted-only ledger fold — proven here end-to-end through HTTP now that the movements
    /// endpoint exists (superseding the old store-level-only coverage noted in ItemDocumentStoreTests).
    /// A receipt's stock blocks deactivation only once its entry is approved (posted-only visible); issuing
    /// the stock back to zero (and approving that entry too) clears the guard.</summary>
    [Fact]
    public async Task Deactivate_is_blocked_while_posted_stock_on_hand_then_succeeds_once_cleared()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (inventory.write + gl.approve)
        await SetUpChartAsync(http, clientId);
        Guid approverUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(approverUserId, clientId, LedgerRole.Approver);
        HttpClient approver = fixture.ClientFor(approverUserId, "Acme Approver", LedgerRole.Approver);

        ItemView item = (await (await http.PostAsJsonAsync($"/clients/{clientId}/items", Widget("STOCKED")))
            .Content.ReadFromJsonAsync<ItemView>())!;

        HttpResponseMessage receiptResponse = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 5m, 10m, new DateOnly(2026, 1, 1), null));
        receiptResponse.EnsureSuccessStatusCode();
        StockMovementView receipt = (await receiptResponse.Content.ReadFromJsonAsync<StockMovementView>())!;

        // Not yet approved — the posted-only guard does not yet see the stock, so deactivation succeeds
        // immediately (a pending receipt does not block deactivation). Reactivate it back to exercise the
        // real guard once the receipt is posted.
        (await http.PostAsync($"/clients/{clientId}/items/{item.Item.Id}/deactivate", null)).EnsureSuccessStatusCode();
        (await http.PostAsync($"/clients/{clientId}/items/{item.Item.Id}/reactivate", null)).EnsureSuccessStatusCode();

        await ApproveBySourceRefAsync(http, approver, clientId, receipt.Movement.Id);

        HttpResponseMessage blocked = await http.PostAsync($"/clients/{clientId}/items/{item.Item.Id}/deactivate", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        HttpResponseMessage issueResponse = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Issue, 5m, null, new DateOnly(2026, 1, 2), null));
        issueResponse.EnsureSuccessStatusCode();
        StockMovementView issue = (await issueResponse.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, issue.Movement.Id);

        HttpResponseMessage deactivated = await http.PostAsync($"/clients/{clientId}/items/{item.Item.Id}/deactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);
    }

    [Fact]
    public async Task A_member_without_inventory_write_cannot_create_but_can_read()
    {
        // Auditor holds inventory.read but NOT inventory.write.
        (Guid clientId, HttpClient auditor) = await fixture.SeedClientAsync(role: LedgerRole.Auditor);

        HttpResponseMessage create = await auditor.PostAsJsonAsync($"/clients/{clientId}/items", Widget());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        HttpResponseMessage list = await auditor.GetAsync($"/clients/{clientId}/items");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task A_client_not_entitled_to_inventory_is_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(enabledModules: []);
        HttpResponseMessage response = await http.PostAsJsonAsync($"/clients/{clientId}/items", Widget());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Editing_a_deactivated_item_returns_409_until_reactivated()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller by default
        await SetUpChartAsync(http, clientId);

        ItemView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/items", Widget()))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ItemView>())!;
        Guid itemId = created.Item.Id;
        (await http.PostAsync($"/clients/{clientId}/items/{itemId}/deactivate", null)).EnsureSuccessStatusCode();

        HttpResponseMessage edit = await http.PutAsJsonAsync($"/clients/{clientId}/items/{itemId}", Widget() with { Name = "Renamed" });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);

        (await http.PostAsync($"/clients/{clientId}/items/{itemId}/reactivate", null)).EnsureSuccessStatusCode();
        HttpResponseMessage edit2 = await http.PutAsJsonAsync($"/clients/{clientId}/items/{itemId}", Widget() with { Name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, edit2.StatusCode);
    }

    [Fact]
    public async Task Reactivating_an_active_item_returns_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        ItemView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/items", Widget()))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ItemView>())!;
        HttpResponseMessage reactivate = await http.PostAsync($"/clients/{clientId}/items/{created.Item.Id}/reactivate", null);
        Assert.Equal(HttpStatusCode.Conflict, reactivate.StatusCode);
    }

    [Fact]
    public void ItemStatus_serializes_as_string() =>
        Assert.Contains("\"Active\"", System.Text.Json.JsonSerializer.Serialize(ItemStatus.Active));
}
