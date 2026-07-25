# EInvoicing Inbound UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Angular preview-and-confirm screen that turns an uploaded EN 16931 e-invoice into a reviewed draft vendor bill.

**Architecture:** One standalone component under `features/einvoicing/` with an `upload → preview` stage signal, backed by a thin HTTP service and two pure helper functions in `core/einvoicing/`. The route nests under the existing Payables shell at `/payables/bills/import`, guarded by a new `moduleEnabled` route guard. The screen posts to Payables' existing `POST /clients/{id}/bills` — it never writes through the e-invoicing module, which is stateless by design.

**Tech Stack:** Angular 22 (standalone components, signals, `ChangeDetectionStrategy.OnPush`, inline templates), spartan-ng helm UI primitives, Tailwind 4, RxJS 7, Vitest 4 + jsdom.

**Spec:** `docs/superpowers/specs/2026-07-25-einvoicing-inbound-ui-design.md`

## Global Constraints

- All work happens in the **public `Accounting101` repo**, on branch `feat/einvoicing-inbound-ui`. Nothing in `Modules.Private` or the `EInvoice` repo is modified.
- Working directory for every command is `C:\Users\jorda\RiderProjects\Accounting101\UI\Angular`.
- **No new npm dependency.** The PDF pane is a browser `<object>`, not a viewer library.
- Every component: standalone, `ChangeDetectionStrategy.OnPush`, inline `template:`, signals over RxJS state. This is the idiom of every neighbouring feature — match it.
- TDD: write the failing test, watch it fail, implement, watch it pass, commit.
- **Stage explicit file lists** in every `git add` — never `git add -A` or `git add .`. There is an unrelated uncommitted change in `src/app/core/api/environment.ts` that must stay uncommitted.
- Run a single spec with: `npx ng test --include=<path-to-spec> --watch=false`
- Component effects normally settle inside `fixture.detectChanges()`. If an effect-driven assertion doesn't, add `TestBed.flushEffects?.()` after it — the pattern `route-sentinel.service.spec.ts:36` already uses.
- Run the whole suite with: `npm test -- --watch=false`
- The public build must stay green for a clone with no e-invoicing entitlement — the screen simply never becomes reachable.

## Backend contract (already shipped — do not change)

`POST /clients/{clientId}/einvoicing/inbound/preview`, multipart, field name `file`, 15 MB cap.
**200-with-findings is the success contract**: a 200 can carry `canImport: false`. 4xx means the *request* failed, never that the *document* did.

