# Fixed Assets FA-2 — Depreciation Runs Design

**Date:** 2026-07-04
**Status:** Draft for review
**Author:** Michael Jordan (with Claude)
**Builds on:** FA-1 asset register (`Modules/FixedAssets/`, merge `228abd7`), the Payroll evidentiary-posting precedent (`PayrollService` / `PayrollPosting` / `ConfiguredPayrollAccountsProvider`), the engine document store + ledger client, capability RBAC, and Phase-3b module entitlement.

## Problem

FA-1 stood up the asset register (master data: what we own, its cost, in-service date, depreciation method + parameters) but computes nothing and posts nothing. FA-2 is the first slice that **depreciates**: each period it computes per-asset depreciation, advances each asset's accumulated depreciation, and posts one aggregate journal entry to the GL. It also resolves the one behavior question FA-1 deferred (deactivation stickiness), because depreciation runs iterate the active asset set.

## Scope & non-goals

**In scope (FA-2):**
- A **pluggable depreciation strategy** (`IDepreciationMethod`) with a `StraightLine` and a `DecliningBalance` implementation, selected by the asset's stored `Method`.
- A **depreciation run** evidentiary document (numbered, finalized, voidable) covering one period across all eligible assets, storing the per-asset breakdown.
- Run orchestration: compute → persist run → advance each asset's `AccumulatedDepreciation` → post one aggregate PendingApproval GL entry.
- **LIFO void:** only the latest non-voided run can be voided; void reverses the GL entry and rolls each asset's accumulated depreciation back.
- A configured posting-accounts provider + the module's first `HttpLedgerClient`.
- **Deactivation stickiness:** updating a deactivated asset returns 409; a new `reactivate` endpoint is the only way back. (Touches the FA-1 store.)
- Capability + entitlement enforcement (free from prior phases) extended to the new endpoints.

**Out of scope (later slices):**
- **FA-3:** disposals (retire an asset, post gain/loss vs net book value), the `Disposed` status transition.
- **FA-4:** Angular UI.
- No SL/DB crossover, no per-asset-class posting accounts, no automatic catch-up across skipped periods, no partial-period proration (see Resolved decisions).

## Resolved decisions

1. **Full-month convention.** An asset in service on any day of a month earns a full month's depreciation for that period; there is no partial-period proration. The in-service month is the asset's first depreciable period.
2. **One run = one period × all eligible assets → one aggregate entry.** A run targets a `Period` (year + month). It gives each eligible asset exactly one month of depreciation and posts a single balanced entry (Dr Depreciation Expense / Cr Accumulated Depreciation) for the run total. A **period guard** rejects a second non-voided run for the same period; there is no automatic catch-up across skipped months (each missed month is run later as its own one-month run).
3. **LIFO void.** Because accumulated depreciation is cumulative and declining-balance depends on prior net book value, only the most recent non-voided run may be voided; older runs must be voided newest-first. Void reverses the GL entry (or withdraws it if still pending) and decrements each asset's accumulated depreciation by that run's line amounts. No recomputation of other runs is ever required.
4. **Declining-balance: salvage-floor cap, no SL crossover.** Each period takes `NetBookValue × (factor / life)`, floored at salvage; the period that would cross salvage takes exactly the remainder down to salvage; once net book value equals salvage the method returns 0. No automatic switch to straight-line.
5. **Deactivation is sticky with an explicit reactivate.** `UpdateAsync` returns 409 on an inactive asset; `POST /assets/{id}/reactivate` is the only path back to active. Depreciation runs exclude inactive assets.
6. **Single configured account pair.** `FixedAssets:Accounts:DepreciationExpense` (expense) + `FixedAssets:Accounts:AccumulatedDepreciation` (contra-asset), resolved per client from configuration like the Payroll provider. No hardcoded account numbers, no per-class accounts.

## The depreciation strategy (pluggable)

`Accounting101.FixedAssets.IDepreciationMethod` — a pure port, one implementation per `DepreciationMethod` member, resolved by a small selector (dictionary keyed by enum, no service locator):

