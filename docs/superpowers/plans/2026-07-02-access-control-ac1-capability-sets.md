# Access Control AC-1: Backend Capability Sets — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add editable, deployment-wide named capability *sets* as first-class control-DB data — seeded from the existing hardcoded role presets — with a deployment-admin CRUD surface, so an owner can define/rename/adjust capability bundles without a code change.

**Architecture:** A new `CapabilitySet` document lives in a `capabilitySets` collection in the per-deployment control database, alongside `clients`/`memberships`/`modules`. `ControlStore` gains CRUD for it. A startup hosted service seeds one built-in set per `LedgerRole` preset, idempotently and **in place** (existing names are never overwritten). A new deployment-admin-only minimal-API surface (`/capability-sets`) exposes list/create/update/delete with capability-vocabulary validation and a built-in-undeletable guard. This slice does **not** touch enforcement or memberships — sets are inert data until AC-2 wires live-bound resolution.

**Tech Stack:** C# / .NET 10, ASP.NET Core Minimal APIs, MongoDB.Driver, xUnit, EphemeralMongo (shared single-node replica set via `Accounting101.TestSupport.SharedMongo`), `WebApplicationFactory<Program>` in-memory host.

## Global Constraints

- **Enforcement is untouched.** No change to `ModuleAccess.AuthorizeAsync`, `LedgerGateway`, or `Membership`. Capabilities are still resolved from `membership.Capabilities` exactly as today. (Live-binding resolution is AC-2.)
- **Sets are deployment-wide**, gated **deployment-admin only** — reuse `AdminEndpoints.Policy` (`"DeploymentAdmin"`, requires the `admin=true` token claim). Per-client `admin.users` governs *assignment*, which is AC-2; it is not used in this slice.
- **No capability is ever sourced from a token claim.** Sets store capability *strings* from the fixed `Capabilities.All` vocabulary; unknown strings are rejected 422.
- **Built-in sets persist edits in place — no forking.** Re-seeding only inserts sets whose `Name` is absent; it never overwrites an existing set. Built-ins are editable but not deletable (delete → 409).
- **Commits:** stage explicit paths only — never `git add -A`/`.`. `environment.ts` and IDE `.csproj`/`.slnx` churn stay UNCOMMITTED. Commit trailer required:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Test runner:** `dotnet test Backend/Accounting101.Ledger.Api.Tests`.
- **Style:** match the surrounding code — explicit types (not `var` where the codebase uses explicit), file-scoped namespaces, `[BsonIgnoreExtraElements]` on control documents, XML-doc summaries on public members.

---

## File Structure

**Create:**
- `Backend/Accounting101.Ledger.Api/Control/CapabilitySet.cs` — the control-DB document.
- `Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs` — `IHostedService` seeding built-ins on startup.
- `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs` — the `/capability-sets` minimal-API surface.
- `Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs` — request/response DTOs.
- `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs` — `ControlStore` CRUD + seeding tests.
- `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs` — HTTP surface tests.

**Modify:**
- `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs` — add the `capabilitySets` collection + CRUD + seeding method.
- `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs` — register the hosted seeder.
- `Accounting101.Host/Program.cs` — map the new endpoint group (one line).

---

