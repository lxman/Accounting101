# Slice D — Admin Users & Roles + Visibility Config — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A per-client Admin manages members and tunes each member's capability set (presets + per-capability toggles) via a Users & Roles screen, backed by `admin.users`-enforced per-client member endpoints with a last-admin guard.

**Architecture:** `ControlStore` gains set/remove. New `MemberEndpoints` (`/clients/{id}/members`, gated on `admin.users` or deployment-admin, last-admin guard) + a `/capabilities/catalog` metadata endpoint (backend truth for the editor). Frontend `MemberService` + a member-list and member-editor under `/admin/users`.

**Tech Stack:** .NET (minimal APIs) + MongoDB; Angular 22 (standalone, OnPush, signals) + vitest.

## Global Constraints

- Enforce `admin.users` (OR the `admin=true` deployment claim) on every new member endpoint.
- Last-admin guard: no operation may leave a client with zero members holding `admin.users`.
- No changes to ledger-endpoint enforcement (Slice E). No editing of role PRESET definitions.
- camelCase JSON; strict binding (request DTOs match exactly). DTOs are `sealed record`s in `Accounting101.Ledger.Contracts`.
- Angular: standalone, OnPush, signals, `takeUntilDestroyed`, `extractProblem(e).detail` on errors; Spartan helm; vitest via `cd UI/Angular && npx ng test --watch=false`.
- `environment.ts` + IDE csproj/slnx churn stay UNCOMMITTED — stage explicit paths only.
- Backend tests: `cd C:\Users\jorda\RiderProjects\Accounting101 && dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`.
- Capability vocabulary + role names are backend truth (`Capabilities`, `RolePresets`); the frontend editor consumes them via `/capabilities/catalog`.

---

### Task 1: ControlStore set/remove + contracts

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ControlStore.cs`
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/MembershipStoreTests.cs` (extend)

**Interfaces:**
- Produces: `ControlStore.SetMembershipAsync(userId, clientId, IReadOnlyList<LedgerRole> roles, IReadOnlyList<string> capabilities, ct)`; `ControlStore.RemoveMembershipAsync(userId, clientId, ct)`; contracts `AddClientMemberRequest`, `SetMemberRequest`, `CapabilityCatalogResponse`, `RolePresetDto`.

- [ ] **Step 1: Write failing store tests** — append to `MembershipStoreTests.cs`:

```csharp
    [Fact]
    public async Task SetMembership_creates_then_replaces_roles_and_capabilities()
    {
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        ControlStore control = fixture.Control();

        await control.SetMembershipAsync(user, client, [LedgerRole.Auditor], ["gl.read", "ar.read"]);
        Membership created = (await control.GetMembershipAsync(user, client))!;
        Assert.Equal([LedgerRole.Auditor], created.GrantedRoles);
        Assert.True(new HashSet<string> { "gl.read", "ar.read" }.SetEquals(created.Capabilities));

        await control.SetMembershipAsync(user, client, [LedgerRole.Controller], ["gl.read", "gl.post"]);
        Membership replaced = (await control.GetMembershipAsync(user, client))!;
        Assert.Equal([LedgerRole.Controller], replaced.GrantedRoles);
        Assert.True(new HashSet<string> { "gl.read", "gl.post" }.SetEquals(replaced.Capabilities));
    }

    [Fact]
    public async Task RemoveMembership_deletes_the_member()
    {
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        ControlStore control = fixture.Control();
        await control.SetMembershipAsync(user, client, [LedgerRole.Auditor], ["gl.read"]);
        await control.RemoveMembershipAsync(user, client);
        Assert.Null(await control.GetMembershipAsync(user, client));
    }
```

- [ ] **Step 2: Run — verify fail.** `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`

- [ ] **Step 3: Add store methods** — in `ControlStore.cs`, after `AddMembershipRolesAsync`:

