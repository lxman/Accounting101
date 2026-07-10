using System.Net;
using System.Net.Http.Json;
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

/// <summary>Proves the receipt path end-to-end through the real host: a receipt re-blends the item's
/// valuation and posts one balanced PendingApproval entry Dr Inventory / Cr GRNI stamped
/// ViaModule="inventory"; a read-only member is forbidden; a non-entitled client is forbidden.</summary>
public sealed class MovementReceiptE2eTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
{
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

    private static async Task<ItemView> CreateItemAsync(HttpClient http, Guid clientId, SaveItemRequest req) =>
        (await (await http.PostAsJsonAsync($"/clients/{clientId}/items", req)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<ItemView>())!;

    /// <summary>Approves every PendingApproval entry spawned for the given sourceRef. Mirrors
    /// MovementVoidE2eTests.ApproveBySourceRefAsync / SettlementScenario.ApproveBySourceRefAsync.</summary>
    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Receipt_posts_one_pending_entry_via_inventory_and_updates_the_item()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (holds inventory.write)
        await SetUpChartAsync(http, clientId);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "Initial receipt"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        StockMovementView movement = (await created.Content.ReadFromJsonAsync<StockMovementView>())!;
        Assert.NotNull(movement.Movement.Number);

        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={movement.Movement.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal("inventory", entry.ViaModule);
        Assert.Equal("PendingApproval", entry.Posting);
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(20m, entry.Lines.Single(l => l.AccountId == fixture.InventoryAssetAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(20m, entry.Lines.Single(l => l.AccountId == fixture.GrniClearingAccountId && l.Direction == "Credit").Amount);

        // The item read is the POSTED-ONLY fold: the movement's own effect is only visible via GET once its
        // spawned entry is approved (a pending receipt does not yet move the read-side valuation).
        Guid approverUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(approverUserId, clientId, LedgerRole.Approver);
        HttpClient approver = fixture.ClientFor(approverUserId, "Acme Approver", LedgerRole.Approver);
        await ApproveBySourceRefAsync(http, approver, clientId, movement.Movement.Id);

        ItemView after = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(10m, after.Item.OnHandQuantity);
        Assert.Equal(20m, after.Item.TotalValue);
        Assert.Equal(2m, after.AverageUnitCost);
    }

    /// <summary>Capability denial (not a wrong-client denial): the Auditor is seeded on the SAME client
    /// where the chart/item live, holding inventory.read but not inventory.write.</summary>
    [Fact]
    public async Task A_member_without_write_cannot_record_a_movement()
    {
        (Guid clientId, HttpClient controller) = await fixture.SeedClientAsync(); // Controller sets up chart
        await SetUpChartAsync(controller, clientId);
        ItemView item = await CreateItemAsync(controller, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        Guid auditorUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(auditorUserId, clientId, LedgerRole.Auditor);
        HttpClient auditor = fixture.ClientFor(auditorUserId, "Acme Auditor", LedgerRole.Auditor);

        HttpResponseMessage response = await auditor.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), null));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_client_not_entitled_to_inventory_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(enabledModules: []); // no inventory entitlement
        HttpResponseMessage response = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(Guid.NewGuid(), MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), null));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
