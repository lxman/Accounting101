# Access Control AC-2: Live-Bound Resolution + Set Assignment — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a member's capabilities the **live union of the capability sets it references** — resolved from the control DB at read time — so editing a set instantly re-privileges every member that references it, and add the per-client route that assigns sets to members.

**Architecture:** `Membership` gains `GrantedSetIds: Guid[]` (the go-forward authoritative grant). `ControlStore.GetMembershipAsync`/`GetMembersAsync` now load the (small, deployment-wide) capability-set catalog and **resolve** `membership.Capabilities` from it — generalizing today's `Hydrate` backfill. The enforcement chokepoints are untouched: they still read `membership.Capabilities`, which is now the live union. A new `PUT /clients/{clientId}/members/{userId}/sets` (gated per-client `admin.users`, last-admin guard preserved) assigns sets; the AC-1 set-CRUD surface gains a referential in-use guard (can't delete a set members hold) and reports an affected-member count on edit. A startup migration backfills `GrantedSetIds` from existing `GrantedRoles`.

**Tech Stack:** C# / .NET 10, ASP.NET Core Minimal APIs, MongoDB.Driver, xUnit, EphemeralMongo (shared single-node replica set via `Accounting101.TestSupport.SharedMongo`), `WebApplicationFactory<Program>` in-memory host.

## Global Constraints

- **Enforcement is untouched.** No logic change to `ModuleAccess.AuthorizeAsync`, `LedgerGateway`, or the enforcement-facing shape of `Membership`. Both chokepoints keep reading `membership.Capabilities` (confirmed: `ModuleAccess.cs:55,62`, `LedgerGateway.cs:24,25`). AC-2 only changes **how that field is populated** — resolved from referenced sets inside `ControlStore.GetMembershipAsync`/`GetMembersAsync`.
- **No capability is ever sourced from a token claim.** Resolution reads capabilities only from the control-DB capability sets (or, for legacy docs, the stored inline capabilities). Client-supplied capability lists are never trusted as authority on the set-assignment path.
- **Resolution precedence (the core invariant):**
  1. If `GrantedSetIds` is non-empty → capabilities = **union of the referenced sets' current capabilities** (dangling ids ignored).
  2. Else if `GrantedRoles` is non-empty and the built-in sets of those names exist → union of those built-in sets' **current** capabilities (so role grants are live-bound too).
  3. Else → the stored inline `Capabilities`, with the existing `LegacyRole` backfill (pre-migration single-role docs).
- **No in-memory catalog cache.** Sets are few and deployment-wide; resolution loads the catalog with one `ListCapabilitySetsAsync` per resolution call. A cache is a deferred optimization (would add invalidation coupling that could defeat live-binding); do **not** build it in this slice.
- **Set CRUD stays deployment-admin** (`AdminEndpoints.Policy`). **Member set-assignment is per-client `admin.users`** (reuse `AdminAuthorization.MayAsync`), and the **last-admin guard is preserved** — a change may not leave a client with zero `admin.users` holders.
- **Slice D raw-cap routes remain (back-compat).** `POST`/`PUT /clients/{clientId}/members/{userId}` keep taking `(Roles, Capabilities)` and keep working. A raw-cap write **clears `GrantedSetIds`** (mutual exclusion: a member is either set-assigned or a custom raw-cap grant, never a stale mix).
- **Referential integrity.** Under id-references, **renaming a set is safe** (members reference by id, not name), so there is **no rename guard**. **Deleting** a set that any membership references is blocked (409). Built-ins remain undeletable (409, from AC-1).
- **Commits:** stage explicit paths only — never `git add -A`/`.`. `UI/Angular/src/app/core/api/environment.ts` and IDE `.csproj`/`.slnx` churn stay UNCOMMITTED. Commit trailer required:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Test runner:** `dotnet test Backend/Accounting101.Ledger.Api.Tests`.
- **Style:** match the surrounding code — explicit types (not `var`), file-scoped namespaces, `[BsonIgnoreExtraElements]` on control documents, XML-doc summaries on public members, `Results.Problem(..., statusCode:)` for error responses.

---

## File Structure

**Modify:**
- `Backend/Accounting101.Ledger.Api/Control/Membership.cs` — add `GrantedSetIds`.
- `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs` — set-aware resolution (replace `Hydrate`), `SetMembershipSetsAsync`, `CountMembersReferencingSetAsync`, `BackfillGrantedSetIdsAsync`, clear `GrantedSetIds` in `SetMembershipAsync`.
- `Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs` — add `AffectedMemberCount` to `CapabilitySetResponse`.
- `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` — add `AssignSetsRequest`.
- `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs` — in-use DELETE guard + `AffectedMemberCount` on PUT.
- `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs` — `PUT /{userId}/sets` assignment route.
- `Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs` — run the backfill after seeding.