### Task 1: `CapabilitySet` document + `ControlStore` CRUD

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Control/CapabilitySet.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs`

**Interfaces:**
- Consumes: `ApiFixture.Control()` → `ControlStore` bound to the fixture's control DB (existing test helper); `LedgerRole`, `RolePresets`, `Capabilities` (existing).
- Produces (later tasks rely on these exact signatures on `ControlStore`):
  - `Task<IReadOnlyList<CapabilitySet>> ListCapabilitySetsAsync(CancellationToken ct = default)`
  - `Task<CapabilitySet?> GetCapabilitySetAsync(Guid id, CancellationToken ct = default)`
  - `Task<CapabilitySet?> GetCapabilitySetByNameAsync(string name, CancellationToken ct = default)` (case-insensitive)
  - `Task CreateCapabilitySetAsync(CapabilitySet set, CancellationToken ct = default)`
  - `Task UpdateCapabilitySetAsync(CapabilitySet set, CancellationToken ct = default)` (replace by `Id`)
  - `Task DeleteCapabilitySetAsync(Guid id, CancellationToken ct = default)`
  - And the `CapabilitySet` type: `{ Guid Id; string Name; string? Description; IReadOnlyList<string> Capabilities; bool Builtin; }`

- [ ] **Step 1: Write the `CapabilitySet` document**

Create `Backend/Accounting101.Ledger.Api/Control/CapabilitySet.cs`:

```csharp
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// A named, editable bundle of capabilities in the deployment's control database. Sets are the
/// owner-managed successor to the hardcoded <see cref="RolePresets"/>: members reference sets and
/// resolve their capabilities from them (AC-2). Deployment-wide — one catalog per deployment.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class CapabilitySet
{
    [BsonId]
    public Guid Id { get; set; }

    /// <summary>Unique, human-facing name (e.g. "Controller", "Warehouse Clerk").</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The capabilities this set grants — each a member of <see cref="Capabilities.All"/>.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>True for sets seeded from <see cref="RolePresets"/>: editable in place, but not deletable.</summary>
    public bool Builtin { get; set; }
}
```

- [ ] **Step 2: Write the failing store test**

Create `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CapabilitySetStoreTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Create_then_get_round_trips_the_set()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = new()
        {
            Id = Guid.NewGuid(),
            Name = "Warehouse Clerk " + Guid.NewGuid().ToString("N"),
            Description = "Receiving desk",
            Capabilities = [Capabilities.GlRead, Capabilities.ApWrite],
            Builtin = false,
        };
        await control.CreateCapabilitySetAsync(set);

        CapabilitySet fetched = (await control.GetCapabilitySetAsync(set.Id))!;
        Assert.Equal(set.Name, fetched.Name);
        Assert.Equal("Receiving desk", fetched.Description);
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.ApWrite }.SetEquals(fetched.Capabilities));
        Assert.False(fetched.Builtin);
    }

    [Fact]
    public async Task GetByName_is_case_insensitive()
    {
        ControlStore control = fixture.Control();
        string name = "Mixed Case " + Guid.NewGuid().ToString("N");
        await control.CreateCapabilitySetAsync(new CapabilitySet { Id = Guid.NewGuid(), Name = name, Capabilities = [] });

        Assert.NotNull(await control.GetCapabilitySetByNameAsync(name.ToUpperInvariant()));
    }

    [Fact]
    public async Task Update_replaces_capabilities_and_name()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = new() { Id = Guid.NewGuid(), Name = "Before " + Guid.NewGuid().ToString("N"), Capabilities = [Capabilities.GlRead] };
        await control.CreateCapabilitySetAsync(set);

        set.Name = "After " + Guid.NewGuid().ToString("N");
        set.Capabilities = [Capabilities.GlRead, Capabilities.GlPost];
        await control.UpdateCapabilitySetAsync(set);

        CapabilitySet fetched = (await control.GetCapabilitySetAsync(set.Id))!;
        Assert.StartsWith("After ", fetched.Name);
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.GlPost }.SetEquals(fetched.Capabilities));
    }

    [Fact]
    public async Task Delete_removes_the_set()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = new() { Id = Guid.NewGuid(), Name = "Doomed " + Guid.NewGuid().ToString("N"), Capabilities = [] };
        await control.CreateCapabilitySetAsync(set);
        await control.DeleteCapabilitySetAsync(set.Id);
        Assert.Null(await control.GetCapabilitySetAsync(set.Id));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetStoreTests`
Expected: FAIL to compile — `ControlStore` has no `CreateCapabilitySetAsync`/`GetCapabilitySetAsync`/etc.

- [ ] **Step 4: Add the collection + CRUD to `ControlStore`**

In `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`, add a field beside the others and initialize it in the constructor, then add the CRUD methods.

Add the field (beside `_modules`):

```csharp
    private readonly IMongoCollection<CapabilitySet> _capabilitySets;
```

In the constructor (after `_modules = ...`):

```csharp
        _capabilitySets = database.GetCollection<CapabilitySet>("capabilitySets");
