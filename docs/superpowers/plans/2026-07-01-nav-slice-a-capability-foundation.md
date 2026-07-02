# Slice A — Backend Capability Foundation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a backend capability model (vocabulary + role presets), make membership carry a per-member capability set, flip GL enforcement to capability-derived (behavior-identical for the 5 roles), and expose `GET /clients/{clientId}/me/capabilities`.

**Architecture:** Capabilities are `area.level` strings; the nine `gl.*` map 1:1 to the existing `Permission` enum. Role presets (incl. narrow per-module clerks) expand to capability sets at grant time; the set is the stored authority. `LedgerGateway` checks the capability that corresponds to the required `Permission`. A new endpoint returns the caller's resolved set + granted roles + the deployment-admin flag.

**Tech Stack:** .NET (minimal APIs), MongoDB (control DB), xUnit + `ApiFixture` + EphemeralMongo.

## Global Constraints

- No frontend in this slice. No new server-side enforcement of subledger/admin capabilities (only `gl.*` is enforced, via the flipped `LedgerGateway`) — that is deferred Slice E.
- GL enforcement MUST stay behavior-identical for the five existing roles; `PolicyTests` and `ModulePostingTests` pass unchanged.
- camelCase JSON (Web defaults); strict binding (`UnmappedMemberHandling.Disallow`) is on — request DTOs must match exactly.
- DTOs are `sealed record`s in `Accounting101.Ledger.Contracts`.
- `environment.ts` (devClientId) and IDE csproj/slnx churn stay UNCOMMITTED — stage explicit paths only, never `git add -A`.
- Capability strings and preset contents are fixed by the spec — use them verbatim.
- Run backend tests: `cd C:\Users\jorda\RiderProjects\Accounting101 && dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`.

**Canonical capability sets (referenced by every task):**
- READS (9): `gl.read, ar.read, ap.read, payroll.read, cash.read, bankrec.read, fixedassets.read, audit.read, reports.read`
- MODULE WRITES (6): `ar.write, ap.write, payroll.write, cash.write, bankrec.write, fixedassets.write`
- GL WRITE VERBS (8): `gl.post, gl.revise, gl.approve, gl.void, gl.reverse, gl.close, gl.manageAccounts, gl.reopen`
- ADMIN (5): `admin.users, admin.firm, admin.client, admin.fiscal, admin.postingAccounts`

**GL↔Permission map:** `gl.read↔Read, gl.post↔Post, gl.revise↔Revise, gl.approve↔Approve, gl.void↔Void, gl.reverse↔Reverse, gl.close↔Close, gl.manageAccounts↔ManageAccounts, gl.reopen↔Reopen`.

**Presets:** Auditor = READS. Clerk = READS + MODULE WRITES. Approver = READS + {gl.approve, gl.void, gl.reverse}. Controller = READS + MODULE WRITES + {gl.post, gl.revise, gl.approve, gl.void, gl.reverse, gl.close, gl.manageAccounts}. Admin = Controller + {gl.reopen} + ADMIN. ArClerk = READS + {ar.write}. ApClerk = READS + {ap.write}. PayrollClerk = READS + {payroll.write}. CashClerk = READS + {cash.write, bankrec.write}.

---

### Task 1: Capability vocabulary + preset map + LedgerRole extension

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs`
- Create: `Backend/Accounting101.Ledger.Api/Control/RolePresets.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/Authorization.cs` (extend `LedgerRole`)
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilityModelTests.cs` (new)

**Interfaces:**
- Consumes: `LedgerRole`, `Permission`, `RolePermissions` (existing).
- Produces:
  - `Capabilities` static class: `const string` for every capability; `IReadOnlySet<string> All`; `string CapabilityForPermission(Permission)`; `Permission? PermissionForCapability(string)`.
  - `RolePresets` static class: `IReadOnlySet<string> For(LedgerRole)`; `HashSet<string> CapabilitiesFor(IEnumerable<LedgerRole> roles)`.
  - `LedgerRole` gains `ArClerk, ApClerk, PayrollClerk, CashClerk`.

