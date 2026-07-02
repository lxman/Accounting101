# Slice C — In-Screen Write-Gating — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hide write controls on the built screens unless the user holds the governing capability, and guard the write routes.

**Architecture:** A `*appCan` structural directive hides controls; a `canWrite(cap, fallback)` route-guard factory redirects when the capability is absent; `CapabilityService` gains a `loaded` signal so the guard waits out the initial fetch. A shared test helper (`StubCapabilityService`/`provideCapabilities`) lets the many affected specs grant capabilities in one line.

**Tech Stack:** Angular 22 (standalone, zoneless, OnPush, signals), Angular CDK drag-drop, Spartan NG helm, vitest.

## Global Constraints

- **Hide** (structural, remove from DOM), not disable. **Route guards** on all write routes.
- Backend is the real gate (Slice E); this is the UI layer only — do NOT add backend changes.
- Only the five BUILT areas (receivables/payables/payroll/journal/accounts). No unbuilt areas.
- Angular: standalone, zoneless, OnPush, signals; vitest via `cd UI/Angular && npx ng test --watch=false`.
- `environment.ts` (devClientId) + IDE csproj/slnx churn stay UNCOMMITTED — stage explicit paths only, never `git add -A`.
- Capability strings are fixed: `ar.write, ap.write, payroll.write, gl.post, gl.approve, gl.void, gl.manageAccounts`.
- Do NOT gate the journal **Validate** button (advisory). No reverse/revise controls exist.

---

### Task 1: `*appCan` directive + `canWrite` guard + `loaded` signal + test helper

**Files:**
- Create: `UI/Angular/src/app/core/capabilities/can.directive.ts`
- Create: `UI/Angular/src/app/core/capabilities/can.guard.ts`
- Create: `UI/Angular/src/app/core/capabilities/capability.testing.ts`
- Modify: `UI/Angular/src/app/core/capabilities/capability.service.ts` (add `loaded`)
- Test: `can.directive.spec.ts`, `can.guard.spec.ts` (new); extend `capability.service.spec.ts`

**Interfaces:**
- Produces: `CanDirective` (`selector: '[appCan]'`, input `appCan: string | string[]`); `canWrite(capability: string, fallback: string): CanActivateFn`; `CapabilityService.loaded: Signal<boolean>`; `StubCapabilityService` + `provideCapabilities(...caps: string[])`.

- [ ] **Step 1: Add `loaded` to `CapabilityService`** — replace the `response`/derived block (lines 27-50) so a sentinel distinguishes loading from loaded-empty:

```ts
  private readonly response = toSignal<CapabilitiesResponse>(
    toObservable(this.key).pipe(
      switchMap((k): Observable<CapabilitiesResponse> =>
        k
          ? this.http
              .get<CapabilitiesResponse>(`${environment.apiBaseUrl}/clients/${k.clientId}/me/capabilities`)
              .pipe(catchError(() => of(EMPTY_CAPABILITIES)))
          : of(EMPTY_CAPABILITIES)),
    ),
    { initialValue: LOADING },
  );

  /** False until the first /me/capabilities response for the current key resolves. */
  readonly loaded: Signal<boolean> = computed(() => this.response() !== LOADING);

  private readonly current = computed<CapabilitiesResponse>(() => {
    const r = this.response();
    return r === LOADING ? EMPTY_CAPABILITIES : r;
  });

  readonly capabilities: Signal<ReadonlySet<string>> = computed(() => new Set(this.current().capabilities));
  readonly roles: Signal<string[]> = computed(() => this.current().roles);
  readonly deploymentAdmin: Signal<boolean> = computed(() => this.current().deploymentAdmin);

  has(capability: string): boolean { return this.capabilities().has(capability); }

  hasArea(area: string): boolean {
    const prefix = area + '.';
    for (const c of this.capabilities()) if (c.startsWith(prefix)) return true;
    return false;
  }
```

Add the sentinel at the top of the file (after imports), and widen the `toSignal` type param:

```ts
/** Sentinel distinguishing "not yet loaded" from a loaded-but-empty response. */
const LOADING = Symbol('capabilities-loading');
```