Response shape (C# source: `Modules.Private/Accounting101.EInvoicing/Accounting101.EInvoicing.Api/EInvoicingContracts.cs`):

```
InboundPreviewResponse {
  syntax: "Cii" | "Ubl" | "Unknown"
  profile: string | null
  canImport: boolean
  findings: [{ ruleId, severity, message, businessTerm, location }]
  proposal: {
    suggestedVendorId, sellerName, billDate, dueDate,
    vendorReference, memo, lines: [{ description, amount }],
    total, linesAreDetail
  } | null
  vendorMatch: { vendorId, vendorName, sellerName }
}
```

Facts that drive the code below:
- `proposal.lines` carry **no** expense account. Coding them is the operator's job.
- `proposal.suggestedVendorId` is **always null** in practice — the endpoint passes an empty vendor list because the module references no other module. `vendorMatch.sellerName` is always populated.
- A Factur-X PDF reports `syntax: "Cii"`, identical to a raw CII XML. The UI knows it uploaded a PDF only because it holds the file.
- `profile` has **two shapes**: a `FacturXProfile` enum name for CII (`"En16931"`), a raw `cbc:CustomizationID` URN for UBL (`"urn:cen.eu:en16931:2017"`).
- `proposal.total` is always BT-112 (tax-inclusive). A bill has no tax concept.

---

### Task 1: Wire models and pure helpers

**Files:**
- Create: `src/app/core/einvoicing/einvoicing.ts`
- Test: `src/app/core/einvoicing/einvoicing.spec.ts`

**Interfaces:**
- Consumes: `Vendor` and `Bill` from `src/app/core/payables/payables.ts`.
- Produces: `InboundPreviewResponse`, `BillProposalDto`, `ProposedLineDto`, `FindingDto`, `FindingSeverity`, `VendorMatchDto`, `InboundSyntax`; `matchVendor(sellerName, vendors): Vendor | null`; `findDuplicates(reference, bills): Bill[]`; `profileLabel(profile): string`.

- [ ] **Step 1: Write the failing test**

Create `src/app/core/einvoicing/einvoicing.spec.ts`:

```ts
import { matchVendor, findDuplicates, profileLabel } from './einvoicing';
import { Vendor, Bill } from '../payables/payables';

const vendor = (id: string, name: string): Vendor => ({ id, name, email: null });

const bill = (id: string, vendorReference: string | null, status: Bill['status'] = 'Entered'): Bill => ({
  id, vendorId: 'v1', number: null, billDate: '2026-07-01', dueDate: null,
  vendorReference, memo: null, status, lines: [],
});

describe('matchVendor', () => {
  const vendors = [vendor('v1', 'Acme Parts'), vendor('v2', 'Globex')];

  it('suggests the single exact match', () => {
    expect(matchVendor('Acme Parts', vendors)?.id).toBe('v1');
  });

  it('ignores case and surrounding whitespace on both sides', () => {
    expect(matchVendor('  acme PARTS ', [vendor('v1', ' Acme Parts ')])?.id).toBe('v1');
  });

  it('returns null when nothing matches', () => {
    expect(matchVendor('Initech', vendors)).toBeNull();
  });

  it('stays silent when two vendors share the name', () => {
    expect(matchVendor('Acme Parts', [vendor('v1', 'Acme Parts'), vendor('v3', 'Acme Parts')])).toBeNull();
  });

  it('returns null for a null or blank seller name', () => {
    expect(matchVendor(null, vendors)).toBeNull();
    expect(matchVendor('   ', vendors)).toBeNull();
  });
});

describe('findDuplicates', () => {
  it('finds a bill with the same vendor reference', () => {
    const hits = findDuplicates('INV-9', [bill('b1', 'INV-9'), bill('b2', 'INV-8')]);
    expect(hits.map(b => b.id)).toEqual(['b1']);
  });

  it('ignores case and whitespace', () => {
    expect(findDuplicates(' inv-9 ', [bill('b1', 'INV-9')]).length).toBe(1);
  });

  it('excludes voided bills', () => {
    expect(findDuplicates('INV-9', [bill('b1', 'INV-9', 'Void')])).toEqual([]);
  });

  it('checks nothing when the document has no reference', () => {
    expect(findDuplicates(null, [bill('b1', 'INV-9')])).toEqual([]);
  });
});

describe('profileLabel', () => {
  it('labels a Factur-X profile enum name', () => {
    expect(profileLabel('En16931')).toBe('EN 16931');
    expect(profileLabel('BasicWl')).toBe('BASIC WL');
  });

  it('labels the UBL customisation URNs it recognises', () => {
    expect(profileLabel('urn:cen.eu:en16931:2017')).toBe('EN 16931');
    expect(profileLabel('urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung_3.0'))
      .toBe('XRechnung');
    expect(profileLabel('urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0'))
      .toBe('Peppol BIS');
  });

  it('shows an unrecognised profile verbatim rather than as "Unknown"', () => {
    expect(profileLabel('urn:example:something:else')).toBe('urn:example:something:else');
  });

  it('reports an absent profile as unspecified', () => {
    expect(profileLabel(null)).toBe('Unspecified');
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx ng test --include=src/app/core/einvoicing/einvoicing.spec.ts --watch=false`
Expected: FAIL — cannot resolve `./einvoicing`.

- [ ] **Step 3: Write the implementation**

Create `src/app/core/einvoicing/einvoicing.ts`:

```ts
import { Vendor, Bill } from '../payables/payables';

// ── Wire models ──────────────────────────────────────────────────────────────
// Mirrors of the DTOs in Accounting101.EInvoicing.Api/EInvoicingContracts.cs.

export type FindingSeverity = 'Error' | 'Warning' | 'Information';
export type InboundSyntax = 'Cii' | 'Ubl' | 'Unknown';

/** One validation finding. Official rule ids (BR-*, PEPPOL-*, BR-DE-*) pass through unchanged;
 *  module findings carry the `einvoicing-` prefix. Both are shown verbatim — they are what an
 *  operator takes to their vendor. */
export interface FindingDto {
  ruleId: string; severity: FindingSeverity; message: string;
  businessTerm: string | null; location: string | null;
}

export interface VendorMatchDto { vendorId: string | null; vendorName: string | null; sellerName: string | null; }

export interface ProposedLineDto { description: string; amount: number; }

/** `total` is always BT-112 (tax-inclusive), never BT-115. `linesAreDetail` is false when the
 *  backend could not reconcile detail lines and fell back to one line at the document total. */
export interface BillProposalDto {
  suggestedVendorId: string | null; sellerName: string; billDate: string; dueDate: string | null;
  vendorReference: string | null; memo: string | null; lines: ProposedLineDto[];
  total: number; linesAreDetail: boolean;
}

export interface InboundPreviewResponse {
  syntax: InboundSyntax; profile: string | null; canImport: boolean;
  findings: FindingDto[]; proposal: BillProposalDto | null; vendorMatch: VendorMatchDto;
}

// ── Pure helpers ─────────────────────────────────────────────────────────────

/**
 * Suggests an existing vendor for a document's seller. A verbatim mirror of the backend's
 * `VendorMatcher.Match`: trim, case-insensitive equality, suggest only on a unique hit, stay
 * silent when two vendors share a name.
 *
 * The mirroring is deliberate. The endpoint always passes an empty vendor list (the module
 * references no other module, so it cannot read Payables' vendors), which leaves matching to this
 * screen — but the backend matcher is built and tested, and the two must not drift.
 */
export function matchVendor(sellerName: string | null | undefined, vendors: readonly Vendor[]): Vendor | null {
  if (!sellerName || !sellerName.trim()) return null;
  const needle = sellerName.trim().toLowerCase();
  const hits = vendors.filter(v => (v.name ?? '').trim().toLowerCase() === needle);
  return hits.length === 1 ? hits[0] : null;
}

/** Existing bills for the same vendor carrying the same vendor reference. Voided bills never
 *  count — they are exactly the case where re-entering the invoice is correct. */
export function findDuplicates(reference: string | null | undefined, bills: readonly Bill[]): Bill[] {
  if (!reference || !reference.trim()) return [];
  const needle = reference.trim().toLowerCase();
  return bills.filter(b => b.status !== 'Void' && (b.vendorReference ?? '').trim().toLowerCase() === needle);
}

const PROFILE_ENUM_LABELS: Record<string, string> = {
  Minimum: 'MINIMUM', BasicWl: 'BASIC WL', Basic: 'BASIC',
  En16931: 'EN 16931', Extended: 'EXTENDED', XRechnung: 'XRechnung', Peppol: 'Peppol BIS',
};

/**
 * A short label for the response's `profile`, which has two shapes: a FacturXProfile enum name for
 * CII, a raw cbc:CustomizationID URN for UBL. Never branches on syntax, and never renders an
 * unrecognised value as "Unknown" — an unfamiliar profile shows up honestly as itself.
 */
export function profileLabel(profile: string | null | undefined): string {
  if (!profile || !profile.trim()) return 'Unspecified';
  const p = profile.trim();
  const known = PROFILE_ENUM_LABELS[p];
  if (known) return known;
  const lower = p.toLowerCase();
  if (lower.includes('xrechnung')) return 'XRechnung';
  if (lower.includes('peppol.eu')) return 'Peppol BIS';
  if (lower === 'urn:cen.eu:en16931:2017') return 'EN 16931';
  return p;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx ng test --include=src/app/core/einvoicing/einvoicing.spec.ts --watch=false`
Expected: PASS — 14 tests.

- [ ] **Step 5: Commit**

```bash
git add src/app/core/einvoicing/einvoicing.ts src/app/core/einvoicing/einvoicing.spec.ts
git commit -m "feat(einvoicing): wire models, vendor matcher and duplicate finder"
```

---

### Task 2: Preview HTTP service

**Files:**
- Create: `src/app/core/einvoicing/einvoicing.service.ts`
- Test: `src/app/core/einvoicing/einvoicing.service.spec.ts`

**Interfaces:**
- Consumes: `InboundPreviewResponse` (Task 1); `ClientContextService`; `environment`.
- Produces: `EInvoicingService.previewInbound(file: File): Observable<InboundPreviewResponse>` and the static `EInvoicingService.maxUploadBytes: number`.

- [ ] **Step 1: Write the failing test**

Create `src/app/core/einvoicing/einvoicing.service.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EInvoicingService } from './einvoicing.service';
import { ClientContextService } from '../client/client-context.service';

describe('EInvoicingService', () => {
  function setup(clientId: string | null) {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select(clientId);
    return { svc: TestBed.inject(EInvoicingService), ctrl: TestBed.inject(HttpTestingController) };
  }

  it('posts the file as multipart to the preview endpoint', () => {
    const { svc, ctrl } = setup('C1');
    const file = new File(['<xml/>'], 'invoice.xml', { type: 'text/xml' });
    let got: unknown = null;
    svc.previewInbound(file).subscribe(r => (got = r));

    const req = ctrl.expectOne('http://localhost:5000/clients/C1/einvoicing/inbound/preview');
    expect(req.request.method).toBe('POST');
    const body = req.request.body as FormData;
    expect(body.get('file')).toBeInstanceOf(File);
    expect((body.get('file') as File).name).toBe('invoice.xml');

    req.flush({ syntax: 'Ubl', profile: null, canImport: false, findings: [], proposal: null,
      vendorMatch: { vendorId: null, vendorName: null, sellerName: 'Acme' } });
    expect(got).not.toBeNull();
    ctrl.verify();
  });

  it('makes no request when no client is selected', () => {
    const { svc, ctrl } = setup(null);
    let emitted = false;
    svc.previewInbound(new File(['x'], 'a.xml')).subscribe({ next: () => (emitted = true) });
    expect(emitted).toBe(false);
    ctrl.verify();
  });

  it('mirrors the endpoint 15 MB cap', () => {
    expect(EInvoicingService.maxUploadBytes).toBe(15 * 1024 * 1024);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx ng test --include=src/app/core/einvoicing/einvoicing.service.spec.ts --watch=false`
Expected: FAIL — cannot resolve `./einvoicing.service`.

- [ ] **Step 3: Write the implementation**

Create `src/app/core/einvoicing/einvoicing.service.ts`:

```ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { InboundPreviewResponse } from './einvoicing';

/**
 * The e-invoicing module's whole HTTP surface: one advisory, stateless preview call. The module
 * writes nothing — turning a preview into a bill is the screen's job, through Payables' own API.
 */
@Injectable({ providedIn: 'root' })
export class EInvoicingService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  /** Mirrors `EInvoicingEndpoints.MaxUploadBytes`. Checked client-side because the endpoint's own
   *  friendly 413 is unreachable for an oversized body: `WithFormOptions` caps the multipart
   *  reader, so Kestrel rejects mid-stream with a bare 400 before the handler ever runs. */
  static readonly maxUploadBytes = 15 * 1024 * 1024;

  /** 200-with-findings is the success contract — a 200 can carry `canImport: false`. */
  previewInbound(file: File): Observable<InboundPreviewResponse> {
    const id = this.client.clientId();
    if (!id) return EMPTY;
    const body = new FormData();
    body.append('file', file, file.name);
    return this.http.post<InboundPreviewResponse>(
      `${environment.apiBaseUrl}/clients/${id}/einvoicing/inbound/preview`, body);
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx ng test --include=src/app/core/einvoicing/einvoicing.service.spec.ts --watch=false`
Expected: PASS — 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/app/core/einvoicing/einvoicing.service.ts src/app/core/einvoicing/einvoicing.service.spec.ts
git commit -m "feat(einvoicing): inbound preview HTTP service"
```

---

### Task 3: Module route guard and sentinel

**Files:**
- Create: `src/app/core/capabilities/module.guard.ts`
- Test: `src/app/core/capabilities/module.guard.spec.ts`
- Modify: `src/app/core/capabilities/route-sentinel.service.ts`
- Test: `src/app/core/capabilities/route-sentinel.service.spec.ts` (extend)

**Interfaces:**
- Consumes: `CapabilityService.moduleEnabled(key)`, `CapabilityService.loaded`.
- Produces: `moduleEnabled: CanActivateFn`, reading `route.data['requiredModule']` and `route.data['fallback']`. `RouteSentinelService` gains a symmetric `requiredModule` check.

Background: `canWrite` (`can.guard.ts:9-19`) guards capabilities at navigation time; `RouteSentinelService` (`route-sentinel.service.ts:30-38`) redirects when a capability is lost mid-session. Modules need both, and neither exists today.

- [ ] **Step 1: Write the failing guard test**

Create `src/app/core/capabilities/module.guard.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { moduleEnabled } from './module.guard';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

@Component({ standalone: true, template: 'import' }) class Importer {}
@Component({ standalone: true, template: 'bills' }) class Bills {}

function setup(modules: string[], loaded = true) {
  const caps = new StubCapabilityService();
  caps.setLoaded(loaded);
  caps.setEnabledModules(modules);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: CapabilityService, useValue: caps },
      provideRouter([
        { path: 'bills', component: Bills },
        { path: 'import', component: Importer, canActivate: [moduleEnabled],
          data: { requiredModule: 'einvoicing', fallback: '/bills' } },
      ]),
    ],
  });
  return caps;
}

