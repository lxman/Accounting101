# Chart-Readiness Capability Gating Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gate the 6 advisory chart-readiness endpoints on the caller's per-module read capability (with an admin bypass), NOT on `EnabledModules` — closing the parity gap while preserving pre-enablement onboarding preview.

**Architecture:** A shared, pure `ReadinessAccess` decision in ModuleKit + a `GetMyCapabilitiesAsync` on the ModuleKit base ledger client (reusing the engine's existing `/me/capabilities`), wired into each of the 6 readiness handlers before the chart read. The dashboard widget applies the same rule client-side so it only requests modules the user may see. Zero engine change.

**Tech Stack:** C# .NET (ModuleKit domain-safe + ModuleKit.Api + 6 module APIs), xUnit; Angular (standalone/OnPush/signals), Vitest.

## Global Constraints

- **The rule:** `allow(moduleKey, caps) = caps.DeploymentAdmin || caps.Capabilities.contains("admin.client") || caps.Capabilities.contains(readCapFor(moduleKey))`. Independent of `EnabledModules`.
- **readCapFor:** `receivables→ar.read`, `payables→ap.read`, `payroll→payroll.read`, `cash→cash.read`, `fixedassets→fixedassets.read`, `inventory→inventory.read`. Unknown key ⇒ null ⇒ deny (unless admin).
- **Membership is always required and is NOT bypassed** — `/me/capabilities` returns 403 for a non-member (relayed), same as the chart read. The admin bypass exempts only the per-module capability.
- **Deny → HTTP 403.** Non-member → relayed 403 from `/me/capabilities`.
- `CapabilitiesResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin)` lives in `Accounting101.Ledger.Contracts` (ModuleKit references Contracts).
- The 6 readiness handlers are structurally identical: `ChartReadiness(Guid clientId, {X}ChartRequirements requirements, ILedgerClient ledger, CancellationToken cancellationToken)`, each passing a module-key literal to `ChartReadinessChecker.Check(reqs, chart, "<key>")`. Reuse that same literal for the gate.
- Enum/DTO/JSON conventions unchanged. No change to the readiness report shape or the checker.
- Angular: standalone, OnPush, signals; Vitest via `ng test --include=<glob>` (NOT `npx vitest run` — that doesn't work standalone in this repo).

---

### Task 1: ModuleKit `ReadinessAccess` (pure decision + capability map)

**Files:**
- Create: `Modules/Shared/Accounting101.ModuleKit/ReadinessAccess.cs`
- Test: `Modules/Shared/Accounting101.ModuleKit.Tests/ReadinessAccessTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ReadinessAccess.ReadCapabilityFor(string) : string?` and `ReadinessAccess.Allows(string moduleKey, bool deploymentAdmin, IReadOnlyCollection<string> capabilities) : bool` — Task 3 calls `Allows`.

- [ ] **Step 1: Write the failing tests.**

Create `Modules/Shared/Accounting101.ModuleKit.Tests/ReadinessAccessTests.cs`:

```csharp
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.ModuleKit.Tests;

public class ReadinessAccessTests
{
    [Theory]
    [InlineData("receivables", "ar.read")]
    [InlineData("payables", "ap.read")]
    [InlineData("payroll", "payroll.read")]
    [InlineData("cash", "cash.read")]
    [InlineData("fixedassets", "fixedassets.read")]
    [InlineData("inventory", "inventory.read")]
    public void ReadCapabilityFor_maps_each_module_to_its_area_read(string key, string expected) =>
        Assert.Equal(expected, ReadinessAccess.ReadCapabilityFor(key));

    [Fact]
    public void ReadCapabilityFor_unknown_key_is_null() =>
        Assert.Null(ReadinessAccess.ReadCapabilityFor("reconciliation"));

    [Fact]
    public void Holding_the_modules_read_capability_allows_that_module_only()
    {
        Assert.True(ReadinessAccess.Allows("cash", deploymentAdmin: false, ["cash.read"]));
        Assert.False(ReadinessAccess.Allows("payroll", deploymentAdmin: false, ["cash.read"]));
    }

    [Fact]
    public void Missing_the_capability_denies()
    {
        Assert.False(ReadinessAccess.Allows("cash", deploymentAdmin: false, ["ar.read", "gl.read"]));
        Assert.False(ReadinessAccess.Allows("cash", deploymentAdmin: false, []));
    }

    [Fact]
    public void Deployment_admin_is_allowed_any_module_without_the_capability()
    {
        Assert.True(ReadinessAccess.Allows("payroll", deploymentAdmin: true, []));
        Assert.True(ReadinessAccess.Allows("inventory", deploymentAdmin: true, ["ar.read"]));
    }

    [Fact]
    public void Client_admin_capability_is_allowed_any_module_without_the_read_capability()
    {
        Assert.True(ReadinessAccess.Allows("payroll", deploymentAdmin: false, ["admin.client"]));
        Assert.True(ReadinessAccess.Allows("cash", deploymentAdmin: false, ["admin.client"]));
    }

    [Fact]
    public void Unknown_module_denies_unless_admin()
    {
        Assert.False(ReadinessAccess.Allows("nope", deploymentAdmin: false, ["cash.read"]));
        Assert.True(ReadinessAccess.Allows("nope", deploymentAdmin: true, []));
        Assert.True(ReadinessAccess.Allows("nope", deploymentAdmin: false, ["admin.client"]));
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests --filter ReadinessAccessTests`
Expected: FAIL — `ReadinessAccess` does not exist.

- [ ] **Step 3: Implement `ReadinessAccess`.**

Create `Modules/Shared/Accounting101.ModuleKit/ReadinessAccess.cs`:

```csharp
namespace Accounting101.ModuleKit;

/// <summary>
/// The authorization decision for a module's advisory chart-readiness report: a member may read it
/// iff they are a deployment admin, a client admin, or hold that module's read capability. Deliberately
/// independent of the client's enabled-modules entitlement, so a chart can be previewed for a module
/// before it is enabled (the onboarding path). Membership is enforced upstream (the capabilities lookup
/// and the chart read both require it) and is not represented here.
/// </summary>
public static class ReadinessAccess
{
    private const string ClientAdmin = "admin.client";

    /// <summary>The "{area}.read" capability a module's readiness requires, or null for an unknown module key.</summary>
    public static string? ReadCapabilityFor(string moduleKey) => moduleKey switch
    {
        "receivables" => "ar.read",
        "payables"    => "ap.read",
        "payroll"     => "payroll.read",
        "cash"        => "cash.read",
        "fixedassets" => "fixedassets.read",
        "inventory"   => "inventory.read",
        _ => null,
    };

    /// <summary>Deployment admin OR client admin OR holds the module's read capability. Fail-closed on an unknown key.</summary>
    public static bool Allows(string moduleKey, bool deploymentAdmin, IReadOnlyCollection<string> capabilities)
    {
        if (deploymentAdmin || capabilities.Contains(ClientAdmin))
            return true;
        string? required = ReadCapabilityFor(moduleKey);
        return required is not null && capabilities.Contains(required);
    }
}
```

- [ ] **Step 4: Run to verify it passes.**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests --filter ReadinessAccessTests`
Expected: PASS.

- [ ] **Step 5: Run the whole ModuleKit test project.**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests`
Expected: all pass.

- [ ] **Step 6: Commit.**

```bash
git add Modules/Shared/Accounting101.ModuleKit/ReadinessAccess.cs Modules/Shared/Accounting101.ModuleKit.Tests/ReadinessAccessTests.cs
git commit -m "feat(chart-readiness): ReadinessAccess capability decision + module read-cap map"
```

---

### Task 2: `GetMyCapabilitiesAsync` on the ModuleKit base client + interfaces + fakes

**Files:**
- Modify: `Modules/Shared/Accounting101.ModuleKit.Api/ModuleLedgerClient.cs`
- Modify (add one interface method each): `Modules/Banking/Cash/Accounting101.Banking.Cash/ILedgerClient.cs`, `Modules/FixedAssets/Accounting101.FixedAssets/ILedgerClient.cs`, `Modules/Inventory/Accounting101.Inventory/ILedgerClient.cs`, `Modules/Payables/Accounting101.Payables/ILedgerClient.cs`, `Modules/Payroll/Accounting101.Payroll/ILedgerClient.cs`, `Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs`
- Modify (add one stub each): the `FakeLedgerClient` in `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs`, `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs`, `Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs`, `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs`, `Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs`, `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `ILedgerClient.GetMyCapabilitiesAsync(Guid clientId, CancellationToken) : Task<CapabilitiesResponse>` on all 6 modules — Task 3's handlers call it.

- [ ] **Step 1: Add the base method.** In `ModuleLedgerClient.cs`, immediately after `GetAccountsAsync` (which ends ~line 93), add:

```csharp
    public async Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/me/capabilities");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken); // non-member → 403 → LedgerClientException → relayed
        return (await response.Content.ReadFromJsonAsync<CapabilitiesResponse>(cancellationToken))!;
    }
```

`CapabilitiesResponse` is in `Accounting101.Ledger.Contracts` — confirm the file's `using` list includes it (it already uses `AccountResponse`/`EntryResponse` from that namespace, so the `using Accounting101.Ledger.Contracts;` is present).

- [ ] **Step 2: Declare it on each of the 6 `ILedgerClient` interfaces.** In each interface file listed above, add — directly after the existing `GetAccountsAsync` declaration:

```csharp
    /// <summary>The acting user's resolved capabilities on the client (for readiness authorization). 403 if not a member.</summary>
    Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default);
