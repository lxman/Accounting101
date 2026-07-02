# Slice D — Admin Users & Roles + Visibility Config — Design

**Date:** 2026-07-02
**Status:** Approved (design)
**Parent:** `2026-07-01-role-based-nav-rbac-design.md` (umbrella). Sub-project D.
Slices 0, A, B, C shipped. This slice makes the capability subsystem
owner-manageable: a per-client Admin assigns roles and tunes each member's
capability set (the configurable visibility range) through a UI.

## Goal

Give a per-client Admin a Users & Roles screen to list members, add/remove them,
and edit each member's roles + capability set — turning the capability subsystem
from dev-switcher-driven into owner-managed. Backed by per-client member
endpoints that enforce `admin.users`.

## Decisions (from brainstorming)

- **Editing UX = presets + per-capability toggles.** An admin picks role
  preset(s) as a base (expands to a capability set) and then toggles individual
  capabilities to customize the exact read/write scope — the full realization of
  "owner-configurable visibility range."
- **Full CRUD** — edit / add (by userId) / remove, with a **last-admin guard**
  (an operation may not leave a client with zero members holding `admin.users`).
- **Enforce `admin.users`** on the new per-client member endpoints (real,
  server-side) — the first admin-capability enforcement, scoped to these new
  endpoints (distinct from the deferred Slice E ledger retrofit). Deployment
  admins (`admin=true` claim) are also allowed (they manage everything).

## Backend reality this builds on

- `Control/ControlStore.cs` — `AddMembershipAsync`/`AddMembershipRolesAsync`
  (create-only), `GetMembershipAsync`/`GetMembersAsync` (hydrated). **No update or
  delete.**
- `Control/Membership.cs` — `{ Id, UserId, ClientId, GrantedRoles: LedgerRole[],
  Capabilities: string[], LegacyRole? }`.
- `Control/Capabilities.cs` — `Capabilities.All` (full vocabulary),
  `AdminUsers = "admin.users"`. `Control/RolePresets.cs` — `For(role)`,
  `CapabilitiesFor(roles)`; `LedgerRole` = Auditor/Clerk/Approver/Controller/
  Admin/ArClerk/ApClerk/PayrollClerk/CashClerk.
- `Endpoints/CapabilitiesEndpoints.cs` — the pattern for a per-client endpoint
  that resolves the caller's membership (`actorFactory.Create(user)` →
  `control.GetMembershipAsync`).
- `Endpoints/AdminEndpoints.cs` — deployment-admin (`/admin/**`) member
  provisioning stays as-is for deployment admins.
- Contracts in `Accounting101.Ledger.Contracts`; camelCase JSON; strict binding.

## Backend changes

### ControlStore (`Control/ControlStore.cs`)
- `SetMembershipAsync(userId, clientId, IReadOnlyList<LedgerRole> roles, IReadOnlyList<string> capabilities, ct)` — ReplaceOne upsert storing the exact roles + capabilities (create or replace).
- `RemoveMembershipAsync(userId, clientId, ct)` — DeleteOne.
- (Keep existing methods. `GetMembersAsync` already returns hydrated members for the list + last-admin check.)

### Member endpoints — `Endpoints/MemberEndpoints.cs` (new)
Route group `/clients/{clientId:guid}` + `.RequireAuthorization()`. Every handler
first calls a shared `RequireAdminUsers` check: allow if the caller has the
`admin=true` deployment claim OR their membership holds `admin.users`; else 403.

- `GET /clients/{clientId}/members` → `MembershipResponse[]` (userId, clientId, roles, capabilities).
- `POST /clients/{clientId}/members` → body `AddClientMemberRequest(Guid UserId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities)`; 409 if already a member; 422 on unknown role/capability; validates roles parse to `LedgerRole` and every capability ∈ `Capabilities.All`; stores verbatim; returns the `MembershipResponse`.
- `PUT /clients/{clientId}/members/{userId:guid}` → body `SetMemberRequest(IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities)`; 404 if not a member; 422 on unknown values; **last-admin guard** (below); stores; returns the `MembershipResponse`.
- `DELETE /clients/{clientId}/members/{userId:guid}` → 404 if not a member; **last-admin guard**; removes; 204.

