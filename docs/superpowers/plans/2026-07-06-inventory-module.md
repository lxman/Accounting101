# Inventory Module Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone perpetual-inventory module — stockable items and their receipt/issue/adjustment movements — that posts its own weighted-average double-entry GL entries.

**Architecture:** A new module under `Modules/Inventory/`, three projects mirroring the **Fixed Assets** module exactly (domain + `.Api` + `.Tests`). Items are mutable **Reference** documents; movements are numbered **Evidentiary** documents that each compose one balanced `PostEntryRequest` posted as PendingApproval through `ILedgerClient`. Valuation is weighted-average carried as an `(OnHandQuantity, TotalValue)` pair; average unit cost is derived, never stored.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, System.Text.Json, MongoDB via the engine's `IDocumentStore`, xUnit + EphemeralMongo (`Accounting101.TestSupport.SharedMongo`), Angular 22 (standalone, zoneless, signals, OnPush) + Tailwind 4 + Spartan, Vitest.

## The template module

This module is a near-exact structural clone of **Fixed Assets**. Throughout, "mirror `<FA file>`" means copy that file's structure and adapt the names/fields per the deltas given. Read the FA file before writing. Key FA files:

- Domain: `Modules/FixedAssets/Accounting101.FixedAssets/` — `Asset.cs`, `AssetBody.cs`, `AssetDocument.cs`, `AssetView.cs`, `AssetStatus.cs`, `AssetValidation.cs`, `DocumentAssetStore.cs`, `FixedAssetsPorts.cs`, `FixedAssetsService.cs`, `FixedAssetsPosting.cs`, `FixedAssetsPostingAccounts.cs`, `DepreciationRun.cs`, `DocumentDepreciationRunStore.cs`, `FixedAssetsRunService.cs`, `ILedgerClient.cs`.
- Api: `Modules/FixedAssets/Accounting101.FixedAssets.Api/` — `FixedAssetsEndpoints.cs`, `FixedAssetsRequests.cs`, `FixedAssetsServiceExtensions.cs`, `ConfiguredFixedAssetsAccountsProvider.cs`, `IFixedAssetsAccountsProvider.cs` (interface), `HttpLedgerClient.cs`.
- Tests: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/` — `AssetValidationTests.cs`, `AssetDocumentStoreFixture.cs`, `AssetDocumentStoreTests.cs`, `AssetLifecycleStoreTests.cs`, `FixedAssetsEndpointsTests.cs`, `FixedAssetsHostFixture.cs`, `Fakes.cs`, `FixedAssetsPostingTests.cs`, `DepreciationRunE2eTests.cs`.

## Global Constraints

- **.NET 10**, `net10.0`, `ImplicitUsings` enable, `Nullable` enable — every csproj.
- **Every enum** gets `[JsonConverter(typeof(JsonStringEnumConverter))]`. A serialization-assertion test proves each serializes to a **string**, not a number. (This is the Banking-module bug that only the smoke test caught; do not repeat it.)
- **Module is default-closed:** it does nothing until `"inventory"` is in the client's `EnabledModules`. Capability `inventory.write` gates every mutation; reads fall back to module membership.
- **Server owns derived state:** `OnHandQuantity`, `TotalValue`, item `Status`, and every movement snapshot field are computed server-side, never accepted from the client.
- **Money is `decimal`**, never floating point. GL amounts are rounded to 2 decimal places with `MidpointRounding.ToEven` (banker's rounding), matching the FA cent convention.
- **Movements post as PendingApproval** through `ILedgerClient` (maker-checker SoD applies). The module never self-approves.
- **Posting accounts resolved BEFORE any persistence** — a config error must fail before side effects (FA `FixedAssetsRunService` step 4 pattern).
- **Named HttpClient** `"InventoryLedgerClient"` to avoid the `ILedgerClient` short-name collision across modules; posts attach the `inventory` module credential (`[FromKeyedServices("inventory")] ModuleCredential`).
- **Commit trailer (verbatim) on every commit:** `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Branch:** all work on `feat/inventory-module` (already created off master). One commit per task. Leave `UI/Angular/src/app/core/api/environment.ts` and `.localdev/start.ps1` UNCOMMITTED.

## Canonical domain types (used verbatim across tasks)

These signatures are fixed here so every task agrees. Do not rename.

```csharp
// ── Item (Reference doc) ──────────────────────────────────────────────
public enum ItemStatus { Active = 0, Inactive = 1 }          // [JsonConverter] on the decl

public sealed record ItemBody(string Sku, string Name, string? Description, string UnitOfMeasure);

public sealed record ItemDocument(                            // persisted body incl. server-owned
    string Sku, string Name, string? Description, string UnitOfMeasure,
    decimal OnHandQuantity, decimal TotalValue);
// (Status is NOT in the document — it derives from the document lifecycle, like FA's Inactive.)

public sealed record Item
{
    public required Guid Id { get; init; }
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string UnitOfMeasure { get; init; }
    public required ItemStatus Status { get; init; }
    public required decimal OnHandQuantity { get; init; }
    public required decimal TotalValue { get; init; }
}

public sealed record ItemView(Item Item)
{
    public decimal AverageUnitCost => Item.OnHandQuantity == 0m ? 0m : Item.TotalValue / Item.OnHandQuantity;
}

// ── StockMovement (Evidentiary doc) ───────────────────────────────────
public enum MovementType { Receipt = 0, Issue = 1, Adjustment = 2 }   // [JsonConverter]
public enum MovementStatus { Posted = 0, Void = 1 }                   // [JsonConverter]

public sealed record StockMovementBody(
    Guid ItemId, MovementType Type, DateOnly EffectiveDate, string? Memo,
    decimal Quantity,             // positive for Receipt/Issue; signed for Adjustment (+overage/-shrinkage)
    decimal AppliedUnitCost,      // unit cost actually used
    decimal ExtendedCost,         // positive magnitude posted to the GL
    decimal ResultingOnHand, decimal ResultingTotalValue);

public sealed record StockMovement
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }               // MV-#####
    public required Guid ItemId { get; init; }
    public required MovementType Type { get; init; }
    public required DateOnly EffectiveDate { get; init; }
    public string? Memo { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal AppliedUnitCost { get; init; }
    public required decimal ExtendedCost { get; init; }
    public required decimal ResultingOnHand { get; init; }
    public required decimal ResultingTotalValue { get; init; }
    public required MovementStatus Status { get; init; }

    /// <summary>The net change this movement applied to the item's on-hand quantity (for void reversal).</summary>
    public decimal SignedQuantityEffect => Type switch
    {
        MovementType.Receipt => Quantity,
        MovementType.Issue => -Quantity,
        _ => Quantity,                                  // Adjustment: Quantity is already signed
    };

    /// <summary>The net change this movement applied to the item's total value (for void reversal).</summary>
    public decimal SignedValueEffect => Type switch
    {
        MovementType.Receipt => ExtendedCost,
        MovementType.Issue => -ExtendedCost,
        _ => Quantity >= 0m ? ExtendedCost : -ExtendedCost,  // Adjustment: overage adds, shrinkage subtracts
    };
}

public sealed record StockMovementView(StockMovement Movement);

// ── Posting accounts ──────────────────────────────────────────────────
public sealed record InventoryPostingAccounts
{
    public required Guid InventoryAssetAccountId { get; init; }
    public required Guid CogsAccountId { get; init; }
    public required Guid GrniClearingAccountId { get; init; }
    public required Guid InventoryAdjustmentAccountId { get; init; }
}
```

**Design refinement vs the spec:** the spec listed a `LedgerEntryId` snapshot field on the movement. Following the FA precedent, the module does **not** store the entry id — it resolves the spawned entry via `ledger.GetEntriesBySourceRefAsync(clientId, movementId)` (the `SourceRef` back-link) during void. No separate stored field; one less thing to keep consistent.

## File structure

