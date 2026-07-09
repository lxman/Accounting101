using System.Net;
using System.Net.Http.Json;
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

public sealed class InventoryEndpointsTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
{
    private static SaveItemRequest Widget(string sku = "SKU1") => new(sku, "Widget", "A widget", "each");

    [Fact]
    public async Task Create_list_get_update_deactivate_lifecycle()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

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
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsync($"/clients/{clientId}/items/{Guid.NewGuid()}/deactivate", null)).StatusCode);

        ItemView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/items", Widget())).Content.ReadFromJsonAsync<ItemView>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/clients/{clientId}/items/{created.Item.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/clients/{clientId}/items/{created.Item.Id}/deactivate", null)).StatusCode);
    }

    // The HasStock deactivate guard is exercised at the store level (ItemDocumentStoreTests) — reaching
    // IItemStore directly here would bypass FirmResolutionMiddleware (no HTTP request in scope) and throw.
    // A future movement-posting endpoint will give stock through HTTP once movements land.

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