- [ ] **Step 1: Write the failing test** — `CapabilityModelTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CapabilityModelTests
{
    [Fact]
    public void Gl_capabilities_round_trip_with_permissions()
    {
        foreach (Permission p in Enum.GetValues<Permission>())
        {
            string cap = Capabilities.CapabilityForPermission(p);
            Assert.StartsWith("gl.", cap);
            Assert.Equal(p, Capabilities.PermissionForCapability(cap));
        }
    }

    [Fact]
    public void Non_gl_capability_has_no_permission()
    {
        Assert.Null(Capabilities.PermissionForCapability("ar.write"));
    }

    [Theory]
    [InlineData(LedgerRole.Auditor, "Read")]
    [InlineData(LedgerRole.Clerk, "Read")]
    [InlineData(LedgerRole.Approver, "Read,Approve,Void,Reverse")]
    [InlineData(LedgerRole.Controller, "Read,Post,Revise,Approve,Void,Reverse,Close,ManageAccounts")]
    [InlineData(LedgerRole.Admin, "Read,Post,Revise,Approve,Void,Reverse,Close,ManageAccounts,Reopen")]
    public void Preset_gl_capabilities_match_the_legacy_role_permission_matrix(LedgerRole role, string expectedPermissions)
    {
        // The gl.* capabilities in a preset must map to exactly that role's RolePermissions set —
        // this is the invariant that keeps GL enforcement unchanged when LedgerGateway flips.
        HashSet<Permission> fromPreset = RolePresets.For(role)
            .Select(Capabilities.PermissionForCapability)
            .Where(p => p is not null).Select(p => p!.Value).ToHashSet();
        HashSet<Permission> expected = expectedPermissions.Split(',').Select(Enum.Parse<Permission>).ToHashSet();
        Assert.Equal(expected, fromPreset);
        Assert.True(expected.All(p => RolePermissions.Allows(role, p)));
        Assert.True(Enum.GetValues<Permission>().Where(p => !expected.Contains(p)).All(p => !RolePermissions.Allows(role, p)));
    }

    [Fact]
    public void Narrow_clerks_hold_only_gl_read_among_gl_capabilities()
    {
        foreach (LedgerRole role in new[] { LedgerRole.ArClerk, LedgerRole.ApClerk, LedgerRole.PayrollClerk, LedgerRole.CashClerk })
        {
            IEnumerable<string> gl = RolePresets.For(role).Where(c => c.StartsWith("gl."));
            Assert.Equal([Capabilities.GlRead], gl);
        }
        Assert.Contains(Capabilities.ArWrite, RolePresets.For(LedgerRole.ArClerk));
        Assert.DoesNotContain(Capabilities.ApWrite, RolePresets.For(LedgerRole.ArClerk));
    }

    [Fact]
    public void CapabilitiesFor_unions_presets()
    {
        HashSet<string> union = RolePresets.CapabilitiesFor([LedgerRole.ArClerk, LedgerRole.ApClerk]);
        Assert.Contains(Capabilities.ArWrite, union);
        Assert.Contains(Capabilities.ApWrite, union);
    }

    [Fact]
    public void Every_preset_capability_is_in_the_vocabulary()
    {
        foreach (LedgerRole role in Enum.GetValues<LedgerRole>())
            Assert.True(RolePresets.For(role).IsSubsetOf(Capabilities.All), $"{role} has an unknown capability");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: compile failure (types don't exist yet) / test failure.

- [ ] **Step 3: Extend `LedgerRole`** in `Control/Authorization.cs` — add four values after `Admin,` (keep existing XML docs above the enum):

```csharp
    /// <summary>Everything a controller can do, plus (future) reopen and user administration.</summary>
    Admin,

    /// <summary>Operational clerk scoped to Accounts Receivable (module write on AR only).</summary>
    ArClerk,

    /// <summary>Operational clerk scoped to Accounts Payable (module write on AP only).</summary>
    ApClerk,

    /// <summary>Operational clerk scoped to Payroll (module write on Payroll only).</summary>
    PayrollClerk,

    /// <summary>Operational clerk scoped to Cash &amp; Banking (module write on Cash + Bank Rec).</summary>
    CashClerk,
```

Leave `Permission`, `RolePermissions` unchanged (they remain valid for the five original roles and back the equivalence test).

- [ ] **Step 4: Create `Control/Capabilities.cs`**:

```csharp
namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// The capability vocabulary — the atomic unit of what a member may see/do, in "area.level" form.
/// The nine gl.* capabilities map 1:1 to <see cref="Permission"/> (the GL/chart authority the host
/// enforces); subledger/assurance/admin capabilities are advisory for the UI until server-side
/// enforcement lands (a later slice).
/// </summary>
public static class Capabilities
{
    // GL — 1:1 with Permission.
    public const string GlRead = "gl.read";
    public const string GlPost = "gl.post";
    public const string GlRevise = "gl.revise";
    public const string GlApprove = "gl.approve";
    public const string GlVoid = "gl.void";
    public const string GlReverse = "gl.reverse";
    public const string GlClose = "gl.close";
    public const string GlManageAccounts = "gl.manageAccounts";
    public const string GlReopen = "gl.reopen";