**Create — `Modules/Inventory/Accounting101.Inventory/` (domain):**
- `Accounting101.Inventory.csproj` — refs `Accounting101.Ledger.Contracts` only
- `Item.cs`, `ItemBody.cs`, `ItemDocument.cs`, `ItemView.cs`, `ItemStatus.cs`, `ItemValidation.cs`
- `InventoryPorts.cs` (`IItemStore`, `IStockMovementStore`, result enums)
- `DocumentItemStore.cs`, `DocumentStockMovementStore.cs`
- `InventoryService.cs` (item lifecycle), `InventoryMovementService.cs` (movements + void)
- `StockMovement.cs` (movement types + view), `MovementType.cs`, `MovementStatus.cs`
- `InventoryValuation.cs` (pure weighted-average engine), `InventoryPosting.cs` (pure recipes), `InventoryPostingAccounts.cs`
- `IInventoryAccountsProvider.cs`, `ILedgerClient.cs` (copy FA's verbatim — same interface)

**Create — `Modules/Inventory/Accounting101.Inventory.Api/`:**
- `Accounting101.Inventory.Api.csproj` — refs domain + `Accounting101.Ledger.Api`
- `InventoryEndpoints.cs`, `InventoryRequests.cs`, `InventoryServiceExtensions.cs`
- `ConfiguredInventoryAccountsProvider.cs`, `HttpLedgerClient.cs`

**Create — `Modules/Inventory/Accounting101.Inventory.Tests/`:**
- `Accounting101.Inventory.Tests.csproj`
- fixtures + tests per task (below)

**Modify:**
- `Accounting101.slnx` — add the 3 projects
- `Accounting101.Host/Accounting101.Host.csproj` — ProjectReference the `.Api` project
- `Accounting101.Host/Program.cs` — `builder.Services.AddInventory(builder.Configuration)` + `app.MapInventoryEndpoints()`
- `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs` — `InventoryRead`/`InventoryWrite` consts, `CapabilityForModule` arm, `All` set
- `UI/Angular/src/app/app.routes.ts`, nav config, `built` array (Task 11)
- `.localdev/start.ps1` — `Inventory__Accounts__*` (Task 11, uncommitted)

---

## Task 1: Module scaffold + host wiring + capability vocabulary

**Goal:** three empty-but-wired projects; the solution builds; the module registers alongside the other six; `inventory.read`/`inventory.write` exist in the capability vocabulary. No item/movement logic yet.

**Files:**
- Create: `Modules/Inventory/Accounting101.Inventory/Accounting101.Inventory.csproj`
- Create: `Modules/Inventory/Accounting101.Inventory.Api/Accounting101.Inventory.Api.csproj`
- Create: `Modules/Inventory/Accounting101.Inventory.Tests/Accounting101.Inventory.Tests.csproj`
- Create: `Modules/Inventory/Accounting101.Inventory/ILedgerClient.cs` (copy FA's verbatim, change namespace to `Accounting101.Inventory`)
- Create: `Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs` (minimal `AddInventory` — manifest only), `InventoryEndpoints.cs` (empty `MapInventoryEndpoints`)
- Modify: `Accounting101.slnx`, `Accounting101.Host/Accounting101.Host.csproj`, `Accounting101.Host/Program.cs`, `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs`

**Interfaces:**
- Produces: `AddInventory(IServiceCollection, IConfiguration)`, `MapInventoryEndpoints(IEndpointRouteBuilder)`, `Capabilities.InventoryRead`/`InventoryWrite`.

- [ ] **Step 1: Domain + Api + Tests csproj files.**

Domain csproj — copy `Accounting101.FixedAssets.csproj` exactly (only the comment changes):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Backend\Accounting101.Ledger.Contracts\Accounting101.Ledger.Contracts.csproj" />
  </ItemGroup>
</Project>
```
Api csproj — mirror `Accounting101.FixedAssets.Api.csproj` (read it; it refs the domain project and `Accounting101.Ledger.Api`). Tests csproj — mirror `Accounting101.FixedAssets.Tests.csproj` (read it; it refs domain, Api, Host, `Accounting101.TestSupport`, and the xUnit/EphemeralMongo/`Microsoft.AspNetCore.Mvc.Testing` packages).

- [ ] **Step 2: Copy `ILedgerClient.cs`** from FA verbatim, namespace `Accounting101.Inventory`.

- [ ] **Step 3: Minimal `AddInventory` + `MapInventoryEndpoints`.**

`InventoryServiceExtensions.cs` (manifest only for now — expands in later tasks):
```csharp
using Accounting101.Ledger.Api.Hosting;
namespace Accounting101.Inventory.Api;

public static class InventoryServiceExtensions
{
    public static IServiceCollection AddInventory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("inventory"), "Inventory", manifest =>
        {
            manifest.Reference("items");
            manifest.Evidentiary("stock-movements");
        });
        return services;
    }
}
```
`InventoryEndpoints.cs`:
```csharp
namespace Accounting101.Inventory.Api;
public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        // routes added in later tasks
    }
}
```
(Confirm the exact `using` for `AddModule`/`ModuleIdentity`/`ModuleManifestBuilder` by matching the top of `FixedAssetsServiceExtensions.cs`.)

- [ ] **Step 4: Capability vocabulary.** In `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs`:
  - Add consts after the FixedAssets pair:
    ```csharp
    public const string InventoryRead = "inventory.read";
    public const string InventoryWrite = "inventory.write";
    ```
  - Add the `CapabilityForModule` arm (after the `"fixedassets"` arm):
    ```csharp
    "inventory"      => level == ModuleAccessLevel.Write ? InventoryWrite : InventoryRead,
    ```
  - Add `InventoryRead, InventoryWrite` to the `All` HashSet.

- [ ] **Step 5: Solution + host wiring.**
  - `Accounting101.slnx`: add the three project entries (match how the FA three are listed).
  - `Accounting101.Host/Accounting101.Host.csproj`: add `<ProjectReference Include="..\Modules\Inventory\Accounting101.Inventory.Api\Accounting101.Inventory.Api.csproj" />` (match the FA ref line).
  - `Program.cs`: add `builder.Services.AddInventory(builder.Configuration);` next to `AddFixedAssets` (~line 21) and `app.MapInventoryEndpoints();` next to `MapFixedAssetsEndpoints()` (~line 105). Add the `using Accounting101.Inventory.Api;` if the host doesn't use implicit usings covering it (check the top of Program.cs).

- [ ] **Step 6: Composition smoke test.** Create `Modules/Inventory/Accounting101.Inventory.Tests/InventoryCompositionTests.cs`. Mirror the smallest FA host test: boot a `WebApplicationFactory<Program>` (you can reuse the FA `FixedAssetsHostFixture` shape but trimmed — no accounts needed yet) and assert the app starts and an unknown `/clients/{guid}/items` route returns 404 (routes not added yet) rather than a startup crash. Minimal assertion: `factory.CreateClient()` succeeds and `GET /health` (or the app's existing liveness route) returns 200. Purpose: prove the seven-module composition still boots.

```csharp
public sealed class InventoryCompositionTests(InventoryHostFixture fx) : IClassFixture<InventoryHostFixture>
{
    [Fact]
    public async Task Host_boots_with_inventory_module_registered()
    {
        HttpClient http = fx.CreateClient();
        HttpResponseMessage res = await http.GetAsync("/health");   // match the host's real liveness path
        Assert.True(res.IsSuccessStatusCode);
    }
}
```
For `InventoryHostFixture`, copy `FixedAssetsHostFixture` but drop the FA account settings and the FA-client-repoint (add them back in Task 6). If the host has no `/health` route, assert instead that a `GET` to a bogus inventory route returns `401`/`404` (proving the pipeline is alive), not a 500.

- [ ] **Step 7: Build + run.**
Run: `dotnet build Accounting101.slnx`
Expected: succeeds, 0 errors.
Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests` (the composition test)
Expected: PASS.
If build fails on the manifest (`one-module-per-host` clobber) — STOP and report; that's a real platform finding, not something to paper over.

- [ ] **Step 8: Commit.**
```bash
git add Modules/Inventory Accounting101.slnx Accounting101.Host Backend/Accounting101.Ledger.Api/Control/Capabilities.cs
git commit -m "feat(inventory): scaffold module (3 projects), host wiring, capability vocabulary

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Item master data — domain, validation, store, CRUD endpoints

**Goal:** create / edit / get / list / deactivate / reactivate items; Sku unique per client; deactivate blocked while stock on hand.

**Files:**
- Create: `Item.cs`, `ItemBody.cs`, `ItemDocument.cs`, `ItemView.cs`, `ItemStatus.cs`, `ItemValidation.cs`, `InventoryPorts.cs` (item half), `DocumentItemStore.cs`, `InventoryService.cs` (domain)
- Create: `InventoryRequests.cs` (Api — `SaveItemRequest`), item endpoints in `InventoryEndpoints.cs`, expand `AddInventory`
- Create tests: `ItemValidationTests.cs`, `ItemDocumentStoreFixture.cs`, `ItemDocumentStoreTests.cs`, `InventoryEndpointsTests.cs` (item routes), `InventoryHostFixture.cs`

**Interfaces:**
- Consumes: canonical `Item`/`ItemBody`/`ItemDocument`/`ItemView`/`ItemStatus` (above).
- Produces:
  - `IItemStore` (below), `DocumentItemStore : IItemStore`
  - `InventoryService` with `CreateAsync`, `UpdateAsync`, `DeactivateAsync`, `ReactivateAsync`, `GetAsync`
  - result enums `UpdateResult`/`UpdateOutcome`, `DeactivateResult`, `ReactivateResult`
  - `SaveItemRequest(string Sku, string Name, string? Description, string UnitOfMeasure)` with `ItemBody ToBody()`
  - endpoints: `POST/GET/PUT /clients/{clientId}/items`, `.../items/{id}`, `.../items/{id}/deactivate|reactivate`

`IItemStore`:
```csharp
public interface IItemStore
{
    Task<Item> CreateAsync(Guid clientId, ItemBody body, CancellationToken ct = default);
    Task<UpdateResult> UpdateAsync(Guid clientId, Guid itemId, ItemBody body, CancellationToken ct = default);
    Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
    Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
    Task<Item?> GetAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
    Task<Item?> GetBySkuAsync(Guid clientId, string sku, CancellationToken ct = default);   // Sku-unique guard
    Task<PagedResponse<Item>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default);

    /// <summary>Overwrite the item's server-owned valuation (on-hand + total value). Used by movements.</summary>
    Task SetValuationAsync(Guid clientId, Guid itemId, decimal onHand, decimal totalValue, CancellationToken ct = default);
}

