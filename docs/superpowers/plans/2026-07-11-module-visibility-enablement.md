# Module Visibility & Enablement — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the UI show subledger modules only when they're enabled for the client (killing reachable 403s), move Chart Health off the dashboard into a firm-admin Module Setup screen, make chart-readiness degrade gracefully instead of 500-ing, and leave a clean entitlement seam for future licensing.

**Architecture:** The one seam is `/me/capabilities` — which the Angular shell already fetches per client/identity to drive the nav. It gains `enabledModules`; the nav gates subledger links on `enabled ∧ capability`. Chart-readiness gets a `NotConfigured` advisory (200, never 500). A new `admin.modules` capability + an `IModuleEntitlement` seam (default = all) gate/bound enablement, surfaced through a Module Setup admin screen.

**Tech Stack:** C# / .NET 10 modular monolith (`Accounting101.Host`), xUnit + EphemeralMongo host fixtures; Angular 17+ standalone components + signals, vitest specs.

## Global Constraints

- **Branch off `master`**; commit per task; merge to local master (no-ff) when done; **do not push**.
- **Backend green:** `dotnet test Accounting101.slnx -m:1` (whole solution; `-m:1` is the arbiter for the parallel-node OOM flake).
- **Frontend green:** `npm test` in `UI/Angular` (vitest).
- **No behavior change to the engine chokepoint.** `ModuleAccess.AuthorizeAsync` (`Backend/Accounting101.Ledger.Api/Control/ModuleAccess.cs:58-60`) stays default-closed and unchanged — this feature makes its 403 *unreachable through the UI*, not weaker.
- **Wire format is camelCase** (System.Text.Json Web defaults); strict binding is on (unknown JSON fields → 400).
- **Deploy note (post-plan):** once merged to master, promote into the JordanSoft books container with `Documents\JordanSoft\deploy\update.ps1`.

## Reference: nav area → module key map (canonical for this plan)

| Nav label | `area` | module KEY (`EnabledModules`) | read cap |
|---|---|---|---|
| Receivables | `ar` | `receivables` | `ar.read` |
| Payables | `ap` | `payables` | `ap.read` |
| Payroll | `payroll` | `payroll` | `payroll.read` |
| Cash & Banking | `cash` | `cash` | `cash.read` |
| — Bank Reconciliation (child) | `bankrec` | `reconciliation` | `bankrec.read` |
| Fixed Assets | `fixedassets` | `fixedassets` | `fixedassets.read` |
| Inventory | `inventory` | `inventory` | `inventory.read` |

---

## PHASE A — Kill the reachable 403s + red widget (the reported complaint)

### Task 1: Backend — `enabledModules` on the capabilities response

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs:29-30`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs:26-35`
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs`

**Interfaces:**
- Produces: `CapabilitiesResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin, IReadOnlyList<string> EnabledModules)`.

- [ ] **Step 1: Write the failing test** in `CapabilitiesTests.cs` (mirror the file's existing `ApiFixture` pattern):

```csharp
[Fact]
public async Task Capabilities_includes_the_clients_enabled_modules()
{
    SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
    await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "cash", "reconciliation" });

    CapabilitiesResponse caps = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
        $"/clients/{c.ClientId}/me/capabilities"))!;

    Assert.Equal(new[] { "cash", "reconciliation" }, caps.EnabledModules);
}
```

- [ ] **Step 2: Run it — FAIL to compile** (`CapabilitiesResponse` has no `EnabledModules`).
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter Capabilities_includes_the_clients_enabled_modules`

- [ ] **Step 3: Add the field.** `AdminContracts.cs:29-30`:
```csharp
public sealed record CapabilitiesResponse(
    IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin,
    IReadOnlyList<string> EnabledModules);
```

- [ ] **Step 4: Populate it.** In `CapabilitiesEndpoints.GetMyCapabilities` (already has `ControlStore control`), read the registration and pass its modules:
```csharp
bool deploymentAdmin = user.HasClaim("admin", "true");
ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
return Results.Ok(new CapabilitiesResponse(
    membership.Capabilities,
    membership.GrantedRoles.Select(r => r.ToString()).ToList(),
    deploymentAdmin,
    client?.EnabledModules ?? []));
```
Add `using Accounting101.Ledger.Api.Control;` if not present.