Change the `response` field declaration type to `toSignal<CapabilitiesResponse | typeof LOADING>(...)` — i.e. the generic and comparisons use `LOADING`. (Keep `import { Signal }` — already present.)

- [ ] **Step 2: Extend `capability.service.spec.ts`** — add:

```ts
  it('loaded is false before the first response and true after', () => {
    expect(svc.loaded()).toBe(false);
    client.select('c1');
    TestBed.flushEffects?.();
    flush(['gl.read']);
    expect(svc.loaded()).toBe(true);
  });

  it('loaded becomes true with no client (resolves empty)', () => {
    TestBed.flushEffects?.();
    expect(svc.loaded()).toBe(true);
    expect(svc.capabilities().size).toBe(0);
  });
```

(Use the same flush mechanism the existing spec uses — `TestBed.flushEffects?.()`.)

- [ ] **Step 3: Run — verify service tests fail then pass**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: the two new service tests drive the change; make them pass. Existing capability.service tests stay green.

- [ ] **Step 4: Create `can.directive.ts`**:

```ts
import { Directive, TemplateRef, ViewContainerRef, effect, inject, input } from '@angular/core';
import { CapabilityService } from './capability.service';

/**
 * Structural directive that renders its content only if the acting user holds the given capability
 * (or ANY of an array). Reactive to CapabilityService — the control appears/disappears as
 * capabilities resolve or the acting identity switches.
 *   <button *appCan="'ar.write'" ...>New invoice</button>
 *   <button *appCan="['gl.approve','gl.reverse']" ...>Approve</button>
 */
@Directive({ selector: '[appCan]', standalone: true })
export class CanDirective {
  private readonly tpl = inject(TemplateRef<unknown>);
  private readonly vcr = inject(ViewContainerRef);
  private readonly caps = inject(CapabilityService);

  readonly appCan = input.required<string | string[]>();

  private rendered = false;

  constructor() {
    effect(() => {
      const req = this.appCan();
      const ok = Array.isArray(req) ? req.some((c) => this.caps.has(c)) : this.caps.has(req);
      if (ok && !this.rendered) {
        this.vcr.createEmbeddedView(this.tpl);
        this.rendered = true;
      } else if (!ok && this.rendered) {
        this.vcr.clear();
        this.rendered = false;
      }
    });
  }
}
```

- [ ] **Step 5: Create `can.guard.ts`**:

```ts
import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { Observable, filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/**
 * Route guard for write screens: waits until capabilities are loaded, then allows if the user holds
 * `capability`, else redirects to `fallback` (the area's list). The UI layer of defense; the backend
 * is the real gate.
 */
export function canWrite(capability: string, fallback: string): CanActivateFn {
  return (): Observable<boolean | UrlTree> => {
    const caps = inject(CapabilityService);
    const router = inject(Router);
    return toObservable(caps.loaded).pipe(
      filter((loaded) => loaded),
      take(1),
      map(() => (caps.has(capability) ? true : router.parseUrl(fallback))),
    );
  };
}
```

- [ ] **Step 6: Create `capability.testing.ts`**:

```ts
import { Provider, Signal, signal } from '@angular/core';
import { CapabilityService } from './capability.service';

/** Test double for CapabilityService with a fixed, mutable capability set. */
export class StubCapabilityService {
  private readonly _caps = signal<ReadonlySet<string>>(new Set());
  readonly loaded: Signal<boolean> = signal(true);
  readonly capabilities: Signal<ReadonlySet<string>> = this._caps.asReadonly();
  readonly roles: Signal<string[]> = signal([]);
  readonly deploymentAdmin: Signal<boolean> = signal(false);

  set(caps: string[]): void { this._caps.set(new Set(caps)); }
  has(capability: string): boolean { return this._caps().has(capability); }
  hasArea(area: string): boolean {
    const prefix = area + '.';
    for (const c of this._caps()) if (c.startsWith(prefix)) return true;
    return false;
  }
}

/** Provider granting the given capabilities to components under test (no HttpClient needed). */
export function provideCapabilities(...caps: string[]): Provider {
  const stub = new StubCapabilityService();
  stub.set(caps);
  return { provide: CapabilityService, useValue: stub };
}
```

