# Fixed Assets FA-1 — Asset Register Foundation Design

**Date:** 2026-07-04
**Status:** Draft for review
**Author:** Michael Jordan (with Claude)
**Builds on:** the established module pattern (Receivables/Payables/Payroll/Cash/Reconciliation), the engine document store, capability RBAC, and Phase 3b module entitlement.

## Problem

Fixed Assets is the one subledger area with a capability vocabulary (`fixedassets.read`/`fixedassets.write`, already in `Capabilities.All` and the role presets) but no module. A depreciation subledger needs, in order: an **asset register** (master data — what we own, its cost, when it went into service, how it depreciates), then **depreciation runs** that post to the GL, then **disposals**. This spec covers **FA-1: the asset register foundation** — standing up the module and its master-data lifecycle. No GL posting happens here; the original acquisition entry (Dr Asset / Cr Cash or A/P) was already booked through Cash/Payables when the asset was purchased. This module tracks the asset for the depreciation and disposal that later slices post.

## Scope & non-goals

**In scope (FA-1):**
- A new `fixedassets` module (domain + API + tests projects), installed into the host like the other five.
- An **Asset** reference-document entity with a create / edit / deactivate / get / list lifecycle.
- The data model needed for pluggable depreciation methods later (the asset carries its method + method parameters), even though FA-1 computes nothing.
- Wiring the module into capability enforcement and role presets.