describe('moduleEnabled guard', () => {
  it('allows navigation when the module is enabled', async () => {
    setup(['einvoicing']);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/import');
    expect(TestBed.inject(Router).url).toBe('/import');
  });

  it('redirects to the fallback when the module is not enabled', async () => {
    setup([]);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/import');
    expect(TestBed.inject(Router).url).toBe('/bills');
  });

  it('waits for capabilities to load before deciding', async () => {
    const caps = setup(['einvoicing'], false);
    const harness = await RouterTestingHarness.create();
    const nav = harness.navigateByUrl('/import');
    expect(TestBed.inject(Router).url).toBe('/');   // still pending — the guard has not resolved
    caps.setLoaded(true);
    await nav;
    expect(TestBed.inject(Router).url).toBe('/import');
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx ng test --include=src/app/core/capabilities/module.guard.spec.ts --watch=false`
Expected: FAIL — cannot resolve `./module.guard`.

- [ ] **Step 3: Write the guard**

Create `src/app/core/capabilities/module.guard.ts`:

```ts
import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivateFn, Router, UrlTree } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { Observable, filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/**
 * Guards a route behind a paid module's entitlement. The module key and the fallback route live in
 * the route's `data` (`requiredModule`, `fallback`) — the same source the route sentinel reads.
 *
 * This is UX, not security: the endpoint's own EnabledModules check is the authority. Compose it
 * with `canWrite` when the route both writes and belongs to a module.
 */
export const moduleEnabled: CanActivateFn = (route: ActivatedRouteSnapshot): Observable<boolean | UrlTree> => {
  const caps = inject(CapabilityService);
  const router = inject(Router);
  const module = route.data['requiredModule'] as string;
  const fallback = route.data['fallback'] as string;
  return toObservable(caps.loaded).pipe(
    filter((loaded) => loaded),
    take(1),
    map(() => (caps.moduleEnabled(module) ? true : router.parseUrl(fallback))),
  );
};
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx ng test --include=src/app/core/capabilities/module.guard.spec.ts --watch=false`
Expected: PASS — 3 tests.

- [ ] **Step 5: Write the failing sentinel test**

Append this `it` block inside the existing `describe('RouteSentinelService', ...)` in `src/app/core/capabilities/route-sentinel.service.spec.ts`, and add `{ path: 'import', component: Editor, data: { requiredModule: 'einvoicing', fallback: '/list' } }` to the `provideRouter([...])` array in that file's `setup()`:

```ts
  it('redirects off the page the moment the required module is disabled', async () => {
    const caps = setup();
    caps.setEnabledModules(['einvoicing']);
    TestBed.inject(RouteSentinelService);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/import');
    TestBed.flushEffects?.();
    expect(TestBed.inject(Router).url).toBe('/import');

    caps.setEnabledModules([]);                          // entitlement lapsed mid-session
    TestBed.flushEffects?.();
    for (let i = 0; i < 20 && TestBed.inject(Router).url !== '/list'; i++) {
      await new Promise((r) => setTimeout(r, 0));
    }
    expect(TestBed.inject(Router).url).toBe('/list');
  });
```

- [ ] **Step 6: Run test to verify it fails**

Run: `npx ng test --include=src/app/core/capabilities/route-sentinel.service.spec.ts --watch=false`
Expected: FAIL — the new test times out at `/import`; the sentinel ignores `requiredModule`.

- [ ] **Step 7: Extend the sentinel**

In `src/app/core/capabilities/route-sentinel.service.ts`, replace the `RouteCap` interface and the constructor's effect:

```ts
interface RouteCap { requiredCapability?: string; fallback?: string; requiredModule?: string; }
```

```ts
  constructor() {
    effect(() => {
      const { requiredCapability, fallback, requiredModule } = this.activeCap();
      if (!this.caps.loaded()) return;
      const lost =
        (!!requiredCapability && !this.caps.has(requiredCapability)) ||
        (!!requiredModule && !this.caps.moduleEnabled(requiredModule));
      if (lost) void this.router.navigateByUrl(fallback ?? '/dashboard');
    });
  }
```

Also update the class doc comment's first line to:

```ts
/** Redirects the user off the current page the instant its required capability or module leaves the
 * caps signal — the reactive complement to the navigation-time canWrite/moduleEnabled guards.
 * Root-level; started by being injected at bootstrap (see app.ts). */
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `npx ng test --include=src/app/core/capabilities/route-sentinel.service.spec.ts --watch=false`
Expected: PASS — the pre-existing capability tests plus the new module test.

- [ ] **Step 9: Commit**

```bash
git add src/app/core/capabilities/module.guard.ts src/app/core/capabilities/module.guard.spec.ts src/app/core/capabilities/route-sentinel.service.ts src/app/core/capabilities/route-sentinel.service.spec.ts
git commit -m "feat(capabilities): moduleEnabled route guard and sentinel module check"
```

---

### Task 4: Shared expense-account list

**Files:**
- Modify: `src/app/core/accounts/accounts.service.ts`
- Modify: `src/app/features/payables/bill-editor.ts:136-137`
- Test: `src/app/core/accounts/accounts.service.spec.ts` (extend)

**Interfaces:**
- Produces: `AccountsService.expenseAccounts: Signal<AccountResponse[]>` — postable, active accounts of type `Expense`. Task 6 consumes it.

`bill-editor.ts:136-137` computes this filter inline and the import screen needs the identical list. One definition, two consumers.

- [ ] **Step 1: Write the failing test**

`src/app/core/accounts/accounts.service.spec.ts` already has a `beforeEach` exposing `service`, `ctrl` and `clientCtx`, and an `afterEach(() => ctrl.verify())`. Add this `it` at the end of that `describe`, using them — no new imports are needed:

```ts
  it('exposes only postable, active expense accounts for line coding', () => {
    const expenseFixtures: AccountResponse[] = [
      { id: 'a1', number: '6000', name: 'Rent', type: 'Expense', parentId: null,
        postable: true, requiredDimension: null, requiredDimensions: [], cashFlowActivity: null,
        isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: true },
      { id: 'a2', number: '6100', name: 'Occupancy (header)', type: 'Expense', parentId: null,
        postable: false, requiredDimension: null, requiredDimensions: [], cashFlowActivity: null,
        isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: true },
      { id: 'a3', number: '6200', name: 'Retired', type: 'Expense', parentId: null,
        postable: true, requiredDimension: null, requiredDimensions: [], cashFlowActivity: null,
        isRetainedEarnings: false, active: false, normalSide: 'Debit', isTemporary: true },
      ...mockAccounts,                                    // an Asset and a Revenue, both postable
    ];
    clientCtx.select(clientId);
    service.load();
    ctrl.expectOne(`${environment.apiBaseUrl}/clients/${clientId}/accounts`).flush(expenseFixtures);
    expect(service.expenseAccounts().map(a => a.id)).toEqual(['a1']);
  });
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx ng test --include=src/app/core/accounts/accounts.service.spec.ts --watch=false`
Expected: FAIL — `svc.expenseAccounts is not a function`.

- [ ] **Step 3: Add the computed**

In `src/app/core/accounts/accounts.service.ts`, directly after the existing `readonly byId = computed(...)` line:

```ts
  /** The codeable set for bill and e-invoice lines: postable, active expense accounts. */
  readonly expenseAccounts = computed(() =>
    this._accounts().filter(a => a.type === 'Expense' && a.postable && a.active));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx ng test --include=src/app/core/accounts/accounts.service.spec.ts --watch=false`
Expected: PASS.

- [ ] **Step 5: Point bill-editor at the shared list**

In `src/app/features/payables/bill-editor.ts`, replace lines 136-137:

```ts
  readonly expenseAccounts = computed<AccountResponse[]>(() =>
    this.accountsSvc.accounts().filter(a => a.type === 'Expense' && a.postable && a.active));
```

with:

```ts
  readonly expenseAccounts = this.accountsSvc.expenseAccounts;
```

Then delete the now-unused `AccountResponse` import on line 12 (`import { AccountResponse } from '../../core/accounts/account';`). Leave every other import alone — `computed` is still used by `total` and `canSave`.

- [ ] **Step 6: Run the bill-editor tests to verify nothing regressed**

Run: `npx ng test --include=src/app/features/payables/bill-editor.spec.ts --watch=false`
Expected: PASS — unchanged behaviour.

- [ ] **Step 7: Commit**

```bash
git add src/app/core/accounts/accounts.service.ts src/app/core/accounts/accounts.service.spec.ts src/app/features/payables/bill-editor.ts
git commit -m "refactor(accounts): share the postable expense-account list with bill-editor"
```

---

### Task 5: Import screen — upload stage, findings, blocked state

**Files:**
- Create: `src/app/features/einvoicing/einvoice-import.ts`
- Test: `src/app/features/einvoicing/einvoice-import.spec.ts`

**Interfaces:**
- Consumes: `EInvoicingService.previewInbound`, `EInvoicingService.maxUploadBytes` (Task 2); `profileLabel`, `InboundPreviewResponse`, `FindingDto` (Task 1); `extractProblem`.
- Produces: `class EInvoiceImport` with public signals `stage: WritableSignal<'upload' | 'preview'>`, `file: WritableSignal<File | null>`, `preview: WritableSignal<InboundPreviewResponse | null>`, `busy`, `message`, and methods `onFile(e: Event)`, `upload()`, `reset()`. Tasks 6-8 extend this same class.

- [ ] **Step 1: Write the failing test**

Create `src/app/features/einvoicing/einvoice-import.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EInvoiceImport } from './einvoice-import';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

const PREVIEW_URL = 'http://localhost:5000/clients/C1/einvoicing/inbound/preview';

export function xmlFile(name = 'invoice.xml'): File {
  return new File(['<xml/>'], name, { type: 'text/xml' });
}

export function blocked(ruleId = 'BR-06', message = 'Seller name is missing.') {
  return {
    syntax: 'Ubl', profile: 'urn:cen.eu:en16931:2017', canImport: false,
    findings: [{ ruleId, severity: 'Error', message, businessTerm: 'BT-27', location: null }],
    proposal: null, vendorMatch: { vendorId: null, vendorName: null, sellerName: 'Acme Parts' },
  };
}

export function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
      provideHttpClientTesting(), provideCapabilities('ap.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(EInvoiceImport);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  // The screen loads vendors and accounts on construction (used from Task 6 onwards).
  ctrl.match(r => r.url.endsWith('/vendors')).forEach(r => r.flush([]));
  ctrl.match(r => r.url.endsWith('/accounts')).forEach(r => r.flush([]));
  fixture.detectChanges();
  return { fixture, ctrl, cmp: fixture.componentInstance };
}

describe('EInvoiceImport', () => {
  it('uploads the file and moves to the preview stage', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    const req = ctrl.expectOne(PREVIEW_URL);
    expect((req.request.body as FormData).get('file')).toBeInstanceOf(File);
    req.flush(blocked());
    fixture.detectChanges();
    expect(cmp.stage()).toBe('preview');
    ctrl.verify();
  });

  it('shows error findings verbatim and no form when the document cannot be imported', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(blocked());
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('BR-06');
    expect(text).toContain('Seller name is missing.');
    expect(text).toContain('EN 16931');                    // profile badge
    expect(text).toContain('Choose another file');
    expect(text).not.toContain('Create draft bill');
    ctrl.verify();
  });

  it('surfaces a 403 as a message and stays on the upload stage', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(
      { detail: 'The e-invoicing module is not enabled for this client.' },
      { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    expect(cmp.stage()).toBe('upload');
    expect(cmp.message()).toContain('not enabled');
    ctrl.verify();
  });

  it('rejects an oversized file client-side without making a request', () => {
    const { ctrl, cmp } = boot();
    const big = new File(['x'], 'huge.pdf', { type: 'application/pdf' });
    Object.defineProperty(big, 'size', { value: 16 * 1024 * 1024 });
    cmp.file.set(big);
    cmp.upload();
    expect(cmp.message()).toContain('15 MB');
    ctrl.verify();                                          // no request was made
  });

  it('collapses advisory findings behind a count', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush({
      syntax: 'Cii', profile: 'En16931', canImport: false,
      findings: [
        { ruleId: 'BR-CO-10', severity: 'Warning', message: 'Sum of line amounts differs.', businessTerm: null, location: null },
        { ruleId: 'einvoicing-note', severity: 'Information', message: 'Detail lines reconciled.', businessTerm: null, location: null },
      ],
      proposal: null, vendorMatch: { vendorId: null, vendorName: null, sellerName: 'Acme Parts' },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('BR-CO-10');
    cmp.showOtherFindings.set(true);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('BR-CO-10');
    expect(fixture.nativeElement.textContent).toContain('einvoicing-note');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: FAIL — cannot resolve `./einvoice-import`.

- [ ] **Step 3: Write the component**

Create `src/app/features/einvoicing/einvoice-import.ts`:

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { EInvoicingService } from '../../core/einvoicing/einvoicing.service';
import { FindingDto, InboundPreviewResponse, profileLabel } from '../../core/einvoicing/einvoicing';
import { extractProblem } from '../../core/api/problem-details';

type Stage = 'upload' | 'preview';

/**
 * Upload an EN 16931 e-invoice — a Factur-X/ZUGFeRD PDF, a CII XML, or a UBL XML — review what the
 * engine found, code each line, and post a draft bill through Payables' own API.
 *
 * The e-invoicing module is advisory and stateless: it writes nothing. Everything that turns a
 * proposal into a bill happens here.
 */
@Component({
  selector: 'app-einvoice-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-5xl">
      <a routerLink="/payables/bills" class="text-sm text-muted-foreground hover:text-foreground">← Bills</a>
      <h1 class="text-2xl font-bold">Import e-invoice</h1>

      @if (stage() === 'upload') {
        <p class="text-sm text-muted-foreground max-w-prose">
          Upload a Factur-X / ZUGFeRD PDF, or a CII or UBL XML invoice. Nothing is saved until you
          create the bill.
        </p>
        <div class="flex flex-col gap-1 max-w-md">
          <label hlmLabel>File</label>
          <input hlmInput type="file" accept=".pdf,.xml,application/pdf,text/xml,application/xml"
                 (change)="onFile($event)" />
        </div>
        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
        <div>
          <button hlmBtn type="button" (click)="upload()" [disabled]="!canUpload() || busy()">
            Upload &amp; preview
          </button>
        </div>
      }

      @if (stage() === 'preview') {
        @if (preview(); as p) {
          <div class="flex items-center gap-2 flex-wrap text-sm">
            <span class="font-medium">{{ file()?.name }}</span>
            <span class="rounded border border-border px-2 py-0.5 text-xs">{{ p.syntax }}</span>
            <span class="rounded border border-border px-2 py-0.5 text-xs">{{ profile() }}</span>
          </div>

          @if (errors().length) {
            <div class="border border-destructive rounded p-3 flex flex-col gap-2">
              <p class="font-semibold text-destructive">Errors</p>
              <ul class="flex flex-col gap-1">
                @for (f of errors(); track $index) {
                  <li class="text-sm">
                    <code class="text-xs bg-muted px-1 rounded">{{ f.ruleId }}</code>
                    <span class="ms-1">{{ f.message }}</span>
                    @if (f.businessTerm) {
                      <span class="text-muted-foreground text-xs ms-1">({{ f.businessTerm }})</span>
                    }
                    @if (f.location) { <div class="text-muted-foreground text-xs">{{ f.location }}</div> }
                  </li>
                }
              </ul>
            </div>
          }

          @if (otherCount()) {
            <div class="border border-border rounded p-3 flex flex-col gap-2">
              <button hlmBtn type="button" variant="ghost" size="sm" class="self-start"
                      (click)="showOtherFindings.set(!showOtherFindings())">
                {{ showOtherFindings() ? 'Hide' : 'Show' }} {{ otherCount() }} advisory finding{{ otherCount() === 1 ? '' : 's' }}
              </button>
              @if (showOtherFindings()) {
                <ul class="flex flex-col gap-1">
                  @for (f of advisories(); track $index) {
                    <li class="text-sm">
                      <code class="text-xs bg-muted px-1 rounded">{{ f.ruleId }}</code>
                      <span class="ms-1 text-muted-foreground text-xs">{{ f.severity }}</span>
                      <span class="ms-1">{{ f.message }}</span>
                      @if (f.businessTerm) {
                        <span class="text-muted-foreground text-xs ms-1">({{ f.businessTerm }})</span>
                      }
                    </li>
                  }
                </ul>
              }
            </div>
          }

          @if (!p.canImport) {
            <p class="text-sm text-muted-foreground max-w-prose">
              This document can't become a bill. Fix it with the vendor, or upload a different file.
            </p>
            <div>
              <button hlmBtn type="button" variant="outline" (click)="reset()">Choose another file</button>
            </div>
          }
        }
      }
    </div>
  `,
})
export class EInvoiceImport {
  private readonly svc = inject(EInvoicingService);
  private readonly destroyRef = inject(DestroyRef);

  readonly stage = signal<Stage>('upload');
  readonly file = signal<File | null>(null);
  readonly preview = signal<InboundPreviewResponse | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly showOtherFindings = signal(false);

  readonly canUpload = computed(() => !!this.file());
  readonly profile = computed(() => profileLabel(this.preview()?.profile ?? null));
  readonly errors = computed(() => this.findingsOf('Error'));
  /** Warnings and information, in that order — advisory, never blocking. */
  readonly advisories = computed(() => [...this.findingsOf('Warning'), ...this.findingsOf('Information')]);
  readonly otherCount = computed(() => this.advisories().length);

  private findingsOf(severity: FindingDto['severity']): FindingDto[] {
    return (this.preview()?.findings ?? []).filter(f => f.severity === severity);
  }

  onFile(e: Event): void {
    const picked = (e.target as HTMLInputElement).files?.[0] ?? null;
    this.file.set(picked);
    this.preview.set(null);
    this.showOtherFindings.set(false);
    this.message.set(picked && picked.size > EInvoicingService.maxUploadBytes ? this.tooLarge() : null);
  }

  upload(): void {
    const f = this.file();
    if (!f || this.busy()) return;
    if (f.size > EInvoicingService.maxUploadBytes) { this.message.set(this.tooLarge()); return; }
    this.busy.set(true);
    this.message.set(null);
    this.svc.previewInbound(f).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: r => { this.preview.set(r); this.stage.set('preview'); this.busy.set(false); },
      error: e => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  reset(): void {
    this.stage.set('upload');
    this.preview.set(null);
    this.file.set(null);
    this.message.set(null);
    this.showOtherFindings.set(false);
  }

  private tooLarge(): string { return 'That file is larger than the 15 MB limit.'; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: PASS — 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/app/features/einvoicing/einvoice-import.ts src/app/features/einvoicing/einvoice-import.spec.ts
git commit -m "feat(einvoicing): import screen upload stage, findings and blocked state"
```

---

### Task 6: Import screen — proposal form

**Files:**
- Modify: `src/app/features/einvoicing/einvoice-import.ts`
- Test: `src/app/features/einvoicing/einvoice-import.spec.ts` (extend)

**Interfaces:**
- Consumes: `PayablesService.vendors/load/create/draftBill`, `DraftBillRequest`, `billTotal` from `core/payables`; `AccountsService.expenseAccounts` (Task 4); `matchVendor` (Task 1); `CurrencyInput`; `CanDirective`; `money`.
- Produces: on `EInvoiceImport` — `vendorId`, `lines`, `createdBillId`, `creatingVendor` signals; `canConfirm`, `linesTotal`, `documentTotal`, `totalDrift`, `sellerName` computeds; `addLine()`, `removeLine(i)`, `patchLine(i, part)`, `createVendor()`, `confirm()`. Task 7 reads `vendorId`; Task 8 reads `stage`/`reset()`.

- [ ] **Step 1: Write the failing tests**

Append to `src/app/features/einvoicing/einvoice-import.spec.ts` — first add this shared fixture beside the existing `blocked()` helper:

```ts
export function importable(overrides: Record<string, unknown> = {}) {
  return {
    syntax: 'Cii', profile: 'En16931', canImport: true, findings: [],
    proposal: {
      suggestedVendorId: null, sellerName: 'Acme Parts', billDate: '2026-07-01', dueDate: '2026-07-31',
      vendorReference: 'INV-9', memo: 'Q3 services', lines: [{ description: 'Consulting', amount: 1200 }],
      total: 1200, linesAreDetail: true, ...overrides,
    },
    vendorMatch: { vendorId: null, vendorName: null, sellerName: 'Acme Parts' },
  };
}
```

then add these tests inside the existing `describe('EInvoiceImport', ...)`:

```ts
  it('prefills the vendor from the seller name once vendors have loaded', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
        provideHttpClientTesting(), provideCapabilities('ap.write')],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(EInvoiceImport);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.match(r => r.url.endsWith('/vendors')).forEach(r =>
      r.flush([{ id: 'v1', name: 'Acme Parts', email: null }]));
    ctrl.match(r => r.url.endsWith('/accounts')).forEach(r => r.flush([]));
    fixture.detectChanges();

    const cmp = fixture.componentInstance;
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();
    expect(cmp.vendorId()).toBe('v1');
    ctrl.match(r => r.url.endsWith('/bills')).forEach(r => r.flush({ items: [], total: 0, skip: 0, limit: 200 }));
    ctrl.verify();
  });

  it('posts a draft bill carrying the document header and the edited lines', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    cmp.patchLine(0, { amount: 700, expenseAccountId: 'a1' });
    cmp.addLine();
    cmp.patchLine(1, { description: 'Utilities', amount: 500, expenseAccountId: 'a2' });
    fixture.detectChanges();
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));

    expect(cmp.canConfirm()).toBe(true);
    cmp.confirm();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills' && r.method === 'POST');
    expect(req.request.body).toEqual({
      vendorId: 'v1', billDate: '2026-07-01', dueDate: '2026-07-31',
      vendorReference: 'INV-9', memo: 'Q3 services',
      lines: [
        { description: 'Consulting', amount: 700, expenseAccountId: 'a1' },
        { description: 'Utilities', amount: 500, expenseAccountId: 'a2' },
      ],
    });
    req.flush({ id: 'b9', vendorId: 'v1', number: 'B-9', billDate: '2026-07-01', dueDate: '2026-07-31',
      vendorReference: 'INV-9', memo: 'Q3 services', status: 'Draft', lines: [] });
    fixture.detectChanges();
    expect(cmp.createdBillId()).toBe('b9');
    expect(fixture.nativeElement.textContent).toContain('Draft bill created');
    ctrl.verify();
  });

  it('warns on total drift without blocking the confirm', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    cmp.patchLine(0, { amount: 900, expenseAccountId: 'a1' });
    fixture.detectChanges();
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));
    fixture.detectChanges();

    expect(cmp.totalDrift()).toBe(-300);
    expect(fixture.nativeElement.textContent).toContain('document says');
    expect(cmp.canConfirm()).toBe(true);                    // a warning, not a gate
    ctrl.verify();
  });

  it('creates the vendor inline from the seller name and selects it', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();
    expect(cmp.vendorId()).toBe('');

    cmp.createVendor();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/vendors' && r.method === 'POST');
    expect(req.request.body).toEqual({ name: 'Acme Parts', email: null });
    req.flush({ id: 'v7', name: 'Acme Parts', email: null });
    fixture.detectChanges();
    expect(cmp.vendorId()).toBe('v7');
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));
    ctrl.verify();
  });