    // Subledgers.
    public const string ArRead = "ar.read";
    public const string ArWrite = "ar.write";
    public const string ApRead = "ap.read";
    public const string ApWrite = "ap.write";
    public const string PayrollRead = "payroll.read";
    public const string PayrollWrite = "payroll.write";
    public const string CashRead = "cash.read";
    public const string CashWrite = "cash.write";
    public const string BankRecRead = "bankrec.read";
    public const string BankRecWrite = "bankrec.write";
    public const string FixedAssetsRead = "fixedassets.read";
    public const string FixedAssetsWrite = "fixedassets.write";

    // Assurance.
    public const string AuditRead = "audit.read";
    public const string ReportsRead = "reports.read";

    // Admin.
    public const string AdminUsers = "admin.users";
    public const string AdminFirm = "admin.firm";
    public const string AdminClient = "admin.client";
    public const string AdminFiscal = "admin.fiscal";
    public const string AdminPostingAccounts = "admin.postingAccounts";

    private static readonly Dictionary<Permission, string> PermissionToCapability = new()
    {
        [Permission.Read] = GlRead,
        [Permission.Post] = GlPost,
        [Permission.Revise] = GlRevise,
        [Permission.Approve] = GlApprove,
        [Permission.Void] = GlVoid,
        [Permission.Reverse] = GlReverse,
        [Permission.Close] = GlClose,
        [Permission.ManageAccounts] = GlManageAccounts,
        [Permission.Reopen] = GlReopen,
    };

    private static readonly Dictionary<string, Permission> CapabilityToPermission =
        PermissionToCapability.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>The gl.* capability that corresponds to a GL permission.</summary>
    public static string CapabilityForPermission(Permission permission) => PermissionToCapability[permission];

    /// <summary>The GL permission a capability corresponds to, or null for non-gl.* capabilities.</summary>
    public static Permission? PermissionForCapability(string capability) =>
        CapabilityToPermission.TryGetValue(capability, out Permission p) ? p : null;

    /// <summary>Every capability in the vocabulary.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        GlRead, GlPost, GlRevise, GlApprove, GlVoid, GlReverse, GlClose, GlManageAccounts, GlReopen,
        ArRead, ArWrite, ApRead, ApWrite, PayrollRead, PayrollWrite, CashRead, CashWrite,
        BankRecRead, BankRecWrite, FixedAssetsRead, FixedAssetsWrite,
        AuditRead, ReportsRead,
        AdminUsers, AdminFirm, AdminClient, AdminFiscal, AdminPostingAccounts,
    };
}
```

- [ ] **Step 5: Create `Control/RolePresets.cs`**:

```csharp
namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Role → default capability bundle. Roles are grant-time PRESETS: at grant time a role expands into
/// the capability set stored on the membership (the authority). The gl.* of each of the five original
/// presets mirrors <see cref="RolePermissions"/> exactly, so flipping GL enforcement to capabilities
/// is behavior-preserving (see CapabilityModelTests).
/// </summary>
public static class RolePresets
{
    private static readonly string[] Reads =
    [
        Capabilities.GlRead, Capabilities.ArRead, Capabilities.ApRead, Capabilities.PayrollRead,
        Capabilities.CashRead, Capabilities.BankRecRead, Capabilities.FixedAssetsRead,
        Capabilities.AuditRead, Capabilities.ReportsRead,
    ];

    private static readonly string[] ModuleWrites =
    [
        Capabilities.ArWrite, Capabilities.ApWrite, Capabilities.PayrollWrite,
        Capabilities.CashWrite, Capabilities.BankRecWrite, Capabilities.FixedAssetsWrite,
    ];

