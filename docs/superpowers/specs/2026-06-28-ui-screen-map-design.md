# Accounting 101 — UI Screen Map & Inventory — Design

**Date:** 2026-06-28
**Status:** In review (screen inventory + design system; precedes slice sequencing)

## Purpose

The complete screen inventory for the Accounting 101 SPA, grounded in the **real
backend API as it exists today**, updated for everything shipped recently (Bank
Reconciliation, CSV/OFX import, list pagination) and for the look-and-feel +
structural decisions settled in the 2026-06-28 design session. Each screen lists
its purpose, a rough wireframe, the backing endpoints, and whether it is
**Backed** (existing API), **Needs-BE** (requires new backend), or **Partial**.
Once agreed, this map is sequenced into build slices.

Supersedes the screen map in `UI/UI-UX-provisioning-plan.md` (which predates the
Reconciliation/Import modules and the design system below).

## Stack & conventions (decided)

- **Angular 22** — standalone components, signals + the modern services
  architecture, typed reactive forms, the newest everything. Component library
  (Angular Material vs Bootstrap) **still open** — decided before the first build
  slice.
- Static build → Azure Static Web Apps; the API is `api.accounting-101.com`
  (never navigated). JWT bearer on every call.
- **Auth is greenfield** (Entra ID + a user-mapping store don't exist yet). The
  accounting screens build against a **dev-token auth stub** (the same dev tokens
  the tests use); real Entra is wired in its own slice.

## Design system (decided 2026-06-28)

**Palette** — from `Accounting101Palette.pdf`, the default preset "Accounting 101":

| Token role | Hex | Use |
|---|---|---|
| `--teal` | `#24CCAB` | primary / positive accents, active nav |
| `--blue` | `#1D4DE8` | **buttons** (solid, no gradient) + links/interactive |
| `--slate` | `#36413E` | ink / dark surfaces / sidebar |
| `--cream` | `#F3E3B3` | warm highlight (e.g. the net-income KPI) |
| `--lavender` | `#BFACC8` | muted / pending-state badges |

- The palette lives in **CSS custom properties**; Light and Dark are two token
  sets. **Light is the default.**
- **Theme switch: Light / Dark / Follow-OS** (`prefers-color-scheme`), stored as a
  user preference.
- **Palette presets + per-client palette:** the palette is one of several
  selectable **presets** (Accounting 101 is the default). A preset is
  **assignable per client**, so switching clients can shift the accent — a
  "which client am I in" cue. Stored in the client's display settings; rendered
  by swapping the CSS-variable token set. (Infra-cheap; the preset *picker* UI is
  a Settings screen — see area M.)
- Buttons are **solid blue**; negatives default to **accounting parentheses**;
  money columns are **decimal-aligned** (see below).

**Number formatting — the Format Profile (per client):** one stored config,
applied everywhere (screens **and** PDF reports). Default values in brackets.

```
FormatProfile (per client)
  negativeStyle:    parens [default] | minus | red | trailing
  decimals:         2 [default] | 0
  scale:            none [default] | thousands | millions | auto
  thousandsSep:     true [default]
  currencySymbol:   firstAndTotal [default] | every | none
  zeroDisplay:      0.00 [default] | dash
  dateFormat:       ISO/locale [default]
  accountCodeShown: true [default]
```

- Architecture (honors the standing rule *server owns numbers, client owns
  formatting*): the API always returns raw `decimal` + ISO currency; the **Format
  Profile is the single source of truth**, stored server-side per client, and
  consumed by **two formatters sharing its schema** — a TypeScript formatter in
  the SPA and a C# formatter in the PDF generator — kept in lockstep by a shared
  spec + a golden-file test asserting identical output. Change the profile →
  every screen and report reflows.
- Money columns: right-aligned, tabular numerals, fixed decimals so points stack;
  subtotals single-ruled, grand totals double-ruled (accounting convention).

## Cross-cutting patterns (apply to every screen)

- **Paged tables** consume the `PagedResponse<T> { items, total, skip, limit }`
  envelope shipped recently — server-side paging, "Page N of M", page-size,
  `order=asc|desc`, and `includeVoided` where supported. Use the echoed effective
  `skip`/`limit` for page math.
- **Maker-checker is visible.** Posting a journal entry or a module document
  lands **PendingApproval**; a distinct approver approves it (SoD). Show pending
  state, an approval queue, and creator/approver stamps.
- **Money** is USD, formatted by the Format Profile; aligned decimals.
- **Lifecycle actions** per state: void (withdraw if pending / reverse if posted),
  revise (supersede), reverse (closed-period-safe).
- **Period-aware:** posting into a closed period is rejected; surface the engine's
  409/422 inline and offer the reverse-into-open-period path.
- **Empty/loading/error** states on every data view; the **client switcher** in
  the shell scopes every call to `/clients/{clientId}/…`.

---

## A. Global shell

**A1. App shell / nav + top bar** — *Backed (nav); Needs-BE (firm/client edit, my-clients)*
The persistent frame: a **global top bar** and a left sidebar (progressive reveal).
```
┌─────────────────────────────────────────────────────────────────────┐
│ [ Acme Corp ▼ ]              Edit Firm   Edit Client   Jordan ▼ (out) │
├──────────┬──────────────────────────────────────────────────────────┤
│ Dashboard│                                                           │
│ Journal  │            << routed screen content >>                    │
│ Accounts │     (every nav selector is scoped to the selected client) │
│ …        │                                                           │
└──────────┴──────────────────────────────────────────────────────────┘
```
- **Client switcher** (upper-left) sets the active client; all nav scoped to it.
  → `GET` the user's clients [today `GET /admin/clients` is all-clients/admin-only;
  a per-user "my clients" list is Needs-BE].