```

> The `/bills` GET flushes are the duplicate check from Task 7. Until Task 7 lands, `ctrl.match(...)` simply finds nothing and the `forEach` is a no-op — the tests are written once and stay green through both tasks.

- [ ] **Step 2: Run tests to verify they fail**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: FAIL — `cmp.vendorId is not a function`.

- [ ] **Step 3: Extend the component class**

In `src/app/features/einvoicing/einvoice-import.ts`, replace the import block at the top with:

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { EInvoicingService } from '../../core/einvoicing/einvoicing.service';
import { FindingDto, InboundPreviewResponse, matchVendor, profileLabel } from '../../core/einvoicing/einvoicing';
import { PayablesService } from '../../core/payables/payables.service';
import { DraftBillRequest, billTotal } from '../../core/payables/payables';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';
import { CanDirective } from '../../core/capabilities/can.directive';

type Stage = 'upload' | 'preview';

/** One editable proposal line. The document supplies description and amount; the operator supplies
 *  the expense account, which is the one thing a proposal can never carry. */
interface EditableLine { lineId: string; description: string; amount: number; expenseAccountId: string | null; }
```

Update the `@Component` `imports` array to:

```ts
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports,
            CurrencyInput, CanDirective],
```

Add these members to the class, after the existing `showOtherFindings` signal:

