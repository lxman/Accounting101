# Private-Module Composition (Modules.Private overlay + discovery hook) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Host discovers and composes modules via a new `IModuleComposition` hook (deps.json-based discovery) instead of naming them in `Program.cs`, and gains a gitignored `Modules.Private/` overlay glob so proprietary add-on modules compose in with zero public-repo changes.

**Architecture:** New `IModuleComposition` interface + `ModuleCompositionDiscovery` (walks `DependencyContext.Default.RuntimeLibraries` filtered to `Type == "project"`) + host extensions in `Accounting101.ModuleKit.Api`; a trivial delegating composition class in each of the seven module Api projects; `Program.cs` swaps 7 Add + 7 Map calls for `AddDiscoveredModules()`/`MapDiscoveredModuleEndpoints()`; `Host.csproj` adds an unconditioned glob `ProjectReference` over `Modules.Private\**\*.Api.csproj`. Zero behavior change for the seven existing modules — the whole existing test suite (booting the real Host via `WebApplicationFactory<Program>`) is the regression gate.

**Tech Stack:** .NET 10 minimal APIs, `Microsoft.Extensions.DependencyModel` (deps.json reader), xUnit + `ApiFixture`.

**Spec:** `docs/superpowers/specs/2026-07-18-private-module-composition-design.md`
**Branch:** `feat/private-module-composition` (create from `master` before Task 1)

## Global Constraints

- Discovery MUST be deps.json-based (`DependencyContext`), never `Assembly.GetReferencedAssemblies()` — once `Program.cs` stops naming module types the compiler prunes those assembly references, but `ProjectReference`s always survive in deps.json as libraries with `Type == "project"`.
- Discovery order is deterministic: libraries ordered by name with `StringComparer.Ordinal`.
- A `project`-type library with no loadable assembly is skipped (not fatal); catch only `FileNotFoundException`/`FileLoadException`/`BadImageFormatException`.
- Engine composition stays explicit in `Program.cs` (`AddLedgerEngine`, all `Map*` engine endpoint groups) — only the seven module Add/Map pairs are replaced.
- The seven modules' existing `Add{Module}`/`Map{Module}Endpoints` extensions are untouched; composition classes are additive delegates. All seven Map extensions take `this IEndpointRouteBuilder` (verified), so `IModuleComposition.MapEndpoints(IEndpointRouteBuilder app)` composes directly.
- The `Host.csproj` overlay glob is UNconditioned (`..\Modules.Private\**\*.Api.csproj`) — an unmatched MSBuild glob yields zero items; no `Exists` condition.
- The seven explicit in-repo module `ProjectReference`s in `Host.csproj` stay explicit.
- Every commit message ends with the trailer: `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- Run backend tests with `dotnet test` from the repo root (Windows; PowerShell is the default shell). No Angular changes in this plan.

---

### Task 1: Composition contract, discovery engine, seven composition classes

**Files:**
- Create: `Modules/Shared/Accounting101.ModuleKit.Api/IModuleComposition.cs`
- Create: `Modules/Shared/Accounting101.ModuleKit.Api/ModuleCompositionDiscovery.cs`
- Modify: `Modules/Shared/Accounting101.ModuleKit.Api/Accounting101.ModuleKit.Api.csproj` (add `Microsoft.Extensions.DependencyModel`)
- Create: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesComposition.cs`
- Create: `Modules/Payables/Accounting101.Payables.Api/PayablesComposition.cs`
- Create: `Modules/Payroll/Accounting101.Payroll.Api/PayrollComposition.cs`
- Create: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashComposition.cs`
- Create: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationComposition.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsComposition.cs`
- Create: `Modules/Inventory/Accounting101.Inventory.Api/InventoryComposition.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModuleCompositionDiscoveryTests.cs`

**Interfaces:**
- Consumes: the existing per-module extensions — `Add{Receivables|Payables|Payroll|Cash|Reconciliation|FixedAssets|Inventory}(this IServiceCollection, IConfiguration)` and `Map{…}Endpoints(this IEndpointRouteBuilder)`; every module Api project already references `Accounting101.ModuleKit.Api` (verified).
- Produces (Task 2 relies on these exact signatures):
  - `interface IModuleComposition { void AddServices(IServiceCollection services, IConfiguration configuration); void MapEndpoints(IEndpointRouteBuilder app); }` in namespace `Accounting101.ModuleKit.Api`.
  - `static IReadOnlyList<IModuleComposition> ModuleCompositionDiscovery.DiscoverAll()`.
  - `static void ModuleCompositionHostExtensions.AddDiscoveredModules(this WebApplicationBuilder builder)` — discovers, calls `AddServices` on each, registers the list as a singleton `IReadOnlyList<IModuleComposition>`.
  - `static void ModuleCompositionHostExtensions.MapDiscoveredModuleEndpoints(this WebApplication app)` — resolves the singleton list, calls `MapEndpoints` on each (same instances; no re-discovery).