    private static readonly Dictionary<LedgerRole, HashSet<string>> Map = new()
    {
        [LedgerRole.Auditor] = [.. Reads],
        [LedgerRole.Clerk] = [.. Reads, .. ModuleWrites],
        [LedgerRole.Approver] = [.. Reads, Capabilities.GlApprove, Capabilities.GlVoid, Capabilities.GlReverse],
        [LedgerRole.Controller] =
        [
            .. Reads, .. ModuleWrites,
            Capabilities.GlPost, Capabilities.GlRevise, Capabilities.GlApprove, Capabilities.GlVoid,
            Capabilities.GlReverse, Capabilities.GlClose, Capabilities.GlManageAccounts,
        ],
        [LedgerRole.Admin] =
        [
            .. Reads, .. ModuleWrites,
            Capabilities.GlPost, Capabilities.GlRevise, Capabilities.GlApprove, Capabilities.GlVoid,
            Capabilities.GlReverse, Capabilities.GlClose, Capabilities.GlManageAccounts, Capabilities.GlReopen,
            Capabilities.AdminUsers, Capabilities.AdminFirm, Capabilities.AdminClient,
            Capabilities.AdminFiscal, Capabilities.AdminPostingAccounts,
        ],
        [LedgerRole.ArClerk] = [.. Reads, Capabilities.ArWrite],
        [LedgerRole.ApClerk] = [.. Reads, Capabilities.ApWrite],
        [LedgerRole.PayrollClerk] = [.. Reads, Capabilities.PayrollWrite],
        [LedgerRole.CashClerk] = [.. Reads, Capabilities.CashWrite, Capabilities.BankRecWrite],
    };

    /// <summary>The default capability set for a role.</summary>
    public static IReadOnlySet<string> For(LedgerRole role) => Map[role];

