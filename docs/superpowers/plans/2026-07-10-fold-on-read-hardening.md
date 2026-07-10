# Fold-on-Read Error Relay (FixedAssets + Inventory) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Relay the engine's real status/reason (a clean 4xx) instead of an opaque 500 when a FixedAssets or Inventory list/detail read folds a misconfigured control account.

**Architecture:** Copy the Receivables/Payables precedent verbatim into FixedAssets and Inventory — a typed `LedgerClientException`, an `EnsureSuccessAsync` + `ReasonFrom` pair on the module's `HttpLedgerClient` (applied to READ methods only), and a relay catch on the four fold-reading endpoints. No engine change, no post-path change, no degrade-to-0.

**Tech Stack:** C# / .NET 8 minimal-API modules, xUnit + EphemeralMongo host fixtures (`WebApplicationFactory<Program>`), shared-Mongo test infra.

## Global Constraints

- **Read methods only.** Swap `EnsureSuccessStatusCode()` → `await EnsureSuccessAsync(...)` **only** on `GetSubledgerAsync`, `GetEntriesBySourceRefAsync`, and (Inventory) `GetEntriesBySourceRefsAsync`. Leave `PostAsync` / `ReverseAsync` / `VoidAsync` on `EnsureSuccessStatusCode()` unchanged (post-path relay gap is an explicit non-goal).
- **Verbatim precedent.** `LedgerClientException`, `EnsureSuccessAsync`, and `ReasonFrom` are byte-for-byte ports of the Receivables versions (namespace adjusted only). Do not "improve" them.
- **No degrade-to-0, no runtime guard, no startup validator, no shared-SDK consolidation, no UI change, no engine change.** Surface the fault; do not mask it.
- **Relay catch is verbatim AR/AP:** `catch (LedgerClientException ex) { return Results.Problem(ex.Reason, statusCode: ex.StatusCode); }`.
- Reference source (copy from): `Modules/Receivables/Accounting101.Receivables/LedgerClientException.cs` and `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs` (helpers at lines 108–175). Reference test shape: `Modules/Receivables/Accounting101.Receivables.Tests/LedgerErrorRelayE2eTests.cs`.
- Branch off master `a2be799`. Commit per task.

## File Structure

**Task 1 — FixedAssets:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/LedgerClientException.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`
- Create (test): `Modules/FixedAssets/Accounting101.FixedAssets.Tests/LedgerErrorRelayE2eTests.cs`

**Task 2 — Inventory:**
- Create: `Modules/Inventory/Accounting101.Inventory/LedgerClientException.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/InventoryEndpoints.cs`
- Create (test): `Modules/Inventory/Accounting101.Inventory.Tests/LedgerErrorRelayE2eTests.cs`

The two tasks are independent (separate modules, separate files). Task 1 establishes the exact port; Task 2 repeats it in Inventory. A reviewer could accept FA and reject Inventory independently, so they are separate tasks.

---

### Task 1: FixedAssets fold-read error relay

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/LedgerClientException.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/LedgerErrorRelayE2eTests.cs`

**Interfaces:**
- Consumes: engine `GET /clients/{id}/subledger` returns 422 when the named control account's `RequiredDimensions` is empty or omits the requested dimension (`LedgerEndpoints.cs:554,557`). FA folds `AccumulatedDepreciationAccountId` with dimension `"Asset"` on the read path (`FixedAssetsService.cs:55,62`).
- Produces: `Accounting101.FixedAssets.LedgerClientException(int statusCode, string reason)` with `int StatusCode` + `string Reason`; `HttpLedgerClient` read methods throw it on non-2xx; `GetAsset`/`ListAssets` relay it.

