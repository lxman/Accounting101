using System.Net;
using System.Net.Http.Json;
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

/// <summary>Proves the adjustment path end-to-end through the real host: an overage (positive signed
/// quantity) behaves like a receipt at the supplied unit cost and posts one balanced PendingApproval entry
/// Dr Inventory / Cr InventoryAdjustment stamped ViaModule="inventory"; a shrinkage (negative signed
/// quantity) costs at the item's current average and posts Dr InventoryAdjustment / Cr Inventory; a
/// shrinkage that would drive on-hand below zero is rejected with 409 and leaves the item untouched.</summary>
public sealed class MovementAdjustmentE2eTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
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

    /// <summary>Approves every PendingApproval entry spawned for the given sourceRef — the item read is a
    /// POSTED-ONLY fold, so a movement's effect is invisible via GET until its entry is approved.</summary>
    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> SeedApproverAsync(Guid clientId)
    {
        Guid approverUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(approverUserId, clientId, LedgerRole.Approver);
        return fixture.ClientFor(approverUserId, "Acme Approver", LedgerRole.Approver);
    }

    [Fact]
    public async Task Overage_posts_one_pending_inventory_debit_and_updates_the_item()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (holds inventory.write)
        await SetUpChartAsync(http, clientId);
        HttpClient approver = await SeedApproverAsync(clientId);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage receiptResponse = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 3m, new DateOnly(2026, 1, 15), "Initial receipt"));
        receiptResponse.EnsureSuccessStatusCode();
        StockMovementView receipt = (await receiptResponse.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, receipt.Movement.Id);

        HttpResponseMessage adjusted = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Adjustment, 5m, 4m, new DateOnly(2026, 1, 20), "Cycle count overage"));
        Assert.Equal(HttpStatusCode.Created, adjusted.StatusCode);
        StockMovementView movement = (await adjusted.Content.ReadFromJsonAsync<StockMovementView>())!;
        Assert.NotNull(movement.Movement.Number);

        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={movement.Movement.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal("inventory", entry.ViaModule);
        Assert.Equal("PendingApproval", entry.Posting);
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(20m, entry.Lines.Single(l => l.AccountId == fixture.InventoryAssetAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(20m, entry.Lines.Single(l => l.AccountId == fixture.InventoryAdjustmentAccountId && l.Direction == "Credit").Amount);

        await ApproveBySourceRefAsync(http, approver, clientId, movement.Movement.Id);

        ItemView after = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(15m, after.Item.OnHandQuantity);
        Assert.Equal(50m, after.Item.TotalValue);
    }

    [Fact]
    public async Task Shrinkage_posts_one_pending_inventory_credit_and_updates_the_item()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        HttpClient approver = await SeedApproverAsync(clientId);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage receiptResponse = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 3m, new DateOnly(2026, 1, 15), "Initial receipt"));
        receiptResponse.EnsureSuccessStatusCode();
        StockMovementView receipt = (await receiptResponse.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, receipt.Movement.Id);

        HttpResponseMessage adjusted = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Adjustment, -4m, null, new DateOnly(2026, 1, 20), "Cycle count shrinkage"));
        Assert.Equal(HttpStatusCode.Created, adjusted.StatusCode);
        StockMovementView movement = (await adjusted.Content.ReadFromJsonAsync<StockMovementView>())!;
        Assert.NotNull(movement.Movement.Number);

        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={movement.Movement.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal("inventory", entry.ViaModule);
        Assert.Equal("PendingApproval", entry.Posting);
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(12m, entry.Lines.Single(l => l.AccountId == fixture.InventoryAdjustmentAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(12m, entry.Lines.Single(l => l.AccountId == fixture.InventoryAssetAccountId && l.Direction == "Credit").Amount);

        await ApproveBySourceRefAsync(http, approver, clientId, movement.Movement.Id);

        ItemView after = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(6m, after.Item.OnHandQuantity);
        Assert.Equal(18m, after.Item.TotalValue);
    }

    [Fact]
    public async Task Shrinkage_beyond_on_hand_is_rejected_with_409_and_leaves_the_item_unchanged()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        HttpClient approver = await SeedApproverAsync(clientId);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage receiptResponse = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 3m, new DateOnly(2026, 1, 15), "Initial receipt"));
        receiptResponse.EnsureSuccessStatusCode();
        StockMovementView receipt = (await receiptResponse.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, receipt.Movement.Id);

        // Rejected before any persistence (pending-inclusive fold at compute time — no approval needed for
        // the guard itself); the item's read-side (posted-only) valuation is therefore still just the receipt.
        HttpResponseMessage rejected = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Adjustment, -999m, null, new DateOnly(2026, 1, 21), null));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        string body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("below zero", body, StringComparison.OrdinalIgnoreCase);

        ItemView after = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(10m, after.Item.OnHandQuantity);
        Assert.Equal(30m, after.Item.TotalValue);
    }
}