```

Add these methods (place them after the module methods, before the closing brace):

```csharp
    /// <summary>All capability sets in this deployment (built-in + custom).</summary>
    public async Task<IReadOnlyList<CapabilitySet>> ListCapabilitySetsAsync(CancellationToken cancellationToken = default) =>
        await _capabilitySets.Find(FilterDefinition<CapabilitySet>.Empty).ToListAsync(cancellationToken);

    /// <summary>The capability set with this id, or null.</summary>
    public async Task<CapabilitySet?> GetCapabilitySetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _capabilitySets.Find(s => s.Id == id).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The capability set with this name (case-insensitive), or null. Sets are few, so an
    /// in-memory scan is cheaper and simpler than a collated index query.</summary>
    public async Task<CapabilitySet?> GetCapabilitySetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        List<CapabilitySet> all = await _capabilitySets.Find(FilterDefinition<CapabilitySet>.Empty).ToListAsync(cancellationToken);
        return all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Insert a new capability set.</summary>
    public Task CreateCapabilitySetAsync(CapabilitySet set, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        return _capabilitySets.InsertOneAsync(set, cancellationToken: cancellationToken);
    }

    /// <summary>Replace an existing capability set (matched by id).</summary>
    public Task UpdateCapabilitySetAsync(CapabilitySet set, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        return _capabilitySets.ReplaceOneAsync(s => s.Id == set.Id, set, cancellationToken: cancellationToken);
    }

    /// <summary>Delete a capability set by id.</summary>
    public Task DeleteCapabilitySetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _capabilitySets.DeleteOneAsync(s => s.Id == id, cancellationToken);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetStoreTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/CapabilitySet.cs \
        Backend/Accounting101.Ledger.Api/Control/ControlStore.cs \
        Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs
git commit -m "$(cat <<'EOF'
feat(access): CapabilitySet control-DB document + ControlStore CRUD (AC-1)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Seed built-in sets from role presets (persist-in-place)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`
- Create: `Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs` (add to it)

**Interfaces:**
- Consumes: `ControlStore` CRUD from Task 1; `Enum.GetValues<LedgerRole>()`, `RolePresets.For(role)`.
- Produces: `Task ControlStore.SeedBuiltinCapabilitySetsAsync(CancellationToken ct = default)` — idempotent, inserts one `Builtin=true` set per `LedgerRole` whose `Name` is absent; never overwrites. Startup wiring via `CapabilitySetSeeder : IHostedService`.

- [ ] **Step 1: Write the failing seeding tests**

Append to `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Seeding_creates_one_builtin_per_role()
    {
        ControlStore control = fixture.Control();
        await control.SeedBuiltinCapabilitySetsAsync();

        IReadOnlyList<CapabilitySet> all = await control.ListCapabilitySetsAsync();
        foreach (LedgerRole role in Enum.GetValues<LedgerRole>())
        {
            CapabilitySet? set = all.FirstOrDefault(s => s.Name == role.ToString());
            Assert.NotNull(set);
            Assert.True(set!.Builtin);
            Assert.True(RolePresets.For(role).SetEquals(set.Capabilities));
        }
    }

    [Fact]
    public async Task Seeding_is_idempotent_and_never_overwrites_an_edited_builtin()
    {
        ControlStore control = fixture.Control();
        await control.SeedBuiltinCapabilitySetsAsync();

        // Owner edits a built-in in place.
        CapabilitySet clerk = (await control.GetCapabilitySetByNameAsync("Clerk"))!;
        clerk.Capabilities = [Capabilities.GlRead];
        await control.UpdateCapabilitySetAsync(clerk);

        // Re-seed (e.g. next startup) must NOT restore the preset.
        await control.SeedBuiltinCapabilitySetsAsync();

        CapabilitySet after = (await control.GetCapabilitySetByNameAsync("Clerk"))!;
        Assert.Equal(clerk.Id, after.Id);
        Assert.True(new HashSet<string> { Capabilities.GlRead }.SetEquals(after.Capabilities));
    }
```

Note: `fixture` is shared across a test class, so these two tests seed into the same control DB — that is fine (seeding is idempotent and both assert against the shared state consistently). The store CRUD tests in Task 1 use unique GUID names and are unaffected.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetStoreTests`
Expected: FAIL to compile — `SeedBuiltinCapabilitySetsAsync` does not exist.

- [ ] **Step 3: Add the seeding method to `ControlStore`**

In `ControlStore.cs`, add after `DeleteCapabilitySetAsync`:

