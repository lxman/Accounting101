# RBAC Hardening — design

Status: APPROVED (2026-07-03). Follow-up to the closed RBAC Access-Control area (AC-1→AC-4). Turns
the model from "expresses RBAC" into "resists the everyone's-an-admin drift." Captured origin from
[[accounting101-rbac-admin-sprawl-audit-gap]] (design backlog) — all four items now in scope.

## Problem

Two gaps surfaced after the Access-Control area shipped:
1. **Escalation leak.** A per-client `admin.users` holder can assign ANY existing set — including the
   full built-in **Admin** set — to anyone, including themselves. Nothing checks that a grantor can
   only grant what they already hold. `admin.users` is effectively a one-hop path to full admin.
2. **The control plane is unaudited.** Membership/set mutations are plain in-place Mongo writes: no
   actor, no timestamp, no before/after, no history. The ledger's tamper-evident hash-chain covers
   journal postings only, not access-control decisions.

Plus the temptation the user named directly: shops default everyone to admin because granting the
right narrow thing is more friction than granting Admin.

## Approved decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | No-self-escalation reach | **All grant paths, 422.** Enforce on the set-assignment route, the legacy raw-cap routes, and the deployment control-plane AddMember. Deployment admins exempt. |
| 2 | God-set gating | **Explicit `Restricted` flag** on `CapabilitySet`. A restricted set is assignable to a member only by a deployment admin (else 403). Built-in Admin defaults restricted; toggleable in the set editor. |
| 3 | Least-privilege defaults | **Seed narrow admin built-in sets** (User Admin, Fiscal Admin, Posting-Accounts Admin) so narrow admin grants are one-click and the full Admin set is the deliberate exception. |
| 4 | Audit reach | **Append-only log + query endpoint** now; UI viewer deferred. Capture every control-plane mutation. |

## A. No-self-escalation (item #1)

A shared authorization check on every path that grants capabilities to a member:

