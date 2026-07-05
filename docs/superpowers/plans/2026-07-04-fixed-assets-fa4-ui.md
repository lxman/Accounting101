# Fixed Assets FA-4 — Angular UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Angular UI for the Fixed Assets module — asset register (list/editor/detail), dispose flow, depreciation runs (list/editor/detail), and disposals (list/detail) — following the Payroll module-UI pattern.

**Architecture:** A `core/fixed-assets/` API service + models, and a `features/fixed-assets/` shell with three tabs (Assets · Depreciation runs · Disposals), each entity getting list/editor/detail standalone components with reactive signals, whole-row-click lists, `*appCan`/`canWrite` capability gating, and posted-journal-entry links. A thin backend touch first makes the FixedAssets enums serialize as strings (matching every sibling module) so the UI consumes string statuses.

**Tech Stack:** Angular 22 (standalone, zoneless, signals), Tailwind 4 + Spartan (`@spartan-ng/helm/*`), RxJS 7, Vitest (`npm test`) with `HttpTestingController`. Backend: .NET 10 / System.Text.Json.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-07-04-fixed-assets-fa4-ui-design.md`.
- **Base commit:** branch off `master` at the FA-3 merge into a new branch `feat/fixed-assets-fa4`.
- **Stage explicit paths only — NEVER `git add -A` / `git add .`.** Each commit lists its exact files.
- **Leave pre-existing uncommitted working-tree noise untouched:** `UI/Angular/src/app/core/api/environment.ts` (the `devClientId` line is intentionally uncommitted) and the several `*.Tests.csproj` files that show as modified. Do not stage or revert them.
- **Commit trailer, exactly:** `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Money is a JSON number** in the UI; dates are ISO `yyyy-MM-dd` strings.
- **UI test runner:** `npm test` (from `UI/Angular/`) runs the whole Angular unit suite via Vitest. (If the runner accepts a file filter — `npx vitest run <path>` — use it for speed during a task; the whole suite must be green at task end regardless.)
- **Backend test runner (Task 1 only):** `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests` (from repo root). EphemeralMongo can flake on mongod startup — a startup timeout is not a real failure; retry once.
- **Whole Angular suite must stay green at each task and end green.** The FixedAssets backend suite (101 tests) must stay green after Task 1.

**Established UI idioms (verified against `features/payroll/*`):**
- Component: `@Component({ selector, changeDetection: ChangeDetectionStrategy.OnPush, imports: [...], template: `...` })`, standalone (no `standalone: true` needed — default in v22).
- Imports used: `HlmButton` (`@spartan-ng/helm/button`), `HlmInputImports` (`@spartan-ng/helm/input`), `HlmLabelImports` (`@spartan-ng/helm/label`), `HlmTableImports` (`@spartan-ng/helm/table`), `CanDirective` (`../../core/capabilities/can.directive`) → `*appCan="'fixedassets.write'"`, `CurrencyInput` (`../../shared/currency-input`) → `<app-currency-input ariaLabel="X" [value]="sig()" (valueChange)="sig.set($event)" />`.
- Services/helpers: `ClientContextService` (`../../core/client/client-context.service`, `.clientId()` signal), `PagedResponse<T>` (`../../core/api/paged-response`), `EntryResponse` (`../../core/entries/entry`), `extractProblem(e).detail` (`../../core/api/problem-details`), `money`/`displayDate` (`../../core/format/display`), `environment` (`../../core/api/environment`).
- List reactive pattern: signals `skip`/`limit`/`includeVoided`/`error` + `computed` query → `toObservable` → `switchMap`(service, `catchError`→`of(null)`) → `toSignal`. Whole-row click: `class="cursor-pointer hover:bg-muted/50" tabindex="0" (click)="open(id)" (keydown.enter)="open(id)"`.
- Editor/detail pattern: `DestroyRef` + `takeUntilDestroyed(this.destroyRef)` on subscriptions; navigate via `Router`.
- Spec pattern: `TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('fixedassets.write')] })`, `TestBed.inject(ClientContextService).select('C1')`, `HttpTestingController`, URL base `http://localhost:5000/clients/C1/...`. `provideCapabilities` from `../../core/capabilities/capability.testing`.

---

## Task 1: Backend — FixedAssets enums serialize as strings

Add `[JsonConverter(typeof(JsonStringEnumConverter))]` to the four FixedAssets enums so the API returns string status/method values, matching every sibling module (Payroll etc.). This is what lets the UI type these as string unions.

**Files:**
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/AssetStatus.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationMethod.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationRun.cs` (the `DepreciationRunStatus` enum in this file)
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/Disposal.cs` (the `DisposalStatus` enum in this file)

**Interfaces:**
- Consumes: `System.Text.Json.Serialization.JsonConverter`, `JsonStringEnumConverter` (BCL).
- Produces: no API-shape change to C# types; the wire JSON for these four enums becomes their string name (`"Active"`, `"StraightLine"`, `"Posted"`, `"Voided"`, etc.).

- [ ] **Step 1: Add the converter attribute to each enum**

`AssetStatus.cs` — add the using + attribute:

```csharp
using System.Text.Json.Serialization;

namespace Accounting101.FixedAssets;

/// <summary>Register lifecycle of an asset. New assets are Active; FA-3 disposal sets Disposed. FA-1 never
/// sets Disposed. 0-default so a legacy document reads as Active.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetStatus
{
    Active = 0,
    Disposed = 1,
}
```

`DepreciationMethod.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Accounting101.FixedAssets;

/// <summary>How an asset depreciates. Multi-member from the start so FA-2 can add a pluggable strategy
/// without a data migration; FA-1 stores and validates the choice but computes nothing. 0-default so a
/// legacy document reads as straight-line.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DepreciationMethod
{
    StraightLine = 0,
    DecliningBalance = 1,
}
```

In `DepreciationRun.cs`, add `using System.Text.Json.Serialization;` at the top and the attribute on the `DepreciationRunStatus` enum:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DepreciationRunStatus
{
    Posted = 0,
    Voided = 1,
}
```

In `Disposal.cs`, add `using System.Text.Json.Serialization;` at the top and the attribute on the `DisposalStatus` enum:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DisposalStatus
{
    Posted = 0,
    Voided = 1,
}
```

- [ ] **Step 2: Run the FixedAssets backend suite**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: 101/101 pass. The E2E tests deserialize responses into typed views (`AssetView`, `DepreciationRunView`, `DisposalView`) and compare `.Status`/`.Method` as enums; the `JsonStringEnumConverter` round-trips strings↔enum in both directions, so nothing breaks. Report the count.

- [ ] **Step 3: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/AssetStatus.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/DepreciationMethod.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/DepreciationRun.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/Disposal.cs
git commit -m "feat(fixedassets): string-serialize module enums (UI consistency)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: UI models + service

The `core/fixed-assets/` models and API service — the contract every screen consumes. Tested with `HttpTestingController`.

**Files:**
- Create: `UI/Angular/src/app/core/fixed-assets/fixed-assets.ts`
- Create: `UI/Angular/src/app/core/fixed-assets/fixed-assets.service.ts`
- Test: `UI/Angular/src/app/core/fixed-assets/fixed-assets.service.spec.ts`

