# Fixed Assets FA-4 — Angular UI Design

**Date:** 2026-07-04
**Status:** Draft for review
**Author:** Michael Jordan (with Claude)
**Builds on:** the shipped FA-1 register + FA-2 depreciation runs + FA-3 disposals backend (`b828d8b`), and the established Angular module-UI pattern (Payroll is the near-exact template: `features/payroll/*` + `core/payroll/*`).

## Problem

The Fixed Assets backend is complete (register, depreciation runs, disposals) but has no UI — the `/fixed-assets` nav leaf currently falls through to the generic `Placeholder`. FA-4 builds the Angular UI so a user can manage the asset register, run depreciation, and dispose assets, following the same shell + per-entity list/editor/detail pattern the other subledger modules use.

## Scope & non-goals

**In scope (FA-4):**
- A `core/fixed-assets/` API service + models, and a `features/fixed-assets/` shell with three tabs — **Assets · Depreciation runs · Disposals**.
- Asset register: list, create/edit editor, detail (with net book value + status).
- Dispose flow: a dispose editor launched from the asset detail (`POST /assets/{id}/dispose`).
- Depreciation runs: list, run editor (pick a period, fire, view), detail with per-asset lines + posted-entry link + void.
- Disposals: list, detail with the gain/loss breakdown + posted-entry link + void.
- Route wiring into `app.routes.ts` (the nav entry + `fixedassets` area gating already exist).
- Capability gating: reads visible with `fixedassets.read` (existing nav-area filter), writes guarded by `fixedassets.write`.
- A `.spec.ts` per component, written RED-then-GREEN.

**Out of scope (deferred / not planned):**
- **Deactivate / reactivate UI.** The asset read model (`AssetView`) does not expose the reference-lifecycle state (a deactivated asset still reads `Status = Active`), so the UI cannot reliably distinguish active from deactivated assets or offer the right lifecycle action without a backend read-model change. Disposal already covers the primary "retire an asset" flow, so deactivate/reactivate UI is deferred to a later polish (the backend still supports both). The asset list shows active + disposed assets; there is no "show inactive" toggle.
- No per-asset depreciation history (no backend "runs for this asset" endpoint; runs are aggregate).
- No dry-run preview of a depreciation run (the run endpoint is atomic; a preview would replicate the backend depreciation math as a second source of truth).
- No bulk actions, no charts/visualizations, no CSV export.

## Established pattern this follows (verified against Payroll)

- **Service** (`core/<module>/<module>.service.ts`): injects `HttpClient` + `ClientContextService`; `base(path)` = `${environment.apiBaseUrl}/clients/${clientId}${path}`; returns `EMPTY` when no client; list methods map the `PagedResponse<XView>` envelope to `PagedResponse<X>` via `pipe(map(...))`; `entriesForSource(sourceRef)` powers the posted-entry link.
- **Models** (`core/<module>/<module>.ts`): domain interfaces + `*View` wrappers + request types + list-query type + label helpers.
- **Shell** (`features/<module>/<module>-shell.ts`): `OnPush`, a tab `<nav>` of `routerLink` + `routerLinkActive` + `<router-outlet/>`.
- **List**: `OnPush`; signals (`skip`/`limit`/`includeVoided`/`error`) + a `computed` query fed through `toObservable`→`switchMap`(service call)→`toSignal`; `hlmTable`; **whole-row click** (`cursor-pointer hover:bg-muted/50 tabindex=0 (click)/(keydown.enter) → router.navigate`); paging (Previous/Next); a `*appCan="'<cap>'"` action button.
- **Editor**: `OnPush`; a reactive form; create + edit modes (edit hydrates from `getX`); submit calls the service then navigates; guarded by the `canWrite` route guard with `data: { requiredCapability, fallback }`.
- **Detail**: `OnPush`; shows the document; a "posted journal entry" link via `entriesForSource`; lifecycle actions (void) gated by `*appCan`.
- **Routes**: a module route with `component: <Shell>` + `children`, editors carry `canActivate: [canWrite]` + `data: { requiredCapability: 'fixedassets.write', fallback: '<list>' }`; specific paths precede `:id`.
- **Formatting**: `money`, `displayDate` from `core/format/display`.
- **Money is number** in the UI (decimals serialized as JSON numbers); dates are ISO `yyyy-MM-dd` strings.