```csharp
    /// <summary>Create or replace a member's granted roles + capability set (the authoritative grant).</summary>
    public Task SetMembershipAsync(Guid userId, Guid clientId, IReadOnlyList<LedgerRole> roles, IReadOnlyList<string> capabilities, CancellationToken cancellationToken = default) =>
        _memberships.ReplaceOneAsync(
            m => m.UserId == userId && m.ClientId == clientId,
            new Membership { Id = Guid.NewGuid(), UserId = userId, ClientId = clientId, GrantedRoles = roles, Capabilities = capabilities },
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);

    /// <summary>Remove a member from a client's books.</summary>
    public Task RemoveMembershipAsync(Guid userId, Guid clientId, CancellationToken cancellationToken = default) =>
        _memberships.DeleteOneAsync(m => m.UserId == userId && m.ClientId == clientId, cancellationToken);
```

- [ ] **Step 4: Add contracts** — in `AdminContracts.cs`:

```csharp
/// <summary>Add a member to a client with an explicit role preset list and capability set.</summary>
public sealed record AddClientMemberRequest(Guid UserId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities);

/// <summary>Replace an existing member's role presets and capability set.</summary>
public sealed record SetMemberRequest(IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities);

/// <summary>The full capability vocabulary and the role presets (backend truth for the admin editor).</summary>
public sealed record CapabilityCatalogResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<RolePresetDto> Roles);
public sealed record RolePresetDto(string Role, IReadOnlyList<string> Capabilities);
```

- [ ] **Step 5: Run — verify pass.** Full backend suite green.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ControlStore.cs Backend/Accounting101.Ledger.Contracts/AdminContracts.cs Backend/Accounting101.Ledger.Api.Tests/MembershipStoreTests.cs
git commit -m "feat(control): membership set/remove + member-management contracts"
```

---

### Task 2: Member endpoints + capability catalog

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/CapabilityCatalogEndpoints.cs`
- Modify: `Accounting101.Host/Program.cs` (map both)
- Test: `Backend/Accounting101.Ledger.Api.Tests/MemberManagementTests.cs` (new)

**Interfaces:**
- Consumes: `ControlStore` (Task 1), `Capabilities`, `RolePresets`, `IActorFactory`.
- Produces: routes `GET/POST /clients/{id}/members`, `PUT/DELETE /clients/{id}/members/{userId}`, `GET /capabilities/catalog`.

- [ ] **Step 1: Write failing integration tests** — `MemberManagementTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MemberManagementTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // Seed a client whose primary member is an Admin (holds admin.users).
    private async Task<SeededClient> SeedWithAdminAsync()
        => await fixture.SeedClientAsync(role: LedgerRole.Admin);

    [Fact]
    public async Task Admin_lists_adds_edits_and_removes_members()
    {
        SeededClient c = await SeedWithAdminAsync();

        // list (self only, initially)
        MembershipResponse[] initial = (await c.Http.GetFromJsonAsync<MembershipResponse[]>($"/clients/{c.ClientId}/members"))!;
        Assert.Single(initial);

        // add
        Guid newUser = Guid.NewGuid();
        HttpResponseMessage add = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(newUser, ["ArClerk"], ["gl.read", "ar.read", "ar.write"]));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        // edit (widen visibility: add ap.read)
        HttpResponseMessage edit = await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{newUser}",
            new SetMemberRequest(["ArClerk"], ["gl.read", "ar.read", "ar.write", "ap.read"]));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        MembershipResponse edited = (await edit.Content.ReadFromJsonAsync<MembershipResponse>())!;
        Assert.Contains("ap.read", edited.Capabilities);

        // remove
        HttpResponseMessage del = await c.Http.DeleteAsync($"/clients/{c.ClientId}/members/{newUser}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    [Fact]
    public async Task A_non_admin_member_is_forbidden()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Clerk);   // no admin.users
        HttpResponseMessage list = await c.Http.GetAsync($"/clients/{c.ClientId}/members");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    }

    [Fact]
    public async Task Adding_an_existing_member_is_a_conflict()
    {
        SeededClient c = await SeedWithAdminAsync();
        HttpResponseMessage dup = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(c.UserId, ["Auditor"], ["gl.read"]));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Unknown_role_or_capability_is_rejected()
    {
        SeededClient c = await SeedWithAdminAsync();
        HttpResponseMessage badRole = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["Wizard"], ["gl.read"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badRole.StatusCode);
        HttpResponseMessage badCap = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(Guid.NewGuid(), ["Auditor"], ["gl.fly"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCap.StatusCode);
    }

    [Fact]
    public async Task Cannot_remove_the_last_admin()
    {
        SeededClient c = await SeedWithAdminAsync();   // sole admin is c.UserId
        // DELETE self (last admin) → 409
        HttpResponseMessage del = await c.Http.DeleteAsync($"/clients/{c.ClientId}/members/{c.UserId}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        // PUT self removing admin.users → 409
        HttpResponseMessage strip = await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{c.UserId}",
            new SetMemberRequest(["Auditor"], ["gl.read"]));
        Assert.Equal(HttpStatusCode.Conflict, strip.StatusCode);
    }

    [Fact]
    public async Task Catalog_returns_the_vocabulary_and_presets()
    {
        SeededClient c = await SeedWithAdminAsync();
        CapabilityCatalogResponse cat = (await c.Http.GetFromJsonAsync<CapabilityCatalogResponse>("/capabilities/catalog"))!;
        Assert.Contains("ar.write", cat.Capabilities);
        Assert.Contains(cat.Roles, r => r.Role == "Controller" && r.Capabilities.Contains("gl.post"));
    }
}
```

