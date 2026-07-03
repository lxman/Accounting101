# Access Control AC-3: Frontend Access Control Area — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give owners a visible Access Control UI — manage named capability *sets* (create/edit/delete with a capability picker and a confirm-on-save blast-radius step) and assign sets to members — built on the AC-1/AC-2 backend.

**Architecture:** One small backend task exposes on *reads* the set-reference data AC-2 only wrote (member count on the set list; a member's assigned set ids/names on the member list). Then Angular: a deployment-admin-scoped `CapabilitySetService` + a Sets list screen (`/admin/access/sets`) + a Set editor with a capability picker and an inline confirm-on-save panel; and the existing Slice D member editor (`/admin/users/:userId`) is converted from a raw-capability grid into a **set-picker** that calls AC-2's `PUT .../members/{userId}/sets`. A new "Capability Sets" nav link sits under Administration, gated to deployment admins.

**Tech Stack:** Angular 22 (standalone, zoneless, signals), Tailwind + Spartan-ng (locally-scaffolded helm libs), `ng test` (Vitest-backed unit-test builder, `vi.spyOn`), `HttpTestingController`. Backend task: C# / .NET 10, ASP.NET Core Minimal APIs, xUnit, EphemeralMongo.

## Global Constraints

- **Reuse, don't reinvent.** Follow the established patterns exactly:
  - HTTP services inject `HttpClient` and build URLs from `environment.apiBaseUrl` (`UI/Angular/src/app/core/api/environment.ts`). Client-scoped services also inject `ClientContextService` and interpolate `client.clientId()` (see `core/members/member.service.ts`). **`CapabilitySetService` is deployment-scoped — it must NOT use `clientId` (route is `/capability-sets`, no `{clientId}`).**
  - Component/service specs use `provideHttpClient()` + `provideHttpClientTesting()` + `HttpTestingController` with `afterEach(http.verify())`; capability gating in specs uses `provideCapabilities(...caps)` / `StubCapabilityService` from `core/capabilities/capability.testing.ts`; components add `provideZonelessChangeDetection()` and call `fixture.detectChanges()`.
  - Buttons use `hlmBtn` (`@spartan-ng/helm/button`); tables use `HlmTableImports` (`@spartan-ng/helm/table`); inputs/labels use `HlmInputImports`/`HlmLabelImports`. **Capability pickers use raw `<input type="checkbox">` + `(change)` mutating a `Set<string>` signal — mirror `features/admin/member-editor.ts`. Do NOT scaffold new Spartan checkbox/dialog libs** (they are not generated locally; the confirm step is an inline panel, not a modal).
- **Deployment-admin vs per-client admin.** Set CRUD endpoints (`/capability-sets`) require the deployment-admin claim (`CapabilityService.deploymentAdmin()` is the client signal for it). Member set-assignment (`PUT .../members/{userId}/sets`) requires the per-client `admin.users` capability. Gate the Sets screen/nav on **deployment admin**; gate the member set-picker's write on **`admin.users`** (reuse `*appCan="'admin.users'"` / `canWrite('admin.users', ...)`).
- **Confirm-on-save shows the affected-member count *before* the edit is applied.** The count is invariant under a capability edit (editing caps never changes *who* references the set), so it is read from the set-list entry (exposed by Task 1), not from the post-hoc PUT response. Creating a new set (0 members) skips the confirm.
- **environment.ts is NEVER committed.** `UI/Angular/src/app/core/api/environment.ts` carries `devClientId` and must stay out of every commit. Stage explicit paths only — never `git add -A`/`.`. IDE `.csproj`/`.slnx` churn stays UNCOMMITTED. Commit trailer required:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Test runners:** frontend `cd UI/Angular && ng test` (or `npx ng test`); backend `dotnet test Backend/Accounting101.Ledger.Api.Tests`.
- **Nav leaf count:** `nav.spec.ts` asserts a total leaf-path count and the `built` routes array in `app.routes.ts` must list any nav leaf backed by a real component (else it renders as a Placeholder). Update both when adding links.

---

## File Structure

**Backend — modify (Task 1):**
- `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs` — populate `AffectedMemberCount` on the list.
- `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` — add `GrantedSetIds` + `SetNames` to `MembershipResponse`.
- `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs` — resolve + populate set ids/names on the member list.
- Tests: `CapabilitySetEndpointsTests.cs`, `MemberSetAssignmentTests.cs` (append).

**Frontend — create:**
- `UI/Angular/src/app/core/capability-sets/capability-set.ts` — TS models.
- `UI/Angular/src/app/core/capability-sets/capability-set.service.ts` — deployment-scoped CRUD service.
- `UI/Angular/src/app/core/capability-sets/capability-set.service.spec.ts`
- `UI/Angular/src/app/features/admin/capability-set-list.ts` — Sets list screen.
- `UI/Angular/src/app/features/admin/capability-set-list.spec.ts`
- `UI/Angular/src/app/features/admin/capability-set-editor.ts` — Set create/edit + picker + confirm.
- `UI/Angular/src/app/features/admin/capability-set-editor.spec.ts`
- `UI/Angular/src/app/core/capabilities/deployment-admin.guard.ts` — route guard for deployment-admin-only screens.

**Frontend — modify:**
- `UI/Angular/src/app/core/members/member.ts` — add `grantedSetIds`/`setNames` to `Member`; add `AssignSetsRequest`.
- `UI/Angular/src/app/core/members/member.service.ts` — add `assignSets()`.
- `UI/Angular/src/app/core/members/member.service.spec.ts` — cover `assignSets()`.
- `UI/Angular/src/app/features/admin/member-editor.ts` — convert raw grid → set-picker.
- `UI/Angular/src/app/features/admin/member-editor.spec.ts` — update for set-picker.
- `UI/Angular/src/app/layout/nav.ts` — `NavLink.deploymentAdmin?` + the "Capability Sets" link; `visibleSections` honors the flag.
- `UI/Angular/src/app/layout/shell.ts` — pass a link-level predicate to `visibleSections`.
- `UI/Angular/src/app/layout/nav.spec.ts` — leaf-count + deployment-admin gating.
- `UI/Angular/src/app/app.routes.ts` — routes for the Sets screens + `built` entries.

---

### Task 1: Backend — expose set-reference data on reads

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs`, `Backend/Accounting101.Ledger.Api.Tests/MemberSetAssignmentTests.cs`

**Interfaces:**
- Consumes (AC-2): `ControlStore.CountMembersReferencingSetAsync(Guid)`, `ControlStore.ListCapabilitySetsAsync()`, `ControlStore.GetMembersAsync(clientId)`, `Membership.GrantedSetIds`, `ControlStore.SetMembershipSetsAsync`.
- Produces:
  - `GET /capability-sets` list entries now carry `AffectedMemberCount` = members referencing that set.
  - `MembershipResponse` gains trailing `IReadOnlyList<Guid>? GrantedSetIds = null, IReadOnlyList<string>? SetNames = null`; `GET /clients/{clientId}/members` populates both.

- [ ] **Step 1: Write the failing backend tests**

Append to `Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task List_reports_affected_member_count_per_set()
    {
        HttpClient admin = fixture.AdminClient();
        CapabilitySetResponse created = (await (await admin.PostAsJsonAsync("/capability-sets",
            new CreateCapabilitySetRequest("Listed " + Guid.NewGuid().ToString("N"), null, ["gl.read"])))
            .Content.ReadFromJsonAsync<CapabilitySetResponse>())!;
        await fixture.Control().SetMembershipSetsAsync(Guid.NewGuid(), Guid.NewGuid(), [created.Id]);

        List<CapabilitySetResponse> sets =
            (await admin.GetFromJsonAsync<List<CapabilitySetResponse>>("/capability-sets"))!;
        CapabilitySetResponse mine = sets.First(s => s.Id == created.Id);
        Assert.Equal(1, mine.AffectedMemberCount);
    }
```

Append to `Backend/Accounting101.Ledger.Api.Tests/MemberSetAssignmentTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Member_list_exposes_assigned_set_ids_and_names()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        Guid setId = await CreateSetAsync(Capabilities.GlRead, Capabilities.ArWrite);

        Guid newUser = Guid.NewGuid();
        await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/members",
            new AddClientMemberRequest(newUser, ["Auditor"], ["gl.read"]));
        await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/members/{newUser}/sets",
            new AssignSetsRequest([setId]));

        MembershipResponse[] members =
            (await c.Http.GetFromJsonAsync<MembershipResponse[]>($"/clients/{c.ClientId}/members"))!;
        MembershipResponse assigned = members.First(m => m.UserId == newUser);
        Assert.Contains(setId, assigned.GrantedSetIds!);
        Assert.NotEmpty(assigned.SetNames!);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "CapabilitySetEndpointsTests|MemberSetAssignmentTests"`
Expected: FAIL — list reports `AffectedMemberCount == 0`; `MembershipResponse` has no `GrantedSetIds`/`SetNames` (compile error).

- [ ] **Step 3: Add `GrantedSetIds` + `SetNames` to `MembershipResponse`**

In `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`, replace the `MembershipResponse` record:

```csharp
public sealed record MembershipResponse(
    Guid UserId, Guid ClientId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities,
    IReadOnlyList<Guid>? GrantedSetIds = null, IReadOnlyList<string>? SetNames = null);
