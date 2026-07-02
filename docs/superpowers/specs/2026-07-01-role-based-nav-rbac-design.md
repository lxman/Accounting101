# Role-Based Navigation & Capability Subsystem — Umbrella Design

**Date:** 2026-07-01
**Status:** Approved (north-star umbrella design)
**Scope:** North-star, full product. This is an umbrella spec covering a
five-sub-project subsystem. It pins the destination; each sub-project gets its
own spec → plan → build cycle later. No build slice is committed by this
document.

## Goal

Replace the flat 13-item sidebar with a grouped, north-star information
architecture whose visible items are **filtered per role**, driven for now by
the temporary "Acting as" identity switcher (upper right). Role visibility is
**capability-based** and resolves to a **union** when a person holds multiple
roles. The same capability model that filters the sidebar also gates
**write controls inside each screen**, so (e.g.) an AR clerk sees Receivables
read/write while an auditor sees the same Receivables screen read-only. The
capability map is **backend-owned** (the only persistent store) and the
read-visibility range is **configurable by the owner**.

## Background — what exists today

- **Sidebar:** `UI/Angular/src/app/layout/nav.ts` is a flat `NavItem[]` of 13
  items, rendered in `shell.ts` with a longest-prefix active-path computed.
  Everyone sees every item; there is no role filtering.
- **Identity switcher:** `DevIdentityService` exposes two dev identities —
  Dev Clerk (role claim `Controller`) and Dev Approver (role `Approver` +
  `admin`). Each identity carries **claims**, including `{type:'role', value}`.
  This is the "temporary role switcher" the sidebar will read from.
- **Backend role/permission matrix** (`Backend/Accounting101.Ledger.Api/Control/Authorization.cs`)
  is the current single source of truth for GL/chart authority:
  - Roles: `Auditor, Clerk, Approver, Controller, Admin` (no "CPA" — overlaps
    like "CPA + Auditor" are expressed as multiple roles, not a distinct role).
  - Permissions: `Read, Post, Revise, Approve, Void, Reverse, Close,
    ManageAccounts, Reopen`.
  - Map: Auditor→{Read}; Clerk→{Read}; Approver→{Read,Approve,Void,Reverse};
    Controller→{Read,Post,Revise,Approve,Void,Reverse,Close,ManageAccounts};
    Admin→Controller+{Reopen}.
- **Key wrinkle:** the backend `Permission` enum gates only the raw GL/chart
  endpoints. It has **no "use a module" capability** — module access is gated
  separately by module credentials. That is why `Clerk` is `Read`-only here yet
  clerks are the primary module operators (they post *through* modules, which
  post to the GL on their behalf). The UI capability set must therefore extend
  beyond the backend `Permission` enum to cover subledger + admin areas.

## North-star sidebar inventory (grouped)

Five sections. Items marked *(future)* are north-star destinations that route to
placeholders until built. Vetted for accounting terminology by an opinionated-CPA
review (Period Close naming, Bank Rec nesting, Subledgers section name, Budgets /
Fixed Assets roadmap gaps all incorporated).

```
Overview
  Dashboard

General Ledger
  Journal                    (entries + post)
  Approvals                  (journal approval queue)
  Chart of Accounts
  Trial Balance
  Financial Statements       (Balance Sheet, Income Statement; later Cash Flow, Equity)
  Period Close               (month-end + year-end)

Subledgers
  Receivables
  Payables
  Payroll
  Cash & Banking
    ↳ Bank Reconciliation    (nested under Cash & Banking, not a peer)
  Fixed Assets               (future)

Assurance
  Audit                      → Audit Trail · Verify Integrity · Subledger Reconciliations
  Reports                    (future)
    ↳ Budgets                (future — budget-vs-actual)

Administration
  Users & Roles
  Firm                       (firm settings — today the header "Edit Firm" button)
  Client                     (client settings — today the header "Edit Client" button)
  Fiscal settings            (fiscal year-end config)
  Posting accounts           (module → GL account mapping; today lives in start.ps1 env)
```

Built today: Dashboard, Journal, Approvals, Chart of Accounts, Trial Balance,
Financial Statements, Receivables, Payables, Payroll (pending merge).
Not yet built (placeholder): Period Close, Cash & Banking, Bank Reconciliation,
Fixed Assets, Audit, Reports, Budgets, and the entire Administration group.

This app is **multi-client firm software** (firm → many clients; the shell has a
client selector and firm/client edit affordances), so "Firm" and "Client" as
distinct settings destinations are correct.

## Capability model

Capabilities are `area.level` pairs. Every area has `read` and, where writable,
`write`. The GL keeps its finer-grained verbs because they already exist on the
backend.

- **GL:** `gl.read`, `gl.post`, `gl.approve`, `gl.close`, `gl.manageAccounts`,
  `gl.reopen` — mirror the backend `Permission` enum verbatim.
- **Subledgers:** `ar.read`/`ar.write`, `ap.read`/`ap.write`,
  `payroll.read`/`payroll.write`, `cash.read`/`cash.write`,
  `bankrec.read`/`bankrec.write`, `fixedassets.read`/`fixedassets.write`.
- **Assurance:** `audit.read`, `reports.read`.
- **Admin:** `admin.users`, `admin.firm`, `admin.client`, `admin.fiscal`,
  `admin.postingAccounts`.

