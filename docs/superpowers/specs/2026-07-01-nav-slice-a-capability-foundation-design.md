# Slice A — Backend Capability Foundation — Design

**Date:** 2026-07-01
**Status:** Approved (design)
**Parent:** `2026-07-01-role-based-nav-rbac-design.md` (umbrella). Sub-project A of
that decomposition. Slice 0 (grouped sidebar) already shipped. This slice builds
the backend source of truth for capabilities; Slice B consumes it to filter the
sidebar and gate screens.

## Goal

Give the backend a capability model and a per-client endpoint that returns the
acting user's resolved capability set, so the frontend can drive role-based
navigation and in-screen write-gating from a persistent, server-owned source of
truth (never the token claim).

## Decisions (from brainstorming)

- **Authority model: per-member capability set.** A membership stores the
  resolved set of capabilities it holds. Roles become **grant-time presets** that
  expand into that set. This directly enables Slice D's owner-configurable
  visibility (edit the set) and makes union of overlapping roles trivial (union
  of preset sets at grant time).
- **Response includes role labels.** The endpoint returns
  `{ capabilities, roles, deploymentAdmin }`; the membership therefore also
  records the granted preset(s) for display (distinct from the authoritative
  capability set).
- **GL enforcement flips to capability-derived**, behavior-identical for the five
  existing roles.

## Backend reality this builds on

- `Backend/Accounting101.Ledger.Api/Control/Authorization.cs` — `LedgerRole`
  {Auditor, Clerk, Approver, Controller, Admin}; `Permission` {Read, Post, Revise,
  Approve, Void, Reverse, Close, ManageAccounts, Reopen}; `RolePermissions` static
  map + `Allows(role, perm)`.
- `Control/Membership.cs` — `{ Id, UserId, ClientId, Role: LedgerRole }` (single
  role, stored as BSON string). One doc per (user, client) by app convention (no
  DB unique index).
- `Control/ControlStore.cs` — `GetMembershipAsync`, `AddMembershipAsync`
  (idempotent create-only), `GetMembersAsync`; collections `clients`,
  `memberships`, `modules`.
- `Endpoints/LedgerGateway.cs:18-29` — `ResolveAsync(user, clientId, Permission
  required)`: the ONE production choke point; `actorFactory.Create(user)` → userId
  → `control.GetMembershipAsync` → `RolePermissions.Allows(role, required)` →
  403/resolve. Permission is resolved fresh from the DB per request; the `role`
  claim in the dev token is NOT consulted.
- `Auth/DevTokenAuthenticationHandler.cs` / `ClaimsActorFactory` — caller identity
  from `DevToken` claims; `admin=true` claim gates the `DeploymentAdmin` policy
  (deployment-admin axis, separate from `LedgerRole`).
- Minimal APIs; endpoints under `app.MapGroup("/clients/{clientId:guid}")
  .RequireAuthorization()`. DTOs are `sealed record`s in
  `Accounting101.Ledger.Contracts`; camelCase JSON (Web defaults, strict binding).
- Tests: `Backend/Accounting101.Ledger.Api.Tests` with `ApiFixture`
  (`SeedClientAsync(role)`, `AddMemberAsync(role)`, `ClientFor`, `AdminClient`,
  `Control()`).

## Capability vocabulary — `Control/Capabilities.cs` (new)

String constants in `area.level` form (matches the wire format and the umbrella):

- **GL** (1:1 with `Permission`): `gl.read`, `gl.post`, `gl.revise`, `gl.approve`,
  `gl.void`, `gl.reverse`, `gl.close`, `gl.manageAccounts`, `gl.reopen`.
- **Subledgers:** `ar.read`/`ar.write`, `ap.read`/`ap.write`,
  `payroll.read`/`payroll.write`, `cash.read`/`cash.write`,
  `bankrec.read`/`bankrec.write`, `fixedassets.read`/`fixedassets.write`.
