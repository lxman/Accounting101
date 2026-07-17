# Hide the Approvals Queue Under AutoApprove — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the active client's approval mode is `AutoApprove`, hide the "Approvals" nav leaf (`/journal/approvals`) and redirect that route to `/journal`.

**Architecture:** Carry the client's approval mode on the existing `/me/capabilities` response (the reactive single source the nav already reads via `CapabilityService`), then gate the nav leaf and a route guard on it, and refresh capabilities after a policy save so the change is immediate.

**Tech Stack:** ASP.NET Core minimal APIs + xUnit (backend); Angular (zoneless, standalone, OnPush signals) + Vitest/TestBed (frontend).

**Spec:** `docs/superpowers/specs/2026-07-17-hide-approvals-under-autoapprove-design.md`

## Global Constraints

- Backend `ApprovalMode` serializes as its string name on the wire (global string-enum converter) — e.g. `"approvalMode":"AutoApprove"`.
- `ApprovalPolicy.ModeOf(client)` resolves the effective mode (never `Unspecified`).
- FE `ApprovalMode` type is the union `'TwoPerson' | 'SelfApprove' | 'AutoApprove'` from `core/approval-policy/approval-policy.ts`.
- `EMPTY_CAPABILITIES.approvalMode` defaults to `'TwoPerson'` (a non-AutoApprove default: nothing hides during load).
- Only the `/journal/approvals` leaf carries `hideWhenAutoApprove`. The Approval policy admin screen stays visible.
- Test runner is `npx ng test --include=<path> --watch=false` (NOT `vitest run`); production build gate is `npx ng build --configuration production`.
- TDD: failing test first; commit after each green task. Do NOT push. Do NOT stage the pre-existing uncommitted `UI/Angular/src/app/core/api/environment.ts`.

---

### Task 1: Backend — carry `approvalMode` on `/me/capabilities`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` (the `CapabilitiesResponse` record, ~line 31)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs` (the `GetMyCapabilities` handler)
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs`

**Interfaces:**
- Consumes: `ApprovalPolicy.ModeOf(ClientRegistration)`, `ControlStore.GetClientAsync`/`RegisterClientAsync`, `ApprovalMode` enum (all existing).
- Produces: `CapabilitiesResponse` gains a trailing `ApprovalMode ApprovalMode` positional member; `/me/capabilities` returns `approvalMode` as a string.

- [ ] **Step 1: Write the failing tests**

Add to `CapabilitiesTests.cs` (the file already has `using Accounting101.Ledger.Contracts;`, `using Accounting101.Ledger.Api.Control;`, and `System.Net.Http.Json`):

```csharp
    [Fact]
    public async Task Capabilities_reports_the_clients_approval_mode_default_two_person()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Controller);
        CapabilitiesResponse body = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;
        Assert.Equal(ApprovalMode.TwoPerson, body.ApprovalMode);
    }

    [Fact]
    public async Task Capabilities_reflects_auto_approve_when_the_client_is_set_to_it()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Controller);
        ClientRegistration reg = (await fixture.Control().GetClientAsync(c.ClientId, default))!;
        reg.ApprovalMode = ApprovalMode.AutoApprove;
        await fixture.Control().RegisterClientAsync(reg, default);

        CapabilitiesResponse body = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;
        Assert.Equal(ApprovalMode.AutoApprove, body.ApprovalMode);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~CapabilitiesTests&FullyQualifiedName~approval"`
Expected: FAIL — `CapabilitiesResponse` has no `ApprovalMode` member (compile error).

- [ ] **Step 3: Add the field to the response record**

In `AdminContracts.cs`, change the `CapabilitiesResponse` record (keep the doc comment) to add a trailing positional member:

```csharp
public sealed record CapabilitiesResponse(
    IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin,
    IReadOnlyList<string> EnabledModules, ApprovalMode ApprovalMode);
```

Before implementing, grep for other construction sites so none break:
Run: `grep -rn "new CapabilitiesResponse(" Backend/`
Expected: only the one in `CapabilitiesEndpoints.cs` (updated next). If any other site exists, add the trailing argument there too.

- [ ] **Step 4: Populate it in the handler**

In `CapabilitiesEndpoints.cs`, `GetMyCapabilities` already loads `ClientRegistration? client`. Update the return:

```csharp
        return Results.Ok(new CapabilitiesResponse(
            membership.Capabilities,
            membership.GrantedRoles.Select(r => r.ToString()).ToList(),
            deploymentAdmin,
            client?.EnabledModules ?? [],
            client is null ? ApprovalMode.TwoPerson : ApprovalPolicy.ModeOf(client)));
```