- [ ] **Step 2: Run — verify fail.**

- [ ] **Step 3: Create `MemberEndpoints.cs`**:

```csharp
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Per-client member management for a client Admin (holds <c>admin.users</c>) or a deployment admin.
/// Distinct from the deployment-only <see cref="AdminEndpoints"/> provisioning surface. Enforces a
/// last-admin guard so a client can never be left without an administrator.
/// </summary>
public static class MemberEndpoints
{
    public static void MapMemberEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/clients/{clientId:guid}/members").RequireAuthorization();
        g.MapGet("", ListMembers);
        g.MapPost("", AddMember);
        g.MapPut("/{userId:guid}", SetMember);
        g.MapDelete("/{userId:guid}", RemoveMember);
    }

    // Allow deployment admins (admin=true claim) or a member holding admin.users.
    private static async Task<bool> CallerMayManage(ClaimsPrincipal user, Guid clientId, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (user.HasClaim("admin", "true")) return true;
        Actor actor = actorFactory.Create(user);
        Membership? m = await control.GetMembershipAsync(actor.UserId, clientId, ct);
        return m is not null && m.Capabilities.Contains(Capabilities.AdminUsers);
    }

    private static MembershipResponse ToResponse(Membership m) =>
        new(m.UserId, m.ClientId, m.GrantedRoles.Select(r => r.ToString()).ToList(), m.Capabilities);

    private static bool TryParse(IReadOnlyList<string> roleNames, IReadOnlyList<string> capabilities, out List<LedgerRole> roles, out IResult? error)
    {
        roles = [];
        foreach (string name in roleNames)
        {
            if (!Enum.TryParse(name, ignoreCase: true, out LedgerRole role))
            { error = Results.Problem($"Unknown role '{name}'.", statusCode: StatusCodes.Status422UnprocessableEntity); return false; }
            roles.Add(role);
        }
        foreach (string cap in capabilities)
            if (!Capabilities.All.Contains(cap))
            { error = Results.Problem($"Unknown capability '{cap}'.", statusCode: StatusCodes.Status422UnprocessableEntity); return false; }
        error = null;
        return true;
    }

    // A change is allowed unless it would leave the client with zero admin.users holders.
    private static async Task<bool> WouldLeaveNoAdmin(ControlStore control, Guid clientId, Guid changedUser, bool changedUserKeepsAdmin, CancellationToken ct)
    {
        IReadOnlyList<Membership> members = await control.GetMembersAsync(clientId, ct);
        bool anotherAdminRemains = members.Any(m => m.UserId != changedUser && m.Capabilities.Contains(Capabilities.AdminUsers));
        return !anotherAdminRemains && !changedUserKeepsAdmin;
    }

    private static async Task<IResult> ListMembers(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        IReadOnlyList<Membership> members = await control.GetMembersAsync(clientId, ct);
        return Results.Ok(members.Select(ToResponse).ToList());
    }

    private static async Task<IResult> AddMember(
        Guid clientId, AddClientMemberRequest request, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!TryParse(request.Roles, request.Capabilities, out List<LedgerRole> roles, out IResult? error)) return error!;
        if (await control.IsMemberAsync(request.UserId, clientId, ct))
            return Results.Problem("Already a member.", statusCode: StatusCodes.Status409Conflict);
        await control.SetMembershipAsync(request.UserId, clientId, roles, request.Capabilities, ct);
        return Results.Ok(new MembershipResponse(request.UserId, clientId, request.Roles, request.Capabilities));
    }

    private static async Task<IResult> SetMember(
        Guid clientId, Guid userId, SetMemberRequest request, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!TryParse(request.Roles, request.Capabilities, out List<LedgerRole> roles, out IResult? error)) return error!;
        if (!await control.IsMemberAsync(userId, clientId, ct)) return Results.NotFound();
        bool keepsAdmin = request.Capabilities.Contains(Capabilities.AdminUsers);
        if (await WouldLeaveNoAdmin(control, clientId, userId, keepsAdmin, ct))
            return Results.Problem("Cannot remove the last administrator.", statusCode: StatusCodes.Status409Conflict);
        await control.SetMembershipAsync(userId, clientId, roles, request.Capabilities, ct);
        return Results.Ok(new MembershipResponse(userId, clientId, request.Roles, request.Capabilities));
    }

    private static async Task<IResult> RemoveMember(
        Guid clientId, Guid userId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!await control.IsMemberAsync(userId, clientId, ct)) return Results.NotFound();
        if (await WouldLeaveNoAdmin(control, clientId, userId, changedUserKeepsAdmin: false, ct))
            return Results.Problem("Cannot remove the last administrator.", statusCode: StatusCodes.Status409Conflict);
        await control.RemoveMembershipAsync(userId, clientId, ct);
        return Results.NoContent();
    }
}
```

