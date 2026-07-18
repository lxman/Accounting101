# Private-Module Composition (Modules.Private overlay + discovery hook) — Design

**Date:** 2026-07-18
**Context:** Enabler slice for proprietary add-on modules (first consumer: the private
`Accounting101.EInvoicing` module, per `ACCOUNTING101-EINVOICING-COORDINATION.md` in the
Pellucid repo root). This slice is **public-repo only** and depends on nothing from the
EInvoice extraction — it ships value on its own by removing the host's by-name module coupling.

## Goal

Let a module that is NOT in this repository compose into `Accounting101.Host` with zero public-repo
changes at install time. A paid module arrives as a private repo cloned into a gitignored
`Modules.Private/` folder; the host discovers and wires it automatically. Public cloners without
the folder build byte-identically to today.

Two pieces:

1. **A discovery hook** (`IModuleComposition` in ModuleKit.Api) so `Program.cs` stops naming
   modules — the last by-name coupling between host and modules.
2. **The overlay**: a glob `ProjectReference` in `Host.csproj` over `Modules.Private/**`, plus
   `.gitignore` and deployment-copy support.

## 1. The composition contract

New in `Accounting101.ModuleKit.Api`:

```csharp
/// <summary>Implemented once per module Api assembly. The host discovers every implementation
/// across its project-referenced assemblies at startup and calls both methods — this replaces
/// naming each module in Program.cs. "Installed" = the module's Api project is referenced by
/// the host (in-repo under Modules/, or overlaid under Modules.Private/).</summary>
public interface IModuleComposition
{
    void AddServices(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(IEndpointRouteBuilder app);
}
```

Each of the seven existing module Api projects gets a trivial implementation delegating to its
existing extensions (no behavior change), e.g. in `Accounting101.Receivables.Api`:

```csharp
public sealed class ReceivablesComposition : IModuleComposition
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddReceivables(configuration);
    public void MapEndpoints(IEndpointRouteBuilder app) => app.MapReceivablesEndpoints();
}
```

The existing `Add{Module}` / `Map{Module}Endpoints` extensions stay public and untouched — the
composition class is additive. A private module ships the same thing.

**All seven in-repo modules migrate to the hook in this slice.** One composition path, not two;
the existing modules and their full test suite become the proof that discovery works.

## 2. Discovery mechanics (and the compiler-pruning trap)

⭐ **Trap this design exists to dodge:** once `Program.cs` no longer references any module type,
the C# compiler prunes the unused assembly references — `Assembly.GetReferencedAssemblies()` on
the host would no longer list the modules, and nothing would load them. Discovery therefore
walks the **`DependencyContext`** (deps.json via `Microsoft.Extensions.DependencyModel`), where
every `ProjectReference` appears regardless of whether code uses it.

New in `Accounting101.ModuleKit.Api` (used only by the host):

```csharp
public static class ModuleCompositionDiscovery
{
    /// <summary>Every IModuleComposition across the host's project-referenced assemblies.
    /// Filter = RuntimeLibraries with Type == "project": exactly the ProjectReferences (in-repo
    /// modules and Modules.Private overlays alike, whatever they are named) and never NuGet
    /// packages. Deterministic order (library name) so registration order is stable.</summary>
    public static IReadOnlyList<IModuleComposition> DiscoverAll()
    {
        return DependencyContext.Default!.RuntimeLibraries
            .Where(l => l.Type == "project")
            .OrderBy(l => l.Name, StringComparer.Ordinal)
            .Select(TryLoad)                       // Assembly.Load(new AssemblyName(l.Name)); null on non-assembly libs
            .Where(a => a is not null)
            .SelectMany(a => a!.GetExportedTypes())
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IModuleComposition).IsAssignableFrom(t))
            .Select(t => (IModuleComposition)Activator.CreateInstance(t)!)
            .ToList();
    }
}
```

(Exact shape at implementer's discretion; the binding requirements are: deps.json-based,
`Type == "project"` filter, deterministic order, instantiate every public non-abstract
implementor, and a load failure on a project library that has no loadable assembly is skipped,
not fatal.)

**Accepted fail-loud behavior:** a composition class without a public parameterless constructor
(`MissingMethodException`) or a loaded module assembly with a missing transitive dependency
(`ReflectionTypeLoadException` from `GetExportedTypes`) takes the whole host down at startup.
Deliberate: a half-loadable module is a deployment fault to surface, not an endpoint to silently
drop. Also noted: discovery requires a deps.json — single-file/trimmed publish would silently
break it; the deployment model is standard framework-dependent `dotnet publish`.

Host convenience extensions (also ModuleKit.Api), discovered list computed ONCE and shared by
both phases:

```csharp
public static class ModuleCompositionHostExtensions
{
    public static void AddDiscoveredModules(this WebApplicationBuilder builder) { ... }
    public static void MapDiscoveredModuleEndpoints(this WebApplication app) { ... }
}
```

(`Microsoft.Extensions.DependencyModel` becomes a PackageReference of ModuleKit.Api — it is the
standard deps.json reader, first-party, tiny.)

Mechanically: `AddDiscoveredModules` runs `DiscoverAll()`, calls `AddServices` on each, and
stashes the list (e.g., registers it as a singleton `IReadOnlyList<IModuleComposition>`) so
`MapDiscoveredModuleEndpoints` maps the same instances without re-discovering.

### Program.cs after