- [ ] **Step 1: Write the failing test**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/LedgerErrorRelayE2eTests.cs`. The chart is set up with the Accumulated Depreciation account as a **plain** `Asset` account (no `RequiredDimensions`), so the fold read on `GET /assets` and `GET /assets/{id}` triggers the engine's 422. Asset creation is a Reference-document POST that does not post to the ledger, so it succeeds despite the misconfig.

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Accounting101.FixedAssets.Tests;

/// <summary>A fold-on-read refusal is relayed with the engine's real status (a 4xx), not an opaque 500.
/// Exercised via the accumulated-depreciation fold: the Accumulated Depreciation account is configured
/// WITHOUT the "Asset" required dimension, so the engine's subledger validation refuses the fold read
/// (422, LedgerEndpoints.cs:554). The list and detail asset reads must relay that 422, not let the bare
/// EnsureSuccessStatusCode 500 escape — the exact /assets smoke crash, pinned.</summary>
public sealed class LedgerErrorRelayE2eTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    [Fact]
    public async Task Listing_assets_when_the_accum_account_lacks_its_dimension_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpMisconfiguredChartAsync(http, clientId);
        await CreateAssetAsync(http, clientId);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/assets");

        await AssertRelayed422(resp);
    }

    [Fact]
    public async Task Getting_one_asset_when_the_accum_account_lacks_its_dimension_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpMisconfiguredChartAsync(http, clientId);
        Guid assetId = await CreateAssetAsync(http, clientId);

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/assets/{assetId}");

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

    /// <summary>Every posting account is configured, but Accumulated Depreciation is a PLAIN Asset account
    /// (no RequiredDimensions), so the "Asset"-dimensioned fold read refuses with 422.</summary>
    private async Task SetUpMisconfiguredChartAsync(HttpClient http, Guid clientId)
    {
        await PutAccountAsync(http, clientId, fixture.DepreciationExpenseAccountId,     "6200", "Depreciation Expense",     "Expense");
        await PutAccountAsync(http, clientId, fixture.AccumulatedDepreciationAccountId, "1590", "Accumulated Depreciation", "Asset"); // NO RequiredDimensions — the misconfig
        await PutAccountAsync(http, clientId, fixture.AssetCostAccountId,        "1500", "Fixed Assets",     "Asset");
        await PutAccountAsync(http, clientId, fixture.DisposalProceedsAccountId, "1000", "Cash",             "Asset");
        await PutAccountAsync(http, clientId, fixture.GainOnDisposalAccountId,   "7100", "Gain on Disposal", "Revenue");
        await PutAccountAsync(http, clientId, fixture.LossOnDisposalAccountId,   "7200", "Loss on Disposal", "Expense");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type })).EnsureSuccessStatusCode();

    private static async Task<Guid> CreateAssetAsync(HttpClient http, Guid clientId)
    {
        AssetView view = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets",
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AssetView>())!;
        return view.Asset.Id;
    }
}
```

- [ ] **Step 2: Run the test to verify it fails (proves the bug)**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter LedgerErrorRelayE2eTests`
Expected: FAIL — both facts get `500 InternalServerError` (the bare `EnsureSuccessStatusCode` on the fold read escapes as 500), so `Assert.NotEqual(InternalServerError, ...)` fails.

- [ ] **Step 3: Create the typed exception**

Create `Modules/FixedAssets/Accounting101.FixedAssets/LedgerClientException.cs` (verbatim port; namespace only differs):

```csharp
namespace Accounting101.FixedAssets;