```ts
  private readonly pay = inject(PayablesService);
  private readonly accounts = inject(AccountsService);

  readonly vendorId = signal('');
  readonly lines = signal<EditableLine[]>([]);
  readonly createdBillId = signal<string | null>(null);
  readonly creatingVendor = signal(false);

  readonly expenseAccounts = this.accounts.expenseAccounts;
  readonly proposal = computed(() => this.preview()?.proposal ?? null);
  readonly sellerName = computed(() => this.preview()?.vendorMatch.sellerName ?? '');
  readonly linesTotal = computed(() => billTotal(this.lines().map(l => ({ amount: l.amount }))));
  readonly documentTotal = computed(() => this.proposal()?.total ?? 0);
  readonly totalDrift = computed(() => Math.round((this.linesTotal() - this.documentTotal()) * 100) / 100);
  readonly canCreateVendor = computed(() => !this.vendorId() && !!this.sellerName().trim());

  /** The same predicate Payables enforces server-side: a vendor, at least one line, and every line
   *  carrying a description, an account and a positive amount. */
  readonly canConfirm = computed(() =>
    !!this.vendorId() && this.lines().length > 0 &&
    this.lines().every(l => !!l.description.trim() && !!l.expenseAccountId && l.amount > 0));

  readonly vendorLabel = (id: string): string => this.pay.vendorName(id);
  readonly accountLabel = (id: string): string => this.accounts.label(id);

  constructor() {
    this.pay.load();
    this.accounts.load();
    // Vendors may still be in flight when the preview lands, so match again when they arrive.
    // Only fills an empty selection — never overrides the operator.
    effect(() => {
      const vendors = this.pay.vendors();
      const r = this.preview();
      if (!r || this.vendorId() || vendors.length === 0) return;
      const hit = matchVendor(r.vendorMatch.sellerName, vendors);
      if (hit) this.vendorId.set(hit.id);
    });
  }

  addLine(): void {
    this.lines.update(ls => [...ls,
      { lineId: crypto.randomUUID(), description: '', amount: 0, expenseAccountId: null }]);
  }

  removeLine(i: number): void { this.lines.update(ls => ls.filter((_, idx) => idx !== i)); }

  patchLine(i: number, part: Partial<EditableLine>): void {
    this.lines.update(ls => ls.map((l, idx) => (idx === i ? { ...l, ...part } : l)));
  }

  createVendor(): void {
    const name = this.sellerName().trim();
    if (!name || this.creatingVendor()) return;
    this.creatingVendor.set(true);
    this.message.set(null);
    this.pay.create(name, null).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: v => { this.vendorId.set(v.id); this.creatingVendor.set(false); },
      error: e => { this.message.set(extractProblem(e).detail); this.creatingVendor.set(false); },
    });
  }

  confirm(): void {
    const p = this.proposal();
    if (!p || !this.canConfirm() || this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    const req: DraftBillRequest = {
      vendorId: this.vendorId(),
      billDate: p.billDate,
      dueDate: p.dueDate,
      vendorReference: p.vendorReference,
      memo: p.memo,
      lines: this.lines().map(l => ({
        description: l.description, amount: l.amount, expenseAccountId: l.expenseAccountId!,
      })),
    };
    this.pay.draftBill(req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: b => { this.createdBillId.set(b.id); this.busy.set(false); },
      error: e => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return displayDate(d); }
```