public enum UpdateOutcome { NotFound, Inactive, Updated, DuplicateSku }
public readonly record struct UpdateResult(UpdateOutcome Outcome, Item? Item)
{
    public static readonly UpdateResult NotFound = new(UpdateOutcome.NotFound, null);
    public static readonly UpdateResult Inactive = new(UpdateOutcome.Inactive, null);
    public static readonly UpdateResult DuplicateSku = new(UpdateOutcome.DuplicateSku, null);
    public static UpdateResult Updated(Item item) => new(UpdateOutcome.Updated, item);
}
public enum DeactivateResult { NotFound, AlreadyInactive, Deactivated, HasStock }
public enum ReactivateResult { NotFound, AlreadyActive, Reactivated }
```

- [ ] **Step 1: Failing validation tests.** `ItemValidationTests.cs` — mirror `AssetValidationTests.cs`. Rules: `Sku` required (non-whitespace), `Name` required, `UnitOfMeasure` required. `Description` optional. Each invalid case returns a non-null reason; a fully-valid body returns null.
```csharp
[Fact] public void Blank_sku_is_rejected() =>
    Assert.NotNull(ItemValidation.Validate(new ItemBody("  ", "Widget", null, "each")));
[Fact] public void Blank_name_is_rejected() =>
    Assert.NotNull(ItemValidation.Validate(new ItemBody("SKU1", " ", null, "each")));
[Fact] public void Blank_uom_is_rejected() =>
    Assert.NotNull(ItemValidation.Validate(new ItemBody("SKU1", "Widget", null, "")));
[Fact] public void Valid_body_passes() =>
    Assert.Null(ItemValidation.Validate(new ItemBody("SKU1", "Widget", "desc", "each")));
```
Run: `dotnet test --filter ItemValidationTests` → FAIL (type missing).

- [ ] **Step 2: Domain types + validation.** Write `ItemStatus.cs` (enum with `[JsonConverter(typeof(JsonStringEnumConverter))]`), `ItemBody.cs`, `ItemDocument.cs`, `Item.cs`, `ItemView.cs` (canonical shapes above), and `ItemValidation.cs`:
```csharp
namespace Accounting101.Inventory;
public static class ItemValidation
{
    public static string? Validate(ItemBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(body.Sku)) return "Sku is required.";
        if (string.IsNullOrWhiteSpace(body.Name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(body.UnitOfMeasure)) return "UnitOfMeasure is required.";
        return null;
    }
}
```
Run: `dotnet test --filter ItemValidationTests` → PASS.

- [ ] **Step 3: Failing store tests.** `ItemDocumentStoreFixture.cs` — copy `AssetDocumentStoreFixture.cs`, changing `"assets"` → `"items"`, `"fixedassets"` → `"inventory"`, and the manifest to `.Reference("items")`. `ItemDocumentStoreTests.cs` + `ItemLifecycleStoreTests.cs` — mirror `AssetDocumentStoreTests.cs`/`AssetLifecycleStoreTests.cs`. Cover: create stamps Status=Active, OnHand=0, TotalValue=0; update preserves valuation; `GetBySkuAsync` finds by sku; deactivate on a zero-stock item succeeds; **deactivate on an item with OnHand>0 returns `HasStock`** (use `SetValuationAsync` to give it stock first); reactivate flips back; `SetValuationAsync` overwrites on-hand/value; paged list excludes inactive unless `includeInactive`.
```csharp
[Fact] public async Task Deactivate_is_blocked_while_stock_on_hand()
{
    var store = new DocumentItemStore(Fx.Store);
    Item item = await store.CreateAsync(Fx.ClientId, new ItemBody("SKU1","Widget",null,"each"));
    await store.SetValuationAsync(Fx.ClientId, item.Id, 5m, 50m);
    Assert.Equal(DeactivateResult.HasStock, await store.DeactivateAsync(Fx.ClientId, item.Id));
}
```
Run → FAIL (`DocumentItemStore` missing).

- [ ] **Step 4: `InventoryPorts.cs` + `DocumentItemStore.cs`.** Write the `IItemStore` interface + result enums (above). Write `DocumentItemStore` by mirroring `DocumentAssetStore.cs` with these deltas:
  - `Collection = "items"`.
  - `ToDocument(ItemBody, decimal onHand, decimal totalValue)` → `new ItemDocument(body.Sku, body.Name, body.Description, body.UnitOfMeasure, onHand, totalValue)`.
  - `CreateAsync`: `ToDocument(body, 0m, 0m)`, then `PutAsync`.
  - `Map(Guid id, DocumentResult<ItemDocument> r)`: set `Status = r.State == DocumentLifecycle.Inactive ? ItemStatus.Inactive : ItemStatus.Active` (Item is a Reference doc; read `DocumentResult.State`). **Note:** unlike FA, the store's `Map` needs the `DocumentResult` (for `.State`), so `GetAsync`/`GetByClientPagedAsync` map from the result, and `CreateAsync`/`UpdateAsync` (which just wrote an Active doc) map with `ItemStatus.Active` directly.
  - `UpdateAsync`: get existing; if null → NotFound; if `existing.State == DocumentLifecycle.Inactive` → Inactive; **Sku-unique guard:** if `body.Sku != existing.Body.Sku` and `GetBySkuAsync(body.Sku)` returns a different item → `DuplicateSku`; else preserve valuation (`existing.Body.OnHandQuantity`, `existing.Body.TotalValue`) into the new document and `PutAsync`.
  - `DeactivateAsync`: get existing; null → NotFound; already inactive → AlreadyInactive; **`existing.Body.OnHandQuantity != 0m` → HasStock**; else `DeactivateAsync` on the document store → Deactivated.
  - `ReactivateAsync`: mirror FA's re-put pattern (a `PutAsync` of the same body rebuilds it Active). Preserve valuation.
  - `GetBySkuAsync`: unbounded `QueryAsync` (no limit — clamps at 200; acceptable for now, note in a comment), `includeInactive: true`, `FirstOrDefault(r => r.Body.Sku == sku)`.
  - `SetValuationAsync`: get existing; if null return; `doc = existing.Body with { OnHandQuantity = onHand, TotalValue = totalValue }`; `PutAsync`. Preserves lifecycle (a Put on an active ref doc keeps it active).
Run store tests → PASS.

- [ ] **Step 5: `InventoryService.cs` (item lifecycle).** Mirror `FixedAssetsService.cs`: validate (throw `ArgumentException` on non-null reason) then delegate to the store. Methods: `CreateAsync`, `UpdateAsync`, `DeactivateAsync`, `ReactivateAsync`, `GetAsync`. **Sku-unique on create:** before `store.CreateAsync`, if `store.GetBySkuAsync(clientId, body.Sku)` is non-null → throw `InvalidOperationException("An item with Sku '…' already exists.")` (→ 409 at the endpoint).

- [ ] **Step 6: Api DTO + endpoints.** `InventoryRequests.cs`:
```csharp
namespace Accounting101.Inventory.Api;
public sealed record SaveItemRequest(string Sku, string Name, string? Description, string UnitOfMeasure)
{
    public ItemBody ToBody() => new(Sku, Name, Description, UnitOfMeasure);
}
```
In `InventoryEndpoints.cs`, mirror the asset routes/handlers from `FixedAssetsEndpoints.cs` (create → 201 `ItemView`; update → outcome switch; deactivate → 204/404/409; reactivate → 200/404/409; get → 200/404; list → paged `ItemView` envelope with `TryOrder`). Map:
  - `UpdateOutcome.DuplicateSku` → 409 `"An item with that Sku already exists."`
  - `DeactivateResult.HasStock` → 409 `"Item has stock on hand; issue or adjust it to zero before deactivating."`
  - create's `InvalidOperationException` (duplicate Sku) → 409; `ArgumentException` (validation) → 422.
Register the six item routes in `MapInventoryEndpoints` (mirror the asset block).

- [ ] **Step 7: Expand `AddInventory`.** Register the store + service (mirror the FA `AddScoped` lines):
```csharp
services.AddScoped<IItemStore>(sp =>
    new DocumentItemStore(sp.GetRequiredKeyedService<IDocumentStore>("inventory")));
services.AddScoped<InventoryService>();
```

- [ ] **Step 8: Endpoint E2E tests + host fixture.** `InventoryHostFixture.cs` — copy `FixedAssetsHostFixture.cs`, drop the FA account settings (add inventory accounts in Task 6), keep `SeedClientAsync` defaulting `EnabledModules = ["inventory"]`, and (for now) no ledger repoint needed. `InventoryEndpointsTests.cs` — mirror `FixedAssetsEndpointsTests.cs` item cases: create returns 201 + ItemView with Status=Active, OnHand=0; duplicate Sku → 409; list paginates; deactivate zero-stock → 204; get after deactivate with `includeInactive=false` → not listed; **a non-entitled client (EnabledModules without "inventory") → 403** on create; **a Read-only member → 403** on create (write cap enforced).
Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests` → all PASS.

- [ ] **Step 9: Enum serialization assertion.** In `InventoryEndpointsTests.cs` (or a new `InventorySerializationTests.cs`), assert the wire value is a string:
```csharp
[Fact] public void ItemStatus_serializes_as_string() =>
    Assert.Contains("\"Active\"", System.Text.Json.JsonSerializer.Serialize(ItemStatus.Active));
```
Run → PASS.