/// <summary>
/// A ledger call returned a non-success status. Carries the engine's HTTP status code and its reason
/// (the ProblemDetails <c>detail</c>, or the raw body) so the module can surface the real cause — a
/// closed-period 409, an unbalanced-entry 422, a fold-on-read dimension 422 — instead of letting it
/// escape as an opaque 500.
/// </summary>
public sealed class LedgerClientException(int statusCode, string reason)
    : Exception($"Ledger request failed ({statusCode}): {reason}")
{
    /// <summary>The HTTP status the engine returned (e.g. 409, 422).</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The engine's human-readable reason, suitable to relay to the caller.</summary>
    public string Reason { get; } = reason;
}
```

- [ ] **Step 4: Add the helpers + swap the read methods in `HttpLedgerClient`**

In `Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs`:

First, add the two `using` directives at the top (after `using System.Net.Http.Json;`):

```csharp
using System.Text;
using System.Text.Json;
```

Then swap `response.EnsureSuccessStatusCode();` → `await EnsureSuccessAsync(response, cancellationToken);` in exactly two methods — `GetEntriesBySourceRefAsync` (line 68) and `GetSubledgerAsync` (line 82). Leave `PostAsync`, `ReverseAsync`, `VoidAsync` unchanged.

Finally, add the two private helpers (verbatim port from Receivables `HttpLedgerClient.cs:108–175`) just before the `Forwarded` method:

```csharp
    /// <summary>
    /// Throw a typed <see cref="LedgerClientException"/> carrying the engine's status and reason on any
    /// non-success response, so a caller (and the module's endpoints) can relay the real cause instead of
    /// the bare <see cref="HttpRequestException"/> that <c>EnsureSuccessStatusCode</c> would throw.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new LedgerClientException((int)response.StatusCode, ReasonFrom(body, response));
    }

    /// <summary>
    /// Pull the best available reason from the response body.
    /// Priority: (1) <c>errors</c> map from ValidationProblemDetails (field-level messages flattened to
    /// <c>"field: msg; field: msg"</c>), (2) ProblemDetails <c>detail</c>, (3) raw body, (4) status phrase.
    /// The <c>errors</c> branch only fires when that property is a non-empty JSON object — plain
    /// ProblemDetails (409 freeze, 422 fold, etc.) that carry only <c>detail</c> are unaffected.
    /// </summary>
    private static string ReasonFrom(string body, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    // ValidationProblemDetails: flatten the `errors` map when present and non-empty.
                    if (root.TryGetProperty("errors", out JsonElement errors)
                        && errors.ValueKind == JsonValueKind.Object)
                    {
                        StringBuilder sb = new();
                        foreach (JsonProperty prop in errors.EnumerateObject())
                        {
                            if (sb.Length > 0) sb.Append("; ");
                            sb.Append(prop.Name).Append(": ");
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                sb.Append(string.Join(", ", prop.Value.EnumerateArray()
                                    .Select(m => m.GetString() ?? string.Empty)));
                            }
                            else
                            {
                                sb.Append(prop.Value.GetRawText().Trim('"'));
                            }
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }

                    // Plain ProblemDetails: use the `detail` field.
                    if (root.TryGetProperty("detail", out JsonElement detail)
                        && detail.ValueKind == JsonValueKind.String
                        && detail.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }
            }
            catch (JsonException) { /* not JSON — relay the raw body */ }

            return body.Trim();
        }

        return response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
    }
```

- [ ] **Step 5: Add the relay catch to `GetAsset` and `ListAssets`**

In `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`, wrap the body of `GetAsset` (line 104) and `ListAssets` (line 111) in a try/catch. The relay catch is verbatim AR/AP.

`GetAsset` becomes:

```csharp
    private static async Task<IResult> GetAsset(
        Guid clientId, Guid assetId, FixedAssetsService service, CancellationToken cancellationToken)
    {
        try
        {
            Asset? asset = await service.GetAsync(clientId, assetId, cancellationToken);
            return asset is null ? Results.NotFound() : Results.Ok(new AssetView(asset));
        }
        catch (LedgerClientException ex) // the engine refused the fold read (e.g. folded control account lacks its required dimension) — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
    }
```

`ListAssets` becomes (keep the `TryOrder` 400 guard OUTSIDE the try — it does not fold):

```csharp
    private static async Task<IResult> ListAssets(
        Guid clientId, int? skip, int? limit, string? order, bool? includeInactive,
        FixedAssetsService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            // Route through the service so accumulated depreciation is folded from the ledger, not read from a
            // store default of 0 (the stored field is gone). Mirrors the Cash/Payroll list reroutes.
            PagedResponse<Asset> page = await service.GetByClientPagedAsync(
                clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeInactive ?? false, cancellationToken);

            return Results.Ok(new PagedResponse<AssetView>(
                page.Items.Select(a => new AssetView(a)).ToList(), page.Total, page.Skip, page.Limit));
        }
        catch (LedgerClientException ex) // the engine refused the fold read (e.g. folded control account lacks its required dimension) — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
    }
```

If `LedgerClientException` is not already visible, add `using Accounting101.FixedAssets;` to the endpoint file's usings (it is the domain namespace; confirm it resolves — `FixedAssetsService`, `Asset`, `AssetView` come from there, so it is almost certainly already imported).

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter LedgerErrorRelayE2eTests`
Expected: PASS — both facts now receive a relayed 422 with a non-empty `Detail`.

- [ ] **Step 7: Run the full FixedAssets suite (no regression)**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS — all pre-existing FA tests still green (the read paths still return 200 for correctly-configured charts; the swap only changes the non-2xx branch).