**Create:**
- `Backend/Accounting101.Ledger.Api.Tests/MembershipResolutionTests.cs` — resolution (live-binding / no-drift / back-compat) + backfill store tests.
- `Backend/Accounting101.Ledger.Api.Tests/MemberSetAssignmentTests.cs` — the `/sets` HTTP surface.

**Reuse (unchanged):** `CapabilitySetStoreTests.cs`, `CapabilitySetEndpointsTests.cs` (extended in Task 2), `ApiFixture` helpers (`Control()`, `AdminClient()`, `SeedClientAsync`, `ClientFor`).

---

### Task 1: `GrantedSetIds` + set-aware read-time resolution

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/Membership.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/MembershipResolutionTests.cs` (create)

**Interfaces:**
- Consumes: `CapabilitySet`, `ControlStore.ListCapabilitySetsAsync` / `GetCapabilitySetAsync` / `CreateCapabilitySetAsync` / `UpdateCapabilitySetAsync` / `SeedBuiltinCapabilitySetsAsync` (AC-1); `RolePresets`, `LedgerRole`, `Capabilities`; `ApiFixture.Control()`.
- Produces (later tasks rely on these exact signatures):
  - `Membership.GrantedSetIds : IReadOnlyList<Guid>` (defaults `[]`).
  - `Task ControlStore.SetMembershipSetsAsync(Guid userId, Guid clientId, IReadOnlyList<Guid> setIds, CancellationToken ct = default)` — upsert; sets `GrantedSetIds`, clears `GrantedRoles` and inline `Capabilities`.
  - `ControlStore.GetMembershipAsync`/`GetMembersAsync` return memberships whose `Capabilities` are resolved per the precedence rule.

- [ ] **Step 1: Add the `GrantedSetIds` field to `Membership`**

In `Backend/Accounting101.Ledger.Api/Control/Membership.cs`, add after the `Capabilities` property (line 25):

```csharp
    /// <summary>The capability set(s) this membership references — the authoritative grant going
    /// forward. <see cref="Capabilities"/> is resolved from these (their union) at read time
    /// (live-binding); empty means a legacy role/inline grant (see <c>ControlStore</c> resolution).</summary>
    public IReadOnlyList<Guid> GrantedSetIds { get; set; } = [];
```

- [ ] **Step 2: Write the failing resolution tests**

Create `Backend/Accounting101.Ledger.Api.Tests/MembershipResolutionTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MembershipResolutionTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<CapabilitySet> NewSetAsync(ControlStore control, params string[] caps)
    {
        CapabilitySet set = new()
        {
            Id = Guid.NewGuid(),
            Name = "Set " + Guid.NewGuid().ToString("N"),
            Capabilities = caps,
        };
        await control.CreateCapabilitySetAsync(set);
        return set;
    }

    [Fact]
    public async Task Resolved_capabilities_are_the_union_of_referenced_sets()
    {
        ControlStore control = fixture.Control();
        CapabilitySet a = await NewSetAsync(control, Capabilities.GlRead, Capabilities.ArWrite);
        CapabilitySet b = await NewSetAsync(control, Capabilities.ApWrite);

        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        await control.SetMembershipSetsAsync(user, client, [a.Id, b.Id]);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.ArWrite, Capabilities.ApWrite }
            .SetEquals(m.Capabilities));
    }

    [Fact]
    public async Task Editing_a_set_changes_an_assigned_members_capabilities_on_next_read()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = await NewSetAsync(control, Capabilities.GlRead);
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        await control.SetMembershipSetsAsync(user, client, [set.Id]);

        Membership before = (await control.GetMembershipAsync(user, client))!;
        Assert.DoesNotContain(Capabilities.GlPost, before.Capabilities);

        // Owner edits the set in place — no re-apply step.
        set.Capabilities = [Capabilities.GlRead, Capabilities.GlPost];
        await control.UpdateCapabilitySetAsync(set);

        Membership after = (await control.GetMembershipAsync(user, client))!;
        Assert.Contains(Capabilities.GlPost, after.Capabilities);
    }

    [Fact]
    public async Task Two_members_referencing_the_same_set_resolve_identically()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = await NewSetAsync(control, Capabilities.GlRead, Capabilities.CashWrite);
        Guid client = Guid.NewGuid();
        Guid u1 = Guid.NewGuid(), u2 = Guid.NewGuid();
        await control.SetMembershipSetsAsync(u1, client, [set.Id]);
        await control.SetMembershipSetsAsync(u2, client, [set.Id]);

        Membership m1 = (await control.GetMembershipAsync(u1, client))!;
        Membership m2 = (await control.GetMembershipAsync(u2, client))!;
        Assert.True(new HashSet<string>(m1.Capabilities).SetEquals(m2.Capabilities));
    }

    [Fact]
    public async Task A_role_grant_with_no_set_ids_resolves_via_the_builtin_set()
    {
        ControlStore control = fixture.Control();
        await control.SeedBuiltinCapabilitySetsAsync();   // built-in sets must exist to live-bind roles

        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        await control.AddMembershipRolesAsync(user, client, [LedgerRole.Controller]);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.True(RolePresets.For(LedgerRole.Controller).SetEquals(m.Capabilities));
    }

    [Fact]
    public async Task A_legacy_inline_only_grant_keeps_its_stored_capabilities()
    {
        ControlStore control = fixture.Control();
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        // A custom grant: roles empty, inline caps, no set ids (Slice D raw-cap shape).
        await control.SetMembershipAsync(user, client, [], [Capabilities.GlRead, Capabilities.AuditRead]);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.AuditRead }.SetEquals(m.Capabilities));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter MembershipResolutionTests`
Expected: FAIL to compile — `ControlStore` has no `SetMembershipSetsAsync`.

- [ ] **Step 4: Add `SetMembershipSetsAsync` and set-aware resolution to `ControlStore`**

In `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`:

**(a)** Replace the `GetMembershipAsync` method (lines 38–43) with a catalog-loading, resolving version:

```csharp
    /// <summary>The user's membership on the client with capabilities resolved from its referenced
    /// sets (live-binding), or null if not a member.</summary>
    public async Task<Membership?> GetMembershipAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default)
    {
        Membership? m = await _memberships.Find(m => m.UserId == userId && m.ClientId == clientId).FirstOrDefaultAsync(cancellationToken);
        if (m is null) return null;
        IReadOnlyList<CapabilitySet> catalog = await ListCapabilitySetsAsync(cancellationToken);
        return Resolve(m, catalog);
    }
