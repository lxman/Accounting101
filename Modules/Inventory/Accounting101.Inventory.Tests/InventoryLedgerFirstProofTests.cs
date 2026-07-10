using System.Net;
using System.Net.Http.Json;
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Inventory.Tests;

/// <summary>The merge-gate proof suite for the Inventory ledger-first slice: value is a <c>{Item}</c>
/// ledger fold and quantity is a projection over movement documents, both gated on the SAME posted-vs-pending
/// rule (writes read pending-inclusive, reads read posted-only). Every fact here is exercised end-to-end
/// through the real engine host (not the in-memory fake in <see cref="ItemValuationServiceTests"/>), so it
/// proves the gate for real rather than simulating it — a movement's effect on <see cref="ItemView"/> truly
/// depends on whether its spawned entry has been approved, and the guard against over-issue and the
/// module-entry-guard both run against real GL enforcement.</summary>
public sealed class InventoryLedgerFirstProofTests(InventoryHostFixture fixture) : IClassFixture<InventoryHostFixture>
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
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = requiredDimensions }))
            .EnsureSuccessStatusCode();

    private static async Task<ItemView> CreateItemAsync(HttpClient http, Guid clientId, SaveItemRequest req) =>
        (await (await http.PostAsJsonAsync($"/clients/{clientId}/items", req)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<ItemView>())!;

    /// <summary>Approves every PendingApproval entry spawned for the given sourceRef. Mirrors
    /// MovementReceiptE2eTests/MovementVoidE2eTests.ApproveBySourceRefAsync.</summary>
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

    /// <summary>The raw {Item} ledger fold on the Inventory Asset account. Inventory Asset is a
    /// debit-normal ASSET, so the fold reads the item's value directly POSITIVE — no negation (unlike a
    /// credit-normal control account, which would need <c>-fold</c>). Defaults to 0 when the item carries
    /// no on-the-books line at all.</summary>
    private async Task<decimal> ItemFoldAsync(HttpClient http, Guid clientId, Guid itemId)
    {
        SubledgerResponse fold = (await http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?account={fixture.InventoryAssetAccountId}&dimension=Item"))!;
        SubledgerLineResponse? line = fold.Lines.SingleOrDefault(l => l.DimensionValue == itemId);
        return line?.Balance ?? 0m;
    }

    [Fact]
    public async Task Inventory_account_rejects_an_untagged_line()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await PutAccountAsync(http, clientId, fixture.InventoryAssetAccountId, "1400", "Inventory", "Asset", ["Item"]);
        await PutAccountAsync(http, clientId, fixture.GrniClearingAccountId, "2050", "GRNI", "Liability");

        PostEntryRequest untagged = new(null, new DateOnly(2026, 6, 30), "R", "m",
        [
            new PostLineRequest(fixture.InventoryAssetAccountId, "Debit", 100m),   // no {Item}
            new PostLineRequest(fixture.GrniClearingAccountId, "Credit", 100m),
        ]);

        HttpResponseMessage response = await http.PostAsJsonAsync($"/clients/{clientId}/entries", untagged);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>Receipt then Issue: after both spawned entries are approved, the item's folded on-hand,
    /// value, and average reflect the expected weighted-average across two receipts at different unit
    /// costs, and the Issue costs at that blended average rather than either lot's individual cost. The
    /// value side is also cross-checked against the raw {Item} ledger fold, not just ItemView, to prove the
    /// read model really is derived from the ledger and not a stored mirror.</summary>
    [Fact]
    public async Task Receipt_then_issue_folds_to_the_expected_weighted_average_after_approval()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (holds inventory.write)
        await SetUpChartAsync(http, clientId);
        HttpClient approver = await SeedApproverAsync(clientId);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        // Lot 1: 10 @ $2 = $20.
        HttpResponseMessage receipt1 = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 10), "Lot 1"));
        receipt1.EnsureSuccessStatusCode();
        StockMovementView r1 = (await receipt1.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, r1.Movement.Id);

        // Lot 2: 10 @ $4 = $40. Blended: 20 units / $60 => avg $3.
        HttpResponseMessage receipt2 = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 4m, new DateOnly(2026, 1, 15), "Lot 2"));
        receipt2.EnsureSuccessStatusCode();
        StockMovementView r2 = (await receipt2.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, r2.Movement.Id);

        ItemView afterReceipts = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(20m, afterReceipts.Item.OnHandQuantity);
        Assert.Equal(60m, afterReceipts.Item.TotalValue);
        Assert.Equal(3m, afterReceipts.AverageUnitCost);

        // Issue 5 units — costed at the blended average ($3), not either individual lot's cost.
        HttpResponseMessage issue = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Issue, 5m, null, new DateOnly(2026, 1, 20), "Issue"));
        issue.EnsureSuccessStatusCode();
        StockMovementView issueView = (await issue.Content.ReadFromJsonAsync<StockMovementView>())!;
        Assert.Equal(3m, issueView.Movement.AppliedUnitCost);
        Assert.Equal(15m, issueView.Movement.ExtendedCost);
        await ApproveBySourceRefAsync(http, approver, clientId, issueView.Movement.Id);

        ItemView afterIssue = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(15m, afterIssue.Item.OnHandQuantity);
        Assert.Equal(45m, afterIssue.Item.TotalValue);
        Assert.Equal(3m, afterIssue.AverageUnitCost);

        // Cross-check: the raw {Item} ledger fold agrees with ItemView's TotalValue — the read model is
        // the fold, not an independently-maintained mirror that merely happens to agree.
        Assert.Equal(afterIssue.Item.TotalValue, await ItemFoldAsync(http, clientId, item.Item.Id));
    }

    /// <summary>Reads are posted-only: a just-recorded receipt leaves GET /items/{id} at zero on-hand/value
    /// until its spawned entry is approved — proving the shared gate for real against the engine (the fake
    /// in ItemValuationServiceTests.Pending_movement_is_posted_only_invisible_but_write_visible simulates
    /// this; this test proves it end-to-end).</summary>
    [Fact]
    public async Task Reads_are_posted_only_a_just_recorded_movement_leaves_on_hand_at_zero_until_approved()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        HttpClient approver = await SeedApproverAsync(clientId);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage receipt = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 15), "Initial receipt"));
        receipt.EnsureSuccessStatusCode();
        StockMovementView movement = (await receipt.Content.ReadFromJsonAsync<StockMovementView>())!;

        // Not yet approved — the posted-only read sees nothing, even though the movement document and its
        // PendingApproval entry both already exist.
        ItemView beforeApproval = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(0m, beforeApproval.Item.OnHandQuantity);
        Assert.Equal(0m, beforeApproval.Item.TotalValue);
        Assert.Equal(0m, await ItemFoldAsync(http, clientId, item.Item.Id));

        await ApproveBySourceRefAsync(http, approver, clientId, movement.Movement.Id);

        ItemView afterApproval = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(10m, afterApproval.Item.OnHandQuantity);
        Assert.Equal(20m, afterApproval.Item.TotalValue);
        Assert.Equal(20m, await ItemFoldAsync(http, clientId, item.Item.Id));
    }

    /// <summary>Block-negative is enforced against the PENDING-INCLUSIVE fold, not just the posted-only
    /// one: a receipt of 10 (approved) followed by an issue of 6 left unapproved already reserves those 6
    /// units, so a second issue of 5 — which would be fine against the posted-only on-hand of 10, but not
    /// against the pending-inclusive on-hand of 4 — is rejected with 409 before either persistence or a
    /// second entry is posted. This proves §2.4's "writes read pending-inclusive" half of the shared gate
    /// end-to-end (the unit-level version is
    /// <see cref="ItemValuationServiceTests.Pending_movement_is_posted_only_invisible_but_write_visible"/>
    /// combined with the guard in <c>InventoryMovementService.RecordAsync</c>); the plain over-issue-against-
    /// posted case is already covered by
    /// <see cref="MovementIssueE2eTests.Issue_exceeding_on_hand_is_rejected_with_409_and_leaves_the_item_unchanged"/>.</summary>
    [Fact]
    public async Task Issue_is_blocked_against_the_pending_inclusive_on_hand_not_just_the_posted_on_hand()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        HttpClient approver = await SeedApproverAsync(clientId);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage receipt = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 10), "Initial receipt"));
        receipt.EnsureSuccessStatusCode();
        StockMovementView r = (await receipt.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, r.Movement.Id);

        // Issue 1: 6 units, deliberately left unapproved — posted-only on-hand still reads 10, but the
        // pending-inclusive on-hand the RecordAsync guard checks is now 4.
        HttpResponseMessage issue1 = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Issue, 6m, null, new DateOnly(2026, 1, 20), "First issue"));
        issue1.EnsureSuccessStatusCode();

        ItemView postedOnlyAfterIssue1 = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(10m, postedOnlyAfterIssue1.Item.OnHandQuantity); // issue1 not yet on the books

        // Issue 2: 5 units — fine against posted-only 10, but exceeds pending-inclusive 4. Rejected.
        HttpResponseMessage issue2 = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Issue, 5m, null, new DateOnly(2026, 1, 21), "Second issue"));
        Assert.Equal(HttpStatusCode.Conflict, issue2.StatusCode);
        string body = await issue2.Content.ReadAsStringAsync();
        Assert.Contains("below zero", body, StringComparison.OrdinalIgnoreCase);

        // The rejected attempt left the posted-only fold untouched.
        ItemView afterRejectedIssue2 = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(10m, afterRejectedIssue2.Item.OnHandQuantity);
        Assert.Equal(20m, afterRejectedIssue2.Item.TotalValue);
    }

    /// <summary>Void auto-rollback: voiding the latest (already-approved) movement reverses its entry and,
    /// once the reversal is itself approved, the item's folded on-hand and value return to their
    /// pre-movement state — and the raw {Item} ledger fold agrees, so the movement has genuinely dropped
    /// out of the fold rather than merely having a module-side counter decremented back.</summary>
    [Fact]
    public async Task Void_of_the_latest_movement_rolls_the_folded_on_hand_and_value_back_and_drops_out_of_the_ledger_fold()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (inventory.write + gl.reverse)
        await SetUpChartAsync(http, clientId);
        HttpClient approver = await SeedApproverAsync(clientId);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage receipt = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 10), "Initial receipt"));
        receipt.EnsureSuccessStatusCode();
        StockMovementView r = (await receipt.Content.ReadFromJsonAsync<StockMovementView>())!;
        await ApproveBySourceRefAsync(http, approver, clientId, r.Movement.Id);

        ItemView beforeVoid = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(10m, beforeVoid.Item.OnHandQuantity);
        Assert.Equal(20m, beforeVoid.Item.TotalValue);
        Assert.Equal(20m, await ItemFoldAsync(http, clientId, item.Item.Id));

        // The receipt's entry is already Posted, so void takes the REVERSE branch (a new entry with
        // ReversalOf set), left PendingApproval until approved.
        HttpResponseMessage voidResponse = await http.PostAsJsonAsync(
            $"/clients/{clientId}/movements/{r.Movement.Id}/void", new VoidReasonRequest("year-end correction"));
        Assert.Equal(HttpStatusCode.OK, voidResponse.StatusCode);
        await ApproveBySourceRefAsync(http, approver, clientId, r.Movement.Id);

        ItemView afterVoid = (await http.GetFromJsonAsync<ItemView>($"/clients/{clientId}/items/{item.Item.Id}"))!;
        Assert.Equal(0m, afterVoid.Item.OnHandQuantity);
        Assert.Equal(0m, afterVoid.Item.TotalValue);

        // The movement has dropped out of the raw {Item} ledger fold too — not just ItemView's derived read.
        Assert.Equal(0m, await ItemFoldAsync(http, clientId, item.Item.Id));

        StockMovementView voided = (await http.GetFromJsonAsync<StockMovementView>(
            $"/clients/{clientId}/movements/{r.Movement.Id}"))!;
        Assert.Equal(MovementStatus.Void, voided.Movement.Status);
    }

    /// <summary>Inventory-scoped guard proof (mirrors FixedAssets/Payroll/Cash's
    /// <c>Raw_gl_reverse_of_a_..._entry_is_refused_by_the_guard</c>): a movement's spawned entry is stamped
    /// <c>ViaModule="inventory"</c>, so once it is Posted a raw <c>POST /entries/{id}/reverse</c> against the
    /// journal directly — carrying no module credential, even though the caller (Controller) holds raw
    /// <c>gl.reverse</c> — is refused with 409. The entry may only be reversed through the module's own void
    /// surface (<c>POST /movements/{id}/void</c>), never the raw journal.</summary>
    [Fact]
    public async Task Raw_gl_reverse_of_a_movement_entry_is_refused_by_the_guard()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (inventory.write + gl.reverse)
        await SetUpChartAsync(http, clientId);
        HttpClient approver = await SeedApproverAsync(clientId);

        ItemView item = await CreateItemAsync(http, clientId, new SaveItemRequest("SKU1", "Widget", null, "each"));

        HttpResponseMessage receipt = await http.PostAsJsonAsync($"/clients/{clientId}/movements",
            new RecordMovementRequest(item.Item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026, 1, 10), "Initial receipt"));
        receipt.EnsureSuccessStatusCode();
        StockMovementView r = (await receipt.Content.ReadFromJsonAsync<StockMovementView>())!;

        EntryResponse entry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={r.Movement.Id}"))!);
        Assert.Equal("inventory", entry.ViaModule);
        (await approver.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();

        // Raw reverse — a plain journal request carrying no module credential — is refused: correct it
        // through the module's void surface instead.
        HttpResponseMessage rawReverse = await http.PostAsJsonAsync(
            $"/clients/{clientId}/entries/{entry.Id}/reverse",
            new ReverseRequest(new DateOnly(2026, 2, 1), "raw reversal attempt"));
        Assert.Equal(HttpStatusCode.Conflict, rawReverse.StatusCode);
    }
}