Seed the editable model when a preview arrives. Replace the `upload()` method's `next` handler:

```ts
      next: r => { this.preview.set(r); this.seed(r); this.stage.set('preview'); this.busy.set(false); },
```

and add:

```ts
  /** Derive the one editable model from a fresh preview. Header fields stay on the response object
   *  — that is what makes "read-only from the document" structural rather than a disabled input. */
  private seed(r: InboundPreviewResponse): void {
    const p = r.proposal;
    this.lines.set(p ? p.lines.map(l => ({
      lineId: crypto.randomUUID(), description: l.description, amount: l.amount, expenseAccountId: null,
    })) : []);
    this.vendorId.set(p?.suggestedVendorId ?? matchVendor(r.vendorMatch.sellerName, this.pay.vendors())?.id ?? '');
    this.createdBillId.set(null);
  }
```

Extend `reset()` to clear the new state:

```ts
  reset(): void {
    this.stage.set('upload');
    this.preview.set(null);
    this.file.set(null);
    this.message.set(null);
    this.showOtherFindings.set(false);
    this.lines.set([]);
    this.vendorId.set('');
    this.createdBillId.set(null);
  }
```

- [ ] **Step 4: Add the form to the template**

In the template, replace the whole `@if (!p.canImport) { ... }` block with:

```html
          @if (!p.canImport) {
            <p class="text-sm text-muted-foreground max-w-prose">
              This document can't become a bill. Fix it with the vendor, or upload a different file.
            </p>
            <div>
              <button hlmBtn type="button" variant="outline" (click)="reset()">Choose another file</button>
            </div>
          } @else if (createdBillId(); as billId) {
            <div class="border border-border rounded p-3 flex flex-col gap-2">
              <p class="font-semibold">Draft bill created</p>
              <a [routerLink]="['/payables/bills', billId]" class="text-primary hover:underline text-sm">
                Open the draft →
              </a>
            </div>
            <div>
              <button hlmBtn type="button" variant="outline" (click)="reset()">Import another</button>
            </div>
          } @else if (proposal(); as prop) {
            <div class="flex flex-col gap-1 max-w-md">
              <label hlmLabel>Vendor</label>
              <div hlmSelect [value]="vendorId()" [itemToString]="vendorLabel"
                   (valueChange)="vendorId.set($any($event))">
                <hlm-select-trigger class="w-full">
                  <hlm-select-value placeholder="Select a vendor" />
                </hlm-select-trigger>
                <hlm-select-content *hlmSelectPortal>
                  @for (v of pay.vendors(); track v.id) {
                    <hlm-select-item [value]="v.id">{{ v.name }}</hlm-select-item>
                  }
                </hlm-select-content>
              </div>
              @if (canCreateVendor()) {
                <p class="text-xs text-muted-foreground">
                  The document's seller is <span class="font-medium">{{ sellerName() }}</span>, which
                  doesn't match an existing vendor.
                </p>
                <button *appCan="'ap.write'" hlmBtn type="button" variant="outline" size="sm"
                        class="self-start" (click)="createVendor()" [disabled]="creatingVendor()">
                  Create vendor "{{ sellerName() }}"
                </button>
              }
            </div>

            <dl class="grid grid-cols-2 gap-x-6 gap-y-1 text-sm max-w-xl">
              <dt class="text-muted-foreground">Bill date</dt><dd>{{ date(prop.billDate) }}</dd>
              <dt class="text-muted-foreground">Due date</dt>
              <dd>{{ prop.dueDate ? date(prop.dueDate) : '—' }}</dd>
              <dt class="text-muted-foreground">Vendor reference</dt><dd>{{ prop.vendorReference ?? '—' }}</dd>
              <dt class="text-muted-foreground">Memo</dt><dd>{{ prop.memo ?? '—' }}</dd>
            </dl>

            @if (expenseAccounts().length === 0) {
              <p class="text-sm text-destructive">
                No postable expense accounts exist yet —
                <a routerLink="/accounts" class="underline">add one first</a>.
              </p>
            }

            @if (!prop.linesAreDetail) {
              <p class="text-xs text-muted-foreground max-w-prose">
                The document's detail lines couldn't be reconciled to its total, so this is a single
                line at the invoice total. Edit the amount and add lines to split it across accounts.
              </p>
            }

            <table class="w-full text-sm">
              <thead><tr class="text-left text-muted-foreground">
                <th class="py-1">Description</th><th class="pr-2">Expense account</th>
                <th class="text-right pr-5">Amount</th><th></th>
              </tr></thead>
              <tbody>
                @for (line of lines(); track line.lineId; let i = $index) {
                  <tr>
                    <td class="py-1 pr-2">
                      <input hlmInput type="text" class="w-full" [value]="line.description"
                             (input)="patchLine(i, { description: $any($event.target).value })" />
                    </td>
                    <td class="pr-2">
                      <div hlmSelect [value]="line.expenseAccountId ?? ''" [itemToString]="accountLabel"
                           (valueChange)="patchLine(i, { expenseAccountId: $any($event) || null })">
                        <hlm-select-trigger class="w-56">
                          <hlm-select-value placeholder="Select account" />
                        </hlm-select-trigger>
                        <hlm-select-content *hlmSelectPortal>
                          @for (a of expenseAccounts(); track a.id) {
                            <hlm-select-item [value]="a.id">{{ a.number }} · {{ a.name }}</hlm-select-item>
                          }
                        </hlm-select-content>
                      </div>
                    </td>
                    <td class="pr-2"><div class="flex justify-end">
                      <app-currency-input class="w-32" ariaLabel="Amount" [value]="line.amount"
                                          (valueChange)="patchLine(i, { amount: $event })" />
                    </div></td>
                    <td>
                      <button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine(i)">✕</button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>

            <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>

            <div class="text-right text-sm tabular-nums flex flex-col gap-1 w-72 ms-auto">
              <div class="flex justify-between"><span>Document total</span><span>{{ money(documentTotal()) }}</span></div>
              <div class="flex justify-between font-semibold border-t border-border pt-1">
                <span>Lines total</span><span>{{ money(linesTotal()) }}</span>
              </div>
              @if (totalDrift() !== 0) {
                <p class="text-amber-600 text-xs text-left">
                  Lines total {{ money(linesTotal()) }}, document says {{ money(documentTotal()) }}.
                </p>
              }
            </div>

            @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

            <div class="flex items-center gap-2">
              <button *appCan="'ap.write'" hlmBtn type="button" (click)="confirm()"
                      [disabled]="!canConfirm() || busy()">Create draft bill</button>
              <button hlmBtn type="button" variant="outline" (click)="reset()">Choose another file</button>
            </div>
          }
```

The component's `pay` field is referenced by the template, so change its declaration from `private readonly pay` to `protected readonly pay`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: PASS — 9 tests.

- [ ] **Step 6: Commit**

```bash
git add src/app/features/einvoicing/einvoice-import.ts src/app/features/einvoicing/einvoice-import.spec.ts
git commit -m "feat(einvoicing): proposal form with vendor selection, line coding and confirm"
```

---

### Task 7: Duplicate-invoice check