- **Paths:** `PUT /clients/{clientId}/members/{userId}/sets` (`MemberEndpoints.AssignSets` — checks the
  union of the assigned sets' capabilities); `POST` + `PUT /clients/{clientId}/members`
  (`MemberEndpoints.AddMember`/`SetMember` — checks `request.Capabilities`); and
  `POST /admin/clients/{clientId}/members` (`AdminEndpoints.AddMember` — checks the granted role's
  expanded preset capabilities).
- **Rule:** if the caller is NOT a deployment admin (`admin=true` claim), the set of capabilities
  being granted must be a subset of the caller's own resolved capabilities on that client
  (`GetMembershipAsync(callerUserId, clientId).Capabilities`). Any capability in the grant that the
  caller does not hold → **422** with a ProblemDetails naming the first offending capability
  ("Cannot grant 'gl.reopen' — you do not hold it.").
- **Deployment admins are exempt** (they bootstrap the system). The caller's user id comes from
  `IActorFactory.Create(user)` as elsewhere.
- **Consequence:** a member can never grant — or self-grant — a capability they lack, so `admin.users`
  confines its holder to propagating exactly their own authority.

Ordering in the handlers: existing gate (`CallerMayManage`) → existing validation (set/member
existence) → **no-escalation check** → **restricted-set check (B)** → last-admin guard → mutate →
**audit append (C)**.

## B. Restricted sets + narrow admin built-ins (items #2, #3)

### `Restricted` flag

- `CapabilitySet` gains `bool Restricted`. A **restricted** set may be *assigned to a member* only by
  a deployment admin; a per-client `admin.users` holder attempting it gets **403** (deliberately
  distinct from #1's 422 — 403 means "wrong tier for this set," 422 means "these caps exceed yours").
- Seeding: the built-in **Admin** set seeds `Restricted = true`; every other built-in `false`.
  Persist-in-place semantics unchanged (re-seed never overwrites an owner's edit).
- The flag flows through `CreateCapabilitySetRequest`/`UpdateCapabilitySetRequest`/`CapabilitySetResponse`
  and gets a checkbox in the AC-3 set editor (already deployment-admin-only). Toggling it is a set edit
  and is therefore audited (C).
- This stops even a legitimate full client-admin from delegating full Admin: minting a full admin must
  climb to the deployment tier. (Under #1 alone a full client-admin could still delegate Admin; the
  Restricted flag is what closes that.)

### Narrow admin built-in sets (item #3)

Seed three additional built-in sets (`Builtin = true`, `Restricted = false`), so granting a *single*
admin power is one click rather than a hand-assembled custom set:

- **User Admin** — `{ admin.users, gl.read }`
- **Fiscal Admin** — `{ admin.fiscal, gl.read }`
- **Posting-Accounts Admin** — `{ admin.postingAccounts, gl.read }`

(`admin.firm`/`admin.client` are deployment-level and stay Admin-only.) These are delegable: under #1
a client-admin holding `admin.users` can grant User Admin (their caps ⊇ the set's caps). The full
**Admin** set stays restricted, so it becomes the deliberate exception, not the default reach.

Seeding note: `SeedBuiltinCapabilitySetsAsync` currently seeds one set per `LedgerRole`. It gains a
second pass that seeds these named non-role built-ins (idempotent, persist-in-place, same as the role
sets). The narrow sets are addressed by name, so re-seeding is safe.

## C. Control-plane audit trail (item #4)

### Store

- New control-DB collection **`adminAudit`** and an **`AdminAuditStore`** exposing exactly two
  methods: `AppendAsync(AdminAuditEntry)` and `QueryAsync(AdminAuditFilter)`. **No update or delete
  method exists** — the log is append-only by construction; the application cannot rewrite it.
- `AdminAuditEntry`:
  - `Id: Guid`
  - `Timestamp: DateTime` (UTC, server-stamped)
  - `ActorUserId: Guid`, `ActorIsDeploymentAdmin: bool`
  - `Action: string` — one of `MemberAdded`, `MemberRemoved`, `MemberSetsAssigned`,
    `MemberCapabilitiesSet`, `SetCreated`, `SetUpdated`, `SetDeleted`
  - `ClientId: Guid?`, `TargetUserId: Guid?`, `TargetSetId: Guid?`
  - `Before: AuditState?`, `After: AuditState?` — a small typed snapshot: for member changes
    `{ SetIds: Guid[], Capabilities: string[] }`; for set changes `{ Name, Restricted, Capabilities }`.
    Null where not applicable (e.g. `Before` on a create, `After` on a delete).

### Wiring

Every control-plane mutation appends an entry after it succeeds, from the endpoint (which holds the
actor). The before-state is read prior to the mutation (most handlers already read it for their
guards):

- `MemberEndpoints.AddMember` → `MemberAdded`; `SetMember`/`AssignSets` → `MemberCapabilitiesSet` /
  `MemberSetsAssigned`; `RemoveMember` → `MemberRemoved`.
- `AdminEndpoints.AddMember` → `MemberAdded`.
- `CapabilitySetEndpoints.Create` → `SetCreated`; `Update` → `SetUpdated` (before→after captures a
  Restricted toggle or capability change); `Delete` → `SetDeleted`.

### Query

- **`GET /admin/audit`** — gated deployment-admin (`AdminEndpoints.Policy`). Query params:
  `clientId?`, `actorUserId?`, `targetUserId?`, `limit` (default 100). Returns entries newest-first as
  `AdminAuditEntryResponse[]`.
- Deployment-admin-only for now (the log is cross-client and sensitive). A per-client admin view and a
  UI viewer screen are deferred to a later slice.

## Enforcement invariant (must hold)

No member-grant path may increase a non-deployment-admin caller's reach beyond their own resolved
capabilities (#1), no non-deployment-admin may assign a restricted set (#2), and every control-plane
mutation leaves an append-only audit entry that names the actor and the before→after (#4). Tests must
assert: (a) a client-admin cannot grant a capability they lack (422) via each path; (b) a client-admin
cannot assign the restricted Admin set (403) but a deployment admin can; (c) the narrow admin built-ins
exist and are non-restricted and delegable; (d) each mutation writes exactly one audit entry with the
correct actor/action/before→after; (e) the audit store exposes no mutation of existing entries.

## Out of scope (this slice)

- Audit-log UI viewer screen (query endpoint only for now).
- Per-client audit visibility (deployment-admin-only query for now).
- Decomposing the existing Admin set's contents (it stays the full god-set, now restricted).
- Retention/rotation of the audit log (append-only, unbounded for now).

## Decomposition (→ implementation plan)

Mostly backend, one small frontend touch:
- **H-1** No-self-escalation check across the three grant paths (+ 422 tests per path).
- **H-2** `Restricted` flag on `CapabilitySet` + assignment enforcement (403) + set create/edit API +
  editor checkbox.
- **H-3** Seed the three narrow admin built-in sets.
- **H-4** `AdminAuditStore` (append-only) + `AdminAuditEntry` + wiring every control-plane mutation.
- **H-5** `GET /admin/audit` query endpoint (deployment-admin gated, filterable).
