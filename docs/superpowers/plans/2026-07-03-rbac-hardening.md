# RBAC Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the capability RBAC resist admin-sprawl and record who changed whose access — a no-self-escalation rule on every grant path, deployment-only "restricted" sets, narrow admin built-ins, and an append-only control-plane audit trail with a query endpoint.

**Architecture:** Four backend concerns plus one small frontend touch. (1) A shared `GrantScope` check rejects any grant that exceeds the caller's own capabilities (422) on all three member-grant paths. (2) `CapabilitySet` gains a `Restricted` flag; assigning a restricted set requires deployment admin (403); built-in Admin seeds restricted. (3) The seeder gains three narrow admin built-ins. (4) A new append-only `AdminAuditStore` records every control-plane mutation, surfaced via `GET /admin/audit`. Frontend: a `Restricted` checkbox in the existing set editor.

**Tech Stack:** C# / .NET 10, ASP.NET Core Minimal APIs, MongoDB.Driver, xUnit, EphemeralMongo (shared via `Accounting101.TestSupport.SharedMongo`), `WebApplicationFactory<Program>`. Frontend: Angular 22 (standalone, zoneless), `ng test`.

## Global Constraints

- **No-self-escalation (item #1):** on every member-grant path, if the caller is NOT a deployment admin (`admin=true` claim), the capabilities being granted must be a subset of the caller's own resolved capabilities on that client (`ControlStore.GetMembershipAsync(callerUserId, clientId).Capabilities`). First offending capability → **422** ProblemDetails naming it. Deployment admins are exempt. Caller id via `IActorFactory.Create(user).UserId`.
- **Restricted sets (item #2):** a `CapabilitySet.Restricted == true` set may be *assigned to a member* only by a deployment admin; a non-deployment-admin attempt → **403** (distinct from #1's 422). Built-in **Admin** seeds `Restricted = true`; all other built-ins `false`. Restricted enforcement is on the set-assignment path (`AssignSets`) only — raw-cap routes reference no set and are already covered by #1.
- **Handler ordering:** existing gate (`CallerMayManage` / `AdminAuthorization.MayAsync`) → existing validation (set/member existence, `TryParse`) → **no-escalation (422)** → **restricted-set (403)** → last-admin guard (409) → mutate → **audit append**.
- **Narrow admin built-ins (item #3):** seed **User Admin** `{admin.users, gl.read}`, **Fiscal Admin** `{admin.fiscal, gl.read}`, **Posting-Accounts Admin** `{admin.postingAccounts, gl.read}` — `Builtin = true`, `Restricted = false`, idempotent by name.
- **Audit (item #4):** append-only control-DB collection `adminAudit` via `AdminAuditStore` exposing ONLY `AppendAsync` + `QueryAsync` (no update/delete method may exist). Every control-plane mutation appends one entry after it succeeds. `GET /admin/audit` gated deployment-admin (`AdminEndpoints.Policy`), filterable by `clientId`/`actorUserId`/`targetUserId`, newest-first.
- **Commits:** stage explicit paths only — never `git add -A`/`.`. `UI/Angular/src/app/core/api/environment.ts` and IDE `.csproj`/`.slnx` churn stay UNCOMMITTED. Trailer required:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Test runners:** backend `dotnet test Backend/Accounting101.Ledger.Api.Tests`; frontend `cd UI/Angular && npx ng test --watch=false --include='**/<spec>.spec.ts'`.
- **Style:** explicit types (not `var`), file-scoped namespaces, `[BsonIgnoreExtraElements]` on control documents, XML-doc summaries on public members, `Results.Problem(..., statusCode:)` for errors.

---

## File Structure

**Backend — create:**
- `Backend/Accounting101.Ledger.Api/Endpoints/GrantScope.cs` — the no-escalation check (H-1).
- `Backend/Accounting101.Ledger.Api/Control/AdminAuditEntry.cs` — audit document + `AuditState` (H-4).
- `Backend/Accounting101.Ledger.Api/Control/AdminAuditStore.cs` — append-only store + `AdminAuditFilter` (H-4).
- `Backend/Accounting101.Ledger.Api/Endpoints/AdminAuditEndpoints.cs` — `GET /admin/audit` (H-6).
- Tests: `GrantScopeTests.cs`, `AdminAuditStoreTests.cs`, `AdminAuditWiringTests.cs`, `AdminAuditEndpointTests.cs`.

**Backend — modify:**
- `Control/CapabilitySet.cs` — add `Restricted` (H-2).
- `Control/ControlStore.cs` — seed Admin restricted (H-2) + narrow admin sets (H-3).
- `Contracts/CapabilitySetContracts.cs` — `Restricted` on request/response DTOs (H-2).
- `Endpoints/CapabilitySetEndpoints.cs` — set/read `Restricted` + audit wiring (H-2, H-5).
- `Endpoints/MemberEndpoints.cs` — no-escalation + restricted enforcement + audit wiring (H-1, H-2, H-5).
- `Endpoints/AdminEndpoints.cs` — no-escalation on AddMember + audit wiring + map audit endpoint (H-1, H-5, H-6).
- `Hosting/LedgerEngineExtensions.cs` — register `AdminAuditStore` (H-4).
- `Accounting101.Host/Program.cs` — map `AdminAuditEndpoints` (H-6).
- `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs` — `Audit()` helper (H-4).

**Frontend — modify (H-7):**
- `UI/Angular/src/app/core/capability-sets/capability-set.ts` — `restricted` on model + requests.
- `UI/Angular/src/app/core/capability-sets/capability-set.service.spec.ts` — cover `restricted` in the body.
- `UI/Angular/src/app/features/admin/capability-set-editor.ts` — `Restricted` checkbox.
- `UI/Angular/src/app/features/admin/capability-set-editor.spec.ts` — cover the checkbox.

---

### Task 1: No-self-escalation on every member-grant path

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/GrantScope.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`, `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/GrantScopeTests.cs`

**Interfaces:**
- Consumes: `ControlStore.GetMembershipAsync`, `IActorFactory.Create(user).UserId`, `user.HasClaim("admin","true")`, `RolePresets.For(role)`, `Capabilities.*`.
- Produces: `GrantScope.FirstNotHeldByCallerAsync(ClaimsPrincipal user, Guid clientId, IEnumerable<string> grantedCapabilities, IActorFactory actorFactory, ControlStore control, CancellationToken ct) → Task<string?>` — returns the first granted capability the caller does not hold, or `null` if the caller is a deployment admin or holds them all.

- [ ] **Step 1: Write the helper**

Create `Backend/Accounting101.Ledger.Api/Endpoints/GrantScope.cs`:

```csharp
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// No-self-escalation: a non-deployment-admin caller may only grant capabilities they themselves hold
/// on the client. Prevents a per-client <c>admin.users</c> holder from granting (or self-granting)
/// authority beyond their own — the difference between "we have RBAC" and RBAC that resists sprawl.
/// </summary>
internal static class GrantScope
{
    /// <summary>The first granted capability the caller does not hold, or null if the caller is a
    /// deployment admin (exempt) or holds every granted capability.</summary>
    public static async Task<string?> FirstNotHeldByCallerAsync(
        ClaimsPrincipal user, Guid clientId, IEnumerable<string> grantedCapabilities,
        IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (user.HasClaim("admin", "true")) return null;

        Actor actor = actorFactory.Create(user);
        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, ct);
        HashSet<string> own = membership is null ? [] : [.. membership.Capabilities];

        foreach (string capability in grantedCapabilities)
            if (!own.Contains(capability)) return capability;
        return null;
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `Backend/Accounting101.Ledger.Api.Tests/GrantScopeTests.cs`. These exercise the three HTTP grant paths end-to-end (a non-admin client member trying to grant beyond their scope):

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class GrantScopeTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // An ArClerk holds admin.users (added below) so it can manage members, but NOT gl.post.
    private async Task<SeededClient> SeedUserAdminClerkAsync()
    {
        SeededClient admin = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        // Add a second member who can manage users (admin.users) but is otherwise a narrow AR clerk.
        Guid clerk = Guid.NewGuid();
        await admin.Http.PostAsJsonAsync($"/clients/{admin.ClientId}/members",
            new AddClientMemberRequest(clerk, ["ArClerk"], ["gl.read", "ar.read", "ar.write", "admin.users"]));
        HttpClient clerkHttp = fixture.ClientFor(clerk, "User-Admin Clerk");
        return new SeededClient(admin.ClientId, admin.Database, clerk, clerkHttp);
    }

    [Fact]
    public async Task Raw_cap_grant_beyond_caller_scope_is_422()
    {
        SeededClient clerk = await SeedUserAdminClerkAsync();
        // The clerk lacks gl.post, so it cannot grant gl.post to anyone.
        HttpResponseMessage res = await clerk.Http.PostAsJsonAsync($"/clients/{clerk.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["Controller"], ["gl.read", "gl.post"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Set_assignment_beyond_caller_scope_is_422()
    {
        SeededClient clerk = await SeedUserAdminClerkAsync();
        // Assign the built-in Controller set (has gl.post, which the clerk lacks) to a new member.
        Guid target = Guid.NewGuid();
        await clerk.Http.PostAsJsonAsync($"/clients/{clerk.ClientId}/members",
            new AddClientMemberRequest(target, ["ArClerk"], ["gl.read", "ar.read", "ar.write"]));
        CapabilitySet controller = (await fixture.Control().GetCapabilitySetByNameAsync("Controller"))!;
        HttpResponseMessage res = await clerk.Http.PutAsJsonAsync(
            $"/clients/{clerk.ClientId}/members/{target}/sets", new AssignSetsRequest([controller.Id]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Grant_within_caller_scope_succeeds()
    {
        SeededClient clerk = await SeedUserAdminClerkAsync();
        // The clerk holds ar.write, so granting an AR-only member is fine.
        HttpResponseMessage res = await clerk.Http.PostAsJsonAsync($"/clients/{clerk.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["ArClerk"], ["gl.read", "ar.read", "ar.write"]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_may_grant_anything()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        HttpClient deploymentAdmin = fixture.AdminClient();
        // A deployment admin is exempt — can grant gl.post even though it holds no per-client membership.
        HttpResponseMessage res = await deploymentAdmin.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["Controller"], ["gl.read", "gl.post"]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter GrantScopeTests`
Expected: FAIL — the two 422 tests currently return 200 (no escalation check yet). (`GrantScope.cs` from Step 1 already compiles.)

- [ ] **Step 4: Wire the check into the three grant paths**

In `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`:

**AddMember** — after the `TryParse` line, before the `IsMemberAsync` check:

```csharp
        if (await GrantScope.FirstNotHeldByCallerAsync(user, clientId, request.Capabilities, actorFactory, control, ct) is { } badAdd)
            return Results.Problem($"Cannot grant '{badAdd}' — you do not hold it.", statusCode: StatusCodes.Status422UnprocessableEntity);
```

**SetMember** — after `TryParse`, before the `IsMemberAsync`/last-admin block:

```csharp
        if (await GrantScope.FirstNotHeldByCallerAsync(user, clientId, request.Capabilities, actorFactory, control, ct) is { } badSet)
            return Results.Problem($"Cannot grant '{badSet}' — you do not hold it.", statusCode: StatusCodes.Status422UnprocessableEntity);
```

**AssignSets** — after building the `resolved` union, before the `keepsAdmin`/last-admin block:

```csharp
        if (await GrantScope.FirstNotHeldByCallerAsync(user, clientId, resolved, actorFactory, control, ct) is { } badAssign)
            return Results.Problem($"Cannot grant '{badAssign}' — you do not hold it.", statusCode: StatusCodes.Status422UnprocessableEntity);
```

In `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs`, in `AddMember` — after the `Enum.TryParse` role parse and before/after the `GetClientAsync` null check (place it right after the role parse):

```csharp
        if (await GrantScope.FirstNotHeldByCallerAsync(user, clientId, RolePresets.For(role), actorFactory, control, cancellationToken) is { } badRole)
            return Results.Problem($"Cannot grant '{badRole}' — you do not hold it.", statusCode: StatusCodes.Status422UnprocessableEntity);
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter GrantScopeTests`
Expected: PASS (4 tests). Then full project: `dotnet test Backend/Accounting101.Ledger.Api.Tests` — expected green (existing member/admin tests: their callers are deployment admins or hold the caps they grant, so the check is a no-op for them; verify no regression).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/GrantScope.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/GrantScopeTests.cs
git commit -m "$(cat <<'EOF'
feat(access): no-self-escalation guard on all member-grant paths (RBAC hardening)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `Restricted` capability sets — deployment-admin-only assignment

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/CapabilitySet.cs`, `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`, `Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs`, `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`, `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/RestrictedSetTests.cs` (create)

**Interfaces:**
- Consumes: H-1's shape (deployment-admin detection); `ControlStore` set CRUD/seeding.
- Produces: `CapabilitySet.Restricted : bool`; `CapabilitySetResponse(..., bool Restricted = false)`; `CreateCapabilitySetRequest`/`UpdateCapabilitySetRequest` gain `bool Restricted = false`. Assigning any restricted set via `AssignSets` by a non-deployment-admin → 403.

- [ ] **Step 1: Add the field**

In `Backend/Accounting101.Ledger.Api/Control/CapabilitySet.cs`, add after `Builtin`:

```csharp
    /// <summary>When true, this set may be ASSIGNED to a member only by a deployment admin — even a
    /// full per-client admin cannot delegate it. The built-in Admin set defaults restricted.</summary>
    public bool Restricted { get; set; }
```

- [ ] **Step 2: Add `Restricted` to the DTOs**

In `Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs`, append a trailing `Restricted` (default false) to all three records:

```csharp
public sealed record CapabilitySetResponse(
    Guid Id, string Name, string? Description, IReadOnlyList<string> Capabilities, bool Builtin,
    int AffectedMemberCount = 0, bool Restricted = false);

public sealed record CreateCapabilitySetRequest(
    string Name, string? Description, IReadOnlyList<string> Capabilities, bool Restricted = false);

public sealed record UpdateCapabilitySetRequest(
    string Name, string? Description, IReadOnlyList<string> Capabilities, bool Restricted = false);
```

- [ ] **Step 3: Write the failing tests**

Create `Backend/Accounting101.Ledger.Api.Tests/RestrictedSetTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class RestrictedSetTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Builtin_Admin_set_is_restricted()
    {
        HttpClient admin = fixture.AdminClient();
        List<CapabilitySetResponse> sets =
            (await admin.GetFromJsonAsync<List<CapabilitySetResponse>>("/capability-sets"))!;
        Assert.True(sets.First(s => s.Name == "Admin").Restricted);
        Assert.False(sets.First(s => s.Name == "Controller").Restricted);
    }

    [Fact]
    public async Task Create_can_mark_a_set_restricted_and_it_round_trips()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Locked " + Guid.NewGuid().ToString("N");
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name, null, ["gl.read"], Restricted: true)))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        Assert.True(created.Restricted);
    }

    [Fact]
    public async Task Non_deployment_admin_cannot_assign_a_restricted_set()
    {
        // A full client Admin holds every capability, so #1 (no-escalation) would pass — the 403 is
        // purely the restricted-set tier guard.
        SeededClient clientAdmin = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        CapabilitySet adminSet = (await fixture.Control().GetCapabilitySetByNameAsync("Admin"))!;
        Guid target = Guid.NewGuid();
        await clientAdmin.Http.PostAsJsonAsync($"/clients/{clientAdmin.ClientId}/members",
            new AddClientMemberRequest(target, ["Auditor"], ["gl.read"]));

        HttpResponseMessage res = await clientAdmin.Http.PutAsJsonAsync(
            $"/clients/{clientAdmin.ClientId}/members/{target}/sets", new AssignSetsRequest([adminSet.Id]));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_can_assign_a_restricted_set()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        HttpClient deploymentAdmin = fixture.AdminClient();
        CapabilitySet adminSet = (await fixture.Control().GetCapabilitySetByNameAsync("Admin"))!;
        Guid target = Guid.NewGuid();
        await deploymentAdmin.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(target, ["Auditor"], ["gl.read"]));

        HttpResponseMessage res = await deploymentAdmin.PutAsJsonAsync(
            $"/clients/{c.ClientId}/members/{target}/sets", new AssignSetsRequest([adminSet.Id]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
```

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter RestrictedSetTests`
Expected: FAIL — Admin isn't seeded restricted yet, Create ignores `Restricted`, and AssignSets has no restricted guard.

- [ ] **Step 5: Seed Admin restricted + read/write Restricted in the endpoints**

In `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`, in `SeedBuiltinCapabilitySetsAsync`, set `Restricted` when inserting the per-role set (the loop that builds `new CapabilitySet { ... }`):

```csharp
                new CapabilitySet
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Description = $"Built-in preset for the {name} role.",
                    Capabilities = [.. RolePresets.For(role)],
                    Builtin = true,
                    Restricted = role == LedgerRole.Admin,
                },
```

In `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`:

Update `ToResponse` to carry `Restricted`:

```csharp
    private static CapabilitySetResponse ToResponse(CapabilitySet s) =>
        new(s.Id, s.Name, s.Description, s.Capabilities, s.Builtin, Restricted: s.Restricted);
```

In `Create`, set it on the new set (`Builtin = false,` line):

```csharp
            Builtin = false,
            Restricted = request.Restricted,
```

In `Update`, set it alongside the other fields (after `existing.Capabilities = capabilities;`):

```csharp
        existing.Restricted = request.Restricted;
```

In `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`, in `AssignSets`, add the restricted guard AFTER the no-escalation check (H-1) and BEFORE the `keepsAdmin`/last-admin block:

```csharp
        if (sets.Any(s => s.Restricted) && !user.HasClaim("admin", "true"))
            return Results.Problem("Only a deployment admin may assign a restricted capability set.",
                statusCode: StatusCodes.Status403Forbidden);
```

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter RestrictedSetTests`
Expected: PASS (4 tests). Then full project: `dotnet test Backend/Accounting101.Ledger.Api.Tests` — expected green (the appended DTO param defaults to false, so existing set-endpoint/assignment tests are unaffected).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/CapabilitySet.cs \
        Backend/Accounting101.Ledger.Api/Control/ControlStore.cs \
        Backend/Accounting101.Ledger.Contracts/CapabilitySetContracts.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/RestrictedSetTests.cs
git commit -m "$(cat <<'EOF'
feat(access): Restricted capability sets — deployment-admin-only assignment (RBAC hardening)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Seed narrow admin built-in sets

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs` (append)

**Interfaces:**
- Consumes: `SeedBuiltinCapabilitySetsAsync` (H-2 shape), `Capabilities.AdminUsers`/`AdminFiscal`/`AdminPostingAccounts`/`GlRead`.
- Produces: three additional non-role built-in sets (`Builtin=true`, `Restricted=false`), idempotent by name.

- [ ] **Step 1: Write the failing test**

Append to `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Seeding_creates_the_narrow_admin_builtins()
    {
        ControlStore control = fixture.Control();
        await control.SeedBuiltinCapabilitySetsAsync();

        CapabilitySet userAdmin = (await control.GetCapabilitySetByNameAsync("User Admin"))!;
        Assert.True(userAdmin.Builtin);
        Assert.False(userAdmin.Restricted);
        Assert.True(new HashSet<string> { Capabilities.AdminUsers, Capabilities.GlRead }.SetEquals(userAdmin.Capabilities));

        Assert.NotNull(await control.GetCapabilitySetByNameAsync("Fiscal Admin"));
        Assert.NotNull(await control.GetCapabilitySetByNameAsync("Posting-Accounts Admin"));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetStoreTests`
Expected: FAIL — the narrow admin sets are not seeded.

- [ ] **Step 3: Seed the narrow admin sets**

In `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`, at the END of `SeedBuiltinCapabilitySetsAsync` (after the per-role loop, before the method's closing brace), add a second pass:

```csharp
        // Narrow admin built-ins — one delegable single-power admin set each, so granting (say) just
        // user management is one click instead of a hand-assembled set that tempts a full-Admin grant.
        (string Name, string[] Capabilities)[] narrowAdmins =
        [
            ("User Admin", [Capabilities.AdminUsers, Capabilities.GlRead]),
            ("Fiscal Admin", [Capabilities.AdminFiscal, Capabilities.GlRead]),
            ("Posting-Accounts Admin", [Capabilities.AdminPostingAccounts, Capabilities.GlRead]),
        ];
        foreach ((string name, string[] capabilities) in narrowAdmins)
        {
            if (await GetCapabilitySetByNameAsync(name, cancellationToken) is not null) continue;
            await _capabilitySets.InsertOneAsync(
                new CapabilitySet
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Description = $"Built-in narrow admin set: {name}.",
                    Capabilities = capabilities,
                    Builtin = true,
                    Restricted = false,
                },
                cancellationToken: cancellationToken);
        }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CapabilitySetStoreTests`
Expected: PASS. Then full project: `dotnet test Backend/Accounting101.Ledger.Api.Tests` — expected green (seeding more built-ins doesn't affect existing tests; the unique-Name index still holds).

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ControlStore.cs \
        Backend/Accounting101.Ledger.Api.Tests/CapabilitySetStoreTests.cs
git commit -m "$(cat <<'EOF'
feat(access): seed narrow admin built-in sets (User/Fiscal/Posting-Accounts Admin) (RBAC hardening)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Append-only `AdminAuditStore`

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Control/AdminAuditEntry.cs`, `Backend/Accounting101.Ledger.Api/Control/AdminAuditStore.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`, `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/AdminAuditStoreTests.cs`

**Interfaces:**
- Consumes: `IMongoDatabase` (control DB), `LedgerMongoBootstrap.RegisterOnce()`.
- Produces:
  - `AdminAuditEntry { Guid Id; DateTime Timestamp; Guid ActorUserId; bool ActorIsDeploymentAdmin; string Action; Guid? ClientId; Guid? TargetUserId; Guid? TargetSetId; AuditState? Before; AuditState? After; }`
  - `AuditState { IReadOnlyList<Guid>? SetIds; IReadOnlyList<string>? Capabilities; string? Name; bool? Restricted; }`
  - `AdminAuditStore(IMongoDatabase)` with `Task AppendAsync(AdminAuditEntry, CancellationToken=default)` and `Task<IReadOnlyList<AdminAuditEntry>> QueryAsync(AdminAuditFilter, CancellationToken=default)` — and NO other public instance methods.
  - `AdminAuditFilter(Guid? ClientId = null, Guid? ActorUserId = null, Guid? TargetUserId = null, int Limit = 100)`.
  - `ApiFixture.Audit()` → `AdminAuditStore` bound to the fixture control DB.

- [ ] **Step 1: Write the document + store**

Create `Backend/Accounting101.Ledger.Api/Control/AdminAuditEntry.cs`:

```csharp
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>An append-only record of one control-plane access change (who, what, target, before→after).</summary>
[BsonIgnoreExtraElements]
public sealed class AdminAuditEntry
{
    [BsonId] public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid ActorUserId { get; set; }
    public bool ActorIsDeploymentAdmin { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid? ClientId { get; set; }
    public Guid? TargetUserId { get; set; }
    public Guid? TargetSetId { get; set; }
    public AuditState? Before { get; set; }
    public AuditState? After { get; set; }
}

/// <summary>A small snapshot of the changed thing: a member's sets/caps, or a set's definition.</summary>
[BsonIgnoreExtraElements]
public sealed class AuditState
{
    public IReadOnlyList<Guid>? SetIds { get; set; }
    public IReadOnlyList<string>? Capabilities { get; set; }
    public string? Name { get; set; }
    public bool? Restricted { get; set; }
}
```

Create `Backend/Accounting101.Ledger.Api/Control/AdminAuditStore.cs`:

```csharp
using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Control;

/// <summary>Append-only audit of control-plane access changes. Exposes ONLY append + query — there is
/// deliberately no update or delete method, so the application cannot rewrite the record.</summary>
public sealed class AdminAuditStore
{
    private readonly IMongoCollection<AdminAuditEntry> _entries;

    static AdminAuditStore() => LedgerMongoBootstrap.RegisterOnce();

    public AdminAuditStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _entries = database.GetCollection<AdminAuditEntry>("adminAudit");
    }

    /// <summary>Append one entry. Insert-only.</summary>
    public Task AppendAsync(AdminAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _entries.InsertOneAsync(entry, cancellationToken: cancellationToken);
    }

    /// <summary>Entries matching the filter, newest-first, capped at <see cref="AdminAuditFilter.Limit"/>.</summary>
    public async Task<IReadOnlyList<AdminAuditEntry>> QueryAsync(AdminAuditFilter filter, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<AdminAuditEntry> b = Builders<AdminAuditEntry>.Filter;
        List<FilterDefinition<AdminAuditEntry>> clauses = [];
        if (filter.ClientId is { } clientId) clauses.Add(b.Eq(x => x.ClientId, clientId));
        if (filter.ActorUserId is { } actor) clauses.Add(b.Eq(x => x.ActorUserId, actor));
        if (filter.TargetUserId is { } target) clauses.Add(b.Eq(x => x.TargetUserId, target));
        FilterDefinition<AdminAuditEntry> query = clauses.Count > 0 ? b.And(clauses) : b.Empty;

        return await _entries.Find(query)
            .SortByDescending(x => x.Timestamp)
            .Limit(filter.Limit)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Filter for <see cref="AdminAuditStore.QueryAsync"/>. All criteria optional; ANDed.</summary>
public sealed record AdminAuditFilter(Guid? ClientId = null, Guid? ActorUserId = null, Guid? TargetUserId = null, int Limit = 100);
```

- [ ] **Step 2: Register the store + add the fixture helper**

In `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`, after the `ControlStore` registration (the `services.AddSingleton(sp => new ControlStore(...))` block):

```csharp
        services.AddSingleton(sp =>
            new AdminAuditStore(sp.GetRequiredService<IMongoClient>().GetDatabase(controlDatabase)));
```

In `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`, add beside the existing `Control()` helper:

```csharp
    /// <summary>An audit store bound to the same control DB the app writes — for asserting audit entries.</summary>
    public AdminAuditStore Audit() => new(Mongo.GetDatabase(ControlDatabase));
```

- [ ] **Step 3: Write the failing store tests**

Create `Backend/Accounting101.Ledger.Api.Tests/AdminAuditStoreTests.cs`:

```csharp
using System.Reflection;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class AdminAuditStoreTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Append_then_query_round_trips_newest_first_and_filters()
    {
        AdminAuditStore audit = fixture.Audit();
        Guid actor = Guid.NewGuid(), client = Guid.NewGuid(), target = Guid.NewGuid();

        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ActorUserId = actor, Action = "MemberAdded", ClientId = client, TargetUserId = target,
        });
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            ActorUserId = actor, Action = "MemberRemoved", ClientId = client, TargetUserId = target,
        });

        IReadOnlyList<AdminAuditEntry> byActor = await audit.QueryAsync(new AdminAuditFilter(ActorUserId: actor));
        Assert.Equal(2, byActor.Count);
        Assert.Equal("MemberRemoved", byActor[0].Action);   // newest first
        Assert.Equal("MemberAdded", byActor[1].Action);

        IReadOnlyList<AdminAuditEntry> other = await audit.QueryAsync(new AdminAuditFilter(ActorUserId: Guid.NewGuid()));
        Assert.Empty(other);
    }

    [Fact]
    public void Store_exposes_no_mutation_of_existing_entries()
    {
        string[] methods = typeof(AdminAuditStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).ToArray();
        Assert.DoesNotContain(methods, n =>
            n.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Replace", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Remove", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("AppendAsync", methods);
        Assert.Contains("QueryAsync", methods);
    }
}
```

- [ ] **Step 4: Run to verify they fail then pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter AdminAuditStoreTests`
Expected: initially FAIL to compile (no `AdminAuditStore`), then after Steps 1–2 are in place, PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/AdminAuditEntry.cs \
        Backend/Accounting101.Ledger.Api/Control/AdminAuditStore.cs \
        Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs \
        Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs \
        Backend/Accounting101.Ledger.Api.Tests/AdminAuditStoreTests.cs
git commit -m "$(cat <<'EOF'
feat(access): append-only AdminAuditStore for control-plane changes (RBAC hardening)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Wire every control-plane mutation to append an audit entry

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`, `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs`, `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/AdminAuditWiringTests.cs` (create)

**Interfaces:**
- Consumes: `AdminAuditStore.AppendAsync` (H-4), `IActorFactory.Create(user).UserId`, `user.HasClaim("admin","true")`, `ControlStore.GetMembershipAsync`, existing set `existing` snapshots.
- Produces: each control-plane mutation writes one `AdminAuditEntry` with the correct `Action`, actor, target, and before→after. Handlers gain an `AdminAuditStore audit` parameter.

**Note on timestamps:** use `DateTime.UtcNow` at append time.

- [ ] **Step 1: Write the failing wiring tests**

Create `Backend/Accounting101.Ledger.Api.Tests/AdminAuditWiringTests.cs`:

```csharp
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class AdminAuditWiringTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Assigning_sets_writes_an_audit_entry_naming_actor_and_target()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        CapabilitySet arClerk = (await fixture.Control().GetCapabilitySetByNameAsync("ArClerk"))!;
        Guid target = Guid.NewGuid();
        await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(target, ["Auditor"], ["gl.read"]));

        await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{target}/sets",
            new AssignSetsRequest([arClerk.Id]));

        IReadOnlyList<AdminAuditEntry> entries =
            await fixture.Audit().QueryAsync(new AdminAuditFilter(TargetUserId: target));
        Assert.Contains(entries, e => e.Action == "MemberSetsAssigned"
            && e.ClientId == c.ClientId && e.After!.SetIds!.Contains(arClerk.Id));
    }

    [Fact]
    public async Task Editing_a_set_writes_a_before_after_audit_entry()
    {
        HttpClient admin = fixture.AdminClient();
        string name = "Audited " + Guid.NewGuid().ToString("N");
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest(name, null, ["gl.read"]))).Content.ReadFromJsonAsync<CapabilitySetResponse>())!;

        await admin.PutAsJsonAsync($"/capability-sets/{created.Id}",
            new UpdateCapabilitySetRequest(name, null, ["gl.read", "gl.post"]));

        IReadOnlyList<AdminAuditEntry> entries =
            await fixture.Audit().QueryAsync(new AdminAuditFilter(Limit: 500));
        AdminAuditEntry edit = entries.First(e => e.Action == "SetUpdated" && e.TargetSetId == created.Id);
        Assert.Contains("gl.read", edit.Before!.Capabilities!);
        Assert.DoesNotContain("gl.post", edit.Before!.Capabilities!);
        Assert.Contains("gl.post", edit.After!.Capabilities!);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter AdminAuditWiringTests`
Expected: FAIL — no entries are written (handlers don't append yet).

- [ ] **Step 3: Wire the member endpoints**

In `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`, add a private helper (below `WouldLeaveNoAdmin`):

```csharp
    private static AdminAuditEntry AuditEntry(
        ClaimsPrincipal user, IActorFactory actorFactory, string action, Guid clientId,
        Guid? targetUserId = null, AuditState? before = null, AuditState? after = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            ActorUserId = actorFactory.Create(user).UserId,
            ActorIsDeploymentAdmin = user.HasClaim("admin", "true"),
            Action = action,
            ClientId = clientId,
            TargetUserId = targetUserId,
            Before = before,
            After = after,
        };
```

Add `AdminAuditStore audit` to the parameter list of `AddMember`, `SetMember`, `AssignSets`, and `RemoveMember` (place it after `ControlStore control`). Then append after each mutation:

**AddMember** — after `await control.SetMembershipAsync(...)`, before the return:

```csharp
        await audit.AppendAsync(AuditEntry(user, actorFactory, "MemberAdded", clientId, request.UserId,
            after: new AuditState { Capabilities = request.Capabilities }), ct);
```

**SetMember** — capture before, append after `SetMembershipAsync`:

```csharp
        // (before the mutation) snapshot the member's current caps:
        Membership? beforeSet = await control.GetMembershipAsync(userId, clientId, ct);
        // (after control.SetMembershipAsync(...))
        await audit.AppendAsync(AuditEntry(user, actorFactory, "MemberCapabilitiesSet", clientId, userId,
            before: new AuditState { Capabilities = beforeSet?.Capabilities.ToList() },
            after: new AuditState { Capabilities = request.Capabilities }), ct);
```

**AssignSets** — capture before at the top (right after the `IsMemberAsync` check), append after `SetMembershipSetsAsync`:

```csharp
        // (right after IsMemberAsync passes) snapshot current sets/caps:
        Membership? beforeAssign = await control.GetMembershipAsync(userId, clientId, ct);
        // (after control.SetMembershipSetsAsync(...))
        await audit.AppendAsync(AuditEntry(user, actorFactory, "MemberSetsAssigned", clientId, userId,
            before: new AuditState { SetIds = beforeAssign?.GrantedSetIds.ToList(), Capabilities = beforeAssign?.Capabilities.ToList() },
            after: new AuditState { SetIds = request.SetIds, Capabilities = resolved.ToList() }), ct);
```

**RemoveMember** — capture before, append after `RemoveMembershipAsync`:

```csharp
        // (right after IsMemberAsync passes) snapshot:
        Membership? beforeRemove = await control.GetMembershipAsync(userId, clientId, ct);
        // (after control.RemoveMembershipAsync(...))
        await audit.AppendAsync(AuditEntry(user, actorFactory, "MemberRemoved", clientId, userId,
            before: new AuditState { SetIds = beforeRemove?.GrantedSetIds.ToList(), Capabilities = beforeRemove?.Capabilities.ToList() }), ct);
```

- [ ] **Step 4: Wire the deployment AddMember**

In `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs`, add `AdminAuditStore audit` to `AddMember`'s parameters and append after `AddMembershipAsync`:

```csharp
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow,
            ActorUserId = actorFactory.Create(user).UserId,
            ActorIsDeploymentAdmin = user.HasClaim("admin", "true"),
            Action = "MemberAdded", ClientId = clientId, TargetUserId = request.UserId,
            After = new AuditState { Capabilities = [.. RolePresets.For(role)] },
        }, cancellationToken);
```

- [ ] **Step 5: Wire the capability-set endpoints**

In `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`, add `AdminAuditStore audit` to `Create`, `Update`, `Delete`. This surface has no per-client actor context beyond the deployment-admin token, so build the entry inline. Add a small helper:

```csharp
    private static AuditState SetState(CapabilitySet s) =>
        new() { Name = s.Name, Restricted = s.Restricted, Capabilities = s.Capabilities.ToList() };
```

**Create** — after `await control.CreateCapabilitySetAsync(set, ct)`:

```csharp
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, ActorIsDeploymentAdmin = true,
            Action = "SetCreated", TargetSetId = set.Id, After = SetState(set),
        }, ct);
```

**Update** — capture `before` BEFORE mutating `existing` (i.e. snapshot at the top, right after the null check), append after `UpdateCapabilitySetAsync`:

```csharp
        // (right after `existing is null` check) snapshot the pre-edit state:
        AuditState beforeState = SetState(existing);
        // (after control.UpdateCapabilitySetAsync(existing, ct))
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, ActorIsDeploymentAdmin = true,
            Action = "SetUpdated", TargetSetId = id, Before = beforeState, After = SetState(existing),
        }, ct);
```

**Delete** — after `await control.DeleteCapabilitySetAsync(id, ct)`:

```csharp
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, ActorIsDeploymentAdmin = true,
            Action = "SetDeleted", TargetSetId = id, Before = SetState(existing),
        }, ct);
```

Add `using Accounting101.Ledger.Api.Control;` if not already present (it is — `ControlStore`/`CapabilitySet` come from there).

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter AdminAuditWiringTests`
Expected: PASS (2 tests). Then full project: `dotnet test Backend/Accounting101.Ledger.Api.Tests` — expected green (adding an injected parameter + append is additive; existing endpoint tests still pass because the app registers `AdminAuditStore` in DI).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/AdminAuditWiringTests.cs
git commit -m "$(cat <<'EOF'
feat(access): write an audit entry for every control-plane mutation (RBAC hardening)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: `GET /admin/audit` query endpoint

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/AdminAuditEndpoints.cs`
- Modify: `Accounting101.Host/Program.cs`, `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/AdminAuditEndpointTests.cs`

**Interfaces:**
- Consumes: `AdminAuditStore.QueryAsync` + `AdminAuditFilter` (H-4), `AdminEndpoints.Policy` (`"DeploymentAdmin"`).
- Produces: `GET /admin/audit?clientId=&actorUserId=&targetUserId=&limit=` → 200 `AdminAuditEntryResponse[]`, deployment-admin gated; DTO `AdminAuditEntryResponse`.

- [ ] **Step 1: Add the response DTO**

In `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`, append:

```csharp
/// <summary>One control-plane audit entry as returned by GET /admin/audit.</summary>
public sealed record AdminAuditEntryResponse(
    Guid Id, DateTime Timestamp, Guid ActorUserId, bool ActorIsDeploymentAdmin, string Action,
    Guid? ClientId, Guid? TargetUserId, Guid? TargetSetId,
    IReadOnlyList<string>? BeforeCapabilities, IReadOnlyList<string>? AfterCapabilities);
```

- [ ] **Step 2: Write the failing endpoint tests**

Create `Backend/Accounting101.Ledger.Api.Tests/AdminAuditEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class AdminAuditEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Deployment_admin_can_query_the_audit_trail()
    {
        HttpClient admin = fixture.AdminClient();
        Guid target = Guid.NewGuid();
        await fixture.Audit().AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, ActorUserId = Guid.NewGuid(),
            Action = "MemberAdded", TargetUserId = target,
        });

        List<AdminAuditEntryResponse> entries =
            (await admin.GetFromJsonAsync<List<AdminAuditEntryResponse>>($"/admin/audit?targetUserId={target}"))!;
        Assert.Contains(entries, e => e.Action == "MemberAdded" && e.TargetUserId == target);
    }

    [Fact]
    public async Task A_non_deployment_admin_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        HttpResponseMessage res = await c.Http.GetAsync("/admin/audit");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter AdminAuditEndpointTests`
Expected: FAIL — `/admin/audit` is not mapped (404, and the non-admin test may 404 too).

- [ ] **Step 4: Write the endpoint**

Create `Backend/Accounting101.Ledger.Api/Endpoints/AdminAuditEndpoints.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>Read-only view of the control-plane audit trail. Deployment-admin only — the log spans all
/// clients and records who changed whose access.</summary>
public static class AdminAuditEndpoints
{
    public static void MapAdminAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/admin")
           .RequireAuthorization(AdminEndpoints.Policy)
           .MapGet("/audit", Query);
    }

    private static async Task<IResult> Query(
        AdminAuditStore audit, CancellationToken ct,
        Guid? clientId = null, Guid? actorUserId = null, Guid? targetUserId = null, int limit = 100)
    {
        IReadOnlyList<AdminAuditEntry> entries =
            await audit.QueryAsync(new AdminAuditFilter(clientId, actorUserId, targetUserId, limit), ct);
        return Results.Ok(entries.Select(e => new AdminAuditEntryResponse(
            e.Id, e.Timestamp, e.ActorUserId, e.ActorIsDeploymentAdmin, e.Action,
            e.ClientId, e.TargetUserId, e.TargetSetId,
            e.Before?.Capabilities, e.After?.Capabilities)).ToList());
    }
}
```

- [ ] **Step 5: Map it in the host**

In `Accounting101.Host/Program.cs`, after `app.MapCapabilitySetEndpoints();` (or beside the other `app.Map*Endpoints()` calls):

```csharp
app.MapAdminAuditEndpoints();
```

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter AdminAuditEndpointTests`
Expected: PASS (2 tests). Then the full project: `dotnet test Backend/Accounting101.Ledger.Api.Tests` — expected all green.

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/AdminAuditEndpoints.cs \
        Backend/Accounting101.Ledger.Contracts/AdminContracts.cs \
        Accounting101.Host/Program.cs \
        Backend/Accounting101.Ledger.Api.Tests/AdminAuditEndpointTests.cs
git commit -m "$(cat <<'EOF'
feat(access): GET /admin/audit query endpoint (deployment-admin) (RBAC hardening)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Frontend — `Restricted` checkbox in the set editor

**Files:**
- Modify: `UI/Angular/src/app/core/capability-sets/capability-set.ts`, `capability-set.service.spec.ts`, `UI/Angular/src/app/features/admin/capability-set-editor.ts`, `capability-set-editor.spec.ts`

**Interfaces:**
- Consumes: the `restricted` field on `CapabilitySetResponse`/create/update (H-2 backend); the existing `CapabilitySetEditor` (name/description/capability picker + confirm-on-save).
- Produces: `CapabilitySet.restricted: boolean`; `CreateCapabilitySetRequest`/`UpdateCapabilitySetRequest` gain `restricted?: boolean`; the editor has a `Restricted` checkbox that round-trips through save.

- [ ] **Step 1: Add `restricted` to the models**

In `UI/Angular/src/app/core/capability-sets/capability-set.ts`, add `restricted` to the interfaces:

```ts
export interface CapabilitySet {
  id: string;
  name: string;
  description?: string;
  capabilities: string[];
  builtin: boolean;
  affectedMemberCount: number;
  restricted: boolean;
}

export interface CreateCapabilitySetRequest {
  name: string;
  description?: string;
  capabilities: string[];
  restricted?: boolean;
}

export interface UpdateCapabilitySetRequest {
  name: string;
  description?: string;
  capabilities: string[];
  restricted?: boolean;
}
```

- [ ] **Step 2: Write the failing editor test**

Add to `UI/Angular/src/app/features/admin/capability-set-editor.spec.ts` a case asserting the editor sends `restricted` (read the file first to match its existing `seed`/`route`/`CATALOG` helpers and HttpTestingController flow). Append inside the `describe`:

```ts
  it('sends the restricted flag when creating a set', () => {
    seed(null); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(CapabilitySetEditor);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capabilities/catalog`).flush(CATALOG);
    f.detectChanges();
    const c = f.componentInstance as CapabilitySetEditor;
    c.setName('Locked'); c.toggleCapability('gl.read'); c.toggleRestricted(); f.detectChanges();
    c.save();
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets`);
    expect(req.request.body.restricted).toBe(true);
    req.flush({ id: 'x', name: 'Locked', capabilities: ['gl.read'], builtin: false, affectedMemberCount: 0, restricted: true });
  });
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-set-editor.spec.ts'`
Expected: FAIL — `toggleRestricted` does not exist / `restricted` not sent.

- [ ] **Step 4: Add the checkbox to the editor**

In `UI/Angular/src/app/features/admin/capability-set-editor.ts`:

Add a `restricted` signal beside the others (`name`/`description`/`selected`):

```ts
  protected readonly restricted = signal(false);
```

Add a toggle method (beside `toggleCapability`):

```ts
  toggleRestricted(): void { this.restricted.update((v) => !v); }
```

In the constructor's edit-load branch (where `name`/`description`/`selected` are set from the fetched set), also seed it:

```ts
          this.restricted.set(set.restricted);
```

In `persist()`, include it in the request object (the `req` built from name/description/capabilities):

```ts
      restricted: this.restricted(),
```

In the template, add a checkbox after the capability picker `<div>` (before the confirm/save block), matching the raw-checkbox style used for capabilities:

```html
    <label class="flex items-center gap-2 mt-3">
      <input type="checkbox" [checked]="restricted()" (change)="toggleRestricted()" />
      <span>Restricted — only a deployment admin may assign this set</span>
    </label>
```

- [ ] **Step 5: Run to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-set-editor.spec.ts'`
Expected: PASS.

- [ ] **Step 6: Run the full UI suite**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS, exit 0. (The `CapabilitySet` model gained a required `restricted` field — any list/editor test that constructs a `CapabilitySet` literal must include `restricted`; update those literals if the compiler flags them.)

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/capability-sets/capability-set.ts \
        UI/Angular/src/app/core/capability-sets/capability-set.service.spec.ts \
        UI/Angular/src/app/features/admin/capability-set-editor.ts \
        UI/Angular/src/app/features/admin/capability-set-editor.spec.ts
git commit -m "$(cat <<'EOF'
feat(access): Restricted checkbox in the capability-set editor (RBAC hardening)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

- [ ] Backend: `dotnet test Backend/Accounting101.Ledger.Api.Tests` — all green.
- [ ] Frontend: `cd UI/Angular && npx ng test --watch=false` — all green, exit 0.
- [ ] Confirm only intended files staged across the seven commits — no `environment.ts`, no `.csproj`/`.slnx`.

---

## Self-Review notes (against the RBAC hardening spec)

- **#1 No-self-escalation (all grant paths, 422, deployment-admin exempt)** → H-1: `GrantScope.FirstNotHeldByCallerAsync` wired into `MemberEndpoints.AddMember`/`SetMember`/`AssignSets` and `AdminEndpoints.AddMember`; tests cover raw-cap + set paths + within-scope success + deployment-admin exemption. ✓
- **#2 Restricted flag (403, Admin defaults restricted, editor toggle)** → H-2 backend (field, seed Admin restricted, DTOs, `AssignSets` 403 after the 422 check per the spec's ordering) + H-7 editor checkbox. ✓
- **#3 Narrow admin built-ins** → H-3 seeds User/Fiscal/Posting-Accounts Admin (non-restricted, idempotent). ✓
- **#4 Audit (append-only store + query endpoint; UI deferred)** → H-4 store (append/query only; a reflection test asserts no update/delete/replace/remove method exists) + H-5 wiring every mutation + H-6 `GET /admin/audit` deployment-admin gated. UI viewer correctly out of scope. ✓
- **Handler ordering** (gate → validation → 422 escalation → 403 restricted → last-admin → mutate → audit) → encoded in H-1/H-2/H-5 step placement. ✓
- **Enforcement invariant tests (a)–(e)** → (a) GrantScopeTests 422 per path; (b) RestrictedSetTests 403 vs deployment-admin OK; (c) CapabilitySetStoreTests narrow-admin built-ins present + non-restricted; (d) AdminAuditWiringTests one entry per action with before→after; (e) AdminAuditStoreTests reflection test = no mutation method. ✓
- **Type consistency:** `GrantScope.FirstNotHeldByCallerAsync`, `CapabilitySet.Restricted`, `CapabilitySetResponse(..., Restricted=false)`, `AdminAuditEntry`/`AuditState`/`AdminAuditStore.AppendAsync`/`QueryAsync`/`AdminAuditFilter`, `AdminAuditEntryResponse`, and the FE `restricted`/`toggleRestricted` are used identically across tasks and tests. ✓
- **Deferred (out of scope):** audit-log UI viewer; per-client audit visibility; decomposing the Admin set's contents; audit retention/rotation.