- [ ] **Step 10: Commit.**
```bash
git add Modules/Inventory
git commit -m "feat(inventory): item master CRUD with Sku-unique + deactivate-with-stock guards

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Weighted-average valuation engine (pure)

**Goal:** a pure, I/O-free costing engine — the heart of the module. No dependencies, exhaustively unit-tested.

**Files:**
- Create: `Modules/Inventory/Accounting101.Inventory/InventoryValuation.cs`
- Create test: `Modules/Inventory/Accounting101.Inventory.Tests/InventoryValuationTests.cs`

**Interfaces:**
- Produces:
```csharp
public readonly record struct Valuation(decimal OnHand, decimal TotalValue)
{
    public decimal AverageUnitCost => OnHand == 0m ? 0m : TotalValue / OnHand;
}
public readonly record struct MovementEffect(
    decimal AppliedUnitCost, decimal ExtendedCost, decimal ResultingOnHand, decimal ResultingTotalValue);

public static class InventoryValuation
{
    public static MovementEffect Receipt(Valuation current, decimal quantity, decimal unitCost);
    public static MovementEffect Issue(Valuation current, decimal quantity);
    public static MovementEffect Adjustment(Valuation current, decimal signedQuantity, decimal? unitCost);
}
```

- [ ] **Step 1: Failing tests.** `InventoryValuationTests.cs`:
```csharp
using Accounting101.Inventory;
using Xunit;

public class InventoryValuationTests
{
    [Fact] public void Receipt_reblends_average()
    {
        // 10 @ $2 then 10 @ $4 → 20 on hand, $60 value, $3 avg
        MovementEffect a = InventoryValuation.Receipt(new Valuation(0m, 0m), 10m, 2m);
        MovementEffect b = InventoryValuation.Receipt(new Valuation(a.ResultingOnHand, a.ResultingTotalValue), 10m, 4m);
        Assert.Equal(20m, b.ResultingOnHand);
        Assert.Equal(60m, b.ResultingTotalValue);
        Assert.Equal(40m, b.ExtendedCost);          // 10 × 4
    }

    [Fact] public void Issue_costs_at_current_average()
    {
        // on hand 20 @ $3 avg; issue 5 → COGS 15, remaining 15 @ $45
        MovementEffect e = InventoryValuation.Issue(new Valuation(20m, 60m), 5m);
        Assert.Equal(15m, e.ExtendedCost);
        Assert.Equal(15m, e.ResultingOnHand);
        Assert.Equal(45m, e.ResultingTotalValue);
        Assert.Equal(3m, e.AppliedUnitCost);
    }

    [Fact] public void Full_issue_clears_value_exactly()
    {
        // a rounding-prone average: 3 @ $10 = $30 → avg 10; but make it messy: 3 units, $10.00, then issue 1,1,1
        Valuation v = new(3m, 10m);                  // avg = 3.3333…
        MovementEffect i1 = InventoryValuation.Issue(v, 1m);
        MovementEffect i2 = InventoryValuation.Issue(new Valuation(i1.ResultingOnHand, i1.ResultingTotalValue), 1m);
        MovementEffect i3 = InventoryValuation.Issue(new Valuation(i2.ResultingOnHand, i2.ResultingTotalValue), 1m);
        Assert.Equal(0m, i3.ResultingOnHand);
        Assert.Equal(0m, i3.ResultingTotalValue);    // exact clear on the final unit, no residue
        Assert.Equal(10m, i1.ExtendedCost + i2.ExtendedCost + i3.ExtendedCost);   // COGS totals the value exactly
    }

    [Fact] public void Issue_beyond_on_hand_throws()
    {
        Assert.Throws<InvalidOperationException>(() => InventoryValuation.Issue(new Valuation(2m, 10m), 3m));
    }

    [Fact] public void Overage_adjustment_reblends_like_receipt()
    {
        MovementEffect e = InventoryValuation.Adjustment(new Valuation(10m, 30m), 5m, 4m);   // +5 @ $4
        Assert.Equal(15m, e.ResultingOnHand);
        Assert.Equal(50m, e.ResultingTotalValue);   // 30 + 20
        Assert.Equal(20m, e.ExtendedCost);
    }

    [Fact] public void Shrinkage_adjustment_costs_at_average_and_blocks_negative()
    {
        MovementEffect e = InventoryValuation.Adjustment(new Valuation(10m, 30m), -4m, null);  // -4 @ avg $3
        Assert.Equal(6m, e.ResultingOnHand);
        Assert.Equal(18m, e.ResultingTotalValue);
        Assert.Equal(12m, e.ExtendedCost);
        Assert.Throws<InvalidOperationException>(() => InventoryValuation.Adjustment(new Valuation(2m, 6m), -3m, null));
    }

    [Fact] public void Overage_adjustment_requires_unit_cost()
    {
        Assert.Throws<ArgumentException>(() => InventoryValuation.Adjustment(new Valuation(10m, 30m), 5m, null));
    }
}
```
Run: `dotnet test --filter InventoryValuationTests` → FAIL (type missing).

- [ ] **Step 2: Implement `InventoryValuation.cs`.**
```csharp
namespace Accounting101.Inventory;

public readonly record struct Valuation(decimal OnHand, decimal TotalValue)
{
    public decimal AverageUnitCost => OnHand == 0m ? 0m : TotalValue / OnHand;
}

public readonly record struct MovementEffect(
    decimal AppliedUnitCost, decimal ExtendedCost, decimal ResultingOnHand, decimal ResultingTotalValue);

/// <summary>Pure weighted-average costing over a carried (OnHand, TotalValue) pair. GL amounts are
/// cents, banker's-rounded. A full issue clears value exactly (no rounding residue). Blocks any move
/// that would drive on-hand below zero.</summary>
public static class InventoryValuation
{
    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);

    public static MovementEffect Receipt(Valuation current, decimal quantity, decimal unitCost)
    {
        if (quantity <= 0m) throw new ArgumentException("Receipt quantity must be positive.", nameof(quantity));
        if (unitCost < 0m) throw new ArgumentException("Unit cost must not be negative.", nameof(unitCost));
        decimal ext = Round(quantity * unitCost);
        return new MovementEffect(unitCost, ext, current.OnHand + quantity, current.TotalValue + ext);
    }

    public static MovementEffect Issue(Valuation current, decimal quantity)
    {
        if (quantity <= 0m) throw new ArgumentException("Issue quantity must be positive.", nameof(quantity));
        if (quantity > current.OnHand)
            throw new InvalidOperationException("Issue would drive on-hand below zero.");
        decimal avg = current.AverageUnitCost;
        decimal cost = quantity == current.OnHand ? current.TotalValue : Round(quantity * avg);
        return new MovementEffect(avg, cost, current.OnHand - quantity, current.TotalValue - cost);
    }

    public static MovementEffect Adjustment(Valuation current, decimal signedQuantity, decimal? unitCost)
    {
        if (signedQuantity == 0m) throw new ArgumentException("Adjustment quantity must be non-zero.", nameof(signedQuantity));
        if (signedQuantity > 0m)   // overage — behaves like a receipt at the provided cost
        {
            if (unitCost is not { } cost) throw new ArgumentException("An increase adjustment requires a unit cost.", nameof(unitCost));
            if (cost < 0m) throw new ArgumentException("Unit cost must not be negative.", nameof(unitCost));
            decimal ext = Round(signedQuantity * cost);
            return new MovementEffect(cost, ext, current.OnHand + signedQuantity, current.TotalValue + ext);
        }
        decimal decrease = -signedQuantity;   // shrinkage — costs at current average
        if (decrease > current.OnHand)
            throw new InvalidOperationException("Adjustment would drive on-hand below zero.");
        decimal avg = current.AverageUnitCost;
        decimal shrink = decrease == current.OnHand ? current.TotalValue : Round(decrease * avg);
        return new MovementEffect(avg, shrink, current.OnHand - decrease, current.TotalValue - shrink);
    }
}
```
Run: `dotnet test --filter InventoryValuationTests` → PASS (all 7).

- [ ] **Step 3: Commit.**
```bash
git add Modules/Inventory
git commit -m "feat(inventory): pure weighted-average valuation engine

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Posting recipes + accounts provider (pure + config)

**Goal:** the four balanced GL recipes and the configured-accounts provider.

**Files:**
- Create: `InventoryPosting.cs`, `InventoryPostingAccounts.cs`, `IInventoryAccountsProvider.cs`
- Create: `Accounting101.Inventory.Api/ConfiguredInventoryAccountsProvider.cs`
- Create test: `InventoryPostingTests.cs`

**Interfaces:**
- Consumes: `InventoryPostingAccounts` (canonical, above), `MovementType`.
- Produces:
```csharp
public interface IInventoryAccountsProvider
{
    Task<InventoryPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default);
}
public static class InventoryPosting
{
    public const string StockMovementSourceType = "StockMovement";
    public static PostEntryRequest Compose(
        MovementType type, decimal signedQuantity, Guid movementId, decimal extendedCost,
        DateOnly effectiveDate, string? memo, InventoryPostingAccounts accounts);
}
```

