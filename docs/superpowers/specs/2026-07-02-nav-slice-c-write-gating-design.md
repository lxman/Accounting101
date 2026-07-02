# Slice C — In-Screen Write-Gating — Design

**Date:** 2026-07-02
**Status:** Approved (design)
**Parent:** `2026-07-01-role-based-nav-rbac-design.md` (umbrella). Sub-project C.
Slices 0, A, B shipped. This slice gates the write controls on the built screens
by capability, so the same screen is read-only for a viewer and read/write for an
operator.

## Goal

Hide every write control (create/edit/record/post/approve/void/apply/refund/
reparent) on the built screens unless the acting user holds the governing
capability, and guard the write routes so a direct URL cannot reach an editor the
user can't use.

## Decisions (from brainstorming)

- **Hide, not disable** — write controls the user lacks are removed from the DOM
  (a clean read-only screen for auditors).
- **Route guards now** — the write routes get `canActivate` guards that redirect
  to the area's list when the capability is absent, with a "capabilities loaded
  yet?" gate so the initial fetch doesn't spuriously deny.
- Backend remains the real gate (deferred **Slice E**); this is the UI layer.

## Mechanism

- **`*appCan` structural directive** (`core/capabilities/can.directive.ts`,
  standalone): `*appCan="'ar.write'"` renders content only if the user holds that
  capability; `*appCan="['gl.approve','gl.reverse']"` renders if they hold ANY of
  them. Reactive to `CapabilityService` (a signal `effect` creates/clears the
  embedded view), so controls appear/disappear as capabilities resolve or the
  identity switches.
- **`canWrite(capability, fallback)` guard factory** (`core/capabilities/can.guard.ts`):
  a `CanActivateFn` that waits for `CapabilityService.loaded`, then allows if the
  capability is held, else returns a `UrlTree` to `fallback` (the area's list).
- **`CapabilityService.loaded`** (new signal): true once the first
  `/me/capabilities` response for the current key has resolved (distinguished from
  the loaded-empty case via a sentinel initial value), so the guard blocks only
  during the genuine loading window.
- **Test helper** (`core/capabilities/capability.testing.ts`): a
  `StubCapabilityService` + `provideCapabilities(...caps)` provider factory so the
  many affected component specs can grant capabilities in one line (and construct
  without a real `HttpClient`).

## Capability-per-control mapping

| Area | Controls | Capability |
|------|----------|-----------|
| **Receivables** | New invoice, Edit/Delete/Issue draft, invoice Void, per-payment Void, invoice Save, Record payment (+Save), Record adjustment (+Save; the one button covers credit-note/write-off/apply — all `ar.write`), credit Void, Issue refund (+Save), refund Void, customer Add | `ar.write` |
| **Payables** | New bill, Edit/Delete/Enter draft, bill Void, per-payment Void, bill Save/Discard, Record payment (+Save), Apply credit (+Save), vendor Add | `ap.write` |
| **Payroll** | Record run (+Save), run Void, Record remittance (+Save), remittance Void | `payroll.write` |
| **Journal (GL)** | New entry + Post → `gl.post`; Approve → `gl.approve`; Void → `gl.void` (Approve & Void wrapped **separately**) | per-verb |
| **Chart of Accounts** | New account, per-row Edit, account Save → `gl.manageAccounts`; drag-drop reparent → gate inside `onDrop()` + `[cdkDragDisabled]` | `gl.manageAccounts` |

Not gated: the journal **Validate** button (advisory dry-run, no persistence).
No UI exists for **reverse**/**revise** — `gl.reverse`/`gl.revise` have nothing to
retrofit this slice.

## Write-route guards

Apply `canActivate: [canWrite('<cap>', '<fallback>')]` to each:

| Route | Cap | Fallback |
|-------|-----|----------|
| receivables/invoices/new, invoices/:id/edit | ar.write | /receivables/invoices |
| receivables/payments/new | ar.write | /receivables/payments |
| receivables/credits/new | ar.write | /receivables/credits |
| receivables/refunds/new | ar.write | /receivables/refunds |
| payables/bills/new, bills/:id/edit | ap.write | /payables/bills |
| payables/payments/new | ap.write | /payables/payments |
| payables/credits/new | ap.write | /payables/credits |
| payroll/runs/new | payroll.write | /payroll/runs |
| payroll/remittances/new | payroll.write | /payroll/remittances |
| journal/new | gl.post | /journal |
| accounts/new, accounts/:id/edit | gl.manageAccounts | /accounts |

## Notes on specific controls

- **List "New X" links** are today visually disabled via CSS only
  (`pointer-events-none`/`opacity-50`) when e.g. no customer is selected. Wrap the
  link in `*appCan` (structural) IN ADDITION to keeping the existing customer/
  balance CSS condition — don't conflate the two.
- **COA drag-drop** has no button: set `[cdkDragDisabled]="!caps.has('gl.manageAccounts')"`
  on the draggable rows AND early-return in `onDrop()` if the capability is absent
  (defense in depth — a drop event must not post).
- **entry-detail Approve/Void** live in one conditional block; wrap each button in
  its own `*appCan` so a user with only one capability sees only that action.

## Out of scope

- Backend enforcement of subledger/admin capabilities (**Slice E**) — a direct API
  call still succeeds server-side this slice; the guard/hiding is the UI layer.
- Owner config UI (**Slice D**).
- Gating unbuilt areas (Cash, Bank Rec, Audit, Reports, Admin, Periods, Fixed
  Assets) — no screens yet.
- Reverse/revise controls (no UI).

## Testing & verification

- **Directive spec**: renders content when the capability is held (stub), removes
  it when absent, reacts to a capability change.
- **Guard spec**: allows when held; redirects to fallback when absent; waits for
  `loaded` before deciding.
- **CapabilityService spec**: `loaded` is false before the first response, true
  after (including the no-client → empty case).
- **Area specs**: every affected component spec provides `provideCapabilities(...)`;
  the two DOM-click specs (`credit-list`, `refund-list`) grant `ar.write` so the
  Void button is present; add at least one "hidden without the capability" test per
  area (e.g. receivables list with no `ar.write` → no "New invoice").
- Run: `cd UI/Angular && npx ng test --watch=false`. Full suite green.
- **Smoke (finish)**: with the dev roster, switch to **Dev Auditor** and confirm
  the built screens show no write controls and direct-URL to an editor redirects to
  the list; switch to **Dev AR Clerk** and confirm Receivables has its write
  controls but (once its sidebar is the only writable area) Payables/Payroll do not.

## Execution

One branch (`feature/nav-slice-c-write-gating`), subagent-driven. Five tasks:
(1) `*appCan` directive + `canWrite` guard + `CapabilityService.loaded` + test
helper + specs; (2) Receivables sweep; (3) Payables sweep; (4) Payroll sweep;
(5) GL sweep (Journal per-verb + Chart of Accounts incl. drag-drop). Then a
controller smoke test across the dev roster.