```

**(b)** Add `SetMembershipSetsAsync` after `SetMembershipAsync` (after line 93):

```csharp
    /// <summary>Assign a member to capability sets (the live-bound grant). Upsert: sets
    /// <see cref="Membership.GrantedSetIds"/> and clears the legacy role list + inline capability copy —
    /// capabilities are resolved from the referenced sets at read time.</summary>
    public Task SetMembershipSetsAsync(Guid userId, Guid clientId, IReadOnlyList<Guid> setIds, CancellationToken cancellationToken = default)
    {
        UpdateDefinition<Membership> update = Builders<Membership>.Update
            .Set(m => m.GrantedSetIds, setIds)
            .Set(m => m.GrantedRoles, Array.Empty<LedgerRole>())
            .Set(m => m.Capabilities, Array.Empty<string>())
            .SetOnInsert(m => m.Id, Guid.NewGuid())
            .SetOnInsert(m => m.UserId, userId)
            .SetOnInsert(m => m.ClientId, clientId);

        return _memberships.UpdateOneAsync(
            m => m.UserId == userId && m.ClientId == clientId,
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }
```

**(c)** Replace `GetMembersAsync` (lines 103–108) so it resolves against a single catalog load:

```csharp
    /// <summary>All memberships granted on a client (capabilities resolved from referenced sets).</summary>
    public async Task<IReadOnlyList<Membership>> GetMembersAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        List<Membership> members = await _memberships.Find(m => m.ClientId == clientId).ToListAsync(cancellationToken);
        IReadOnlyList<CapabilitySet> catalog = await ListCapabilitySetsAsync(cancellationToken);
        return members.Select(m => Resolve(m, catalog)).ToList();
    }