- [ ] **Step 1: Failing tests.** `InventoryPostingTests.cs` — mirror `FixedAssetsPostingTests.cs`. Build an `InventoryPostingAccounts` with four distinct Guids and assert each recipe's debit/credit account + amount, and that lines balance. Confirm `PostLineRequest` shape from `FixedAssetsPosting.cs` (`new(accountId, "Debit"|"Credit", amount)`).
```csharp
public class InventoryPostingTests
{
    private static readonly InventoryPostingAccounts Accts = new()
    {
        InventoryAssetAccountId = Guid.NewGuid(), CogsAccountId = Guid.NewGuid(),
        GrniClearingAccountId = Guid.NewGuid(), InventoryAdjustmentAccountId = Guid.NewGuid(),
    };

    [Fact] public void Receipt_debits_inventory_credits_grni()
    {
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Receipt, 10m, Guid.NewGuid(), 40m, new DateOnly(2026,1,31), null, Accts);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAssetAccountId && l.Direction == "Debit"  && l.Amount == 40m);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.GrniClearingAccountId   && l.Direction == "Credit" && l.Amount == 40m);
    }

    [Fact] public void Issue_debits_cogs_credits_inventory()
    {
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Issue, 5m, Guid.NewGuid(), 15m, new DateOnly(2026,1,31), null, Accts);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.CogsAccountId           && l.Direction == "Debit"  && l.Amount == 15m);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAssetAccountId && l.Direction == "Credit" && l.Amount == 15m);
    }

    [Fact] public void Shrinkage_debits_adjustment_credits_inventory()
    {
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Adjustment, -4m, Guid.NewGuid(), 12m, new DateOnly(2026,1,31), null, Accts);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAdjustmentAccountId && l.Direction == "Debit"  && l.Amount == 12m);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAssetAccountId      && l.Direction == "Credit" && l.Amount == 12m);
    }

    [Fact] public void Overage_debits_inventory_credits_adjustment()
    {
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Adjustment, 5m, Guid.NewGuid(), 20m, new DateOnly(2026,1,31), null, Accts);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAssetAccountId      && l.Direction == "Debit"  && l.Amount == 20m);
        Assert.Contains(e.Lines, l => l.AccountId == Accts.InventoryAdjustmentAccountId && l.Direction == "Credit" && l.Amount == 20m);
    }

    [Fact] public void Source_backlink_is_set()
    {
        Guid mv = Guid.NewGuid();
        PostEntryRequest e = InventoryPosting.Compose(MovementType.Receipt, 10m, mv, 40m, new DateOnly(2026,1,31), "memo", Accts);
        Assert.Equal(mv, e.SourceRef);
        Assert.Equal(InventoryPosting.StockMovementSourceType, e.SourceType);
    }
}
```
Run → FAIL.

- [ ] **Step 2: Implement `InventoryPostingAccounts.cs`, `IInventoryAccountsProvider.cs`, `InventoryPosting.cs`.**
```csharp
using Accounting101.Ledger.Contracts;
namespace Accounting101.Inventory;

public static class InventoryPosting
{
    public const string StockMovementSourceType = "StockMovement";

    public static PostEntryRequest Compose(
        MovementType type, decimal signedQuantity, Guid movementId, decimal extendedCost,
        DateOnly effectiveDate, string? memo, InventoryPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (extendedCost <= 0m)
            throw new ArgumentException("Extended cost must be positive.", nameof(extendedCost));

        List<PostLineRequest> lines = type switch
        {
            MovementType.Receipt =>
            [
                new(accounts.InventoryAssetAccountId, "Debit",  extendedCost),
                new(accounts.GrniClearingAccountId,   "Credit", extendedCost),
            ],
            MovementType.Issue =>
            [
                new(accounts.CogsAccountId,           "Debit",  extendedCost),
                new(accounts.InventoryAssetAccountId, "Credit", extendedCost),
            ],
            MovementType.Adjustment when signedQuantity < 0m =>   // shrinkage
            [
                new(accounts.InventoryAdjustmentAccountId, "Debit",  extendedCost),
                new(accounts.InventoryAssetAccountId,      "Credit", extendedCost),
            ],
            MovementType.Adjustment =>                            // overage
            [
                new(accounts.InventoryAssetAccountId,      "Debit",  extendedCost),
                new(accounts.InventoryAdjustmentAccountId, "Credit", extendedCost),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(StockMovementSourceType, movementId),
            EffectiveDate: effectiveDate,
            Reference: null,
            Memo: memo,
            Lines: lines,
            SourceRef: movementId,
            SourceType: StockMovementSourceType);
    }
}
```
(Confirm the `PostEntryRequest` constructor argument names/order against `FixedAssetsPosting.cs` — copy them exactly.)

- [ ] **Step 3: `ConfiguredInventoryAccountsProvider.cs`** — mirror `ConfiguredFixedAssetsAccountsProvider.cs`, keys `Inventory:Accounts:InventoryAsset|Cogs|GrniClearing|InventoryAdjustment`:
```csharp
using Accounting101.Inventory;
namespace Accounting101.Inventory.Api;

public sealed class ConfiguredInventoryAccountsProvider(IConfiguration configuration) : IInventoryAccountsProvider
{
    public Task<InventoryPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new InventoryPostingAccounts
        {
            InventoryAssetAccountId      = Read("Inventory:Accounts:InventoryAsset"),
            CogsAccountId                = Read("Inventory:Accounts:Cogs"),
            GrniClearingAccountId        = Read("Inventory:Accounts:GrniClearing"),
            InventoryAdjustmentAccountId = Read("Inventory:Accounts:InventoryAdjustment"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id) ? id
            : throw new InvalidOperationException($"Inventory posting account '{key}' is not configured.");
}
```
Run: `dotnet test --filter InventoryPostingTests` → PASS.

- [ ] **Step 4: Commit.**
```bash
git add Modules/Inventory
git commit -m "feat(inventory): GL posting recipes + configured accounts provider

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Stock-movement domain + evidentiary store

**Goal:** the movement document types and their numbered append-only store (`MV-#####`), with the `GetLatestForItemAsync` query the LIFO void depends on.

**Files:**
- Create: `StockMovement.cs`, `MovementType.cs`, `MovementStatus.cs`, `InventoryPorts.cs` (movement half — add `IStockMovementStore`), `DocumentStockMovementStore.cs`
- Create test: `StockMovementStoreFixture.cs`, `StockMovementStoreTests.cs`

**Interfaces:**
- Consumes: canonical `StockMovement`/`StockMovementBody`/`MovementType`/`MovementStatus`.
- Produces:
```csharp
public interface IStockMovementStore
{
    Task<StockMovement> RecordAsync(Guid clientId, StockMovementBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid movementId, CancellationToken ct = default);
    Task<StockMovement?> GetAsync(Guid clientId, Guid movementId, CancellationToken ct = default);
    Task<PagedResponse<StockMovement>> GetByItemPagedAsync(
        Guid clientId, Guid itemId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);
    Task<StockMovement?> GetLatestForItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
}
```

- [ ] **Step 1: Failing store tests.** `StockMovementStoreFixture.cs` — copy `AssetDocumentStoreFixture.cs`, manifest `.Evidentiary("stock-movements")`, collection `"stock-movements"`, module `"inventory"`. `StockMovementStoreTests.cs`: record assigns `MV-00001`, `MV-00002`…; `GetByItemPagedAsync` filters by item and excludes voided unless `includeVoided`; `GetLatestForItemAsync` returns the most-recent non-voided movement **for that item** (not another item); void flips Status.
```csharp
[Fact] public async Task Records_number_and_latest_is_per_item()
{
    var store = new DocumentStockMovementStore(Fx.Store);
    Guid itemA = Guid.NewGuid(), itemB = Guid.NewGuid();
    StockMovement a1 = await store.RecordAsync(Fx.ClientId, Body(itemA, MovementType.Receipt, 10m, 2m, 20m, 10m, 20m));
    StockMovement b1 = await store.RecordAsync(Fx.ClientId, Body(itemB, MovementType.Receipt, 5m, 3m, 15m, 5m, 15m));
    StockMovement a2 = await store.RecordAsync(Fx.ClientId, Body(itemA, MovementType.Issue, 4m, 2m, 8m, 6m, 12m));
    Assert.StartsWith("MV-", a1.Number);
    StockMovement? latestA = await store.GetLatestForItemAsync(Fx.ClientId, itemA);
    Assert.Equal(a2.Id, latestA!.Id);         // a2, not b1
}
```
Run → FAIL.

- [ ] **Step 2: Domain types.** `MovementType.cs`, `MovementStatus.cs` (both `[JsonConverter]`), `StockMovement.cs` (canonical `StockMovementBody`, `StockMovement` with the `SignedQuantityEffect`/`SignedValueEffect` derived props, `StockMovementView`).

- [ ] **Step 3: `DocumentStockMovementStore.cs`.** Mirror `DocumentDepreciationRunStore.cs` with deltas: `Collection = "stock-movements"`; `RecordAsync` creates + finalizes + maps; `Map` sets `Number = r.Sequence is { } seq ? $"MV-{seq:D5}" : null` and `Status = r.State is DocumentLifecycle.Voided or DocumentLifecycle.Superseded ? MovementStatus.Void : MovementStatus.Posted`, copying every `StockMovementBody` field onto `StockMovement`. `GetByItemPagedAsync`: query the collection, then filter `r.Body.ItemId == itemId` **in memory after an unbounded query** (the doc store has no per-field query; mirror the `GetByPeriodAsync` unbounded-query note in `DocumentDepreciationRunStore` — clamps at 200, acceptable for now, leave the same comment) then page the filtered list and compute `total` from the filtered count. `GetLatestForItemAsync`: unbounded non-voided query, `Where(ItemId==itemId)`, order by `Sequence` desc, first.

- [ ] **Step 4: Run store tests** → PASS.