## Components (new)

### `core/fixed-assets/fixed-assets.ts` (models)
- `DepreciationMethod` — the wire value is a number (`0` = StraightLine, `1` = DecliningBalance); a `methodLabel(m)` helper maps to `'Straight line'` / `'Declining balance'`, and the editor's select offers the two.
- `AssetStatus` — wire number (`0` = Active, `1` = Disposed); `statusLabel(s)` → `'Active'` / `'Disposed'`.
- `Asset` — `id`, `description`, `acquisitionCost`, `inServiceDate`, `usefulLifeMonths`, `salvageValue`, `method`, `decliningBalanceFactor` (nullable), `status`, `accumulatedDepreciation`.
- `AssetView` — `{ asset: Asset; netBookValue: number }` (the API response shape).
- `DepreciationRunLine` — `{ assetId: string; amount: number }`.
- `DepreciationRun` — `id`, `number` (nullable), `period: { year: number; month: number }`, `effectiveDate`, `memo` (nullable), `lines: DepreciationRunLine[]`, `total`, `status` (`0` Posted / `1` Voided; `runStatusLabel`).
- `DepreciationRunView` — `{ run: DepreciationRun }`.
- `Disposal` — `id`, `number` (nullable), `assetId`, `disposalDate`, `proceeds`, `catchUpDepreciation`, `accumulatedBeforeDisposal`, `accumulatedAtDisposal`, `netBookValue`, `gainLoss`, `memo` (nullable), `status` (`0` Posted / `1` Voided; `disposalStatusLabel`).
- `DisposalView` — `{ disposal: Disposal }`.
- Request types: `SaveAssetRequest` (the editable fields), `RunDepreciationRequest` (`{ year, month, effectiveDate?, memo? }`), `DisposeAssetRequest` (`{ disposalDate, proceeds, memo? }`), `VoidReasonRequest` (`{ reason?: string | null }`).
- `FixedAssetsListQuery` — `{ skip, limit, order?, includeInactive? , includeVoided? }` (assets use `includeInactive`, runs/disposals use `includeVoided`; the service sets whichever the endpoint takes).

### `core/fixed-assets/fixed-assets.service.ts`
Endpoints (all under `base()`):
- Assets: `listAssets(q)` → `GET /assets` (maps `AssetView[]` → keep the view; the list needs NBV so it maps to `AssetView`, not bare `Asset`); `getAsset(id)` → `GET /assets/{id}` → `AssetView`; `createAsset(req)` → `POST /assets`; `updateAsset(id, req)` → `PUT /assets/{id}`; `disposeAsset(id, req)` → `POST /assets/{id}/dispose` → `DisposalView`.
- Runs: `listRuns(q)` → `GET /depreciation-runs` → `DepreciationRun[]`; `getRun(id)` → `GET /depreciation-runs/{id}`; `runDepreciation(req)` → `POST /depreciation-runs` → `DepreciationRun`; `voidRun(id, reason)` → `POST /depreciation-runs/{id}/void`.
- Disposals: `listDisposals(q)` → `GET /disposals` → `Disposal[]`; `getDisposal(id)` → `GET /disposals/{id}`; `voidDisposal(id, reason)` → `POST /disposals/{id}/void`.
- `entriesForSource(sourceRef)` → `GET /entries?sourceRef=` → `EntryResponse[]` (posted-entry link, reused from the shared entries model).

Note: the asset list keeps the `AssetView` (needs `netBookValue`); runs/disposals map the `*View` envelope to the bare domain like Payroll (`.pipe(map(pg => ({ ...pg, items: pg.items.map(v => v.run) })))`).