```

**(d)** Replace the `Hydrate` method (lines 110–119) with `Resolve` + `HydrateLegacy`:

```csharp
    /// <summary>Resolve a membership's capabilities per the AC-2 precedence: referenced sets (union of
    /// their CURRENT caps) → built-in sets matching granted roles (role grants are live-bound too) →
    /// stored inline caps with the pre-migration Role backfill. Read-only derivation; no write.</summary>
    private static Membership Resolve(Membership m, IReadOnlyList<CapabilitySet> catalog)
    {
        // 1) Explicit set references are the go-forward authority.
        if (m.GrantedSetIds.Count > 0)
        {
            Dictionary<Guid, CapabilitySet> byId = catalog.ToDictionary(s => s.Id);
            HashSet<string> union = [];
            foreach (Guid id in m.GrantedSetIds)
                if (byId.TryGetValue(id, out CapabilitySet? s))
                    union.UnionWith(s.Capabilities);
            m.Capabilities = [.. union];
            return m;
        }

        // 2) Legacy role grant with no set ids yet: live-bind to the built-in sets of the same name so
        //    an owner's edit to a built-in set flows to role-based members too. If the sets are not
        //    seeded (e.g. a direct pre-startup read), fall through to the stored caps.
        if (m.GrantedRoles.Count > 0)
        {
            Dictionary<string, CapabilitySet> byName = catalog.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            HashSet<string> union = [];
            bool matched = false;
            foreach (LedgerRole role in m.GrantedRoles)
                if (byName.TryGetValue(role.ToString(), out CapabilitySet? s))
                {
                    union.UnionWith(s.Capabilities);
                    matched = true;
                }
            if (matched)
            {
                m.Capabilities = [.. union];
                return m;
            }
        }

        // 3) Pre-migration single-Role doc or a truly custom inline grant: keep the stored caps.
        return HydrateLegacy(m);
    }

    /// <summary>Backfill a pre-migration (Role-only) doc to the capability shape at read time (no write).</summary>
    private static Membership HydrateLegacy(Membership m)
    {
        if (m.Capabilities.Count == 0 && m.LegacyRole is { } role)
        {
            m.GrantedRoles = [role];
            m.Capabilities = [.. RolePresets.For(role)];
        }
        return m;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter MembershipResolutionTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Run the full API test project (guard the enforcement invariant)**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests`
Expected: PASS — all existing tests still green (`CapabilitiesTests`, `MemberManagementTests`, module-access tests all read resolved caps that equal the old presets).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/Membership.cs \
        Backend/Accounting101.Ledger.Api/Control/ControlStore.cs \
        Backend/Accounting101.Ledger.Api.Tests/MembershipResolutionTests.cs
git commit -m "$(cat <<'EOF'
feat(access): live-bound capability resolution from referenced sets (AC-2)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Referential in-use guard + affected-member count

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`
- Modify: `Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs` (append)

**Interfaces:**
- Consumes: `SetMembershipSetsAsync` (Task 1); `ApiFixture.AdminClient()`, `ApiFixture.Control()`; AC-1 `/capability-sets` surface.
- Produces:
  - `Task<long> ControlStore.CountMembersReferencingSetAsync(Guid setId, CancellationToken ct = default)`.
  - `CapabilitySetResponse` gains a trailing `int AffectedMemberCount = 0`.
  - `DELETE /capability-sets/{id}` → 409 when the set is referenced by ≥1 membership (in addition to the built-in 409).
  - `PUT /capability-sets/{id}` → response `AffectedMemberCount` = number of memberships referencing the set.

- [ ] **Step 1: Add `AffectedMemberCount` to the response DTO**

In `Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs`, replace the `CapabilitySetResponse` record with:

```csharp
/// <summary>A capability set as returned by the admin API. <paramref name="AffectedMemberCount"/> is
/// the number of memberships that reference this set — populated on edit so the UI can confirm the
/// blast radius before saving; 0 on list/create.</summary>
public sealed record CapabilitySetResponse(
    Guid Id, string Name, string? Description, IReadOnlyList<string> Capabilities, bool Builtin,
    int AffectedMemberCount = 0);
```

- [ ] **Step 2: Write the failing referential tests**

Append to `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Deleting_a_set_that_a_member_references_is_409()
    {
        HttpClient admin = fixture.AdminClient();
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Held " + Guid.NewGuid().ToString("N"), null, ["gl.read"])))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        // A member now references the set.
        await fixture.Control().SetMembershipSetsAsync(Guid.NewGuid(), Guid.NewGuid(), [created.Id]);

        HttpResponseMessage res = await admin.DeleteAsync($"/capability-sets/{created.Id}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Editing_a_set_reports_the_affected_member_count()
    {
        HttpClient admin = fixture.AdminClient();
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Counted " + Guid.NewGuid().ToString("N"), null, ["gl.read"])))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        ControlStore control = fixture.Control();
        await control.SetMembershipSetsAsync(Guid.NewGuid(), Guid.NewGuid(), [created.Id]);
        await control.SetMembershipSetsAsync(Guid.NewGuid(), Guid.NewGuid(), [created.Id]);

        HttpResponseMessage res = await admin.PutAsJsonAsync($"/capability-sets/{created.Id}",
            new UpdateCapabilitySetRequest(created.Name, "edited", [Capabilities.GlRead, Capabilities.GlPost]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        CapabilitySetResponse updated = (await res.Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.Equal(2, updated.AffectedMemberCount);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetEndpointsTests`
Expected: FAIL — delete returns 204 (no in-use guard yet); edit reports `AffectedMemberCount == 0`.

- [ ] **Step 4: Add `CountMembersReferencingSetAsync` to `ControlStore`**

In `ControlStore.cs`, add after `SetMembershipSetsAsync`:

```csharp
    /// <summary>How many memberships reference the given capability set (deployment-wide) — the
    /// blast radius of editing or deleting it.</summary>
    public Task<long> CountMembersReferencingSetAsync(Guid setId, CancellationToken cancellationToken = default) =>
        _memberships.CountDocumentsAsync(
            Builders<Membership>.Filter.AnyEq(m => m.GrantedSetIds, setId),
            cancellationToken: cancellationToken);
```

- [ ] **Step 5: Wire the guard + count into the endpoints**

In `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`:

**(a)** In `Update`, replace the final two lines (`await control.UpdateCapabilitySetAsync(existing, ct);` and `return Results.Ok(ToResponse(existing));`) with:

```csharp
        await control.UpdateCapabilitySetAsync(existing, ct);
        long affected = await control.CountMembersReferencingSetAsync(id, ct);
        return Results.Ok(ToResponse(existing) with { AffectedMemberCount = (int)affected });
```

**(b)** In `Delete`, replace the in-use comment line
(`// In-use guard (members referencing this set) is added in AC-2, once GrantedSetIds exists.`) with the guard:

```csharp
        long referencing = await control.CountMembersReferencingSetAsync(id, ct);
        if (referencing > 0)
            return Results.Problem($"{referencing} member(s) reference this set; reassign them before deleting.",
                statusCode: StatusCodes.Status409Conflict);
```

- [ ] **Step 6: Run the endpoint tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetEndpointsTests`
Expected: PASS — the AC-1 endpoint tests (unreferenced delete still 204, built-in delete still 409) plus the 2 new referential tests.

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ControlStore.cs \
        Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs
git commit -m "$(cat <<'EOF'
feat(access): in-use delete guard + affected-member count on set edit (AC-2)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Member set-assignment route + last-admin guard + raw-cap mutual exclusion

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs` (clear `GrantedSetIds` in `SetMembershipAsync`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/MemberSetAssignmentTests.cs` (create)

**Interfaces:**
- Consumes: `AssignSetsRequest`; `SetMembershipSetsAsync`, `GetCapabilitySetAsync`, `ListCapabilitySetsAsync`, `GetMembersAsync`, `IsMemberAsync` (`ControlStore`); `AdminAuthorization.MayAsync`, `Capabilities.AdminUsers`; `ApiFixture.SeedClientAsync(role: Admin)`, `AdminClient()`, `Control()`, `ClientFor`.
- Produces:
  - `AdminContracts`: `sealed record AssignSetsRequest(IReadOnlyList<Guid> SetIds)`.
  - `PUT /clients/{clientId}/members/{userId}/sets` → 200 `MembershipResponse` (403 non-admin; 404 non-member; 422 unknown set id; 409 last-admin).
  - `SetMembershipAsync` now also clears `GrantedSetIds` (raw-cap grant ⇒ not set-assigned).

- [ ] **Step 1: Add the request DTO**

In `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`, add after `SetMemberRequest` (line 35):

```csharp
/// <summary>Assign a member to one or more capability sets (the go-forward, live-bound grant).
/// Resolved capabilities are the union of the referenced sets' current capabilities — never client-supplied.</summary>
public sealed record AssignSetsRequest(IReadOnlyList<Guid> SetIds);
```

- [ ] **Step 2: Write the failing assignment tests**

Create `Backend/Accounting101.Ledger.Api.Tests/MemberSetAssignmentTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MemberSetAssignmentTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // Create a deployment-wide custom set via the admin surface; return its id.
    private async Task<Guid> CreateSetAsync(params string[] caps)
    {
        HttpClient admin = fixture.AdminClient();
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Assignable " + Guid.NewGuid().ToString("N"), null, caps)))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        return created.Id;
    }

    [Fact]
    public async Task Assigning_sets_resolves_the_members_capabilities_from_them()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);   // admin.users holder
        Guid setId = await CreateSetAsync(Capabilities.GlRead, Capabilities.ArWrite);

        Guid newUser = Guid.NewGuid();
        await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(newUser, ["ArClerk"], ["gl.read", "ar.read", "ar.write"]));

        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{newUser}/sets", new AssignSetsRequest([setId]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        MembershipResponse body = (await res.Content.ReadFromJsonAsync<MembershipResponse>())!;
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.ArWrite }.SetEquals(body.Capabilities));
    }

    [Fact]
    public async Task Assigning_an_unknown_set_is_422()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        Guid newUser = Guid.NewGuid();
        await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(newUser, ["Auditor"], ["gl.read"]));

        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{newUser}/sets", new AssignSetsRequest([Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Assigning_a_non_member_is_404()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        Guid setId = await CreateSetAsync(Capabilities.GlRead);
        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{Guid.NewGuid()}/sets", new AssignSetsRequest([setId]));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_non_admin_member_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Clerk);   // no admin.users
        Guid setId = await CreateSetAsync(Capabilities.GlRead);
        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{c.UserId}/sets", new AssignSetsRequest([setId]));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Assigning_the_last_admin_a_non_admin_set_is_409()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);   // sole admin.users holder
        Guid readOnly = await CreateSetAsync(Capabilities.GlRead, Capabilities.AuditRead);

        HttpResponseMessage res = await c.Http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{c.UserId}/sets", new AssignSetsRequest([readOnly]));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task A_raw_capability_edit_clears_a_prior_set_assignment()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        Guid setId = await CreateSetAsync(Capabilities.GlRead, Capabilities.ApWrite);

        Guid newUser = Guid.NewGuid();
        await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(newUser, ["Auditor"], ["gl.read"]));
        await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{newUser}/sets", new AssignSetsRequest([setId]));

        // Switch back to a raw-cap grant — the set reference must be dropped so the raw caps take effect.
        await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{newUser}",
            new SetMemberRequest(["Auditor"], ["gl.read"]));

        Membership m = (await fixture.Control().GetMembershipAsync(newUser, c.ClientId))!;
        Assert.Empty(m.GrantedSetIds);
        Assert.True(new HashSet<string> { Capabilities.GlRead }.SetEquals(m.Capabilities));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter MemberSetAssignmentTests`
Expected: FAIL — `/sets` route returns 404/405 (not mapped); mutual-exclusion test sees a lingering `GrantedSetIds`.

- [ ] **Step 4: Clear `GrantedSetIds` on the raw-cap write**

In `ControlStore.cs`, in `SetMembershipAsync` (the update builder around line 81), add the `GrantedSetIds` clear so a raw-cap grant is never a stale mix with set references:

```csharp
        var update = Builders<Membership>.Update
            .Set(m => m.GrantedRoles, roles)
            .Set(m => m.Capabilities, capabilities)
            .Set(m => m.GrantedSetIds, Array.Empty<Guid>())
            .SetOnInsert(m => m.Id, Guid.NewGuid())
            .SetOnInsert(m => m.UserId, userId)
            .SetOnInsert(m => m.ClientId, clientId);