```

Each interface already declares `GetAccountsAsync` returning `AccountResponse` from `Accounting101.Ledger.Contracts`, so the namespace is in scope; add no new `using` unless the build reports one missing.

- [ ] **Step 3: Add the stub to each of the 6 `FakeLedgerClient`s.** In each `Fakes.cs` listed, directly after that fake's existing `GetAccountsAsync` stub, add:

```csharp
    public Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by this fake's consumers; ChartReadinessE2eTests exercises the real HTTP-backed engine.");
```

(These fakes drive service unit tests that never hit the readiness path; the gate is exercised only via the real host fixture in Task 3. This mirrors the established `GetAccountsAsync` stub convention.)

- [ ] **Step 4: Build the whole solution to confirm every `ILedgerClient` implementer satisfies the new member.**

Run: `dotnet build Accounting101.sln`
Expected: succeeds. (If `Modules/Inventory/Accounting101.Inventory.Tests/FakeLedgerClientTests.cs` asserts the fake's surface, update it only if the build/test requires; otherwise leave it.)

- [ ] **Step 5: No unit test for the base method here.** There is no `Accounting101.ModuleKit.Api.Tests` project (verified: `Modules/Shared/` has ModuleKit, ModuleKit.Api, ModuleKit.Tests, Settlement, Settlement.Tests — no Api.Tests). Do NOT create a new test project for one method. `GetMyCapabilitiesAsync`'s behavior is covered end-to-end by Task 3's Cash E2E: the non-member case proves the call forwards the bearer to `/me/capabilities` and relays its 403; the member-with-cap and member-without-cap cases prove the `CapabilitiesResponse` deserialized correctly and drove the decision. Note this coverage in the report.

- [ ] **Step 6: Commit.**

```bash
git add Modules/Shared/Accounting101.ModuleKit.Api/ModuleLedgerClient.cs \
  Modules/Banking/Cash/Accounting101.Banking.Cash/ILedgerClient.cs \
  Modules/FixedAssets/Accounting101.FixedAssets/ILedgerClient.cs \
  Modules/Inventory/Accounting101.Inventory/ILedgerClient.cs \
  Modules/Payables/Accounting101.Payables/ILedgerClient.cs \
  Modules/Payroll/Accounting101.Payroll/ILedgerClient.cs \
  Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs \
  Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs \
  Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs \
  Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs \
  Modules/Payables/Accounting101.Payables.Tests/Fakes.cs \
  Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs \
  Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs
