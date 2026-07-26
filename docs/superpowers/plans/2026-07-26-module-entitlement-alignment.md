# Module Entitlement Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every surface that answers "can this client use module X?" answer with `installed ∩ entitled`, so an entitlement this deployment never composed can no longer render nav to routes that 404.

**Architecture:** One new DI singleton, `InstalledModules`, carries the module keys the host actually composed — derived at resolution time from the `ModuleRegistration` singletons that every `AddModule` call already contributes. The two read paths that drive UI (`GET /clients/{id}/me/capabilities`, `GET /clients/{id}/posting-accounts`) intersect stored entitlement against it; the admin write path (`PUT /admin/clients/{id}/modules`) rejects uninstalled keys 422 all-or-nothing; a new admin read path (`GET /admin/clients/{id}/modules`) keeps entitlement raw and adds installed, so support can tell "not sold" from "not installed". Stored `EnabledModules` is never rewritten — the intersection is a view, not a migration.

**Tech Stack:** .NET 10 minimal APIs, ASP.NET Core DI, MongoDB (control DB), xUnit + `WebApplicationFactory<Program>` + EphemeralMongo. No Angular change. No new dependency.

## Global Constraints

- .NET 10; Angular 22; build with **0 new warnings**; TDD; commit per task; **stage explicit file lists** (`git add <paths>`, never `git add -A` / `git add .`).
- `UI/Angular/src/app/core/api/environment.ts` carries a long-lived local dev-config edit that **must stay uncommitted**. Never stage that file.
- No new npm or NuGet dependency.
- No module gains a reference to another module. `InstalledModules` lives beside the existing module hosting types in `Backend/Accounting101.Ledger.Api/Hosting/`, which every module already references.
- Solution file is `Accounting101.slnx` (**not** `.sln`).
- Pre-existing and out of scope: 4 NU1902/NU1903 NuGet advisories from `Accounting101.TestSupport` transitive deps; 13 NG04002 console errors in the Angular suite.
- Branch: `feat/module-entitlement-alignment` (already checked out, spec committed at `7049716`).
- Test command used throughout:
  `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`

## Spec deviation you must know about (already decided — do not re-litigate)

The spec's consumer table names `AdminContracts.cs:41` as "client detail". That line is actually the
`EnabledModules` parameter of `CapabilitiesResponse`, and **no admin client-detail endpoint exposing
modules exists today**. The spec's *intent* is unambiguous ("this is where support distinguishes 'not
sold' from 'not installed'"), so Task 6 builds that surface as a new
`GET /admin/clients/{clientId}/modules` returning both lists. The line reference was stale; the
requirement is not.

## File Structure

**Created:**
- `Backend/Accounting101.Ledger.Api/Hosting/InstalledModules.cs` — the seam. A dumb value object over a key set. Keys only, never `ModuleRegistration` (which carries `Secret`).
- `Backend/Accounting101.Ledger.Api.Tests/ModuleAvailabilityTests.cs` — every intersection assertion on the two read paths, plus "entitlement survives uninstall".

**Modified:**
- `Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs` — register the `InstalledModules` factory inside `AddModule`.
- `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs` — register the *same* factory, so a host composing zero modules still resolves an empty set instead of throwing.
- `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs` — intersect.
- `Backend/Accounting101.Ledger.Api/Endpoints/PostingAccountEndpoints.cs` — intersect.
- `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` — 422 write guard against DI truth; new GET.
- `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` — `ClientModulesResponse` gains `InstalledModuleKeys`.
- `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs` — the substitution hook (`ClientWithInstalledModules`).
- `Backend/Accounting101.Ledger.Api.Tests/ModuleHostingTests.cs` — DI-level tests for the seam.
- `Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs` — 400 → 422, plus the all-or-nothing assertion.
- `Backend/Accounting101.Ledger.Api.Tests/ModuleCompositionDiscoveryTests.cs` — exact-set → superset.

**Untouched, deliberately:** `ModuleAccess.cs` (authorization — intersecting is a no-op there, an
uninstalled module's code is not running to present a credential); `PlatformEndpoints.cs` (the usage
meter bills what was *sold*); the entire Angular app.

---