- [ ] **Step 7: Write `can.directive.spec.ts` and `can.guard.spec.ts`**:

```ts
// can.directive.spec.ts
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CanDirective } from './can.directive';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

@Component({
  standalone: true,
  imports: [CanDirective],
  template: `<button *appCan="'ar.write'">New</button>`,
})
class Host {}

describe('CanDirective', () => {
  function make(caps: string[]) {
    const stub = new StubCapabilityService();
    stub.set(caps);
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [{ provide: CapabilityService, useValue: stub }],
    });
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    return { f, stub };
  }

  it('renders the control when the capability is held', () => {
    const { f } = make(['ar.write']);
    expect((f.nativeElement as HTMLElement).querySelector('button')).not.toBeNull();
  });

  it('removes the control when the capability is absent', () => {
    const { f } = make(['ar.read']);
    expect((f.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });

  it('reacts to a capability change', () => {
    const { f, stub } = make(['ar.read']);
    stub.set(['ar.write']);
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).querySelector('button')).not.toBeNull();
  });
});
```

```ts
// can.guard.spec.ts
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { runInInjectionContext, EnvironmentInjector } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { canWrite } from './can.guard';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

describe('canWrite', () => {
  async function run(caps: string[]) {
    const stub = new StubCapabilityService();
    stub.set(caps);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    });
    const injector = TestBed.inject(EnvironmentInjector);
    const guard = canWrite('ar.write', '/receivables/invoices');
    return runInInjectionContext(injector, () =>
      firstValueFrom(guard({} as any, {} as any) as any));
  }

  it('allows when the capability is held', async () => {
    expect(await run(['ar.write'])).toBe(true);
  });

  it('redirects to the fallback when the capability is absent', async () => {
    const result = await run(['ar.read']);
    expect(result).toBeInstanceOf(UrlTree);
    expect((result as UrlTree).toString()).toBe('/receivables/invoices');
  });
});
```

- [ ] **Step 8: Run — full suite green**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS (directive, guard, service loaded tests; full suite green).

- [ ] **Step 9: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/can.directive.ts UI/Angular/src/app/core/capabilities/can.guard.ts UI/Angular/src/app/core/capabilities/capability.testing.ts UI/Angular/src/app/core/capabilities/capability.service.ts UI/Angular/src/app/core/capabilities/capability.service.spec.ts UI/Angular/src/app/core/capabilities/can.directive.spec.ts UI/Angular/src/app/core/capabilities/can.guard.spec.ts
git commit -m "feat(nav): *appCan directive + canWrite route guard + CapabilityService.loaded + test helper"
```

---

### Task 2: Receivables write-gating

**Files:** all under `UI/Angular/src/app/features/receivables/` + `app.routes.ts` + specs.

**Cap:** `ar.write` for every receivables write control and write route.

- [ ] **Step 1: Gate controls.** Import `CanDirective` into each component's `imports` and wrap each write control with `*appCan="'ar.write'"`:
  - `invoice-list.ts:60-66` "New invoice" link
  - `invoice-detail.ts:78` Edit link, `:79` Delete, `:80` Issue, `:87` Void, `:102-103` per-payment Void
  - `invoice-editor.ts:153` Save
  - `payment-list.ts:22-28` "Record payment" link
  - `payment-editor.ts:85` Save
  - `credit-list.ts:22-28` "Record adjustment" link, `:58` Void
  - `adjustment-editor.ts:106` Save (single button covers all three credit verbs — one `ar.write` wrapper)
  - `refund-list.ts:22-28` "Issue refund" link, `:56` Void
  - `refund-editor.ts:47` Save
  - `customer-list.ts:21` Add
  For a control that already has an `@if`/`@case` wrapper, place `*appCan` on the control element itself (a structural directive is fine alongside an outer `@if` block). For `[class.pointer-events-none]` links, keep the existing class binding and add `*appCan` structurally.

- [ ] **Step 2: Guard routes** in `app.routes.ts` (receivables children): add `canActivate: [canWrite('ar.write', '<fallback>')]` to `invoices/new`, `invoices/:id/edit` (fallback `/receivables/invoices`), `payments/new` (`/receivables/payments`), `credits/new` (`/receivables/credits`), `refunds/new` (`/receivables/refunds`). Import `canWrite` from `./core/capabilities/can.guard`.

- [ ] **Step 3: Fix specs.** Add `provideCapabilities('ar.write')` (from `../../core/capabilities/capability.testing`) to the `providers` of every receivables component spec that constructs a component (all of them, so the injected `CapabilityService` resolves). `credit-list.spec.ts` and `refund-list.spec.ts` MUST grant `ar.write` so their Void-button DOM clicks still find the button.

- [ ] **Step 4: Add a hidden-without-capability test** — in `invoice-list.spec.ts`, add a case with `provideCapabilities()` (no caps) asserting the "New invoice" link is absent:

```ts
  it('hides "New invoice" without ar.write', async () => {
    // configure with provideCapabilities() (empty) ...
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('New invoice');
  });