git commit -m "feat(chart-readiness): GetMyCapabilitiesAsync on the module ledger client"
```

---

### Task 3: Gate the 6 readiness handlers + E2E

**Files:**
- Modify (identical 3-line gate each): `FixedAssetsEndpoints.cs`, `InventoryEndpoints.cs`, `ReceivablesEndpoints.cs`, `PayablesEndpoints.cs`, `CashEndpoints.cs`, `PayrollEndpoints.cs` (each under its `*.Api` project)
- Modify (add `AdminClientFor` helper): `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashHostFixture.cs`, `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesHostFixture.cs`
- Modify (add gating tests): `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/ChartReadinessE2eTests.cs`, `Modules/Receivables/Accounting101.Receivables.Tests/ChartReadinessE2eTests.cs`

**Interfaces:**
- Consumes: `ReadinessAccess.Allows` (Task 1); `ILedgerClient.GetMyCapabilitiesAsync` (Task 2).
- Produces: nothing downstream (Task 4 is client-side, independent).

- [ ] **Step 1: Gate each handler.** In each of the 6 `ChartReadiness` handlers, insert the gate as the first two statements of the body, reusing that handler's own module-key literal. Cash example (`CashEndpoints.cs`), and apply the identical shape to the other five with their literals (`fixedassets`, `inventory`, `receivables`, `payables`, `payroll`):

```csharp
    private static async Task<IResult> ChartReadiness(
        Guid clientId, CashChartRequirements requirements, ILedgerClient ledger, CancellationToken cancellationToken)
    {
        CapabilitiesResponse caps = await ledger.GetMyCapabilitiesAsync(clientId, cancellationToken); // non-member → relayed 403
        if (!ReadinessAccess.Allows("cash", caps.DeploymentAdmin, caps.Capabilities))
            return Results.Problem("Not authorized to view this module's chart readiness.", statusCode: StatusCodes.Status403Forbidden);

        IReadOnlyList<AccountRequirement> reqs = await requirements.ForAsync(clientId, cancellationToken);
        IReadOnlyList<AccountResponse> chart = await ledger.GetAccountsAsync(clientId, cancellationToken);
        return Results.Ok(ChartReadinessChecker.Check(reqs, chart, "cash"));
    }