- **Assurance:** `audit.read`, `reports.read`.
- **Admin:** `admin.users`, `admin.firm`, `admin.client`, `admin.fiscal`,
  `admin.postingAccounts`.

Plus a `Permission ↔ gl.* capability` bidirectional mapping:
`CapabilityForPermission(Permission) : string` and
`PermissionForCapability(string) : Permission?`. An `All` set of every capability
(for validation/tests).

## Role presets — `Control/RolePresets.cs` (new)

`Dictionary<LedgerRole, HashSet<string>>` — the umbrella bundle table. `LedgerRole`
gains narrow per-module presets: **ArClerk, ApClerk, PayrollClerk, CashClerk**.

| Role | Capability preset |
|------|-------------------|
| Auditor | every `*.read` (gl.read, ar/ap/payroll/cash/bankrec/fixedassets.read, audit.read, reports.read) — no writes |
| Clerk | every `*.read` + all subledger `.write` (ar/ap/payroll/cash/bankrec/fixedassets.write) — no gl write verbs |
| Approver | every `*.read` + `gl.approve`, `gl.void`, `gl.reverse` |
| Controller | every `*.read` + `gl.post/revise/approve/void/reverse/close/manageAccounts` + all subledger `.write` |
| Admin | everything Controller has + `gl.reopen` + all `admin.*` |
| ArClerk | every `*.read` + `ar.write` |
| ApClerk | every `*.read` + `ap.write` |
| PayrollClerk | every `*.read` + `payroll.write` |
| CashClerk | every `*.read` + `cash.write` + `bankrec.write` |

Helper `CapabilitiesFor(IEnumerable<LedgerRole> roles) : HashSet<string>` unions the
presets — the union of overlapping roles.

**GL-enforcement equivalence invariant:** for each of the five original roles, the
set `{ PermissionForCapability(c) : c in preset, c starts "gl." }` MUST equal that
role's current `RolePermissions` set. A unit test asserts this so enforcement is
provably unchanged. (Narrow clerks hold only `gl.read` → `Permission.Read`, like
Clerk.)

## Membership — `Control/Membership.cs`

```csharp
public sealed class Membership {
    [BsonId] public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ClientId { get; set; }
    public IReadOnlyList<LedgerRole> GrantedRoles { get; set; } = [];   // presets granted (display/provenance)
    public IReadOnlyList<string> Capabilities { get; set; } = [];        // authoritative resolved set
    [BsonElement("Role")] public LedgerRole? LegacyRole { get; set; }    // pre-migration docs only
}
```

- Authority = `Capabilities`. `GrantedRoles` is provenance for display (the endpoint's
  `roles`). A future custom-set grant (Slice D) may leave `GrantedRoles` empty.
- `[BsonIgnoreExtraElements]` on the class; `LegacyRole` maps the old `Role` field so
  pre-migration docs still deserialize.

## ControlStore — `Control/ControlStore.cs`

- `AddMembershipAsync(userId, clientId, params LedgerRole[] roles)` (default
  `[Controller]` to preserve the existing default): sets `GrantedRoles = roles`,
  `Capabilities = RolePresets.CapabilitiesFor(roles)`. Keeps the current
  idempotent create-only behavior (no-op if a membership exists) — an update path
  is Slice D.
- `GetMembershipAsync(userId, clientId)` — on read, if a doc has `LegacyRole` set
  and empty `Capabilities`, backfill in memory: `GrantedRoles = [LegacyRole]`,
  `Capabilities = preset(LegacyRole)` (so pre-migration docs never 403). Backfill
  is read-time only; no write.
- `GetMembersAsync(clientId)` — unchanged shape (returns the memberships).

## Enforcement — `Endpoints/LedgerGateway.cs`

`ResolveAsync` replaces `RolePermissions.Allows(membership.Role, required)` with:
`membership.Capabilities.Contains(Capabilities.CapabilityForPermission(required))`.
`ResolveForPostAsync`'s module path is unchanged (still membership existence +
`ModuleAccess`). Behavior for the five roles is identical by the equivalence
invariant above.