**Out of scope (later slices):**
- **FA-2:** depreciation runs (a pluggable depreciation strategy — straight-line + declining-balance to start — computing per-period depreciation, updating each asset's accumulated depreciation, posting one PendingApproval GL entry, voidable).
- **FA-3:** disposals (retire an asset, post gain/loss vs net book value).
- **FA-4:** Angular UI.
- No GL posting, no `HttpLedgerClient`, no posting-accounts provider in FA-1 (they arrive with FA-2, which is the first slice that posts).

## Resolved decisions

- **Asset = reference document.** Assets are mutable master data (correct a cost typo, adjust useful life before depreciation begins), like customers and vendors — so `assets` is a `.Reference` collection (auditable, deactivatable, unnumbered), not an evidentiary one. The reference lifecycle (create/update/deactivate + audit-on-mutation) is exactly what the engine's `ScopedDocumentStore` already provides for reference collections.
- **Design for multiple depreciation methods.** The `DepreciationMethod` enum has more than one member from the start (`StraightLine`, `DecliningBalance`), and the asset stores method-specific parameters, so FA-2 can implement a pluggable strategy without a data migration. FA-1 stores and validates the method choice; it does not compute depreciation.
- **No GL posting in FA-1.** The register is cataloging. Keeping FA-1 posting-free de-risks the module scaffolding before the depreciation math.

## The Asset entity

`Accounting101.FixedAssets.Asset` (domain record, persisted as the body of a reference document keyed by asset id):

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | The reference document id. |
| `Description` | `string` | Human label, e.g. "Delivery van #3". |
| `AcquisitionCost` | `decimal` | Original cost. Must be > 0. |
| `InServiceDate` | `DateOnly` | When depreciation begins (FA-2 uses it). |
| `UsefulLifeMonths` | `int` | Depreciable life in months. Must be > 0. |
| `SalvageValue` | `decimal` | Residual value. `>= 0` and `<= AcquisitionCost`. |
| `Method` | `DepreciationMethod` | `StraightLine` \| `DecliningBalance`. |
| `DecliningBalanceFactor` | `decimal?` | The DB rate multiplier (e.g. `2.0` = double-declining). Required + > 0 when `Method == DecliningBalance`; must be null otherwise. |
| `Status` | `AssetStatus` | `Active` \| `Disposed`. New assets are `Active`; FA-1 never sets `Disposed` (FA-3 does). |
| `AccumulatedDepreciation` | `decimal` | `0` at creation; FA-2's runs increase it. FA-1 never changes it after create. |

- **`AssetBody`** (create/update input DTO): `Description`, `AcquisitionCost`, `InServiceDate`, `UsefulLifeMonths`, `SalvageValue`, `Method`, `DecliningBalanceFactor`. It does NOT carry `Status` or `AccumulatedDepreciation` (server-owned).
- **`AssetView`** (response): the full asset including derived/server-owned fields.
- **`DepreciationMethod`** enum: `StraightLine = 0`, `DecliningBalance = 1` (0-default so legacy docs read as straight-line, per the codebase's enum-default convention).
- **`AssetStatus`** enum: `Active = 0`, `Disposed = 1`.

**Validation (create + update), returning 422 on failure:** `AcquisitionCost > 0`; `UsefulLifeMonths > 0`; `0 <= SalvageValue <= AcquisitionCost`; if `Method == DecliningBalance` then `DecliningBalanceFactor` is present and `> 0`, else it must be null; `Description` non-blank.

## Components (new)

Following the Payroll/Receivables project split:

- **`Modules/FixedAssets/Accounting101.FixedAssets`** (domain): `Asset`, `AssetBody`, `AssetView`, `DepreciationMethod`, `AssetStatus`; `IAssetStore` (port); `DocumentAssetStore` (reference-document-backed); `FixedAssetsService` (the lifecycle orchestrator).
- **`Modules/FixedAssets/Accounting101.FixedAssets.Api`**: `FixedAssetsEndpoints`, request DTOs, `FixedAssetsServiceExtensions.AddFixedAssets` (`AddModule(new ModuleIdentity("fixedassets"), "Fixed Assets", m => m.Reference("assets"))` + DI). **No `HttpLedgerClient`** — FA-1 does not post.
- **`Modules/FixedAssets/Accounting101.FixedAssets.Tests`**: host + document-store fixtures and suites.

### `IAssetStore` / `DocumentAssetStore`
Backed by `GetRequiredKeyedService<IDocumentStore>("fixedassets")`, using the reference-collection operations (`PutAsync` for create/update, `DeactivateAsync` for deactivate, `GetAsync`/`QueryAsync` for reads):

```
Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct);
Task<Asset> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct);
Task DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct);
Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct);
Task<PagedResponse<Asset>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct);
```

(`AccumulatedDepreciation` and `Status` are stamped by the store: `0`/`Active` on create, unchanged on update; `includeInactive` mirrors the `includeVoided` flag other stores expose.)

### `FixedAssetsService`
Thin orchestration + validation (no ledger dependency): `CreateAsync` / `UpdateAsync` / `DeactivateAsync` / `GetAsync` validate the body (throwing `ArgumentException` → 422 at the endpoint) and delegate to the store. Deactivate/get-missing throw `InvalidOperationException` → 404/409, matching the other modules' endpoint error mapping.

### `FixedAssetsEndpoints`
Under `/clients/{clientId:guid}` (`RequireAuthorization()`), mirroring `PayrollEndpoints`:
- `POST /assets` → 201 + `AssetView`
- `PUT /assets/{assetId:guid}` → 200 + `AssetView` (404 if missing)
- `POST /assets/{assetId:guid}/deactivate` → 200 (404 if missing, 409 if already inactive)
- `GET /assets/{assetId:guid}` → 200 `AssetView` / 404
- `GET /assets?skip=&limit=&order=asc|desc&includeInactive=` → 200 `PagedResponse<AssetView>`

## Platform wiring

- **Capability enforcement:** add `"fixedassets" => level == ModuleAccessLevel.Write ? FixedAssetsWrite : FixedAssetsRead` to `Capabilities.CapabilityForModule`. Every asset operation flows through `ScopedDocumentStore` → `ModuleAccess`, which then requires `fixedassets.write` (mutations) / `fixedassets.read` (reads). The constants and their `All`-set membership already exist.
- **Role presets:** grant `fixedassets.read` + `fixedassets.write` to the roles that manage subledgers (the same roles that hold `ap.write`/`payroll.write` today — Controller and the relevant module clerk preset). Read-only/assurance roles get `fixedassets.read`. Exact preset membership pinned in the plan against the current `RolePresets`.
- **Entitlement + secrets (free from prior phases):** the installed `"fixedassets"` module registers into every firm's control DB via `ModuleRegistrar` with a persisted secret (Phase-3b + secret-persistence machinery, unchanged). A client reaches the module only if `"fixedassets"` is in its `EnabledModules`; the test fixtures seed that.
- **Host:** `builder.Services.AddFixedAssets(builder.Configuration)` and `app.MapFixedAssetsEndpoints()` in `Accounting101.Host/Program.cs`, alongside the other five modules.

## Testing

Mirrors the existing module suites (EphemeralMongo via `SharedMongo`, real HTTP through `WebApplicationFactory<Program>`):

- **`FixedAssetsHostFixture`** — boots the composition-root host; `SeedClientAsync` registers a client with `EnabledModules = ["fixedassets"]` and a member; the module's loopback needs no ledger client (FA-1 does not post).
- **`AssetDocumentStoreFixture`** — a direct `ScopedDocumentStore` bound to the `fixedassets` identity + a `.Reference("assets")` manifest, for store-level tests.
- **E2E lifecycle:** create an asset → it appears in the list and by id → update it (edit cost/life) → deactivate it → it is excluded unless `includeInactive=true`.
- **Validation:** each rule returns 422 (cost ≤ 0; life ≤ 0; salvage > cost; `DecliningBalance` without a positive factor; a factor supplied for `StraightLine`; blank description).
- **Capability enforcement:** a member without `fixedassets.write` gets 403 creating an asset; a member with only `fixedassets.read` can list but not mutate; a client without `"fixedassets"` entitlement gets 403 (`NotEntitled`).
- **Store unit tests:** create stamps `Active` + `AccumulatedDepreciation = 0`; update preserves both; deactivate flips the reference document inactive.

Each new suite ends green; the whole solution stays green.

## Open questions

None for FA-1. Deferred to FA-2: the `IDepreciationMethod` strategy interface and its `StraightLine` + `DecliningBalance` implementations; the depreciation-run evidentiary document; the posting-accounts provider and the `HttpLedgerClient`; accumulated-depreciation updates. Deferred to FA-3: disposals and gain/loss.