Add `using Accounting101.Ledger.Contracts;` only if not already present (it is — `CapabilitiesResponse` is already referenced). `ApprovalPolicy` is in `Accounting101.Ledger.Api.Control` (already imported).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~CapabilitiesTests"`
Expected: PASS (existing capabilities tests + the two new approval-mode tests).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/AdminContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs
git commit -m "feat(caps): carry approvalMode on /me/capabilities"
```

---

### Task 2: Frontend — surface the mode and gate the nav leaf

**Files:**
- Modify: `UI/Angular/src/app/core/capabilities/capabilities.ts`
- Modify: `UI/Angular/src/app/core/capabilities/capability.service.ts`
- Modify: `UI/Angular/src/app/core/capabilities/capability.testing.ts` (shared test double)
- Modify: `UI/Angular/src/app/layout/nav.ts`
- Modify: `UI/Angular/src/app/layout/shell.ts`
- Test: `UI/Angular/src/app/layout/shell.spec.ts`

**Interfaces:**
- Consumes: `approvalMode` string from `/me/capabilities` (Task 1); `ApprovalMode` type from `core/approval-policy/approval-policy`.
- Produces: `CapabilityService.approvalMode: Signal<ApprovalMode>`; `NavLink.hideWhenAutoApprove?: boolean`; `StubCapabilityService.setApprovalMode(mode)`. Consumed by Tasks 3 and 4.

- [ ] **Step 1: Write the failing shell test**

In `shell.spec.ts`, update the local `StubCaps` class to support a mode, extend `make()` with a `mode` parameter, and add a new test. Replace the `StubCaps` class (lines 8-18) with:

```ts
class StubCaps {
  areas = new Set<string>();
  modules = new Set<string>();
  mode = 'TwoPerson';
  hasArea(a: string) { return this.areas.has(a); }
  has() { return false; }
  capabilities() { return new Set<string>(); }
  roles() { return []; }
  deploymentAdmin() { return false; }
  enabledModules() { return this.modules; }
  moduleEnabled(key: string) { return this.modules.has(key); }
  approvalMode() { return this.mode; }
}
```

Change the `make()` signature and body to thread the mode (add the third parameter and set it on the stub):

```ts
  async function make(
    areas: string[] = ['gl','ar','ap','payroll','cash','bankrec','fixedassets','audit','reports','admin'],
    modules: string[] = ['receivables','payables','payroll','cash','reconciliation','fixedassets','inventory'],
    mode = 'TwoPerson',
  ) {
    TestBed.resetTestingModule();
    const stub = new StubCaps();
    areas.forEach(a => stub.areas.add(a));
    modules.forEach(m => stub.modules.add(m));
    stub.mode = mode;
    await TestBed.configureTestingModule({
      imports: [Shell],
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    }).compileComponents();
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }
```

Add this test (after the "gates subledger links…" test, before the closing `});` of the describe):

```ts
  it('hides the Approvals leaf when the client is on AutoApprove', async () => {
    const shown = await make(['gl'], [], 'TwoPerson');
    sectionHeader(shown.nativeElement, 'General Ledger').click();
    shown.detectChanges();
    expect((shown.nativeElement as HTMLElement).textContent).toContain('Approvals');

    const hidden = await make(['gl'], [], 'AutoApprove');
    sectionHeader(hidden.nativeElement, 'General Ledger').click();
    hidden.detectChanges();
    expect((hidden.nativeElement as HTMLElement).textContent).not.toContain('Approvals');
  });
```

- [ ] **Step 2: Run the shell test to verify it fails**

Run: `cd UI/Angular && npx ng test --include=src/app/layout/shell.spec.ts --watch=false`
Expected: FAIL — the AutoApprove case still shows "Approvals" (predicate not yet added); the other existing shell tests still pass (they now call `stub.approvalMode()` which returns `'TwoPerson'`).

- [ ] **Step 3: Add `approvalMode` to the capabilities model**

In `capabilities.ts`:

```ts
import { ApprovalMode } from '../approval-policy/approval-policy';

export interface CapabilitiesResponse {
  capabilities: string[];
  roles: string[];
  deploymentAdmin: boolean;
  enabledModules: string[];
  approvalMode: ApprovalMode;
}