## Endpoint — `Endpoints/CapabilitiesEndpoints.cs` (new)

`GET /clients/{clientId}/me/capabilities` mapped in a new
`MapCapabilitiesEndpoints` under the `clients/{clientId}` group (inherits
`.RequireAuthorization()`; no specific-permission gate — any authenticated member
may read their own). Handler:

1. `actorFactory.Create(user)` → `userId`.
2. `control.GetMembershipAsync(userId, clientId)`. If none → 403 (not a member).
3. Return `CapabilitiesResponse` (below), reading `deploymentAdmin` from the
   `admin=true` claim on the principal.

Mapped from the composition root (`Accounting101.Host/Program.cs`) alongside the
other `Map*Endpoints` calls.

## Contracts — `Accounting101.Ledger.Contracts`

- `CapabilitiesResponse(IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin)`
  (roles as their `LedgerRole` names).
- `AddMemberRequest` — extend to accept one or more role names
  (`IReadOnlyList<string> Roles`, or keep `string Role` + add optional `Roles`);
  parse each via `Enum.TryParse<LedgerRole>`. `MembershipResponse` gains
  `Roles: string[]` and `Capabilities: string[]` (keep it firm-admin-facing).

## Out of scope (later slices)

- Frontend consumption / sidebar filtering / screen write-gating (**Slice B**).
- Admin UI to assign roles/craft custom capability sets + owner-configurable
  visibility range (**Slice D**) — Slice A grants via `ControlStore`/seed/admin
  endpoint only; no member-update endpoint.
- Real server-side enforcement of subledger/admin capabilities (**Slice E**) —
  those capabilities are advisory (UI-facing) this slice; only `gl.*` is enforced
  (via the flipped `LedgerGateway`).
- Real (non-dev) auth / IdP.

## Testing & verification

- **Unit** (`Backend/Accounting101.Ledger.Api.Tests`):
  - `Capabilities` GL↔Permission round-trips; `All` completeness.
  - `RolePresets` equivalence invariant: gl.* of each of the 5 original presets
    maps exactly to that role's `RolePermissions` set. Narrow-clerk presets contain
    `gl.read` only among gl.*. `CapabilitiesFor` unions correctly.
- **Integration** (`CapabilitiesTests.cs`, mirrors `PolicyTests`/`AdminTests`):
  - member gets their resolved capabilities + roles; Auditor vs Controller vs a
    narrow ArClerk differ as expected; multi-preset grant returns the union;
    `deploymentAdmin` reflects the claim; non-member → 403; unknown client → 403/404
    per existing convention.
  - **Regression:** `PolicyTests` and `ModulePostingTests` pass unchanged (GL
    enforcement equivalence).
- Run: `cd Backend && dotnet test Accounting101.Ledger.Api.Tests` (or the solution
  test target). All existing suites stay green.

## Controller finish step (not app code)

Migrate the two durable `.localdev` membership docs (Dev Clerk = Controller, Dev
Approver = Approver) to the new shape (`GrantedRoles` + `Capabilities`) via a
scratchpad script, so the running dev stack resolves capabilities without a
re-seed (re-seeding would churn GUIDs). Read-time backfill also covers this, but an
explicit migration keeps the stored docs canonical. Then smoke-test
`GET /clients/{clientId}/me/capabilities` for both identities.

## Execution

One branch (`feature/nav-slice-a-capabilities`), subagent-driven. Four tasks:
(1) vocabulary + preset map + GL↔Permission mapping + LedgerRole extension;
(2) Membership → capability-backed + ControlStore + admin contract/test updates;
(3) LedgerGateway capability enforcement + PolicyTests/ModulePostingTests green;
(4) `/me/capabilities` endpoint + contracts + CapabilitiesTests.