- [ ] **Step 4: Create `CapabilityCatalogEndpoints.cs`**:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>Static capability vocabulary + role presets — backend truth for the admin member editor.</summary>
public static class CapabilityCatalogEndpoints
{
    public static void MapCapabilityCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/capabilities/catalog", GetCatalog).RequireAuthorization();
    }

    private static IResult GetCatalog()
    {
        List<RolePresetDto> roles = Enum.GetValues<LedgerRole>()
            .Select(r => new RolePresetDto(r.ToString(), RolePresets.For(r).ToList()))
            .ToList();
        return Results.Ok(new CapabilityCatalogResponse(Capabilities.All.OrderBy(c => c).ToList(), roles));
    }
}
```

- [ ] **Step 5: Map in `Program.cs`** — after `app.MapCapabilitiesEndpoints();`:

```csharp
app.MapMemberEndpoints();
app.MapCapabilityCatalogEndpoints();
```

- [ ] **Step 6: Run — verify pass.** Full backend suite green.

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs Backend/Accounting101.Ledger.Api/Endpoints/CapabilityCatalogEndpoints.cs Accounting101.Host/Program.cs Backend/Accounting101.Ledger.Api.Tests/MemberManagementTests.cs
git commit -m "feat(api): per-client member CRUD (admin.users gate + last-admin guard) + capability catalog"
```

---

### Task 3: Frontend member service

**Files:**
- Create: `UI/Angular/src/app/core/members/member.ts` (types)
- Create: `UI/Angular/src/app/core/members/member.service.ts`
- Test: `UI/Angular/src/app/core/members/member.service.spec.ts`

**Interfaces:**
- Produces: `Member`, `AddMemberRequest`, `SetMemberRequest`, `CapabilityCatalog`; `MemberService` with `list()`, `add(req)`, `set(userId, req)`, `remove(userId)`, `catalog()`.

