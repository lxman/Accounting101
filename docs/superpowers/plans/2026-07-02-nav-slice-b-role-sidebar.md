# Slice B — Role-Based Sidebar — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Filter the sidebar by the acting user's capabilities (fetched from the Slice-A endpoint, re-resolved on client/identity change), with a tight narrow-clerk default and a dev roster that demonstrates the range.

**Architecture:** A root `CapabilityService` reactively GETs `/clients/{id}/me/capabilities`. `nav.ts` gains an `area` per link + a pure `visibleSections` filter. The shell renders a `visibleNav` computed from `NAV` ∩ capabilities. Narrow-clerk presets are tightened backend-side; the dev identity roster is expanded so the switcher shows distinct sidebars.

**Tech Stack:** Angular 22 (standalone, zoneless, OnPush, signals), Spartan NG helm, vitest; .NET backend for the preset tweak.

## Global Constraints

- **Sidebar visibility only** — no screen/button write-gating (Slice C).
- The backend `gl.*` equivalence invariant must stay intact — only narrow-clerk subledger/assurance reads change; `CapabilityModelTests` stays green.
- Angular: standalone, zoneless, OnPush, signals; `takeUntilDestroyed`/`toSignal` patterns; vitest via `cd UI/Angular && npx ng test --watch=false`.
- Backend tests via `cd C:\Users\jorda\RiderProjects\Accounting101 && dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`.
- `environment.ts` (devClientId) + IDE csproj/slnx churn stay UNCOMMITTED — stage explicit paths only, never `git add -A`.
- camelCase JSON on the wire.
- Capability areas (fixed): `gl, ar, ap, payroll, cash, bankrec, fixedassets, audit, reports, admin`.

---

### Task 1: Tighten narrow-clerk presets (backend)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/RolePresets.cs`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs` (the AR-clerk assertion)

**Interfaces:**
- Consumes/Produces: `RolePresets.For(role)` unchanged signature; only the four narrow-clerk entries change contents.

- [ ] **Step 1: Update the failing test** — in `CapabilitiesTests.cs`, the test `A_narrow_ar_clerk_can_write_ar_but_not_ap` currently asserts `ap.read` is present. Change it to assert the tight scope:

```csharp
    [Fact]
    public async Task A_narrow_ar_clerk_can_write_ar_but_not_ap()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.ArClerk);
        CapabilitiesResponse body = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Contains("ar.write", body.Capabilities);
        Assert.Contains("ar.read", body.Capabilities);
        Assert.Contains("gl.read", body.Capabilities);
        Assert.DoesNotContain("ap.write", body.Capabilities);
        Assert.DoesNotContain("ap.read", body.Capabilities);   // tight scope: no other-module reads
        Assert.DoesNotContain("audit.read", body.Capabilities);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: FAIL — the AR clerk currently holds `ap.read`/`audit.read` (broad preset).

- [ ] **Step 3: Tighten the four narrow-clerk presets** in `RolePresets.cs` — replace the four narrow-clerk entries in the `Map` initializer:

```csharp
        [LedgerRole.ArClerk] = [Capabilities.GlRead, Capabilities.ArRead, Capabilities.ArWrite],
        [LedgerRole.ApClerk] = [Capabilities.GlRead, Capabilities.ApRead, Capabilities.ApWrite],
        [LedgerRole.PayrollClerk] = [Capabilities.GlRead, Capabilities.PayrollRead, Capabilities.PayrollWrite],
        [LedgerRole.CashClerk] = [Capabilities.GlRead, Capabilities.CashRead, Capabilities.CashWrite, Capabilities.BankRecRead, Capabilities.BankRecWrite],
```

(Leave Auditor/Clerk/Approver/Controller/Admin entries unchanged.)

- [ ] **Step 4: Run to verify green**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS. `CapabilityModelTests` (narrow clerks hold only gl.read among gl.*; equivalence) still green — the gl.* of each narrow clerk is still exactly `{gl.read}`.

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/RolePresets.cs Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs
git commit -m "feat(control): tight default read scope for narrow per-module clerks"
```

---

### Task 2: CapabilityService (frontend)