```

(Mirror the existing spec's setup; just swap the capability provider.)

- [ ] **Step 5: Run — full suite green**

Run: `cd UI/Angular && npx ng test --watch=false`

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables UI/Angular/src/app/app.routes.ts
git commit -m "feat(nav): gate receivables write controls + routes on ar.write"
```

---

### Task 3: Payables write-gating

**Files:** `UI/Angular/src/app/features/payables/` + `app.routes.ts` + specs. **Cap:** `ap.write`.

- [ ] **Step 1: Gate controls** (import `CanDirective`, wrap with `*appCan="'ap.write'"`):
  - `bill-list.ts:34-35` "New bill"
  - `bill-detail.ts:63` Edit, `:64` Delete, `:65` Enter, `:72` Void, `:86-87` per-payment Void
  - `bill-editor.ts:101` Save, `:104` Discard
  - `bill-payment-list.ts:22-28` "Record payment"
  - `bill-payment-editor.ts:85` Save
  - `vendor-credit-list.ts:25-31` "Apply credit"
  - `vendor-credit-apply-editor.ts:75` Save
  - `vendor-list.ts:21` Add

- [ ] **Step 2: Guard routes** in `app.routes.ts` (payables): `bills/new`, `bills/:id/edit` (fallback `/payables/bills`), `payments/new` (`/payables/payments`), `credits/new` (`/payables/credits`) → `canWrite('ap.write', ...)`.

- [ ] **Step 3: Fix specs** — add `provideCapabilities('ap.write')` to every payables component spec.

- [ ] **Step 4: Add a hidden-without-capability test** in `bill-list.spec.ts` (no caps → no "New bill").

- [ ] **Step 5: Run — full suite green.** `cd UI/Angular && npx ng test --watch=false`

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/payables UI/Angular/src/app/app.routes.ts
git commit -m "feat(nav): gate payables write controls + routes on ap.write"
```

---

### Task 4: Payroll write-gating

**Files:** `UI/Angular/src/app/features/payroll/` + `app.routes.ts` + specs. **Cap:** `payroll.write`.

- [ ] **Step 1: Gate controls** (`*appCan="'payroll.write'"`):
  - `run-list.ts:21` "Record payroll run"
  - `run-editor.ts:60` Save
  - `run-detail.ts:48` Void
  - `remittance-list.ts:21` "Record remittance"
  - `remittance-editor.ts:47` Save
  - `remittance-detail.ts:45` Void

- [ ] **Step 2: Guard routes**: `runs/new` (fallback `/payroll/runs`), `remittances/new` (`/payroll/remittances`) → `canWrite('payroll.write', ...)`.

- [ ] **Step 3: Fix specs** — add `provideCapabilities('payroll.write')` to every payroll component spec.

- [ ] **Step 4: Add a hidden-without-capability test** in `run-list.spec.ts` (no caps → no "Record payroll run").

- [ ] **Step 5: Run — full suite green.**

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/payroll UI/Angular/src/app/app.routes.ts
git commit -m "feat(nav): gate payroll write controls + routes on payroll.write"
```