    /// <summary>Union of the presets for the given roles (the union of overlapping roles).</summary>
    public static HashSet<string> CapabilitiesFor(IEnumerable<LedgerRole> roles)
    {
        HashSet<string> union = [];
        foreach (LedgerRole role in roles) union.UnionWith(Map[role]);
        return union;
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS. Entire suite still green (this task is purely additive).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/Capabilities.cs Backend/Accounting101.Ledger.Api/Control/RolePresets.cs Backend/Accounting101.Ledger.Api/Control/Authorization.cs Backend/Accounting101.Ledger.Api.Tests/CapabilityModelTests.cs
git commit -m "feat(control): capability vocabulary + role presets + narrow clerk roles"
```

---

### Task 2: Capability-backed membership + enforcement flip + admin contracts

Cohesive task: change `Membership` to carry a capability set, update `ControlStore` (grant expands presets; reads backfill legacy docs), flip `LedgerGateway` to capability-derived enforcement, and update the admin contract/endpoints/tests. Grouped so the build ends green (removing `Membership.Role` breaks `LedgerGateway`/`AdminEndpoints`, which are fixed here in the same task).

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/Membership.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs:23-24`
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` (`MembershipResponse`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` (`AddMember`, `ListMembers`)
- Modify: `Backend/Accounting101.Ledger.Api.Tests/AdminTests.cs:56`
- Test: add `MembershipStoreTests.cs` (new)

**Interfaces:**
- Consumes: `RolePresets`, `Capabilities`, `LedgerRole` (Task 1).
- Produces:
  - `Membership { Guid Id, UserId, ClientId; IReadOnlyList<LedgerRole> GrantedRoles; IReadOnlyList<string> Capabilities; LedgerRole? LegacyRole; }`
  - `ControlStore.AddMembershipAsync(userId, clientId, LedgerRole role = Controller, ct)` (unchanged signature; body now stores presets), `AddMembershipRolesAsync(userId, clientId, IReadOnlyList<LedgerRole> roles, ct)`, `GetMembershipAsync`/`GetMembersAsync` return hydrated memberships.
  - `MembershipResponse(Guid UserId, Guid ClientId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities)`.

- [ ] **Step 1: Write the failing test** — `MembershipStoreTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MembershipStoreTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Granting_a_role_stores_its_preset_capabilities_and_granted_role()
    {
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        ControlStore control = fixture.Control();
        await control.AddMembershipAsync(user, client, LedgerRole.Controller);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.Equal([LedgerRole.Controller], m.GrantedRoles);
        Assert.True(RolePresets.For(LedgerRole.Controller).SetEquals(m.Capabilities));
    }

    [Fact]
    public async Task Granting_multiple_roles_unions_their_capabilities()
    {
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        ControlStore control = fixture.Control();
        await control.AddMembershipRolesAsync(user, client, [LedgerRole.ArClerk, LedgerRole.ApClerk]);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.Contains(Capabilities.ArWrite, m.Capabilities);
        Assert.Contains(Capabilities.ApWrite, m.Capabilities);
    }

    [Fact]
    public async Task A_legacy_role_only_document_is_backfilled_on_read()
    {
        // Simulate a pre-migration doc that has only the old "Role" field.
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        IMongoCollection<Membership> raw = fixture.Mongo.GetDatabase(fixture.ControlDatabase).GetCollection<Membership>("memberships");
        await raw.InsertOneAsync(new Membership { Id = Guid.NewGuid(), UserId = user, ClientId = client, LegacyRole = LedgerRole.Approver });

        Membership m = (await fixture.Control().GetMembershipAsync(user, client))!;
        Assert.Equal([LedgerRole.Approver], m.GrantedRoles);
        Assert.True(RolePresets.For(LedgerRole.Approver).SetEquals(m.Capabilities));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: compile failure (new members/methods absent).

- [ ] **Step 3: Rewrite `Control/Membership.cs`**:

```csharp
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Grants a user authority on a client's books. The authoritative grant is <see cref="Capabilities"/>
/// (a per-member set); <see cref="GrantedRoles"/> records which role preset(s) were granted, for display.
/// Authentication (who the user is) is upstream; this is authorization.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class Membership
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>The role preset(s) granted (provenance/display). May be empty for a custom capability grant.</summary>
    [BsonRepresentation(BsonType.String)]
    public IReadOnlyList<LedgerRole> GrantedRoles { get; set; } = [];

    /// <summary>The authoritative resolved capability set (see <see cref="Capabilities"/>).</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>Pre-migration single-role docs stored their role in "Role"; read-time backfill uses this.</summary>
    [BsonElement("Role")]
    [BsonRepresentation(BsonType.String)]
    [BsonIgnoreIfNull]
    public LedgerRole? LegacyRole { get; set; }
}
```

- [ ] **Step 4: Update `Control/ControlStore.cs`** — replace `AddMembershipAsync` (`:50-59`) and `GetMembershipAsync` (`:35-37`) and `GetMembersAsync` (`:65-67`) with:

```csharp
    /// <summary>The user's membership on the client (capabilities hydrated), or null if not a member.</summary>
    public async Task<Membership?> GetMembershipAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default)
    {
        Membership? m = await _memberships.Find(m => m.UserId == userId && m.ClientId == clientId).FirstOrDefaultAsync(cancellationToken);
        return m is null ? null : Hydrate(m);
    }

    /// <summary>Grant a user a role on a client's books (idempotent — an existing membership is left as is).</summary>
    public Task AddMembershipAsync(Guid userId, Guid clientId, LedgerRole role = LedgerRole.Controller, CancellationToken cancellationToken = default) =>
        AddMembershipRolesAsync(userId, clientId, [role], cancellationToken);

    /// <summary>Grant a user one or more role presets; the stored capability set is their union.</summary>
    public async Task AddMembershipRolesAsync(Guid userId, Guid clientId, IReadOnlyList<LedgerRole> roles, CancellationToken cancellationToken = default)
    {
        if (await IsMemberAsync(userId, clientId, cancellationToken))
            return;

        await _memberships.InsertOneAsync(
            new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ClientId = clientId,
                GrantedRoles = roles,
                Capabilities = [.. RolePresets.CapabilitiesFor(roles)],
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>All memberships granted on a client (capabilities hydrated).</summary>
    public async Task<IReadOnlyList<Membership>> GetMembersAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        List<Membership> members = await _memberships.Find(m => m.ClientId == clientId).ToListAsync(cancellationToken);
        return members.Select(Hydrate).ToList();
    }

    /// <summary>Backfill a pre-migration (Role-only) doc to the capability shape at read time (no write).</summary>
    private static Membership Hydrate(Membership m)
    {
        if (m.Capabilities.Count == 0 && m.LegacyRole is { } role)
        {
            m.GrantedRoles = [role];
            m.Capabilities = [.. RolePresets.For(role)];
        }
        return m;
    }
```

(Leave `IsMemberAsync`, client/module methods unchanged.)

- [ ] **Step 5: Flip `Endpoints/LedgerGateway.cs`** — replace the check at `:23-25`:

```csharp
        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null || !membership.Capabilities.Contains(Capabilities.CapabilityForPermission(required)))
            return LedgerContext.Forbidden();