```

`CapabilitiesResponse`, `ReadinessAccess` are already reachable: the file uses `Accounting101.Ledger.Contracts` (for `AccountResponse`) and `Accounting101.ModuleKit` (for `ChartReadinessChecker`/`AccountRequirement`). Add no new `using` unless the build reports one missing.

- [ ] **Step 2: Add `AdminClientFor` to the Cash + Receivables fixtures.** These mint an authenticated client for a member whose token also carries the deployment-admin claim. In `CashHostFixture.cs` (and the equivalent in `ReceivablesHostFixture.cs`), add next to `ClientFor`:

```csharp
    /// <summary>Like <see cref="ClientFor"/>, but the token also carries the deployment-admin claim (admin=true).</summary>
    public HttpClient AdminClientFor(Guid userId, string name)
    {
        HttpClient http = CreateClient();
        string token = DevToken.Encode(new DevTokenPayload(userId, name, [new DevClaim("admin", "true")]));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(DevTokenDefaults.Scheme, token);
        return http;
    }
```

(Match the Receivables fixture's own namespaces/usings; it already has `ClientFor`, `DevToken`, `DevTokenPayload`, `DevTokenDefaults`, `AuthenticationHeaderValue`.)

- [ ] **Step 3: Write the failing Cash gating tests.** Append to `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/ChartReadinessE2eTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Member_with_cash_read_may_read_cash_readiness()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.CashClerk); // CashClerk holds cash.read
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/cash/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Member_without_cash_read_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.ArClerk); // ArClerk lacks cash.read
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/cash/chart-readiness");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_member_without_cash_read_may_read()
    {
        (Guid clientId, _) = await fixture.SeedClientAsync(LedgerRole.Controller);
        Guid adminUser = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(adminUser, clientId, LedgerRole.ArClerk); // no cash.read…
        HttpClient adminHttp = fixture.AdminClientFor(adminUser, "Acme Admin");               // …but admin=true
        HttpResponseMessage resp = await adminHttp.GetAsync($"/clients/{clientId}/cash/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Non_member_is_forbidden()
    {
        (Guid clientId, _) = await fixture.SeedClientAsync(LedgerRole.Controller);
        HttpClient stranger = fixture.ClientFor(Guid.NewGuid(), "Nobody"); // authenticated, not a member
        HttpResponseMessage resp = await stranger.GetAsync($"/clients/{clientId}/cash/chart-readiness");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
```

- [ ] **Step 4: Write the failing Receivables gating test** (proves the non-identity `receivables→ar.read` mapping end-to-end). Append to `Modules/Receivables/Accounting101.Receivables.Tests/ChartReadinessE2eTests.cs` (adapt to that fixture's `SeedClientAsync` shape — Receivables' fixture may return more clients; use its established readiness-test pattern):

```csharp
    [Fact]
    public async Task Member_with_ar_read_may_read_receivables_readiness()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.ArClerk); // ArClerk holds ar.read
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/receivables/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Member_without_ar_read_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.CashClerk); // CashClerk lacks ar.read
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/receivables/chart-readiness");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
```

(Check the Receivables fixture's `SeedClientAsync` signature; if it differs from `(role) → (clientId, http)`, seed a member of the given role via `fixture.Control().AddMembershipAsync` + `fixture.ClientFor` instead, mirroring the Cash tests. The onboarding guarantee — no `EnabledModules` gate — is already proven by construction in Task 1: `ReadinessAccess` takes no entitlement input; note this in the report rather than adding a bespoke not-enabled-client E2E.)

- [ ] **Step 5: Run to verify the new tests fail (gate not yet applied if you wrote tests first) then, after Step 1's gate is in, pass.**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter ChartReadinessE2eTests`
Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter ChartReadinessE2eTests`
Expected: PASS (new gating tests + the two pre-existing readiness tests — the pre-existing ones seed `LedgerRole.Controller`, which holds every `.read`, so they stay 200).

- [ ] **Step 6: Build + run all 6 module test projects to confirm the identical gate compiles and no existing readiness E2E regressed.**

Run: `dotnet build Accounting101.sln` then, for each module, `dotnet test Modules/.../<Module>.Tests --filter ChartReadinessE2eTests`.
Expected: all pass. (Every existing readiness E2E seeds a Controller/admin-equivalent member, so all stay authorized.)

- [ ] **Step 7: Commit.**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs \
  Modules/Inventory/Accounting101.Inventory.Api/InventoryEndpoints.cs \
  Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs \
  Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs \
  Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashEndpoints.cs \
  Modules/Payroll/Accounting101.Payroll.Api/PayrollEndpoints.cs \
  Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/ \
  Modules/Receivables/Accounting101.Receivables.Tests/
git commit -m "feat(chart-readiness): gate the 6 readiness endpoints on per-module read capability"
```

---

### Task 4: Dashboard widget applies the same rule

**Files:**
- Modify: `UI/Angular/src/app/core/chart-health/chart-health.ts` (add `readCap` per module)
- Modify: `UI/Angular/src/app/core/chart-health/chart-health.service.ts` (fetch only a passed subset)
- Modify: `UI/Angular/src/app/features/dashboard/chart-health-widget.ts` (filter to visible modules)
- Test: `UI/Angular/src/app/features/dashboard/chart-health-widget.spec.ts`

**Interfaces:**
- Consumes: `CapabilityService` (`deploymentAdmin: Signal<boolean>`, `has(cap): boolean`).
- Produces: nothing.

- [ ] **Step 1: Add `readCap` to each module descriptor.** In `chart-health.ts`, extend `CHART_HEALTH_MODULES`:

```typescript
export const CHART_HEALTH_MODULES: { key: string; label: string; readCap: string }[] = [
  { key: 'receivables', label: 'Receivables', readCap: 'ar.read' },
  { key: 'payables', label: 'Payables', readCap: 'ap.read' },
  { key: 'payroll', label: 'Payroll', readCap: 'payroll.read' },
  { key: 'cash', label: 'Cash', readCap: 'cash.read' },
  { key: 'fixedassets', label: 'Fixed Assets', readCap: 'fixedassets.read' },
  { key: 'inventory', label: 'Inventory', readCap: 'inventory.read' },
];
```

- [ ] **Step 2: Let the service fetch a passed subset.** In `chart-health.service.ts`, change `readiness()` to accept the modules to query (default all):

```typescript
  readiness(modules: { key: string; label: string }[] = CHART_HEALTH_MODULES): Observable<ModuleHealth[]> {
    const id = this.client.clientId();
    if (!id) return EMPTY;
    return forkJoin(
      modules.map(m =>
        this.http.get<ChartReadinessReport>(`${environment.apiBaseUrl}/clients/${id}/${m.key}/chart-readiness`).pipe(
          map((report): ModuleHealth => ({ key: m.key, label: m.label, report, errored: false })),
          catchError(() => of<ModuleHealth>({ key: m.key, label: m.label, report: null, errored: true })),
        ),
      ),
    );
  }
```

(If `modules` is empty, `forkJoin([])` completes with `[]` — the widget shows an empty list, which is correct for a user who can see nothing.)

- [ ] **Step 3: Filter to the visible set in the widget.** In `chart-health-widget.ts`, inject `CapabilityService`, compute the visible modules, and drive `total` + the load from it:

```typescript
import { CapabilityService } from '../../core/capabilities/capability.service';
// …
  private readonly caps = inject(CapabilityService);

  readonly visibleModules = computed(() =>
    CHART_HEALTH_MODULES.filter(m =>
      this.caps.deploymentAdmin() || this.caps.has('admin.client') || this.caps.has(m.readCap)));

  readonly total = computed(() => this.visibleModules().length);
```

Change `readyCount` to a `computed` if it isn't already, and update the load effect to request only the visible modules:

```typescript
  constructor() {
    effect((onCleanup) => {
      const id = this.client.clientId();
      const modules = this.visibleModules();
      if (!id) { this.modules.set([]); this.loading.set(false); return; }
      this.loading.set(true);
      const sub = this.health.readiness(modules).subscribe(m => { this.modules.set(m); this.loading.set(false); });
      onCleanup(() => sub.unsubscribe());
    });
  }
```

(`total` was a plain `number` field; making it a `computed` requires updating the template binding `{{ total }}` → `{{ total() }}`.)

- [ ] **Step 4: Update the widget spec.** In `chart-health-widget.spec.ts`, `setup()` already provides capabilities via `provideCapabilities(...)`. The existing tests pass `['gl.manageAccounts']` — update `flushAll`/`setup` so the capability set includes the six read caps when the test expects all six modules (or switch those tests to an admin stub). Add two tests:

```typescript
  it('shows only the modules the user has read capability for', () => {
    // Caps: cash.read + payroll.read only.
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]),
      provideHttpClient(), provideHttpClientTesting(), provideCapabilities('cash.read', 'payroll.read')] });
    const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    for (const key of ['cash', 'payroll']) // only these two are requested
      ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`).flush({ moduleKey: key, ready: true, accounts: [] });
    f.detectChanges();
    expect(f.componentInstance.total()).toBe(2);
    expect((f.nativeElement as HTMLElement).textContent).toContain('2 / 2');
    ctrl.verify(); // proves NO request was made for the other four modules
  });

  it('an admin sees all six modules', () => {
    // Deployment admin: no per-module caps needed.
    const stub = provideCapabilities(); // empty caps
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]),
      provideHttpClient(), provideHttpClientTesting(), stub] });
    (TestBed.inject(CapabilityService) as unknown as { setDeploymentAdmin(v: boolean): void }).setDeploymentAdmin(true);
    const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    for (const key of ['receivables', 'payables', 'payroll', 'cash', 'fixedassets', 'inventory'])
      ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`).flush({ moduleKey: key, ready: true, accounts: [] });
    f.detectChanges();
    expect(f.componentInstance.total()).toBe(6);
    ctrl.verify();
  });
```

