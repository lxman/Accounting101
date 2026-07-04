# Access Control AC-4: Frontend Liveness — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bounce a user off a page the instant their access is pulled — closing the last gap where an idle user keeps seeing a screen they no longer have the capability for.

**Architecture:** Under live-binding the backend is already instant (a revoked user's next request 403s at the chokepoint). AC-4 closes the *client-idle* gap with three transport-agnostic pieces: (1) a `CapabilityService.reload()` manual refetch trigger; (2) a **403 self-heal** HTTP interceptor that calls `reload()` on any forbidden response so the next action reflects reality; (3) a **live route sentinel** — a root effect that redirects off the current page the moment its required capability disappears from the caps signal — plus a **gentle ~15s idle poll** (paused when the tab is hidden) as a push-ready placeholder. Route capability metadata moves into route `data` so the existing `canWrite` guard and the new sentinel share one source of truth.

**Tech Stack:** Angular 22 (standalone, zoneless, signals), RxJS, `@angular/core/rxjs-interop` (`toObservable`/`toSignal`), functional HTTP interceptors (`withInterceptors`), `ng test` (Vitest-backed), `HttpTestingController`, `RouterTestingHarness`, Vitest fake timers.

## Global Constraints

- **Reuse the established idioms exactly:**
  - Functional interceptors only — `HttpInterceptorFn` added to `provideHttpClient(withInterceptors([...]))` in `app.config.ts`; mirror `core/api/auth.interceptor.ts`.
  - Signal↔stream bridging via `toObservable`/`toSignal` + `switchMap`; the NavigationEnd→signal idiom from `layout/nav-state.service.ts:33-39`; root-level `effect()` wired in a service constructor as in `nav-state.service.ts:62-73`.
  - Specs: `provideHttpClient()` + `provideHttpClientTesting()` + `HttpTestingController` with `afterEach(ctrl.verify())`; capability gating via `provideCapabilities(...)` / `StubCapabilityService` (`core/capabilities/capability.testing.ts`); the local `tick()` = `TestBed.flushEffects?.()` helper (`capability.service.spec.ts:28-30`) to force zoneless effects; `provideZonelessChangeDetection()`.
- **The capabilities fetch contract is fixed:** GET `${environment.apiBaseUrl}/clients/${clientId}/me/capabilities`, `catchError(() => of(EMPTY_CAPABILITIES))` (a 403 ⇒ empty set). `reload()` and the poll must reuse this exact URL and fallback — no new endpoint, no divergent error handling.
- **The 403 self-heal interceptor MUST skip the `/me/capabilities` request itself** — otherwise a forbidden capabilities fetch would call `reload()` → refetch → 403 → loop. Guard on `req.url.includes('/me/capabilities')`.
- **The sentinel is targeted, not blanket.** It redirects ONLY when the *active* route's declared `requiredCapability` is absent (and caps are loaded). It must never re-navigate or reset a page whose capability the user still holds — a poll returning unchanged caps must not disturb the current screen or an open form.
- **No websockets in Phase 1.** The idle poll is the push-ready placeholder; structure it so a future SignalR push could replace the timer without touching the interceptor or sentinel.
- **Poll gating:** only while a client is selected (`ClientContextService.clientId() !== null`, mirroring `CapabilityService.key`) AND the tab is visible (`document.visibilityState === 'visible'`); ~15s cadence.
- **Dev-harness caveat (for manual testing):** a hard page reload re-runs bootstrap and resets identity to `DEV_IDENTITIES[0]` and client to `environment.devClientId` — so manual liveness checks must use the "Acting as" switcher WITHOUT reloading. The sentinel/poll are unit-tested; do not rely on reload to observe them.
- **Commits:** stage explicit paths only — never `git add -A`/`.`. `UI/Angular/src/app/core/api/environment.ts` (devClientId) must NEVER be committed; IDE `.csproj`/`.slnx` churn stays UNCOMMITTED. Trailer required:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Test runner:** `cd UI/Angular && npx ng test --watch=false --include='**/<spec>.spec.ts'` (multiple `--include` allowed; confirmed working). Run target specs red-then-green, then the FULL UI suite once per task.

---

## File Structure

**Create:**
- `UI/Angular/src/app/core/capabilities/self-heal.interceptor.ts` — 403 → `reload()`.
- `UI/Angular/src/app/core/capabilities/self-heal.interceptor.spec.ts`
- `UI/Angular/src/app/core/capabilities/route-sentinel.service.ts` — reactive redirect off a page whose required cap vanished.
- `UI/Angular/src/app/core/capabilities/route-sentinel.service.spec.ts`
- `UI/Angular/src/app/core/capabilities/capability-poll.service.ts` — idle poll → `reload()`.
- `UI/Angular/src/app/core/capabilities/capability-poll.service.spec.ts`

**Modify:**
- `UI/Angular/src/app/core/capabilities/capability.service.ts` — add `reload()` + merge its trigger into the fetch.
- `UI/Angular/src/app/core/capabilities/capability.service.spec.ts` — cover `reload()`.
- `UI/Angular/src/app/core/capabilities/can.guard.ts` — `canWrite` reads `requiredCapability`/`fallback` from route `data` (single source).
- `UI/Angular/src/app/core/capabilities/can.guard.spec.ts` — update to data-driven form (create if absent).
- `UI/Angular/src/app/app.routes.ts` — move each `canWrite('cap','fallback')` into `canActivate: [canWrite], data: { requiredCapability, fallback }`.
- `UI/Angular/src/app/app.config.ts` — register the self-heal interceptor; instantiate the sentinel + poll at bootstrap.
- `UI/Angular/src/app/app.ts` — pull the sentinel + poll services in (so their root effects/subscriptions start).

---

### Task 1: `CapabilityService.reload()` — manual refetch trigger

**Files:**
- Modify: `UI/Angular/src/app/core/capabilities/capability.service.ts`
- Test: `UI/Angular/src/app/core/capabilities/capability.service.spec.ts`

**Interfaces:**
- Consumes: existing `key` computed, `toObservable`/`toSignal`/`switchMap` fetch pipeline, `environment.apiBaseUrl`, `EMPTY_CAPABILITIES`.
- Produces: `CapabilityService.reload(): void` — re-fetches `/me/capabilities` for the current client without a key change. The existing key-driven refetch is preserved.

- [ ] **Step 1: Write the failing test**

Append to `UI/Angular/src/app/core/capabilities/capability.service.spec.ts` (inside the existing `describe`, reusing its `tick()` helper and TestBed setup — read the file first to match its providers, which select a client and flush the initial fetch):

```ts
  it('reload() refetches /me/capabilities without a key change', () => {
    // (Assumes the suite's beforeEach selected a client and the initial GET is already flushed;
    //  if not, flush the initial request first exactly as the sibling tests do.)
    const svc = TestBed.inject(CapabilityService);
    tick();
    // First, satisfy the initial fetch triggered by client selection.
    const first = ctrl.expectOne((r) => r.url.endsWith('/me/capabilities'));
    first.flush({ capabilities: ['ar.read'], roles: [], deploymentAdmin: false });
    tick();
    expect(svc.has('ar.read')).toBe(true);

    // Now reload() must trigger a brand-new GET to the same URL.
    svc.reload();
    tick();
    const second = ctrl.expectOne((r) => r.url.endsWith('/me/capabilities'));
    second.flush({ capabilities: ['ar.read', 'ar.write'], roles: [], deploymentAdmin: false });
    tick();
    expect(svc.has('ar.write')).toBe(true);
  });
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability.service.spec.ts'`
Expected: FAIL — `reload` does not exist on `CapabilityService` (compile error), or no second request is issued.

- [ ] **Step 3: Add `reload()` + merge its trigger into the fetch source**

In `capability.service.ts`, add a reload counter signal and fold it into the fetch trigger so bumping it re-emits the current key. Replace the `key`/`response` region (lines 19–34) with:

```ts
  private readonly reloadTick = signal(0);

  private readonly key = computed(() => {
    const clientId = this.client.clientId();
    return clientId ? { clientId, sub: this.identity.active().sub } : null;
  });

  // Re-fetch when the identity/client key changes OR when reload() bumps the tick.
  private readonly fetchTrigger = computed(() => ({ key: this.key(), tick: this.reloadTick() }));

  private readonly response = toSignal<CapabilitiesResponse | typeof LOADING, typeof LOADING>(
    toObservable(this.fetchTrigger).pipe(
      switchMap(({ key }): Observable<CapabilitiesResponse> =>
        key
          ? this.http
              .get<CapabilitiesResponse>(`${environment.apiBaseUrl}/clients/${key.clientId}/me/capabilities`)
              .pipe(catchError(() => of(EMPTY_CAPABILITIES)))
          : of(EMPTY_CAPABILITIES)),
    ) as Observable<CapabilitiesResponse | typeof LOADING>,
    { initialValue: LOADING },
  );
```

Add `signal` to the `@angular/core` import (it currently imports `Injectable, Signal, computed, inject`). Then add the public method (below `hasArea`):

```ts
  /** Force a re-fetch of the current client's capabilities (e.g. after a 403, or on a poll tick). */
  reload(): void { this.reloadTick.update((n) => n + 1); }
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability.service.spec.ts'`
Expected: PASS (existing tests + the new reload test).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/capability.service.ts \
        UI/Angular/src/app/core/capabilities/capability.service.spec.ts
git commit -m "$(cat <<'EOF'
feat(access): CapabilityService.reload() manual refetch trigger (AC-4)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 403 self-heal interceptor

**Files:**
- Create: `UI/Angular/src/app/core/capabilities/self-heal.interceptor.ts`, `self-heal.interceptor.spec.ts`
- Modify: `UI/Angular/src/app/app.config.ts`

**Interfaces:**
- Consumes: `CapabilityService.reload()` (Task 1); `HttpInterceptorFn`, `inject`, `HttpErrorResponse`; the `withInterceptors([...])` registration in `app.config.ts`.
- Produces: `capabilitySelfHealInterceptor: HttpInterceptorFn` — on a `403` from any request except `/me/capabilities`, calls `CapabilityService.reload()`, then rethrows.

- [ ] **Step 1: Write the failing test**

Create `UI/Angular/src/app/core/capabilities/self-heal.interceptor.spec.ts` (mirror `core/api/auth.interceptor.spec.ts`):

```ts
import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { capabilitySelfHealInterceptor } from './self-heal.interceptor';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

describe('capabilitySelfHealInterceptor', () => {
  let http: HttpClient;
  let ctrl: HttpTestingController;
  let caps: StubCapabilityService;

  beforeEach(() => {
    caps = new StubCapabilityService();
    vi.spyOn(caps, 'reload');
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([capabilitySelfHealInterceptor])),
        provideHttpClientTesting(),
        { provide: CapabilityService, useValue: caps },
      ],
    });
    http = TestBed.inject(HttpClient);
    ctrl = TestBed.inject(HttpTestingController);
  });
  afterEach(() => ctrl.verify());

  it('reloads capabilities on a 403 from a normal request', () => {
    http.get('/clients/c1/entries').subscribe({ next: () => {}, error: () => {} });
    ctrl.expectOne('/clients/c1/entries').flush('nope', { status: 403, statusText: 'Forbidden' });
    expect(caps.reload).toHaveBeenCalledTimes(1);
  });

  it('does NOT reload on a 403 from the capabilities fetch itself (no loop)', () => {
    http.get('/clients/c1/me/capabilities').subscribe({ next: () => {}, error: () => {} });
    ctrl.expectOne('/clients/c1/me/capabilities').flush('nope', { status: 403, statusText: 'Forbidden' });
    expect(caps.reload).not.toHaveBeenCalled();
  });

  it('does not reload on a non-403 error', () => {
    http.get('/clients/c1/entries').subscribe({ next: () => {}, error: () => {} });
    ctrl.expectOne('/clients/c1/entries').flush('boom', { status: 500, statusText: 'Server Error' });
    expect(caps.reload).not.toHaveBeenCalled();
  });
});
```

Note: if `StubCapabilityService` has no `reload` method yet, this test's `vi.spyOn(caps, 'reload')` will fail — Task 2 Step 3 adds `reload` to the stub.

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/self-heal.interceptor.spec.ts'`
Expected: FAIL — `capabilitySelfHealInterceptor` does not exist.

- [ ] **Step 3: Write the interceptor + add `reload` to the test stub**

Create `UI/Angular/src/app/core/capabilities/self-heal.interceptor.ts`:

```ts
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { CapabilityService } from './capability.service';

/** On any 403 (except the capabilities fetch itself), refetch the caller's capabilities so the next
 * action reflects the revocation instantly. Rethrows the error untouched. */
export const capabilitySelfHealInterceptor: HttpInterceptorFn = (req, next) => {
  const caps = inject(CapabilityService);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 403 && !req.url.includes('/me/capabilities')) {
        caps.reload();
      }
      return throwError(() => err);
    }),
  );
};
```

In `UI/Angular/src/app/core/capabilities/capability.testing.ts`, add a no-op `reload` to `StubCapabilityService` (read the file first; add the method alongside its existing `set`/`setLoaded`/`setDeploymentAdmin` knobs):

```ts
  reload(): void { /* no-op stub; spied on in interceptor/poll tests */ }
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/self-heal.interceptor.spec.ts'`
Expected: PASS (3 tests).

- [ ] **Step 5: Register the interceptor**

In `UI/Angular/src/app/app.config.ts`, add the import and include it in the interceptor array (order after `authInterceptor` so the auth header is attached before the response is evaluated):

```ts
import { capabilitySelfHealInterceptor } from './core/capabilities/self-heal.interceptor';
```
```ts
    provideHttpClient(withInterceptors([authInterceptor, capabilitySelfHealInterceptor])),
```

- [ ] **Step 6: Run the full UI suite**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — no regressions.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/self-heal.interceptor.ts \
        UI/Angular/src/app/core/capabilities/self-heal.interceptor.spec.ts \
        UI/Angular/src/app/core/capabilities/capability.testing.ts \
        UI/Angular/src/app/app.config.ts
git commit -m "$(cat <<'EOF'
feat(access): 403 self-heal interceptor refetches capabilities (AC-4)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Route capability metadata (single source) — `canWrite` reads route `data`

**Files:**
- Modify: `UI/Angular/src/app/core/capabilities/can.guard.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`
- Test: `UI/Angular/src/app/core/capabilities/can.guard.spec.ts` (create if absent)

**Interfaces:**
- Consumes: `CapabilityService.loaded`/`has`; `ActivatedRouteSnapshot.data`, `Router`, `UrlTree`.
- Produces: `canWrite: CanActivateFn` (parameterless) that reads `route.data['requiredCapability']` (string) and `route.data['fallback']` (string). Every write-guarded route now carries `data: { requiredCapability, fallback }` — the SAME metadata the route sentinel (Task 4) consumes.

- [ ] **Step 1: Write the failing guard test**

Create/replace `UI/Angular/src/app/core/capabilities/can.guard.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideRouter, Router } from '@angular/router';
import { Component } from '@angular/core';
import { provideZonelessChangeDetection } from '@angular/core';
import { canWrite } from './can.guard';
import { provideCapabilities } from './capability.testing';

@Component({ standalone: true, template: 'editor' }) class Editor {}
@Component({ standalone: true, template: 'list' }) class List {}

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideCapabilities(...caps),
      provideRouter([
        { path: 'list', component: List },
        { path: 'edit', component: Editor, canActivate: [canWrite],
          data: { requiredCapability: 'ar.write', fallback: '/list' } },
      ]),
    ],
  });
}

describe('canWrite (data-driven)', () => {
  it('allows navigation when the caller holds the required capability', async () => {
    setup(['ar.write']);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/edit');
    expect(TestBed.inject(Router).url).toBe('/edit');
  });

  it('redirects to the route data fallback when the capability is missing', async () => {
    setup(['ar.read']);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/edit');
    expect(TestBed.inject(Router).url).toBe('/list');
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/can.guard.spec.ts'`
Expected: FAIL — `canWrite` is still a factory taking `(capability, fallback)`, so `canActivate: [canWrite]` (bare reference) doesn't compile/behave.

- [ ] **Step 3: Rewrite `canWrite` to read route data**

Replace `UI/Angular/src/app/core/capabilities/can.guard.ts` with:

```ts
import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivateFn, Router, UrlTree } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { Observable, filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/** Guards a write route. The required capability and the fallback route live in the route's `data`
 * (`requiredCapability`, `fallback`) — the single source shared with the live route sentinel. */
export const canWrite: CanActivateFn = (route: ActivatedRouteSnapshot): Observable<boolean | UrlTree> => {
  const caps = inject(CapabilityService);
  const router = inject(Router);
  const capability = route.data['requiredCapability'] as string;
  const fallback = route.data['fallback'] as string;
  return toObservable(caps.loaded).pipe(
    filter((loaded) => loaded),
    take(1),
    map(() => (caps.has(capability) ? true : router.parseUrl(fallback))),
  );
};
```

- [ ] **Step 4: Move cap/fallback into `data` on every write route**

In `UI/Angular/src/app/app.routes.ts`, convert **each** `canActivate: [canWrite('CAP', 'FALLBACK')]` to `canActivate: [canWrite], data: { requiredCapability: 'CAP', fallback: 'FALLBACK' }`, preserving the exact `CAP`/`FALLBACK` strings. Read the whole file and update all such routes (the three known examples — keep every one in the file):

```ts
// before: { path: 'new', component: EntryForm, canActivate: [canWrite('gl.post', '/journal')] },
{ path: 'new', component: EntryForm, canActivate: [canWrite], data: { requiredCapability: 'gl.post', fallback: '/journal' } },
// before: { path: 'invoices/new', component: InvoiceEditor, canActivate: [canWrite('ar.write', '/receivables/invoices')] },
{ path: 'invoices/new', component: InvoiceEditor, canActivate: [canWrite], data: { requiredCapability: 'ar.write', fallback: '/receivables/invoices' } },
// before: { path: 'bills/new', component: BillEditor, canActivate: [canWrite('ap.write', '/payables/bills')] },
{ path: 'bills/new', component: BillEditor, canActivate: [canWrite], data: { requiredCapability: 'ap.write', fallback: '/payables/bills' } },
```

Leave `deploymentAdminGuard(...)` routes unchanged (that guard is separate and takes a fallback arg; it is out of scope for the capability sentinel).

- [ ] **Step 5: Run the guard test + full suite**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/can.guard.spec.ts'` → PASS.
Then: `cd UI/Angular && npx ng test --watch=false` → PASS. Existing route-guard behavior is unchanged; any spec that navigated through a `canWrite` route still passes because the guard now reads the same cap/fallback from `data`.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/can.guard.ts \
        UI/Angular/src/app/core/capabilities/can.guard.spec.ts \
        UI/Angular/src/app/app.routes.ts
git commit -m "$(cat <<'EOF'
refactor(access): canWrite reads requiredCapability/fallback from route data (AC-4)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Live route sentinel

**Files:**
- Create: `UI/Angular/src/app/core/capabilities/route-sentinel.service.ts`, `route-sentinel.service.spec.ts`
- Modify: `UI/Angular/src/app/app.ts` (instantiate the service so its effect runs)

**Interfaces:**
- Consumes: `Router` (events + `routerState.snapshot` + `navigateByUrl`), `CapabilityService.loaded`/`has`/`capabilities`, the route `data.requiredCapability`/`data.fallback` from Task 3, the NavigationEnd→signal idiom (`nav-state.service.ts:33-39`), constructor `effect()` idiom (`nav-state.service.ts:62-73`).
- Produces: `RouteSentinelService` (`providedIn: 'root'`) — a root effect that redirects to `data.fallback` (or `/dashboard`) the moment the active route's `requiredCapability` is absent from a loaded caps signal.

- [ ] **Step 1: Write the failing test**

Create `UI/Angular/src/app/core/capabilities/route-sentinel.service.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideRouter, Router } from '@angular/router';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { RouteSentinelService } from './route-sentinel.service';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

@Component({ standalone: true, template: 'editor' }) class Editor {}
@Component({ standalone: true, template: 'list' }) class List {}

function setup() {
  const caps = new StubCapabilityService();
  caps.setLoaded(true);
  caps.set('ar.write');
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: CapabilityService, useValue: caps },
      provideRouter([
        { path: 'list', component: List },
        { path: 'edit', component: Editor, data: { requiredCapability: 'ar.write', fallback: '/list' } },
      ]),
    ],
  });
  return caps;
}

describe('RouteSentinelService', () => {
  it('stays put while the required capability is held', async () => {
    const caps = setup();
    TestBed.inject(RouteSentinelService);              // start the sentinel
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/edit');
    TestBed.flushEffects?.();
    expect(TestBed.inject(Router).url).toBe('/edit');
  });

  it('redirects off the page the moment the required capability disappears', async () => {
    const caps = setup();
    TestBed.inject(RouteSentinelService);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/edit');
    TestBed.flushEffects?.();
    expect(TestBed.inject(Router).url).toBe('/edit');

    caps.set();                                        // capability revoked (empty set)
    TestBed.flushEffects?.();
    await TestBed.inject(Router).events.toPromise?.();  // let the redirect navigation settle
    // Poll the URL until it flips (navigation is async):
    for (let i = 0; i < 10 && TestBed.inject(Router).url !== '/list'; i++) { await Promise.resolve(); TestBed.flushEffects?.(); }
    expect(TestBed.inject(Router).url).toBe('/list');
  });
});
```

Note: if `StubCapabilityService.set(...)` doesn't accept a varargs capability list / there's no `setLoaded`, adjust to whatever knobs the stub exposes (read `capability.testing.ts`); the intent is "loaded, holds ar.write" then "loaded, holds nothing."

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/route-sentinel.service.spec.ts'`
Expected: FAIL — `RouteSentinelService` does not exist.

- [ ] **Step 3: Write the sentinel**

Create `UI/Angular/src/app/core/capabilities/route-sentinel.service.ts`:

```ts
import { Injectable, Signal, effect, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';
import { CapabilityService } from './capability.service';

interface RouteCap { requiredCapability?: string; fallback?: string; }

/** Redirects the user off the current page the instant its required capability leaves the caps
 * signal — the reactive complement to the navigation-time canWrite guard. Root-level; started by
 * being injected at bootstrap (see app.ts). */
@Injectable({ providedIn: 'root' })
export class RouteSentinelService {
  private readonly router = inject(Router);
  private readonly caps = inject(CapabilityService);

  // The deepest active route's capability metadata, refreshed on every completed navigation.
  private readonly activeCap: Signal<RouteCap> = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map(() => {
        let r = this.router.routerState.snapshot.root;
        while (r.firstChild) r = r.firstChild;
        return r.data as RouteCap;
      }),
    ),
    { initialValue: {} as RouteCap },
  );

  constructor() {
    effect(() => {
      const { requiredCapability, fallback } = this.activeCap();
      if (!requiredCapability || !this.caps.loaded()) return;
      if (!this.caps.has(requiredCapability)) {
        void this.router.navigateByUrl(fallback ?? '/dashboard');
      }
    });
  }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/route-sentinel.service.spec.ts'`
Expected: PASS (2 tests).

- [ ] **Step 5: Start the sentinel at bootstrap**

In `UI/Angular/src/app/app.ts`, inject the sentinel in the root component's constructor so its effect is created once at app start (mirroring how `App` already injects `ClientContextService`):

```ts
import { RouteSentinelService } from './core/capabilities/route-sentinel.service';
```
```ts
  constructor() {
    const c = inject(ClientContextService);
    if (environment.devClientId) c.select(environment.devClientId);
    inject(RouteSentinelService);   // start the live route sentinel
  }
```

- [ ] **Step 6: Run the full UI suite**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — no regressions.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/route-sentinel.service.ts \
        UI/Angular/src/app/core/capabilities/route-sentinel.service.spec.ts \
        UI/Angular/src/app/app.ts
git commit -m "$(cat <<'EOF'
feat(access): live route sentinel redirects off a page when its cap vanishes (AC-4)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Gentle idle poll

**Files:**
- Create: `UI/Angular/src/app/core/capabilities/capability-poll.service.ts`, `capability-poll.service.spec.ts`
- Modify: `UI/Angular/src/app/app.ts` (instantiate the poll service)

**Interfaces:**
- Consumes: `CapabilityService.reload()` (Task 1), `ClientContextService.clientId()`, `document.visibilityState`, RxJS `interval`.
- Produces: `CapabilityPollService` (`providedIn: 'root'`) — every `POLL_INTERVAL_MS` (15000), if a client is selected and the tab is visible, calls `CapabilityService.reload()`. A push (SignalR) could later replace the timer without touching consumers.

- [ ] **Step 1: Write the failing test**

Create `UI/Angular/src/app/core/capabilities/capability-poll.service.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CapabilityPollService, POLL_INTERVAL_MS } from './capability-poll.service';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';
import { ClientContextService } from '../client/client-context.service';

describe('CapabilityPollService', () => {
  let caps: StubCapabilityService;
  let client: ClientContextService;

  beforeEach(() => {
    vi.useFakeTimers();
    caps = new StubCapabilityService();
    vi.spyOn(caps, 'reload');
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: CapabilityService, useValue: caps },
        ClientContextService,
      ],
    });
    client = TestBed.inject(ClientContextService);
  });
  afterEach(() => { vi.useRealTimers(); });

  it('reloads on each interval while a client is selected and the tab is visible', () => {
    client.select('c1');
    TestBed.inject(CapabilityPollService);      // starts the interval subscription
    vi.advanceTimersByTime(POLL_INTERVAL_MS * 2);
    expect(caps.reload).toHaveBeenCalledTimes(2);
  });

  it('does not reload while no client is selected', () => {
    client.select(null);
    TestBed.inject(CapabilityPollService);
    vi.advanceTimersByTime(POLL_INTERVAL_MS * 2);
    expect(caps.reload).not.toHaveBeenCalled();
  });

  it('does not reload while the tab is hidden', () => {
    client.select('c1');
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    TestBed.inject(CapabilityPollService);
    vi.advanceTimersByTime(POLL_INTERVAL_MS * 2);
    expect(caps.reload).not.toHaveBeenCalled();
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-poll.service.spec.ts'`
Expected: FAIL — `CapabilityPollService` does not exist.

- [ ] **Step 3: Write the poll service**

Create `UI/Angular/src/app/core/capabilities/capability-poll.service.ts`:

```ts
import { DestroyRef, Injectable, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';
import { CapabilityService } from './capability.service';
import { ClientContextService } from '../client/client-context.service';

/** Poll cadence (ms). A future SignalR push could replace this timer without touching consumers. */
export const POLL_INTERVAL_MS = 15000;

/** Gently re-resolves the current user's capabilities on a timer so an IDLE user (making no
 * requests) is still bounced by the sentinel when their access changes. Skips when no client is
 * selected or the tab is hidden. Started by being injected at bootstrap (see app.ts). */
@Injectable({ providedIn: 'root' })
export class CapabilityPollService {
  private readonly caps = inject(CapabilityService);
  private readonly client = inject(ClientContextService);

  constructor() {
    interval(POLL_INTERVAL_MS)
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => {
        if (this.client.clientId() && document.visibilityState === 'visible') {
          this.caps.reload();
        }
      });
  }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-poll.service.spec.ts'`
Expected: PASS (3 tests).

- [ ] **Step 5: Start the poll at bootstrap**

In `UI/Angular/src/app/app.ts`, inject the poll service in the root constructor (beside the sentinel):

```ts
import { CapabilityPollService } from './core/capabilities/capability-poll.service';
```
```ts
    inject(RouteSentinelService);   // start the live route sentinel
    inject(CapabilityPollService);  // start the idle capability poll
```

- [ ] **Step 6: Run the full UI suite**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — all green, output pristine (exit 0, no unhandled errors).

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/capability-poll.service.ts \
        UI/Angular/src/app/core/capabilities/capability-poll.service.spec.ts \
        UI/Angular/src/app/app.ts
git commit -m "$(cat <<'EOF'
feat(access): gentle idle capability poll (visibility + client gated) (AC-4)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

- [ ] `cd UI/Angular && npx ng test --watch=false` — all green, exit 0, no unhandled `NG04002`/promise-rejection noise.
- [ ] Confirm only intended files staged across the five commits — no `environment.ts`, no `.csproj`/`.slnx`.
- [ ] Manual smoke (optional, dev stack up): act as **Dev Admin**, open an editor route (e.g. `/receivables/invoices/new`); in a second tab as another admin, remove `ar.write` from the set the first user holds; within ~15s (or on the first user's next action) the first user is bounced to `/receivables/invoices`. Use the **switcher, not reload**, to change identities.

---

## Self-Review notes (against design spec section D)

- **"403 self-heal interceptor — on ANY 403, CapabilityService refetches /me/capabilities"** → Task 2. Loop-guarded (skips `/me/capabilities`), rethrows. Built on Task 1's `reload()`. ✓
- **"Live route sentinel — root effect mapping active route → required capability, redirecting off the page the moment that capability disappears"** → Task 4, reading route `data.requiredCapability`/`fallback`. Task 3 makes that `data` the single source shared with the `canWrite` guard, so every write route is covered by both mechanisms automatically (no drift-prone second registry). ✓
- **"Gentle idle poll (~15s), paused when tab hidden, drop-in replaceable by SignalR"** → Task 5, gated on `clientId` + `visibilityState`, cadence in one exported constant, timer isolated in its own service so a push can replace it. ✓
- **"No websockets in Phase 1; structured so a SignalR push can replace the poll"** → the poll is the only timer; interceptor + sentinel are push-agnostic (they react to the caps signal, however it's refreshed). ✓
- **"Dev-harness note: full reload resets identity — sentinel unit-tested; manual checks use the switcher without reload"** → captured in Global Constraints + the manual-smoke step. ✓
- **Type/name consistency:** `CapabilityService.reload()`, `capabilitySelfHealInterceptor`, `canWrite: CanActivateFn` (data-driven), route `data: { requiredCapability, fallback }`, `RouteSentinelService`, `CapabilityPollService` + `POLL_INTERVAL_MS` are used identically across Tasks 1–5 and their specs. ✓
- **New test patterns established (flagged for the implementer):** `RouterTestingHarness` (guard + sentinel), `vi.useFakeTimers()` (poll) — none exist in the repo yet; if the zoneless TestBed needs a tweak to make `RouterTestingHarness`/fake-timers cooperate (e.g. an extra `TestBed.flushEffects()` or awaiting a microtask), adjust the test harness while keeping the assertion intact and report it. The load-bearing assertions (reload called, URL redirected, no-loop) must not be weakened.
- **Deferred (out of scope):** deployment-admin route liveness (the sentinel handles `requiredCapability`/write routes — the Slice C gap; `deploymentAdminGuard` routes are left navigation-time only); SignalR push; per-route poll cadence.