**Interfaces:**
- Consumes: `HttpClient`, `HttpParams`, `environment`, `ClientContextService`, `PagedResponse<T>`, `EntryResponse`.
- Produces (models): `DepreciationMethod`, `AssetStatus`, `DepreciationRunStatus`, `DisposalStatus` (string unions); `Asset`, `AssetView`, `DepreciationRunLine`, `DepreciationPeriod`, `DepreciationRun`, `DepreciationRunView`, `Disposal`, `DisposalView`; `SaveAssetRequest`, `RunDepreciationRequest`, `DisposeAssetRequest`, `FixedAssetsListQuery`; `methodLabel(m)`.
- Produces (service `FixedAssetsService`): `listAssets(q) → Observable<PagedResponse<AssetView>>`, `getAsset(id) → Observable<AssetView>`, `createAsset(req) → Observable<AssetView>`, `updateAsset(id, req) → Observable<AssetView>`, `disposeAsset(id, req) → Observable<Disposal>`, `listRuns(q) → Observable<PagedResponse<DepreciationRun>>`, `getRun(id) → Observable<DepreciationRun>`, `runDepreciation(req) → Observable<DepreciationRun>`, `voidRun(id, reason) → Observable<DepreciationRun>`, `listDisposals(q) → Observable<PagedResponse<Disposal>>`, `getDisposal(id) → Observable<Disposal>`, `voidDisposal(id, reason) → Observable<Disposal>`, `entriesForSource(sourceRef) → Observable<EntryResponse[]>`.

- [ ] **Step 1: Write the failing service spec**

Create `UI/Angular/src/app/core/fixed-assets/fixed-assets.service.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FixedAssetsService } from './fixed-assets.service';
import { ClientContextService } from '../client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return { svc: TestBed.inject(FixedAssetsService), ctrl: TestBed.inject(HttpTestingController) };
}

describe('FixedAssetsService', () => {
  it('listAssets keeps the AssetView (with net book value)', () => {
    const { svc, ctrl } = setup();
    let items: unknown[] = [];
    svc.listAssets({ skip: 0, limit: 50 }).subscribe(p => (items = p.items));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/assets');
    expect(req.request.params.get('skip')).toBe('0');
    req.flush({ items: [{ asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01',
      usefulLifeMonths: 24, salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null,
      status: 'Active', accumulatedDepreciation: 500 }, netBookValue: 11500 }], total: 1, skip: 0, limit: 50 });
    expect((items[0] as { netBookValue: number }).netBookValue).toBe(11500);
    ctrl.verify();
  });

  it('listRuns maps the run-view envelope to bare runs', () => {
    const { svc, ctrl } = setup();
    let items: unknown[] = [];
    svc.listRuns({ skip: 0, limit: 50, includeVoided: true }).subscribe(p => (items = p.items));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/depreciation-runs');
    expect(req.request.params.get('includeVoided')).toBe('true');
    req.flush({ items: [{ run: { id: 'r1', number: 'DR-00001', period: { year: 2026, month: 1 },
      effectiveDate: '2026-01-31', memo: null, lines: [{ assetId: 'a1', amount: 500 }], total: 500, status: 'Posted' } }],
      total: 1, skip: 0, limit: 50 });
    expect((items[0] as { total: number }).total).toBe(500);
    ctrl.verify();
  });

  it('runDepreciation posts and unwraps the run view', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.runDepreciation({ year: 2026, month: 1, effectiveDate: null, memo: null }).subscribe(r => (got = r));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/depreciation-runs');
    expect(req.request.method).toBe('POST');
    req.flush({ run: { id: 'r1', number: 'DR-00001', period: { year: 2026, month: 1 }, effectiveDate: '2026-01-31',
      memo: null, lines: [], total: 500, status: 'Posted' } });
    expect(got.id).toBe('r1');
    ctrl.verify();
  });

  it('disposeAsset posts to the asset and unwraps the disposal view', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.disposeAsset('a1', { disposalDate: '2026-06-30', proceeds: 10000, memo: null }).subscribe(d => (got = d));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1/dispose');
    expect(req.request.method).toBe('POST');
    req.flush({ disposal: { id: 'd1', number: 'DP-00001', assetId: 'a1', disposalDate: '2026-06-30', proceeds: 10000,
      catchUpDepreciation: 2500, accumulatedBeforeDisposal: 0, accumulatedAtDisposal: 2500, netBookValue: 9500,
      gainLoss: 500, memo: null, status: 'Posted' } });
    expect(got.id).toBe('d1');
    ctrl.verify();
  });

  it('entriesForSource sets the sourceRef param', () => {
    const { svc, ctrl } = setup();
    svc.entriesForSource('d1').subscribe();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries');
    expect(req.request.params.get('sourceRef')).toBe('d1');
    req.flush([]);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test` (in `UI/Angular/`)
Expected: FAIL — `FixedAssetsService` / models don't exist (compile error).

- [ ] **Step 3: Write the models**

Create `UI/Angular/src/app/core/fixed-assets/fixed-assets.ts`:

```ts
export type DepreciationMethod = 'StraightLine' | 'DecliningBalance';
export type AssetStatus = 'Active' | 'Disposed';
export type DepreciationRunStatus = 'Posted' | 'Voided';
export type DisposalStatus = 'Posted' | 'Voided';

export const methodLabel = (m: DepreciationMethod): string =>
  m === 'DecliningBalance' ? 'Declining balance' : 'Straight line';

export interface Asset {
  id: string;
  description: string;
  acquisitionCost: number;
  inServiceDate: string;
  usefulLifeMonths: number;
  salvageValue: number;
  method: DepreciationMethod;
  decliningBalanceFactor: number | null;
  status: AssetStatus;
  accumulatedDepreciation: number;
}
export interface AssetView { asset: Asset; netBookValue: number; }

export interface DepreciationRunLine { assetId: string; amount: number; }
export interface DepreciationPeriod { year: number; month: number; }
export interface DepreciationRun {
  id: string;
  number: string | null;
  period: DepreciationPeriod;
  effectiveDate: string;
  memo: string | null;
  lines: DepreciationRunLine[];
  total: number;
  status: DepreciationRunStatus;
}
export interface DepreciationRunView { run: DepreciationRun; }

export interface Disposal {
  id: string;
  number: string | null;
  assetId: string;
  disposalDate: string;
  proceeds: number;
  catchUpDepreciation: number;
  accumulatedBeforeDisposal: number;
  accumulatedAtDisposal: number;
  netBookValue: number;
  gainLoss: number;
  memo: string | null;
  status: DisposalStatus;
}
export interface DisposalView { disposal: Disposal; }

export interface SaveAssetRequest {
  description: string;
  acquisitionCost: number;
  inServiceDate: string;
  usefulLifeMonths: number;
  salvageValue: number;
  method: DepreciationMethod;
  decliningBalanceFactor: number | null;
}
export interface RunDepreciationRequest { year: number; month: number; effectiveDate?: string | null; memo?: string | null; }
export interface DisposeAssetRequest { disposalDate: string; proceeds: number; memo?: string | null; }

export interface FixedAssetsListQuery {
  skip: number;
  limit: number;
  order?: 'asc' | 'desc';
  includeInactive?: boolean;
  includeVoided?: boolean;
}
```

- [ ] **Step 4: Write the service**

Create `UI/Angular/src/app/core/fixed-assets/fixed-assets.service.ts` (mirrors `core/payroll/payroll.service.ts`):