- [ ] **Step 8: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/LedgerClientException.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/LedgerErrorRelayE2eTests.cs
git commit -m "fix(fixedassets): relay fold-on-read ledger refusal as 4xx, not opaque 500"
```

---

### Task 2: Inventory fold-read error relay

**Files:**
- Create: `Modules/Inventory/Accounting101.Inventory/LedgerClientException.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/InventoryEndpoints.cs`
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/LedgerErrorRelayE2eTests.cs`

**Interfaces:**
- Consumes: engine `GET /clients/{id}/subledger` returns 422 when the named control account's `RequiredDimensions` is empty or omits the requested dimension (`LedgerEndpoints.cs:554,557`). Inventory folds `InventoryAssetAccountId` with dimension `"Item"` on the read path (`ItemValuationService.cs:25,46`, reached from `InventoryService.GetAsync`/`GetPagedAsync`).
- Produces: `Accounting101.Inventory.LedgerClientException(int statusCode, string reason)`; `HttpLedgerClient` read methods throw it; `GetItem`/`ListItems` relay it.

- [ ] **Step 1: Write the failing test**

Create `Modules/Inventory/Accounting101.Inventory.Tests/LedgerErrorRelayE2eTests.cs`. Chart is set up with the Inventory Asset account (`1400`) as a **plain** `Asset` account (no `RequiredDimensions`), so the `"Item"`-dimensioned fold read refuses with 422. Item creation does not post, so it succeeds.

```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails (proves the bug)**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter LedgerErrorRelayE2eTests`
Expected: FAIL — both facts get `500 InternalServerError` (the bare `EnsureSuccessStatusCode` on the value fold escapes as 500).

- [ ] **Step 3: Create the typed exception**

Create `Modules/Inventory/Accounting101.Inventory/LedgerClientException.cs` (verbatim port; namespace only differs):

```csharp
namespace Accounting101.Inventory;

/// <summary>
/// A ledger call returned a non-success status. Carries the engine's HTTP status code and its reason
/// (the ProblemDetails <c>detail</c>, or the raw body) so the module can surface the real cause — a
/// closed-period 409, an unbalanced-entry 422, a fold-on-read dimension 422 — instead of letting it
/// escape as an opaque 500.
/// </summary>
public sealed class LedgerClientException(int statusCode, string reason)
    : Exception($"Ledger request failed ({statusCode}): {reason}")
{
    /// <summary>The HTTP status the engine returned (e.g. 409, 422).</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The engine's human-readable reason, suitable to relay to the caller.</summary>
    public string Reason { get; } = reason;
}
```

- [ ] **Step 4: Add the helpers + swap the read methods in `HttpLedgerClient`**

In `Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs`:

Add the two `using` directives at the top (after `using System.Net.Http.Json;`):

```csharp
using System.Text;
using System.Text.Json;
```

Swap `response.EnsureSuccessStatusCode();` → `await EnsureSuccessAsync(response, cancellationToken);` in exactly **three** methods — `GetEntriesBySourceRefAsync` (line 68), `GetEntriesBySourceRefsAsync` (line 79), and `GetSubledgerAsync` (line 93). Leave `PostAsync`, `ReverseAsync`, `VoidAsync` unchanged.

Add the same two private helpers (verbatim, identical to Task 1 Step 4 — repeated here so the implementer need not cross-reference) just before the `Forwarded` method:

```csharp
    /// <summary>
    /// Throw a typed <see cref="LedgerClientException"/> carrying the engine's status and reason on any
    /// non-success response, so a caller (and the module's endpoints) can relay the real cause instead of
    /// the bare <see cref="HttpRequestException"/> that <c>EnsureSuccessStatusCode</c> would throw.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new LedgerClientException((int)response.StatusCode, ReasonFrom(body, response));
    }

    /// <summary>
    /// Pull the best available reason from the response body.
    /// Priority: (1) <c>errors</c> map from ValidationProblemDetails (field-level messages flattened to
    /// <c>"field: msg; field: msg"</c>), (2) ProblemDetails <c>detail</c>, (3) raw body, (4) status phrase.
    /// The <c>errors</c> branch only fires when that property is a non-empty JSON object — plain
    /// ProblemDetails (409 freeze, 422 fold, etc.) that carry only <c>detail</c> are unaffected.
    /// </summary>
    private static string ReasonFrom(string body, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    // ValidationProblemDetails: flatten the `errors` map when present and non-empty.
                    if (root.TryGetProperty("errors", out JsonElement errors)
                        && errors.ValueKind == JsonValueKind.Object)
                    {
                        StringBuilder sb = new();
                        foreach (JsonProperty prop in errors.EnumerateObject())
                        {
                            if (sb.Length > 0) sb.Append("; ");
                            sb.Append(prop.Name).Append(": ");
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                sb.Append(string.Join(", ", prop.Value.EnumerateArray()
                                    .Select(m => m.GetString() ?? string.Empty)));
                            }
                            else
                            {
                                sb.Append(prop.Value.GetRawText().Trim('"'));
                            }
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }

                    // Plain ProblemDetails: use the `detail` field.
                    if (root.TryGetProperty("detail", out JsonElement detail)
                        && detail.ValueKind == JsonValueKind.String
                        && detail.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }
            }
            catch (JsonException) { /* not JSON — relay the raw body */ }

            return body.Trim();
        }

        return response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
    }
```