```csharp
    /// <summary>Seed one built-in capability set per <see cref="LedgerRole"/> preset, idempotently.
    /// Persist-in-place: a set whose name already exists is left untouched (an owner's edits survive
    /// restarts); only missing names are inserted. Also ensures a unique index on <c>Name</c>.</summary>
    public async Task SeedBuiltinCapabilitySetsAsync(CancellationToken cancellationToken = default)
    {
        await _capabilitySets.Indexes.CreateOneAsync(
            new CreateIndexModel<CapabilitySet>(
                Builders<CapabilitySet>.IndexKeys.Ascending(s => s.Name),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken);

        IReadOnlyList<CapabilitySet> existing = await ListCapabilitySetsAsync(cancellationToken);
        HashSet<string> present = existing.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (LedgerRole role in Enum.GetValues<LedgerRole>())
        {
            string name = role.ToString();
            if (present.Contains(name)) continue;
            await _capabilitySets.InsertOneAsync(
                new CapabilitySet
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Description = $"Built-in preset for the {name} role.",
                    Capabilities = [.. RolePresets.For(role)],
                    Builtin = true,
                },
                cancellationToken: cancellationToken);
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetStoreTests`
Expected: PASS (6 tests total).

- [ ] **Step 5: Write the startup seeder hosted service**

Create `Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Hosting;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>Seeds the built-in capability sets into the control DB on startup (idempotent,
/// persist-in-place). Runs once per host start.</summary>
public sealed class CapabilitySetSeeder(ControlStore control) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        control.SeedBuiltinCapabilitySetsAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 6: Register the seeder in `AddLedgerEngine`**

In `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`, add after the `ControlStore` registration (the `services.AddSingleton(sp => new ControlStore(...))` line):

```csharp
        // Seed the built-in capability sets (from role presets) once on startup — idempotent.
        services.AddHostedService<CapabilitySetSeeder>();