```ts
import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, map } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import { EntryResponse } from '../entries/entry';
import {
  AssetView, Asset, DepreciationRun, DepreciationRunView, Disposal, DisposalView,
  SaveAssetRequest, RunDepreciationRequest, DisposeAssetRequest, FixedAssetsListQuery,
} from './fixed-assets';

@Injectable({ providedIn: 'root' })
export class FixedAssetsService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  private listParams(q: FixedAssetsListQuery): HttpParams {
    let p = new HttpParams().set('skip', q.skip).set('limit', q.limit);
    if (q.order) p = p.set('order', q.order);
    if (q.includeInactive) p = p.set('includeInactive', true);
    if (q.includeVoided) p = p.set('includeVoided', true);
    return p;
  }

  // ── Assets ────────────────────────────────────────────────────────────────
  listAssets(q: FixedAssetsListQuery): Observable<PagedResponse<AssetView>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<AssetView>>(this.base('/assets'), { params: this.listParams(q) });
  }
  getAsset(id: string): Observable<AssetView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<AssetView>(this.base(`/assets/${id}`));
  }
  createAsset(req: SaveAssetRequest): Observable<AssetView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<AssetView>(this.base('/assets'), req);
  }
  updateAsset(id: string, req: SaveAssetRequest): Observable<AssetView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<AssetView>(this.base(`/assets/${id}`), req);
  }
  disposeAsset(id: string, req: DisposeAssetRequest): Observable<Disposal> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<DisposalView>(this.base(`/assets/${id}/dispose`), req).pipe(map(v => v.disposal));
  }

  // ── Depreciation runs ──────────────────────────────────────────────────────
  listRuns(q: FixedAssetsListQuery): Observable<PagedResponse<DepreciationRun>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<DepreciationRunView>>(this.base('/depreciation-runs'), { params: this.listParams(q) })
      .pipe(map(pg => ({ ...pg, items: pg.items.map(v => v.run) })));
  }
  getRun(id: string): Observable<DepreciationRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<DepreciationRunView>(this.base(`/depreciation-runs/${id}`)).pipe(map(v => v.run));
  }
  runDepreciation(req: RunDepreciationRequest): Observable<DepreciationRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<DepreciationRunView>(this.base('/depreciation-runs'), req).pipe(map(v => v.run));
  }
  voidRun(id: string, reason?: string | null): Observable<DepreciationRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<DepreciationRunView>(this.base(`/depreciation-runs/${id}/void`), { reason: reason ?? null }).pipe(map(v => v.run));
  }

  // ── Disposals ──────────────────────────────────────────────────────────────
  listDisposals(q: FixedAssetsListQuery): Observable<PagedResponse<Disposal>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<DisposalView>>(this.base('/disposals'), { params: this.listParams(q) })
      .pipe(map(pg => ({ ...pg, items: pg.items.map(v => v.disposal) })));
  }
  getDisposal(id: string): Observable<Disposal> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<DisposalView>(this.base(`/disposals/${id}`)).pipe(map(v => v.disposal));
  }
  voidDisposal(id: string, reason?: string | null): Observable<Disposal> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<DisposalView>(this.base(`/disposals/${id}/void`), { reason: reason ?? null }).pipe(map(v => v.disposal));
  }

  /** Posted journal entry(ies) for a fixed-assets document — powers the "posted journal entry" link. */
  entriesForSource(sourceRef: string): Observable<EntryResponse[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<EntryResponse[]>(this.base('/entries'), { params: new HttpParams().set('sourceRef', sourceRef) });
  }
}
```

> Implementer note: confirm `Asset` is imported even if only referenced by other models — trim unused imports so the linter passes (Angular builds treat unused imports as errors in some configs; if the build complains, drop `Asset` from the service import list — it's used only via `AssetView`).

- [ ] **Step 5: Run to verify pass**

Run: `npm test`
Expected: PASS — the 5 service specs plus the whole existing suite. Report the count.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/fixed-assets/fixed-assets.ts \
        UI/Angular/src/app/core/fixed-assets/fixed-assets.service.ts \
        UI/Angular/src/app/core/fixed-assets/fixed-assets.service.spec.ts
git commit -m "feat(fixedassets-ui): core models + API service

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Asset register — list, editor, detail

The three asset-register screens. Standalone components; they compile and their specs pass without routing.

**Files:**
- Create: `UI/Angular/src/app/features/fixed-assets/asset-list.ts` (+ `.spec.ts`)
- Create: `UI/Angular/src/app/features/fixed-assets/asset-editor.ts` (+ `.spec.ts`)
- Create: `UI/Angular/src/app/features/fixed-assets/asset-detail.ts` (+ `.spec.ts`)

**Interfaces:**
- Consumes: `FixedAssetsService`, `AssetView`, `Asset`, `SaveAssetRequest`, `DepreciationMethod`, `methodLabel` (Task 2); the shared UI idioms.
- Produces: `AssetList`, `AssetEditor`, `AssetDetail` components (routed in Task 7).

- [ ] **Step 1: Write the failing specs**

Create `asset-list.spec.ts` (mirrors `run-list.spec.ts`):

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AssetList } from './asset-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps)],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('AssetList', () => {
  it('renders assets with net book value', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(AssetList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/assets').flush({
      items: [{ asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01',
        usefulLifeMonths: 24, salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null,
        status: 'Active', accumulatedDepreciation: 500 }, netBookValue: 11500 }], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Van');
    expect(f.nativeElement.textContent).toContain('11,500');
    ctrl.verify();
  });

  it('hides "New asset" without fixedassets.write', () => {
    const ctrl = setup([]);
    const f = TestBed.createComponent(AssetList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/assets').flush({ items: [], total: 0, skip: 0, limit: 50 });
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).not.toContain('New asset');
    ctrl.verify();
  });
});
```

Create `asset-editor.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute } from '@angular/router';
import { AssetEditor } from './asset-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(paramId: string | null) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('fixedassets.write'),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map(paramId ? [['id', paramId]] : []) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('AssetEditor', () => {
  it('shows the declining-balance factor field only for declining balance', () => {
    const ctrl = setup(null);
    const f = TestBed.createComponent(AssetEditor);
    f.detectChanges();
    const cmp = f.componentInstance as unknown as { method: (m: string) => void; showFactor: () => boolean };
    expect(cmp.showFactor()).toBe(false);
    (f.componentInstance as unknown as { method: { set: (v: string) => void } }).method.set('DecliningBalance');
    expect((f.componentInstance as unknown as { showFactor: () => boolean }).showFactor()).toBe(true);
    ctrl.verify();
  });

  it('create posts the mapped SaveAssetRequest', () => {
    const ctrl = setup(null);
    const f = TestBed.createComponent(AssetEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as {
      description: { set: (v: string) => void }; acquisitionCost: { set: (v: number) => void };
      inServiceDate: { set: (v: string) => void }; usefulLifeMonths: { set: (v: number) => void };
      salvageValue: { set: (v: number) => void }; save: () => void;
    };
    c.description.set('Van'); c.acquisitionCost.set(12000); c.inServiceDate.set('2026-01-01');
    c.usefulLifeMonths.set(24); c.salvageValue.set(0);
    c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/assets');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01',
      usefulLifeMonths: 24, salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null });
    req.flush({ asset: { id: 'a1' }, netBookValue: 12000 });
    ctrl.verify();
  });
});
```

Create `asset-detail.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AssetDetail } from './asset-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'a1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

function flushAsset(ctrl: HttpTestingController, status: string) {
  ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1').flush({
    asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01', usefulLifeMonths: 24,
      salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null, status, accumulatedDepreciation: 2500 },
    netBookValue: 9500 });
}

