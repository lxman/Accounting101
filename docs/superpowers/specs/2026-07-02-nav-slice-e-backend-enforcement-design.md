# Slice E — Backend Per-Module & Admin Enforcement — Design

**Date:** 2026-07-02
**Status:** Approved (design)
**Parent:** `2026-07-01-role-based-nav-rbac-design.md` (umbrella). Sub-project E of
that decomposition — the one row the umbrella deferred. Slices 0, A, B, C, D have
shipped. This slice makes subledger + admin capabilities **real server-side**:
until now they were advisory (UI-facing); only `gl.*` was enforced.

## Goal

The server must **reject** cross-module and unauthorized subledger/admin writes,
not merely hide the controls in the UI. After this slice, a hand-crafted request
(bypassing the UI) that a role's capability set does not permit is refused with
403 — per-module scoping becomes enforced rather than cosmetic. This closes the
RBAC umbrella.

## Background — what exists today

- **GL is already enforced** (Slice A). Every GL/journal endpoint routes through
  `LedgerGateway.ResolveAsync(user, clientId, Permission)` which checks
  `membership.Capabilities.Contains(Capabilities.CapabilityForPermission(required))`.
- **Subledger modules are NOT enforced.** The five module HTTP surfaces
  (`ReceivablesEndpoints`, `PayablesEndpoints`, `PayrollEndpoints`,
  `CashEndpoints`, `ReconciliationEndpoints` — ~40 write endpoints plus reads)
  are declared with `.RequireAuthorization()` only: the caller must be an
  authenticated client member, but **no per-module capability is checked**. An
  AR-only clerk can POST to the Payables API and the server accepts it.
- **The single chokepoint.** Every module operation funnels through
  **`ModuleAccess.AuthorizeAsync`** (`Control/ModuleAccess.cs`). Both planes call it:
  - the **document data plane** — `ScopedDocumentStore.EnterAsync` (line ~296) and
    `NextNumberAsync` (line ~214), reached by create-customer, draft-invoice,
    query-invoices, etc.;
  - the **GL-posting plane** — `LedgerGateway.ResolveForPostAsync`'s module branch
    (line ~53), reached when a module posts to the GL (issue invoice, enter bill).

  Today `AuthorizeAsync` checks *module registered + enabled + owns-namespace + user
  is a member* (`ControlStore.IsMemberAsync`). It never reads the user's
  capabilities. **Adding the per-module capability check here covers all ~40
  endpoints on both planes without touching a single endpoint.**
- **Admin surface.** `AdminEndpoints` (`POST/GET /admin/clients`,
  `PUT /admin/clients/{id}/fiscal-year-end`, `POST/GET /admin/clients/{id}/members`)
  is gated by the `DeploymentAdmin` policy — a trusted `admin=true` token claim,
  above any single client. `MemberEndpoints` (Slice D) already established the
  per-client pattern: **deployment-admin claim OR the per-client `admin.users`
  capability** (`CallerMayManage`).
- **Capability vocabulary** (`Control/Capabilities.cs`): subledger areas
  `ar/ap/payroll/cash/bankrec` each have `.read`/`.write`; admin areas
  `admin.users/firm/client/fiscal/postingAccounts`. Role presets
  (`Control/RolePresets.cs`): every role holds every `*.read`; subledger `.write`
  is held by Clerk/Controller/Admin (all) and by the narrow per-module clerks
  (ArClerk→`ar.write`, etc.); Auditor and Approver hold **no** subledger `.write`.

## Part 1 — Subledger enforcement (the core)

### `Control/ModuleAccess.cs`

- Add `public enum ModuleAccessLevel { Read, Write }`.
- Add `MissingCapability` to `ModuleAccessDecision` (distinct from `NotMember` for
  logging/tests; both map to 403 at the boundary).
- `AuthorizeAsync` gains a `ModuleAccessLevel level` parameter. After the existing
  module registered/enabled/owner checks, it swaps `IsMemberAsync` for
  `GetMembershipAsync` (a member is still required — `null` → `NotMember`), then:
  - resolves the required capability via `Capabilities.CapabilityForModule(caller.Key, level)`;
  - if that capability is non-null and `!membership.Capabilities.Contains(cap)` →
    `MissingCapability`;
  - otherwise `Allowed`.

  Rationale for the null case: an unmapped module key (a future module with no
  subledger area) yields `null` and falls back to membership-only — the current
  behavior — rather than fail-closed on a module the vocabulary doesn't know. The
  five shipping modules all map, so this is a forward-compat escape hatch, not a
  hole in today's surface.

### `Control/Capabilities.cs`

Add `CapabilityForModule(string moduleKey, ModuleAccessLevel level) : string?` — a
small explicit map mirroring how the rest of the code hardcodes module keys:

| Module key | Area | Read cap | Write cap |
|------------|------|----------|-----------|
| `receivables` | ar | `ar.read` | `ar.write` |
| `payables` | ap | `ap.read` | `ap.write` |
| `payroll` | payroll | `payroll.read` | `payroll.write` |
| `cash` | cash | `cash.read` | `cash.write` |
| `reconciliation` | bankrec | `bankrec.read` | `bankrec.write` |
| *(anything else)* | — | `null` | `null` |

### Call sites pass intent

- `ScopedDocumentStore.EnterAsync` gains a `ModuleAccessLevel level` parameter and
  forwards it to `AuthorizeAsync`. Each public method passes its intent:
  - **Read:** `GetAsync`, `QueryAsync`, `CountAsync`.
  - **Write:** `PutAsync`, `DeleteAsync`, `DeactivateAsync`, `CreateAsync`,
    `UpdateAsync`, `FinalizeAsync`, `SupersedeAsync`, `VoidAsync`.
  - `NextNumberAsync` calls `AuthorizeAsync` directly → **Write** (it assigns a
    number during finalize, a mutation).