```

- [ ] **Step 5: Add the assignment route to `MemberEndpoints`**

In `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`:

**(a)** Register the route in `MapMemberEndpoints` (after the existing `MapPut("/{userId:guid}", SetMember)` line):

```csharp
        g.MapPut("/{userId:guid}/sets", AssignSets);
```

**(b)** Add the handler (place it after `SetMember`, before `RemoveMember`):

```csharp
    // Assign a member to capability sets — the go-forward, live-bound grant. Resolved capabilities are
    // the union of the referenced sets' current capabilities (never client-supplied). Last-admin guarded.
    private static async Task<IResult> AssignSets(
        Guid clientId, Guid userId, AssignSetsRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!await control.IsMemberAsync(userId, clientId, ct)) return Results.NotFound();

        // Validate every set exists and gather the sets we resolve from.
        List<CapabilitySet> sets = [];
        foreach (Guid setId in request.SetIds)
        {
            CapabilitySet? set = await control.GetCapabilitySetAsync(setId, ct);
            if (set is null)
                return Results.Problem($"Unknown capability set '{setId}'.", statusCode: StatusCodes.Status422UnprocessableEntity);
            sets.Add(set);
        }

        HashSet<string> resolved = [];
        foreach (CapabilitySet set in sets) resolved.UnionWith(set.Capabilities);

        bool keepsAdmin = resolved.Contains(Capabilities.AdminUsers);
        if (await WouldLeaveNoAdmin(control, clientId, userId, keepsAdmin, ct))
            return Results.Problem("Cannot remove the last administrator.", statusCode: StatusCodes.Status409Conflict);

        await control.SetMembershipSetsAsync(userId, clientId, request.SetIds, ct);
        return Results.Ok(new MembershipResponse(
            userId, clientId, sets.Select(s => s.Name).ToList(), resolved.ToList()));
    }