describe('AssetDetail', () => {
  it('shows Dispose for an active asset with write cap', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(AssetDetail);
    f.detectChanges(); flushAsset(ctrl, 'Active'); f.detectChanges();
    expect(f.nativeElement.textContent).toContain('9,500'); // net book value
    expect(f.nativeElement.textContent).toContain('Dispose');
    ctrl.verify();
  });

  it('hides Dispose for a disposed asset and shows the disposals link', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(AssetDetail);
    f.detectChanges(); flushAsset(ctrl, 'Disposed'); f.detectChanges();
    expect(f.nativeElement.textContent).not.toContain('Dispose asset');
    expect(f.nativeElement.textContent).toContain('View disposals');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test` → FAIL (components don't exist).

- [ ] **Step 3: Write `asset-list.ts`**

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { AssetView, methodLabel } from '../../core/fixed-assets/fixed-assets';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-asset-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Asset register</h1>
        <a *appCan="'fixedassets.write'" hlmBtn size="sm" routerLink="/fixed-assets/assets/new" class="ms-auto">New asset</a>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (assets().length === 0) {
        <p class="text-muted-foreground text-sm">No assets yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr>
                <th hlmTh>Description</th><th hlmTh class="text-right">Cost</th>
                <th hlmTh class="text-right">Net book value</th><th hlmTh>Method</th><th hlmTh>Status</th>
              </tr>
            </thead>
            <tbody hlmTBody>
              @for (v of assets(); track v.asset.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="open(v.asset.id)" (keydown.enter)="open(v.asset.id)">
                  <td hlmTd>{{ v.asset.description }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(v.asset.acquisitionCost) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(v.netBookValue) }}</td>
                  <td hlmTd>{{ method(v.asset.method) }}</td>
                  <td hlmTd>{{ v.asset.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <div class="flex items-center justify-between text-sm text-muted-foreground">
          <span>Page {{ currentPage() }} of {{ pageCount() }}</span>
          <div class="flex gap-2">
            <button hlmBtn variant="outline" size="sm" [disabled]="skip() === 0" (click)="prev()">Previous</button>
            <button hlmBtn variant="outline" size="sm" [disabled]="currentPage() >= pageCount()" (click)="next()">Next</button>
          </div>
        </div>
      }
    </div>
  `,
})
export class AssetList {
  private readonly svc = inject(FixedAssetsService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({ id: this.client.clientId(), skip: this.skip(), limit: this.limit() }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listAssets({ skip, limit }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading assets'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<AssetView> | null },
  );

  readonly assets = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  open(id: string): void { void this.router.navigate(['/fixed-assets/assets', id]); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  money(n: number): string { return fmtMoney(n); }
  method(m: AssetView['asset']['method']): string { return methodLabel(m); }
}
```

- [ ] **Step 4: Write `asset-editor.ts`**

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { DepreciationMethod, SaveAssetRequest } from '../../core/fixed-assets/fixed-assets';
import { extractProblem } from '../../core/api/problem-details';
import { CurrencyInput } from '../../shared/currency-input';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-asset-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">{{ editId() ? 'Edit asset' : 'New asset' }}</h1>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Description</label>
          <input hlmInput type="text" [value]="description()" (input)="description.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Acquisition cost</label>
          <app-currency-input ariaLabel="Acquisition cost" [value]="acquisitionCost()" (valueChange)="acquisitionCost.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>In-service date</label>
          <input hlmInput type="date" [value]="inServiceDate()" (change)="inServiceDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Useful life (months)</label>
          <input hlmInput type="number" min="1" [value]="usefulLifeMonths()" (input)="usefulLifeMonths.set(+$any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Salvage value</label>
          <app-currency-input ariaLabel="Salvage value" [value]="salvageValue()" (valueChange)="salvageValue.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Method</label>
          <select hlmInput [value]="method()" (change)="method.set($any($event.target).value)">
            <option value="StraightLine">Straight line</option>
            <option value="DecliningBalance">Declining balance</option>
          </select>
        </div>
        @if (showFactor()) {
          <div class="flex flex-col gap-1">
            <label hlmLabel>Declining-balance factor</label>
            <input hlmInput type="number" min="0" step="0.1" [value]="factor() ?? ''" (input)="factor.set($any($event.target).value === '' ? null : +$any($event.target).value)" />
          </div>
        }
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'fixedassets.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">
          {{ editId() ? 'Save' : 'Create asset' }}
        </button>
        <a hlmBtn variant="outline" routerLink="/fixed-assets/assets">Cancel</a>
      </div>
    </div>
  `,
})
export class AssetEditor {
  private readonly svc = inject(FixedAssetsService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly editId = signal<string | null>(this.route.snapshot.paramMap.get('id'));
  readonly description = signal('');
  readonly acquisitionCost = signal(0);
  readonly inServiceDate = signal(new Date().toISOString().slice(0, 10));
  readonly usefulLifeMonths = signal(12);
  readonly salvageValue = signal(0);
  readonly method = signal<DepreciationMethod>('StraightLine');
  readonly factor = signal<number | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly showFactor = computed(() => this.method() === 'DecliningBalance');
  readonly canSave = computed(() =>
    this.description().trim().length > 0 && this.acquisitionCost() > 0 && !!this.inServiceDate() &&
    this.usefulLifeMonths() > 0 && this.salvageValue() >= 0 &&
    (this.method() !== 'DecliningBalance' || (this.factor() ?? 0) > 0));

  constructor() {
    const id = this.editId();
    if (id) {
      this.svc.getAsset(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (v) => {
          this.description.set(v.asset.description); this.acquisitionCost.set(v.asset.acquisitionCost);
          this.inServiceDate.set(v.asset.inServiceDate); this.usefulLifeMonths.set(v.asset.usefulLifeMonths);
          this.salvageValue.set(v.asset.salvageValue); this.method.set(v.asset.method); this.factor.set(v.asset.decliningBalanceFactor);
        },
        error: (e) => this.message.set(extractProblem(e).detail),
      });
    }
  }

