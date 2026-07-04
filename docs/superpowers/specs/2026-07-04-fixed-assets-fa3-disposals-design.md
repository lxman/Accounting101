# Fixed Assets FA-3 — Disposals Design

**Date:** 2026-07-04
**Status:** Draft for review
**Author:** Michael Jordan (with Claude)
**Builds on:** FA-1 asset register (`228abd7`) + FA-2 depreciation runs (`44a7fc9`) in `Modules/FixedAssets/`, the Payroll evidentiary-posting precedent, the engine document store + ledger client, capability RBAC, and Phase-3b module entitlement.

## Problem

FA-1 built the register and FA-2 depreciates it; nothing retires an asset. FA-3 disposes an asset — sale or retirement — depreciating it current to the disposal date, removing its cost and accumulated depreciation from the books, recording proceeds and the resulting gain or loss, and transitioning it to `Disposed`. It also opportunistically closes FA-2's deferred non-transactional-run recovery gap.

## Scope & non-goals

**In scope (FA-3):**
- A **disposal** evidentiary document (numbered, finalized, voidable), one per asset, that posts one aggregate PendingApproval GL entry.
- **Auto-catch-up depreciation** to the disposal month, booked as one line inside the disposal entry (no separate depreciation run).
- **Gain/loss** vs net book value, posted to **separate** configured Gain-on-Disposal and Loss-on-Disposal accounts.
- The **`Disposed`** status transition; disposed assets are excluded from depreciation runs and reject further edit/deactivate/re-dispose.
- **Void** a disposal: reverse the entry, roll accumulated depreciation back, return the asset to `Active`.
- Configured posting accounts extending FA-2's set.
- **Folded-in FA-2 fix:** resolve accounts before persist/apply in `RunDepreciationAsync`; make `VoidRunAsync` tolerate a missing entry.

**Out of scope (later / not planned):**
- **FA-4:** Angular UI.
- Partial-month / mid-month / actual-days conventions (full-month only, matching FA-2).
- Per-asset-class posting accounts (single configured set).
- Bulk / mass disposal (one asset per disposal).
- Trade-in / like-kind exchange, disposal reversal-into-a-new-asset.

## Resolved decisions

1. **Auto-run depreciation to the disposal month first.** Disposal depreciates the asset current to its disposal date before computing gain/loss (see "Auto-catch-up mechanism").
2. **Full-month convention at disposal: the disposal month earns NO depreciation** (mirror of the acquisition month earning a full month in FA-2). `targetMonths = min(monthsBetween(inServiceMonth, disposalMonth), UsefulLifeMonths)`; same-month acquire-and-dispose → 0 months.
3. **One combined GL entry** removes the asset AND books the catch-up depreciation (see "Posting recipe"). No separate depreciation-run document is created by a disposal.
4. **Separate Gain-on-Disposal and Loss-on-Disposal accounts** (not one combined account); the recipe picks one by the sign of `proceeds − NBV`.
5. **Configured accounts, consistent with FA-2.** Extend the `FixedAssetsPostingAccounts` record + `ConfiguredFixedAssetsAccountsProvider` with `AssetCost`, `DisposalProceeds`, `GainOnDisposal`, `LossOnDisposal`; reuse `DepreciationExpense` + `AccumulatedDepreciation`.
6. **No LIFO for disposals.** Disposals are per-asset and independent (unlike cumulative depreciation runs), so any posted disposal is voidable on its own.
7. **Disposal is per-asset, one active disposal at a time.** An asset with `Status == Active` can be disposed; a `Disposed` asset rejects edit / deactivate / re-dispose (409) until its disposal is voided.
8. **Fold in the FA-2 deferral fix** (accounts-before-apply + void-tolerates-missing-entry).

## Auto-catch-up mechanism

Because every FA-2 depreciation run applies exactly one month via the same pure `IDepreciationMethod`, an asset's stored `AccumulatedDepreciation` after K runs equals iterating the method K times from zero — deterministic. So a disposal computes the depreciation the asset *should* have by its disposal date and books only the shortfall, without tracking K:

- `targetMonths = min(MonthsBetween(InServiceDate, DisposalDate), UsefulLifeMonths)`, where `MonthsBetween` counts whole months from the in-service **month** up to but **excluding** the disposal **month**: `(disposalYear*12 + disposalMonth) − (inServiceYear*12 + inServiceMonth)`, floored at 0.
- A pure helper `DepreciationSchedule.AccumulatedAfter(Asset asset, int months)` iterates the asset's method `months` times from `AccumulatedDepreciation = 0`, returning `targetAccumulated`. (Lives beside the FA-1/FA-2 strategy code; reuses `DepreciationMethodSelector`.)
- `catchUp = max(0m, targetAccumulated − currentAccumulated)`.
- `finalAccumulated = currentAccumulated + catchUp` (equals `targetAccumulated` when a catch-up is applied; equals `currentAccumulated` when the asset is already current or over-depreciated).
- `NBV = AcquisitionCost − finalAccumulated`.
- `gainLoss = proceeds − NBV` (positive = gain, negative = loss).