**Files:**
- Modify: `src/app/features/einvoicing/einvoice-import.ts`
- Test: `src/app/features/einvoicing/einvoice-import.spec.ts` (extend)

**Interfaces:**
- Consumes: `PayablesService.listBills(q: BillListQuery)`, `PagedResponse<BillView>`, `findDuplicates` (Task 1), `vendorId` (Task 6).
- Produces: on `EInvoiceImport` — `duplicates: Signal<Bill[]>`, `duplicateScanTotal: Signal<number>`, `duplicateChecked: Signal<boolean>`, and the constant `DUPLICATE_SCAN_LIMIT = 200`.

Payables has no server-side `vendorReference` filter and `vendorId` is required on `BillListQuery`, so this is a bounded client-side scan that runs only once a vendor is chosen.

- [ ] **Step 1: Write the failing tests**

Add inside the existing `describe('EInvoiceImport', ...)`:

```ts
  it('warns when the vendor already has a bill with the same reference', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    fixture.detectChanges();
    const req = ctrl.expectOne(r =>
      r.url === 'http://localhost:5000/clients/C1/bills' && r.params.get('vendorId') === 'v1');
    expect(req.request.params.get('limit')).toBe('200');
    expect(req.request.params.get('order')).toBe('desc');
    req.flush({ items: [
      { bill: { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-01', dueDate: null,
        vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [] },
        openBalance: 0, settlementStatus: 'Paid' },
    ], total: 1, skip: 0, limit: 200 });
    fixture.detectChanges();

    expect(cmp.duplicates().map(b => b.id)).toEqual(['b1']);
    expect(fixture.nativeElement.textContent).toContain('already has a bill');
    expect(cmp.canConfirm()).toBe(false);                    // no accounts coded yet, not because of the warning
    ctrl.verify();
  });

  it('says what it actually checked when the scan is bounded', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    fixture.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills')
      .flush({ items: [], total: 512, skip: 0, limit: 200 });
    fixture.detectChanges();

    expect(cmp.duplicates()).toEqual([]);
    expect(fixture.nativeElement.textContent).toContain('200 most recent');
    ctrl.verify();
  });

  it('does not check when the document carries no vendor reference', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable({ vendorReference: null }));
    fixture.detectChanges();
    cmp.vendorId.set('v1');
    fixture.detectChanges();
    expect(cmp.duplicateChecked()).toBe(false);
    ctrl.verify();                                            // no /bills request at all
  });
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: FAIL — `cmp.duplicates is not a function`.

- [ ] **Step 3: Implement the check**

In `src/app/features/einvoicing/einvoice-import.ts`, extend the payables import to include the list types:

```ts
import { Bill, BillListQuery, DraftBillRequest, billTotal } from '../../core/payables/payables';
```

Add above the `@Component` decorator:

```ts
/** How many of the vendor's most recent bills the duplicate check scans. Payables exposes no
 *  server-side vendorReference filter, so this is a bounded client-side scan — the warning text
 *  says so rather than implying a clean bill of health. */
const DUPLICATE_SCAN_LIMIT = 200;
```

Add to the class, beside the other signals:

```ts
  readonly duplicates = signal<Bill[]>([]);
  readonly duplicateScanTotal = signal(0);
  readonly duplicateChecked = signal(false);
  readonly duplicateScanLimit = DUPLICATE_SCAN_LIMIT;
  readonly duplicateScanBounded = computed(() => this.duplicateScanTotal() > DUPLICATE_SCAN_LIMIT);

  /** Guards the effect below against re-running for a vendor/reference pair it already scanned. */
  #lastDuplicateKey: string | null = null;
```

Add this effect inside the existing `constructor()`, after the vendor-match effect:

```ts
    // Re-runs whenever the vendor or the document changes — including after an inline create.
    effect(() => {
      const vendorId = this.vendorId();
      const reference = this.proposal()?.vendorReference ?? null;
      if (!vendorId || !reference) {
        this.#lastDuplicateKey = null;
        this.duplicates.set([]);
        this.duplicateChecked.set(false);
        return;
      }
      const key = `${vendorId}|${reference}`;
      if (this.#lastDuplicateKey === key) return;
      this.#lastDuplicateKey = key;
      const query: BillListQuery = { vendorId, skip: 0, limit: DUPLICATE_SCAN_LIMIT, order: 'desc' };
      this.pay.listBills(query).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: page => {
          this.duplicates.set(findDuplicates(reference, page.items.map(v => v.bill)));
          this.duplicateScanTotal.set(page.total);
          this.duplicateChecked.set(true);
        },
        // A failed scan is not a failed import — say nothing rather than claim a clean result.
        error: () => { this.duplicateChecked.set(false); },
      });
    });
```

Import `findDuplicates` by extending the einvoicing import:

```ts
import { FindingDto, InboundPreviewResponse, findDuplicates, matchVendor, profileLabel } from '../../core/einvoicing/einvoicing';
```

Clear the check in `reset()` — add these three lines to it:

```ts
    this.duplicates.set([]);
    this.duplicateChecked.set(false);
    this.duplicateScanTotal.set(0);
```

- [ ] **Step 4: Add the warning strip to the template**

Insert directly after the closing `</dl>` of the read-only header block:

```html
            @if (duplicates().length) {
              <div class="border border-amber-400 rounded p-3 text-sm flex flex-col gap-1">
                <p class="font-semibold">This vendor already has a bill with reference
                  {{ prop.vendorReference }}</p>
                <ul class="list-disc ps-5">
                  @for (d of duplicates(); track d.id) {
                    <li>
                      <a [routerLink]="['/payables/bills', d.id]" class="text-primary hover:underline">
                        {{ d.number ?? d.id }}</a>
                      · {{ date(d.billDate) }} · {{ d.status }}
                    </li>
                  }
                </ul>
                <p class="text-xs text-muted-foreground">
                  Re-issues and corrections are legitimate — this is a warning, not a block.
                </p>
              </div>
            } @else if (duplicateChecked() && duplicateScanBounded()) {
              <p class="text-xs text-muted-foreground">
                No duplicate among this vendor's {{ duplicateScanLimit }} most recent bills.
              </p>
            }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: PASS — 12 tests.

- [ ] **Step 6: Commit**

```bash
git add src/app/features/einvoicing/einvoice-import.ts src/app/features/einvoicing/einvoice-import.spec.ts
git commit -m "feat(einvoicing): bounded duplicate-invoice warning on the import screen"
```

---

### Task 8: PDF pane and stale-state guards

**Files:**
- Modify: `src/app/features/einvoicing/einvoice-import.ts`
- Test: `src/app/features/einvoicing/einvoice-import.spec.ts` (extend)

**Interfaces:**
- Consumes: `ClientContextService.clientId`.
- Produces: on `EInvoiceImport` — `pdfUrl: Signal<string | null>`, `showPdf: WritableSignal<boolean>`.

- [ ] **Step 1: Write the failing tests**

Add inside the existing `describe('EInvoiceImport', ...)`:

```ts
  it('creates a blob URL for a PDF upload and revokes it on reset', () => {
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake');
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const { fixture, cmp } = boot();

    const pdf = new File(['%PDF-1.7'], 'invoice.pdf', { type: 'application/pdf' });
    cmp.onFile({ target: { files: [pdf] } } as unknown as Event);
    fixture.detectChanges();
    expect(create).toHaveBeenCalledWith(pdf);
    expect(cmp.pdfUrl()).toBe('blob:fake');

    cmp.reset();
    expect(revoke).toHaveBeenCalledWith('blob:fake');
    expect(cmp.pdfUrl()).toBeNull();
    create.mockRestore();
    revoke.mockRestore();
  });

  it('creates no blob URL for an XML upload', () => {
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake');
    const { cmp } = boot();
    cmp.onFile({ target: { files: [xmlFile()] } } as unknown as Event);
    expect(create).not.toHaveBeenCalled();
    expect(cmp.pdfUrl()).toBeNull();
    create.mockRestore();
  });

  it('resets to the upload stage when the active client changes', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();
    expect(cmp.stage()).toBe('preview');

    TestBed.inject(ClientContextService).select('C2');
    fixture.detectChanges();
    expect(cmp.stage()).toBe('upload');
    expect(cmp.preview()).toBeNull();
    ctrl.match(() => true).forEach(r => r.flush([]));        // vendors/accounts refetch for C2
    ctrl.verify();
  });
```

Add `import { vi } from 'vitest';` to the spec's import block.

- [ ] **Step 2: Run tests to verify they fail**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: FAIL — `cmp.pdfUrl is not a function`.

- [ ] **Step 3: Implement the pane and the guards**

In `src/app/features/einvoicing/einvoice-import.ts`, add the client-context and sanitizer imports:

```ts
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { ClientContextService } from '../../core/client/client-context.service';
```

Add to the class, beside the other signals:

```ts
  private readonly client = inject(ClientContextService);
  private readonly sanitizer = inject(DomSanitizer);

  readonly pdfUrl = signal<string | null>(null);
  readonly showPdf = signal(true);

  /** Angular treats `<object data>` as a RESOURCE_URL context and throws on a plain string. The
   *  bypass is safe: this is a blob URL the app itself minted from a file the operator just picked. */
  readonly safePdfUrl = computed<SafeResourceUrl | null>(() => {
    const url = this.pdfUrl();
    return url ? this.sanitizer.bypassSecurityTrustResourceUrl(url) : null;
  });

  /** Tracks the client this screen's state belongs to. `undefined` means "not yet observed", which
   *  is distinct from a genuinely null client id. */
  #knownClient: string | null | undefined = undefined;
```

Add to the `constructor()`, after the duplicate-check effect:

```ts
    // A proposal on screen under a different client's name is worth preventing outright — posting
    // would fail anyway (the old vendorId does not exist under the new client), but silently.
    effect(() => {
      const id = this.client.clientId();
      if (this.#knownClient === undefined) { this.#knownClient = id; return; }
      if (id !== this.#knownClient) { this.#knownClient = id; this.reset(); }
    });

    this.destroyRef.onDestroy(() => this.revokePdf());
```

Add these methods:

```ts
  private revokePdf(): void {
    const url = this.pdfUrl();
    if (url) URL.revokeObjectURL(url);
    this.pdfUrl.set(null);
  }

  private isPdf(f: File): boolean {
    return f.type === 'application/pdf' || f.name.toLowerCase().endsWith('.pdf');
  }
```

In `onFile()`, replace `this.file.set(picked);` with:

```ts
    this.file.set(picked);
    this.revokePdf();
    if (picked && this.isPdf(picked)) this.pdfUrl.set(URL.createObjectURL(picked));
    this.showPdf.set(true);
```

In `reset()`, add `this.revokePdf();` as its first statement.

- [ ] **Step 4: Add the pane to the template**

Insert directly after the document strip's closing `</div>` (the block showing file name, syntax and profile badges), before the errors block:

```html
          @if (safePdfUrl(); as url) {
            <div class="flex flex-col gap-1">
              <button hlmBtn type="button" variant="ghost" size="sm" class="self-start"
                      (click)="showPdf.set(!showPdf())">
                {{ showPdf() ? 'Hide' : 'Show' }} source document
              </button>
              @if (showPdf()) {
                <object [data]="url" type="application/pdf" class="w-full h-[32rem] border border-border rounded">
                  <p class="p-3 text-sm text-muted-foreground">
                    Your browser can't display this PDF inline.
                  </p>
                </object>
              }
            </div>
          }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: PASS — 15 tests.

- [ ] **Step 6: Commit**

```bash
git add src/app/features/einvoicing/einvoice-import.ts src/app/features/einvoicing/einvoice-import.spec.ts
git commit -m "feat(einvoicing): collapsible source-PDF pane and stale-state guards"
```

---

### Task 9: Route, navigation and entry point

**Files:**
- Modify: `src/app/app.routes.ts`
- Modify: `src/app/layout/nav.ts:18`
- Modify: `src/app/features/payables/bill-list.ts:38-39`
- Test: `src/app/features/payables/bill-list.spec.ts` (extend)

**Interfaces:**
- Consumes: `EInvoiceImport` (Tasks 5-8), `moduleEnabled` (Task 3), `canWrite`.
- Produces: the reachable route `/payables/bills/import`.

`app.routes.spec.ts` already asserts every nav leaf resolves to a route, so the nav link and the route must land together.

- [ ] **Step 1: Write the failing test**

Add inside the existing `describe('BillList', ...)` in `src/app/features/payables/bill-list.spec.ts`:

```ts
  it('offers the e-invoice import only when the module is enabled', () => {
    const caps = new StubCapabilityService();
    caps.set(['ap.write']);
    caps.setEnabledModules([]);
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
        provideHttpClientTesting(), { provide: CapabilityService, useValue: caps }],
    });
    TestBed.inject(ClientContextService).select('C1');
    const ctrl = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(BillList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).not.toContain('Import e-invoice');

    caps.setEnabledModules(['einvoicing']);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Import e-invoice');
    ctrl.verify();
  });
```

Add to that spec's imports: `CapabilityService` from `../../core/capabilities/capability.service` and `StubCapabilityService` from `../../core/capabilities/capability.testing`.

- [ ] **Step 2: Run test to verify it fails**

Run: `npx ng test --include=src/app/features/payables/bill-list.spec.ts --watch=false`
Expected: FAIL — "Import e-invoice" is never rendered.

- [ ] **Step 3: Add the bill-list entry point**

In `src/app/features/payables/bill-list.ts`, replace lines 38-39:

```html
        <a *appCan="'ap.write'" hlmBtn size="sm" class="ms-auto" routerLink="/payables/bills/new"
           [class.pointer-events-none]="!vendorId()" [class.opacity-50]="!vendorId()">New bill</a>
```

with:

```html
        <div class="ms-auto flex items-center gap-2">
          @if (caps.moduleEnabled('einvoicing')) {
            <a *appCan="'ap.write'" hlmBtn size="sm" variant="outline"
               routerLink="/payables/bills/import">Import e-invoice</a>
          }
          <a *appCan="'ap.write'" hlmBtn size="sm" routerLink="/payables/bills/new"
             [class.pointer-events-none]="!vendorId()" [class.opacity-50]="!vendorId()">New bill</a>
        </div>
```

Add the capability service to the class body (place it beside the existing injected fields):

```ts
  protected readonly caps = inject(CapabilityService);
```

and add its import:

```ts
import { CapabilityService } from '../../core/capabilities/capability.service';
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx ng test --include=src/app/features/payables/bill-list.spec.ts --watch=false`
Expected: PASS.

- [ ] **Step 5: Register the route**

In `src/app/app.routes.ts`, add the import beside the other payables imports:

```ts
import { EInvoiceImport } from './features/einvoicing/einvoice-import';
import { moduleEnabled } from './core/capabilities/module.guard';
```

and add this child to the `payables` children array, immediately after the `bills/new` entry:

```ts
    { path: 'bills/import', component: EInvoiceImport, canActivate: [canWrite, moduleEnabled],
      data: { requiredCapability: 'ap.write', requiredModule: 'einvoicing', fallback: '/payables/bills' } },
```

Note the ordering: `bills/import` must come before `bills/:id` so the router does not match `import` as a bill id.

- [ ] **Step 6: Add the nav link**

In `src/app/layout/nav.ts`, replace line 18:

```ts
    { label: 'Payables', path: '/payables', area: 'ap', moduleKey: 'payables' },
```

with:

```ts
    { label: 'Payables', path: '/payables', area: 'ap', moduleKey: 'payables', children: [
      { label: 'Import e-invoice', path: '/payables/bills/import', area: 'ap', moduleKey: 'einvoicing' },
    ] },
```

- [ ] **Step 7: Verify the route and nav specs still pass**

Run: `npx ng test --include=src/app/app.routes.spec.ts --watch=false`
Expected: PASS — `/payables/bills/import` resolves via the payables children expansion, and the built-path prefix `/payables` keeps a Placeholder route from being generated for it.

Run: `npx ng test --include=src/app/layout/nav.spec.ts --watch=false`
Expected: PASS — the existing moduleKey test asserts `subledgers.items.map(i => i.path)` equals `['/receivables']`, which is unaffected by adding a *child* to Payables (the Payables item itself is filtered out when `payables` is not enabled).

If the nav spec fails because a `children` array now survives on a filtered item, fix the *test's* expectation, not `visibleSections` — the filter's contract (children filtered by the same predicate, empty arrays dropped) is already correct.

- [ ] **Step 8: Run the full suite**

Run: `npm test -- --watch=false`
Expected: PASS — the entire suite, with no regressions.

- [ ] **Step 9: Verify the production build**

Run: `npm run build`
Expected: build succeeds with no errors.

- [ ] **Step 10: Commit**

```bash
git add src/app/app.routes.ts src/app/layout/nav.ts src/app/features/payables/bill-list.ts src/app/features/payables/bill-list.spec.ts
git commit -m "feat(einvoicing): route, nav link and bill-list entry point for e-invoice import"
```

---

## Manual verification

After Task 9, confirm the screen works end to end against a running host:

1. Start the API and `npm start`.
2. Ensure the demo client has `einvoicing` in its enabled modules — otherwise the nav link, the bill-list button and the route are all correctly invisible, which is the point.
3. Navigate to Payables → Bills. The "Import e-invoice" button appears.
4. Upload a Factur-X PDF: the pane renders the printed invoice, the profile badge reads its conformance level, the proposal lists a line at the invoice total.
5. Code the line to an expense account, confirm the vendor, create the draft, and follow the link to the resulting bill.
6. Upload the same file again: the duplicate strip appears and links the bill you just made.
7. Upload a UBL credit note: the screen shows the blocking finding and offers no form.