```

- [ ] **Step 4: Populate `AffectedMemberCount` on the set list**

In `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs`, replace the `List` handler body so each response carries its reference count:

```csharp
    private static async Task<IResult> List(ControlStore control, CancellationToken ct)
    {
        IReadOnlyList<CapabilitySet> sets = await control.ListCapabilitySetsAsync(ct);
        List<CapabilitySetResponse> responses = [];
        foreach (CapabilitySet s in sets)
        {
            long count = await control.CountMembersReferencingSetAsync(s.Id, ct);
            responses.Add(ToResponse(s) with { AffectedMemberCount = (int)count });
        }
        return Results.Ok(responses);
    }
```

- [ ] **Step 5: Populate set ids/names on the member list**

In `Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs`, change `ToResponse` to resolve set names from a catalog, and have `ListMembers` load the catalog once and pass it in. Replace the `ToResponse` method:

```csharp
    private static MembershipResponse ToResponse(Membership m, IReadOnlyList<CapabilitySet> catalog)
    {
        Dictionary<Guid, string> nameById = catalog.ToDictionary(s => s.Id, s => s.Name);
        List<string> setNames = m.GrantedSetIds
            .Where(nameById.ContainsKey).Select(id => nameById[id]).ToList();
        return new MembershipResponse(
            m.UserId, m.ClientId,
            m.GrantedRoles.Select(r => r.ToString()).ToList(), m.Capabilities,
            m.GrantedSetIds, setNames);
    }
```

Replace `ListMembers` so it loads the catalog once and maps with it:

```csharp
    private static async Task<IResult> ListMembers(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        IReadOnlyList<Membership> members = await control.GetMembersAsync(clientId, ct);
        IReadOnlyList<CapabilitySet> catalog = await control.ListCapabilitySetsAsync(ct);
        return Results.Ok(members.Select(m => ToResponse(m, catalog)).ToList());
    }
