# Access Control area — design (post-Slice-E RBAC)

Status: APPROVED direction (2026-07-02). Successor to the closed nav-RBAC umbrella
(`2026-07-01-role-based-nav-rbac-design.md`). No code yet — this is the brainstorm output;
a plan follows.

## Problem

The RBAC umbrella (Slices 0–E) built the *mechanism*: an `area.level` capability model, a
per-member capability set stored on `Membership`, single-chokepoint server-side enforcement
(`ModuleAccess.AuthorizeAsync`), a role-filtered sidebar, in-screen write-gating, and a
per-client member editor (Slice D). What it lacks is a *cohesive administrative home* that
brings this together the way an IT admin expects:

1. **No named, editable capability "sets."** Roles are the de-facto sets but they are
   **hardcoded** in `RolePresets.cs` and only usable as grant-time snapshots. An owner can't
   create "Warehouse Clerk" or rename/adjust a bundle without a code change.
2. **No instant propagation in the UI.** Backend enforcement is already per-request instant
   (a revoked user's next request 403s at the chokepoint). But a user *already sitting on a
   page* is not bounced when their access is pulled — the client only refetches capabilities
   when the `clientId`/acting-identity key changes. IT departments expect "revoke → they're
   off the page." (User's durable requirement, 2026-07-02.)

## Approved decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Core entity shape | **Capability SETS + members now; People/Employee directory deferred to Phase 2** (after real auth/IdP exists — don't invent an identity store the IdP will own). |
| 2 | Set semantics | **Live-binding / reference.** A member *references* one or more sets; their capabilities are the **union of the referenced sets' current capabilities, resolved at enforcement time.** There is only ever one copy of a set, so "same name, different perms" is impossible by construction, and a set edit takes effect on each affected member's next request with no re-apply step. |
| 3 | Set scope | **Deployment-wide** to start (one catalog, like today's global presets). Per-client custom sets deferred. |
| 4 | Liveness | **403 self-heal + live route sentinel + gentle idle poll.** No websockets in Phase 1; structured so a SignalR push can replace the poll later. |

### Why live-binding over snapshots (2026-07-02 discussion)

Snapshots (copy caps at assignment, edit → explicit re-apply) were rejected because they *create*
the drift they then have to manage: two members both labeled "ArClerk" could hold different
capabilities depending on when they were assigned. Redefining what a class means is *supposed* to
move everyone in that class — that is the point of a class. Live-binding makes drift impossible
(one definition, resolved live) and removes the re-apply machinery entirely. The one property
snapshots gave us — "a one-line edit shouldn't silently re-privilege many people" — is preserved
not by freezing copies but by a **confirm-on-save that shows the affected member count** plus an
audit-log entry. **No forking:** editing a built-in set persists in place, so one name always maps
to exactly one current definition.

## Grounding facts (verified against current code)

- A member today = `Membership { UserId, ClientId, GrantedRoles: LedgerRole[], Capabilities: string[] }`.
  `Capabilities` is the authority the chokepoint reads; `GrantedRoles` is provenance/display.
  No name/email/person entity anywhere. Identity is the dev-switcher; no auth/IdP yet.
  (`Control/Membership.cs`)
- `ControlStore.Hydrate` already **derives** `Capabilities` at read time for legacy Role-only docs
  (backfill, no write). Live-binding generalizes exactly this move: resolve `Capabilities` from the
  referenced sets at read time. (`Control/ControlStore.cs`)
- The chokepoint reads `membership.Capabilities` fresh every request and never trusts a token claim.
  So if that field is *resolved from sets at read time*, **enforcement code is unchanged** — only how
  the field is populated changes. (`Control/ModuleAccess.cs`, `LedgerGateway`)
- Capability vocabulary is served from `GET /capabilities/catalog`. (`CapabilityCatalogEndpoints.cs`)
- The control DB (`ControlStore`) holds `clients`, `memberships`, `modules` collections in one
  per-deployment database. A new `capabilitySets` collection is the natural home for sets.

## Phase 1 scope (this build)

### A. Backend — editable, live-bound capability sets (deployment-wide)

- **Entity** `CapabilitySet { Id: Guid, Name: string (unique), Description?: string, Capabilities: string[], Builtin: bool }`
  in a new `capabilitySets` collection via `ControlStore`.
- **Seeding.** On startup, upsert (keyed by Name, idempotent) one set per current `RolePresets`
  entry as `Builtin = true` — the five umbrella roles + four narrow clerks become first-class,
  visible, live-bound sets. Built-in sets are **editable** (an owner can adjust "Clerk") but
  **not deletable** (delete → 409). **Persist edits in place** (no forking): re-seed only fills
  *missing* names, never overwrites an owner's edit to an existing built-in.
- **Validation.** Every capability must be in `Capabilities.All` (reuse the 422 pattern in
  `MemberEndpoints.TryParse`). Name unique and non-empty.
- **Referential integrity.** A set that any membership references cannot be deleted or renamed out
  from under it (409, "N members hold this set"). Deletion requires zero referencing members.
- **Endpoints** (`CapabilitySetEndpoints.cs`), gated **deployment-admin only** — sets are global
  infrastructure; per-client `admin.users` governs *assignment*, not set definition (see B).
  - `GET  /capability-sets` → list (built-in + custom)
  - `POST /capability-sets` → create (422 bad cap / dup name)
  - `PUT  /capability-sets/{id}` → rename / redescribe / edit capabilities (409 dup name or
    rename-in-use; response includes `affectedMemberCount` so the UI can confirm blast radius)
  - `DELETE /capability-sets/{id}` → delete (409 if `Builtin` or in use)

### B. Backend — resolve capabilities from referenced sets (the core change)

- **Membership gains `GrantedSetIds: Guid[]`** (the authoritative grant going forward). The inline
  `Capabilities` field becomes a **read-time-resolved cache**, not stored authority — populated by
  resolving the union of the referenced sets' current capabilities (generalizing today's `Hydrate`).
- **Resolution + caching.** Sets are deployment-wide and few; `ControlStore` keeps an in-memory set
  catalog (invalidated on any set create/edit/delete) so resolving a membership's capabilities is a
  cheap union, not a DB round-trip per authorize. The chokepoint keeps reading
  `membership.Capabilities` — unchanged — but that value is now the live union.
- **Assignment surface** (extends Slice D's `MemberEndpoints`, gated per-client `admin.users`,
  last-admin guard preserved):
  - Assigning a member = setting `GrantedSetIds`. `PUT /clients/{clientId}/members/{userId}` takes
    a set-id list; resolved capabilities are computed, never client-supplied.
  - `AddClientMemberRequest` / `SetMemberRequest` shift from `(Roles, Capabilities)` to
    `(SetIds)`. (Roles remain expressible because each role is a built-in set of the same name.)
- **Migration / back-compat.** Existing memberships carry `GrantedRoles` + stored `Capabilities`
  but no `GrantedSetIds`. On read: if `GrantedSetIds` is empty, map `GrantedRoles` → the built-in
  sets of the same name (they seed with matching names) to derive references; if there are neither,
  fall back to the stored `Capabilities` (legacy custom grant) untouched. A one-time backfill writes
  `GrantedSetIds` from `GrantedRoles`. Last-admin guard now tests "still references a set that grants
  `admin.users`."
- **Power-user / ad-hoc caps.** Everything flows through named sets — a one-off need = make a set
  (sets are cheap). Slice D's "edit this member's raw capability list" becomes "edit the set" or
  "assign a different set." This unifies two concepts into one. *Deferred, not built now:* an
  optional per-member additive override cap list, if a real need for person-level exceptions
  appears; Phase 1 stays pure (authority = union of referenced sets).

### C. Frontend — Access Control admin area

Under the existing **Administration** nav section (`nav.ts`), gated by `admin` area:

- **Access Control ▸ Capability Sets** (`/admin/access/sets`) — list built-in + custom sets;
  create/edit/delete with a capability picker (vocabulary from `GET /capabilities/catalog`, grouped
  by area). Saving an edit shows a **confirm dialog with the affected-member count** (from the PUT
  response) before it takes effect — the deliberate-blast-radius guard that replaces re-apply.
- **Access Control ▸ Members** (`/admin/access/members`, or fold into `/admin/users`) — per-client
  member list; assign membership by **picking one or more sets** (dropdown of set names). Reuse
  Slice D's `MemberService`/`member-list`; the `member-editor` becomes a set-picker rather than a
  raw capability grid.
- New `CapabilitySetService` (Angular): set CRUD against the new endpoints.

*Phase 2 (deferred):* a People/Employee directory (name/email/person) with a per-person link into
Access Control — blocked on real auth/IdP so we don't build a throwaway identity store.

### D. Frontend — instant propagation (liveness)

Live-binding makes the **backend** side automatic: the moment a set is saved, every affected member's
next request re-resolves and is allowed/blocked accordingly — no re-apply, no snapshot lag. The
remaining gap is the UI bouncing a user who is **idle** on a page. Three transport-agnostic pieces,
no websockets:

1. **403 self-heal interceptor.** On ANY `403`, `CapabilityService` refetches `/me/capabilities`,
   so the user's next *action* instantly reflects the change (gates/sidebar already reactive on the
   caps signal — Slice B/C).