- `LedgerGateway.ResolveForPostAsync` module branch passes **Write** (a post is a
  write).

### Enforcement behavior (intended, not incidental)

- **Reads: zero regression.** Every role preset holds every `*.read`, so read
  enforcement changes no existing behavior; it is defense-in-depth and makes a
  future narrow-read visibility config (Slice D territory) actually bite.
- **Writes: the point.** Auditor and Approver hold no subledger `.write` → they can
  no longer post through any module server-side (matching the UI, which already
  hides those controls). Narrow clerks are confined to their own module's writes.
  Clerk/Controller/Admin hold all subledger writes → unaffected.
- **Double-checked issue/enter flows** (issue invoice, enter bill) hit both the
  document plane (`Finalize` → Write) and the GL plane (`ResolveForPostAsync` →
  Write); both require the same `*.write`. Consistent and idempotent — defense in
  depth, not a conflict.

## Part 2 — Admin enforcement

Reuse the `MemberEndpoints.CallerMayManage` pattern verbatim in spirit —
**deployment-admin claim OR the per-client `admin.*` capability** — for the
per-client admin endpoints. Split the `AdminEndpoints` route group so control-plane
bootstrap ops keep the hard `DeploymentAdmin` policy while per-client ops move to
plain `.RequireAuthorization()` + an in-handler `deployment-admin OR admin.<area>`
check.

| Endpoint | New gate |
|----------|----------|
| `POST /admin/clients` (create client) | **`DeploymentAdmin` only** — control-plane bootstrap; no per-client context exists to gate on (chicken-and-egg). Documented as intentional. |
| `GET /admin/clients` (list all clients) | **`DeploymentAdmin` only** — spans the whole deployment. |
| `PUT /admin/clients/{id}/fiscal-year-end` | deployment-admin **OR** `admin.fiscal` |
| `POST /admin/clients/{id}/members` (seed) | deployment-admin **OR** `admin.users` (consistent with `MemberEndpoints`) |
| `GET /admin/clients/{id}/members` | deployment-admin **OR** `admin.users` |

Factor the "deployment-admin OR capability" test into a shared helper so
`AdminEndpoints` and `MemberEndpoints` express it once (e.g. an
`AdminAuthorization.MayAsync(user, clientId, capability, actorFactory, control, ct)`
used by both; `MemberEndpoints.CallerMayManage` becomes a call with
`Capabilities.AdminUsers`).

### Admin capabilities with no endpoint (out of scope because nonexistent)

`admin.postingAccounts` (module→GL account mapping — lives in `start.ps1` env
today), `admin.firm`, and `admin.client` (firm/client settings — no dedicated
backend routes; provisioning is the deployment-admin create path) have **no
endpoints to gate**. Explicitly noted as not-applicable this slice rather than
silently skipped. When those endpoints are built, they gate on their capability
via the same shared helper.

## Testing & verification

- **Unit** (`Backend/Accounting101.Ledger.Api.Tests`):
  - `ModuleAccessTests`: with `ar.write` + `receivables`/`Write` → `Allowed`;
    without → `MissingCapability`; `ar.read`/`Read` symmetric; non-member →
    `NotMember`; each module's read/write mapping; unmapped key → membership-only.
  - `Capabilities.CapabilityForModule` table round-trips; unknown key → null.
- **Integration:**
  - `ModulePostingTests` (regression): the posting identity holds the needed
    `*.write` → still green. Add: wrong-module clerk (ArClerk posting via
    `payables`) → 403; Auditor write → 403; Auditor read → OK.
  - Per-module test projects (`Accounting101.Receivables.Tests`,
    `.Payables.Tests`, `.Payroll.Tests`, `.Banking.Cash.Tests`,
    `.Banking.Reconciliation.Tests`): correct clerk writes succeed; wrong-module
    clerk write → 403; auditor read OK / write 403. Document-store **fixtures**
    (`DocumentStoreFixture`, `PayablesDocumentStoreFixture`, …) seed a
    capability-complete role (Controller) so existing tests stay green; negative
    tests seed narrow clerks / auditor.
  - Admin: `SetFiscalYearEnd` succeeds for a non-deployment member holding
    `admin.fiscal`; 403 without it; deployment admin still works. `AddMember`/
    `ListMembers` succeed for `admin.users` holder; 403 without.
- Run: `cd Backend && dotnet test` for the API + control suites; run each module
  test project. UI is unaffected (already gated in Slice C) but its suite stays
  green.

## Out of scope

- Real (non-dev) authentication / IdP — the switcher remains the driver.
- `admin.postingAccounts` / `admin.firm` / `admin.client` endpoints (do not exist).
- The narrow-clerk read-visibility-range configuration (Slice D territory).
- Any UI change — Slice C already gates the write controls; this slice makes the
  server agree.

## Execution

One branch (`feature/nav-slice-e-enforcement`), subagent-driven. Rough task cut:
(1) `Capabilities.CapabilityForModule` + `ModuleAccessLevel`/`MissingCapability` +
`ModuleAccess.AuthorizeAsync` capability check + `ModuleAccessTests`;
(2) thread `level` through `ScopedDocumentStore` (+ `NextNumberAsync`) and
`ResolveForPostAsync`; migrate module fixtures; module-level negative/positive
tests; `ModulePostingTests` regression + cross-module 403;
(3) admin shared helper + `AdminEndpoints` split (fiscal → `admin.fiscal`, members →
`admin.users`; create/list stay deployment-only) + admin tests.