```

The other `ToResponse(m)` call sites in this file (`AddMember`, `SetMember`) build `MembershipResponse` inline and are unaffected (they already pass only roles+caps; the new params default to null). If any call `ToResponse(m)` with one arg, update them to pass the catalog, or leave the inline `new MembershipResponse(...)` constructions as-is.

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "CapabilitySetEndpointsTests|MemberSetAssignmentTests"`
Expected: PASS. Then run the full project: `dotnet test Backend/Accounting101.Ledger.Api.Tests` — expected all green (added params default to null; existing member/set tests unaffected).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/CapabilitySetEndpoints.cs \
        Backend/Accounting101.Ledger.Contracts/AdminContracts.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/MemberEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/CapabilitySetEndpointsTests.cs \
        Backend/Accounting101.Ledger.Api.Tests/MemberSetAssignmentTests.cs
git commit -m "$(cat <<'EOF'
feat(access): expose set member-count on list + member set ids/names on read (AC-3)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Frontend models + services (CapabilitySetService, MemberService.assignSets)

**Files:**
- Create: `UI/Angular/src/app/core/capability-sets/capability-set.ts`, `capability-set.service.ts`, `capability-set.service.spec.ts`
- Modify: `UI/Angular/src/app/core/members/member.ts`, `member.service.ts`, `member.service.spec.ts`

**Interfaces:**
- Consumes: `environment.apiBaseUrl`; `ClientContextService` (members only); `HttpClient`.
- Produces:
  - `CapabilitySet { id; name; description?; capabilities: string[]; builtin: boolean; affectedMemberCount: number }`, `CreateCapabilitySetRequest { name; description?; capabilities }`, `UpdateCapabilitySetRequest { name; description?; capabilities }`.
  - `CapabilitySetService`: `list(): Observable<CapabilitySet[]>`, `create(req): Observable<CapabilitySet>`, `update(id, req): Observable<CapabilitySet>`, `remove(id): Observable<void>` — all against `${apiBaseUrl}/capability-sets` (no clientId).
  - `Member` gains `grantedSetIds: string[]`, `setNames: string[]`; `AssignSetsRequest { setIds: string[] }`; `MemberService.assignSets(userId, req): Observable<Member>` → `PUT .../members/{userId}/sets`.

- [ ] **Step 1: Write the `CapabilitySet` models**

Create `UI/Angular/src/app/core/capability-sets/capability-set.ts`:

```ts
export interface CapabilitySet {
  id: string;
  name: string;
  description?: string;
  capabilities: string[];
  builtin: boolean;
  affectedMemberCount: number;
}

export interface CreateCapabilitySetRequest {
  name: string;
  description?: string;
  capabilities: string[];
}

export interface UpdateCapabilitySetRequest {
  name: string;
  description?: string;
  capabilities: string[];
}
```

- [ ] **Step 2: Write the failing service spec**

Create `UI/Angular/src/app/core/capability-sets/capability-set.service.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CapabilitySetService } from './capability-set.service';
import { environment } from '../api/environment';

describe('CapabilitySetService', () => {
  let http: HttpTestingController;
  let svc: CapabilitySetService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    svc = TestBed.inject(CapabilitySetService);
  });
  afterEach(() => http.verify());

  it('lists sets from the deployment-scoped route', () => {
    svc.list().subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('creates a set', () => {
    svc.create({ name: 'Warehouse', capabilities: ['gl.read'] }).subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'Warehouse', capabilities: ['gl.read'] });
    req.flush({});
  });

  it('updates a set by id', () => {
    svc.update('s1', { name: 'Edited', capabilities: ['gl.read', 'gl.post'] }).subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets/s1`);
    expect(req.request.method).toBe('PUT');
    req.flush({});
  });

  it('deletes a set by id', () => {
    svc.remove('s1').subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets/s1`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });
});
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-set.service.spec.ts'`
Expected: FAIL — `CapabilitySetService` does not exist.

- [ ] **Step 4: Write `CapabilitySetService`**

Create `UI/Angular/src/app/core/capability-sets/capability-set.service.ts`:

```ts
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../api/environment';
import { CapabilitySet, CreateCapabilitySetRequest, UpdateCapabilitySetRequest } from './capability-set';

/** Deployment-scoped capability-set CRUD (no client context — the route is /capability-sets). */
@Injectable({ providedIn: 'root' })
export class CapabilitySetService {
  private readonly http = inject(HttpClient);
  private base(path = ''): string { return `${environment.apiBaseUrl}/capability-sets${path}`; }

  list(): Observable<CapabilitySet[]> { return this.http.get<CapabilitySet[]>(this.base()); }
  create(req: CreateCapabilitySetRequest): Observable<CapabilitySet> { return this.http.post<CapabilitySet>(this.base(), req); }
  update(id: string, req: UpdateCapabilitySetRequest): Observable<CapabilitySet> { return this.http.put<CapabilitySet>(this.base(`/${id}`), req); }
  remove(id: string): Observable<void> { return this.http.delete<void>(this.base(`/${id}`)); }
}
```

- [ ] **Step 5: Extend the `Member` model + `MemberService`**

In `UI/Angular/src/app/core/members/member.ts`, extend `Member` and add the request type:

```ts
export interface Member { userId: string; roles: string[]; capabilities: string[]; grantedSetIds: string[]; setNames: string[]; }
```
```ts
export interface AssignSetsRequest { setIds: string[]; }
```
(Leave `AddMemberRequest`, `SetMemberRequest`, `RolePreset`, `CapabilityCatalog` as they are.)

In `UI/Angular/src/app/core/members/member.service.ts`, add the method (beside `set`), returning `EMPTY` when no client is selected exactly like the sibling methods:

```ts
  assignSets(userId: string, req: AssignSetsRequest): Observable<Member> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<Member>(this.base(`/${userId}/sets`), req);
  }
```
Add `AssignSetsRequest` to the existing `member` import in the service file.

- [ ] **Step 6: Cover `assignSets` in the member service spec**

Append to `UI/Angular/src/app/core/members/member.service.spec.ts`:

```ts
  it('assigns sets to a member', () => {
    svc.assignSets('u1', { setIds: ['s1', 's2'] }).subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members/u1/sets`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ setIds: ['s1', 's2'] });
    req.flush({ userId: 'u1', roles: [], capabilities: [], grantedSetIds: ['s1', 's2'], setNames: [] });
  });