- [ ] **Step 5: Add the relay catch to `GetItem` and `ListItems`**

In `Modules/Inventory/Accounting101.Inventory.Api/InventoryEndpoints.cs`, wrap the fold-reading body of `GetItem` (line 159) and `ListItems` (line 166) in a try/catch (verbatim relay). Leave `ReactivateItem` (line 148) unchanged — it folds via `service.GetAsync`, but it is a write-path transition, out of scope per the post-path non-goal.

`GetItem` becomes:

```csharp
    private static async Task<IResult> GetItem(
        Guid clientId, Guid itemId, InventoryService service, CancellationToken cancellationToken)
    {
        try
        {
            Item? item = await service.GetAsync(clientId, itemId, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(new ItemView(item));
        }
        catch (LedgerClientException ex) // the engine refused the fold read (e.g. folded control account lacks its required dimension) — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
    }
```

`ListItems` becomes (keep the `TryOrder` 400 guard OUTSIDE the try — it does not fold):

```csharp
    private static async Task<IResult> ListItems(
        Guid clientId, int? skip, int? limit, string? order, bool? includeInactive,
        InventoryService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            PagedResponse<Item> page = await service.GetPagedAsync(
                clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeInactive ?? false, cancellationToken);

            return Results.Ok(new PagedResponse<ItemView>(
                page.Items.Select(i => new ItemView(i)).ToList(), page.Total, page.Skip, page.Limit));
        }
        catch (LedgerClientException ex) // the engine refused the fold read (e.g. folded control account lacks its required dimension) — relay its real status + reason, not a 500
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
    }
```

If `LedgerClientException` is not already visible, add `using Accounting101.Inventory;` to the endpoint file's usings (it is the domain namespace; `InventoryService`, `Item`, `ItemView` come from there, so it is almost certainly already imported).

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter LedgerErrorRelayE2eTests`
Expected: PASS — both facts now receive a relayed 422 with a non-empty `Detail`.

- [ ] **Step 7: Run the full Inventory suite (no regression)**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS — all pre-existing Inventory tests still green.

- [ ] **Step 8: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/LedgerClientException.cs \
        Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs \
        Modules/Inventory/Accounting101.Inventory.Api/InventoryEndpoints.cs \
        Modules/Inventory/Accounting101.Inventory.Tests/LedgerErrorRelayE2eTests.cs
git commit -m "fix(inventory): relay fold-on-read ledger refusal as 4xx, not opaque 500"
```

---

### Final verification (after both tasks)

- [ ] **Whole-solution build + test**

Run: `dotnet test Accounting101.sln`
Expected: PASS — whole solution green, including the two new relay tests.

- [ ] **Optional: live smoke of the fixed path**

If the dev stack is up (27018/5000/4200), re-run the `/assets` scenario that originally 500'd against a correctly-configured seeded client to confirm the normal read still returns 200 (the fix only changes the non-2xx branch). No misconfigured client exists in the seed, so the 422 relay itself is covered by the E2E tests, not the smoke.

## Success criteria (from spec)

- No FA or Inventory list/detail read returns 500 when the folded control account is misconfigured; the engine's 422 + reason are relayed as a clean 4xx `ProblemDetails`.
- `LedgerClientException` exists in FixedAssets and Inventory; read methods use `EnsureSuccessAsync`; the four fold-reading endpoints relay it.
- Post/reverse/void path unchanged (still `EnsureSuccessStatusCode`); no degrade-to-0.
- Whole solution green; both module suites green; the two new relay tests fail before the change and pass after.