`catchUp` is clamped at 0, so an asset that was over-depreciated (e.g. runs posted beyond the disposal date) is never reversed by a disposal — it simply books no catch-up.

## Posting recipe

`FixedAssetsDisposalPosting.ComposeDisposal(disposalId, inputs, accounts)` → one balanced `PostEntryRequest`, `SourceType = "Disposal"`, `Id = EntryIdentity.ForSource("Disposal", disposalId)`, effective on the disposal date. Lines (zero-amount lines are **omitted**, unlike Payroll's fixed shape — a disposal's structure genuinely varies by proceeds / catch-up / gain-vs-loss):

- Dr **DepreciationExpense** = `catchUp` — omit when `catchUp == 0`.
- Dr **AccumulatedDepreciation** = `currentAccumulated` — clears the contra already on the books. (The catch-up's own contribution to accumulated depreciation and its immediate clearing net to zero, so only `currentAccumulated` is cleared here.) Omit when `currentAccumulated == 0`.
- Dr **DisposalProceeds** (cash) = `proceeds` — omit when `proceeds == 0` (a retirement/scrap).
- Cr **AssetCost** = `AcquisitionCost` — always present (> 0 by FA-1 validation).
- plug: Cr **GainOnDisposal** = `gainLoss` when `gainLoss > 0`; Dr **LossOnDisposal** = `−gainLoss` when `gainLoss < 0`; omit when `gainLoss == 0`.

**Balance proof.** Debits `= catchUp + currentAccumulated + proceeds (+ loss)`; Credits `= AcquisitionCost (+ gain)`. With `gainLoss = proceeds − (AcquisitionCost − currentAccumulated − catchUp)`:
- Gain case: Credits `= AcquisitionCost + gainLoss = proceeds + currentAccumulated + catchUp =` Debits. ✓
- Loss case: Debits `= catchUp + currentAccumulated + proceeds + (−gainLoss) = AcquisitionCost =` Credits. ✓

The recipe throws `ArgumentException` if `AcquisitionCost <= 0` (guarded upstream by FA-1 validation) or if the computed line set is empty (cannot happen — `AssetCost` is always present).

## The disposal document

Evidentiary collection `disposals` (numbered `DP-{seq:D5}`, finalized-on-create, voidable), mirroring `DocumentDepreciationRunStore`:

- **`DisposalBody`** (stored body): `AssetId`, `DisposalDate` (DateOnly), `Proceeds` (decimal), `CatchUpDepreciation` (decimal), `AccumulatedBeforeDisposal` (decimal — the `currentAccumulated`, needed to roll back on void), `AccumulatedAtDisposal` (decimal — `finalAccumulated`), `NetBookValue` (decimal), `GainLoss` (decimal, signed), `Memo` (string?).
- **`DisposalStatus`** — `Posted = 0`, `Voided = 1`.
- **`Disposal`** — `Id`, `Number` (string?), + the body fields, `Status`.
- **`DisposalView`** — `DisposalView(Disposal Disposal)`.
- **Request DTO** `DisposeAssetRequest(DateOnly DisposalDate, decimal Proceeds, string? Memo)` (Api).

`IDisposalStore` / `DocumentDisposalStore` mirror `IDepreciationRunStore` / `DocumentDepreciationRunStore`: `RecordAsync`, `VoidAsync`, `GetAsync`, `GetByClientPagedAsync`, plus `GetActiveByAssetAsync(Guid clientId, Guid assetId, CancellationToken ct)` returning the non-voided disposal for an asset if one exists (guards re-dispose + locates the doc to void). Uses the unbounded no-limit query for the by-asset lookup (the same lesson as FA-2's `GetByPeriodAsync` — a supplied limit is clamped to 200).

## Asset store additions

`IAssetStore` / `DocumentAssetStore` gain two server-owned lifecycle mutations (the store already owns `Status` + `AccumulatedDepreciation`):
- `Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, decimal finalAccumulated, CancellationToken ct)` — re-reads the asset; refuses (`NotFound` / `NotActive`) if missing or not `Status == Active`; else writes `Status = Disposed` + `AccumulatedDepreciation = finalAccumulated`. Returns a small `readonly record struct DisposeStamp(DisposeOutcome Outcome, Asset? Asset, decimal PriorAccumulated)` carrying the outcome, the stamped asset, and the pre-disposal `currentAccumulated` (a re-read safety value; the service computes the catch-up from the asset it loaded in step 1). `enum DisposeOutcome { NotFound, NotActive, Disposed }`.
- `Task ReinstateAsync(Guid clientId, Guid assetId, decimal restoreAccumulated, CancellationToken ct)` — re-reads the asset; writes `Status = Active` + `AccumulatedDepreciation = restoreAccumulated`. Used by disposal void. (Re-Put of a reference doc keeps it Active; this only changes body fields.)

`UpdateAsync`, `DeactivateAsync`, `ReactivateAsync` gain a **disposed guard**: a `Status == Disposed` asset returns 409 on edit / deactivate / reactivate (a disposed asset is frozen until its disposal is voided). `UpdateResult` gains a `Disposed` outcome; deactivate/reactivate map disposed → a 409 problem. (Editing a disposed asset must not silently succeed.)

## Run-engine integration

`FixedAssetsRunService.RunDepreciationAsync` eligibility gains a `asset.Status == AssetStatus.Active` filter: a `Disposed` asset is still reference-Active, so `includeInactive: false` alone will not skip it. Add the status check alongside the existing `OnOrAfterServiceMonth` + positive-amount checks. (A disposal already advanced the asset to its final accumulated and froze it; it must not depreciate further.)

## Orchestration

**`FixedAssetsDisposalService`** (depends on `IAssetStore`, `IDisposalStore`, `DepreciationMethodSelector`, `IFixedAssetsAccountsProvider`, `ILedgerClient`):

`DisposeAsync(Guid clientId, DisposeAssetRequest request, Guid assetId, CancellationToken ct)`:
1. **Validate** — `Proceeds >= 0`; load the asset; asset must exist, be reference-Active, and `Status == Active` (else `InvalidOperationException` → 409); `DisposalDate >= InServiceDate` (else `ArgumentException` → 422); re-dispose guard via `GetActiveByAssetAsync` (a live disposal exists → 409).
2. **Resolve accounts FIRST** (before any persistence — the FA-2 lesson: a config error must fail before side effects).
3. **Compute** `targetMonths`, `targetAccumulated` (via `DepreciationSchedule.AccumulatedAfter`), `catchUp`, `finalAccumulated`, `NBV`, `gainLoss`.
4. **Persist** the disposal doc (evidentiary, finalized) with the full breakdown.
5. **Stamp** the asset: `MarkDisposedAsync(clientId, assetId, finalAccumulated)` → sets `Disposed` + `AccumulatedDepreciation = finalAccumulated`.
6. **Compose + post** one PendingApproval entry via `FixedAssetsDisposalPosting.ComposeDisposal`.

`VoidDisposalAsync(Guid clientId, Guid disposalId, string? reason, CancellationToken ct)`:
1. Require the disposal; require `Status == Posted` (else 409).
2. Find the spawned entry by source ref; if `Posting == "Posted"` → `ReverseAsync` (effective on the disposal date), else `VoidAsync` (withdraw pending). **Tolerate a missing entry** (same robustness as the folded-in run fix — still reinstate + void the doc).
3. **Reinstate** the asset: `ReinstateAsync(clientId, assetId, disposal.AccumulatedBeforeDisposal)` → `Active` + accumulated rolled back to its pre-disposal value.
4. Void the disposal doc.

`GetDisposalAsync` / paged list round out the read surface.

## FA-2 deferral fix (folded in)

- **`RunDepreciationAsync`:** move `accounts.GetAccountsAsync(...)` to before `runs.RecordAsync` / `assets.ApplyDepreciationAsync`, so an unconfigured-accounts failure throws before any side effect (no stranded run).
- **`VoidRunAsync`:** when no spawned entry is found for the run's source ref, do NOT throw; skip the reverse/withdraw and still `ReverseDepreciationAsync` + void the doc — so a run stranded by a post failure is recoverable.

## Endpoints

Extend `FixedAssetsEndpoints` under `/clients/{clientId:guid}`, `RequireAuthorization()`, write ops gated by `fixedassets.write`, reads by `fixedassets.read` (enforced at the chokepoint):

- `POST /assets/{assetId:guid}/dispose` (body `DisposeAssetRequest`) → 201 `DisposalView` (409 not disposable / re-dispose, 422 date-before-in-service / negative proceeds)
- `POST /disposals/{disposalId:guid}/void` → 200 `DisposalView` (404 missing, 409 not posted)
- `GET /disposals/{disposalId:guid}` → 200 `DisposalView` / 404
- `GET /disposals?skip=&limit=&order=asc|desc&includeVoided=` → 200 `PagedResponse<DisposalView>`

## Platform wiring

- **`AddFixedAssets`** gains: `manifest.Evidentiary("disposals")`, the `DocumentDisposalStore` registration, and `FixedAssetsDisposalService`. The strategy selector, ledger client, and provider are already registered (FA-2); the provider's returned record simply carries four more account ids.
- **`ConfiguredFixedAssetsAccountsProvider`** reads four new keys: `FixedAssets:Accounts:AssetCost`, `:DisposalProceeds`, `:GainOnDisposal`, `:LossOnDisposal` (all required Guids, same throw-if-missing pattern).
- **Host fixture / dev config** seed the four new accounts alongside FA-2's two.
- Capability / entitlement — unchanged machinery; the new endpoints inherit enforcement through the chokepoint.

## Testing

Mirrors the existing suites (EphemeralMongo via `SharedMongo`, real HTTP through `WebApplicationFactory<Program>`); every new suite ends green and the whole solution stays green (baseline 938).

**Pure unit tests (no Mongo):**
- `DepreciationSchedule.AccumulatedAfter`: SL and DB, iterating N months equals the sum of N single-period computations; capped at the method's floor; `months == 0` → 0.
- `MonthsBetween` / `targetMonths`: in-service Jan → dispose Jun = 5; same month = 0; capped at useful life; disposal before in-service handled (caller validates, but the helper floors at 0).
- `FixedAssetsDisposalPosting.ComposeDisposal`: gain case (all lines, balanced, Cr Gain), loss case (Dr Loss, balanced), retirement (proceeds 0 → no cash line, balanced), no-catch-up (catchUp 0 → no depreciation-expense line), zero gain/loss (no plug line), deterministic id + `SourceType == "Disposal"`.

**Disposal store tests (EphemeralMongo):** record assigns `DP-#####` + Posted; round-trips the breakdown; `GetActiveByAssetAsync` finds a non-voided disposal and ignores voided; paged list excludes voided unless requested.

**Asset store tests:** `MarkDisposedAsync` sets Disposed + finalAccumulated and returns prior accumulated; refuses a non-Active asset; `ReinstateAsync` restores Active + accumulated; the disposed guard makes `UpdateAsync`/`DeactivateAsync`/`ReactivateAsync` return the 409-mapped outcome on a disposed asset.

**Run-service unit test:** a `Disposed` asset is excluded from a depreciation run (eligibility filter).

**FA-2 fix unit tests:** `RunDepreciationAsync` throws (no doc, no apply) when accounts are unresolved before persistence; `VoidRunAsync` still rolls accumulated back + marks Voided when the ledger reports no spawned entry.

**Disposal service unit tests (fakes):** dispose computes catch-up + gain/loss, advances the asset, marks Disposed, posts one entry; a non-Active asset → rejected; a re-dispose → rejected; void reverses the entry, reinstates the asset to its pre-disposal accumulated, and flips it Active.

**HTTP E2E:** sale with gain (proceeds > NBV) → one balanced PendingApproval entry via `fixedassets`, asset `Disposed` with `AccumulatedDepreciation == finalAccumulated`, disposal retrievable; retirement (proceeds 0) with loss; dispose-then-run-depreciation excludes the disposed asset; re-dispose → 409; edit/deactivate a disposed asset → 409; void → entry reversed, asset back to `Active` with accumulated restored, re-dispose now allowed; capability 403 (read-only member, same client) and entitlement 403 (`enabledModules: []`).

## Components (new)

- **Domain (`Accounting101.FixedAssets`):** `DepreciationSchedule` (`AccumulatedAfter`, `MonthsBetween`); `DisposalBody`, `DisposalStatus`, `Disposal`, `DisposalView`; `IDisposalStore`, `DocumentDisposalStore`; `FixedAssetsDisposalPosting`; `FixedAssetsDisposalService`; `IAssetStore`/`DocumentAssetStore` additions (`MarkDisposedAsync`, `ReinstateAsync`, disposed guards, `DisposeStamp`/`DisposeOutcome`, `UpdateResult.Disposed`); extended `FixedAssetsPostingAccounts` (four new ids); `FixedAssetsRunService` eligibility + deferral-fix edits.
- **Api (`Accounting101.FixedAssets.Api`):** `DisposeAssetRequest`; dispose + disposal-void + get + list endpoints; `ConfiguredFixedAssetsAccountsProvider` four new keys; expanded `AddFixedAssets`.
- **Tests (`Accounting101.FixedAssets.Tests`):** schedule/posting unit suites; disposal store fixture + suite; asset-store disposed-lifecycle tests; disposal-service fake-based suite; run-service disposed-exclusion + FA-2-fix tests; disposal HTTP E2E; host-fixture new accounts.

## Open questions

None. Deferred to FA-4: the Angular UI. Not planned: partial-month conventions, per-class accounts, bulk disposal, trade-in/exchange.