### Task 1: The `InstalledModules` seam and its registration

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Hosting/InstalledModules.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs:49-54`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs:70`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModuleHostingTests.cs`

**Interfaces:**
- Consumes: `ModuleRegistration` (`Accounting101.Ledger.Api.Control`) — its `Key` property only. `AddModule` (`Accounting101.Ledger.Api.Hosting.ModuleHostingExtensions`).
- Produces: `Accounting101.Ledger.Api.Hosting.InstalledModules`, a sealed class with a public constructor `InstalledModules(IEnumerable<string> keys)`, a `IReadOnlySet<string> Keys { get; }` property, and a `bool Has(string moduleKey)` method. Registered as a singleton resolvable from any endpoint handler. Tasks 3–6 all inject it.

- [ ] **Step 1: Write the failing tests**

Append these two facts inside the existing `ModuleHostingTests` class in
`Backend/Accounting101.Ledger.Api.Tests/ModuleHostingTests.cs` (before the closing `}`):

```csharp
    /// <summary>
    /// The registration inside AddModule is a TryAdd, so only the FIRST AddModule call registers the
    /// factory — but the factory does not RUN until first resolution, long after every AddModule has
    /// completed. The single instance must therefore see every module, not just the first. This test is
    /// the guard against "fixing" it by computing the key set eagerly at registration time.
    /// </summary>
    [Fact]
    public void Installed_modules_sees_every_module_not_just_the_first_registered()
    {
        ServiceCollection services = new();
        services.AddModule(new ModuleIdentity("payables"), "Payables");
        services.AddModule(new ModuleIdentity("receivables"), "Receivables");

        using ServiceProvider provider = services.BuildServiceProvider();
        InstalledModules installed = provider.GetRequiredService<InstalledModules>();

        Assert.Equal(
            ["payables", "receivables"],
            installed.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.True(installed.Has("payables"));
        Assert.True(installed.Has("receivables"));
        Assert.False(installed.Has("einvoicing"));
    }

    /// <summary>
    /// A host that composes no module at all must still resolve an (empty) InstalledModules rather than
    /// throw — the engine registers the same late-bound factory, so first-wins TryAdd ordering between
    /// the engine and AddModule is irrelevant.
    /// </summary>
    [Fact]
    public void An_engine_with_no_modules_resolves_an_empty_installed_set()
    {
        ServiceCollection services = new();
        services.AddLedgerEngine(new ConfigurationBuilder().Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Empty(provider.GetRequiredService<InstalledModules>().Keys);
    }
```

`ModuleHostingTests.cs` already has `using Accounting101.Ledger.Api.Hosting;`,
`using Microsoft.Extensions.Configuration;` and `using Microsoft.Extensions.DependencyInjection;` at
the top — no new usings needed.

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ModuleHostingTests"
```
Expected: **compile error** — `The type or namespace name 'InstalledModules' could not be found`. A
build failure is the correct red for a type that does not exist yet.

- [ ] **Step 3: Create the type**

Create `Backend/Accounting101.Ledger.Api/Hosting/InstalledModules.cs`:

```csharp
namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// The module keys this deployment actually composed — the installation half of
/// "availability = installed ∩ entitled". Derived from the ModuleRegistration singletons every
/// <c>AddModule</c> call contributes, so it cannot drift from what the host built.
///
/// Taken from DI, not from the control database: ModuleRegistrar seeds a copy of each registration
/// into the DEFAULT firm's control DB only, so under multi-firm that copy can be partial or stale.
/// DI is the in-process truth, costs nothing, and needs no read.
///
/// Keys and nothing else, deliberately: ModuleRegistration carries the module's shared Secret, and
/// handing that object to a read path puts a credential one careless serialization from a response
/// body.
///
/// A dumb value object with a public constructor — discovery stays in <c>AddModule</c>, which already
/// knows the key. That is what makes the "not installed" branch testable at all: the test process
/// always has every module composed in, so tests substitute this instance instead.
/// </summary>
public sealed class InstalledModules(IEnumerable<string> keys)
{
    /// <summary>Every module key composed into this host. Ordinal comparison — keys are stable
    /// lowercase identifiers ("payables", "einvoicing"), matched the same way everywhere else.</summary>
    public IReadOnlySet<string> Keys { get; } = keys.ToHashSet(StringComparer.Ordinal);

    public bool Has(string moduleKey) => Keys.Contains(moduleKey);
}
```

- [ ] **Step 4: Register it inside `AddModule`**

In `Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs`, insert this immediately
after the `services.AddSingleton(new ModuleRegistration { … });` block (currently lines 49-54) and
before the `TryAddEnumerable` registrar line:

```csharp
        // The installation half of "availability = installed ∩ entitled". TryAdd, so the FIRST
        // AddModule registers this factory and every later call is a no-op — but the factory does not
        // RUN until first resolution, at request time, long after every AddModule has completed. So the
        // single instance sees ALL modules, not just the first. Do NOT "fix" this by computing the key
        // set eagerly here: that would capture only the modules registered so far.
        services.TryAddSingleton(sp => new InstalledModules(
            sp.GetServices<ModuleRegistration>().Select(m => m.Key)));
```

The file already has `using Accounting101.Ledger.Api.Control;` and
`using Microsoft.Extensions.DependencyInjection.Extensions;` — no new usings needed.

- [ ] **Step 5: Register the same factory in the engine**

In `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`, insert immediately after the
`services.TryAddScoped<IModuleAuthenticator, CredentialModuleAuthenticator>();` line (currently line
70):

```csharp
        // Installed-module keys for "availability = installed ∩ entitled". AddModule registers this same
        // TryAdd factory; registering it here too means a host that composes NO module still resolves an
        // empty set instead of throwing. Both registrations are the identical late-bound factory over
        // GetServices<ModuleRegistration>(), so first-wins ordering is irrelevant — the same reasoning as
        // the TryAddScoped<IModuleAuthenticator> above.
        services.TryAddSingleton(sp => new InstalledModules(
            sp.GetServices<ModuleRegistration>().Select(m => m.Key)));
```

The file already has `using Accounting101.Ledger.Api.Control;` and
`using Microsoft.Extensions.DependencyInjection.Extensions;` — no new usings needed.

- [ ] **Step 6: Run the tests to verify they pass**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ModuleHostingTests"
```
Expected: PASS, all facts in the class, **0 new warnings**.

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Hosting/InstalledModules.cs Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs Backend/Accounting101.Ledger.Api.Tests/ModuleHostingTests.cs
git commit -m "feat(modules): InstalledModules seam — the installed half of availability"
```

---

### Task 2: Capabilities returns installed ∩ entitled

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs:23-40`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs` (add the substitution hook)
- Create: `Backend/Accounting101.Ledger.Api.Tests/ModuleAvailabilityTests.cs`

**Interfaces:**
- Consumes: `InstalledModules` with `bool Has(string)` and `IReadOnlySet<string> Keys` (Task 1). `CapabilitiesResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin, IReadOnlyList<string> EnabledModules, ApprovalMode ApprovalMode, string BaseCurrency)` — shape unchanged; only what goes in the `EnabledModules` slot changes.
- Produces: `ApiFixture.ClientWithInstalledModules(Guid userId, IEnumerable<string> installedKeys, params (string Type, string Value)[] claims) → HttpClient`. Tasks 3, 4 and 5 all use it.

**Why the fixture hook is in this task and not its own:** it has no independently reviewable
deliverable — it is scaffolding the first intersection test needs.

- [ ] **Step 1: Add the substitution hook to the fixture**

In `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`, add these usings to the top of the file
(keeping the existing ones):

```csharp
using Accounting101.Ledger.Api.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
```

Then add this method to the `ApiFixture` class, immediately after `AnonymousClient()` (currently line 73):

```csharp
    /// <summary>
    /// An HttpClient from a host whose <see cref="InstalledModules"/> is replaced with exactly
    /// <paramref name="installedKeys"/>. This is the ONLY way to exercise the "not installed" branch:
    /// the test process's deps.json includes every module project, so a module can never genuinely be
    /// absent here.
    ///
    /// <c>ConfigureTestServices</c> runs AFTER the host's own registrations, and the engine registers
    /// InstalledModules with <c>TryAddSingleton</c> (first-wins) — so this must <c>Replace</c> the
    /// descriptor, not <c>Add</c> a second one. The derived factory is tracked by its parent and
    /// disposed with the fixture.
    /// </summary>
    public HttpClient ClientWithInstalledModules(
        Guid userId, IEnumerable<string> installedKeys, params (string Type, string Value)[] claims)
    {
        InstalledModules installed = new(installedKeys);
        HttpClient client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.Replace(ServiceDescriptor.Singleton(installed))))
            .CreateClient();
        string token = DevToken.Encode(new DevTokenPayload(
            userId, "Installed-Subset Member", claims.Select(c => new DevClaim(c.Type, c.Value)).ToList()));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(DevTokenDefaults.Scheme, token);
        return client;
    }
```

- [ ] **Step 2: Write the failing tests**

Create `Backend/Accounting101.Ledger.Api.Tests/ModuleAvailabilityTests.cs`:

```csharp
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Availability = installed ∩ entitled. Every surface that answers "can this client use module X?"
/// must answer with the intersection; a client entitled to a module this deployment never composed
/// would otherwise get nav links to routes nobody registered.
/// </summary>
public sealed class ModuleAvailabilityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Capabilities_lists_an_entitled_module_that_is_installed()
    {
        SeededClient c = await fixture.SeedClientAsync("AvailInstalled", role: LedgerRole.Admin);
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash", "reconciliation"]);

        HttpClient http = fixture.ClientWithInstalledModules(c.UserId, ["cash", "reconciliation"]);
        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Equal(["cash", "reconciliation"], caps.EnabledModules);
    }

    [Fact]
    public async Task Capabilities_omits_an_entitled_module_that_is_not_installed()
    {
        SeededClient c = await fixture.SeedClientAsync("AvailAbsent", role: LedgerRole.Admin);
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash", "reconciliation"]);

        // This deployment composed "cash" only — "reconciliation" was sold but is not here.
        HttpClient http = fixture.ClientWithInstalledModules(c.UserId, ["cash"]);
        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Equal(["cash"], caps.EnabledModules);
    }

    [Fact]
    public async Task Capabilities_omits_an_installed_module_the_client_is_not_entitled_to()
    {
        // The default-closed property, pinned: installation alone never grants availability.
        SeededClient c = await fixture.SeedClientAsync("AvailNotSold", role: LedgerRole.Admin);
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash"]);

        HttpClient http = fixture.ClientWithInstalledModules(c.UserId, ["cash", "reconciliation"]);
        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Equal(["cash"], caps.EnabledModules);
    }

    [Fact]
    public async Task Entitlement_survives_a_module_being_uninstalled()
    {
        // The intersection is a VIEW, not a migration: a redeploy must never silently delete what a
        // customer bought. Reading availability through a host lacking the module must not rewrite the
        // stored entitlement — re-installing has to restore the feature.
        SeededClient c = await fixture.SeedClientAsync("AvailSurvives", role: LedgerRole.Admin);
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash", "reconciliation"]);

        HttpClient http = fixture.ClientWithInstalledModules(c.UserId, ["cash"]);
        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;
        Assert.DoesNotContain("reconciliation", caps.EnabledModules);

        ClientRegistration? stored = await fixture.Control().GetClientAsync(c.ClientId);
        Assert.NotNull(stored);
        Assert.Contains("reconciliation", stored!.EnabledModules);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ModuleAvailabilityTests"
```
Expected: `Capabilities_omits_an_entitled_module_that_is_not_installed` FAILS with
`Assert.Equal() Failure: … Expected: ["cash"] Actual: ["cash", "reconciliation"]`. The other three
pass already (they assert properties the current code happens to satisfy — they are regression pins,
which is why they are here rather than omitted).

- [ ] **Step 4: Intersect in the handler**

In `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs`, add
`using Accounting101.Ledger.Api.Hosting;` to the usings, then replace the whole `GetMyCapabilities`
method body (lines 23-40) with:

```csharp
    private static async Task<IResult> GetMyCapabilities(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        InstalledModules installed, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);
        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null)
            return Results.Forbid();

        bool deploymentAdmin = user.HasClaim("admin", "true");
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);

        // Availability = installed ∩ entitled. An entitlement this deployment never composed would
        // otherwise render nav to routes nobody registered — the guard reads THIS list. Entitlement
        // order is preserved, and the stored entitlement is left untouched: the raw value stays
        // readable at GET /admin/clients/{id}/modules, which is where support needs it.
        IReadOnlyList<string> available = client is null
            ? []
            : client.EnabledModules.Where(installed.Has).ToList();

        return Results.Ok(new CapabilitiesResponse(
            membership.Capabilities,
            membership.GrantedRoles.Select(r => r.ToString()).ToList(),
            deploymentAdmin,
            available,
            client is null ? ApprovalMode.TwoPerson : ApprovalPolicy.ModeOf(client),
            client?.BaseCurrency ?? "USD"));
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ModuleAvailabilityTests|FullyQualifiedName~CapabilitiesTests"
```
Expected: PASS, including the pre-existing `Capabilities_includes_the_clients_enabled_modules`
(`cash` and `reconciliation` are both installed in the test process, so the intersection is a no-op
there and its ordered assertion still holds).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs Backend/Accounting101.Ledger.Api.Tests/ModuleAvailabilityTests.cs
git commit -m "feat(capabilities): report installed ∩ entitled, not raw entitlement"
```

---

### Task 3: Posting-account slots respect installation

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/PostingAccountEndpoints.cs:23-48`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ModuleAvailabilityTests.cs` (append)

**Interfaces:**
- Consumes: `InstalledModules.Has(string)` (Task 1); `ApiFixture.ClientWithInstalledModules(Guid, IEnumerable<string>, params (string, string)[])` (Task 2).
- Produces: nothing new. `PostingAccountsResponse(IReadOnlyList<PostingAccountSlotResponse> Slots)` and `PostingAccountSlotResponse(string ModuleKey, string SlotKey, string Label, …, Guid? CurrentAccountId)` are unchanged.

- [ ] **Step 1: Write the failing tests**

Append these two facts to `ModuleAvailabilityTests` (before the class's closing `}`):

```csharp
    [Fact]
    public async Task Posting_account_slots_are_offered_for_an_installed_entitled_module()
    {
        SeededClient c = await fixture.SeedClientAsync("SlotsInstalled");
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash"]);
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(
            userId, c.ClientId, [], [Capabilities.AdminPostingAccounts, Capabilities.GlRead]);

        HttpClient http = fixture.ClientWithInstalledModules(userId, ["cash"]);
        PostingAccountsResponse response = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;

        Assert.Equal("cash", Assert.Single(response.Slots).ModuleKey);
    }

    [Fact]
    public async Task Posting_account_slots_are_not_offered_for_an_uninstalled_module()
    {
        // Offering a config slot for a module this deployment never composed is the same lie the
        // capabilities payload used to tell, in a different place.
        SeededClient c = await fixture.SeedClientAsync("SlotsAbsent");
        await fixture.Control().SetClientModulesAsync(c.ClientId, ["cash"]);
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(
            userId, c.ClientId, [], [Capabilities.AdminPostingAccounts, Capabilities.GlRead]);

        HttpClient http = fixture.ClientWithInstalledModules(userId, ["reconciliation"]);
        PostingAccountsResponse response = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;

        Assert.Empty(response.Slots);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ModuleAvailabilityTests"
```
Expected: `Posting_account_slots_are_not_offered_for_an_uninstalled_module` FAILS —
`Assert.Empty() Failure: Collection: [PostingAccountSlotResponse { ModuleKey = "cash", … }]`.

- [ ] **Step 3: Intersect in the handler**

In `Backend/Accounting101.Ledger.Api/Endpoints/PostingAccountEndpoints.cs`, add
`using Accounting101.Ledger.Api.Hosting;` to the usings. Then change the `GetPostingAccounts`
signature (line 23-25) to take the singleton:

```csharp
    private static async Task<IResult> GetPostingAccounts(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        PostingAccountStore store, InstalledModules installed, CancellationToken ct)
```

and replace line 33 (`HashSet<string> enabled = [.. client.EnabledModules];`) with:

```csharp
        // Availability = installed ∩ entitled: only offer config slots for modules that are both sold
        // to this client AND composed into this deployment.
        HashSet<string> available = [.. client.EnabledModules.Where(installed.Has)];
```

and the filter on line 42 (`.Where(s => enabled.Contains(s.ModuleKey))`) with:

```csharp
            .Where(s => available.Contains(s.ModuleKey))
```

Leave `SetPostingAccounts`, `GetRevenueCategories` and `SetRevenueCategories` alone — they are keyed
by `PostingAccountSlots.ForModule` / `PostingAccountCategoryMaps.ConfigSectionFor`, which already 422
an unknown module key, and they are admin-configuration writes rather than availability answers.

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ModuleAvailabilityTests|FullyQualifiedName~PostingAccountEndpointTests"
```
Expected: PASS, including the whole pre-existing `PostingAccountEndpointTests` class (every module it
touches is installed in the test process, so the intersection is a no-op there).

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/PostingAccountEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/ModuleAvailabilityTests.cs
git commit -m "feat(posting-accounts): offer slots only for installed ∩ entitled modules"
```

---

### Task 4: `SetClientModules` rejects uninstalled keys, all or nothing

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs:158-180`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs:28-39`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs` (append)

**Interfaces:**
- Consumes: `InstalledModules.Has(string)` (Task 1). `SetClientModulesRequest(IReadOnlyList<string> ModuleKeys)`, unchanged.
- Produces: `PUT /admin/clients/{clientId:guid}/modules` now answers **422** (was 400) when any key is not installed, and the problem detail names **every** absent key. Nothing is persisted on rejection.

**Behavioural change to be deliberate about:** the guard's source moves from
`control.ListModulesAsync()` (the control DB's `modules` collection, which `ModuleRegistrar` seeds
only into the *default firm* — partial or stale under multi-firm) to `InstalledModules` (in-process
DI truth), and the status code moves 400 → 422 per the spec. The existing
`Unknown_module_key_is_rejected` test asserts 400 and must be updated, not deleted.

- [ ] **Step 1: Update the existing test and write the new ones**

In `Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs`, replace the whole
`Unknown_module_key_is_rejected` fact (currently lines 28-39) with:

```csharp
    [Fact]
    public async Task An_uninstalled_module_key_is_rejected_and_named()
    {
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        HttpClient admin = fixture.AdminClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules",
            new SetClientModulesRequest(["not-a-real-module"]));

        // 422, not 400: the request is well-formed, it just asks to sell something this deployment
        // cannot run. The body names the key so an operator can act on it.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("not-a-real-module", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_mix_of_installed_and_uninstalled_keys_persists_nothing()
    {
        // A half-applied module set is worse than either clean outcome: the operator would believe the
        // whole request landed. All or nothing.
        ControlStore control = fixture.Control();
        SeededClient client = await fixture.SeedClientAsync(enabledModules: []);
        HttpClient admin = fixture.AdminClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/admin/clients/{client.ClientId}/modules",
            new SetClientModulesRequest(["receivables", "not-a-real-module"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not-a-real-module", body);
        Assert.DoesNotContain("receivables", body);

        ClientRegistration? saved = await control.GetClientAsync(client.ClientId);
        Assert.NotNull(saved);
        Assert.Empty(saved!.EnabledModules);
    }
```

Leave `Deployment_admin_sets_modules_and_they_persist`, `Unknown_client_is_not_found` and
`A_plain_member_without_admin_client_is_forbidden` exactly as they are — `receivables` is genuinely
installed in the test process, so the happy path is unaffected.

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ClientModulesTests"
```
Expected: both new facts FAIL with
`Assert.Equal() Failure: Expected: UnprocessableEntity Actual: BadRequest`.

- [ ] **Step 3: Rewrite the guard**

In `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs`, add
`using Accounting101.Ledger.Api.Hosting;` to the usings, then replace the whole `SetClientModules`
method (currently lines 158-180) with:

```csharp
    private static async Task<IResult> SetClientModules(
        Guid clientId, SetClientModulesRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, InstalledModules installed,
        CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminClient, actorFactory, control, cancellationToken))
            return Results.Forbid();

        if (await control.GetClientAsync(clientId, cancellationToken) is null)
            return Results.NotFound();

        IReadOnlyList<string> keys = request.ModuleKeys ?? [];

        // Refuse to SELL what this deployment cannot RUN. Defence in depth on top of the read-time
        // intersection, not a replacement for it: validating here gives entitled ⊆ installed only until
        // the next deployment changes what is installed, and nothing re-validates stored rows.
        //
        // Taken from DI (what the host actually composed) rather than the control DB's modules
        // collection: ModuleRegistrar seeds that only into the default firm, so it can be partial or
        // stale under multi-firm.
        //
        // All keys are checked before anything is written — a half-applied module set is worse than
        // either clean outcome.
        string[] absent = [.. keys.Where(k => !installed.Has(k))];
        if (absent.Length > 0)
            return Results.Problem(
                $"Module(s) not installed in this deployment: {string.Join(", ", absent)}.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        await control.SetClientModulesAsync(clientId, keys, cancellationToken);
        return Results.Ok(new ClientModulesResponse(clientId, keys));
    }
```

(The third `ClientModulesResponse` argument arrives in Task 5; leave this two-argument call as-is for
now — it compiles against today's record.)

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ClientModulesTests"
```
Expected: PASS, all five facts.

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs
git commit -m "feat(admin): reject entitling a client to an uninstalled module (422, all-or-nothing)"
```

---

### Task 5: Admin client-modules read shows sold *and* installed

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs:67`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` (route map ~line 30, new handler, PUT response)
- Test: `Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs` (append)

**Interfaces:**
- Consumes: `InstalledModules.Keys` (Task 1); `ApiFixture.ClientWithInstalledModules(...)` (Task 2); `AdminAuthorization.MayAsync(ClaimsPrincipal, Guid, string, IActorFactory, ControlStore, CancellationToken)`.
- Produces: `ClientModulesResponse(Guid ClientId, IReadOnlyList<string> ModuleKeys, IReadOnlyList<string> InstalledModuleKeys)` — a **third positional member**, so the existing `PUT` construction must be updated in the same task. New route `GET /admin/clients/{clientId:guid}/modules`.

**Why this surface exists:** under this design a misconfigured install (entitled, not installed) looks
identical to an unsold one from the UI — the right failure for an end user, the wrong one for
support. This is the one place that keeps entitlement raw so the two can be told apart.

- [ ] **Step 1: Write the failing tests**

Append to `ClientModulesTests` (before the class's closing `}`):

```csharp
    [Fact]
    public async Task Admin_read_shows_raw_entitlement_beside_what_is_installed()
    {
        SeededClient client = await fixture.SeedClientAsync(enabledModules: ["cash", "reconciliation"]);
        // A deployment that sold both but composed only "cash" — the misconfiguration the UI hides.
        HttpClient admin = fixture.ClientWithInstalledModules(
            Guid.NewGuid(), ["cash"], ("admin", "true"));

        ClientModulesResponse body = (await admin.GetFromJsonAsync<ClientModulesResponse>(
            $"/admin/clients/{client.ClientId}/modules"))!;

        Assert.Equal(client.ClientId, body.ClientId);
        // Entitlement is NOT intersected here — support must see "sold but absent".
        Assert.Equal(["cash", "reconciliation"], body.ModuleKeys);
        Assert.Equal(["cash"], body.InstalledModuleKeys);
    }

    [Fact]
    public async Task Admin_read_of_an_unknown_client_is_not_found()
    {
        HttpClient admin = fixture.AdminClient();
        HttpResponseMessage response = await admin.GetAsync($"/admin/clients/{Guid.NewGuid()}/modules");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_read_is_forbidden_to_a_plain_member()
    {
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.Clerk, enabledModules: []);
        HttpResponseMessage response = await client.Http.GetAsync(
            $"/admin/clients/{client.ClientId}/modules");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ClientModulesTests"
```
Expected: **compile error** — `'ClientModulesResponse' does not contain a definition for
'InstalledModuleKeys'`.

- [ ] **Step 3: Widen the contract**

In `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`, replace line 67 with:

```csharp
/// <summary>A client's module picture for support. <c>ModuleKeys</c> is the RAW stored entitlement —
/// what was sold, never intersected — and <c>InstalledModuleKeys</c> is what this deployment actually
/// composed. Their difference is precisely the "entitled but not installed" misconfiguration that is
/// invisible everywhere else, because every user-facing surface reports availability
/// (installed ∩ entitled) and so cannot distinguish it from "not sold".</summary>
public sealed record ClientModulesResponse(
    Guid ClientId, IReadOnlyList<string> ModuleKeys, IReadOnlyList<string> InstalledModuleKeys);
```

- [ ] **Step 4: Add the route, the handler, and fix the PUT construction**

In `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs`:

(a) Add the GET beside the existing PUT in `MapAdminEndpoints` (immediately after the
`perClient.MapPut("/clients/{clientId:guid}/modules", SetClientModules);` line):

```csharp
        perClient.MapGet("/clients/{clientId:guid}/modules", GetClientModules);
```

(b) Add the handler immediately above `SetClientModules`:

```csharp
    private static async Task<IResult> GetClientModules(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        InstalledModules installed, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminClient, actorFactory, control, cancellationToken))
            return Results.Forbid();

        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is null)
            return Results.NotFound();

        // Entitlement RAW, deliberately un-intersected: this is the one surface that lets support tell
        // "not sold" apart from "not installed". Sorted installed keys so the diff is easy to eyeball.
        return Results.Ok(new ClientModulesResponse(
            clientId,
            client.EnabledModules,
            [.. installed.Keys.OrderBy(k => k, StringComparer.Ordinal)]));
    }