The seven `using`s, seven `Add…` calls, and seven `Map…Endpoints` calls collapse to:

```csharp
builder.Services.AddLedgerEngine(builder.Configuration);
builder.AddDiscoveredModules();
...
app.MapLedgerEndpoints();
/* engine endpoint groups stay explicit — they are the engine, not modules */
app.MapDiscoveredModuleEndpoints();
```

Engine ordering is preserved: `AddLedgerEngine` before modules; engine endpoint groups mapped
before module endpoints (same relative order as today). Module-to-module order becomes
name-ordered instead of hand-ordered — safe because modules own disjoint route prefixes and
register independent services; nothing today depends on inter-module order.

The `Host.csproj` comment and Program.cs doc comment update: "installed = its line is present"
becomes "installed = its Api project is referenced (Modules/ list or Modules.Private/ overlay)".

## 3. The overlay

**`Host.csproj`** — after the existing explicit list, one glob item:

```xml
<!-- Proprietary add-on modules, overlaid as sibling checkouts under Modules.Private/ (gitignored).
     A glob that matches nothing yields zero items, so public clones build identically. -->
<ProjectReference Include="..\Modules.Private\**\*.Api.csproj" />
```

No `Exists` condition needed — an unmatched MSBuild glob is simply empty. The `*.Api.csproj`
suffix convention keeps a private repo's test projects out of the host. The seven explicit
in-repo references stay explicit (they are the product's own composition, reviewable in the
diff of Host.csproj).

**`.gitignore`** — add `Modules.Private/`.

**Private module authoring contract** (documented in the csproj comment + this spec; the
EInvoicing spec will follow it):
- Cloned (or junctioned) at `<repo>/Modules.Private/<ModuleName>/`.
- Endpoint/service assembly named `*.Api` and containing one public `IModuleComposition`.
- References `Accounting101.ModuleKit.Api` (for the interface) and whatever Contracts it needs —
  same dependency rules as in-repo modules.
- Registers its `ModuleRegistration` inside `AddServices` (exactly like in-repo modules), so
  `ModuleRegistrar` seeding, default-closed entitlement at `ModuleAccess.AuthorizeAsync`, and
  the admin modules screen all work with zero additional wiring.
- Has its own solution in its own repo for IDE/test workflows (the Simulation-repo pattern);
  `dotnet publish Accounting101.Host` needs no solution membership.

**Deployment** — the api image is built by `dotnet publish` inside Docker with selective COPY
(compose lives outside the repo, e.g. the JordanSoft deploy folder). One added line, using a
glob so it is a no-op when the folder is absent:

```dockerfile
COPY Modules.Private*/ ./Modules.Private/
```

This repo does not own that compose file; the change is documented here and applied to the
JordanSoft deploy inline Dockerfile during this slice's smoke.

## 4. What does NOT change

- The seven modules' `Add…`/`Map…` extensions, routes, services, tests — untouched apart from
  the additive composition class per Api project.
- Engine composition (`AddLedgerEngine`, engine endpoint groups) stays explicit in Program.cs.
- `ModuleRegistrar`, entitlement chokepoint, capability system: no changes — the private module
  rides the existing `ModuleRegistration` path.
- The Angular UI: out of scope. (A private module's screens are a genuinely separate problem —
  the EInvoicing spec will address its UI delivery; nothing in this slice constrains it.)
- Runtime plugin loading (drop-in DLLs into a shipped image): explicitly not built; this hook
  keeps that door open but build-time composition is the model.

## Tests

- **Existing suite is the primary proof:** `Ledger.Api.Tests` boots the real Host via
  `WebApplicationFactory<Program>`; every module endpoint test passing means discovery found,
  added, and mapped all seven modules. (The fixture's `SeedClientAsync` default — "all
  registered modules" — also exercises `ModuleRegistrar` seeding through the discovered path.)
- **New `ModuleCompositionDiscoveryTests`** (Ledger.Api.Tests):
  - `DiscoverAll` returns exactly the seven known module compositions (assert the set of
    implementing types/module keys — pins that pruning did not silently drop a module and that
    a future eighth in-repo module fails loudly here, updating the expectation).
  - Deterministic: two calls return the same ordered type sequence.
- **Glob no-op proof:** building/publishing Host with no `Modules.Private/` folder is what CI
  and every test run already does — nothing extra needed beyond the suite being green.
- Overlay positive-path (a real ninth module discovered from `Modules.Private/`) is proven by
  the EInvoicing module itself when it arrives; simulating it here with a synthetic project
  isn't worth the scaffolding. Noted as accepted coverage gap of this slice.

## Verification

- `dotnet test Accounting101.slnx` green (the whole suite runs against the discovery path).
- PROD `ng build` untouched (no UI change) — skip.
- Live JordanSoft smoke: deploy via `update.ps1` (compose gains the `Modules.Private*/` COPY
  line first); confirm the stack boots, `/clients/{id}/me/capabilities` shows the same
  `enabledModules`, one module endpoint from each area answers (e.g. cash accounts +
  reconciliation), and module seeding still upserts 7 modules in the control DB. Zero data
  footprint (read-only checks).

## Out of scope / deferred

- The EInvoicing module itself (own private-repo spec, waiting on the EInvoice extraction and
  coordination answers Q1/Q2/Q4/Q5).
- Private-module Angular UI delivery.
- Runtime plugin loading.
- Any entitlement/licensing enforcement beyond the existing default-closed module entitlement.