```csharp
public interface IDepreciationMethod
{
    DepreciationMethod Method { get; }
    /// <summary>The depreciation for ONE period given the asset's current stored state
    /// (AcquisitionCost, SalvageValue, UsefulLifeMonths, AccumulatedDepreciation, and — for
    /// declining balance — DecliningBalanceFactor). Pure; never negative; never drives
    /// AccumulatedDepreciation past (Cost − Salvage).</summary>
    decimal DepreciationForPeriod(Asset asset);
}
```

- **`StraightLineDepreciation`**: `depreciableBase = AcquisitionCost − SalvageValue`; `monthly = depreciableBase / UsefulLifeMonths`; `remaining = depreciableBase − AccumulatedDepreciation`; returns `Math.Min(monthly, remaining)` (so the final period takes the exact remainder); returns 0 when `remaining <= 0`. Rounded to cents (`decimal`, `MidpointRounding.ToEven`).
- **`DecliningBalanceDepreciation`**: `nbv = AcquisitionCost − AccumulatedDepreciation`; `rate = DecliningBalanceFactor / UsefulLifeMonths`; `raw = nbv × rate`; `floorRemaining = nbv − SalvageValue`; returns `Math.Min(raw, floorRemaining)` (final period takes the remainder to salvage); returns 0 when `floorRemaining <= 0`. Rounded to cents. `DecliningBalanceFactor` is guaranteed present + positive by FA-1 validation.

Both are pure functions of the asset's stored state — no period argument needed (full-month convention means every eligible period is one uniform month), no GL, no I/O. This is the seam FA-3/future methods extend.

## The depreciation run (evidentiary document)

New evidentiary collection `depreciation-runs` (numbered, finalized-on-create, voidable), mirroring `PayrollRun`:

- **`DepreciationPeriod`** — `(int Year, int Month)` value object; equatable; the period-guard key and the source of the default effective date (last day of the month).
- **`DepreciationRunLine`** — `(Guid AssetId, decimal Amount)`.
- **`DepreciationRunBody`** (input to persist) — `Period`, `EffectiveDate`, `Memo?`, `Lines`, `Total`.
- **`DepreciationRunStatus`** — `Posted = 0`, `Voided = 1`.
- **`DepreciationRun`** — `Id`, `Number`, `Period`, `EffectiveDate`, `Memo?`, `Lines`, `Total`, `Status`.
- **`DepreciationRunView`** — response shape (full run).
- **Request DTO** `RunDepreciationRequest` — `Year`, `Month`, `EffectiveDate?` (defaults to last day of the period), `Memo?`. The caller supplies only the period + optional overrides; the amounts are server-computed.

`IDepreciationRunStore` / `DocumentDepreciationRunStore` mirror `IPayrollRunStore` / `DocumentPayrollRunStore` (evidentiary create/get/paged-query/void/count), backed by `GetRequiredKeyedService<IDocumentStore>("fixedassets")` on the evidentiary `depreciation-runs` manifest. Adds one query the guard needs: `Task<DepreciationRun?> GetByPeriodAsync(Guid clientId, DepreciationPeriod period, CancellationToken ct)` returning the non-voided run for that period if any, and `Task<DepreciationRun?> GetLatestAsync(Guid clientId, CancellationToken ct)` returning the most recent non-voided run (for the LIFO void guard).

## Asset store additions (server-owned mutation + stickiness)

`IAssetStore` / `DocumentAssetStore` gain:
- `Task ApplyDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct)` — increments each named asset's `AccumulatedDepreciation` by its line amount (server-owned field; re-reads each doc, adds, writes back). Used by run post.
- `Task ReverseDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct)` — decrements each asset's `AccumulatedDepreciation` by its line amount. Used by void.
- `Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid assetId, CancellationToken ct)` — reference-doc reactivate (`NotFound` / `AlreadyActive` / `Reactivated`).
- `UpdateAsync` now **returns 409 semantics** when the target is inactive (a new `UpdateResult`-style signal, mirroring `DeactivateResult`): the store checks lifecycle state before `PutAsync` and refuses to resurrect a deactivated asset via edit.

A read that lists **eligible** assets for a run: reuse `GetByClientPagedAsync(..., includeInactive: false, ...)` (or a dedicated `GetActiveAsync`) — the run enumerates only active assets whose in-service month ≤ the run period and whose accumulated depreciation is below `Cost − Salvage`.