```

(Add `using` if needed; `Capabilities` is in the same `Accounting101.Ledger.Api.Control` namespace already imported.)

- [ ] **Step 6: Update contracts + admin endpoints.** In `AdminContracts.cs`, replace `MembershipResponse` (`:23`):

```csharp
public sealed record MembershipResponse(
    Guid UserId, Guid ClientId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities);
```

In `AdminEndpoints.cs`, update `AddMember` (`:90`) and `ListMembers` (`:96`) response construction:

```csharp
        await control.AddMembershipAsync(request.UserId, clientId, role, cancellationToken);
        return Results.Ok(new MembershipResponse(
            request.UserId, clientId, [role.ToString()], [.. RolePresets.CapabilitiesFor([role])]));
```
```csharp
        return Results.Ok(members.Select(m => new MembershipResponse(
            m.UserId, m.ClientId, m.GrantedRoles.Select(r => r.ToString()).ToList(), m.Capabilities)).ToList());
```

- [ ] **Step 7: Update `AdminTests.cs:56`** — the members assertion now reads `Roles`:

```csharp
        Assert.Contains(members, m => m.UserId == user && m.Roles.Contains("Auditor"));
```

- [ ] **Step 8: Run the full backend suite**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS — `MembershipStoreTests` green; `PolicyTests`, `ModulePostingTests`, `AdminTests`, and all others green (GL enforcement behavior unchanged by the equivalence invariant). If any other file referenced `membership.Role`, grep `\.Role\b` under `Backend/Accounting101.Ledger.Api` and update it to capabilities/GrantedRoles; report any found.

- [ ] **Step 9: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/Membership.cs Backend/Accounting101.Ledger.Api/Control/ControlStore.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs Backend/Accounting101.Ledger.Contracts/AdminContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/AdminTests.cs Backend/Accounting101.Ledger.Api.Tests/MembershipStoreTests.cs
git commit -m "feat(control): capability-backed membership + capability-derived GL enforcement"
```

---

### Task 3: `GET /me/capabilities` endpoint + tests

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs`
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` (add `CapabilitiesResponse`) — or a new `CapabilitiesContracts.cs`; put it in `AdminContracts.cs` for brevity.
- Modify: `Accounting101.Host/Program.cs` (map the new endpoints)
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs` (new)

**Interfaces:**
- Consumes: `IActorFactory`, `ControlStore`, `Membership.Capabilities`/`GrantedRoles` (Task 2).
- Produces: `CapabilitiesResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin)`; route `GET /clients/{clientId:guid}/me/capabilities`.

- [ ] **Step 1: Write the failing test** — `CapabilitiesTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CapabilitiesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task A_controller_gets_their_resolved_capabilities_and_role()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Controller);
        CapabilitiesResponse body = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Contains("Controller", body.Roles);
        Assert.Contains("gl.post", body.Capabilities);
        Assert.Contains("ar.write", body.Capabilities);
        Assert.False(body.DeploymentAdmin);
    }

    [Fact]
    public async Task An_auditor_gets_reads_only()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Auditor);
        CapabilitiesResponse body = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Contains("gl.read", body.Capabilities);
        Assert.DoesNotContain("gl.post", body.Capabilities);
        Assert.DoesNotContain("ar.write", body.Capabilities);
    }

    [Fact]
    public async Task A_narrow_ar_clerk_can_write_ar_but_not_ap()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.ArClerk);
        CapabilitiesResponse body = (await c.Http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;

        Assert.Contains("ar.write", body.Capabilities);
        Assert.DoesNotContain("ap.write", body.Capabilities);
        Assert.Contains("ar.read", body.Capabilities);
        Assert.Contains("ap.read", body.Capabilities);
    }

    [Fact]
    public async Task Overlapping_roles_return_the_union()
    {
        Guid client = (await fixture.SeedClientAsync(role: LedgerRole.Auditor)).ClientId;
        Guid user = Guid.NewGuid();
        await fixture.Control().AddMembershipRolesAsync(user, client, [LedgerRole.ArClerk, LedgerRole.ApClerk]);
        HttpClient http = fixture.ClientFor(user, "Dual Clerk");

        CapabilitiesResponse body = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{client}/me/capabilities"))!;
        Assert.Contains("ar.write", body.Capabilities);
        Assert.Contains("ap.write", body.Capabilities);
        Assert.Contains("ArClerk", body.Roles);
        Assert.Contains("ApClerk", body.Roles);
    }

    [Fact]
    public async Task Deployment_admin_flag_reflects_the_claim()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Controller);
        // Re-issue the same member's token WITH the deployment-admin claim.
        HttpClient http = fixture.ClientFor(c.UserId, "Admin Member", ("admin", "true"));
        CapabilitiesResponse body = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{c.ClientId}/me/capabilities"))!;
        Assert.True(body.DeploymentAdmin);
    }

    [Fact]
    public async Task A_non_member_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync();
        HttpClient stranger = fixture.ClientFor(Guid.NewGuid(), "Stranger");
        HttpResponseMessage res = await stranger.GetAsync($"/clients/{c.ClientId}/me/capabilities");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: compile/failure (`CapabilitiesResponse`, route absent).