  private body(): SaveAssetRequest {
    return {
      description: this.description().trim(), acquisitionCost: this.acquisitionCost(), inServiceDate: this.inServiceDate(),
      usefulLifeMonths: this.usefulLifeMonths(), salvageValue: this.salvageValue(), method: this.method(),
      decliningBalanceFactor: this.method() === 'DecliningBalance' ? this.factor() : null,
    };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    const id = this.editId();
    const call = id ? this.svc.updateAsset(id, this.body()) : this.svc.createAsset(this.body());
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => { this.busy.set(false); void this.router.navigate(['/fixed-assets/assets', v.asset.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
```

- [ ] **Step 5: Write `asset-detail.ts`**

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { AssetView, methodLabel } from '../../core/fixed-assets/fixed-assets';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-asset-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/fixed-assets/assets" class="text-sm text-muted-foreground hover:text-foreground">← Assets</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">{{ v.asset.description }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="v.asset.status === 'Disposed'">{{ v.asset.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Acquisition cost</td><td class="text-right tabular-nums">{{ money(v.asset.acquisitionCost) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">In-service date</td><td class="text-right">{{ formatDate(v.asset.inServiceDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Useful life</td><td class="text-right">{{ v.asset.usefulLifeMonths }} months</td></tr>
            <tr><td class="py-1 text-muted-foreground">Salvage value</td><td class="text-right tabular-nums">{{ money(v.asset.salvageValue) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Method</td><td class="text-right">{{ method(v.asset.method) }}</td></tr>
            @if (v.asset.method === 'DecliningBalance') { <tr><td class="py-1 text-muted-foreground">DB factor</td><td class="text-right">{{ v.asset.decliningBalanceFactor }}</td></tr> }
            <tr><td class="py-1 text-muted-foreground">Accumulated depreciation</td><td class="text-right tabular-nums">{{ money(v.asset.accumulatedDepreciation) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Net book value</td><td class="text-right tabular-nums">{{ money(v.netBookValue) }}</td></tr>
          </tbody>
        </table>

        @if (v.asset.status === 'Active') {
          <div *appCan="'fixedassets.write'" class="flex items-center gap-2 border-t border-border pt-4">
            <a hlmBtn variant="outline" [routerLink]="['/fixed-assets/assets', v.asset.id, 'edit']">Edit</a>
            <a hlmBtn [routerLink]="['/fixed-assets/assets', v.asset.id, 'dispose']">Dispose asset</a>
          </div>
        } @else {
          <a routerLink="/fixed-assets/disposals" class="text-sm text-primary hover:underline">View disposals →</a>
        }
      }
    </div>
  `,
})
export class AssetDetail {
  private readonly svc = inject(FixedAssetsService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<AssetView | null>(null);
  readonly message = signal<string | null>(null);

  constructor() {
    this.svc.getAsset(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.view.set(v),
      error: (e) => this.message.set(extractProblem(e).detail),
    });
  }

  method(m: AssetView['asset']['method']): string { return methodLabel(m); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

> Note: the detail's Dispose button label is "Dispose asset" (the spec's disposed-case test asserts the *absence* of "Dispose asset" — the disposed branch has no such button, only the "View disposals" link).

- [ ] **Step 6: Run to verify pass**

Run: `npm test`
Expected: PASS — the 6 asset-screen specs + prior suite. Report the count.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/features/fixed-assets/asset-list.ts \
        UI/Angular/src/app/features/fixed-assets/asset-list.spec.ts \
        UI/Angular/src/app/features/fixed-assets/asset-editor.ts \
        UI/Angular/src/app/features/fixed-assets/asset-editor.spec.ts \
        UI/Angular/src/app/features/fixed-assets/asset-detail.ts \
        UI/Angular/src/app/features/fixed-assets/asset-detail.spec.ts
git commit -m "feat(fixedassets-ui): asset register list, editor, detail

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Dispose editor

The dispose form launched from the asset detail (`assets/:id/dispose`).

**Files:**
- Create: `UI/Angular/src/app/features/fixed-assets/dispose-editor.ts` (+ `.spec.ts`)

**Interfaces:**
- Consumes: `FixedAssetsService` (`getAsset`, `disposeAsset`), `AssetView`, `DisposeAssetRequest`.
- Produces: `DisposeEditor` (routed in Task 7).

- [ ] **Step 1: Write the failing spec**

Create `dispose-editor.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DisposeEditor } from './dispose-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('fixedassets.write'),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'a1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('DisposeEditor', () => {
  it('loads the asset summary and posts the disposal', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(DisposeEditor);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1').flush({
      asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01', usefulLifeMonths: 24,
        salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null, status: 'Active', accumulatedDepreciation: 2500 },
      netBookValue: 9500 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Van');
    const c = f.componentInstance as unknown as { disposalDate: { set: (v: string) => void }; proceeds: { set: (v: number) => void }; save: () => void };
    c.disposalDate.set('2026-06-30'); c.proceeds.set(10000); c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1/dispose');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ disposalDate: '2026-06-30', proceeds: 10000, memo: null });
    req.flush({ disposal: { id: 'd1', number: 'DP-1', assetId: 'a1', disposalDate: '2026-06-30', proceeds: 10000,
      catchUpDepreciation: 0, accumulatedBeforeDisposal: 2500, accumulatedAtDisposal: 2500, netBookValue: 9500, gainLoss: 500, memo: null, status: 'Posted' } });
    ctrl.verify();
  });

  it('shows a server error inline on a rejected dispose', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(DisposeEditor);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1').flush({
      asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01', usefulLifeMonths: 24,
        salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null, status: 'Active', accumulatedDepreciation: 0 }, netBookValue: 12000 });
    f.detectChanges();
    const c = f.componentInstance as unknown as { disposalDate: { set: (v: string) => void }; proceeds: { set: (v: number) => void }; save: () => void };
    c.disposalDate.set('2026-06-30'); c.proceeds.set(1000); c.save();
    ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1/dispose')
      .flush({ detail: 'Asset a1 is Disposed; only an active asset can be disposed.' }, { status: 409, statusText: 'Conflict' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('only an active asset can be disposed');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure** — `npm test` → FAIL.

- [ ] **Step 3: Write `dispose-editor.ts`**

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { AssetView } from '../../core/fixed-assets/fixed-assets';
import { extractProblem } from '../../core/api/problem-details';
import { CurrencyInput } from '../../shared/currency-input';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-dispose-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Dispose asset</h1>

      @if (asset(); as v) {
        <div class="text-sm text-muted-foreground border-b border-border pb-2">
          {{ v.asset.description }} · cost {{ money(v.asset.acquisitionCost) }} · net book value {{ money(v.netBookValue) }}
        </div>
      }

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Disposal date</label>
          <input hlmInput type="date" [value]="disposalDate()" (change)="disposalDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Proceeds</label>
          <app-currency-input ariaLabel="Proceeds" [value]="proceeds()" (valueChange)="proceeds.set($event)" />
          <span class="text-xs text-muted-foreground">Enter 0 for a retirement / scrap.</span>
        </div>
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'fixedassets.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Dispose</button>
        <a hlmBtn variant="outline" [routerLink]="['/fixed-assets/assets', id]">Cancel</a>
      </div>
    </div>
  `,
})
export class DisposeEditor {
  private readonly svc = inject(FixedAssetsService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly asset = signal<AssetView | null>(null);
  readonly disposalDate = signal(new Date().toISOString().slice(0, 10));
  readonly proceeds = signal(0);
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly canSave = computed(() => !!this.disposalDate() && this.proceeds() >= 0);

  constructor() {
    this.svc.getAsset(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.asset.set(v),
      error: (e) => this.message.set(extractProblem(e).detail),
    });
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.disposeAsset(this.id, { disposalDate: this.disposalDate(), proceeds: this.proceeds(), memo: this.memo() })
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (d) => { this.busy.set(false); void this.router.navigate(['/fixed-assets/disposals', d.id]); },
        error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
      });
  }