## Orchestration

**`FixedAssetsRunService`** (new; depends on `IAssetStore`, `IDepreciationRunStore`, the `IDepreciationMethod` selector, `IFixedAssetsAccountsProvider`, `ILedgerClient`):

`RunDepreciationAsync(Guid clientId, DepreciationRunBody request, CancellationToken ct)`:
1. **Period guard** — if `runs.GetByPeriodAsync(clientId, period)` returns a non-voided run → `InvalidOperationException` → 409.
2. **Enumerate eligible assets** — active, in-service month ≤ period, `AccumulatedDepreciation < AcquisitionCost − SalvageValue`.
3. For each, `selector[asset.Method].DepreciationForPeriod(asset)`; drop zero amounts; build lines + total.
4. **Nothing to depreciate** — if no non-zero lines → `ArgumentException` → 422 (no doc, no entry).
5. **Persist** the evidentiary run (finalized, numbered).
6. **Advance** each asset via `assets.ApplyDepreciationAsync(clientId, lines)`.
7. **Compose + post** one aggregate entry via `FixedAssetsPosting.ComposeDepreciationRun(runId, total, period/effectiveDate, accounts)` (Dr DepreciationExpense / Cr AccumulatedDepreciation, `EntryIdentity.ForSource("DepreciationRun", runId)`, `SourceType="DepreciationRun"`, `SourceRef=runId`), posted **PendingApproval** — the module never self-approves.

`VoidRunAsync(Guid clientId, Guid runId, string? reason, CancellationToken ct)`:
1. Require the run; require `Status == Posted` (else 409).
2. **LIFO guard** — if `runs.GetLatestAsync(clientId)` is not this run → 409 (must void newest-first).
3. Find the spawned entry by source ref; if `Posting == "Posted"` → `ReverseAsync` (effective on the run date), else `VoidAsync` (withdraw pending) — Payroll precedent.
4. **Roll back** via `assets.ReverseDepreciationAsync(clientId, run.Lines)`.
5. Mark the run `Voided`.

**`FixedAssetsPosting`** — pure recipe (request in, `PostEntryRequest` out), mirroring `PayrollPosting`; `public const string DepreciationRunSourceType = "DepreciationRun";`. Two explicit lines even if total is small; throws `ArgumentException` if total ≤ 0 (guarded upstream by the nothing-to-depreciate check).

## Endpoints

Extend `FixedAssetsEndpoints` (or a sibling `DepreciationRunEndpoints` mapped by the same module) under `/clients/{clientId:guid}`, `RequireAuthorization()`, all write ops gated by `fixedassets.write` and reads by `fixedassets.read` (enforced at the `ScopedDocumentStore` / `LedgerGateway` chokepoint):

- `POST /assets/{assetId:guid}/reactivate` → 200 `AssetView` (404 missing, 409 already active)
- `PUT /assets/{assetId:guid}` → now 409 when the asset is inactive (reactivate first)
- `POST /depreciation-runs` (body `RunDepreciationRequest`) → 201 `DepreciationRunView` (409 period already run, 422 nothing to depreciate)
- `POST /depreciation-runs/{runId:guid}/void` → 200 `DepreciationRunView` (404 missing, 409 not posted / not the latest run)
- `GET /depreciation-runs/{runId:guid}` → 200 `DepreciationRunView` / 404
- `GET /depreciation-runs?skip=&limit=&order=asc|desc&includeVoided=` → 200 `PagedResponse<DepreciationRunView>`

## Platform wiring