Import `CapabilityService` and (for the admin test) rely on `StubCapabilityService.setDeploymentAdmin` (exists in `capability.testing.ts`). Fix any existing test that assumed all six modules render by giving it either an admin stub or all six read caps. Keep the deep-link/gapLink tests unchanged (they call methods directly).

- [ ] **Step 5: Run the widget spec + typecheck.**

Run: `cd UI/Angular && npx ng test --watch=false --include="src/app/features/dashboard/**/*.spec.ts" --include="src/app/core/chart-health/**/*.spec.ts"`
Run: `cd UI/Angular && npx tsc -p tsconfig.app.json --noEmit`
Expected: PASS; no type errors.

- [ ] **Step 6: Commit.**

```bash
git add UI/Angular/src/app/core/chart-health/ UI/Angular/src/app/features/dashboard/
git commit -m "feat(chart-health): widget shows only modules the user may read"
```

---

### Task 5: Dev-stack smoke (mandatory before merge)

**Files:** none (verification only).

**Context:** The `[[accounting101-ui-mock-casing-trap]]` rule — the dev stack is the only layer that sees real cross-host auth. Both stock dev identities are broad (Dev Approver is `admin=true` → sees all; Dev Controller holds every `.read`), so the deny path needs a limited principal.

- [ ] **Step 1: Bring up the stack** (`pwsh .localdev/start.ps1`; wait for host:5000 + ng:4200).