- [ ] **Step 1: Add the DependencyModel package**

Run (from repo root): `dotnet add Modules/Shared/Accounting101.ModuleKit.Api package Microsoft.Extensions.DependencyModel`
Expected: a `<PackageReference Include="Microsoft.Extensions.DependencyModel" Version="10.x.x" />` appears in the csproj (latest 10.x the feed offers).

- [ ] **Step 2: Write the failing discovery tests**

Create `Backend/Accounting101.Ledger.Api.Tests/ModuleCompositionDiscoveryTests.cs`:

```csharp
using Accounting101.ModuleKit.Api;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>Runs in the test process, whose deps.json includes every project the Host references
/// transitively — so discovery here sees exactly what the Host sees at startup.</summary>
public sealed class ModuleCompositionDiscoveryTests
{
    [Fact]
    public void Discovers_exactly_the_seven_module_compositions()
    {
        string[] found = ModuleCompositionDiscovery.DiscoverAll()
            .Select(m => m.GetType().Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Pins the full set: a pruned/undiscovered module fails here loudly, and adding an
        // eighth in-repo module must update this expectation deliberately.
        string[] expected =
        [
            "CashComposition", "FixedAssetsComposition", "InventoryComposition",
            "PayablesComposition", "PayrollComposition", "ReceivablesComposition",
            "ReconciliationComposition",
        ];
        Assert.Equal(expected, found);
    }

    [Fact]
    public void Discovery_order_is_deterministic()
    {
        Assert.Equal(
            ModuleCompositionDiscovery.DiscoverAll().Select(m => m.GetType().FullName).ToArray(),
            ModuleCompositionDiscovery.DiscoverAll().Select(m => m.GetType().FullName).ToArray());
    }
}
```

- [ ] **Step 3: Run the new tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~ModuleCompositionDiscoveryTests" 2>&1 | Select-Object -Last 15`
Expected: compile errors — `ModuleCompositionDiscovery` does not exist.

- [ ] **Step 4: Implement the contract and discovery**

Create `Modules/Shared/Accounting101.ModuleKit.Api/IModuleComposition.cs`:

```csharp
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.ModuleKit.Api;

/// <summary>Implemented once per module Api assembly. The host discovers every implementation
/// across its project-referenced assemblies at startup and calls both methods — installing a
/// module means referencing its Api project (the Modules/ list in Host.csproj, or an overlay
/// under Modules.Private/), not naming it in Program.cs.</summary>
public interface IModuleComposition
{
    void AddServices(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(IEndpointRouteBuilder app);
}
```

Create `Modules/Shared/Accounting101.ModuleKit.Api/ModuleCompositionDiscovery.cs`:

```csharp
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;

namespace Accounting101.ModuleKit.Api;

/// <summary>Finds every <see cref="IModuleComposition"/> across project-referenced assemblies.
/// Walks the DependencyContext (deps.json) rather than Assembly.GetReferencedAssemblies(): once
/// Program.cs stops naming module types the compiler prunes those assembly references, but
/// ProjectReferences always survive in deps.json as libraries of type "project" — which is also
/// exactly the filter that admits Modules.Private overlays and never NuGet packages.</summary>
public static class ModuleCompositionDiscovery
{
    public static IReadOnlyList<IModuleComposition> DiscoverAll()
    {
        DependencyContext context = DependencyContext.Default
            ?? throw new InvalidOperationException("No DependencyContext — module discovery requires a deps.json.");
        return
        [
            .. context.RuntimeLibraries
                .Where(l => l.Type == "project")
                .OrderBy(l => l.Name, StringComparer.Ordinal)
                .Select(TryLoad)
                .OfType<Assembly>()
                .SelectMany(a => a.GetExportedTypes())
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IModuleComposition).IsAssignableFrom(t))
                .Select(t => (IModuleComposition)Activator.CreateInstance(t)!),
        ];
    }

    /// <summary>A project-type library with no loadable assembly (content-only) is skipped, not fatal.</summary>
    private static Assembly? TryLoad(RuntimeLibrary library)
    {
        try { return Assembly.Load(new AssemblyName(library.Name)); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { return null; }
    }
}

/// <summary>Host-side composition: discover once, add all, map the same instances later.</summary>
public static class ModuleCompositionHostExtensions
{
    public static void AddDiscoveredModules(this WebApplicationBuilder builder)
    {
        IReadOnlyList<IModuleComposition> modules = ModuleCompositionDiscovery.DiscoverAll();
        foreach (IModuleComposition module in modules)
            module.AddServices(builder.Services, builder.Configuration);
        builder.Services.AddSingleton(modules);
    }

    public static void MapDiscoveredModuleEndpoints(this WebApplication app)
    {
        foreach (IModuleComposition module in app.Services.GetRequiredService<IReadOnlyList<IModuleComposition>>())
            module.MapEndpoints(app);
    }
}
```