### `features/fixed-assets/fixed-assets-shell.ts`
Three tabs (`data-testid` `tab-assets` / `tab-runs` / `tab-disposals`), `routerLink` to `assets` / `depreciation-runs` / `disposals`, `routerLinkActive` underline, `<router-outlet/>`.

### Assets screens
- **`asset-list.ts`** — table columns: Description, Cost, Net book value, Method, Status. Whole-row click → `/fixed-assets/assets/{id}`. `*appCan="'fixedassets.write'"` "New asset" button → `assets/new`. Paging. Empty state "No assets yet." Shows active + disposed assets (no include-inactive toggle).
- **`asset-editor.ts`** — reactive form: `description` (required), `acquisitionCost` (required, > 0), `inServiceDate` (required date), `usefulLifeMonths` (required int > 0), `salvageValue` (required, ≥ 0), `method` (select: Straight line / Declining balance), `decliningBalanceFactor` (shown + required + > 0 only when method = Declining balance; hidden/null otherwise). Create (`assets/new`) posts `createAsset`; edit (`assets/:id/edit`) hydrates via `getAsset` then `updateAsset`. On success → navigate to the asset detail. Server 422 validation messages surfaced inline. Guarded `fixedassets.write`.
- **`asset-detail.ts`** — header (description + status badge), a definition list of the register fields + acquisition cost + accumulated depreciation + **net book value**; actions: **Edit** (→ `assets/:id/edit`) and **Dispose** (→ `assets/:id/dispose`), both shown only when `status = Active` and gated `*appCan="'fixedassets.write'"`. When `status = Disposed`: show the Disposed badge and a "View disposals" link to the Disposals tab (`/fixed-assets/disposals`), with edit/dispose hidden. (There is no backend "disposal by asset" read endpoint exposed to the UI, so the detail links to the disposals list rather than a specific disposal.)

### `dispose-editor.ts` (route `assets/:id/dispose`)
Reactive form: `disposalDate` (required date), `proceeds` (required, ≥ 0; 0 = retirement, with helper text), `memo` (optional). Loads the asset (via `getAsset`) to show a read-only summary (description, cost, current NBV) for context. Submit calls `disposeAsset(id, req)` → navigates to the new disposal's detail (`disposals/:id`). Surfaces 409 (not disposable / already disposed) and 422 (proceeds negative / date before in-service) inline. Guarded `fixedassets.write`.