- [ ] **Step 2: Confirm the admin/controller view is unchanged** — as Dev Controller or Dev Approver, the dashboard Chart-Health widget shows all six modules (Controller holds every `.read`; Approver is deployment admin).

- [ ] **Step 3: Exercise the deny path directly against the API** with a limited principal (no browser identity needed). Mint a `DevToken` for a NON-member and a member-without-a-cap, and confirm 403; confirm a member WITH the cap gets 200. Concretely, using the demo client `55f47a46-…`: a stranger token (`{sub:<new-guid>}`) → `GET /clients/55f47a46-…/cash/chart-readiness` → **403** (non-member). Then, if a limited membership can be seeded via the running control DB, verify a member with `cash.read` → 200 and one without → 403. If seeding a limited member against the live stack is impractical, record that the deny path is covered by the Task 3 E2E and that the smoke confirmed (a) admin/controller still see all six in the UI and (b) a non-member gets 403 over the real wire.

- [ ] **Step 4: Record** which principals were exercised and the observed status codes; shut the stack down cleanly.

---

## Notes for the whole-branch review

- Confirm zero change under `Backend/` (the engine) — the entire feature is ModuleKit + module APIs + UI.
- Confirm `environment.ts` stays uncommitted.
- Confirm the gate reuses each handler's existing module-key literal (no second source of truth for the module key).
- Full suites before merge: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests`, the 6 module test projects, and `cd UI/Angular && npx ng test --watch=false`.