- [ ] **Step 5: Commit.**
```bash
git add Modules/Inventory
git commit -m "feat(inventory): stock-movement evidentiary store (MV-##### numbering, per-item latest)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Receipt path — movement service, endpoint, ledger client

**Goal:** `POST /inventory/movements` with `Type=Receipt` re-blends the item's valuation and posts `Dr Inventory / Cr GRNI` as PendingApproval. This is the module's first GL post — it wires the credentialed `HttpLedgerClient` and the accounts provider.

**Files:**
- Create: `InventoryMovementService.cs`
- Create: `Accounting101.Inventory.Api/HttpLedgerClient.cs` (copy FA's, module key `"inventory"`)
- Modify: `InventoryEndpoints.cs` (+ `RecordMovement`, get, list-by-item routes), `InventoryRequests.cs` (+ `RecordMovementRequest`), `InventoryServiceExtensions.cs` (register movement store, service, provider, named HttpClient), `InventoryHostFixture.cs` (add inventory account settings + ledger repoint)
- Create test: `InventoryMovementServiceTests.cs` (fakes), `MovementReceiptE2eTests.cs`

**Interfaces:**
- Consumes: `IItemStore`, `IStockMovementStore`, `IInventoryAccountsProvider`, `ILedgerClient`, `InventoryValuation`, `InventoryPosting`.
- Produces:
  - `InventoryMovementService.RecordAsync(Guid clientId, RecordMovement request, CancellationToken)` → `StockMovement`
  - `RecordMovement(Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly? EffectiveDate, string? Memo)` (domain input record)
  - `RecordMovementRequest(...)` Api DTO with `RecordMovement ToRequest()`
  - routes `POST /clients/{clientId}/movements`, `GET .../movements/{id}`, `GET .../movements?itemId=`

- [ ] **Step 1: Failing service tests (fakes).** `InventoryMovementServiceTests.cs` — build an `InMemoryItemStore`, `InMemoryStockMovementStore`, `FakeLedgerClient` (copy the FA `Fakes.cs` `FakeLedgerClient` verbatim — same `ILedgerClient`), and a `FixedInventoryAccountsProvider`. Assert a receipt: creates a movement with `Number`, updates the item's on-hand/value via the store, and posts exactly one entry that is `Dr Inventory / Cr GRNI` for the extended cost. Also: unknown item → `KeyNotFoundException`; inactive item → `InvalidOperationException`; receipt with `Quantity<=0` → `ArgumentException`; receipt missing `UnitCost` → `ArgumentException`.
```csharp
[Fact] public async Task Receipt_updates_item_and_posts_inventory_debit()
{
    (var svc, var items, var movements, var ledger, var accts) = Build();
    Item item = await items.CreateAsync(Client, new ItemBody("SKU1","Widget",null,"each"));
    StockMovement mv = await svc.RecordAsync(Client,
        new RecordMovement(item.Id, MovementType.Receipt, 10m, 2m, new DateOnly(2026,1,15), null));
    Item after = (await items.GetAsync(Client, item.Id))!;
    Assert.Equal(10m, after.OnHandQuantity);
    Assert.Equal(20m, after.TotalValue);
    PostEntryRequest posted = Assert.Single(ledger.Posted);
    Assert.Contains(posted.Lines, l => l.AccountId == accts.InventoryAssetAccountId && l.Direction == "Debit" && l.Amount == 20m);
    Assert.Contains(posted.Lines, l => l.AccountId == accts.GrniClearingAccountId   && l.Direction == "Credit");
}
```
You will need an `InMemoryItemStore` and `InMemoryStockMovementStore` in the tests' `Fakes.cs` (mirror the FA `InMemoryAssetStore`/`InMemoryDepreciationRunStore` shapes, implementing the new interfaces incl. `SetValuationAsync` and `GetLatestForItemAsync`).
Run → FAIL.

- [ ] **Step 2: `InventoryMovementService.cs`.**
```csharp
using Accounting101.Ledger.Contracts;
namespace Accounting101.Inventory;

public sealed record RecordMovement(
    Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly? EffectiveDate, string? Memo);