**Files:**
- Create: `UI/Angular/src/app/core/capabilities/capabilities.ts` (types)
- Create: `UI/Angular/src/app/core/capabilities/capability.service.ts`
- Test: `UI/Angular/src/app/core/capabilities/capability.service.spec.ts`

**Interfaces:**
- Consumes: `ClientContextService.clientId()`, `DevIdentityService.active()`, `HttpClient`, `environment.apiBaseUrl`.
- Produces: `CapabilityService` with `capabilities: Signal<ReadonlySet<string>>`, `roles: Signal<string[]>`, `deploymentAdmin: Signal<boolean>`, `has(cap): boolean`, `hasArea(area): boolean`.

- [ ] **Step 1: Write the failing test** — `capability.service.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CapabilityService } from './capability.service';
import { ClientContextService } from '../client/client-context.service';
import { DevIdentityService } from '../api/dev-identity.service';
import { environment } from '../api/environment';

describe('CapabilityService', () => {
  let http: HttpTestingController;
  let svc: CapabilityService;
  let client: ClientContextService;
  let identity: DevIdentityService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(ClientContextService);
    identity = TestBed.inject(DevIdentityService);
    svc = TestBed.inject(CapabilityService);
  });

  afterEach(() => http.verify());

  function flush(caps: string[], roles: string[] = [], deploymentAdmin = false) {
    const clientId = client.clientId();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/${clientId}/me/capabilities`);
    req.flush({ capabilities: caps, roles, deploymentAdmin });
  }

  it('starts empty with no client selected', () => {
    expect(svc.capabilities().size).toBe(0);
    expect(svc.hasArea('gl')).toBe(false);
  });

  it('fetches and exposes capabilities when a client is selected', async () => {
    client.select('c1');
    TestBed.tick();                       // let the reactive effect emit
    flush(['gl.read', 'ar.read', 'ar.write'], ['ArClerk']);
    expect(svc.has('ar.write')).toBe(true);
    expect(svc.hasArea('ar')).toBe(true);
    expect(svc.hasArea('ap')).toBe(false);
    expect(svc.roles()).toEqual(['ArClerk']);
  });

  it('re-fetches when the acting identity changes', () => {
    client.select('c1');
    TestBed.tick();
    flush(['gl.read']);                    // first identity
    identity.use(identity.identities[1].sub);
    TestBed.tick();
    flush(['gl.read', 'gl.post', 'ar.write'], ['Controller']);  // second identity
    expect(svc.hasArea('ar')).toBe(true);
    expect(svc.has('gl.post')).toBe(true);
  });

  it('treats a 403 as an empty capability set', () => {
    client.select('c1');
    TestBed.tick();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/me/capabilities`);
    req.flush('nope', { status: 403, statusText: 'Forbidden' });
    expect(svc.capabilities().size).toBe(0);
  });
});
```

*(If `TestBed.tick()` is unavailable in this Angular/vitest setup, replace with `await Promise.resolve()` after a `TestBed.flushEffects()` call, or inject `ApplicationRef` and call `.tick()`. The reactive read must be flushed so `toObservable` emits before `expectOne`.)*

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `CapabilityService` does not exist.

- [ ] **Step 3: Create `core/capabilities/capabilities.ts`**:

```ts
export interface CapabilitiesResponse {
  capabilities: string[];
  roles: string[];
  deploymentAdmin: boolean;
}

export const EMPTY_CAPABILITIES: CapabilitiesResponse = { capabilities: [], roles: [], deploymentAdmin: false };
```

- [ ] **Step 4: Create `core/capabilities/capability.service.ts`**:

```ts
import { Injectable, Signal, computed, inject } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { HttpClient } from '@angular/common/http';
import { Observable, of, switchMap, catchError } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { DevIdentityService } from '../api/dev-identity.service';
import { CapabilitiesResponse, EMPTY_CAPABILITIES } from './capabilities';

/**
 * The acting user's resolved capabilities on the active client — the single source of truth the
 * sidebar (and, later, screens) use to decide what is visible/enabled. Re-resolves whenever the
 * client or the acting identity (the "Acting as" switcher) changes; a 403 / no client yields an
 * empty set (nothing beyond always-visible destinations).
 */
@Injectable({ providedIn: 'root' })
export class CapabilityService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);
  private readonly identity = inject(DevIdentityService);

  private readonly key = computed(() => {
    const clientId = this.client.clientId();
    return clientId ? { clientId, sub: this.identity.active().sub } : null;
  });

  private readonly response = toSignal(
    toObservable(this.key).pipe(
      switchMap((k): Observable<CapabilitiesResponse> =>
        k
          ? this.http
              .get<CapabilitiesResponse>(`${environment.apiBaseUrl}/clients/${k.clientId}/me/capabilities`)
              .pipe(catchError(() => of(EMPTY_CAPABILITIES)))
          : of(EMPTY_CAPABILITIES)),
    ),
    { initialValue: EMPTY_CAPABILITIES },
  );

  readonly capabilities: Signal<ReadonlySet<string>> = computed(() => new Set(this.response().capabilities));
  readonly roles: Signal<string[]> = computed(() => this.response().roles);
  readonly deploymentAdmin: Signal<boolean> = computed(() => this.response().deploymentAdmin);

  has(capability: string): boolean { return this.capabilities().has(capability); }

  /** True if the user holds any capability in the given area (e.g. "ar" matches "ar.read"/"ar.write"). */
  hasArea(area: string): boolean {
    const prefix = area + '.';
    for (const c of this.capabilities()) if (c.startsWith(prefix)) return true;
    return false;
  }
}
```

- [ ] **Step 5: Run to verify green**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS (capability.service.spec green; full suite green).

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/capabilities.ts UI/Angular/src/app/core/capabilities/capability.service.ts UI/Angular/src/app/core/capabilities/capability.service.spec.ts
git commit -m "feat(nav): CapabilityService reactively resolves /me/capabilities"
```

---

### Task 3: Nav area binding + sidebar filtering

**Files:**
- Modify: `UI/Angular/src/app/layout/nav.ts` (add `area`, add `visibleSections`)
- Modify: `UI/Angular/src/app/layout/shell.ts` (render `visibleNav`)
- Test: `UI/Angular/src/app/layout/nav.spec.ts` (extend), `UI/Angular/src/app/layout/shell.spec.ts` (update)

**Interfaces:**
- Consumes: `CapabilityService.hasArea` (Task 2).
- Produces: `NavLink.area?`, `visibleSections(nav, canSee)`.

- [ ] **Step 1: Extend `nav.spec.ts`** — add:

```ts
import { NAV, navLeafPaths, visibleSections, NavSection } from './nav';

describe('visibleSections', () => {
  const sectionLabels = (s: NavSection[]) => s.map(x => x.label);

  it('shows only Overview when nothing is permitted', () => {
    const v = visibleSections(NAV, area => !area);   // only area-less links (Dashboard)
    expect(sectionLabels(v)).toEqual(['Overview']);
  });

  it('shows GL + Receivables for an AR-clerk-like scope', () => {
    const allowed = new Set(['gl', 'ar']);
    const v = visibleSections(NAV, area => !area || allowed.has(area));
    expect(sectionLabels(v)).toEqual(['Overview', 'General Ledger', 'Subledgers']);
    const subledgers = v.find(s => s.label === 'Subledgers')!;
    expect(subledgers.items.map(i => i.path)).toEqual(['/receivables']);
  });

  it('shows Administration when admin is permitted', () => {
    const v = visibleSections(NAV, () => true);
    expect(sectionLabels(v)).toContain('Administration');
  });

  it('filters a parent\'s children by their own area', () => {
    const allowed = new Set(['cash']);   // cash but not bankrec
    const v = visibleSections(NAV, area => !area || allowed.has(area));
    const cash = v.find(s => s.label === 'Subledgers')!.items.find(i => i.path === '/cash')!;
    expect(cash.children ?? []).toEqual([]);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `visibleSections` / `area` not present.

- [ ] **Step 3: Update `nav.ts`** — add `area?: string` to `NavLink`, annotate every link, and add `visibleSections`. Full new file:

```ts
export interface NavLink { label: string; path: string; area?: string; children?: NavLink[]; }
export interface NavSection { label: string; items: NavLink[]; }

export const NAV: NavSection[] = [
  { label: 'Overview', items: [
    { label: 'Dashboard', path: '/dashboard' },
  ] },
  { label: 'General Ledger', items: [
    { label: 'Journal', path: '/journal', area: 'gl' },
    { label: 'Approvals', path: '/journal/approvals', area: 'gl' },
    { label: 'Chart of Accounts', path: '/accounts', area: 'gl' },
    { label: 'Trial Balance', path: '/trial-balance', area: 'gl' },
    { label: 'Financial Statements', path: '/statements', area: 'gl' },
    { label: 'Period Close', path: '/periods', area: 'gl' },
  ] },
  { label: 'Subledgers', items: [
    { label: 'Receivables', path: '/receivables', area: 'ar' },
    { label: 'Payables', path: '/payables', area: 'ap' },
    { label: 'Payroll', path: '/payroll', area: 'payroll' },
    { label: 'Cash & Banking', path: '/cash', area: 'cash', children: [
      { label: 'Bank Reconciliation', path: '/cash/reconciliation', area: 'bankrec' },
    ] },
    { label: 'Fixed Assets', path: '/fixed-assets', area: 'fixedassets' },
  ] },
  { label: 'Assurance', items: [
    { label: 'Audit', path: '/audit', area: 'audit', children: [
      { label: 'Audit Trail', path: '/audit/trail', area: 'audit' },
      { label: 'Verify Integrity', path: '/audit/verify', area: 'audit' },
      { label: 'Subledger Reconciliations', path: '/audit/reconciliations', area: 'audit' },
    ] },
    { label: 'Reports', path: '/reports', area: 'reports', children: [
      { label: 'Budgets', path: '/reports/budgets', area: 'reports' },
    ] },
  ] },
  { label: 'Administration', items: [
    { label: 'Users & Roles', path: '/admin/users', area: 'admin' },
    { label: 'Firm', path: '/admin/firm', area: 'admin' },
    { label: 'Client', path: '/admin/client', area: 'admin' },
    { label: 'Fiscal settings', path: '/admin/fiscal', area: 'admin' },
    { label: 'Posting accounts', path: '/admin/posting-accounts', area: 'admin' },
  ] },
];

export function navLeafPaths(): string[] {
  const out: string[] = [];
  const walk = (links: NavLink[]): void => {
    for (const l of links) {
      out.push(l.path);
      if (l.children) walk(l.children);
    }
  };
  for (const section of NAV) walk(section.items);
  return out;
}

/** Sections/links the user can see: a link shows if it has no area or `canSee(area)` is true.
 * Children are filtered by their own area; sections with no visible items are dropped. */
export function visibleSections(nav: NavSection[], canSee: (area?: string) => boolean): NavSection[] {
  return nav
    .map((section) => ({
      label: section.label,
      items: section.items
        .filter((item) => canSee(item.area))
        .map((item) => (item.children ? { ...item, children: item.children.filter((c) => canSee(c.area)) } : item)),
    }))
    .filter((section) => section.items.length > 0);
}
```

- [ ] **Step 4: Update `shell.ts`** — inject `CapabilityService`, compute `visibleNav`, render it. Changes:
  1. Add import: `import { NAV, visibleSections } from './nav';` (replace the existing `import { NAV } from './nav';`) and `import { CapabilityService } from '../core/capabilities/capability.service';` and add `computed` to the `@angular/core` import.
  2. Add field + computed in the class:

```ts
  protected readonly caps = inject(CapabilityService);
  protected readonly visibleNav = computed(() => visibleSections(NAV, (a) => !a || this.caps.hasArea(a)));
```

  3. In the template, change the sections loop from `@for (section of nav; track section.label)` to `@for (section of visibleNav(); track section.label)`. (Keep the existing `protected readonly nav = NAV;` only if still referenced elsewhere; otherwise remove it. `NavStateService` calls are unchanged — they take labels/paths.)

- [ ] **Step 5: Update `shell.spec.ts`** — the shell now injects `CapabilityService`, which would hit HttpClient. Override it with a stub. At the top of the `describe`, add a configurable stub and provide it; existing tests get a full-capability stub (everything visible) so they behave unchanged; add filtering tests.

Replace the `make()` helper and add tests:

```ts
import { CapabilityService } from '../core/capabilities/capability.service';

class StubCaps {
  areas = new Set<string>();
  hasArea(a: string) { return this.areas.has(a); }
  has() { return false; }
  capabilities() { return new Set<string>(); }
  roles() { return []; }
  deploymentAdmin() { return false; }
}

describe('Shell', () => {
  async function make(areas: string[] = ['gl','ar','ap','payroll','cash','bankrec','fixedassets','audit','reports','admin']) {
    const stub = new StubCaps();
    areas.forEach(a => stub.areas.add(a));
    await TestBed.configureTestingModule({
      imports: [Shell],
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    }).compileComponents();
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }
  // ... existing helper `sectionHeader` and existing accordion tests unchanged (they use the default full-areas make()) ...

  it('hides sections the user has no capability for (AR-clerk scope)', async () => {
    const el = (await make(['gl', 'ar'])).nativeElement as HTMLElement;
    expect(el.textContent).toContain('General Ledger');
    expect(el.textContent).toContain('Subledgers');   // Receivables lives here
    expect(el.textContent).not.toContain('Payables');
    expect(el.textContent).not.toContain('Assurance');
    expect(el.textContent).not.toContain('Administration');
  });

  it('shows Administration only when admin capability is present', async () => {
    const withAdmin = (await make(['admin'])).nativeElement as HTMLElement;
    expect(withAdmin.textContent).toContain('Administration');
    const without = (await make(['gl'])).nativeElement as HTMLElement;
    expect(without.textContent).not.toContain('Administration');
  });
});
```

*(Keep every existing accordion/collapse/switcher test; they now run with the default full-capability stub, so all sections are present exactly as before. The auto-open-active test that navigates to `/cash/reconciliation` needs `cash` and `bankrec` in its stub — the default full-areas make() covers it.)*

- [ ] **Step 6: Run to verify green**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — nav.spec (visibleSections), shell.spec (filtering + accordion), full suite green.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/layout/nav.ts UI/Angular/src/app/layout/nav.spec.ts UI/Angular/src/app/layout/shell.ts UI/Angular/src/app/layout/shell.spec.ts
git commit -m "feat(nav): filter sidebar by capability area (role-based visibility)"
```

---

### Task 4: Dev identity roster

**Files:**
- Create: `UI/Angular/src/app/core/api/dev-identities.ts`
- Modify: `UI/Angular/src/app/core/api/dev-identity.service.ts`
- Test: `UI/Angular/src/app/core/api/dev-identity.service.spec.ts` (update)

**Interfaces:**
- Produces: `DEV_IDENTITIES: DevIdentity[]`; `DevIdentityService.identities` reads it.

- [ ] **Step 1: Update `dev-identity.service.spec.ts`** — assert the five-identity roster and switching:

```ts
import { TestBed } from '@angular/core/testing';
import { DevIdentityService } from './dev-identity.service';

describe('DevIdentityService', () => {
  it('offers the five dev roles and starts on the first', () => {
    const svc = TestBed.inject(DevIdentityService);
    expect(svc.identities.map(i => i.name)).toEqual([
      'Dev Controller', 'Dev Approver', 'Dev Auditor', 'Dev AR Clerk', 'Dev Admin',
    ]);
    expect(svc.active()).toBe(svc.identities[0]);
  });

  it('switches the active identity by sub', () => {
    const svc = TestBed.inject(DevIdentityService);
    svc.use(svc.identities[3].sub);
    expect(svc.active().name).toBe('Dev AR Clerk');
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — roster differs / `DEV_IDENTITIES` absent.

- [ ] **Step 3: Create `core/api/dev-identities.ts`**:

```ts
import { DevIdentity } from './dev-identity.service';

/** Fixed dev identities for the "Acting as" switcher. Subs are fixed non-secret GUIDs; each gets a
 * membership seeded in .localdev so /me/capabilities resolves distinct capability sets. Role claims
 * are decorative (authority comes from the membership); admin=true drives the deployment-admin flag. */
export const DEV_IDENTITIES: DevIdentity[] = [
  { sub: '00000000-0000-0000-0000-000000000001', name: 'Dev Controller', claims: [{ type: 'role', value: 'Controller' }] },
  { sub: '00000000-0000-0000-0000-000000000002', name: 'Dev Approver', claims: [{ type: 'role', value: 'Approver' }, { type: 'admin', value: 'true' }] },
  { sub: '00000000-0000-0000-0000-000000000003', name: 'Dev Auditor', claims: [{ type: 'role', value: 'Auditor' }] },
  { sub: '00000000-0000-0000-0000-000000000004', name: 'Dev AR Clerk', claims: [{ type: 'role', value: 'ArClerk' }] },
  { sub: '00000000-0000-0000-0000-000000000005', name: 'Dev Admin', claims: [{ type: 'role', value: 'Admin' }, { type: 'admin', value: 'true' }] },
];
```

- [ ] **Step 4: Update `dev-identity.service.ts`** — read the roster from the constant (keep the `DevIdentity` interface exported here):

```ts
import { Injectable, signal } from '@angular/core';
import { DEV_IDENTITIES } from './dev-identities';

export interface DevIdentity { sub: string; name: string; claims: { type: string; value: string }[]; }

@Injectable({ providedIn: 'root' })
export class DevIdentityService {
  readonly identities: readonly DevIdentity[] = DEV_IDENTITIES;
  private readonly _active = signal<DevIdentity>(this.identities[0]);
  readonly active = this._active.asReadonly();

  use(sub: string): void {
    const match = this.identities.find(i => i.sub === sub);
    if (match) this._active.set(match);
  }
}
```

(`dev-identities.ts` imports `DevIdentity` from the service; the service imports `DEV_IDENTITIES` from `dev-identities.ts` — a type-only ↔ value split, no runtime cycle. If the toolchain complains about the cycle, move the `DevIdentity` interface into `dev-identities.ts` and re-export it from the service.)

- [ ] **Step 5: Run to verify green**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — full frontend suite green. (Existing `auth.interceptor.spec` / `dev-token.spec` that reference `environment.devClerk`/`devApprover` remain valid — those still exist in `environment.ts`; only the service's roster source changed. If any spec asserted a two-item roster, update it to the five-item roster.)

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/api/dev-identities.ts UI/Angular/src/app/core/api/dev-identity.service.ts UI/Angular/src/app/core/api/dev-identity.service.spec.ts
git commit -m "feat(nav): expand dev identity roster to five roles for the switcher"
```

## Self-Review

- **Spec coverage:** Task 1 = tight narrow-clerk presets + backend test; Task 2 = CapabilityService; Task 3 = nav area binding + visibleSections + shell filter; Task 4 = dev roster. All spec changes covered.
- **Type consistency:** `CapabilitiesResponse`/`EMPTY_CAPABILITIES`/`hasArea`/`visibleSections`/`DEV_IDENTITIES`/`DevIdentity` names consistent across tasks. `NavLink.area` used by both nav.ts and visibleSections.
- **Green-build ordering:** Task 1 backend-only. Task 2 additive (new service). Task 3 depends on Task 2 (`CapabilityService`) — shell imports it. Task 4 independent of 2/3. Each task ends green.
- **Placeholder scan:** none — all new files/edits shown in full.
- **Reactive-flush caveat noted** in Task 2 for the test harness (TestBed.tick vs flushEffects).
- **Cycle caveat noted** in Task 4 for the interface/const split.

## Execution Handoff

Subagent-driven. Four tasks; 3 depends on 2, others independent — run 1 → 2 → 3 → 4. After Task 4: controller seeds the three new `.localdev` memberships (subs …0003 Auditor, …0004 ArClerk, …0005 Admin) and smoke-tests distinct sidebars across the switcher.