- **Global top-bar actions: Edit Firm** (business profile, area B2) and **Edit
  Client** (selected client's metadata, B3) — reachable any time; open the B2/B3
  forms in edit mode.
- Progressive reveal (Setup → Clients → Accounting/Subledgers → Admin) per role.

---

## B. Front door & entities — *Needs-BE (auth + profile backend is greenfield)*

**B1. Landing + Login** — *Needs-BE* — MSAL "Sign in with Microsoft" → Entra JWT
wiring + a `user_mappings` store (EntraOid → InternalUserId) consulted by
`LedgerGateway`.

**B2. Firm (business) profile** — *Needs-BE* — name, address, phone (the firm
itself). New `BusinessProfile` collection + CRUD. Serves first-run setup **and**
the top-bar **Edit Firm**.

**B3. Client profile** — *Partial* — name, address, **fiscal year-end**, contact.
`POST /admin/clients` + `PUT /admin/clients/{id}/fiscal-year-end` exist;
address/contact metadata + COA seeding are Needs-BE; `POST /onboarding` seeds
opening balances. Serves first-run setup **and** the top-bar **Edit Client**.

> Because Edit Firm/Edit Client are permanent top-bar fixtures (not just a
> wizard), their CRUD is worth building earlier than "front door last."

---

## C. Dashboard — *Backed*
Quick actions + at-a-glance (cash, A/R, A/P, net income MTD) + a pending-approvals
badge. Numbers from `GET /trial-balance` / `/statements/*`; pending count from
`GET /entries?posting=PendingApproval&limit=1` (read `total`).

## D. Journal — *Backed*
- **D1 Post entry** — balanced Dr/Cr grid, dimensions, effective date, memo;
  **Validate (dry-run)** then post (PendingApproval). `GET /accounts`,
  `POST /entries/validate`, `POST /entries`.
- **D2 Entry list** — paged/filterable. `GET /entries` (`?posting/account/reference`).
- **D3 Pending-approval queue** — maker-checker worklist; approve/void; shows
  creator. `GET /entries?posting=PendingApproval`, `…/approve`, `…/void`.
- **D4 Entry detail** — lines, audit stamp, source back-link, state actions
  (approve/void/revise/reverse). `GET /entries/{id}`, `GET /audit/{id}`.

## E. Chart of Accounts — *Backed*
- **E1 list/tree** by type with balances. `GET /accounts`, `…/balance`.
- **E2 account editor** — number, name, type, normal side, RE flag, cash-flow
  activity + running balance. `GET/PUT /accounts/{id}`.

## F. Trial Balance — *Backed* — as-of, Dr/Cr foot equal, drill to entries.
`GET /trial-balance?asOf=`.

## G. Financial Statements — *Backed*
**G1 Balance sheet** (`/statements/balance-sheet`), **G2 Income statement**
(`/statements/income-statement`), **G3 Cash flow** (`/statements/cash-flow`) —
each a rendered statement + as-of/period selector + Export (PDF/CSV, area N).

## H. Periods — *Backed*
**H1 Close month** (`/periods/close`), **H2 Close year** (`/periods/close-year`,
shows closing entry → RE), **H3 Reopen** (`/periods/reopen`, step-up gated). The
FY-end + pending-blocker guards surface as inline 409s.

## I. Subledgers (four modules) — *Backed*
Each: a customer/vendor/run list, a paged document list, a record form, and a
document detail with void/lifecycle (all post PendingApproval via module identity).
- **I-AR Receivables** — customers; invoices (draft→issue→void) + payments
  (alloc) + credits (credit-apps/write-offs/credit-notes/refunds) + voids.
- **I-AP Payables** — vendors; bills (draft→issue→void) + bill-payments + voids.
- **I-PR Payroll** — payroll-runs + tax-remittances (record + void).
- **I-CA Cash** — disbursements + deposits (record + void).

## J. Bank Reconciliation — *Backed (newer than the old blueprint)*
- **J1 Bank statements list** — `GET /bank-statements?cashAccountId=` (paged),
  `POST /bank-statements`.
- **J2 Statement import** — upload CSV or OFX/QFX → parse-to-preview (lines +
  balances + warnings) → submit. `POST /bank-statements/import?format=csv|ofx`,
  then `POST /bank-statements`.
- **J3 Reconciliation worksheet** — bank lines vs ledger cash entries with
  cleared/uncleared, the difference + balanced verdict; clear/unclear, auto-match,
  record fee/interest adjustment (posts PendingApproval), complete.
  `POST /reconciliations`, `GET /reconciliations/{id}`, `…/clear|unclear|auto-match|adjustments|complete`.

## K. Audit — *Backed*
**K1 log** (`/audit`), **K2 verify** (`/audit/verify`, chain integrity),
**K3 entry audit** (`/audit/{id}`).

## L. Admin / User management — *Needs-BE*
Admin-only (per the security boundary). Leans on the control DB (the existing
membership/role model) + the new user-mapping store.
- **L1 Users** — list + **create user** (Entra invite/user + user-mapping row).
- **L2 Permissions / roles** — edit a user's role/permissions.
- **L3 Client memberships** — grant/revoke a user's access to clients + role per
  client (`ControlStore.GetMembershipAsync` is the existing read side).

## M. Settings (per client) — *Needs-BE (config store)*
- **M1 Number & date formatting** — the **Format Profile** editor (negatives,
  decimals, scale, separators, symbol placement, zero-as-dash, date format,
  account-code display).
- **M2 Palette / theme** — pick the **palette preset** for this client (+ the
  user's Light/Dark/Follow-OS theme lives here too).

## N. Reports / Export — *Backed data; Needs-BE (PDF generation)*
- **Export PDF / CSV** actions on statements, trial balance, subledger,
  reconciliation — server-side **PDF via the PdfLibrary** (`PdfRect`/`PdfColor`
  builder), branded with the palette + formatted by the Format Profile.
- **N-future. Report builder (deferred)** — a drag-and-drop report-layout editor
  (canvas + field/block palette + bindings + save/load templates → rendered
  through PdfLibrary). Stretch; built on top of the standard-reports pipeline.

---

## Screen → backing summary

| Area | Screens | Backed today? |
|---|---|---|
| A Shell | nav + top bar (Edit Firm/Client, switcher) | nav yes; firm/client edit + my-clients Needs-BE |
| B Front door | login, firm profile, client profile | **Needs-BE** (Entra + CRUD; FY-end exists) |
| C–K | dashboard, journal, accounts, trial balance, statements, periods, subledgers, bank rec, audit | **Backed** |
| L Admin | users, permissions, memberships | **Needs-BE** |
| M Settings | formatting profile, palette/theme | **Needs-BE** (config store) |
| N Reports | PDF/CSV export; report builder (deferred) | data Backed; PDF gen Needs-BE |

**Headline:** the whole accounting surface (C–K) is backed by the existing API
today and builds against a dev-auth stub. The front door (B), admin (L), settings
store (M), and PDF generation (N) need new backend.

## Prioritized backlog / parking lot (from the 2026-06-28 riff)

| Item | Priority | Note |
|---|---|---|
| Palette tokens + Light/Dark/Follow-OS switch | **Foundational** | CSS vars; ships with slice 1 |
| Format Profile schema + default + TS formatter | **Foundational** | default profile applied everywhere from day 1 |
| Firm/Client profile CRUD (Edit Firm/Client) | **Early** | permanent top-bar fixtures |
| Standard PDF reports (PdfLibrary) | Mid | reuses palette + profile |
| Settings → Formatting editor (M1) | Mid | edit the profile |
| Admin → user/permission management (L) | Mid | with the real front door |
| Palette presets + per-client palette (M2) | Mid | infra cheap; picker is a settings screen |
| Per-client display settings store (M) | Mid | backs M1/M2 |
| Drag-and-drop report builder (N-future) | **Later / stretch** | on top of standard reports |

## Proposed slicing (to agree next)

Accounting-first (all backed), greenfield backend folded in where it unblocks the
permanent fixtures:
1. **Shell + theming/format foundation + demo critical path** (dev-auth stub):
   app shell + top bar, Light/Dark/Follow-OS + palette tokens, Format-Profile
   default + TS formatter, dashboard, journal post → approve → entry list, trial
   balance, balance sheet + income statement.
2. **Chart of accounts + periods + cash flow.**
3. **Firm/Client profile CRUD** (Edit Firm/Edit Client) + the per-user client list.
4. **Subledgers** (AR/AP first, then Payroll/Cash).
5. **Bank Reconciliation + Import.**
6. **Standard PDF reports** (PdfLibrary) + **Settings → Formatting**.
7. **Real front door** (Entra auth + user-mapping) + **Admin** (users/permissions/
   memberships).
8. **Palette presets / per-client palette**, **Audit views**.
9. **Later:** drag-and-drop report builder.

## Resolved decisions (2026-06-28)
- Angular 22; component library (Material vs Bootstrap) still open.
- Palette = the Accounting101Palette.pdf five colors; solid blue buttons; parens
  negatives; aligned decimals; **Light default**; Light/Dark/Follow-OS switch.
- Top bar = client switcher (left) + Edit Firm + Edit Client.
- Per-client: Format Profile + palette preset (stored display settings).
- Admin user/permission management is in scope (area L).
- PDF reports via PdfLibrary; drag-and-drop report builder deferred.

## Open (for the slicing/plan stage)
- Component library: **Angular Material vs Bootstrap** (vs a Tailwind kit).
- Auth-stub mechanism for early slices (reuse the dev token the tests use).
- Depth of each subledger's first cut (AR invoices+payments first; credits later).