export const EMPTY_CAPABILITIES: CapabilitiesResponse = {
  capabilities: [], roles: [], deploymentAdmin: false, enabledModules: [], approvalMode: 'TwoPerson',
};
```

- [ ] **Step 4: Expose the signal on `CapabilityService`**

In `capability.service.ts`, add the import and a computed signal alongside the existing ones (after `enabledModules`, ~line 58):

```ts
import { ApprovalMode } from '../approval-policy/approval-policy';
```

```ts
  readonly approvalMode: Signal<ApprovalMode> = computed(() => this.current().approvalMode);
```

- [ ] **Step 5: Extend the shared test double**

In `capability.testing.ts`, add a mutable `approvalMode` to `StubCapabilityService`:

```ts
import { ApprovalMode } from '../approval-policy/approval-policy';
```

Inside the class, add a backing signal, a readonly signal, and a setter (place alongside the others):

```ts
  private readonly _approvalMode = signal<ApprovalMode>('TwoPerson');
  readonly approvalMode: Signal<ApprovalMode> = this._approvalMode.asReadonly();
  setApprovalMode(mode: ApprovalMode): void { this._approvalMode.set(mode); }
```

- [ ] **Step 6: Add the nav flag and set it on the Approvals leaf**

In `nav.ts`, extend the `NavLink` interface (line 1) with the optional flag:

```ts
export interface NavLink { label: string; path: string; area?: string; moduleKey?: string; deploymentAdmin?: boolean; hideWhenAutoApprove?: boolean; children?: NavLink[]; }
```

Set it on the Approvals leaf (line 10):

```ts
    { label: 'Approvals', path: '/journal/approvals', area: 'gl', hideWhenAutoApprove: true },
```

- [ ] **Step 7: Add the predicate clause in the shell**

In `shell.ts`, extend the `visibleNav` predicate (the `visibleSections(NAV, (link) => …)` block, ~line 92):

```ts
  protected readonly visibleNav = computed(() =>
    visibleSections(NAV, (link) =>
      (!link.area || this.caps.hasArea(link.area)) &&
      (!link.moduleKey || this.caps.moduleEnabled(link.moduleKey)) &&
      (!link.deploymentAdmin || this.caps.deploymentAdmin()) &&
      (!link.hideWhenAutoApprove || this.caps.approvalMode() !== 'AutoApprove')));
```

- [ ] **Step 8: Run the shell + capability suites to verify green**

Run: `cd UI/Angular && npx ng test --include=src/app/layout/shell.spec.ts --watch=false`
Expected: PASS (including the new AutoApprove case). Then confirm the capability service spec still passes:
Run: `cd UI/Angular && npx ng test --include=src/app/core/capabilities/capability.service.spec.ts --watch=false`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/capabilities.ts UI/Angular/src/app/core/capabilities/capability.service.ts UI/Angular/src/app/core/capabilities/capability.testing.ts UI/Angular/src/app/layout/nav.ts UI/Angular/src/app/layout/shell.ts UI/Angular/src/app/layout/shell.spec.ts
git commit -m "feat(ui): hide Approvals nav leaf under AutoApprove"
```

---

### Task 3: Frontend — route guard for `/journal/approvals`

**Files:**
- Create: `UI/Angular/src/app/core/capabilities/hide-when-autoapprove.guard.ts`
- Test: `UI/Angular/src/app/core/capabilities/hide-when-autoapprove.guard.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `CapabilityService.loaded` + `CapabilityService.approvalMode` (Task 2); `StubCapabilityService.setApprovalMode` (Task 2).
- Produces: `hideWhenAutoApproveGuard: CanActivateFn`.

- [ ] **Step 1: Write the failing guard spec**

Create `UI/Angular/src/app/core/capabilities/hide-when-autoapprove.guard.spec.ts` (mirrors `deployment-admin.guard.spec.ts`):

```ts
import { TestBed } from '@angular/core/testing';
import { UrlTree, provideRouter } from '@angular/router';
import { runInInjectionContext, EnvironmentInjector } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { hideWhenAutoApproveGuard } from './hide-when-autoapprove.guard';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