public sealed class InventoryMovementService(
    IItemStore items, IStockMovementStore movements, IInventoryAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<StockMovement> RecordAsync(Guid clientId, RecordMovement request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Shape validation (→ 422). Direction/cost rules per movement type.
        ValidateShape(request);

        // 2. Resolve item; must exist and be active.
        Item item = await items.GetAsync(clientId, request.ItemId, ct)
            ?? throw new KeyNotFoundException($"Item {request.ItemId} not found.");
        if (item.Status != ItemStatus.Active)
            throw new InvalidOperationException("Item is inactive; reactivate it before recording movements.");

        // 3. Compute the effect (may throw InvalidOperationException for block-negative → 409).
        Valuation current = new(item.OnHandQuantity, item.TotalValue);
        MovementEffect effect = request.Type switch
        {
            MovementType.Receipt    => InventoryValuation.Receipt(current, request.Quantity, request.UnitCost!.Value),
            MovementType.Issue      => InventoryValuation.Issue(current, request.Quantity),
            MovementType.Adjustment => InventoryValuation.Adjustment(current, request.Quantity, request.UnitCost),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        // 4. Resolve accounts BEFORE persistence — config error must fail before side effects.
        InventoryPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);

        DateOnly effectiveDate = request.EffectiveDate ?? item.OnHandQuantity switch { _ => DateOnly.FromDateTime(default) };
        // NOTE: default effective date — mirror how the module wants "today". The engine forbids Date.Now in
        // pure code, but this runs in a request; use the host's clock abstraction if one exists, else require
        // EffectiveDate. SIMPLEST: make EffectiveDate REQUIRED in ValidateShape and drop this line. (See Step 2a.)

        // 5. Persist the numbered movement with its snapshot.
        StockMovement movement = await movements.RecordAsync(clientId, new StockMovementBody(
            request.ItemId, request.Type, effectiveDate, request.Memo,
            request.Quantity, effect.AppliedUnitCost, effect.ExtendedCost,
            effect.ResultingOnHand, effect.ResultingTotalValue), ct);

        // 6. Apply the new valuation to the item.
        await items.SetValuationAsync(clientId, request.ItemId, effect.ResultingOnHand, effect.ResultingTotalValue, ct);

        // 7. Compose + post one PendingApproval entry.
        PostEntryRequest entry = InventoryPosting.Compose(
            request.Type, request.Quantity, movement.Id, effect.ExtendedCost, effectiveDate, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return movement;
    }

    private static void ValidateShape(RecordMovement r)
    {
        switch (r.Type)
        {
            case MovementType.Receipt:
                if (r.Quantity <= 0m) throw new ArgumentException("Receipt quantity must be positive.");
                if (r.UnitCost is not { } c || c < 0m) throw new ArgumentException("Receipt requires a non-negative unit cost.");
                break;
            case MovementType.Issue:
                if (r.Quantity <= 0m) throw new ArgumentException("Issue quantity must be positive.");
                break;
            case MovementType.Adjustment:
                if (r.Quantity == 0m) throw new ArgumentException("Adjustment quantity must be non-zero.");
                if (r.Quantity > 0m && (r.UnitCost is not { } uc || uc < 0m))
                    throw new ArgumentException("An increase adjustment requires a non-negative unit cost.");
                break;
        }
    }
}
```
**Step 2a — resolve the effective-date decision:** make `EffectiveDate` **required** on the movement (the clerk always dates a movement, matching how FA runs take a period and cash vouchers take a date). Change `RecordMovement.EffectiveDate` to non-nullable `DateOnly`, drop the Step-4 `effectiveDate` fallback block, and use `request.EffectiveDate` directly. Update `ValidateShape` is unaffected. (Do NOT call `DateOnly.FromDateTime(DateTime.Now)` — the engine bans wall-clock in module code and it breaks determinism.)

- [ ] **Step 3: `HttpLedgerClient.cs`** — copy the FA Api `HttpLedgerClient.cs` verbatim, change `[FromKeyedServices("fixedassets")]` → `[FromKeyedServices("inventory")]` and the doc-comment `ViaModule = "fixedassets"` → `"inventory"`.

- [ ] **Step 4: Expand `AddInventory`.** Add:
```csharp
services.AddScoped<IStockMovementStore>(sp =>
    new DocumentStockMovementStore(sp.GetRequiredKeyedService<IDocumentStore>("inventory")));
services.AddScoped<InventoryMovementService>();
services.AddSingleton<IInventoryAccountsProvider, ConfiguredInventoryAccountsProvider>();
services.AddHttpClient("InventoryLedgerClient", client =>
        client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
    .AddTypedClient<ILedgerClient, HttpLedgerClient>();
```

- [ ] **Step 5: DTO + endpoints.** `RecordMovementRequest(Guid ItemId, MovementType Type, decimal Quantity, decimal? UnitCost, DateOnly EffectiveDate, string? Memo)` with `ToRequest()`. In `InventoryEndpoints.cs` add:
  - `POST /movements` → `service.RecordAsync`; map `ArgumentException` → 422, `KeyNotFoundException` → 404, `InvalidOperationException` → 409; success → 201 `StockMovementView`.
  - `GET /movements/{id}` → 200/404.
  - `GET /movements?itemId={guid}&skip&limit&order&includeVoided` → paged `StockMovementView` (require `itemId`; missing → 400, mirroring the AR/AP "customerId required" 400 pattern).

- [ ] **Step 6: Host fixture accounts + ledger repoint.** In `InventoryHostFixture.cs` add four account Guids + `builder.UseSetting("Inventory:Accounts:InventoryAsset", ...)` etc., and the test-server repoint:
```csharp
services.AddHttpClient("InventoryLedgerClient", c => c.BaseAddress = new Uri("http://localhost"))
        .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
```

- [ ] **Step 7: Receipt E2E.** `MovementReceiptE2eTests.cs` — mirror `DepreciationRunE2eTests.cs`: seed a client (Controller), create an item, `POST` a receipt, assert 201; `GET` the item → on-hand 10, value 20, avg 2; assert an entry was posted (query the engine's entries-by-source or the pending queue as the FA E2E does). A Read-only member → 403. A non-entitled client → 403.
Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests` → all PASS.

- [ ] **Step 8: Commit.**
```bash
git add Modules/Inventory
git commit -m "feat(inventory): receipt movements — valuation re-blend + Dr Inventory/Cr GRNI post

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: Issue path

**Goal:** `Type=Issue` costs at the current average, blocks going negative (409), posts `Dr COGS / Cr Inventory`.

**Files:** Modify `InventoryMovementService` is already generic over type (Task 6 wired all three branches) — this task is primarily **tests** plus confirming the block-negative 409 surfaces end-to-end.
- Create test: `MovementIssueE2eTests.cs`; extend `InventoryMovementServiceTests.cs`.

- [ ] **Step 1: Service tests.** Issue on `(20,60)` for 5 → COGS 15, item → `(15,45)`, posts `Dr COGS 15 / Cr Inventory 15`. Full issue of remaining 15 → item `(0,0)`, COGS totals to 45 exactly across the two issues. Issue exceeding on-hand → `InvalidOperationException`.
- [ ] **Step 2: E2E.** `MovementIssueE2eTests.cs`: seed item, receipt 20 @ $3, then `POST` issue 5 → 201; item avg still 3, on-hand 15; issue 999 → **409** with a "below zero" message; item unchanged after the 409 (no partial mutation — the guard runs before persistence). Verify the COGS entry posted.
- [ ] **Step 3: Run** → PASS.
- [ ] **Step 4: Commit** `feat(inventory): issue movements — average COGS + block-negative 409`.

---

## Task 8: Adjustment path (both directions)

**Goal:** signed adjustment — overage (`+`, requires unit cost, `Dr Inventory / Cr Adjustment`) and shrinkage (`-`, at average, `Dr Adjustment / Cr Inventory`), block-negative on shrinkage.

**Files:** service branches already exist (Task 6). This task is tests.
- Create test: `MovementAdjustmentE2eTests.cs`; extend service tests.

- [ ] **Step 1: Service tests.** Overage `+5 @ $4` on `(10,30)` → `(15,50)`, posts `Dr Inventory 20 / Cr Adjustment 20`. Shrinkage `-4` on `(10,30)` → `(6,18)`, posts `Dr Adjustment 12 / Cr Inventory 12`. Shrinkage beyond on-hand → 409. Overage missing unit cost → 422.
- [ ] **Step 2: E2E** `MovementAdjustmentE2eTests.cs`: seed + receipt, then overage and shrinkage adjustments; assert item valuation + posted entry direction for each; shrinkage-to-negative → 409.
- [ ] **Step 3: Run** → PASS.
- [ ] **Step 4: Commit** `feat(inventory): adjustment movements — overage + shrinkage`.

---

## Task 9: LIFO void

**Goal:** `POST /inventory/movements/{id}/void` — reverse the GL entry (or withdraw if still pending) and restore the item's on-hand/value; only the latest movement for the item may be voided.

**Files:**
- Modify: `InventoryMovementService.cs` (+ `VoidAsync`), `InventoryEndpoints.cs` (+ void route), `InventoryRequests.cs` (+ `VoidReasonRequest`)
- Create test: `MovementVoidE2eTests.cs`; extend service tests.

**Interfaces:**
- Produces: `InventoryMovementService.VoidAsync(Guid clientId, Guid movementId, string? reason, CancellationToken)` → `StockMovement`; route `POST .../movements/{id}/void`.

- [ ] **Step 1: Failing tests.** Void the latest movement → its entry is reversed/withdrawn (assert on `FakeLedgerClient.ReversedOrWithdrawn`), the item's valuation returns to its pre-movement `(OnHand, TotalValue)`, and the movement Status → Void. Void a **non-latest** movement → `InvalidOperationException` (→ 409). Void an already-void movement → 409. Void a missing movement → `KeyNotFoundException` (404).
```csharp
[Fact] public async Task Void_latest_restores_valuation_and_reverses_entry()
{
    (var svc, var items, var movements, var ledger, _) = Build();
    Item item = await items.CreateAsync(Client, new ItemBody("SKU1","Widget",null,"each"));
    await svc.RecordAsync(Client, new RecordMovement(item.Id, MovementType.Receipt, 10m, 2m, D(2026,1,10), null));
    StockMovement issue = await svc.RecordAsync(Client, new RecordMovement(item.Id, MovementType.Issue, 4m, null, D(2026,1,20), null));
    // item now (6, 12). Void the issue → back to (10, 20).
    await svc.VoidAsync(Client, issue.Id, "oops");
    Item after = (await items.GetAsync(Client, item.Id))!;
    Assert.Equal(10m, after.OnHandQuantity);
    Assert.Equal(20m, after.TotalValue);
    Assert.True(ledger.ReversedOrWithdrawn);
}
```
Run → FAIL.

- [ ] **Step 2: Implement `VoidAsync`.** Mirror `FixedAssetsRunService.VoidRunAsync`:
```csharp
public async Task<StockMovement> VoidAsync(Guid clientId, Guid movementId, string? reason, CancellationToken ct = default)
{
    StockMovement movement = await movements.GetAsync(clientId, movementId, ct)
        ?? throw new KeyNotFoundException($"Stock movement {movementId} not found.");
    if (movement.Status != MovementStatus.Posted)
        throw new InvalidOperationException($"Only a posted movement can be voided; {movementId} is {movement.Status}.");

    // LIFO — only the most recent non-voided movement FOR THIS ITEM may be voided.
    StockMovement? latest = await movements.GetLatestForItemAsync(clientId, movement.ItemId, ct);
    if (latest is null || latest.Id != movement.Id)
        throw new InvalidOperationException("Only the most recent movement for this item can be voided.");

    // Reverse the posted entry (or withdraw it if still pending). Tolerate a missing entry (stranded post).
    IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, movementId, ct);
    EntryResponse? entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });
    if (entry is not null)
    {
        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(movement.EffectiveDate, reason ?? $"Voided movement {movementId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided movement {movementId}"), ct);
    }

    // Restore the item's valuation to its pre-movement state (subtract this movement's applied effect).
    Item item = await items.GetAsync(clientId, movement.ItemId, ct)
        ?? throw new KeyNotFoundException($"Item {movement.ItemId} not found.");
    decimal restoredOnHand = item.OnHandQuantity - movement.SignedQuantityEffect;
    decimal restoredValue  = item.TotalValue     - movement.SignedValueEffect;
    await items.SetValuationAsync(clientId, movement.ItemId, restoredOnHand, restoredValue, ct);

    await movements.VoidAsync(clientId, movementId, ct);
    return (await movements.GetAsync(clientId, movementId, ct))!;
}
```
(Confirm `ReverseRequest`/`VoidRequest`/`EntryResponse` shapes against the FA `HttpLedgerClient`/`FixedAssetsRunService` usage — they're identical.)

- [ ] **Step 3: Endpoint + DTO.** `VoidReasonRequest(string? Reason)` (copy FA's). Route `POST /movements/{id}/void` → `VoidAsync`; `KeyNotFoundException` → 404, `InvalidOperationException` → 409; success → 200 `StockMovementView`.

- [ ] **Step 4: E2E** `MovementVoidE2eTests.cs`: seed + receipt + issue; void the issue → 200, item restored, movement Status Void; attempt to void the (now-latest) receipt → 200 and item back to `(0,0)`; attempt to void the already-void issue → 409; void a random guid → 404.
Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests` → all PASS.

- [ ] **Step 5: Commit.**
```bash
git add Modules/Inventory
git commit -m "feat(inventory): LIFO movement void — reverse entry + restore valuation

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 10: Angular core/inventory service + models

**Goal:** the TypeScript client layer — string-union enums (never numbers), models, and the service wrapping every endpoint.

**Files:**
- Create: `UI/Angular/src/app/core/inventory/inventory.ts` (models), `inventory.service.ts`
- Create test: `inventory.service.spec.ts`

**Reference:** mirror `UI/Angular/src/app/core/banking/banking.ts` + `banking.service.ts` (structure, `PagedResponse<T>` envelope handling, `HttpClient`, the `environment` base URL + `devClientId`).

- [ ] **Step 1: Models (`inventory.ts`).** String-union enums matching the wire:
```ts
export type ItemStatus = 'Active' | 'Inactive';
export type MovementType = 'Receipt' | 'Issue' | 'Adjustment';
export type MovementStatus = 'Posted' | 'Void';