**Last-admin guard:** a PUT that would remove `admin.users` from a member, or a
DELETE of a member who holds it, is rejected (409, "cannot remove the last
administrator") if no *other* member of the client would still hold `admin.users`.
Computed from `GetMembersAsync` + the pending change. Self-removal is allowed as
long as another admin remains.

### Capability catalog — `Endpoints/CapabilityCatalogEndpoints.cs` (new)
- `GET /capabilities/catalog` (RequireAuthorization, not client-scoped) →
  `CapabilityCatalogResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<RolePresetDto> Roles)` where `RolePresetDto(string Role, IReadOnlyList<string> Capabilities)`. Sourced from `Capabilities.All` + `RolePresets` for every `LedgerRole`. Lets the editor render the toggle grid and expand presets from backend truth (no client-side duplication/drift).

### Contracts (`Accounting101.Ledger.Contracts`)
- `AddClientMemberRequest(Guid UserId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities)`
- `SetMemberRequest(IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities)`
- `CapabilityCatalogResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<RolePresetDto> Roles)`; `RolePresetDto(string Role, IReadOnlyList<string> Capabilities)`
- Reuse existing `MembershipResponse(Guid UserId, Guid ClientId, IReadOnlyList<string> Roles, IReadOnlyList<string> Capabilities)`.

## Frontend changes

### Core service — `core/members/member.service.ts`
Root singleton. Base `${apiBaseUrl}/clients/${clientId()}/members`:
- `list(): Observable<Member[]>`, `add(req): Observable<Member>`, `set(userId, req): Observable<Member>`, `remove(userId): Observable<void>`.
- `catalog(): Observable<CapabilityCatalog>` → `GET ${apiBaseUrl}/capabilities/catalog`.
- Types in `core/members/member.ts`: `Member { userId; roles: string[]; capabilities: string[] }`, request shapes, `CapabilityCatalog { capabilities: string[]; roles: { role: string; capabilities: string[] }[] }`.

### Screens — `features/admin/`
- `member-list.ts` at `/admin/users`: table of members — display name (map known dev subs → names; fall back to the userId), roles, capability count. Whole-row → edit. "Add member" button → `/admin/users/new`.
- `member-editor.ts` at `/admin/users/new` and `/admin/users/:userId`:
  - Fetches the catalog. Role presets rendered as checkboxes; checking a preset unions its capabilities into the working set (unchecking does not force-remove — the admin then tunes individually). A capability toggle grid grouped by area (gl / ar / ap / payroll / cash / bankrec / fixedassets / audit / reports / admin), each capability a checkbox. New-member form also has a userId field (GUID).
  - Save: POST (new) or PUT (existing) with the final `{ roles, capabilities }`. Surfaces the 409 last-admin message via `extractProblem(e).detail`.
  - Remove (existing only): DELETE with confirm; surfaces the last-admin 409.
- Routes: `/admin/users` (list), `/admin/users/new`, `/admin/users/:userId` (editor) — replace the `/admin/users` placeholder. Guard the editor routes with `canWrite('admin.users', '/admin/users')`; the whole area is already nav-gated on `admin.*` (Slice B).

## Out of scope

- A real user directory / search (members are userId GUIDs; add-by-GUID, dev-sub→name map for display).
- Firm/Client/Fiscal/Posting-accounts admin screens (other Administration items — future).
- Backend enforcement of subledger/admin capabilities on the *ledger* endpoints (**Slice E**).
- Editing role PRESET definitions (presets are fixed backend truth; the admin customizes per-member sets, not the presets).

## Testing & verification

- **Backend:** ControlStore set/remove unit tests; MemberEndpoints integration tests — admin.users (and deployment-admin) can list/add/edit/remove; a non-admin member → 403; add duplicate → 409; unknown role/capability → 422; PUT/DELETE last-admin → 409; catalog returns full vocabulary + presets. Existing suites green.
- **Frontend:** member.service spec (URLs/verbs); member-list spec (rows, add button, gated); member-editor spec (preset expands into toggles, toggle edits, POST vs PUT, remove, 409 surfaced); route-guard on the editor. Full suite green.
- **Smoke (finish):** as Dev Admin, open Users & Roles → see the seeded members; edit Dev AR Clerk to add `ap.read` (a visibility widen) → save → switch to Dev AR Clerk → Payables now visible read-only in the sidebar (proves owner-config drives the nav). Try to strip `admin.users` from the only admin → blocked (409). As Dev Auditor, `/admin/users` is not in the nav and the route redirects.

## Execution

One branch (`feature/nav-slice-d-admin-users`), subagent-driven. Four tasks:
(1) ControlStore set/remove + contracts + catalog data + unit tests;
(2) MemberEndpoints (CRUD + admin.users gate + last-admin guard) + catalog endpoint + Program wiring + integration tests;
(3) frontend member.service + catalog + types + specs;
(4) member-list + member-editor + routes + guard + specs.
Then a controller smoke test across the roster.