### Depreciation-runs screens
- **`run-list.ts`** — columns: #, Period (`YYYY-MM`), Total, Status. Show-voided toggle. Whole-row click → `depreciation-runs/:id`. `*appCan` "Run depreciation" button → `depreciation-runs/new`. Paging.
- **`run-editor.ts`** (route `depreciation-runs/new`) — form: `year` (required int), `month` (required 1–12, a select), `effectiveDate` (optional; defaults server-side to the period's last day), `memo` (optional). Submit calls `runDepreciation` → navigates to the run detail. Surfaces 409 ("a run already exists for that period") and 422 ("nothing to depreciate") inline. Guarded `fixedassets.write`.
- **`run-detail.ts`** — header (number, period, effective date, total, status); a per-asset lines table (asset id/short, amount); the **posted journal entry** link (via `entriesForSource(run.id)` → the entry id → `/journal/:entryId`); **Void** button gated `*appCan="'fixedassets.write'"`, shown only when status = Posted; on void, surfaces the 409 for a non-latest run (LIFO) with the returned message, and refreshes.

### Disposals screens
- **`disposal-list.ts`** — columns: #, Asset (short id), Date, Proceeds, Gain/Loss (green gain / red loss), Status. Show-voided toggle. Whole-row click → `disposals/:id`. Paging. (No "new" button — disposals are created from the asset detail.)
- **`disposal-detail.ts`** — header (number, asset, date, status); a breakdown definition list: proceeds, catch-up depreciation, accumulated at disposal, net book value, **gain/loss** (signed, colored); the **posted journal entry** link (via `entriesForSource(disposal.id)`); **Void** button gated `*appCan`, shown only when status = Posted; on void (which reinstates the asset), surfaces any 409 and refreshes.

## Routing

Add to `app.routes.ts` (and add `/fixed-assets` to the `built` array that the placeholder-fallback uses):

```
{ path: 'fixed-assets', component: FixedAssetsShell, children: [
  { path: '', pathMatch: 'full', redirectTo: 'assets' },
  { path: 'assets', component: AssetList },
  { path: 'assets/new', component: AssetEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/assets' } },
  { path: 'assets/:id/edit', component: AssetEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/assets' } },
  { path: 'assets/:id/dispose', component: DisposeEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/assets' } },
  { path: 'assets/:id', component: AssetDetail },
  { path: 'depreciation-runs', component: RunList },
  { path: 'depreciation-runs/new', component: RunEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/depreciation-runs' } },
  { path: 'depreciation-runs/:id', component: RunDetail },
  { path: 'disposals', component: DisposalList },
  { path: 'disposals/:id', component: DisposalDetail },
] },
```

Specific paths precede `:id` (Angular matches in order). The nav leaf `/fixed-assets` (area `fixedassets`) already exists in `layout/nav.ts`; no nav change beyond adding the route tree and the `built`-list entry.

## Error handling

- List loads: the reactive query `catchError`s to `null` and sets an `error` signal → a small `text-destructive` line, matching the Payroll list.
- Editor/action submits: subscribe with an error handler that extracts the server message (`ValidationProblemDetails`/`ProblemDetails` `detail`/`title`) into an inline `error` signal; the known status codes (409 run-already-exists / not-disposable / not-latest-void; 422 nothing-to-depreciate / negative-proceeds / date-before-in-service) show the server's own message rather than a generic string.
- No-client state: the service returns `EMPTY`; screens render their empty state.

## Testing

Each component gets a `.spec.ts`, written RED (fails before the component exists) then GREEN, following the existing `features/payroll/*.spec.ts` style (TestBed, provide a fake/HTTP-mocked `FixedAssetsService` or `HttpTestingController`, a stub `ClientContextService` with a fixed client id, assert rendered rows / form submit calls / navigation / capability-gated element presence):
- **asset-list**: renders rows from a mocked page incl. net book value; the "New asset" button is present with `fixedassets.write` and absent without; a row click navigates.
- **asset-editor**: create submits `createAsset` with the mapped body; the declining-balance factor field appears only when method = Declining balance; edit hydrates from `getAsset`; a 422 shows inline.
- **asset-detail**: renders the register + NBV + status; Edit/Dispose shown for Active + write cap, hidden for Disposed; a disposed asset shows the disposal link.
- **dispose-editor**: renders the asset summary; submit posts `disposeAsset` and navigates to the disposal; a 409 shows inline.
- **run-list**: renders runs; show-voided toggles the query; "Run depreciation" gated.
- **run-editor**: submit posts `runDepreciation` and navigates; a 409/422 shows inline.
- **run-detail**: renders lines + total; the posted-entry link resolves via `entriesForSource`; Void shown only when Posted + write cap; a non-latest void surfaces the 409 message.
- **disposal-list**: renders disposals with signed gain/loss; show-voided toggles.
- **disposal-detail**: renders the breakdown; Void shown only when Posted + write cap; void refreshes.
- **service**: `HttpTestingController` — each method hits the right URL/verb, list methods map the `*View` envelope, `entriesForSource` sets the `sourceRef` param.

Each new spec ends green; the whole Angular unit-test suite stays green.

## Open questions

None. Deferred (not part of FA-4): deactivate/reactivate UI (needs the asset read model to expose reference-lifecycle state), per-asset depreciation history, run dry-run preview.