- [ ] **Step 1: Write failing spec** — `member.service.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MemberService } from './member.service';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';

describe('MemberService', () => {
  let http: HttpTestingController; let svc: MemberService; let client: ClientContextService;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(ClientContextService);
    svc = TestBed.inject(MemberService);
    client.select('c1');
  });
  afterEach(() => http.verify());

  it('lists members', () => {
    svc.list().subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('adds a member', () => {
    svc.add({ userId: 'u2', roles: ['Auditor'], capabilities: ['gl.read'] }).subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members`);
    expect(req.request.method).toBe('POST');
    req.flush({ userId: 'u2', roles: ['Auditor'], capabilities: ['gl.read'] });
  });

  it('sets a member', () => {
    svc.set('u2', { roles: ['Controller'], capabilities: ['gl.read', 'gl.post'] }).subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members/u2`);
    expect(req.request.method).toBe('PUT');
    req.flush({ userId: 'u2', roles: ['Controller'], capabilities: ['gl.read', 'gl.post'] });
  });

  it('removes a member', () => {
    svc.remove('u2').subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members/u2`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });

  it('fetches the catalog', () => {
    svc.catalog().subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capabilities/catalog`);
    expect(req.request.method).toBe('GET');
    req.flush({ capabilities: [], roles: [] });
  });
});
```

- [ ] **Step 2: Run — verify fail.**

- [ ] **Step 3: Create `member.ts`**:

```ts
export interface Member { userId: string; roles: string[]; capabilities: string[]; }
export interface AddMemberRequest { userId: string; roles: string[]; capabilities: string[]; }
export interface SetMemberRequest { roles: string[]; capabilities: string[]; }
export interface RolePreset { role: string; capabilities: string[]; }
export interface CapabilityCatalog { capabilities: string[]; roles: RolePreset[]; }
```

- [ ] **Step 4: Create `member.service.ts`**:

```ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { Member, AddMemberRequest, SetMemberRequest, CapabilityCatalog } from './member';

@Injectable({ providedIn: 'root' })
export class MemberService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}/members${path}`; }

  list(): Observable<Member[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<Member[]>(this.base());
  }
  add(req: AddMemberRequest): Observable<Member> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<Member>(this.base(), req);
  }
  set(userId: string, req: SetMemberRequest): Observable<Member> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<Member>(this.base(`/${userId}`), req);
  }
  remove(userId: string): Observable<void> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.delete<void>(this.base(`/${userId}`));
  }
  catalog(): Observable<CapabilityCatalog> {
    return this.http.get<CapabilityCatalog>(`${environment.apiBaseUrl}/capabilities/catalog`);
  }
}
```

- [ ] **Step 5: Run — verify pass.**

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/members/member.ts UI/Angular/src/app/core/members/member.service.ts UI/Angular/src/app/core/members/member.service.spec.ts
git commit -m "feat(nav): MemberService (per-client member CRUD + capability catalog)"
```

---

### Task 4: Users & Roles screens + routes

**Files:**
- Create: `UI/Angular/src/app/features/admin/member-list.ts`
- Create: `UI/Angular/src/app/features/admin/member-editor.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`
- Create: `UI/Angular/src/app/core/api/dev-identity-names.ts` (sub → display-name map for the member list)
- Test: `member-list.spec.ts`, `member-editor.spec.ts`

**Interfaces:**
- Consumes: `MemberService` (Task 3), `CanDirective`/`canWrite` (Slice C), `extractProblem`.

- [ ] **Step 1: Add the dev-name map** — `core/api/dev-identity-names.ts`:

```ts
import { DEV_IDENTITIES } from './dev-identities';

/** Best-effort display name for a member userId (known dev identities → their name; else the id). */
export function memberDisplayName(userId: string): string {
  return DEV_IDENTITIES.find((i) => i.sub === userId)?.name ?? userId;
}
```

