using System.Net;
using System.Net.Http.Json;
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Inventory.Tests;

/// <summary>The retry-key contract on POST /movements: absent key behaves as before, a replayed key
/// returns the stored movement without creating a second one, and a key reused for different financial
/// content is a 422 relayed from the ledger.
///
/// Plus the one this module needs that the other twelve endpoints do not: a replay must not double-count
/// the item's VALUATION. Inventory's record path is the only retry-key endpoint that reads mutable state
/// (the pending-inclusive fold) to derive what it posts, so the assertion reads on-hand/value back from
/// the item endpoint — a movement-list assertion alone would pass even with the double-count present.</summary>
public sealed class MovementRetryKeyTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
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

    /// <summary>Approves every PendingApproval entry spawned by EVERY movement recorded against the item —
    /// the item read is a POSTED-ONLY fold, so nothing shows up in the valuation until its entry is
    /// approved. Enumerating the movements rather than approving one known sourceRef is deliberate: a
    /// double-posting replay would spawn a SECOND movement under an id the test never chose, and approving
    /// only the retry key would leave that entry pending — hiding the very double-count under test.
    /// Mirrors MovementReceiptE2eTests.ApproveBySourceRefAsync otherwise.</summary>
    private async Task ApproveEverythingForItemAsync(HttpClient reader, Guid clientId, Guid itemId)
    {
        Guid approverUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(approverUserId, clientId, LedgerRole.Approver);
        HttpClient approver = fixture.ClientFor(approverUserId, "Acme Approver", LedgerRole.Approver);

        PagedResponse<StockMovementView> movements = (await reader.GetFromJsonAsync<PagedResponse<StockMovementView>>(
            $"/clients/{clientId}/movements?itemId={itemId}&includeVoided=true"))!;

        foreach (StockMovementView m in movements.Items)
        {
            EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
                $"/clients/{clientId}/entries?sourceRef={m.Movement.Id}"))!;
            foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
                (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Without_a_key_the_post_creates_as_before()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage resp = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "Initial receipt"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        StockMovementView movement = (await resp.Content.ReadFromJsonAsync<StockMovementView>())!;
        Assert.NotNull(movement.Movement.Number);
    }

    [Fact]
    public async Task A_replayed_key_returns_the_stored_movement_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        Guid key = Guid.NewGuid();
        RecordMovementRequest req = new(
            item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "Initial receipt", key);

        HttpResponseMessage first = await http.PostAsJsonAsync($"/clients/{clientId}/movements", req);
        HttpResponseMessage second = await http.PostAsJsonAsync($"/clients/{clientId}/movements", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        StockMovementView a = (await first.Content.ReadFromJsonAsync<StockMovementView>())!;
        StockMovementView b = (await second.Content.ReadFromJsonAsync<StockMovementView>())!;
        Assert.Equal(key, a.Movement.Id);
        Assert.Equal(a.Movement.Id, b.Movement.Id);
        Assert.Equal(a.Movement.Number, b.Movement.Number);   // the replay burned no second sequence number

        PagedResponse<StockMovementView> all = (await http.GetFromJsonAsync<PagedResponse<StockMovementView>>(
            $"/clients/{clientId}/movements?itemId={item.Item.Id}"))!;
        Assert.Single(all.Items);
    }

    /// <summary>Divergence is arbitrated by the ledger on the entry's financial content. EffectiveDate is
    /// part of that content (EntryComparison.SameFinancialContent), so a same-key retry re-dated to a
    /// different day is a reused operation id, not a replay.</summary>
    [Fact]
    public async Task The_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        Guid key = Guid.NewGuid();
        (await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "Initial receipt", key)))
            .EnsureSuccessStatusCode();

        HttpResponseMessage divergent = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 2, 20), "Initial receipt", key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }

    /// <summary>THE load-bearing test for this endpoint. On-hand and total value are derived from the ledger
    /// fold + movement projection, so a replay that re-ran the record path — a second movement document, a
    /// second posted entry, or a re-applied effect — would show 20 units / $40 instead of 10 / $20. The
    /// assertion deliberately reads the ITEM (the valuation), not the movement list.</summary>
    [Fact]
    public async Task A_replayed_movement_does_not_double_count_the_valuation()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        Guid key = Guid.NewGuid();
        RecordMovementRequest receipt = new(
            item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "Initial receipt", key);

        // Both calls must succeed; WHICH success code they return is the subject of the test above, not of
        // this one — asserting it here would short-circuit before the valuation ever gets read.
        HttpResponseMessage first = await http.PostAsJsonAsync($"/clients/{clientId}/movements", receipt);
        HttpResponseMessage second = await http.PostAsJsonAsync($"/clients/{clientId}/movements", receipt);
        Assert.True(first.IsSuccessStatusCode, $"first post: {first.StatusCode}");
        Assert.True(second.IsSuccessStatusCode, $"second post: {second.StatusCode}");

        // The item read is the posted-only fold: approve everything the two calls spawned.
        await ApproveEverythingForItemAsync(http, clientId, item.Item.Id);

        ItemView after = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(10m, after.Item.OnHandQuantity);   // ONE receipt of 10, not two
        Assert.Equal(20m, after.Item.TotalValue);       // ONE receipt at $2, not two
        Assert.Equal(2m, after.AverageUnitCost);
    }
}