```

- [ ] **Step 7: Run to verify green**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-set.service.spec.ts' --include='**/member.service.spec.ts'`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/capability-sets/ \
        UI/Angular/src/app/core/members/member.ts \
        UI/Angular/src/app/core/members/member.service.ts \
        UI/Angular/src/app/core/members/member.service.spec.ts
git commit -m "$(cat <<'EOF'
feat(access): CapabilitySetService + MemberService.assignSets (AC-3)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Sets list screen + deployment-admin gating + nav/routes

**Files:**
- Create: `UI/Angular/src/app/features/admin/capability-set-list.ts`, `capability-set-list.spec.ts`, `UI/Angular/src/app/core/capabilities/deployment-admin.guard.ts`
- Modify: `UI/Angular/src/app/layout/nav.ts`, `shell.ts`, `nav.spec.ts`, `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `CapabilitySetService` (Task 2), `CapabilityService.deploymentAdmin`, `NavLink`/`NavSection`/`visibleSections`, the `canWrite`-guard pattern (`core/capabilities/can.guard.ts`).
- Produces: route `/admin/access/sets` → `CapabilitySetList` (guarded deployment-admin); `NavLink.deploymentAdmin?: boolean` honored by `visibleSections`; `deploymentAdminGuard(fallback)` CanActivateFn.

- [ ] **Step 1: Write the `deploymentAdmin` route guard**

Create `UI/Angular/src/app/core/capabilities/deployment-admin.guard.ts` (mirror `can.guard.ts`, which waits on `loaded` then checks a predicate):

```ts
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/** Allow only deployment admins; redirect others to `fallback` once capabilities have loaded. */
export function deploymentAdminGuard(fallback: string): CanActivateFn {
  return () => {
    const caps = inject(CapabilityService);
    const router = inject(Router);
    return toObservable(caps.loaded).pipe(
      filter((loaded) => loaded),
      take(1),
      map(() => (caps.deploymentAdmin() ? true : router.parseUrl(fallback))),
    );
  };
}
```

- [ ] **Step 2: Write the failing list-screen spec**

Create `UI/Angular/src/app/features/admin/capability-set-list.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CapabilitySetList } from './capability-set-list';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { environment } from '../../core/api/environment';

function seed() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities('admin.users')],
  });
}