- [ ] **Step 3: Add `CapabilitiesResponse`** to `AdminContracts.cs`:

```csharp
/// <summary>The acting user's resolved capabilities on a client, the role preset(s) granted, and
/// whether they hold the deployment-admin claim (a separate authorization axis).</summary>
public sealed record CapabilitiesResponse(
    IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin);
```

- [ ] **Step 4: Create `Endpoints/CapabilitiesEndpoints.cs`**:

```csharp
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Self-service capability lookup: any authenticated member may read their own resolved capabilities
/// on a client. Resolved server-side from the control DB (never the token's role claim), so it is the
/// single source of truth the frontend uses to drive role-based navigation and screen write-gating.
/// </summary>
public static class CapabilitiesEndpoints
{
    public static void MapCapabilitiesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/clients/{clientId:guid}")
           .RequireAuthorization()
           .MapGet("/me/capabilities", GetMyCapabilities);
    }

    private static async Task<IResult> GetMyCapabilities(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);
        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null)
            return Results.Forbid();

        bool deploymentAdmin = user.HasClaim("admin", "true");
        return Results.Ok(new CapabilitiesResponse(
            membership.Capabilities,
            membership.GrantedRoles.Select(r => r.ToString()).ToList(),
            deploymentAdmin));
    }
}
```

(`Actor` is in `Accounting101.Ledger.Mongo`; add `using Accounting101.Ledger.Mongo;` if the compiler needs it.)

- [ ] **Step 5: Map it** in `Accounting101.Host/Program.cs` after `app.MapAdminEndpoints();` (line ~73):

```csharp
app.MapCapabilitiesEndpoints();
```

(Add `using Accounting101.Ledger.Api.Endpoints;` if not already present — it is, since other `Map*Endpoints` are called.)

- [ ] **Step 6: Run the full suite**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS — `CapabilitiesTests` green; whole suite green.

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs Backend/Accounting101.Ledger.Contracts/AdminContracts.cs Accounting101.Host/Program.cs Backend/Accounting101.Ledger.Api.Tests/CapabilitiesTests.cs
git commit -m "feat(api): GET /clients/{id}/me/capabilities returns resolved capability set"
```

## Self-Review

- **Spec coverage:** Task 1 = vocabulary + presets + narrow clerks + GL↔Permission map + equivalence invariant test; Task 2 = capability-backed membership + store + enforcement flip + admin contract/tests + legacy backfill; Task 3 = endpoint + response contract + integration tests (roles differ, union, deployment-admin, non-member 403). All spec sections covered. No frontend, no Slice E enforcement — matches scope.
- **Type consistency:** `Capabilities`/`RolePresets`/`Membership.Capabilities`/`GrantedRoles`/`CapabilitiesResponse`/`AddMembershipRolesAsync` names identical across tasks. `MembershipResponse` new shape used in both AdminEndpoints and AdminTests.
- **Green-build ordering:** Task 1 additive. Task 2 groups the `Membership.Role` removal with every consumer (LedgerGateway, AdminEndpoints, AdminTests) so the build ends green. Task 3 additive.
- **Placeholder scan:** none — all new files and edits are shown in full.
- **Migration note:** read-time `Hydrate` backfills legacy docs; the durable `.localdev` docs get an explicit migration as a controller finish step (not a task).

## Execution Handoff

Subagent-driven. Three tasks, sequential (2 depends on 1; 3 depends on 1+2). After Task 3: controller migrates the two `.localdev` membership docs and smoke-tests `GET /clients/{clientId}/me/capabilities` for both dev identities, then the finish/merge decision.