### The two rules (one model, two layers)

1. **Sidebar visibility:** a nav link renders if the acting user holds *any*
   capability for the link's area (Cash & Banking shows for `cash.*` OR
   `bankrec.*`).
2. **In-screen write-gating:** a screen renders its write controls
   (New/Edit/Record/Void) only if the user holds that area's `write` (or the
   specific GL verb). Read-only visitors get the same screen without the
   buttons.

### Roles are capability bundles

The switcher/identity carries a **capability set** (not a single role claim) so
both broad and narrow roles are expressible. Default bundles, mirroring +
extending the backend matrix:

| Role | Capabilities |
|------|--------------|
| **Auditor** | every `*.read` + `audit.read` + `reports.read`; **no writes** |
| **Approver** | every `*.read` + `gl.approve` + GL void/reverse |
| **Controller** | every `*.read` + `gl.post/revise/approve/void/reverse/close/manageAccounts` + **all** subledger `.write` |
| **Admin** | everything Controller has + `gl.reopen` + all `admin.*` |
| **AR Clerk** | `ar.read`+`ar.write` + read context; **not** `ap.write`, audit, or admin |
| **AP Clerk** | `ap.read`+`ap.write` + read context (symmetric) |
| *(Payroll Clerk, Cash Clerk, …)* | same pattern — one module's `.write` |

### Union semantics (overlapping roles)

A person holding multiple roles gets the **set-union** of their bundles:
*Approver + Auditor* merges both; *AR Clerk + AP Clerk* can write both
subledgers. This is the overlapping-roles requirement and it is free with a
capability set.

**Worked example (the driving requirement):** an AR Clerk and an Auditor both
hold `ar.read`, so both see the Receivables link. Only the AR Clerk holds
`ar.write`, so only they see New Invoice / Record Payment. Same destination,
different screen — resolved by one model.

### Configurable read-visibility range (owner-controlled)

The read-scope of a **narrow** clerk (does an AR Clerk see read-only links for
areas they don't operate — Payables, Trial Balance, Statements — or only their
own module?) is **not hardcoded**. The owner/admin configures the visibility
range per the firm's preference. Defaults ship with the backend foundation;
the configuration UI and its persistence ship with the Admin sub-project.

### Ownership: backend-owned map

The capability vocabulary, role→capability default bundles, per-member
capability sets, and the visibility-range configuration all live in the
**backend** (the only persistent store). The frontend fetches the acting user's
**resolved (unioned) capability set** for the active client via an endpoint and
uses it to drive both the sidebar filter and screen write-gating.

## Decomposition (5 sub-projects + 1 deferred)

Each row is its own spec → plan → build cycle.

| # | Sub-project | Delivers | Depends on |
|---|-------------|----------|------------|
| **0** | **Grouped sidebar (visual only)** | Flat nav → the grouped north-star tree (sections, nesting Bank Rec under Cash & Banking, placeholders). Everyone still sees everything. Permanent structure; only the *filter* is added later. | — |
| **A** | **Backend capability foundation** | Capability vocabulary (GL + subledger + admin), role→capability default bundles incl. narrow per-module clerks, per-member capability sets, and an endpoint returning the acting user's resolved capability set for a client. Default visibility range. | — |
| **B** | **Role-based sidebar** | Frontend capability service (fetch from A), switcher carrying capability sets, sidebar filters links by capability. The headline "different nav per role" feature. | 0, A |
| **C** | **In-screen write-gating** | Sweep every GL/subledger screen to render write controls off `*.write`; auditors get read-only screens. | A |
| **D** | **Admin: Users & Roles + visibility config** | Owner-facing screen to assign roles/capabilities per member and set the configurable read-visibility range. | A |
| **E** | *(deferred)* **Backend per-module enforcement** | Server rejects cross-module writes, not just hidden UI. Makes per-module scoping real rather than cosmetic. | A |

**Recommended sequence:** 0 → A → B → C → D, with E deferred until the system is
more than a dev-switcher demo. Slice 0 is the smallest safe first step: the
regrouping is permanent and low-risk, and when B lands only the filter changes.

## Out of scope (this umbrella)

- Committing to a build slice — this document only pins the north-star scope.
- Backend per-module *enforcement* (Sub-project E, explicitly deferred). Until
  E ships, the UI is honest but the server would not reject a hand-crafted
  cross-module write; "done" on B/C/D must not overclaim server enforcement.
- Building the placeholder destinations themselves (Period Close, Cash &
  Banking, Bank Rec, Fixed Assets, Audit, Reports, Budgets). The sidebar lists
  them as north-star; their screens are separate module efforts.
- Real (non-dev) authentication and identity provisioning. The switcher remains
  the driver "for now."

## Open questions / deferred decisions

- Exact endpoint shape for the resolved capability set (new `…/me/capabilities`
  vs. folding into an existing membership/"me" response) — decided in Sub-project A's spec.
- Whether narrow per-module clerk roles (AR Clerk, AP Clerk, …) are seeded as
  named roles or composed purely from capabilities — decided in A.
- Where Budgets ultimately lives (under Reports vs. near Financial Statements) —
  revisit when Reports is built.
- Per-client theming/format already exists; whether visibility-range config
  reuses that per-client settings surface — decided in D.
```