- **`AddFixedAssets`** gains, on the existing `AddModule(new ModuleIdentity("fixedassets"), "Fixed Assets", manifest => { … })` call, a second imperative manifest line `manifest.Evidentiary("depreciation-runs");` alongside the existing `manifest.Reference("assets");`. It also registers: the `DocumentDepreciationRunStore` (keyed `IDocumentStore("fixedassets")`), the `IDepreciationMethod` implementations + selector, `IFixedAssetsAccountsProvider` → `ConfiguredFixedAssetsAccountsProvider`, `FixedAssetsRunService`, and the ledger HttpClient (below).
- **`HttpLedgerClient`** (module's first) — copy the Payroll loopback client. Register it with an **explicit client name** (`services.AddHttpClient("FixedAssetsLedgerClient", …).AddTypedClient<ILedgerClient, HttpLedgerClient>()`) reading `Engine:BaseAddress`, exactly like Payroll — this avoids the documented short-name collision between the several modules that each declare a type named `ILedgerClient`. The host fixture repoints the base address at the in-process ledger, as the Payroll fixture does.
- **Capability / entitlement** — unchanged machinery; the new endpoints inherit enforcement through the chokepoint (`CapabilityForModule("fixedassets")` already added in FA-1; `RolePresets` already grants the caps).
- **Config** — `FixedAssets:Accounts:DepreciationExpense` + `:AccumulatedDepreciation` in the host/dev config and the test fixture's seeded accounts.

## Testing

Mirrors the existing module suites (EphemeralMongo via `SharedMongo`, real HTTP through `WebApplicationFactory<Program>`); every new suite ends green and the whole solution stays green (baseline 890).

**Pure strategy unit tests (no Mongo, no host):**
- StraightLine: uniform monthly amount; final period takes the exact remainder (no over-depreciation past `Cost − Salvage`); returns 0 when fully depreciated; cent rounding.
- DecliningBalance: `NBV × rate` each period; salvage floor honored; the crossing period takes exactly the remainder to salvage; returns 0 once `NBV == Salvage`; cent rounding.
- Full-month timing is a run-level concern (eligibility), asserted in the run tests, not the pure math.

**Run E2E (host):**
- Create two assets (one SL, one DB) → run period P → each asset's `AccumulatedDepreciation` advanced by exactly its computed month → one aggregate entry exists PendingApproval with `SourceType=DepreciationRun` and total = sum of lines → run appears in the list and by id.
- **Period guard:** a second run for the same period → 409.
- **Nothing to depreciate:** a period with no eligible assets (all inactive / fully depreciated / not yet in service) → 422, no run doc, no entry.
- **In-service timing:** an asset with in-service month after the run period is excluded; in the in-service month it earns a full month.
- **LIFO void:** void the latest run → its entry reverses (or withdraws if still pending) and each asset's accumulated depreciation rolls back to the pre-run value; voiding a non-latest run → 409; voiding an already-voided run → 409.
- **Reactivate lifecycle:** deactivate an asset → `PUT` returns 409 → `reactivate` → `PUT` succeeds; reactivating an active asset → 409; a deactivated asset is excluded from a run, and after reactivation is included.
- **Capability / entitlement:** a member without `fixedassets.write` gets 403 running/ voiding; a read-only member can list runs; a client without the `fixedassets` entitlement gets 403.

## Components (new)

- **Domain (`Accounting101.FixedAssets`):** `IDepreciationMethod`, `StraightLineDepreciation`, `DecliningBalanceDepreciation`, the method selector; `DepreciationPeriod`, `DepreciationRunLine`, `DepreciationRunBody`, `DepreciationRunStatus`, `DepreciationRun`, `DepreciationRunView`; `IDepreciationRunStore`, `DocumentDepreciationRunStore`; `FixedAssetsPosting`; `ILedgerClient` port; `FixedAssetsRunService`; `IAssetStore`/`DocumentAssetStore` additions (`ApplyDepreciationAsync`, `ReverseDepreciationAsync`, `ReactivateAsync`, sticky `UpdateAsync`, `ReactivateResult`).
- **Api (`Accounting101.FixedAssets.Api`):** `HttpLedgerClient`, `ConfiguredFixedAssetsAccountsProvider`, `IFixedAssetsAccountsProvider`, `FixedAssetsPostingAccounts`, depreciation-run + reactivate endpoints, `RunDepreciationRequest`, expanded `AddFixedAssets`.
- **Tests (`Accounting101.FixedAssets.Tests`):** strategy unit suite; depreciation-run store fixture + suite; expanded host fixture (ledger loopback repoint + seeded posting accounts) + run/reactivate E2E suite.

## Open questions

None. Deferred to FA-3: disposals, the `Disposed` transition, gain/loss vs net book value, and whether disposal auto-voids or coexists with an open depreciation schedule. Deferred to FA-4: the Angular UI.