describe('hideWhenAutoApproveGuard', () => {
  async function run(mode: 'TwoPerson' | 'SelfApprove' | 'AutoApprove') {
    const stub = new StubCapabilityService();
    stub.setApprovalMode(mode);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    });
    const injector = TestBed.inject(EnvironmentInjector);
    const route = { data: { fallback: '/journal' } } as any;
    return runInInjectionContext(injector, () =>
      firstValueFrom(hideWhenAutoApproveGuard(route, {} as any) as any));
  }

  it('allows the route when the client is on TwoPerson', async () => {
    expect(await run('TwoPerson')).toBe(true);
  });

  it('allows the route when the client is on SelfApprove', async () => {
    expect(await run('SelfApprove')).toBe(true);
  });

  it('redirects to the fallback when the client is on AutoApprove', async () => {
    const result = await run('AutoApprove');
    expect(result).toBeInstanceOf(UrlTree);
    expect((result as UrlTree).toString()).toBe('/journal');
  });

  it('waits until capabilities are loaded before emitting', async () => {
    const stub = new StubCapabilityService();
    stub.setApprovalMode('AutoApprove');
    stub.setLoaded(false);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    });
    const injector = TestBed.inject(EnvironmentInjector);
    const route = { data: { fallback: '/journal' } } as any;

    let resolved = false;
    const result$ = runInInjectionContext(injector, () => hideWhenAutoApproveGuard(route, {} as any) as any);
    const promise = firstValueFrom(result$).then((v: unknown) => { resolved = true; return v; });
    await Promise.resolve();
    await Promise.resolve();
    expect(resolved).toBe(false);

    stub.setLoaded(true);
    const result = await promise;
    expect(resolved).toBe(true);
    expect(result).toBeInstanceOf(UrlTree);
  });
});
```

- [ ] **Step 2: Run the guard spec to verify it fails**

Run: `cd UI/Angular && npx ng test --include=src/app/core/capabilities/hide-when-autoapprove.guard.spec.ts --watch=false`
Expected: FAIL — cannot resolve `./hide-when-autoapprove.guard`.

- [ ] **Step 3: Implement the guard**

Create `UI/Angular/src/app/core/capabilities/hide-when-autoapprove.guard.ts`:

```ts
import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivateFn, Router, UrlTree } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { Observable, filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/** Redirects away from a route that is meaningless under AutoApprove (e.g. the pending-approvals
 * queue — always empty when entries post straight to the books). The fallback route lives in the
 * route's `data.fallback` (defaults to `/journal`). Waits for capabilities to load before deciding. */
export const hideWhenAutoApproveGuard: CanActivateFn = (
  route: ActivatedRouteSnapshot,
): Observable<boolean | UrlTree> => {
  const caps = inject(CapabilityService);
  const router = inject(Router);
  const fallback = (route.data['fallback'] as string) ?? '/journal';
  return toObservable(caps.loaded).pipe(
    filter((loaded) => loaded),
    take(1),
    map(() => (caps.approvalMode() === 'AutoApprove' ? router.parseUrl(fallback) : true)),
  );
};
```

- [ ] **Step 4: Wire the guard onto the route**

In `app.routes.ts`, add the import near the other capability-guard imports:

```ts
import { hideWhenAutoApproveGuard } from './core/capabilities/hide-when-autoapprove.guard';
```

Change the approvals child route (currently `{ path: 'approvals', component: ApprovalQueue },`) to:

```ts
      { path: 'approvals', component: ApprovalQueue, canActivate: [hideWhenAutoApproveGuard], data: { fallback: '/journal' } },
```

- [ ] **Step 5: Run the guard spec to verify it passes**

Run: `cd UI/Angular && npx ng test --include=src/app/core/capabilities/hide-when-autoapprove.guard.spec.ts --watch=false`
Expected: PASS (4/4).

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/hide-when-autoapprove.guard.ts UI/Angular/src/app/core/capabilities/hide-when-autoapprove.guard.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): redirect /journal/approvals under AutoApprove"
```

---

### Task 4: Frontend — refresh capabilities after a policy change

**Files:**
- Modify: `UI/Angular/src/app/features/admin/approval-policy.ts`
- Test: `UI/Angular/src/app/features/admin/approval-policy.spec.ts`

**Interfaces:**
- Consumes: `CapabilityService.reload()` (existing); `provideCapabilities`/`StubCapabilityService` (spied).
- Produces: nav updates immediately after a mode change (no consumer beyond the user).

- [ ] **Step 1: Write the failing test**

In `approval-policy.spec.ts`, add a test that a successful save triggers `caps.reload()`. Extend the existing spec (it already configures `provideCapabilities('admin.approvalPolicy')` and `ClientContextService`). Add imports and a test:

```ts
import { CapabilityService } from '../../core/capabilities/capability.service';
```

```ts
  it('reloads capabilities after a successful save so nav re-gates', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const caps = TestBed.inject(CapabilityService);
    const reloadSpy = vi.spyOn(caps, 'reload');
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'TwoPerson' });
    f.detectChanges();

    const c = f.componentInstance as ApprovalPolicyScreen;
    c.select('AutoApprove');
    c.save();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'AutoApprove' });

    expect(reloadSpy).toHaveBeenCalledTimes(1);
  });
```

Ensure `vi` is imported in this spec (add `import { vi } from 'vitest';` if not already present).

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd UI/Angular && npx ng test --include=src/app/features/admin/approval-policy.spec.ts --watch=false`
Expected: FAIL — `reload` is not called (0 times).

- [ ] **Step 3: Call `caps.reload()` on save success**

In `approval-policy.ts`, inject `CapabilityService` and call `reload()` in the save success handler. Add the import:

```ts
import { CapabilityService } from '../../core/capabilities/capability.service';
```

Add the injection near the existing `service` field:

```ts
  private readonly caps = inject(CapabilityService);
```

Update the `save()` success branch:

```ts
    this.service.set(mode).subscribe({
      next: () => { this.saved.set(true); this.caps.reload(); },
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd UI/Angular && npx ng test --include=src/app/features/admin/approval-policy.spec.ts --watch=false`
Expected: PASS (existing save test + the new reload test).

- [ ] **Step 5: Full frontend build gate**

Run: `cd UI/Angular && npx ng build --configuration production`
Expected: build succeeds within budgets (< 2MB error gate).

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/admin/approval-policy.ts UI/Angular/src/app/features/admin/approval-policy.spec.ts
git commit -m "feat(ui): reload capabilities after approval-mode change"
```

---

### Task 5: Dev-stack SMOKE (live, JordanSoft — currently AutoApprove)

**Files:** none (verification only).

**Interfaces:** Consumes the full stack from Tasks 1–4.

- [ ] **Step 1: Deploy the branch**

Run `C:\Users\jorda\OneDrive\Documents\JordanSoft\deploy\update.ps1` (backs up first; rebuilds `api`+`web`; mongo data untouched).

- [ ] **Step 2: Confirm the mode is on the capabilities wire**

With the Owner DevToken (sub `00000000-0000-0000-0000-000000000005`, claims `role=Admin`,`admin=true`) and client `761f80b1-f0b5-4927-b8de-dedf84477e59`:

```bash
curl -s -H "Authorization: DevToken <token>" \
  http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/me/capabilities
```
Expected: JSON includes `"approvalMode":"AutoApprove"` (camelCase key, string value). Build the token as in the fiscal-settings smoke: base64url of `{"sub":"00000000-0000-0000-0000-000000000005","name":"Owner","claims":[{"type":"role","value":"Admin"},{"type":"admin","value":"true"}]}`.

- [ ] **Step 3: Smoke the UI**

Open the app at `http://localhost:4200`. Expand **General Ledger** — the **Approvals** leaf should be ABSENT (client is AutoApprove). Navigate directly to `http://localhost:4200/journal/approvals` — it should redirect to `/journal`.

- [ ] **Step 4: Flip to TwoPerson and confirm it returns, then restore**

```bash
# switch to TwoPerson
curl -s -X PUT -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"mode":"TwoPerson"}' \
  http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/approval-policy
```
In the app, switch identity (or reload) to re-fetch capabilities — the **Approvals** leaf should now appear and `/journal/approvals` should load. Then RESTORE AutoApprove:

```bash
curl -s -X PUT -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"mode":"AutoApprove"}' \
  http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/approval-policy
```
Confirm a final GET `/me/capabilities` reports `"approvalMode":"AutoApprove"` — JordanSoft restored to its original mode.

---

## Self-Review

**1. Spec coverage:**
- Backend `approvalMode` on `/me/capabilities` (field + populate + test) → Task 1. ✓
- FE model + `CapabilityService.approvalMode` signal → Task 2. ✓
- `NavLink.hideWhenAutoApprove` + shell predicate + shell test → Task 2. ✓
- Route guard + wiring + guard spec → Task 3. ✓
- `caps.reload()` after policy save + spec → Task 4. ✓
- Shared/local test doubles updated (`StubCapabilityService`, `StubCaps`) → Tasks 2. ✓
- Dev-stack smoke → Task 5. ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows complete code.

**3. Type consistency:** `approvalMode: ApprovalMode` (`'TwoPerson'|'SelfApprove'|'AutoApprove'`) is consistent across `capabilities.ts`, `CapabilityService`, both test doubles, the guard, and the shell predicate. Backend `ApprovalMode` (last positional on `CapabilitiesResponse`) serializes to the same string values. Guard name `hideWhenAutoApproveGuard` matches across guard file, spec, and route. `EMPTY_CAPABILITIES.approvalMode` = `'TwoPerson'` matches the model type.
