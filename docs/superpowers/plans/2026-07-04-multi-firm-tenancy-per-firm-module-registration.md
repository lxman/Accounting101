# Multi-Firm Tenancy — Per-Firm Module Registration on Provisioning

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A newly provisioned firm's control DB gets the installed modules registered into it (with the same process-global secrets), so a firm admin can immediately entitle a client to a module and the module can authenticate and act against that firm.

**Architecture:** Module secrets are process-global — `AddModule` generates one secret per module at wiring time and puts it into BOTH the module's outbound `ModuleCredential` and the DI-registered `ModuleRegistration`. `ModuleRegistrar` (hosted service) already writes those `ModuleRegistration`s into the DEFAULT firm's control DB at startup. This change extracts that write into a `ControlStore.SeedModulesAsync` helper and calls it from `ProvisionFirm` too, targeting the newly created control DB. Because every firm shares the same in-process secrets, the module's existing outbound credential authenticates against any firm's control DB with no per-firm secret plumbing.

**Tech Stack:** .NET (C#), ASP.NET Core minimal APIs, MongoDB driver, xUnit + EphemeralMongo (`SharedMongo`), `WebApplicationFactory<Program>`.

## Global Constraints

- Modules are registered `Enabled = true` (installed ≠ entitled; the per-CLIENT `EnabledModules` gate from Phase 3b decides whether a client may use a module).
- Seeding is idempotent (`RegisterModuleAsync` is an upsert keyed by module key).
- Provisioning keeps its **seed-then-register** ordering (Phase 3a invariant): seed the new control DB (capability sets AND now modules) BEFORE `PlatformStore.RegisterFirmAsync` commits the firm, so a seed failure leaves no registered-but-unusable firm.
- No secret is ever logged or returned in a response.
- Stage explicit paths only (never `git add -A`); leave the pre-existing uncommitted noise (`*.Tests.csproj`, `environment.ts`, `.slnx`) alone.

---

## Task 1: Seed installed modules into a provisioned firm's control DB

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs` — add `SeedModulesAsync`.
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/ModuleRegistrar.cs` — call the helper (DRY).
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs` — `ProvisionFirm` seeds modules.
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs` — add one `[Fact]` (append to the existing class).

**Interfaces:**
- Produces: `ControlStore.SeedModulesAsync(IEnumerable<ModuleRegistration> modules, CancellationToken) → Task`.
- `ProvisionFirm` gains an `IEnumerable<ModuleRegistration> modules` DI parameter.

- [ ] **Step 1: Write the failing test.** Append to `PlatformFirmsTests.cs`. A freshly provisioned firm's control DB must contain the same module registrations (key + secret + enabled) that the default firm's control DB holds — proving the provisioned firm is immediately module-usable (matching keys AND secrets means the modules' existing in-process credentials authenticate against it). Mirror the operator-token + provisioning pattern already used in this file (read the file first to match its exact helpers — operator client, how it reads `FirmResponse`, and how it opens a control DB via `fixture.Mongo.GetDatabase(...)`).

```csharp
[Fact]
public async Task Provisioning_registers_installed_modules_into_the_new_firms_control_db()
{
    // The default firm's control DB was seeded at startup by ModuleRegistrar; use it as the source of truth
    // for the installed module set (keys + process-global secrets).
    ControlStore defaultControl = fixture.Control();
    IReadOnlyList<ModuleRegistration> expected = await defaultControl.ListModulesAsync();
    Assert.NotEmpty(expected); // the host installs modules; sanity guard

    HttpClient op = /* operator client with ("platform","true") — match this file's existing pattern */;
    HttpResponseMessage provisioned = await op.PostAsJsonAsync(
        "/platform/firms", new ProvisionFirmRequest { Name = "Modules Firm" });
    Assert.Equal(HttpStatusCode.Created, provisioned.StatusCode);
    FirmResponse firm = (await provisioned.Content.ReadFromJsonAsync<FirmResponse>())!;

    ControlStore newControl = new(fixture.Mongo.GetDatabase(firm.ControlDatabase));
    IReadOnlyList<ModuleRegistration> actual = await newControl.ListModulesAsync();

    // Same modules, same secrets, all enabled — the new firm is immediately module-usable.
    Assert.Equal(
        expected.OrderBy(m => m.Key).Select(m => (m.Key, m.Secret, m.Enabled)),
        actual.OrderBy(m => m.Key).Select(m => (m.Key, m.Secret, m.Enabled)));
}
```

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FullyQualifiedName~PlatformFirmsTests`
Expected: FAIL — the new firm's control DB has no modules (empty `actual`), so the sequence assertion fails.

- [ ] **Step 3: Add the `SeedModulesAsync` helper.** In `ControlStore.cs`, next to `RegisterModuleAsync`:

```csharp
/// <summary>Idempotently upsert each installed module's registration into this control DB. Used both by
/// the startup registrar (default firm) and by firm provisioning (a newly created firm's control DB), so
/// a provisioned firm holds the same module set + process-global secrets as the default firm.</summary>
public async Task SeedModulesAsync(IEnumerable<ModuleRegistration> modules, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(modules);
    foreach (ModuleRegistration module in modules)
        await RegisterModuleAsync(module, cancellationToken);
}
```

- [ ] **Step 4: Use the helper in `ModuleRegistrar`.** Replace the `foreach` loop in `ModuleRegistrar.StartAsync` with:

```csharp
await control.SeedModulesAsync(modules, cancellationToken);
```

(Keeps behavior identical; the loop moves into the shared helper.)

- [ ] **Step 5: Seed modules in `ProvisionFirm`.** In `PlatformEndpoints.cs`, add `IEnumerable<ModuleRegistration> modules` to the `ProvisionFirm` handler's parameter list, and call `SeedModulesAsync` right after the capability-set seed, still before `RegisterFirmAsync`:

```csharp
ControlStore control = new(client.GetDatabase(controlDatabase));
await control.SeedBuiltinCapabilitySetsAsync(cancellationToken);
await control.SeedModulesAsync(modules, cancellationToken);

await platform.RegisterFirmAsync(firm, cancellationToken);
```

(Add the `using` for `Accounting101.Ledger.Api.Control` if not already present — `ControlStore`/`ModuleRegistration` live there. `ModuleRegistration` is the DI-registered type; the minimal-API handler resolves `IEnumerable<ModuleRegistration>` from the container.)

- [ ] **Step 6: Run the focused test to verify pass.**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FullyQualifiedName~PlatformFirmsTests`
Expected: PASS (existing PlatformFirmsTests + the new one).

- [ ] **Step 7: Run the full Api.Tests project (the `ModuleRegistrar` refactor is engine-wide).**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: all PASS (was 332/332; this adds 1).

- [ ] **Step 8: Commit.**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ControlStore.cs Backend/Accounting101.Ledger.Api/Hosting/ModuleRegistrar.cs Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs
git commit -m "feat(tenancy): register installed modules into a provisioned firm's control DB"
```

---

## Self-Review

**Spec coverage:** the deferred phase-3 item ("provisioning should also seed module registrations into its control DB") → Task 1. ✓
**Placeholder scan:** the test's operator-client line is intentionally left to match the file's existing pattern — the implementer reads the file to fill it; everything else is concrete. ✓
**Type consistency:** `SeedModulesAsync(IEnumerable<ModuleRegistration>, CancellationToken)` used identically in `ModuleRegistrar` and `ProvisionFirm`. ✓