describe('CapabilitySetList', () => {
  let http: HttpTestingController;
  beforeEach(() => { seed(); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());

  it('lists sets with their member counts', () => {
    const f = TestBed.createComponent(CapabilitySetList);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([
      { id: 's1', name: 'Controller', capabilities: ['gl.post'], builtin: true, affectedMemberCount: 3 },
    ]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Controller');
    expect(f.nativeElement.textContent).toContain('3');
  });

  it('deletes a set and refreshes', () => {
    const f = TestBed.createComponent(CapabilitySetList);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([
      { id: 's2', name: 'Custom', capabilities: ['gl.read'], builtin: false, affectedMemberCount: 0 },
    ]);
    f.detectChanges();
    (f.componentInstance as CapabilitySetList).remove({ id: 's2', name: 'Custom', capabilities: ['gl.read'], builtin: false, affectedMemberCount: 0 });
    http.expectOne(`${environment.apiBaseUrl}/capability-sets/s2`).flush(null);
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([]);
  });
});
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-set-list.spec.ts'`
Expected: FAIL — `CapabilitySetList` does not exist.

- [ ] **Step 4: Write the list screen**

Create `UI/Angular/src/app/features/admin/capability-set-list.ts`:

```ts
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { CapabilitySetService } from '../../core/capability-sets/capability-set.service';
import { CapabilitySet } from '../../core/capability-sets/capability-set';

@Component({
  selector: 'app-capability-set-list',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, ...HlmTableImports],
  template: `
    <div class="flex items-center justify-between mb-4">
      <h1 class="text-xl font-semibold">Capability Sets</h1>
      <button hlmBtn (click)="create()">New set</button>
    </div>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }
    <div hlmTableContainer>
      <table hlmTable>
        <thead hlmTHead>
          <tr hlmTr><th hlmTh>Name</th><th hlmTh>Capabilities</th><th hlmTh>Members</th><th hlmTh></th></tr>
        </thead>
        <tbody hlmTBody>
          @for (s of sets(); track s.id) {
            <tr hlmTr class="cursor-pointer" (click)="edit(s)">
              <td hlmTd>{{ s.name }} @if (s.builtin) { <span class="text-xs text-muted-foreground">(built-in)</span> }</td>
              <td hlmTd>{{ s.capabilities.length }}</td>
              <td hlmTd>{{ s.affectedMemberCount }}</td>
              <td hlmTd>
                <button hlmBtn variant="destructive" size="sm"
                        (click)="$event.stopPropagation(); remove(s)">Delete</button>
              </td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class CapabilitySetList {
  private readonly service = inject(CapabilitySetService);
  private readonly router = inject(Router);
  protected readonly sets = signal<CapabilitySet[]>([]);
  protected readonly error = signal<string | null>(null);

  constructor() { this.reload(); }

  private reload(): void {
    this.service.list().subscribe({ next: (s) => this.sets.set(s), error: () => this.error.set('Failed to load sets.') });
  }

  protected create(): void { void this.router.navigate(['/admin/access/sets/new']); }
  protected edit(s: CapabilitySet): void { void this.router.navigate(['/admin/access/sets', s.id]); }

  remove(s: CapabilitySet): void {
    this.error.set(null);
    this.service.remove(s.id).subscribe({
      next: () => this.reload(),
      error: (e) => this.error.set(e?.error?.detail ?? `Cannot delete "${s.name}".`),
    });
  }
}
```

- [ ] **Step 5: Run the list spec to green**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-set-list.spec.ts'`
Expected: PASS.

- [ ] **Step 6: Add the nav link + deployment-admin gating**

In `UI/Angular/src/app/layout/nav.ts`, extend `NavLink` with the flag:

```ts
export interface NavLink { label: string; path: string; area?: string; deploymentAdmin?: boolean; children?: NavLink[]; }
```

Add the link to the Administration section's `items` (after 'Users & Roles'):

```ts
    { label: 'Capability Sets', path: '/admin/access/sets', area: 'admin', deploymentAdmin: true },
```

Change `visibleSections` to filter on a **link predicate** rather than just an area predicate. Replace its signature/body:

```ts
export function visibleSections(sections: NavSection[], canSee: (link: NavLink) => boolean): NavSection[] {
  return sections
    .map((section) => ({
      ...section,
      items: section.items
        .filter(canSee)
        .map((item) => ({ ...item, children: item.children?.filter(canSee) })),
    }))
    .filter((section) => section.items.length > 0);
}
```

In `UI/Angular/src/app/layout/shell.ts`, update the `visibleNav` computed to pass a link predicate that honors both `area` and `deploymentAdmin`:

```ts
  protected readonly visibleNav = computed(() =>
    visibleSections(NAV, (link) =>
      (!link.area || this.caps.hasArea(link.area)) &&
      (!link.deploymentAdmin || this.caps.deploymentAdmin())));
```

- [ ] **Step 7: Update `nav.spec.ts`**

In `UI/Angular/src/app/layout/nav.spec.ts`: bump the expected leaf-path count by 1 (new 'Capability Sets' leaf), and update any `visibleSections(...)` calls to pass a **link predicate** (e.g. `(link) => !link.area || allowed.has(link.area)`) instead of an area predicate. Add a case asserting the Capability Sets link is hidden when `deploymentAdmin` is false even if `admin` area is allowed, and shown when true:

```ts
  it('hides deployment-admin links from non-deployment-admins', () => {
    const seen = visibleSections(NAV, (l) => (!l.area || l.area === 'admin') && !l.deploymentAdmin);
    const admin = seen.find((s) => s.label === 'Administration');
    expect(admin?.items.some((i) => i.path === '/admin/access/sets')).toBe(false);
  });
```

- [ ] **Step 8: Add the routes**

In `UI/Angular/src/app/app.routes.ts`: import `CapabilitySetList` + `deploymentAdminGuard`, add the route, and add `/admin/access/sets` to the `built` array so it isn't Placeholder'd:

```ts
{ path: 'admin/access/sets', component: CapabilitySetList, canActivate: [deploymentAdminGuard('/admin/users')] },
```

- [ ] **Step 9: Run the nav + list specs, verify green**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/nav.spec.ts' --include='**/capability-set-list.spec.ts'`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add UI/Angular/src/app/features/admin/capability-set-list.ts \
        UI/Angular/src/app/features/admin/capability-set-list.spec.ts \
        UI/Angular/src/app/core/capabilities/deployment-admin.guard.ts \
        UI/Angular/src/app/layout/nav.ts \
        UI/Angular/src/app/layout/shell.ts \
        UI/Angular/src/app/layout/nav.spec.ts \
        UI/Angular/src/app/app.routes.ts
git commit -m "$(cat <<'EOF'
feat(access): Capability Sets list screen + deployment-admin nav gating (AC-3)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Set editor — capability picker + confirm-on-save

**Files:**
- Create: `UI/Angular/src/app/features/admin/capability-set-editor.ts`, `capability-set-editor.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (routes + `built`)

**Interfaces:**
- Consumes: `CapabilitySetService`; `MemberService.catalog()` (existing `GET /capabilities/catalog` → `CapabilityCatalog { capabilities; roles }`); `ActivatedRoute` (`:id` param); the raw-checkbox-grouped-by-area pattern from `member-editor.ts:88-99`.
- Produces: route `/admin/access/sets/new` and `/admin/access/sets/:id` → `CapabilitySetEditor` (deployment-admin-guarded). On edit with `affectedMemberCount > 0`, Save reveals an inline confirm panel before the PUT.

- [ ] **Step 1: Write the failing editor spec**

Create `UI/Angular/src/app/features/admin/capability-set-editor.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CapabilitySetEditor } from './capability-set-editor';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { environment } from '../../core/api/environment';

function route(id: string | null) {
  return { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } };
}
function seed(id: string | null) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities('admin.users'), route(id)],
  });
}
const CATALOG = { capabilities: ['gl.read', 'gl.post', 'ar.write'], roles: [] };