```

- [ ] **Step 7: Verify the whole test project still builds and passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests`
Expected: PASS — all existing tests plus the 6 store tests. (The hosted service now runs at fixture startup; it seeds built-ins into each fixture's fresh control DB with no effect on existing tests.)

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ControlStore.cs \
        Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs \
        Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs \
        Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs
git commit -m "$(cat <<'EOF'
feat(access): seed built-in capability sets from role presets on startup (AC-1)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `/capability-sets` deployment-admin CRUD endpoints

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs`
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`
- Modify: `Accounting101.Host/Program.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs`

**Interfaces:**
- Consumes: `ControlStore` CRUD + seeding (Tasks 1–2); `Capabilities.All`; `AdminEndpoints.Policy` (`"DeploymentAdmin"`); `ApiFixture.AdminClient()` (deployment-admin HttpClient), `ApiFixture.SeedClientAsync()` (a non-admin member), `ApiFixture.ClientFor(...)`.
- Produces: HTTP surface under `/capability-sets`:
  - `GET /capability-sets` → 200 `CapabilitySetResponse[]`
  - `POST /capability-sets` → 201 `CapabilitySetResponse` (422 unknown cap / blank name; 409 duplicate name)
  - `PUT /capability-sets/{id:guid}` → 200 `CapabilitySetResponse` (404 missing; 422 unknown cap / blank name; 409 duplicate name)
  - `DELETE /capability-sets/{id:guid}` → 204 (404 missing; 409 built-in)
  - DTOs: `CapabilitySetResponse(Guid Id, string Name, string? Description, IReadOnlyList<string> Capabilities, bool Builtin)`, `CreateCapabilitySetRequest(string Name, string? Description, IReadOnlyList<string> Capabilities)`, `UpdateCapabilitySetRequest(string Name, string? Description, IReadOnlyList<string> Capabilities)`.

- [ ] **Step 1: Write the DTOs**

Create `Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs`:

```csharp
namespace Accounting101.Ledger.Contracts;

/// <summary>A capability set as returned by the admin API.</summary>
public sealed record CapabilitySetResponse(
    Guid Id, string Name, string? Description, IReadOnlyList<string> Capabilities, bool Builtin);

/// <summary>Create a new custom capability set. Every capability must be in the known vocabulary.</summary>
public sealed record CreateCapabilitySetRequest(
    string Name, string? Description, IReadOnlyList<string> Capabilities);

/// <summary>Replace an existing capability set's name/description/capabilities.</summary>
public sealed record UpdateCapabilitySetRequest(
    string Name, string? Description, IReadOnlyList<string> Capabilities);
```

- [ ] **Step 2: Write the failing endpoint tests**

Create `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CapabilitySetEndpointsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task List_returns_the_seeded_builtins_to_a_deployment_admin()
    {
        HttpClient admin = fixture.AdminClient();
        List<CapabilitySetResponse> sets =
            (await admin.GetFromJsonAsync<List<CapabilitySetResponse>>("/capability-sets"))!;

        Assert.Contains(sets, s => s.Name == "Controller" && s.Builtin);
        Assert.Contains(sets, s => s.Name == "ArClerk" && s.Builtin);
    }

    [Fact]
    public async Task A_non_admin_member_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Controller);
        HttpResponseMessage res = await c.Http.GetAsync("/capability-sets");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Create_then_list_includes_the_new_custom_set()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Warehouse " + Guid.NewGuid().ToString("N");
        HttpResponseMessage res = await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name, "Receiving", [Capabilities.GlRead, Capabilities.ApWrite]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        CapabilitySetResponse created = (await res.Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.False(created.Builtin);
        Assert.Equal(name, created.Name);
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.ApWrite }.SetEquals(created.Capabilities));
    }

    [Fact]
    public async Task Create_with_an_unknown_capability_is_422()
    {
        HttpClient admin = fixture.AdminClient();
        HttpResponseMessage res = await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Bad " + Guid.NewGuid().ToString("N"), null, ["gl.read", "not.a.capability"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Create_with_a_duplicate_name_is_409()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Dup " + Guid.NewGuid().ToString("N");
        await admin.PostAsJsonAsync("/capability-sets", new CreateCapabilitySetRequest(name, null, ["gl.read"]));
        HttpResponseMessage res = await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name.ToUpperInvariant(), null, ["gl.read"]));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Update_edits_a_set_in_place()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Editable " + Guid.NewGuid().ToString("N");
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name, null, ["gl.read"]))).Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        HttpResponseMessage res = await admin.PutAsJsonAsync($"/capability-sets/{created.Id}",
            new UpdateCapabilitySetRequest(name, "now with post", [Capabilities.GlRead, Capabilities.GlPost]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        CapabilitySetResponse updated = (await res.Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.Equal("now with post", updated.Description);
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.GlPost }.SetEquals(updated.Capabilities));
    }

    [Fact]
    public async Task Update_a_missing_set_is_404()
    {
        HttpClient admin = fixture.AdminClient();
        HttpResponseMessage res = await admin.PutAsJsonAsync($"/capability-sets/{Guid.NewGuid()}",
            new UpdateCapabilitySetRequest("Ghost", null, ["gl.read"]));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Delete_a_custom_set_is_204()
    {
        HttpClient admin = fixture.AdminClient();
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Doomed " + Guid.NewGuid().ToString("N"), null, ["gl.read"])))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        HttpResponseMessage res = await admin.DeleteAsync($"/capability-sets/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_builtin_is_409()
    {
        HttpClient admin = fixture.AdminClient();
        List<CapabilitySetResponse> sets =
            (await admin.GetFromJsonAsync<List<CapabilitySetResponse>>("/capability-sets"))!;
        CapabilitySetResponse controller = sets.First(s => s.Name == "Controller");

        HttpResponseMessage res = await admin.DeleteAsync($"/capability-sets/{controller.Id}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetEndpointsTests`
Expected: FAIL — 404 on all routes (`/capability-sets` not mapped yet).

- [ ] **Step 4: Write the endpoint surface**

Create `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Deployment-wide capability-set management. Sets are global infrastructure, so this surface is
/// deployment-admin only (the <see cref="AdminEndpoints.Policy"/>); per-client assignment of sets
/// to members is a separate, <c>admin.users</c>-gated surface (AC-2). Built-in sets (seeded from
/// role presets) are editable in place but not deletable.
/// </summary>
public static class CapabilitySetEndpoints
{
    public static void MapCapabilitySetEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/capability-sets").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", List);
        g.MapPost("", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapDelete("/{id:guid}", Delete);
    }

    private static CapabilitySetResponse ToResponse(CapabilitySet s) =>
        new(s.Id, s.Name, s.Description, s.Capabilities, s.Builtin);

    // Every capability must be in the known vocabulary; returns a 422 problem for the first offender.
    private static IResult? ValidateCapabilities(IReadOnlyList<string> capabilities)
    {
        foreach (string cap in capabilities)
            if (!Capabilities.All.Contains(cap))
                return Results.Problem($"Unknown capability '{cap}'.", statusCode: StatusCodes.Status422UnprocessableEntity);
        return null;
    }

    private static async Task<IResult> List(ControlStore control, CancellationToken ct)
    {
        IReadOnlyList<CapabilitySet> sets = await control.ListCapabilitySetsAsync(ct);
        return Results.Ok(sets.Select(ToResponse).ToList());
    }

    private static async Task<IResult> Create(CreateCapabilitySetRequest request, ControlStore control, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Name is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        if (ValidateCapabilities(request.Capabilities) is { } capError) return capError;
        if (await control.GetCapabilitySetByNameAsync(request.Name, ct) is not null)
            return Results.Problem($"A capability set named '{request.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);

        CapabilitySet set = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description,
            Capabilities = request.Capabilities,
            Builtin = false,
        };
        await control.CreateCapabilitySetAsync(set, ct);
        return Results.Created($"/capability-sets/{set.Id}", ToResponse(set));
    }

    private static async Task<IResult> Update(Guid id, UpdateCapabilitySetRequest request, ControlStore control, CancellationToken ct)
    {
        CapabilitySet? existing = await control.GetCapabilitySetAsync(id, ct);
        if (existing is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Name is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        if (ValidateCapabilities(request.Capabilities) is { } capError) return capError;

        CapabilitySet? byName = await control.GetCapabilitySetByNameAsync(request.Name, ct);
        if (byName is not null && byName.Id != id)
            return Results.Problem($"A capability set named '{request.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);

        existing.Name = request.Name.Trim();
        existing.Description = request.Description;
        existing.Capabilities = request.Capabilities;
        await control.UpdateCapabilitySetAsync(existing, ct);
        return Results.Ok(ToResponse(existing));
    }

    private static async Task<IResult> Delete(Guid id, ControlStore control, CancellationToken ct)
    {
        CapabilitySet? existing = await control.GetCapabilitySetAsync(id, ct);
        if (existing is null) return Results.NotFound();
        if (existing.Builtin)
            return Results.Problem("Built-in capability sets cannot be deleted.", statusCode: StatusCodes.Status409Conflict);
        // In-use guard (members referencing this set) is added in AC-2, once GrantedSetIds exists.
        await control.DeleteCapabilitySetAsync(id, ct);
        return Results.NoContent();
    }
}
```

- [ ] **Step 5: Map the endpoint group in the host**

In `Accounting101.Host/Program.cs`, add after the `app.MapCapabilityCatalogEndpoints();` line:

```csharp
app.MapCapabilitySetEndpoints();
```

- [ ] **Step 6: Run the endpoint tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetEndpointsTests`
Expected: PASS (9 tests).

- [ ] **Step 7: Run the full API test project**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests`
Expected: PASS — all existing tests plus the new store (6) and endpoint (9) tests.

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs \
        Accounting101.Host/Program.cs \
        Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs
git commit -m "$(cat <<'EOF'
feat(access): deployment-admin CRUD endpoints for capability sets (AC-1)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

- [ ] Run the full solution test suite to confirm nothing regressed:

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests` (and, if time permits, the whole solution).
Expected: all green.

- [ ] Confirm only intended files are staged across the three commits (no `environment.ts`, no `.csproj`/`.slnx` churn).

---

## Self-Review notes (against the AC-1 spec section)

- **"CapabilitySet entity + ControlStore CRUD"** → Task 1. ✓
- **"startup seeding from presets (persist-in-place)"** → Task 2 (idempotent, missing-names-only, edit-survives-reseed test). ✓
- **"in-memory catalog cache"** → deliberately deferred to AC-2, where the live-bound resolution path is its consumer; building a cache here with no reader would be untested speculative code (YAGNI). Noted as a scope boundary, not a gap.
- **"CapabilitySetEndpoints + validation/referential guards"** → Task 3. Deployment-admin gate ✓, 422 unknown-cap ✓, 409 dup-name ✓, 409 built-in-undeletable ✓. **Referential in-use guard (delete/rename of a set members reference) and the PUT `affectedMemberCount`** are deferred to AC-2 — they require `Membership.GrantedSetIds`, which does not exist until AC-2. Explicitly noted in code comments and here.
- **Built-in editable-in-place, no forking** → PUT does not block built-ins; only DELETE does. ✓
- **Type consistency:** `CapabilitySet` shape, the six `ControlStore` method signatures, and the three DTOs are used identically in Tasks 1–3 and their tests. ✓
- **Deferred to later AC slices (correctly out of AC-1 scope):** `Membership.GrantedSetIds`, live-bound resolution, member set-assignment, `GrantedRoles`→sets migration, `admin.users` per-client assignment gate, frontend, liveness.