```

- [ ] **Step 6: Run the assignment tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter MemberSetAssignmentTests`
Expected: PASS (6 tests).

- [ ] **Step 7: Run the full API test project**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests`
Expected: PASS — existing member/capability tests plus the new assignment tests.

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/AdminContracts.cs \
        Backend/Accounting101.Ledger.Api/Control/ControlStore.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/MemberSetAssignmentTests.cs
git commit -m "$(cat <<'EOF'
feat(access): per-client member set-assignment route with last-admin guard (AC-2)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Startup backfill — `GrantedRoles` → `GrantedSetIds`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/MembershipResolutionTests.cs` (append)

**Interfaces:**
- Consumes: `SeedBuiltinCapabilitySetsAsync`, `ListCapabilitySetsAsync`, `AddMembershipRolesAsync`, `GetMembershipAsync`, `GetCapabilitySetByNameAsync` (`ControlStore`); `RolePresets`, `LedgerRole`.
- Produces: `Task ControlStore.BackfillGrantedSetIdsAsync(CancellationToken ct = default)` — for each membership with empty `GrantedSetIds` and non-empty `GrantedRoles`, set `GrantedSetIds` to the built-in sets matching those role names. Idempotent. Runs from `CapabilitySetSeeder` after seeding.

- [ ] **Step 1: Write the failing backfill tests**

