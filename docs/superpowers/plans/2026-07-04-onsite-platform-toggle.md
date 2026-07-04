# On-Site Platform-Surface Toggle Implementation Plan (Phase 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Disable the `/platform/*` operator control plane by default, so an on-site single-firm deployment has no operator surface; a SaaS operator opts in with `Tenancy:Platform:Enabled=true`.

**Architecture:** A boolean config flag read by a new `TenancyDefaults.PlatformEnabled` helper. `MapPlatformEndpoints` self-gates on it — mapping nothing when disabled (so `/platform/*` returns 404). Only the operator endpoints toggle; the platform registry tier (`PlatformStore`, firm resolution, seeders) always runs. Because the default flips to off, the fixtures that exercise `/platform` opt in, and the module suites now run in genuine on-site mode.

**Tech Stack:** .NET (C#), ASP.NET Core minimal APIs, xUnit + EphemeralMongo (`SharedMongo`), `WebApplicationFactory<Program>`.

## Global Constraints

- Flag: `Tenancy:Platform:Enabled` (bool). Default **false** (unset / blank / unparseable → false).
- Gating point: `MapPlatformEndpoints` self-gates and returns early when disabled — `Program.cs` keeps its single unconditional call. Disabled → `/platform/*` is an unrouted **404** (not 403).
- Toggle the operator **surface only**. `AddPlatformRegistry` (PlatformStore, cluster factory, `FirmResolutionMiddleware`, default-firm + cluster seeders) stays unconditional. Do NOT modify `FirmResolutionMiddleware` or the `PlatformAdmin` policy registration.
- No additional single-firm guards (the data already enforces it — only the default firm exists on-site).
- Stage explicit paths only (never `git add -A`); leave the pre-existing uncommitted noise (`*.Tests.csproj`, `environment.ts`, `.slnx`) alone. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

## File Structure

**Modify:**
- `Backend/Accounting101.Ledger.Api/Platform/TenancyDefaults.cs` — add `PlatformEnabled(IConfiguration)`.
- `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs` — `MapPlatformEndpoints` self-gates.
- `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs` — opt in (`Tenancy:Platform:Enabled=true`).
- `Backend/Accounting101.Ledger.Api.Tests/ModuleSecretPersistenceTests.cs` — its own two-host factory opts in.

**Create:**
- `Backend/Accounting101.Ledger.Api.Tests/PlatformToggleTests.cs` — the toggle behavior.

---

## Task 1: Gate the platform surface behind `Tenancy:Platform:Enabled` (default off)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Platform/TenancyDefaults.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ModuleSecretPersistenceTests.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformToggleTests.cs` (create)

**Interfaces:**
- Produces: `TenancyDefaults.PlatformEnabled(IConfiguration) → bool` (default false).

> Atomic: flipping the default to off removes `/platform/*` from every host that does not opt in, so the fixture opt-ins and the self-gate must land in one commit to keep the existing platform tests green.

- [ ] **Step 1: Write the failing tests.** Create `Backend/Accounting101.Ledger.Api.Tests/PlatformToggleTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformToggleTests
{
    private static async Task<WebApplicationFactory<Program>> HostAsync(bool platformEnabled)
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Mongo:ConnectionString", runner.ConnectionString)
             .UseSetting("Mongo:ControlDatabase", "control_" + Guid.NewGuid().ToString("N"))
             .UseSetting("Mongo:PlatformDatabase", "platform_" + Guid.NewGuid().ToString("N"))
             .UseSetting("Tenancy:Platform:Enabled", platformEnabled ? "true" : "false"));
    }

    private static HttpClient Operator(WebApplicationFactory<Program> host)
    {
        HttpClient http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            DevTokenDefaults.Scheme,
            DevToken.Encode(new DevTokenPayload(Guid.NewGuid(), "Operator", [new DevClaim("platform", "true")])));
        return http;
    }

    [Fact]
    public async Task Disabled_platform_returns_404_on_platform_routes_even_with_an_operator_token()
    {
        await using WebApplicationFactory<Program> host = await HostAsync(platformEnabled: false);
        HttpClient op = Operator(host);

        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync("/platform/firms")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await op.PostAsJsonAsync("/platform/firms", new ProvisionFirmRequest { Name = "X" })).StatusCode);
    }

    [Fact]
    public async Task Disabled_platform_leaves_the_rest_of_the_app_routing()
    {
        await using WebApplicationFactory<Program> host = await HostAsync(platformEnabled: false);
        HttpClient anon = host.CreateClient();

        // A non-platform control-plane route is still mapped — it challenges auth (401), it is NOT 404.
        HttpResponseMessage response = await anon.GetAsync("/admin/clients");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Enabled_platform_maps_the_routes()
    {
        await using WebApplicationFactory<Program> host = await HostAsync(platformEnabled: true);
        HttpClient op = Operator(host);

        HttpResponseMessage response = await op.GetAsync("/platform/firms");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FullyQualifiedName~PlatformToggleTests`
Expected: FAIL — with the default still effectively "always mapped", the disabled-case asserts (expecting 404) fail because `/platform/firms` currently maps regardless of config. (`TenancyDefaults.PlatformEnabled` does not exist yet, but the test does not reference it directly — the failure is the 404 assertions.)

- [ ] **Step 3: Add the `PlatformEnabled` config reader.** In `TenancyDefaults.cs`, add after `ResolveDefaultFirmId`:

```csharp
    /// <summary>Whether the platform-operator control plane (/platform/*) is exposed. OFF by default — an
    /// on-site single-firm deployment has no operator tier. A SaaS operator sets Tenancy:Platform:Enabled=true.</summary>
    public static bool PlatformEnabled(IConfiguration configuration) =>
        bool.TryParse(configuration["Tenancy:Platform:Enabled"], out bool enabled) && enabled;
```

(`IConfiguration` / `Microsoft.Extensions.Configuration` is already imported in this file.)

- [ ] **Step 4: Self-gate `MapPlatformEndpoints`.** In `PlatformEndpoints.cs`, add these usings at the top if not present:

```csharp
using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
```

(`Accounting101.Ledger.Api.Platform` is already imported — it is where `TenancyDefaults` lives. Add `Microsoft.Extensions.Configuration` and `Microsoft.Extensions.DependencyInjection` for `IConfiguration` and `GetRequiredService`.)

Then make `MapPlatformEndpoints` return early when disabled — insert the guard as the first statement of the method body, before `RouteGroupBuilder platform = ...`:

```csharp
    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        if (!TenancyDefaults.PlatformEnabled(app.ServiceProvider.GetRequiredService<IConfiguration>()))
            return; // on-site: no operator control plane

        RouteGroupBuilder platform = app.MapGroup("/platform").RequireAuthorization(Policy);
        // ... existing route mappings unchanged ...
    }
```

- [ ] **Step 5: Opt the platform-testing fixtures in.** In `ApiFixture.cs`, add the flag to the `WithWebHostBuilder` chain (the `_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(...)` block) — append one `.UseSetting`:

```csharp
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Mongo:ConnectionString", connectionString)
             .UseSetting("Mongo:ControlDatabase", ControlDatabase)
             .UseSetting("Mongo:PlatformDatabase", PlatformDatabase)
             .UseSetting("Tenancy:Platform:Enabled", "true"));
```

In `ModuleSecretPersistenceTests.cs`, the `A_firm_provisioned_before_a_restart_keeps_valid_module_secrets` test builds its own host via a local `Build()` function that provisions a firm through `/platform/firms`. Add `.UseSetting("Tenancy:Platform:Enabled", "true")` to that `Build()`'s `WithWebHostBuilder` chain (read the current file to match its exact shape). The other test in that file (`Resolver_loads_the_same_secret_on_a_second_boot`) does not boot a host and needs no change.

- [ ] **Step 6: Run the focused toggle tests.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FullyQualifiedName~PlatformToggleTests`
Expected: PASS (3/3).

- [ ] **Step 7: Run the full Api.Tests project (the platform tests must stay green via the fixture opt-in).**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: all PASS (was 338/338; this adds 3 → 341). If `PlatformFirmsTests` / `PlatformClustersTests` / `PlatformUsageTests` / `ModuleSecretPersistenceTests` fail with 404s, a fixture opt-in was missed (Step 5).

- [ ] **Step 8: Run the module suites (they now run in on-site mode — flag defaults off — and must stay green).**

Run each:
`dotnet test Modules/Receivables/Accounting101.Receivables.Tests`
`dotnet test Modules/Payables/Accounting101.Payables.Tests`
`dotnet test Modules/Payroll/Accounting101.Payroll.Tests`
`dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests`
`dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests`
Expected: all PASS — modules, `/admin`, and the ledger work with the platform surface absent. (These fixtures do not set the flag, so they exercise on-site mode. None of them call `/platform`.)

- [ ] **Step 9: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/Platform/TenancyDefaults.cs Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs Backend/Accounting101.Ledger.Api.Tests/ModuleSecretPersistenceTests.cs Backend/Accounting101.Ledger.Api.Tests/PlatformToggleTests.cs
git commit -m "feat(tenancy): gate /platform surface behind Tenancy:Platform:Enabled (off by default)"
```

---

## Self-Review

**Spec coverage:**
- `TenancyDefaults.PlatformEnabled` (default off) → Step 3. ✓
- `MapPlatformEndpoints` self-gate → Step 4. ✓
- Registry tier / `FirmResolutionMiddleware` / `PlatformAdmin` policy untouched → not in any Modify list; verified. ✓
- Fixture opt-in (`ApiFixture`, `ModuleSecretPersistenceTests`) → Step 5. ✓
- Module suites run on-site (flag off) → Step 8. ✓
- Tests: disabled→404 (with operator token), disabled→rest-of-app routes (401 not 404), enabled→mapped → Step 1. ✓

**Placeholder scan:** Step 4 and Step 5 reference "existing route mappings unchanged" / "match its exact shape" — these are edits to existing code the implementer reads in place, not omitted logic; every new line of code is shown concretely. No functional placeholder.

**Type consistency:** `PlatformEnabled(IConfiguration) → bool` used identically in `TenancyDefaults` and the `MapPlatformEndpoints` guard; `Tenancy:Platform:Enabled` config key spelled identically in the helper, both fixtures, and the test's `HostAsync`. ✓