describe('CapabilitySetEditor', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('creates a new set without a confirm step', () => {
    seed(null); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(CapabilitySetEditor);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capabilities/catalog`).flush(CATALOG);
    f.detectChanges();
    const c = f.componentInstance as CapabilitySetEditor;
    c.setName('Warehouse'); c.toggleCapability('gl.read'); f.detectChanges();
    c.save();
    // No confirm needed for a new set → POST fires immediately.
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets`);
    expect(req.request.method).toBe('POST');
    req.flush({ id: 'x', name: 'Warehouse', capabilities: ['gl.read'], builtin: false, affectedMemberCount: 0 });
  });

  it('requires confirmation before editing a set that has members', () => {
    seed('s1'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(CapabilitySetEditor);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capabilities/catalog`).flush(CATALOG);
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([
      { id: 's1', name: 'Controller', capabilities: ['gl.read'], builtin: true, affectedMemberCount: 4 },
    ]);
    f.detectChanges();
    const c = f.componentInstance as CapabilitySetEditor;
    c.toggleCapability('gl.post');
    c.save();                       // does NOT PUT yet — asks to confirm
    http.expectNone(`${environment.apiBaseUrl}/capability-sets/s1`);
    expect(c.confirming()).toBe(true);
    expect(f.nativeElement.textContent).toContain('4');
    c.confirmSave();                // now it PUTs
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets/s1`);
    expect(req.request.method).toBe('PUT');
    req.flush({ id: 's1', name: 'Controller', capabilities: ['gl.read', 'gl.post'], builtin: true, affectedMemberCount: 4 });
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-set-editor.spec.ts'`
Expected: FAIL — `CapabilitySetEditor` does not exist.

- [ ] **Step 3: Write the editor**

Create `UI/Angular/src/app/features/admin/capability-set-editor.ts`:

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { CapabilitySetService } from '../../core/capability-sets/capability-set.service';
import { CapabilitySet } from '../../core/capability-sets/capability-set';
import { MemberService } from '../../core/members/member.service';

interface CapGroup { area: string; capabilities: string[]; }

@Component({
  selector: 'app-capability-set-editor',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, ...HlmInputImports, ...HlmLabelImports],
  template: `
    <h1 class="text-xl font-semibold mb-4">{{ editId ? 'Edit set' : 'New set' }}</h1>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }

    <label hlmLabel>Name
      <input hlmInput [value]="name()" (input)="setName($any($event.target).value)" />
    </label>
    <label hlmLabel class="block mt-3">Description
      <input hlmInput [value]="description()" (input)="setDescription($any($event.target).value)" />
    </label>

    <div class="mt-4 space-y-3">
      @for (g of groups(); track g.area) {
        <fieldset class="border rounded p-3">
          <legend class="text-sm font-medium">{{ g.area }}</legend>
          @for (cap of g.capabilities; track cap) {
            <label class="flex items-center gap-2">
              <input type="checkbox" [checked]="selected().has(cap)" (change)="toggleCapability(cap)" />
              <span>{{ cap }}</span>
            </label>
          }
        </fieldset>
      }
    </div>

    @if (confirming()) {
      <div class="mt-4 border border-amber-500 rounded p-3">
        <p>This set is held by <strong>{{ current()?.affectedMemberCount }}</strong> member(s).
           Applying these changes updates their access immediately.</p>
        <div class="flex gap-2 mt-2">
          <button hlmBtn (click)="confirmSave()">Apply changes</button>
          <button hlmBtn variant="outline" (click)="cancelConfirm()">Cancel</button>
        </div>
      </div>
    } @else {
      <div class="flex gap-2 mt-4">
        <button hlmBtn [disabled]="!name().trim()" (click)="save()">Save</button>
        <button hlmBtn variant="outline" (click)="back()">Cancel</button>
      </div>
    }
  `,
})
export class CapabilitySetEditor {
  private readonly service = inject(CapabilitySetService);
  private readonly members = inject(MemberService);
  private readonly router = inject(Router);
  protected readonly editId = inject(ActivatedRoute).snapshot.paramMap.get('id');

  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly selected = signal<Set<string>>(new Set());
  protected readonly current = signal<CapabilitySet | null>(null);
  protected readonly confirming = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly catalog = signal<string[]>([]);

  protected readonly groups = computed<CapGroup[]>(() => {
    const byArea = new Map<string, string[]>();
    for (const cap of this.catalog()) {
      const area = cap.split('.')[0];
      byArea.set(area, [...(byArea.get(area) ?? []), cap]);
    }
    return [...byArea.entries()].map(([area, capabilities]) => ({ area, capabilities }));
  });

  constructor() {
    this.members.catalog().subscribe({ next: (c) => this.catalog.set(c.capabilities) });
    if (this.editId) {
      this.service.list().subscribe({
        next: (sets) => {
          const set = sets.find((s) => s.id === this.editId);
          if (!set) { this.error.set('Set not found.'); return; }
          this.current.set(set);
          this.name.set(set.name);
          this.description.set(set.description ?? '');
          this.selected.set(new Set(set.capabilities));
        },
      });
    }
  }

  setName(v: string): void { this.name.set(v); }
  setDescription(v: string): void { this.description.set(v); }
  toggleCapability(cap: string): void {
    const next = new Set(this.selected());
    next.has(cap) ? next.delete(cap) : next.add(cap);
    this.selected.set(next);
  }

  save(): void {
    this.error.set(null);
    // New set (or an edited set nobody holds) applies immediately; otherwise confirm the blast radius.
    if (this.editId && (this.current()?.affectedMemberCount ?? 0) > 0) { this.confirming.set(true); return; }
    this.persist();
  }
  confirmSave(): void { this.confirming.set(false); this.persist(); }
  cancelConfirm(): void { this.confirming.set(false); }

  private persist(): void {
    const req = {
      name: this.name().trim(),
      description: this.description().trim() || undefined,
      capabilities: [...this.selected()],
    };
    const call = this.editId ? this.service.update(this.editId, req) : this.service.create(req);
    call.subscribe({
      next: () => this.back(),
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }

  back(): void { void this.router.navigate(['/admin/access/sets']); }
}
```

- [ ] **Step 4: Run the editor spec to green**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/capability-set-editor.spec.ts'`
Expected: PASS.

- [ ] **Step 5: Add the editor routes**

In `UI/Angular/src/app/app.routes.ts`: import `CapabilitySetEditor`, add both routes (guarded), and add both paths to `built`:

```ts
{ path: 'admin/access/sets/new', component: CapabilitySetEditor, canActivate: [deploymentAdminGuard('/admin/users')] },
{ path: 'admin/access/sets/:id', component: CapabilitySetEditor, canActivate: [deploymentAdminGuard('/admin/users')] },
```
(Place the `:id` route AFTER `sets/new` so `new` isn't captured as an id. Add `'/admin/access/sets/new'` to the `built` array; the `:id` route is parameterized and needs no Placeholder entry.)

- [ ] **Step 6: Run the full frontend suite**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — all existing specs plus the new set-editor/list/service specs.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/features/admin/capability-set-editor.ts \
        UI/Angular/src/app/features/admin/capability-set-editor.spec.ts \
        UI/Angular/src/app/app.routes.ts
git commit -m "$(cat <<'EOF'
feat(access): capability-set editor with picker + confirm-on-save (AC-3)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Convert the member editor into a set-picker

**Files:**
- Modify: `UI/Angular/src/app/features/admin/member-editor.ts`, `member-editor.spec.ts`

**Interfaces:**
- Consumes: `CapabilitySetService.list()` (available sets), `MemberService.list()` (find the member's current `grantedSetIds`), `MemberService.add`/`assignSets`; `*appCan="'admin.users'"` gating; `DevIdentityService`/`memberDisplayName` if already used.
- Produces: `/admin/users/:userId` (and `/admin/users/new`) editor now selects **capability sets** (checkbox list) and saves via `assignSets` (existing member) — replacing the raw-capability grid.

- [ ] **Step 1: Update the member-editor spec for the set-picker**

Rewrite `UI/Angular/src/app/features/admin/member-editor.spec.ts` to drive the set-based flow. Keep the existing providers pattern (`provideCapabilities('admin.users')`, `route(userId)`, `HttpTestingController`). New assertions:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MemberEditor } from './member-editor';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function route(id: string | null) {
  return { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } };
}
function seed(id: string | null) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities('admin.users'), route(id)],
  });
  TestBed.inject(ClientContextService).select('c1');
}