- [ ] **Step 2: Write the member-list** — `features/admin/member-list.ts` (mirror the app's list conventions: OnPush, `takeUntilDestroyed`, whole-row click to editor, `extractProblem` on error). Table columns: Member (name), Roles (joined), Capabilities (count). Header "Add member" link → `/admin/users/new`. Reactive load off `ClientContextService` via `toSignal(toObservable(clientId)→switchMap(list))` OR a simple `reload()` in the constructor mirroring existing lists. Whole row → `router.navigate(['/admin/users', m.userId])`.

- [ ] **Step 3: Write the member-editor** — `features/admin/member-editor.ts`:
  - Reads `:userId` from the route (absent → new-member mode). New mode shows a userId text input.
  - Loads `catalog()`; holds a working `Set<string>` of selected capabilities (signal) and a `Set<string>` of checked role presets.
  - Role presets: a checkbox per `catalog.roles`; checking a preset does `capabilities ∪= preset.capabilities` (union in); unchecking leaves capabilities as-is (admin tunes individually).
  - Capability grid: group `catalog.capabilities` by area (prefix before `.`); render a labeled section per area with a checkbox per capability bound to the working set.
  - Existing mode: preload the member's current roles+capabilities (from `list()` find by id, or a `get`—reuse `list()` then filter).
  - Save: new → `add({userId, roles, capabilities})`; existing → `set(userId, {roles, capabilities})`; on success navigate to `/admin/users`; on error show `extractProblem(e).detail` (surfaces 409 last-admin).
  - Remove (existing only): `remove(userId)` with a confirm; on 409 show the detail.

- [ ] **Step 4: Wire routes** — in `app.routes.ts`, add explicit routes BEFORE the placeholder spread and add `/admin/users` to the built-prefix exclusion so no placeholder is generated for it:

```ts
  { path: 'admin/users', component: MemberList },
  { path: 'admin/users/new', component: MemberEditor, canActivate: [canWrite('admin.users', '/admin/users')] },
  { path: 'admin/users/:userId', component: MemberEditor, canActivate: [canWrite('admin.users', '/admin/users')] },
```

Add `'/admin/users'` to the `built` array in the placeholder-derivation IIFE so `navLeafPaths()` doesn't also emit a `Placeholder` for it. Import `MemberList`, `MemberEditor`, `canWrite`.

- [ ] **Step 5: Specs** — `member-list.spec.ts`: provide `provideHttpClientTesting` + `provideCapabilities('admin.users')`; assert rows render from a flushed list and the "Add member" link is present. `member-editor.spec.ts`: flush a catalog; assert checking a preset selects its capabilities (grid reflects it); assert Save issues POST (new) / PUT (existing) with the working set; assert a 409 flush surfaces the detail message. Use the app's existing spec harness patterns (mirror payroll/receivables editor specs). Provide `provideRouter([])`.

- [ ] **Step 6: Run — full suite green.** `cd UI/Angular && npx ng test --watch=false`

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/features/admin UI/Angular/src/app/core/api/dev-identity-names.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(nav): Users & Roles admin screens (member list + capability editor)"
```

## Self-Review

- **Spec coverage:** Task 1 store+contracts; Task 2 endpoints (CRUD + admin.users gate + last-admin guard + catalog); Task 3 service; Task 4 UI (list + presets/toggles editor + routes/guard). All spec sections covered.
- **Type consistency:** `SetMembershipAsync`/`RemoveMembershipAsync`, `AddClientMemberRequest`/`SetMemberRequest`/`CapabilityCatalogResponse`/`RolePresetDto`, FE `Member`/`CapabilityCatalog`/`MemberService` names consistent across tasks.
- **Green-build ordering:** each task ends green; Task 2 depends on Task 1 store+contracts; Task 4 depends on Task 3 service + Slice C `canWrite`/`CanDirective`.
- **Last-admin guard** centralized in `WouldLeaveNoAdmin`, applied to PUT (when it drops admin.users) and DELETE. **admin.users OR deployment-admin** gate on every handler.
- **Placeholder scan:** none — backend + FE service fully coded; Task 4 component bodies described against existing conventions (mirror sibling list/editor components) with exact routes/contracts.

## Execution Handoff

Subagent-driven. Four tasks (1→2 backend, 3→4 frontend; 2 needs 1, 4 needs 3). After Task 4: controller smoke test — as Dev Admin edit Dev AR Clerk to add `ap.read`, confirm Payables appears read-only in the AR Clerk sidebar; confirm last-admin strip is blocked; as Dev Auditor confirm `/admin/users` is nav-hidden and route-redirects.