- [ ] **Step 5: Add the seven composition classes**

One file per module Api project, identical shape. Exact contents:

`Modules/Receivables/Accounting101.Receivables.Api/ReceivablesComposition.cs`:

```csharp
using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Receivables.Api;

/// <summary>Discovered by the host (see <see cref="IModuleComposition"/>); delegates to the
/// module's existing extensions.</summary>
public sealed class ReceivablesComposition : IModuleComposition
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddReceivables(configuration);

    public void MapEndpoints(IEndpointRouteBuilder app) => app.MapReceivablesEndpoints();
}
```

The other six are the same file with these substitutions (namespace / class / Add call / Map call):

| File | Namespace | Class | AddServices body | MapEndpoints body |
|---|---|---|---|---|
| `Modules/Payables/Accounting101.Payables.Api/PayablesComposition.cs` | `Accounting101.Payables.Api` | `PayablesComposition` | `services.AddPayables(configuration)` | `app.MapPayablesEndpoints()` |
| `Modules/Payroll/Accounting101.Payroll.Api/PayrollComposition.cs` | `Accounting101.Payroll.Api` | `PayrollComposition` | `services.AddPayroll(configuration)` | `app.MapPayrollEndpoints()` |
| `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashComposition.cs` | `Accounting101.Banking.Cash.Api` | `CashComposition` | `services.AddCash(configuration)` | `app.MapCashEndpoints()` |
| `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationComposition.cs` | `Accounting101.Banking.Reconciliation.Api` | `ReconciliationComposition` | `services.AddReconciliation(configuration)` | `app.MapReconciliationEndpoints()` |
| `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsComposition.cs` | `Accounting101.FixedAssets.Api` | `FixedAssetsComposition` | `services.AddFixedAssets(configuration)` | `app.MapFixedAssetsEndpoints()` |
| `Modules/Inventory/Accounting101.Inventory.Api/InventoryComposition.cs` | `Accounting101.Inventory.Api` | `InventoryComposition` | `services.AddInventory(configuration)` | `app.MapInventoryEndpoints()` |