- [ ] **Step 5: Run the test — PASS.** Then check other `CapabilitiesResponse` constructor call sites compile (the module `ChartReadiness` handlers deserialize it, they don't construct it; the positional add is source-compatible for deserialization). Build: `dotnet build Accounting101.slnx -m:1`.

- [ ] **Step 6: Commit** — `feat(caps): expose client EnabledModules on /me/capabilities`.

---

### Task 2: Frontend — capabilities type + `moduleEnabled`

**Files:**
- Modify: `UI/Angular/src/app/core/capabilities/capabilities.ts`
- Modify: `UI/Angular/src/app/core/capabilities/capability.service.ts:55-66`
- Test: `UI/Angular/src/app/core/capabilities/capability.service.spec.ts`

**Interfaces:**
- Consumes: the Task 1 wire field `enabledModules`.
- Produces: `CapabilityService.enabledModules: Signal<ReadonlySet<string>>` and `moduleEnabled(key: string): boolean`.

- [ ] **Step 1: Write the failing spec** in `capability.service.spec.ts` (follow the file's existing harness that stubs the `/me/capabilities` HTTP response): assert that when the response includes `enabledModules: ['cash']`, `service.moduleEnabled('cash')` is `true` and `service.moduleEnabled('payables')` is `false`.

- [ ] **Step 2: Run — FAIL** (`moduleEnabled` undefined). Run: `npm test -- capability.service`

- [ ] **Step 3: Extend the type.** `capabilities.ts`:
```ts
export interface CapabilitiesResponse {
  capabilities: string[];
  roles: string[];
  deploymentAdmin: boolean;
  enabledModules: string[];
}
export const EMPTY_CAPABILITIES: CapabilitiesResponse = {
  capabilities: [], roles: [], deploymentAdmin: false, enabledModules: [],
};
```

- [ ] **Step 4: Add the signal + method.** In `capability.service.ts`, alongside `capabilities`/`roles`/`deploymentAdmin` (lines 55-57):
```ts
readonly enabledModules: Signal<ReadonlySet<string>> = computed(() => new Set(this.current().enabledModules));
```
and next to `has`/`hasArea` (lines 59-66):
```ts
/** True if the given module key is enabled for the active client. */
moduleEnabled(key: string): boolean { return this.enabledModules().has(key); }
```

- [ ] **Step 5: Run — PASS.** `npm test -- capability.service`

- [ ] **Step 6: Commit** — `feat(caps-ui): CapabilityService.moduleEnabled from enabledModules`.

---

### Task 3: Frontend — nav filters subledgers by enablement

**Files:**
- Modify: `UI/Angular/src/app/layout/nav.ts:1-2,16-25`
- Modify: `UI/Angular/src/app/layout/shell.ts:91-94`
- Test: `UI/Angular/src/app/layout/nav.spec.ts`, `UI/Angular/src/app/layout/shell.spec.ts`

**Interfaces:**
- Consumes: `CapabilityService.moduleEnabled` (Task 2).
- Produces: nav visibility rule `(!link.area || hasArea(area)) && (!link.moduleKey || moduleEnabled(moduleKey)) && (!link.deploymentAdmin || deploymentAdmin())`.

- [ ] **Step 1: Write the failing spec** in `nav.spec.ts` (the file drives `visibleSections(NAV, canSee)` with a hand-built predicate). Add a test: with a predicate where `hasArea` is true for all areas but `moduleEnabled` is true only for `receivables`, `visibleSections` yields the Receivables link but **not** Payables/Payroll/Fixed Assets/Inventory. (This requires `visibleSections`/predicate to consider `moduleKey` — see Step 3/4.)

- [ ] **Step 2: Run — FAIL.** `npm test -- nav`

- [ ] **Step 3: Add `moduleKey` to the nav model + subledger items.** `nav.ts`:
```ts
export interface NavLink { label: string; path: string; area?: string; moduleKey?: string; deploymentAdmin?: boolean; children?: NavLink[]; }
```
Subledgers group (lines 16-25) — add `moduleKey` per the plan's area→key map:
```ts
{ label: 'Subledgers', items: [
  { label: 'Receivables', path: '/receivables', area: 'ar', moduleKey: 'receivables' },
  { label: 'Payables', path: '/payables', area: 'ap', moduleKey: 'payables' },
  { label: 'Payroll', path: '/payroll', area: 'payroll', moduleKey: 'payroll' },
  { label: 'Cash & Banking', path: '/cash', area: 'cash', moduleKey: 'cash', children: [
    { label: 'Bank Reconciliation', path: '/cash/reconciliation', area: 'bankrec', moduleKey: 'reconciliation' },
  ] },
  { label: 'Fixed Assets', path: '/fixed-assets', area: 'fixedassets', moduleKey: 'fixedassets' },
  { label: 'Inventory', path: '/inventory', area: 'inventory', moduleKey: 'inventory' },
] },
```
(`visibleSections` already prunes empty groups and a parent's hidden children.)

- [ ] **Step 4: Extend the shell predicate.** `shell.ts:91-94`:
```ts
protected readonly visibleNav = computed(() =>
  visibleSections(NAV, (link) =>
    (!link.area || this.caps.hasArea(link.area)) &&
    (!link.moduleKey || this.caps.moduleEnabled(link.moduleKey)) &&
    (!link.deploymentAdmin || this.caps.deploymentAdmin())));
```

- [ ] **Step 5: Run — PASS** (`npm test -- nav shell`). Confirm `shell.spec.ts` still passes (add a case if it stubs caps: a client with only `cash` enabled shows Cash & Banking, hides the other subledgers).

- [ ] **Step 6: Commit** — `feat(nav): gate subledger links on module enablement`.

---

### Task 4: Frontend — remove Chart Health from the dashboard

**Files:**
- Modify: `UI/Angular/src/app/features/dashboard/dashboard.ts`
- Test: `UI/Angular/src/app/features/dashboard/dashboard.spec.ts`

**Interfaces:** Consumes nothing new. `ChartHealthService` + `chart-health.ts` are **kept** (reused in Task 8).

- [ ] **Step 1: Update the failing spec.** In `dashboard.spec.ts`, assert the rendered dashboard does **not** contain `app-chart-health-widget` (and no longer needs the widget's providers).

- [ ] **Step 2: Run — FAIL.** `npm test -- dashboard`

- [ ] **Step 3: Strip the widget.** `dashboard.ts` becomes:
```ts
import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<h1 class="text-2xl font-bold mb-2">Dashboard</h1>
    <p class="text-muted-foreground mb-4">Welcome to Accounting 101.</p>`,
})
export class Dashboard {}
```

- [ ] **Step 4: Run — PASS.** `npm test -- dashboard`

- [ ] **Step 5: Commit** — `feat(dashboard): remove Chart Health widget (moves to Module Setup)`.

> **Phase A checkpoint:** with Tasks 1–4 promoted, a core-only client sees only enabled subledgers, no dashboard readiness widget, and **no reachable 403** through the nav. The reported complaint is resolved. Phases B/C harden and add the setup home.

---

## PHASE B — Readiness hardening + enablement authorization

### Task 5: Backend — chart-readiness degrades to a `NotConfigured` advisory (200, never 500)

**Files:**
- Modify: `Modules/Shared/Accounting101.ModuleKit/AccountRequirement.cs:7` (enum), and add a `ChartReadinessReport.NotConfigured(moduleKey, detail)` factory.
- Modify each of the 6 `ChartReadiness` handlers: `Modules/Receivables/.../ReceivablesEndpoints.cs:351`, `Payables/.../PayablesEndpoints.cs:250`, `Payroll/.../PayrollEndpoints.cs:126`, `Banking/Cash/.../CashEndpoints.cs:140`, `FixedAssets/.../FixedAssetsEndpoints.cs:236`, `Inventory/.../InventoryEndpoints.cs:194`.
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/ChartReadinessE2eTests.cs` (already asserts advisory-200 at line 74).

**Interfaces:**
- Produces: `AccountReadinessStatus.NotConfigured`; `ChartReadinessReport` returned (200) when `ForAsync` throws `InvalidOperationException` (posting account not configured).

- [ ] **Step 1: Write the failing E2E** in the Receivables `ChartReadinessE2eTests.cs`: seed a client + membership but do **not** configure the Receivables posting accounts (no `Receivables__Accounts__*`); GET `/clients/{id}/receivables/chart-readiness`; assert `HttpStatusCode.OK`, `report.Ready == false`, and the report signals not-configured (e.g. `report.ModuleKey == "receivables"` with a `NotConfigured` marker). Mirror the fixture setup already used in that file.

- [ ] **Step 2: Run — FAIL** (currently 500). Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter ChartReadiness`

- [ ] **Step 3: Add the status + factory.** `AccountRequirement.cs`:
```csharp
public enum AccountReadinessStatus { Ok, Missing, Inactive, WrongType, MissingDimensions, NotConfigured }
```
Add to `ChartReadinessReport`:
```csharp
public static ChartReadinessReport NotConfigured(string moduleKey, string detail) =>
    new(moduleKey, false, [ new AccountReadinessResult(
        Guid.Empty, "(module not configured)", null, [], AccountReadinessStatus.NotConfigured, null, null, detail) ]);
```
(Match the actual `AccountReadinessResult` constructor arity in that file.)

- [ ] **Step 4: Wrap the throw site in each handler.** Pattern (apply to all 6, substituting the module key):
```csharp
IReadOnlyList<AccountRequirement> reqs;
try { reqs = await requirements.ForAsync(clientId, cancellationToken); }
catch (InvalidOperationException ex) // posting accounts not configured for this module
{ return Results.Ok(ChartReadinessReport.NotConfigured("receivables", ex.Message)); }
IReadOnlyList<AccountResponse> chart = await ledger.GetAccountsAsync(clientId, cancellationToken);
return Results.Ok(ChartReadinessChecker.Check(reqs, chart, "receivables"));
```
Keep the existing `ReadinessAccess.Allows` 403 guard above it unchanged.

- [ ] **Step 5: Run the Receivables E2E — PASS.** Add one equivalent test in a second module's test project (e.g. Payables) to prove the pattern generalizes.

- [ ] **Step 6: Whole-solution** — `dotnet test Accounting101.slnx -m:1` (PASS; ModuleKit checker tests + 6 module suites).

- [ ] **Step 7: Commit** — `fix(readiness): unconfigured module returns 200 NotConfigured, not 500`.

---

### Task 6: Backend — `admin.modules` capability

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs:43-47,89-97`
- Modify: `RolePresets` (grep `RolePresets` — add `AdminModules` to the built-in **Admin** preset).
- Test: an assertion in `AdminCapabilityTests.cs` or a `Capabilities.All` catalog test.

**Interfaces:** Produces `Capabilities.AdminModules = "admin.modules"`, present in `Capabilities.All` and the Admin role preset.

- [ ] **Step 1: Failing test** — assert `Capabilities.All.Contains("admin.modules")` and that the Admin role preset includes it. (Place near existing catalog/preset tests.)

- [ ] **Step 2: Run — FAIL.**

- [ ] **Step 3: Add the constant + catalog entry.** `Capabilities.cs`:
```csharp
public const string AdminModules = "admin.modules";
```
and add `AdminModules` to the `All` set (line ~96).

- [ ] **Step 4: Add to the Admin preset** in `RolePresets` (so a client Admin can manage modules by default).

- [ ] **Step 5: Run — PASS.** Build whole solution.

- [ ] **Step 6: Commit** — `feat(rbac): add admin.modules capability`.

---

### Task 7: Backend — entitlement seam + gate enablement on `admin.modules`

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Control/IModuleEntitlement.cs` (+ `UnboundedModuleEntitlement` default returning all installed keys) and register it in DI.
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs:130-152` (`SetClientModules`).
- Test: `Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs`.

**Interfaces:**
- Produces: `IModuleEntitlement.AvailableModulesAsync(clientId, ct) → IReadOnlyList<string>`; `SetClientModules` gated on `Capabilities.AdminModules` and validating `enabled ⊆ available`.

- [ ] **Step 1: Failing tests** — (a) a member with `admin.modules` (but not `admin.client`) may `PUT /admin/clients/{id}/modules`; (b) a member without it gets 403; (c) enabling a key outside the available set returns 400 (with default entitlement = all installed, use an unknown key to trip it, OR inject a stub entitlement in the fixture that narrows availability).

- [ ] **Step 2: Run — FAIL.**

- [ ] **Step 3: Create the seam.** `IModuleEntitlement.cs`:
```csharp
namespace Accounting101.Ledger.Api.Control;

/// <summary>The set of module keys a client is *entitled* to enable. Bounds enablement
/// (enabled ⊆ available). The default is unbounded (all installed); licensed/operator-console
/// implementations replace it without touching enablement or the UI.</summary>
public interface IModuleEntitlement
{
    Task<IReadOnlyList<string>> AvailableModulesAsync(Guid clientId, CancellationToken cancellationToken = default);
}

public sealed class UnboundedModuleEntitlement(ControlStore control) : IModuleEntitlement
{
    public async Task<IReadOnlyList<string>> AvailableModulesAsync(Guid clientId, CancellationToken cancellationToken = default)
        => (await control.ListModulesAsync(cancellationToken)).Select(m => m.Key).ToList();
}
```
Register: `services.AddScoped<IModuleEntitlement, UnboundedModuleEntitlement>();` (in the engine's `AddLedgerEngine`/control registration).

- [ ] **Step 4: Gate + bound in `SetClientModules`.** Change the auth capability and add the subset check:
```csharp
private static async Task<IResult> SetClientModules(
    Guid clientId, SetClientModulesRequest request, ClaimsPrincipal user,
    IActorFactory actorFactory, ControlStore control, IModuleEntitlement entitlement, CancellationToken cancellationToken)
{
    if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminModules, actorFactory, control, cancellationToken))
        return Results.Forbid();
    if (await control.GetClientAsync(clientId, cancellationToken) is null) return Results.NotFound();

    IReadOnlyList<string> keys = request.ModuleKeys ?? [];
    HashSet<string> available = [.. await entitlement.AvailableModulesAsync(clientId, cancellationToken)];
    if (keys.FirstOrDefault(k => !available.Contains(k)) is { } notAllowed)
        return Results.Problem($"Module '{notAllowed}' is not available for this client.", statusCode: StatusCodes.Status400BadRequest);

    await control.SetClientModulesAsync(clientId, keys, cancellationToken);
    return Results.Ok(new ClientModulesResponse(clientId, keys));
}
```
(The unknown-key/installed check is now subsumed by the availability check, since default availability = installed.)

- [ ] **Step 5: Run — PASS.** Whole solution green.

- [ ] **Step 6: Commit** — `feat(modules): entitlement seam + admin.modules gate on enablement`.

---

## PHASE C — Module Setup screen + gap badge

### Task 8: Backend — Module Setup read endpoint

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` (+ handler) or a new `ModuleSetupEndpoints.cs` mapped in `Program.cs`.
- Add contract: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` — `ModuleSetupResponse`.
- Test: `AdminCapabilityTests.cs`.

**Interfaces:**
- Produces: `GET /admin/clients/{clientId}/module-setup` (gated `admin.modules`) → `ModuleSetupResponse(IReadOnlyList<string> Available, IReadOnlyList<string> Enabled)`. (Per-module readiness is fetched client-side via the existing `chart-readiness` endpoints, now 200-safe after Task 5 — keeps this endpoint simple and avoids duplicating readiness aggregation.)

- [ ] **Step 1: Failing test** — a member with `admin.modules` GETs `/admin/clients/{id}/module-setup` and receives `available` (all installed by default) + `enabled` (what was set). Non-`admin.modules` → 403.

- [ ] **Step 2: Run — FAIL.**

- [ ] **Step 3: Add the contract:**
```csharp
public sealed record ModuleSetupResponse(IReadOnlyList<string> Available, IReadOnlyList<string> Enabled);
```

- [ ] **Step 4: Add the handler** (gated via `AdminAuthorization.MayAsync(..., Capabilities.AdminModules, ...)`), returning `entitlement.AvailableModulesAsync` + `client.EnabledModules`.

- [ ] **Step 5: Run — PASS.** Whole solution green.

- [ ] **Step 6: Commit** — `feat(modules): GET /admin/clients/{id}/module-setup`.

---

### Task 9: Frontend — Module Setup screen

**Files:**
- Create: `UI/Angular/src/app/features/admin/module-setup/module-setup.ts` (+ `.spec.ts`), and a small `core/modules/module-setup.service.ts`.
- Modify: `UI/Angular/src/app/app.routes.ts:178-190` (route + `built` array), `UI/Angular/src/app/layout/nav.ts:36-43` (Administration nav item).
- Reuse: `core/chart-health/chart-health.service.ts` for per-enabled-module readiness gaps + the `chart-health-widget`'s deep-link affordance.

**Interfaces:**
- Consumes: `GET /admin/clients/{id}/module-setup` (Task 8), `PUT /admin/clients/{id}/modules` (existing), `chart-readiness` (Task 5, 200-safe).

- [ ] **Step 1: Failing spec** — `module-setup.spec.ts`: renders available modules with enable/disable toggles reflecting `enabled`; toggling issues `PUT .../modules`; for an enabled module it renders readiness gaps (stub `ChartHealthService`).

- [ ] **Step 2: Run — FAIL.** `npm test -- module-setup`

- [ ] **Step 3: Build the service + component.** Service wraps the two endpoints; component (OnPush, signals) lists `available`, toggles enablement (PUT, then refetch), and for each enabled module shows `ChartHealthService.readiness([module])` gaps with the existing "Fix ›" deep-link into Chart of Accounts.

- [ ] **Step 4: Wire routing + nav.** `app.routes.ts`:
```ts
{ path: 'admin/module-setup', component: ModuleSetup, canActivate: [canWrite], data: { requiredCapability: 'admin.modules', fallback: '/admin/users' } },
```
Add `'admin/module-setup'` to the `built` array (line ~185) so the fallback loop doesn't route it to `Placeholder`. Add a nav item under the Administration group (`nav.ts:36-43`, `area: 'admin'`).

- [ ] **Step 5: Run — PASS.** `npm test -- module-setup nav`

- [ ] **Step 6: Commit** — `feat(admin): Module Setup screen (enable modules + fix chart gaps)`.

---

### Task 10: Frontend — exception-only gap badge on enabled-module nav items

**Files:**
- Modify: `UI/Angular/src/app/layout/shell.ts` (compute per-enabled-module readiness → gap set), the nav item template (badge), and a small use of `ChartHealthService`.
- Test: `shell.spec.ts`.

**Interfaces:** Consumes `CapabilityService.enabledModules` + `ChartHealthService.readiness`. Produces a `gapModules: Signal<ReadonlySet<string>>`; a nav item whose `moduleKey ∈ gapModules` renders a warning badge.

- [ ] **Step 1: Failing spec** — with `cash` enabled and its readiness `ready:false`, the Cash & Banking nav item shows a badge; with `ready:true`, no badge.

- [ ] **Step 2: Run — FAIL.** `npm test -- shell`

- [ ] **Step 3: Implement.** In the shell, for the enabled module set, call `ChartHealthService.readiness(enabledModulesAsChartHealthList)` (map module keys → `CHART_HEALTH_MODULES` entries; note `reconciliation` has no readiness endpoint — exclude it), derive `gapModules` = keys whose report is `!ready` or errored, and render a small badge on nav items whose `moduleKey` is in the set. Silent when all ready.

- [ ] **Step 4: Run — PASS.**

- [ ] **Step 5: Commit** — `feat(nav): exception-only chart-gap badge on enabled modules`.

---

## Final verification

- [ ] **Whole solution:** `dotnet test Accounting101.slnx -m:1` → PASS.
- [ ] **Frontend:** `npm test` (UI/Angular) → PASS.
- [ ] **Manual (dev loop):** `dotnet run --project Accounting101.Host` + `ng serve`, acting as an Admin on a client with only `cash` enabled: Subledgers shows only Cash & Banking; dashboard has no Chart Health; `/admin/module-setup` lists modules + toggles + gaps; no console 403/500 from nav.
- [ ] **Promote:** merge to local master (no-ff), then `pwsh Documents\JordanSoft\deploy\update.ps1` to roll it into the books container; reload `http://localhost:4200` and confirm the JordanSoft nav is clean.

## Success criteria (from spec)

- Core-only (Cash) client: no AR/AP/Payroll/FA/Inventory nav links, no dashboard readiness widget, no reachable 403 through normal navigation.
- Firm admin (`admin.modules`) can enable/disable available modules and see/fix chart gaps in one screen.
- `chart-readiness` never 500s for an unconfigured module (returns 200 `NotConfigured`).
- Enablement bounded by `IModuleEntitlement` (default = all) — licensing plugs in later as a swap.