  money(n: number): string { return fmtMoney(n); }
}
```

- [ ] **Step 4: Run to verify pass** — `npm test` → PASS. Report the count.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/fixed-assets/dispose-editor.ts \
        UI/Angular/src/app/features/fixed-assets/dispose-editor.spec.ts
git commit -m "feat(fixedassets-ui): dispose editor

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Depreciation runs — list, editor, detail

**Files:**
- Create: `UI/Angular/src/app/features/fixed-assets/run-list.ts` (+ `.spec.ts`)
- Create: `UI/Angular/src/app/features/fixed-assets/run-editor.ts` (+ `.spec.ts`)
- Create: `UI/Angular/src/app/features/fixed-assets/run-detail.ts` (+ `.spec.ts`)

**Interfaces:**
- Consumes: `FixedAssetsService` (`listRuns`/`getRun`/`runDepreciation`/`voidRun`/`entriesForSource`), `DepreciationRun`.
- Produces: `RunList`, `RunEditor`, `RunDetail` (routed in Task 7).

- [ ] **Step 1: Write the failing specs**

Create `run-list.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunList } from './run-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps)],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('FA RunList', () => {
  it('renders runs with period and total', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(RunList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/depreciation-runs').flush({
      items: [{ run: { id: 'r1', number: 'DR-00001', period: { year: 2026, month: 1 }, effectiveDate: '2026-01-31',
        memo: null, lines: [], total: 1500, status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('DR-00001');
    expect(f.nativeElement.textContent).toContain('2026-01');
    expect(f.nativeElement.textContent).toContain('1,500');
    ctrl.verify();
  });
});
```

Create `run-editor.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunEditor } from './run-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('fixedassets.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('FA RunEditor', () => {
  it('posts the run request', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as { year: { set: (v: number) => void }; month: { set: (v: number) => void }; save: () => void };
    c.year.set(2026); c.month.set(1); c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/depreciation-runs');
    expect(req.request.body).toEqual({ year: 2026, month: 1, effectiveDate: null, memo: null });
    req.flush({ run: { id: 'r1', number: 'DR-1', period: { year: 2026, month: 1 }, effectiveDate: '2026-01-31', memo: null, lines: [], total: 500, status: 'Posted' } });
    ctrl.verify();
  });

  it('shows the 422 nothing-to-depreciate message inline', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as { year: { set: (v: number) => void }; month: { set: (v: number) => void }; save: () => void };
    c.year.set(2026); c.month.set(1); c.save();
    ctrl.expectOne('http://localhost:5000/clients/C1/depreciation-runs')
      .flush({ detail: 'No assets to depreciate for 2026-01.' }, { status: 422, statusText: 'Unprocessable' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No assets to depreciate');
    ctrl.verify();
  });
});
```

Create `run-detail.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunDetail } from './run-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'r1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('FA RunDetail', () => {
  it('renders lines + total and resolves the posted entry link', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(RunDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/depreciation-runs/r1').flush({
      run: { id: 'r1', number: 'DR-1', period: { year: 2026, month: 1 }, effectiveDate: '2026-01-31', memo: null,
        lines: [{ assetId: 'a1', amount: 500 }], total: 500, status: 'Posted' } });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries').flush([{ id: 'e1' }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('500');
    expect(f.nativeElement.textContent).toContain('Void');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure** — `npm test` → FAIL.

- [ ] **Step 3: Write `run-list.ts`** (mirror `asset-list.ts`; columns #, Period, Total, Status; show-voided toggle; row → `/fixed-assets/depreciation-runs/:id`; action → `depreciation-runs/new`):

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { DepreciationRun } from '../../core/fixed-assets/fixed-assets';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-fa-run-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Depreciation runs</h1>
        <a *appCan="'fixedassets.write'" hlmBtn size="sm" routerLink="/fixed-assets/depreciation-runs/new" class="ms-auto">Run depreciation</a>
        <label class="flex items-center gap-2 text-sm text-muted-foreground">
          <input type="checkbox" [checked]="includeVoided()" (change)="toggleVoided($any($event.target).checked)" /> Show voided
        </label>
      </div>
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (runs().length === 0) {
        <p class="text-muted-foreground text-sm">No depreciation runs yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>#</th><th hlmTh>Period</th><th hlmTh class="text-right">Total</th><th hlmTh>Status</th></tr></thead>
            <tbody hlmTBody>
              @for (run of runs(); track run.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0" (click)="open(run.id)" (keydown.enter)="open(run.id)">
                  <td hlmTd>{{ run.number ?? '—' }}</td>
                  <td hlmTd>{{ period(run) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(run.total) }}</td>
                  <td hlmTd>{{ run.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <div class="flex items-center justify-between text-sm text-muted-foreground">
          <span>Page {{ currentPage() }} of {{ pageCount() }}</span>
          <div class="flex gap-2">
            <button hlmBtn variant="outline" size="sm" [disabled]="skip() === 0" (click)="prev()">Previous</button>
            <button hlmBtn variant="outline" size="sm" [disabled]="currentPage() >= pageCount()" (click)="next()">Next</button>
          </div>
        </div>
      }
    </div>
  `,
})
export class RunList {
  private readonly svc = inject(FixedAssetsService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly includeVoided = signal(false);
  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({ id: this.client.clientId(), includeVoided: this.includeVoided(), skip: this.skip(), limit: this.limit() }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, includeVoided, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listRuns({ skip, limit, includeVoided }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading runs'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<DepreciationRun> | null },
  );

  readonly runs = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  period(r: DepreciationRun): string { return `${r.period.year}-${String(r.period.month).padStart(2, '0')}`; }
  open(id: string): void { void this.router.navigate(['/fixed-assets/depreciation-runs', id]); }
  toggleVoided(v: boolean): void { this.includeVoided.set(v); this.skip.set(0); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  money(n: number): string { return fmtMoney(n); }
}
```

- [ ] **Step 4: Write `run-editor.ts`**

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-fa-run-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Run depreciation</h1>
      <p class="text-sm text-muted-foreground">Depreciation for the chosen month is computed for every eligible asset and posted as one journal entry (pending approval).</p>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Year</label>
          <input hlmInput type="number" [value]="year()" (input)="year.set(+$any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Month</label>
          <select hlmInput [value]="month()" (change)="month.set(+$any($event.target).value)">
            @for (m of months; track m) { <option [value]="m">{{ m }}</option> }
          </select>
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Effective date (optional)</label>
          <input hlmInput type="date" [value]="effectiveDate() ?? ''" (change)="effectiveDate.set($any($event.target).value || null)" />
        </div>
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'fixedassets.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Run depreciation</button>
        <a hlmBtn variant="outline" routerLink="/fixed-assets/depreciation-runs">Cancel</a>
      </div>
    </div>
  `,
})
export class RunEditor {
  private readonly svc = inject(FixedAssetsService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly months = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
  readonly year = signal(new Date().getFullYear());
  readonly month = signal(new Date().getMonth() + 1);
  readonly effectiveDate = signal<string | null>(null);
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly canSave = computed(() => this.year() > 0 && this.month() >= 1 && this.month() <= 12);

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.runDepreciation({ year: this.year(), month: this.month(), effectiveDate: this.effectiveDate(), memo: this.memo() })
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (run) => { this.busy.set(false); void this.router.navigate(['/fixed-assets/depreciation-runs', run.id]); },
        error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
      });
  }
}
```

- [ ] **Step 5: Write `run-detail.ts`** (mirror the Payroll `run-detail.ts`; per-asset lines table; posted-entry link; void):

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { DepreciationRun } from '../../core/fixed-assets/fixed-assets';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-fa-run-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton, CanDirective, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/fixed-assets/depreciation-runs" class="text-sm text-muted-foreground hover:text-foreground">← Depreciation runs</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (run(); as r) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Depreciation run {{ r.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="r.status === 'Voided'">{{ r.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Period</td><td class="text-right">{{ r.period.year }}-{{ pad(r.period.month) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Effective date</td><td class="text-right">{{ formatDate(r.effectiveDate) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Total</td><td class="text-right tabular-nums">{{ money(r.total) }}</td></tr>
            @if (r.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ r.memo }}</td></tr> }
          </tbody>
        </table>

        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>Asset</th><th hlmTh class="text-right">Depreciation</th></tr></thead>
            <tbody hlmTBody>
              @for (line of r.lines; track line.assetId) {
                <tr hlmTr>
                  <td hlmTd><a [routerLink]="['/fixed-assets/assets', line.assetId]" class="text-primary hover:underline">{{ shortId(line.assetId) }}</a></td>
                  <td hlmTd class="text-right tabular-nums">{{ money(line.amount) }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        @if (postedEntryId(); as eid) {
          <a [routerLink]="['/journal', eid]" class="text-sm text-primary hover:underline">Posted journal entry →</a>
        }

        @if (r.status === 'Posted') {
          <div *appCan="'fixedassets.write'" class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)" [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button hlmBtn type="button" variant="outline" (click)="void()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class RunDetail {
  private readonly svc = inject(FixedAssetsService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly run = signal<DepreciationRun | null>(null);
  readonly postedEntryId = signal<string | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getRun(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (r) => { this.run.set(r); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
    this.svc.entriesForSource(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (entries) => this.postedEntryId.set(entries[0]?.id ?? null),
      error: () => this.postedEntryId.set(null),
    });
  }

  void(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidRun(this.id, this.reason()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  pad(m: number): string { return String(m).padStart(2, '0'); }
  shortId(id: string): string { return id.slice(0, 8); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 6: Run to verify pass** — `npm test` → PASS. Report the count.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/features/fixed-assets/run-list.ts \
        UI/Angular/src/app/features/fixed-assets/run-list.spec.ts \
        UI/Angular/src/app/features/fixed-assets/run-editor.ts \
        UI/Angular/src/app/features/fixed-assets/run-editor.spec.ts \
        UI/Angular/src/app/features/fixed-assets/run-detail.ts \
        UI/Angular/src/app/features/fixed-assets/run-detail.spec.ts
git commit -m "feat(fixedassets-ui): depreciation run list, editor, detail

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Disposals — list, detail

**Files:**
- Create: `UI/Angular/src/app/features/fixed-assets/disposal-list.ts` (+ `.spec.ts`)
- Create: `UI/Angular/src/app/features/fixed-assets/disposal-detail.ts` (+ `.spec.ts`)

**Interfaces:**
- Consumes: `FixedAssetsService` (`listDisposals`/`getDisposal`/`voidDisposal`/`entriesForSource`), `Disposal`.
- Produces: `DisposalList`, `DisposalDetail` (routed in Task 7).

- [ ] **Step 1: Write the failing specs**

Create `disposal-list.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DisposalList } from './disposal-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('fixedassets.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('DisposalList', () => {
  it('renders disposals with signed gain/loss', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(DisposalList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/disposals').flush({
      items: [{ disposal: { id: 'd1', number: 'DP-00001', assetId: 'a1abcdef', disposalDate: '2026-06-30', proceeds: 10000,
        catchUpDepreciation: 2500, accumulatedBeforeDisposal: 0, accumulatedAtDisposal: 2500, netBookValue: 9500, gainLoss: 500, memo: null, status: 'Posted' } }],
      total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('DP-00001');
    expect(f.nativeElement.textContent).toContain('500');
    ctrl.verify();
  });
});
```

Create `disposal-detail.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DisposalDetail } from './disposal-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'd1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('DisposalDetail', () => {
  it('renders the gain/loss breakdown and Void for a posted disposal', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(DisposalDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/disposals/d1').flush({
      id: 'd1', number: 'DP-1', assetId: 'a1', disposalDate: '2026-06-30', proceeds: 10000, catchUpDepreciation: 2500,
      accumulatedBeforeDisposal: 0, accumulatedAtDisposal: 2500, netBookValue: 9500, gainLoss: 500, memo: null, status: 'Posted' });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries').flush([{ id: 'e1' }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('9,500'); // NBV
    expect(f.nativeElement.textContent).toContain('Void');
    ctrl.verify();
  });
});
```

> Note: `getDisposal` returns the bare `Disposal` (the service unwraps `DisposalView`), so the detail spec flushes a bare disposal object (not wrapped in `{ disposal: ... }`).

- [ ] **Step 2: Run to verify failure** — `npm test` → FAIL.

- [ ] **Step 3: Write `disposal-list.ts`** (mirror `run-list.ts`; columns #, Asset, Date, Proceeds, Gain/Loss, Status; row → `/fixed-assets/disposals/:id`; no "new" button):

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { Disposal } from '../../core/fixed-assets/fixed-assets';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';

@Component({
  selector: 'app-disposal-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Disposals</h1>
        <label class="flex items-center gap-2 text-sm text-muted-foreground ms-auto">
          <input type="checkbox" [checked]="includeVoided()" (change)="toggleVoided($any($event.target).checked)" /> Show voided
        </label>
      </div>
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (disposals().length === 0) {
        <p class="text-muted-foreground text-sm">No disposals yet. Dispose an asset from its detail page.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>#</th><th hlmTh>Asset</th><th hlmTh>Date</th><th hlmTh class="text-right">Proceeds</th><th hlmTh class="text-right">Gain / loss</th><th hlmTh>Status</th></tr></thead>
            <tbody hlmTBody>
              @for (d of disposals(); track d.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0" (click)="open(d.id)" (keydown.enter)="open(d.id)">
                  <td hlmTd>{{ d.number ?? '—' }}</td>
                  <td hlmTd>{{ shortId(d.assetId) }}</td>
                  <td hlmTd>{{ formatDate(d.disposalDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(d.proceeds) }}</td>
                  <td hlmTd class="text-right tabular-nums" [class.text-emerald-600]="d.gainLoss > 0" [class.text-destructive]="d.gainLoss < 0">{{ money(d.gainLoss) }}</td>
                  <td hlmTd>{{ d.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <div class="flex items-center justify-between text-sm text-muted-foreground">
          <span>Page {{ currentPage() }} of {{ pageCount() }}</span>
          <div class="flex gap-2">
            <button hlmBtn variant="outline" size="sm" [disabled]="skip() === 0" (click)="prev()">Previous</button>
            <button hlmBtn variant="outline" size="sm" [disabled]="currentPage() >= pageCount()" (click)="next()">Next</button>
          </div>
        </div>
      }
    </div>
  `,
})
export class DisposalList {
  private readonly svc = inject(FixedAssetsService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly includeVoided = signal(false);
  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({ id: this.client.clientId(), includeVoided: this.includeVoided(), skip: this.skip(), limit: this.limit() }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, includeVoided, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listDisposals({ skip, limit, includeVoided }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading disposals'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<Disposal> | null },
  );

  readonly disposals = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  shortId(id: string): string { return id.slice(0, 8); }
  open(id: string): void { void this.router.navigate(['/fixed-assets/disposals', id]); }
  toggleVoided(v: boolean): void { this.includeVoided.set(v); this.skip.set(0); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Write `disposal-detail.ts`** (mirror `run-detail.ts`; breakdown + posted-entry link + void):

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { Disposal } from '../../core/fixed-assets/fixed-assets';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-disposal-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/fixed-assets/disposals" class="text-sm text-muted-foreground hover:text-foreground">← Disposals</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (disposal(); as d) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Disposal {{ d.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="d.status === 'Voided'">{{ d.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Asset</td><td class="text-right"><a [routerLink]="['/fixed-assets/assets', d.assetId]" class="text-primary hover:underline">{{ shortId(d.assetId) }}</a></td></tr>
            <tr><td class="py-1 text-muted-foreground">Disposal date</td><td class="text-right">{{ formatDate(d.disposalDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Proceeds</td><td class="text-right tabular-nums">{{ money(d.proceeds) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Catch-up depreciation</td><td class="text-right tabular-nums">{{ money(d.catchUpDepreciation) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Accumulated at disposal</td><td class="text-right tabular-nums">{{ money(d.accumulatedAtDisposal) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Net book value</td><td class="text-right tabular-nums">{{ money(d.netBookValue) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Gain / loss</td><td class="text-right tabular-nums" [class.text-emerald-600]="d.gainLoss > 0" [class.text-destructive]="d.gainLoss < 0">{{ money(d.gainLoss) }}</td></tr>
            @if (d.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ d.memo }}</td></tr> }
          </tbody>
        </table>

        @if (postedEntryId(); as eid) {
          <a [routerLink]="['/journal', eid]" class="text-sm text-primary hover:underline">Posted journal entry →</a>
        }

        @if (d.status === 'Posted') {
          <div *appCan="'fixedassets.write'" class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)" [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button hlmBtn type="button" variant="outline" (click)="void()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class DisposalDetail {
  private readonly svc = inject(FixedAssetsService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly disposal = signal<Disposal | null>(null);
  readonly postedEntryId = signal<string | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getDisposal(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (d) => { this.disposal.set(d); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
    this.svc.entriesForSource(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (entries) => this.postedEntryId.set(entries[0]?.id ?? null),
      error: () => this.postedEntryId.set(null),
    });
  }

  void(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidDisposal(this.id, this.reason()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  shortId(id: string): string { return id.slice(0, 8); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 5: Run to verify pass** — `npm test` → PASS. Report the count.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/fixed-assets/disposal-list.ts \
        UI/Angular/src/app/features/fixed-assets/disposal-list.spec.ts \
        UI/Angular/src/app/features/fixed-assets/disposal-detail.ts \
        UI/Angular/src/app/features/fixed-assets/disposal-detail.spec.ts
git commit -m "feat(fixedassets-ui): disposal list + detail

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: Shell + route wiring

The three-tab shell and the `fixed-assets` route tree (replacing the placeholder fallback). Now that every component exists, the route file's imports compile.

**Files:**
- Create: `UI/Angular/src/app/features/fixed-assets/fixed-assets-shell.ts` (+ `.spec.ts`)
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: all nine `features/fixed-assets/*` components + `canWrite` guard.
- Produces: `FixedAssetsShell`; the routed module.

- [ ] **Step 1: Write the shell + its spec**

Create `fixed-assets-shell.ts` (mirror `payroll-shell.ts`):

```ts
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-fixed-assets-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="flex flex-col gap-0">
      <nav class="flex gap-1 border-b border-border px-4 pt-4">
        <a routerLink="assets" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-assets">Assets</a>
        <a routerLink="depreciation-runs" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-runs">Depreciation runs</a>
        <a routerLink="disposals" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-disposals">Disposals</a>
      </nav>
      <router-outlet />
    </div>
  `,
})
export class FixedAssetsShell {}
```

Create `fixed-assets-shell.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { FixedAssetsShell } from './fixed-assets-shell';

describe('FixedAssetsShell', () => {
  it('renders the three tabs', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([])] });
    const f = TestBed.createComponent(FixedAssetsShell);
    f.detectChanges();
    const el = f.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="tab-assets"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="tab-runs"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="tab-disposals"]')).toBeTruthy();
  });
});
```

- [ ] **Step 2: Wire the routes**

Edit `UI/Angular/src/app/app.routes.ts`:
- Add the component imports near the other feature imports:

```ts
import { FixedAssetsShell } from './features/fixed-assets/fixed-assets-shell';
import { AssetList } from './features/fixed-assets/asset-list';
import { AssetEditor } from './features/fixed-assets/asset-editor';
import { AssetDetail } from './features/fixed-assets/asset-detail';
import { DisposeEditor } from './features/fixed-assets/dispose-editor';
import { RunList as FaRunList } from './features/fixed-assets/run-list';
import { RunEditor as FaRunEditor } from './features/fixed-assets/run-editor';
import { RunDetail as FaRunDetail } from './features/fixed-assets/run-detail';
import { DisposalList } from './features/fixed-assets/disposal-list';
import { DisposalDetail } from './features/fixed-assets/disposal-detail';
```

(Alias the run components to `FaRunList`/`FaRunEditor`/`FaRunDetail` to avoid a name clash with the Payroll `RunList`/`RunEditor`/`RunDetail` already imported in this file.)

- Add the route tree right after the `payroll` route block:

```ts
  { path: 'fixed-assets', component: FixedAssetsShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'assets' },
    { path: 'assets', component: AssetList },
    { path: 'assets/new', component: AssetEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/assets' } },
    { path: 'assets/:id/edit', component: AssetEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/assets' } },
    { path: 'assets/:id/dispose', component: DisposeEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/assets' } },
    { path: 'assets/:id', component: AssetDetail },
    { path: 'depreciation-runs', component: FaRunList },
    { path: 'depreciation-runs/new', component: FaRunEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/depreciation-runs' } },
    { path: 'depreciation-runs/:id', component: FaRunDetail },
    { path: 'disposals', component: DisposalList },
    { path: 'disposals/:id', component: DisposalDetail },
  ] },
```

- Add `/fixed-assets` to the `built` array in the placeholder-fallback IIFE:

```ts
    const built = ['/dashboard', '/journal', '/trial-balance', '/statements', '/accounts', '/receivables', '/payables', '/payroll', '/fixed-assets', '/admin/users', '/admin/access/sets', '/admin/access/sets/new'];
```

- [ ] **Step 3: Run the whole suite**

Run: `npm test`
Expected: PASS — the shell spec + all FA specs + the whole existing suite (the route file now compiles with all FA components present, and the `/fixed-assets` leaf no longer maps to `Placeholder`). Report the count.

- [ ] **Step 4: Build to confirm no template/route errors**

Run: `npm run build` (in `UI/Angular/`)
Expected: build succeeds (the FA route tree + all components compile).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/fixed-assets/fixed-assets-shell.ts \
        UI/Angular/src/app/features/fixed-assets/fixed-assets-shell.spec.ts \
        UI/Angular/src/app/app.routes.ts
git commit -m "feat(fixedassets-ui): three-tab shell + route wiring

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 8: Whole-suite green + whole-branch review + memory

- [ ] **Step 1: Whole Angular suite + build**

Run (in `UI/Angular/`): `npm test`
Expected: all specs green. Report the total (baseline + the ~15 new FA specs).
Run: `npm run build`
Expected: build succeeds, 0 errors.

- [ ] **Step 2: Backend suite unaffected**

Run (repo root): `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: 101/101 (Task 1's converter change stayed green).

- [ ] **Step 3: Whole-branch review**

Dispatch the whole-branch reviewer (opus) over `master..HEAD`. Focus: the enum-converter change is correct + backend-safe; the service maps every `*View` envelope correctly and returns `EMPTY` with no client; the string-union models match the wire; capability gating (`*appCan` on every write action + `canWrite` on every editor route); whole-row click on every list; error surfacing via `extractProblem`; the route tree order (specific before `:id`) + the run-component alias avoids the Payroll name clash + `/fixed-assets` added to `built`; no scope creep (no deactivate UI, no preview, no per-asset history). Address Critical/Important before merge; record Minor/Nit as deferred.

- [ ] **Step 4: Merge, push, delete branch, update memory**

After review passes: merge `feat/fixed-assets-fa4` into `master` with `--no-ff`, push, delete the branch, and update the FA memory file (`accounting101-fixed-assets-fa1.md` → note FA-4 shipped, the module UI is complete, the enum-string-serialization touch, and the deferred deactivate/reactivate UI) + the `MEMORY.md` pointer.

---

## Self-Review

**Spec coverage:**
- Backend enum string-serialization touch → Task 1. ✓
- Models + service (all endpoints, `*View` mapping, `entriesForSource`) → Task 2. ✓
- Asset list/editor/detail (NBV, method-conditional factor field, disposed-vs-active actions) → Task 3. ✓
- Dispose editor (asset summary, proceeds=0 retirement, 409/422 inline) → Task 4. ✓
- Run list/editor/detail (period, fire-and-view, per-asset lines, posted-entry link, void, 409/422) → Task 5. ✓
- Disposal list/detail (signed gain/loss, breakdown, posted-entry link, void) → Task 6. ✓
- Shell + three tabs + route tree + `built` list + capability gating → Task 7. ✓
- Whole-suite green + review + memory → Task 8. ✓
- Deferred (no task, by design): deactivate/reactivate UI, per-asset history, run preview. ✓

**Type consistency:** the models (Task 2) — `AssetView`/`DepreciationRun`/`Disposal` + the string-union statuses + `SaveAssetRequest`/`RunDepreciationRequest`/`DisposeAssetRequest` — are the exact shapes every component (Tasks 3–6) consumes. `FixedAssetsService` method names/returns in Task 2 match every call site: `listAssets→PagedResponse<AssetView>`, `getAsset/createAsset/updateAsset→AssetView`, `disposeAsset→Disposal`, `listRuns→PagedResponse<DepreciationRun>`, `getRun/runDepreciation/voidRun→DepreciationRun`, `listDisposals→PagedResponse<Disposal>`, `getDisposal/voidDisposal→Disposal`, `entriesForSource→EntryResponse[]`. Route paths used in `router.navigate`/`routerLink` across components (`/fixed-assets/assets/:id`, `/fixed-assets/depreciation-runs/:id`, `/fixed-assets/disposals/:id`, `.../assets/:id/edit`, `.../assets/:id/dispose`) match the Task-7 route tree. The Payroll-name-clash in `app.routes.ts` is resolved by aliasing the FA run components on import (`FaRunList`/`FaRunEditor`/`FaRunDetail`).

**Placeholder scan:** every code step carries complete code; every spec carries complete test code. No "TBD"/"handle errors"/"similar to Task N". The two implementer-note callouts (trim an unused `Asset` import if the linter flags it; the disposal detail flushes a bare `Disposal` because the service unwraps the view) are precise clarifications, not deferred work. The one wire-format assumption (FA enums serialize as strings) is guaranteed by Task 1, which runs first.