(Doc-comment stays the same wording in each. If any module's `Add…` extension takes different parameters than `(IServiceCollection, IConfiguration)`, match its real signature — `Program.cs` currently calls every one as `builder.Services.Add…(builder.Configuration)`, so none should differ.)

- [ ] **Step 6: Run the discovery tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~ModuleCompositionDiscoveryTests" 2>&1 | Select-Object -Last 10`
Expected: PASS (2/2).

- [ ] **Step 7: Run the full Ledger.Api test project (no regression from the additive classes)**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests 2>&1 | Select-Object -Last 10`
Expected: PASS, no failures.

- [ ] **Step 8: Commit**

```powershell
git add Modules/Shared/Accounting101.ModuleKit.Api/IModuleComposition.cs Modules/Shared/Accounting101.ModuleKit.Api/ModuleCompositionDiscovery.cs Modules/Shared/Accounting101.ModuleKit.Api/Accounting101.ModuleKit.Api.csproj Modules/Receivables/Accounting101.Receivables.Api/ReceivablesComposition.cs Modules/Payables/Accounting101.Payables.Api/PayablesComposition.cs Modules/Payroll/Accounting101.Payroll.Api/PayrollComposition.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashComposition.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationComposition.cs Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsComposition.cs Modules/Inventory/Accounting101.Inventory.Api/InventoryComposition.cs Backend/Accounting101.Ledger.Api.Tests/ModuleCompositionDiscoveryTests.cs
git commit -m @'
feat(modulekit): IModuleComposition contract + deps.json module discovery

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 2: Rewire the host — discovery in Program.cs, overlay glob, gitignore

**Files:**
- Modify: `Accounting101.Host/Program.cs`
- Modify: `Accounting101.Host/Accounting101.Host.csproj`
- Modify: `.gitignore` (repo root)

**Interfaces:**
- Consumes (from Task 1, exact): `builder.AddDiscoveredModules()` and `app.MapDiscoveredModuleEndpoints()` from `Accounting101.ModuleKit.Api.ModuleCompositionHostExtensions` (namespace `Accounting101.ModuleKit.Api` — Program.cs already imports it).
- Produces: nothing downstream — leaf task. The regression gate is the entire existing test suite booting `WebApplicationFactory<Program>` through the discovery path.

- [ ] **Step 1: Rewire Program.cs**

In `Accounting101.Host/Program.cs`:

Replace the using block (lines 1–11) with (only module usings drop; engine + ModuleKit stay):

```csharp
using Accounting101.Ledger.Api.Endpoints;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Api.Platform;
using Accounting101.ModuleKit.Api;
```

Replace the service wiring (the `AddLedgerEngine` comment + the 8 Add lines):

```csharp
// The engine. Installed modules compose themselves via discovery: every project-referenced Api
// assembly's IModuleComposition is found and added here. "Installed" = the module's Api project
// is referenced by Host.csproj (the Modules/ list, or a Modules.Private/ overlay).
builder.Services.AddLedgerEngine(builder.Configuration);
builder.AddDiscoveredModules();
```

Replace the seven module Map lines (`app.MapReceivablesEndpoints();` … `app.MapInventoryEndpoints();`) — the engine endpoint groups (`MapLedgerEndpoints` … `MapPlatformEndpoints`) stay exactly as they are — with:

```csharp
app.MapDiscoveredModuleEndpoints();
```

Everything else in the file (CORS, strict JSON, exception middleware, `UseModuleLedgerExceptionRelay`, auth, firm resolution, `public partial class Program;`) is untouched.

- [ ] **Step 2: Add the overlay glob to Host.csproj**

In `Accounting101.Host/Accounting101.Host.csproj`, update the header comment and append a second `ItemGroup` after the existing references:

Header comment becomes:

```xml
  <!-- The composition root: the one deployable. It references the engine and every installed module
       and wires them into a single process; modules are discovered (IModuleComposition), so
       "installed" = referenced below — the in-repo Modules/ list, or a proprietary overlay checkout
       under Modules.Private/. The engine never references this; this references the engine. -->
```

New ItemGroup (after the existing one):

```xml
  <ItemGroup>
    <!-- Proprietary add-on modules, overlaid as checkouts under Modules.Private/ (gitignored). An
         unmatched glob yields zero items, so public clones build identically without the folder.
         Convention: only *.Api projects are host-composed; a private module's tests stay in its
         own solution. -->
    <ProjectReference Include="..\Modules.Private\**\*.Api.csproj" />
  </ItemGroup>
```

- [ ] **Step 3: Gitignore the overlay folder**

Append to the repo-root `.gitignore` (read it first; add under its existing structure):

```
# Proprietary add-on module checkouts composed into the Host via the Modules.Private overlay glob.
Modules.Private/
```

- [ ] **Step 4: Run the full Ledger.Api test project — the discovery boot gate**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests 2>&1 | Select-Object -Last 10`
Expected: PASS, no failures. (Every endpoint test in the suite now proves its module was discovered, added, and mapped; `ApiFixture.SeedClientAsync`'s "all registered modules" default proves `ModuleRegistrar` still sees all seven `ModuleRegistration`s through the discovered path.)

- [ ] **Step 5: Run the full solution**

Run: `dotnet test Accounting101.slnx 2>&1 | Select-Object -Last 15`
Expected: PASS across all projects, no failures.

- [ ] **Step 6: Prove the empty-glob publish (what a public clone does)**

Run: `dotnet publish Accounting101.Host/Accounting101.Host.csproj -c Release -o "$env:TEMP\a101-host-publish-check" 2>&1 | Select-Object -Last 5; Remove-Item -Recurse -Force "$env:TEMP\a101-host-publish-check"`
Expected: publish succeeds with no `Modules.Private/` folder present (the folder does not exist in the working tree), proving the glob is a clean no-op.

- [ ] **Step 7: Commit**

```powershell
git add Accounting101.Host/Program.cs Accounting101.Host/Accounting101.Host.csproj .gitignore
git commit -m @'
feat(host): compose modules by discovery + Modules.Private overlay glob

Program.cs no longer names modules: IModuleComposition implementations are
discovered across project-referenced assemblies (deps.json), so a proprietary
module overlaid under gitignored Modules.Private/ composes in with no public
repo change.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

## After the tasks (supervisor, not subagents)

1. Final review of the whole branch diff (opus, per the established workflow).
2. Live JordanSoft smoke per the spec's Verification section: first add the `COPY Modules.Private*/ ./Modules.Private/` line to the api `dockerfile_inline` in `C:\Users\jorda\OneDrive\Documents\JordanSoft\deploy\docker-compose.yml` (immediately after the `COPY Modules/ ./Modules/` line), then deploy via `update.ps1`; confirm boot, unchanged `enabledModules` via `/clients/{id}/me/capabilities`, one endpoint per area answers, and the control DB still shows 7 seeded modules. Read-only — zero data footprint.
3. Merge to `master` and push.