describe('MemberEditor (set-picker)', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('preselects the member\'s current sets and assigns the chosen ones', () => {
    seed('u1'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(MemberEditor);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([
      { id: 's1', name: 'ArClerk', capabilities: ['ar.write'], builtin: true, affectedMemberCount: 0 },
      { id: 's2', name: 'Auditor', capabilities: ['audit.read'], builtin: true, affectedMemberCount: 0 },
    ]);
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/members`).flush([
      { userId: 'u1', roles: [], capabilities: ['ar.write'], grantedSetIds: ['s1'], setNames: ['ArClerk'] },
    ]);
    f.detectChanges();
    const c = f.componentInstance as MemberEditor;
    expect(c.selected().has('s1')).toBe(true);
    c.toggleSet('s2');
    c.save();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members/u1/sets`);
    expect(req.request.method).toBe('PUT');
    expect(new Set(req.request.body.setIds)).toEqual(new Set(['s1', 's2']));
    req.flush({ userId: 'u1', roles: [], capabilities: [], grantedSetIds: ['s1', 's2'], setNames: [] });
    expect(TestBed.inject(Router).url).toBeDefined();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/member-editor.spec.ts'`
Expected: FAIL — `MemberEditor` still uses the raw-capability grid / has no `toggleSet`/set-based `selected`.

- [ ] **Step 3: Rewrite `member-editor.ts` as a set-picker**

Replace `UI/Angular/src/app/features/admin/member-editor.ts` with a set-based editor (mirror the prior file's providers/gating; swap the capability grid for a set checkbox list; on save call `assignSets`):

```ts
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { CanDirective } from '../../core/capabilities/can.directive';
import { CapabilitySetService } from '../../core/capability-sets/capability-set.service';
import { CapabilitySet } from '../../core/capability-sets/capability-set';
import { MemberService } from '../../core/members/member.service';

@Component({
  selector: 'app-member-editor',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective],
  template: `
    <h1 class="text-xl font-semibold mb-4">Member access</h1>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }
    <p class="text-sm text-muted-foreground mb-3">Assign one or more capability sets. The member's
       access is the union of the selected sets, applied immediately.</p>

    <div class="space-y-2">
      @for (s of sets(); track s.id) {
        <label class="flex items-center gap-2">
          <input type="checkbox" [checked]="selected().has(s.id)" (change)="toggleSet(s.id)" />
          <span>{{ s.name }} @if (s.builtin) { <span class="text-xs text-muted-foreground">(built-in)</span> }</span>
        </label>
      }
    </div>

    <div class="flex gap-2 mt-4">
      <button *appCan="'admin.users'" hlmBtn (click)="save()">Save</button>
      <button hlmBtn variant="outline" (click)="back()">Cancel</button>
    </div>
  `,
})
export class MemberEditor {
  private readonly setService = inject(CapabilitySetService);
  private readonly members = inject(MemberService);
  private readonly router = inject(Router);
  protected readonly userId = inject(ActivatedRoute).snapshot.paramMap.get('userId');

  protected readonly sets = signal<CapabilitySet[]>([]);
  protected readonly selected = signal<Set<string>>(new Set());
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.setService.list().subscribe({ next: (s) => this.sets.set(s) });
    if (this.userId) {
      this.members.list().subscribe({
        next: (members) => {
          const me = members.find((m) => m.userId === this.userId);
          if (me) this.selected.set(new Set(me.grantedSetIds));
        },
      });
    }
  }

  toggleSet(id: string): void {
    const next = new Set(this.selected());
    next.has(id) ? next.delete(id) : next.add(id);
    this.selected.set(next);
  }

  save(): void {
    if (!this.userId) return;
    this.error.set(null);
    this.members.assignSets(this.userId, { setIds: [...this.selected()] }).subscribe({
      next: () => this.back(),
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }

  back(): void { void this.router.navigate(['/admin/users']); }
}
```

Note: the pre-existing `/admin/users/new` route (adding a brand-new member) targeted the raw editor. Set-assignment requires an existing membership (the backend `/sets` route 404s a non-member). Leave `/admin/users/new` pointing at this editor but with `userId` null — the Save is a no-op guard (`if (!this.userId) return;`); creating the initial membership stays a deployment-admin/`AddMember` concern out of AC-3 scope. If the reviewer flags the dead `new` route, drop it from `app.routes.ts` and the nav in a follow-up; do not expand scope here.

- [ ] **Step 4: Run the member-editor spec to green**

Run: `cd UI/Angular && npx ng test --watch=false --include='**/member-editor.spec.ts'`
Expected: PASS.

- [ ] **Step 5: Run the full frontend suite**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — no regressions across the app.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/admin/member-editor.ts \
        UI/Angular/src/app/features/admin/member-editor.spec.ts
git commit -m "$(cat <<'EOF'
feat(access): member editor becomes a capability-set picker (AC-3)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Final verification

- [ ] Backend: `dotnet test Backend/Accounting101.Ledger.Api.Tests` — all green.
- [ ] Frontend: `cd UI/Angular && npx ng test --watch=false` — all green.
- [ ] Confirm only intended files are staged across the five commits — **no `environment.ts`**, no `.csproj`/`.slnx` churn.
- [ ] Manual smoke (optional, dev stack running): as the deployment-admin "Acting as" identity, open **Administration ▸ Capability Sets**, create a set, edit a built-in (observe the "held by N members" confirm), and from **Users & Roles** assign sets to a member; confirm a non-deployment-admin identity does not see the Capability Sets link.

---

## Self-Review notes (against the AC-3 spec section C)

- **"Access Control ▸ Capability Sets — list + create/edit/delete with a capability picker"** → Tasks 3 (list) + 4 (editor + raw-checkbox picker grouped by area from `/capabilities/catalog`). ✓
- **"Saving an edit shows a confirm dialog with the affected-member count before it takes effect"** → Task 4 inline confirm panel; count sourced from the set-list entry (invariant under the edit), which **Task 1** exposes on `GET /capability-sets` (AC-2 only returned it post-hoc on PUT). New-set create skips the confirm (0 members). The confirm is an inline panel, not a Spartan modal (checkbox/dialog helm libs are not scaffolded; matches the codebase's raw-checkbox + `hlmBtn` convention). ✓
- **"Members — assign membership by picking one or more sets; reuse Slice D MemberService/member-list; member-editor becomes a set-picker"** → Task 5 converts `member-editor.ts` into a set checkbox list saving via `assignSets` → AC-2 `PUT .../members/{userId}/sets`; folded into the existing `/admin/users` (user decision 2026-07-03). Preselection needs the member's `grantedSetIds`, which **Task 1** adds to the member-list response. ✓
- **"New CapabilitySetService"** → Task 2, deployment-scoped (no clientId). ✓
- **Deployment-admin vs per-client gating** → Sets screens guarded by `deploymentAdminGuard` + nav `deploymentAdmin` flag (Task 3); member set-picker write gated `*appCan="'admin.users'"` (Task 5). ✓
- **Backend touches beyond "frontend-only"** — Task 1 exposes on reads the two values the approved UX requires (set member-count; member set ids/names). Called out as a deliberate, additive (default-null / count-on-list) extension of AC-2's read surface, not a scope creep — the UI cannot honor "confirm before it takes effect" or "preselect current sets" without them. ✓
- **Type consistency:** `CapabilitySet` (with `affectedMemberCount`), `CapabilitySetService.{list,create,update,remove}`, `Member.{grantedSetIds,setNames}`, `AssignSetsRequest{setIds}`, `MemberService.assignSets`, `deploymentAdminGuard(fallback)`, `NavLink.deploymentAdmin`, `visibleSections(sections, (link)=>bool)` are used identically across Tasks 1–5 and their specs. ✓
- **Deferred (out of AC-3 scope):** AC-4 liveness (403 self-heal interceptor, live route sentinel, idle poll); creating a brand-new membership from the set-picker (`/admin/users/new` left inert — initial membership stays an `AddMember` concern); a polished Spartan modal for the confirm; showing set names as chips in the member list.