Append to `Backend/Accounting101.Ledger.Api.Tests/MembershipResolutionTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Backfill_populates_GrantedSetIds_from_granted_roles()
    {
        ControlStore control = fixture.Control();
        await control.SeedBuiltinCapabilitySetsAsync();

        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        await control.AddMembershipRolesAsync(user, client, [LedgerRole.Controller]);

        await control.BackfillGrantedSetIdsAsync();

        CapabilitySet controllerSet = (await control.GetCapabilitySetByNameAsync("Controller"))!;
        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.Contains(controllerSet.Id, m.GrantedSetIds);

        // And it is now live-bound: editing the built-in set moves the member.
        controllerSet.Capabilities = [Capabilities.GlRead];
        await control.UpdateCapabilitySetAsync(controllerSet);
        Membership after = (await control.GetMembershipAsync(user, client))!;
        Assert.True(new HashSet<string> { Capabilities.GlRead }.SetEquals(after.Capabilities));
    }

    [Fact]
    public async Task Backfill_leaves_an_already_set_assigned_member_untouched()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = await NewSetAsync(control, Capabilities.GlRead);
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        await control.SetMembershipSetsAsync(user, client, [set.Id]);

        await control.BackfillGrantedSetIdsAsync();

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.Equal([set.Id], m.GrantedSetIds);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter MembershipResolutionTests`
Expected: FAIL to compile — `BackfillGrantedSetIdsAsync` does not exist.

- [ ] **Step 3: Add `BackfillGrantedSetIdsAsync` to `ControlStore`**

In `ControlStore.cs`, add after `CountMembersReferencingSetAsync`:

```csharp
    /// <summary>One-time migration (idempotent): for every membership that still has no set references
    /// but does carry granted roles, set <see cref="Membership.GrantedSetIds"/> to the built-in sets of
    /// the same name. Role-based members created before AC-2 become explicit, live-bound set references.
    /// Members already carrying set ids (or with no roles) are skipped.</summary>
    public async Task BackfillGrantedSetIdsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CapabilitySet> catalog = await ListCapabilitySetsAsync(cancellationToken);
        Dictionary<string, Guid> idByName = catalog.ToDictionary(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase);

        List<Membership> all = await _memberships.Find(FilterDefinition<Membership>.Empty).ToListAsync(cancellationToken);
        foreach (Membership m in all)
        {
            if (m.GrantedSetIds.Count > 0 || m.GrantedRoles.Count == 0) continue;

            List<Guid> ids = [];
            foreach (LedgerRole role in m.GrantedRoles)
                if (idByName.TryGetValue(role.ToString(), out Guid id))
                    ids.Add(id);
            if (ids.Count == 0) continue;

            await _memberships.UpdateOneAsync(
                x => x.Id == m.Id,
                Builders<Membership>.Update.Set(x => x.GrantedSetIds, ids),
                cancellationToken: cancellationToken);
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter MembershipResolutionTests`
Expected: PASS (7 tests — 5 from Task 1 + 2 backfill).

- [ ] **Step 5: Run the backfill from the seeder on startup**

In `Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs`, replace `StartAsync` so it seeds then backfills:

```csharp
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await control.SeedBuiltinCapabilitySetsAsync(cancellationToken);
        await control.BackfillGrantedSetIdsAsync(cancellationToken);
    }
```

(Update the class summary line to mention the backfill, e.g. "Seeds the built-in capability sets and backfills legacy role grants to set references on startup (idempotent).")