2. **Live route sentinel.** A root-level `effect()` mapping the active route → its required
   capability and **redirecting off the page the moment that capability disappears** from the caps
   signal (closing Slice C's navigation-only `canWrite` gap).
3. **Gentle idle poll.** `CapabilityService` refetches `/me/capabilities` on a ~15s timer while a
   client is selected (paused when the tab is hidden). Bounds pure-idle latency; drop-in replaceable
   by a future SignalR push.

*Dev-harness note:* full page reload resets in-memory identity to default Controller (Slice C),
masking live redirect in manual testing — sentinel is unit-tested; manual checks use the "Acting as"
switcher without reload.

## Explicitly out of scope (Phase 1)

- People/Employee directory & person entity (Phase 2, blocked on IdP).
- Per-client custom sets (deployment-wide only for now).
- Per-member additive override capabilities (all authority via referenced sets for now).
- SignalR / websocket real-time push (poll is the push-ready placeholder).

## Enforcement invariant (must be preserved)

Capabilities are resolved from the referenced sets at read time and the server authorizes every
request through `ModuleAccess.AuthorizeAsync` reading `membership.Capabilities` (now the live union)
— no set, assignment, cache, or UI feature may introduce a capability source that bypasses the
chokepoint or is sourced from a token claim. Tests must assert: (a) editing a set changes an
already-assigned member's resolved capabilities on the very next resolution (live-binding proof);
(b) two members referencing the same set always resolve identical capabilities (no-drift proof);
(c) a set in use cannot be deleted/renamed out from under members (referential guard);
(d) the last-admin guard holds under set references.

## Decomposition (each its own plan → build slice)

- **AC-1** Backend sets: `CapabilitySet` entity + `ControlStore` CRUD + startup seeding from presets
  (persist-in-place) + in-memory catalog cache + `CapabilitySetEndpoints` + validation/referential
  guards + tests.
- **AC-2** Live-bound resolution + assignment: `Membership.GrantedSetIds`, read-time union
  resolution, `GrantedRoles`→sets migration/backfill, member set-assignment routes, last-admin guard
  under references, no-drift/live-binding/referential tests.
- **AC-3** Frontend Access Control area: `CapabilitySetService`, Sets screen (CRUD + capability
  picker + affected-count confirm), Members screen (set-picker assignment, reuse Slice D), nav
  entries under Administration.
- **AC-4** Frontend liveness: 403 self-heal interceptor, live route sentinel, idle poll; unit tests;
  push-ready structure.

Recommended sequence AC-1 → AC-2 → AC-3 → AC-4 (backend first so the UI builds on real endpoints).
AC-4 is independently valuable and could ship first if instant-propagation is the priority.