export interface Item {
  id: string; sku: string; name: string; description: string | null;
  unitOfMeasure: string; status: ItemStatus; onHandQuantity: number; totalValue: number;
}
export interface ItemView { item: Item; averageUnitCost: number; }
export interface StockMovement {
  id: string; number: string | null; itemId: string; type: MovementType;
  effectiveDate: string; memo: string | null; quantity: number;
  appliedUnitCost: number; extendedCost: number;
  resultingOnHand: number; resultingTotalValue: number; status: MovementStatus;
}
export interface StockMovementView { movement: StockMovement; }
export interface SaveItemRequest { sku: string; name: string; description: string | null; unitOfMeasure: string; }
export interface RecordMovementRequest {
  itemId: string; type: MovementType; quantity: number;
  unitCost: number | null; effectiveDate: string; memo: string | null;
}
```
(Note the response envelopes: item endpoints return `ItemView` (wraps `item`); movement endpoints return `StockMovementView` (wraps `movement`); lists return `PagedResponse<ItemView>` / `PagedResponse<StockMovementView>`. Confirm the exact envelope by matching how `banking.service.ts` unwraps its `{disbursement:…}` shapes.)

- [ ] **Step 2: Service (`inventory.service.ts`).** Methods: `listItems({skip,limit,order,includeInactive})`, `getItem(id)`, `createItem(req)`, `updateItem(id,req)`, `deactivateItem(id)`, `reactivateItem(id)`, `recordMovement(req)`, `getMovement(id)`, `listMovements(itemId,{…})`, `voidMovement(id, reason)`. Base URL from `environment` + `/clients/{devClientId}/…`.

- [ ] **Step 3: Spec.** `inventory.service.spec.ts` — mirror `banking.service.spec.ts` using `HttpTestingController`; assert URLs, methods, and that a movement request serializes `type` as the string (e.g. `'Receipt'`).
Run: `npx ng test --include='**/inventory.service.spec.ts' --watch=false` → PASS.

- [ ] **Step 4: Commit** `feat(inventory-ui): core service + models`.

---

## Task 11: Angular UI screens + routes + nav + dev config

**Goal:** the item-centric UI — list, editor, detail (with movement history), movement editor, movement detail (+void). Wire routes, nav leaf, `built`, and the dev-stack accounts.

**Files:**
- Create: `UI/Angular/src/app/features/inventory/{item-list,item-editor,item-detail,movement-editor,movement-detail}.ts` (+ `.spec.ts` each)
- Modify: `UI/Angular/src/app/app.routes.ts`, the nav config, the `built` array
- Modify (uncommitted): `.localdev/start.ps1`

**Reference:** mirror `UI/Angular/src/app/features/banking/` and `features/fixed-assets/` — OnPush, signals, `@angular/forms/signals`, the shared `<app-paginator>` component, whole-row-click list rows (`accounting101-ui-whole-row-click`), Spartan `hlm-*` imports.

- [ ] **Step 1: item-list.** Paged list of items (sku, name, on-hand, avg cost, status), whole-row-click → detail, `<app-paginator>`, "New item" → editor. Spec: renders rows from a mocked `listItems`, row click navigates.
- [ ] **Step 2: item-editor.** Create/edit `SaveItemRequest` (sku, name, description, unitOfMeasure). Signal-forms validation mirrors the backend (required sku/name/uom). Spec: submit calls `createItem`/`updateItem`.
- [ ] **Step 3: item-detail.** Shows on-hand, average cost, total value, status; a movement-history table (`listMovements(itemId)`) with `<app-paginator>`; buttons: New movement, Deactivate/Reactivate (deactivate disabled/hidden when on-hand ≠ 0, matching the 409), Edit. Spec: renders item + history; deactivate button disabled when `onHandQuantity > 0`.
- [ ] **Step 4: movement-editor.** Type selector (Receipt/Issue/Adjustment); **type-driven form** — unit-cost field shown for Receipt and for a positive Adjustment, hidden for Issue and negative Adjustment; quantity; effective date (required); memo. For Adjustment, a signed quantity (or a direction toggle + magnitude). Spec: switching type toggles the unit-cost control's presence; submit calls `recordMovement` with `type` as the string.
- [ ] **Step 5: movement-detail.** Full snapshot (type, qty, applied unit cost, extended cost, resulting balances, number, status) + Void button (calls `voidMovement`; the UI may optimistically show void only for the latest — but the backend is the source of truth, so surface the 409 as a message rather than hiding the button). Spec: void calls the service; a 409 renders an error.
- [ ] **Step 6: routes + nav + built.** In `app.routes.ts` add the `/inventory` tree (list, `items/:id`, `items/:id/edit`, `items/new`, `movements/new?itemId=`, `movements/:id`) — **specific routes before `:id`**. Add a nav leaf gated on the `inventory` capability (mirror how the banking/fixed-assets leaves are declared). Add `'/inventory'` to the `built` array.
- [ ] **Step 7: dev config (uncommitted).** In `.localdev/start.ps1`, after the FA block, add the four inventory accounts. Reuse existing seeded accounts where sensible and create new ones for the inventory-specific accounts (Inventory asset 1400, COGS 5000, GRNI clearing 2050, Inventory adjustment 5100 — create these via the seed or the account editor during smoke). Example (fill the real seeded Guids):
```powershell
$env:Inventory__Accounts__InventoryAsset      = '<guid 1400 Inventory>'
$env:Inventory__Accounts__Cogs                = '<guid 5000 COGS>'
$env:Inventory__Accounts__GrniClearing        = '<guid 2050 GRNI Clearing>'
$env:Inventory__Accounts__InventoryAdjustment = '<guid 5100 Inventory Adjustment>'
```
- [ ] **Step 8: Run the Angular suite.**
Run: `npx ng test --include='**/features/inventory/**' --watch=false` (and the service spec)
Expected: PASS. Then `npx ng build` → clean.
- [ ] **Step 9: Commit** `feat(inventory-ui): item + movement screens, routes, nav`.

---

## Task 12: Smoke test + branch review (the merge gate)

**Goal:** the non-optional visual + serialization gate before merge — the only layer that observes real serialization end-to-end (see `accounting101-ui-mock-casing-trap`).

- [ ] **Step 1:** Bring up the dev stack: `pwsh .localdev/start.ps1`. Ensure the four inventory accounts exist in Demo Co's chart (create via the account editor if not seeded) and their Guids are in `start.ps1`. Enable the `inventory` module for Demo Co if not already: `PUT /admin/clients/{id}/modules` including `"inventory"` in `moduleKeys`.
- [ ] **Step 2:** In the browser (kapture), exercise: create an item; record a receipt (10 @ $2); confirm on-hand 10 / avg $2; record another receipt (10 @ $4); confirm avg $3; issue 5; confirm COGS and on-hand 15; overage +2 @ $5; shrinkage −3; void the latest movement; confirm the item valuation and the pending GL entries. **Watch the network payloads** — confirm every enum is a **string** (`"type":"Receipt"`, `"status":"Posted"`), not a number. Confirm the deactivate-with-stock 409 message renders, and the void-non-latest 409 renders.
- [ ] **Step 3:** Fix anything the smoke surfaces (expect an enum-serialization miss to be the likely culprit if any). Re-smoke.
- [ ] **Step 4:** Request the whole-branch review (opus) per `superpowers:requesting-code-review`; address Critical/Important.
- [ ] **Step 5:** Report to the user with the smoke result and hold for their explicit green light before merge (the standing gate). Do NOT merge unprompted.

---

## Self-review

**Spec coverage:**
- Standalone module, 3 projects mirroring FA → Task 1. ✓
- Weighted-average carried-value `(OnHand, TotalValue)`, average derived → Task 3 (`InventoryValuation`, `ItemView.AverageUnitCost`). ✓
- Single stock pool, no locations → data model has no location dimension. ✓
- Block-negative 409 → Task 3 (throws) + Task 7 (E2E surfaces 409). ✓
- GRNI clearing on receipt credit → Task 4 recipe + Task 6 E2E. ✓
- LIFO void → Task 9. ✓
- Three movement types, signed adjustment → Tasks 6/7/8; `Quantity` sign convention documented in canonical types. ✓
- Sku-unique + deactivate-with-stock guards → Task 2. ✓
- Configured accounts `Inventory__Accounts__*`, default-closed entitlement, `inventory.write` cap → Tasks 1/2/4/6/11. ✓
- Enum string-serialization + assertion test → Global Constraints + Task 2 Step 9. ✓
- Angular UI (all screens) + routes/nav/built → Tasks 10/11. ✓
- Smoke gate → Task 12. ✓
- Future AP-integration seam (GRNI, reuse issue path) → spec's "Future integration seam"; no task needed (not built here). ✓

**Placeholder scan:** the only "fill in" is the four seeded account Guids in Task 11 Step 7 (`<guid …>`) — these are environment-specific and correctly resolved during smoke, not plan placeholders. The effective-date ambiguity in Task 6 Step 2 is resolved explicitly in Step 2a (make `EffectiveDate` required). No other placeholders.

**Type consistency:** `Item`/`ItemBody`/`ItemDocument`/`ItemView`/`ItemStatus`, `StockMovement`/`StockMovementBody`/`MovementType`/`MovementStatus`, `IItemStore` (incl. `SetValuationAsync`, `GetBySkuAsync`), `IStockMovementStore` (incl. `GetLatestForItemAsync`), `InventoryValuation.{Receipt,Issue,Adjustment}` → `MovementEffect`, `InventoryPosting.Compose`, `InventoryPostingAccounts` (4 ids), `IInventoryAccountsProvider`, `InventoryMovementService.{RecordAsync,VoidAsync}`, `RecordMovement`/`RecordMovementRequest`, `SaveItemRequest`, `AddInventory`/`MapInventoryEndpoints` — all defined once in the canonical block and used identically across tasks. ✓
