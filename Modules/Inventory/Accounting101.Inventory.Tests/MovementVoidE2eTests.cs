using System.Net;
using System.Net.Http.Json;
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

/// <summary>Proves LIFO void end-to-end through the real host: voiding the latest movement for an item
/// reverses/withdraws its spawned entry and restores the item's pre-movement valuation; only the latest
/// movement for an item may be voided; voiding an already-void or unknown movement is rejected.</summary>
public sealed class MovementVoidE2eTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
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
    /// SettlementScenario.ApproveBySourceRefAsync in the Receivables tests.</summary>
    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    /// <summary>Every OTHER void test in this file leaves the spawned entry PendingApproval, so
    /// <c>InventoryMovementService.VoidAsync</c> always takes its WITHDRAW branch (<c>ledger.VoidAsync</c>) —
    /// that branch's valuation-restore is proven at the unit level by
    /// <c>InventoryMovementServiceTests.Void_latest_restores_valuation_and_reverses_entry</c> (which reads
    /// the item straight from its in-memory store, not through a posted-only fold). This test approves the
    /// receipt's spawned entry first — flipping it to Posted — so voiding the movement instead takes the
    /// REVERSE branch (<c>ledger.ReverseAsync</c>), AND approves the resulting reversal entry too, since the
    /// item read here is now the POSTED-ONLY ledger fold: a reversal that is itself still PendingApproval
    /// does not yet roll the value back for a posted-only reader (see
    /// ItemValuationServiceTests.Reversal_gates_on_posting_keeping_value_and_quantity_coherent). The
    /// discriminator vs. withdraw is that reversal creates a NEW entry with <c>ReversalOf</c> set on the
    /// original, whereas withdraw does not.</summary>
    [Fact]
    public async Task Void_of_movement_with_an_approved_entry_reverses_it_and_restores_valuation()
    {
        (Guid clientId, HttpClient controller) = await fixture.SeedClientAsync(); // Controller (inventory.write + gl.reverse)
        await SetUpChartAsync(controller, clientId);

        ItemView item = await CreateItemAsync(controller, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage receiptResponse = await controller.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 10), "Initial receipt"));
        receiptResponse.EnsureSuccessStatusCode();
        StockMovementView receipt = (await receiptResponse.Content.ReadFromJsonAsync<StockMovementView>())!;

        Guid approverUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(approverUserId, clientId, LedgerRole.Approver);
        HttpClient approver = fixture.ClientFor(approverUserId, "Acme Approver", LedgerRole.Approver);

        // Approve the receipt's spawned entry — Posting flips PendingApproval -> Posted.
        await ApproveBySourceRefAsync(controller, approver, clientId, receipt.Movement.Id);

        // Void the now-posted receipt movement — exercises the REVERSE branch.
        HttpResponseMessage voidResponse = await controller.PostAsJsonAsync(
            $"/clients/{clientId}/movements/{receipt.Movement.Id}/void", new VoidReasonRequest("year-end correction"));
        Assert.Equal(HttpStatusCode.OK, voidResponse.StatusCode);

        // The void's reversal entry is itself PendingApproval until approved — approve it so the
        // posted-only item read reflects the full roll-back.
        await ApproveBySourceRefAsync(controller, approver, clientId, receipt.Movement.Id);

        ItemView afterVoid = (await controller.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(0m, afterVoid.Item.OnHandQuantity);
        Assert.Equal(0m, afterVoid.Item.TotalValue);

        StockMovementView voidedReceipt = (await controller.GetFromJsonAsync<StockMovementView>(
            $"/clients/{clientId}/movements/{receipt.Movement.Id}"))!;
        Assert.Equal(MovementStatus.Void, voidedReceipt.Movement.Status);

        EntryResponse[] afterEntries = (await controller.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={receipt.Movement.Id}"))!;
        Assert.Contains(afterEntries, e => e.ReversalOf is not null);
    }

    /// <summary>Proves LIFO unwind of valuation across TWO sequential voids via the posted-only item read:
    /// every entry along the way (receipt, issue, and both voids' reversals) is approved so the fold is
    /// meaningful at each checkpoint — an unapproved entry is invisible to GET, so leaving anything pending
    /// would make every intermediate assertion trivially (and meaninglessly) zero. The WITHDRAW branch is
    /// covered separately (see the class doc-comment on the sibling REVERSE-branch test above).</summary>
    [Fact]
    public async Task Void_of_latest_movement_reverses_entry_and_restores_valuation_then_LIFO_unwinds_to_zero()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (holds inventory.write)
        await SetUpChartAsync(http, clientId);

        Guid approverUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(approverUserId, clientId, LedgerRole.Approver);
        HttpClient approver = fixture.ClientFor(approverUserId, "Acme Approver", LedgerRole.Approver);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage receiptResponse = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 10), "Initial receipt"));
        receiptResponse.EnsureSuccessStatusCode();
        StockMovementView receipt = (await receiptResponse.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, receipt.Movement.Id);

        HttpResponseMessage issueResponse = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Issue, 4m, null, new DateOnly(2026, 1, 20), "Issue to production"));
        issueResponse.EnsureSuccessStatusCode();
        StockMovementView issue = (await issueResponse.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, issue.Movement.Id);

        ItemView afterIssue = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(6m, afterIssue.Item.OnHandQuantity);
        Assert.Equal(12m, afterIssue.Item.TotalValue);

        // item now (6, 12). Void the issue (latest) → back to (10, 20) — both entries are Posted, so this
        // takes the REVERSE branch; approve the reversal too so the posted-only read sees the roll-back.
        HttpResponseMessage voidIssueResponse = await http.PostAsJsonAsync(
            $"/clients/{clientId}/movements/{issue.Movement.Id}/void", new VoidReasonRequest("oops"));
        Assert.Equal(HttpStatusCode.OK, voidIssueResponse.StatusCode);
        await ApproveBySourceRefAsync(http, approver, clientId, issue.Movement.Id);

        ItemView afterVoidIssue = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(10m, afterVoidIssue.Item.OnHandQuantity);
        Assert.Equal(20m, afterVoidIssue.Item.TotalValue);

        StockMovementView voidedIssue = (await http.GetFromJsonAsync<StockMovementView>(
            $"/clients/{clientId}/movements/{issue.Movement.Id}"))!;
        Assert.Equal(MovementStatus.Void, voidedIssue.Movement.Status);

        // The receipt is now latest for the item — void it too → item back to (0, 0).
        HttpResponseMessage voidReceiptResponse = await http.PostAsJsonAsync(
            $"/clients/{clientId}/movements/{receipt.Movement.Id}/void", new VoidReasonRequest("oops"));
        Assert.Equal(HttpStatusCode.OK, voidReceiptResponse.StatusCode);
        await ApproveBySourceRefAsync(http, approver, clientId, receipt.Movement.Id);

        ItemView afterVoidReceipt = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(0m, afterVoidReceipt.Item.OnHandQuantity);
        Assert.Equal(0m, afterVoidReceipt.Item.TotalValue);

        // Voiding the already-void issue again → 409.
        HttpResponseMessage voidAgainResponse = await http.PostAsJsonAsync(
            $"/clients/{clientId}/movements/{issue.Movement.Id}/void", new VoidReasonRequest("oops"));
        Assert.Equal(HttpStatusCode.Conflict, voidAgainResponse.StatusCode);

        // Voiding an unknown movement → 404.
        HttpResponseMessage voidMissingResponse = await http.PostAsJsonAsync(
            $"/clients/{clientId}/movements/{Guid.NewGuid()}/void", new VoidReasonRequest("oops"));
        Assert.Equal(HttpStatusCode.NotFound, voidMissingResponse.StatusCode);
    }
}