- [ ] **Step 6: Run the full API test project**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests`
Expected: PASS. (At host startup the seeder now also backfills; existing role-based members created before host start become set-referenced, resolving to the same preset caps — every existing capability assertion still holds.)

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ControlStore.cs \
        Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs \
        Backend/Accounting101.Ledger.Api.Tests/MembershipResolutionTests.cs
git commit -m "$(cat <<'EOF'
feat(access): startup backfill of GrantedSetIds from legacy role grants (AC-2)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

- [ ] Run the full API test suite (and, if time permits, the whole solution):

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests`
Expected: all green.

- [ ] Sanity-check the enforcement invariant by inspection: `ModuleAccess.AuthorizeAsync` and `LedgerGateway` still call `GetMembershipAsync` and read `membership.Capabilities` — no edits there. The only behavioral change is that `Capabilities` is now resolved from referenced sets.

- [ ] Confirm only intended files are staged across the four commits (no `environment.ts`, no `.csproj`/`.slnx` churn).

---

## Self-Review notes (against the AC-2 spec section B + enforcement invariant)

- **"Membership gains `GrantedSetIds`; `Capabilities` becomes a read-time-resolved cache"** → Task 1 (field + `Resolve`). ✓
- **"Resolution — union of referenced sets' current capabilities, generalizing `Hydrate`"** → Task 1 `Resolve` precedence 1; `HydrateLegacy` preserves the old backfill for precedence 3. ✓
- **"Sets are deployment-wide and few; keep an in-memory catalog"** → **deliberately deferred.** Resolution loads the small catalog per call; a cache adds invalidation coupling that could silently defeat live-binding (the feature's whole point). Documented as a scope decision, not a gap. Enforcement stays correct and instant. ✓ (constraint stated in Global Constraints)
- **"Assignment surface — setting `GrantedSetIds`, resolved caps never client-supplied, last-admin guard preserved"** → Task 3 (`PUT .../sets`, union computed server-side, `WouldLeaveNoAdmin` with set-derived `keepsAdmin`). ✓
- **"`AddClientMemberRequest`/`SetMemberRequest` shift to sets"** → **partially, by addition not replacement.** The new `/sets` route is the go-forward set assignment; the Slice D `(Roles, Capabilities)` routes are kept for back-compat (standing constraint), now clearing `GrantedSetIds` on write (mutual exclusion). Full removal of the raw-cap routes is an AC-3/cleanup concern once the UI is set-based. ✓ (noted deviation, justified)
- **"Migration / back-compat — backfill `GrantedSetIds` from `GrantedRoles`; fall back to stored caps for legacy custom grants"** → Task 4 backfill + Task 1 `Resolve` precedence 2 (live role→set at read) and 3 (stored caps fallback). ✓
- **"Last-admin guard now tests 'still references a set that grants `admin.users`'"** → Task 3 resolves the requested sets' union and derives `keepsAdmin`; `WouldLeaveNoAdmin` reads other members' **resolved** caps (Task 1). ✓
- **Referential integrity (spec A + invariant c)** → Task 2 in-use DELETE guard (409) + `AffectedMemberCount` on edit for the blast-radius confirm; **rename is safe under id-references (no guard)** — a deliberate refinement of AC-1's name-based assumption. ✓
- **Enforcement invariant tests (a)/(b)/(c)/(d)** → (a) `Editing_a_set_changes_an_assigned_members_capabilities_on_next_read` (T1); (b) `Two_members_referencing_the_same_set_resolve_identically` (T1); (c) `Deleting_a_set_that_a_member_references_is_409` (T2); (d) `Assigning_the_last_admin_a_non_admin_set_is_409` (T3). ✓
- **Type consistency:** `GrantedSetIds : IReadOnlyList<Guid>`, `SetMembershipSetsAsync(Guid,Guid,IReadOnlyList<Guid>,CancellationToken)`, `CountMembersReferencingSetAsync(Guid) → Task<long>`, `BackfillGrantedSetIdsAsync(CancellationToken)`, `AssignSetsRequest(IReadOnlyList<Guid> SetIds)`, and `CapabilitySetResponse(..., int AffectedMemberCount = 0)` are used identically across Tasks 1–4 and their tests. ✓
- **Deferred to later AC slices (correctly out of AC-2 scope):** in-memory catalog cache; removal of the raw-cap routes; per-member additive overrides; the entire frontend (AC-3 Access Control area, AC-4 liveness). ✓
- **Existing-test safety:** `MemberManagementTests.Admin_lists...` asserts on the PUT **echo** response (endpoint returns `request.Capabilities`), not a resolved read — unaffected. `CapabilitiesTests.Overlapping_roles...` resolves `[ArClerk,ApClerk]` → built-in-set union == the stored preset union — unchanged. `Cannot_remove_the_last_admin` resolves the Admin built-in set (holds `admin.users`) — guard still fires. ✓