---

### Task 5: GL write-gating (Journal per-verb + Chart of Accounts)

**Files:** `UI/Angular/src/app/features/journal/`, `UI/Angular/src/app/features/accounts/` + `app.routes.ts` + specs.

- [ ] **Step 1: Gate journal controls** (import `CanDirective`):
  - `entry-list.ts:37` "New entry" link → `*appCan="'gl.post'"`
  - `entry-form.ts:100` Post button → `*appCan="'gl.post'"` (do NOT gate `:99` Validate)
  - `entry-detail.ts:63` Approve → `*appCan="'gl.approve'"`; `:65` Void → `*appCan="'gl.void'"` (wrap EACH separately, not the shared block)

- [ ] **Step 2: Gate accounts controls**:
  - `chart-of-accounts.ts:21` "New account" → `*appCan="'gl.manageAccounts'"`
  - `chart-of-accounts.ts:63` per-row Edit link → `*appCan="'gl.manageAccounts'"`
  - `account-editor.ts:81` Save → `*appCan="'gl.manageAccounts'"`
  - Drag-drop: inject `CapabilityService` into `chart-of-accounts.ts`; bind `[cdkDragDisabled]="!caps.has('gl.manageAccounts')"` on the draggable rows (`:29-53` area), and early-return at the top of `onDrop()` (`:99-114`): `if (!this.caps.has('gl.manageAccounts')) return;`

- [ ] **Step 3: Guard routes** in `app.routes.ts`: `journal/new` → `canWrite('gl.post', '/journal')`; `accounts/new`, `accounts/:id/edit` → `canWrite('gl.manageAccounts', '/accounts')`.

- [ ] **Step 4: Fix specs** — add `provideCapabilities(...)` to journal + accounts component specs. Grant the specific caps each spec needs: entry-form spec → `gl.post`; entry-detail spec → `gl.approve','gl.void`; chart-of-accounts + account-editor specs → `gl.manageAccounts`. (chart-of-accounts.spec calls `onDrop` directly — grant `gl.manageAccounts` so the early-return doesn't no-op the existing assertions.)

- [ ] **Step 5: Add hidden-without-capability tests** — entry-list.spec (no caps → no "New entry"); entry-detail.spec (grant only `gl.approve` → Approve present, Void absent) to prove the independent wrappers.

- [ ] **Step 6: Run — full suite green.** `cd UI/Angular && npx ng test --watch=false`

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/features/journal UI/Angular/src/app/features/accounts UI/Angular/src/app/app.routes.ts
git commit -m "feat(nav): gate journal (per-verb) + chart-of-accounts write controls + routes"
```

## Self-Review

- **Spec coverage:** Task 1 = directive + guard + loaded + helper; Tasks 2-5 = the five built areas' controls + routes + specs, per the inventory. Journal per-verb (post/approve/void) and COA drag-drop both covered. Validate ungated; reverse/revise absent.
- **Type consistency:** `CanDirective`/`appCan`/`canWrite`/`loaded`/`provideCapabilities`/`StubCapabilityService` names consistent across tasks.
- **Green-build ordering:** Task 1 additive (new files + backward-compatible service change — `capabilities`/`has`/`hasArea` keep working). Tasks 2-5 each import the directive/guard from Task 1 and end green. Each area edits its own slice of `app.routes.ts` sequentially.
- **Placeholder scan:** none — Task 1 fully coded; Tasks 2-5 give exact file:line + the exact directive/guard to apply.
- **Spec-DI note:** every touched component spec must add `provideCapabilities(...)` or the injected `CapabilityService` (real, needs HttpClient) fails to construct — called out in each task.

## Execution Handoff

Subagent-driven. Five tasks, sequential (2-5 depend on Task 1). After Task 5: controller smoke test — switch to Dev Auditor (no write controls anywhere; direct-URL editor redirects to list) and Dev AR Clerk (Receivables writable), then the merge decision.