```

(c) Update the PUT's success return (the last line of `SetClientModules`) to carry the same installed
set:

```csharp
        await control.SetClientModulesAsync(clientId, keys, cancellationToken);
        return Results.Ok(new ClientModulesResponse(
            clientId, keys, [.. installed.Keys.OrderBy(k => k, StringComparer.Ordinal)]));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ClientModulesTests"
```
Expected: PASS, all eight facts.

- [ ] **Step 6: Confirm nothing else consumed the contract**

Run:
```
grep -rn "ClientModulesResponse" --include=*.cs --include=*.ts Backend UI Modules Modules.Private Accounting101.Host
```
Expected: hits only in `AdminContracts.cs`, `AdminEndpoints.cs` and `ClientModulesTests.cs`. If any
`.ts` file appears, stop and report it — the spec asserts the Angular side changes nothing, and a hit
would contradict that.

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/AdminContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/ClientModulesTests.cs
git commit -m "feat(admin): GET client modules — raw entitlement beside installed keys"
```

---

### Task 6: Composition discovery becomes a superset assertion

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ModuleCompositionDiscoveryTests.cs:9-26`

**Interfaces:**
- Consumes: `ModuleCompositionDiscovery.DiscoverAll()` (`Accounting101.ModuleKit.Api`) — unchanged.
- Produces: nothing. Test-only.

**Why:** the test pins an exact global set of compositions, which cannot be right once composition is
deployment-dependent. It fails on any checkout that has a `Modules.Private/` overlay present (eight
found, seven expected) — confirmed failing at `8d2a255`, before the retry-key work. Its original
purpose must survive: the comment says it exists so *"a pruned/undiscovered module fails here
loudly."* The superset form still does that.

- [ ] **Step 1: Rewrite the test**

Replace the whole `Discovers_exactly_the_seven_module_compositions` fact (currently lines 9-26) in
`Backend/Accounting101.Ledger.Api.Tests/ModuleCompositionDiscoveryTests.cs` with:

```csharp
    [Fact]
    public void Discovers_at_least_the_seven_public_module_compositions()
    {
        string[] found = ModuleCompositionDiscovery.DiscoverAll()
            .Select(m => m.GetType().Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Superset, not equality: composition is deployment-dependent. A Modules.Private/ overlay
        // (EInvoicing today) legitimately adds more, and this checkout may or may not have one — an
        // exact-set assertion fails purely on whether the overlay happens to be present.
        //
        // The original purpose survives unchanged: a pruned or undiscovered PUBLIC module still fails
        // here loudly, which is the whole reason this test exists.
        string[] expected =
        [
            "CashComposition", "FixedAssetsComposition", "InventoryComposition",
            "PayablesComposition", "PayrollComposition", "ReceivablesComposition",
            "ReconciliationComposition",
        ];
        string[] missing = [.. expected.Except(found).OrderBy(n => n, StringComparer.Ordinal)];

        Assert.True(
            missing.Length == 0,
            $"Undiscovered public module composition(s): {string.Join(", ", missing)}. "
            + $"Discovered: {string.Join(", ", found)}.");
    }
```

Leave `Discovery_order_is_deterministic` exactly as it is.

- [ ] **Step 2: Run the test to verify it passes**

Run:
```
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ModuleCompositionDiscoveryTests"
```
Expected: PASS, both facts. (This is the one task with no red phase — the pre-change state was
already red for the wrong reason, and the change is the fix. Confirm you saw the old
`Discovers_exactly_the_seven_module_compositions` fail before rewriting it, by running the filter
above *before* Step 1: expected
`Assert.Equal() Failure … Actual: [… , "EInvoicingComposition", …]` on a checkout with the overlay.)

- [ ] **Step 3: Verify the whole suite**

Run:
```
dotnet build Accounting101.slnx
dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj
```
Expected: **all tests pass, 0 new warnings.** Baseline before this branch was 1479/1480 with the
single failure being exactly `ModuleCompositionDiscoveryTests` — so the expected end state is a
**clean 1480/1480** (plus the new facts added here). The 4 NU1902/NU1903 advisories from
`Accounting101.TestSupport` are pre-existing noise.

Then verify the spec's one open question — that no server-side test asserts `einvoicing` appears in a
capabilities payload (such a test would fail on a public-only checkout and would need the same
substitution treatment):

```
grep -rn "einvoicing" --include=*.cs Backend Modules Modules.Private
```
Expected: hits only inside `Modules.Private/`. If a hit appears under `Backend/` in a capabilities
assertion, fix it with `ClientWithInstalledModules` and note it in the commit.

- [ ] **Step 4: Verify the Angular suite is untouched**

Run:
```
cd UI/Angular && npm test -- --watch=false
```
Expected: 599/599 pass. No Angular file was modified in this plan — `caps.moduleEnabled()` already
reads the capabilities list, and its meaning simply shifts from "sold" to "available", which is what
the route guard always meant.

- [ ] **Step 5: Confirm the dev-config edit is still unstaged**

Run:
```
git status --short
```
Expected: `UI/Angular/src/app/core/api/environment.ts` shows as ` M` (modified, **unstaged**) and
nothing else is dirty. **Never stage that file.**

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api.Tests/ModuleCompositionDiscoveryTests.cs
git commit -m "test(modules): discovery pins a superset — composition is deployment-dependent"
```

---

## Self-Review

**1. Spec coverage**

| Spec requirement | Task |
|---|---|
| `InstalledModules` singleton, `IReadOnlySet<string> Keys`, keys only (no `Secret`) | 1 |
| Registered via `TryAddSingleton` factory inside `AddModule`; resolves late so it sees all modules | 1 |
| Taken from DI, not the control database | 1 (+ Task 4 moves the write guard off `ListModulesAsync`) |
| `CapabilitiesEndpoints:37` intersects | 2 |
| `PostingAccountEndpoints:33` intersects | 3 |
| `SetClientModules` rejects uninstalled keys, 422, naming them | 4 |
| Admin client detail keeps entitlement raw and adds installed | 5 |
| `PlatformEndpoints:138` usage meter keeps raw | *No task — deliberate no-op, stated under File Structure* |
| `ModuleAccess` unchanged | *No task — deliberate no-op, stated under File Structure* |
| Angular changes nothing | Verified in Task 6 Step 4 |
| Tests substitute via `ConfigureTestServices` + `Replace` (TryAdd is first-wins) | 2 (`ClientWithInstalledModules`) |
| Intersection tested three ways on both read paths | 2, 3 |
| Mixed installed/absent write rejects wholly and persists nothing | 4 |
| Entitlement survives uninstall | 2 |
| Admin detail exposes both fields, differing in the constructed case | 5 |
| Discovery test → superset, `Discovery_order_is_deterministic` untouched | 6 |
| Verify no server-side test asserts `einvoicing` in a capabilities payload | 6 Step 3 |
| No new dependency; no module→module reference; 0 new warnings | Global Constraints; verified 6 Step 3 |

Gap deliberately left: nothing in this plan makes the DLL unobtainable — self-hosted packaging and
licensing is a separate sub-project per the spec.

**2. Placeholder scan:** no TBD/TODO, no "add error handling", no "similar to Task N". Every code
step carries the literal code. Every test step carries the literal assertions and the expected
failure text.

**3. Type consistency:** `InstalledModules(IEnumerable<string> keys)` / `.Keys` / `.Has(string)` are
declared in Task 1 and used with those exact names in Tasks 2, 3, 4, 5.
`ClientWithInstalledModules(Guid, IEnumerable<string>, params (string Type, string Value)[])` is
declared in Task 2 and called with that shape in Tasks 3 and 5.
`ClientModulesResponse` gains its third member and *both* construction sites (GET and PUT) are
updated inside Task 5 — Task 4 deliberately leaves the two-argument call, which still compiles
against the pre-Task-5 record.
