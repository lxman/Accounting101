# Accounting 101 — UI Screen Map & Inventory — Design

**Date:** 2026-06-28
**Status:** In review (screen inventory; precedes slice sequencing)

## Purpose

The complete screen inventory for the Accounting 101 SPA, grounded in the **real
backend API as it exists today**, updated for everything shipped this session
(Bank Reconciliation, CSV/OFX import, list pagination). Each screen lists its
purpose, a rough wireframe, the exact backing endpoints, and whether it is
**Backed** (existing API), **Needs-BE** (requires new backend), or **Partial**.
Once agreed, this map is sequenced into build slices.

Supersedes the screen map in `UI/UI-UX-provisioning-plan.md` (which predates the
Reconciliation/Import modules and listed them as "future").

## Stack & conventions (decided)

- **Angular 22** — standalone components, signals + the modern services
  architecture, typed reactive forms, the newest everything. Component library
  (Angular Material vs Bootstrap) **still open** — decided before the first build
  slice.
- Static build → Azure Static Web Apps; the API is `api.accounting-101.com`
  (never navigated). JWT bearer on every call.
- **Auth is greenfield** (Entra ID + a user-mapping store don't exist yet). For
  building the accounting screens we can run against a **dev-token auth stub**
  (the same dev tokens the tests use) and wire real Entra later.

## Cross-cutting patterns (apply to every screen)

- **Paged tables** consume the `PagedResponse<T> { items, total, skip, limit }`
  envelope shipped this session — server-side paging, "Page N of M", page-size,
  `order=asc|desc`, and `includeVoided` where the list supports it. The UI uses
  the *echoed effective* `skip`/`limit` for page math.
- **Maker-checker is visible.** Posting a journal entry or a module document
  lands **PendingApproval**; a distinct approver approves it (SoD). The UI must
  show pending state, an approval queue, and "who created / who approved."
- **Money** is USD, 2 dp, debit-positive convention; negatives in parentheses or
  red per accounting norms. (Multi-currency is explicitly out — USD-only.)
- **Lifecycle actions** on a posted entry/document: void (withdraw if pending /
  reverse if posted), revise (supersede), reverse (closed-period-safe). Surface
  the right action per state.
- **Period-aware:** posting into a closed period is rejected; the UI surfaces the
  engine's 409/422 messages (e.g. "closed through …", "run close-year instead")
  inline, and offers the reverse-into-open-period path.
- **Empty/loading/error** states on every data view; **client switcher** in the
  shell scopes every call to `/clients/{clientId}/…`.

---

## A. Global shell

**A1. App shell / nav** — *Backed (nav only)*
The persistent frame: top bar (client switcher, signed-in user, sign-out),
left sidebar (progressive reveal per the blueprint), routed content area.
```
┌───────────────────────────────────────────────────────────────┐
│  Accounting 101      [Acme Corp ▼]            Jordan ▼  (out)  │
├───────────┬───────────────────────────────────────────────────┤
│ Dashboard │                                                   │
│ Journal   │            << routed screen content >>            │
│ Accounts  │                                                   │
│ Trial Bal │                                                   │
│ Statements│                                                   │
│ Periods   │                                                   │
│ Subledgers│                                                   │
│  Recvbl…  │                                                   │
│  Payabl…  │                                                   │
│  Payroll  │                                                   │
│  Cash     │                                                   │
│  Bank Rec │                                                   │
│ Audit     │                                                   │
└───────────┴───────────────────────────────────────────────────┘
```
- Client switcher → `GET /admin/clients` (the user's clients) [today admin-only;
  needs a per-user "my clients" list — Needs-BE].
- Progressive reveal (Setup → Clients → Accounting/Subledgers) per the blueprint.

---

## B. Front door — *Needs-BE (auth + profile backend is greenfield)*

**B1. Landing + Login** — *Needs-BE*
Marketing-thin landing with "Sign in with Microsoft" → MSAL redirect → returns
authenticated. Backend: Entra JWT wiring (~20 lines) + a `user_mappings` store
(EntraOid → InternalUserId) consulted by `LedgerGateway`.

**B2. Business-profile setup** — *Needs-BE*
First-run "tell us about your firm" (name, address, phone). Backend: a new
`BusinessProfile` collection + CRUD (none today).
```
Tell us about your firm
  Name      [___________]
  Address   [___________]
  Phone     [___________]            [ Save → ]
```

**B3. Client setup** — *Partial*
"Create your first client" (name, address, **fiscal year-end**, contact). Backend:
`POST /admin/clients` exists (name, FY-end, SoD) + `PUT /admin/clients/{id}/fiscal-year-end`
(shipped); address/contact metadata + COA seeding are Needs-BE; `POST
/clients/{id}/onboarding` seeds opening balances (exists).
```
Create a client
  Name            [___________]
  Fiscal year-end [December ▼]
  Address/contact [___________]
  [ ] Require segregation of duties
                                    [ Create → seeds chart ]
```

---

## C. Dashboard — *Backed*

**C1. Dashboard** — *Backed*
Landing after setup: quick actions + at-a-glance numbers (cash, A/R, A/P, net
income MTD) + a pending-approvals badge.
```
Acme Corp — Dashboard
┌── Quick actions ─────────────┐  ┌── At a glance ───────────┐
│ + Journal entry              │  │ Cash        $ 12,400     │
│ ▸ Trial balance              │  │ A/R         $  8,150     │
│ ▸ Income statement           │  │ A/P         $  3,900     │
│ ▸ Close period               │  │ Net income (MTD) $ 4,100 │
└──────────────────────────────┘  └──────────────────────────┘
⚠ 3 entries pending approval  →
```
- Numbers from `GET /trial-balance` / `GET /statements/*`; pending count from
  `GET /entries?posting=PendingApproval&limit=1` (read `total`).

---

## D. Journal — *Backed*

**D1. Post journal entry** — *Backed*
The core data-entry grid: balanced debit/credit lines, optional dimensions,
effective date, memo. **Validate (dry-run) before post.** Posts PendingApproval.
```
New journal entry            Date [2026-06-30]  Ref [____]  Memo [______]
┌ Account ──────────────┬ Dr ───────┬ Cr ───────┬ Dimension ─────┐
│ 1000 Cash         ▼   │ 1,000.00  │           │  —             │
│ 4000 Revenue      ▼   │           │ 1,000.00  │  Customer:Acme │
│ + add line                                                     │
└───────────────────────┴───────────┴───────────┴────────────────┘
                         Dr 1,000.00  Cr 1,000.00  ✓ balanced
                         [ Validate ]      [ Post (pending) ]
```
- `GET /accounts` (account picker), `POST /entries/validate` (pre-flight → inline
  field errors via the ValidationProblemDetails the engine returns), `POST /entries`.

**D2. Entry list** — *Backed*
Paged, filterable list of all entries (date, account, posting state, reference).
```
Journal               [All ▼] [Posted ▼]  search…        Page 2 of 9
┌ # ─┬ Date ──────┬ Memo ───────────┬ Dr/Cr ──┬ State ────────┐
│ 142│ 2026-06-30 │ Sale to Acme    │ 1,000   │ Posted        │
│ 141│ 2026-06-29 │ Office supplies │   240   │ PendingApprov │
└────┴────────────┴─────────────────┴─────────┴───────────────┘
                                     « 1 2 3 … 9 »  [50/page ▼]
```
- `GET /entries` (paged envelope; `?posting=`, `?account=`, `?reference=`).

**D3. Pending-approval queue** — *Backed*
The maker-checker worklist: entries awaiting approval; approve / void; shows
creator (SoD blocks self-approval).
- `GET /entries?posting=PendingApproval` (paged), `POST /entries/{id}/approve`,
  `POST /entries/{id}/void`.

**D4. Entry detail** — *Backed*
One entry: lines, audit stamp (created/approved by + at), source back-link, and
the state-appropriate actions (approve / void / revise / reverse).
- `GET /entries/{id}`, `GET /audit/{id}`, the lifecycle POSTs.

---

## E. Chart of Accounts — *Backed*

**E1. Accounts list / tree** — *Backed*
The chart grouped by type (Asset/Liability/Equity/Revenue/Expense) with balances.
- `GET /accounts`, `GET /accounts/{id}/balance` (or balances from trial balance).

**E2. Account detail / editor** — *Backed*
View/edit one account (number, name, type, normal side, RE flag, cash-flow
activity) + its running balance/activity.
- `GET /accounts/{id}`, `PUT /accounts/{id}`, `GET /accounts/{id}/balance`.

---

## F. Trial Balance — *Backed*

**F1. Trial balance** — *Backed*
As-of trial balance; Dr/Cr columns foot equal; drill from a row → that account's
entries.
```
Trial balance     as of [2026-06-30]                 Dr        Cr
1000 Cash                                          12,400
1100 Accounts Receivable                            8,150
2000 Accounts Payable                                        3,900
3000 Equity                                                 12,550
4000 Revenue                                                 8,200
5000 Expenses                                       4,100
                                                  ───────   ───────
                                                   24,650    24,650  ✓
```
- `GET /trial-balance?asOf=`.

---

## G. Financial Statements — *Backed*

**G1. Balance sheet** — *Backed* (`GET /statements/balance-sheet`) — assets =
liabilities + equity, `IsBalanced` badge, as-of selector.
**G2. Income statement** — *Backed* (`GET /statements/income-statement`) — revenue
− expenses → net income, period selector.
**G3. Cash flow** — *Backed* (`GET /statements/cash-flow`) — operating/investing/
financing.
Each: a clean rendered statement + as-of/period selector + (later) export.

---

## H. Periods — *Backed*

**H1. Close month** — *Backed* — `POST /periods/close`; the UI surfaces the FY-end
guard ("run close-year instead", 409) and the pending-blocker guard.
**H2. Close year** — *Backed* — `POST /periods/close-year`; shows the closing
entry → retained earnings; the wrong-date guard.
**H3. Reopen period** — *Backed* — `POST /periods/reopen` (admin/step-up gated);
the UI handles the step-up auth challenge.
```
Periods       Closed through: 2026-05-31
  [ Close month (2026-06-30) ]   [ Close year ]   [ Reopen… ]
```

---

## I. Subledgers (the four modules) — *Backed*

Each module: a customer/vendor (or run) list, a document list (paged), a
create/record form, and a document detail with void/lifecycle. All post
PendingApproval via module identity.

**I-AR. Receivables** — *Backed*
- Customers list/create; **Invoices** (draft → issue → void) paged list + form +
  detail; **Payments** (record + void) with allocation to invoices; **Credits**
  (credit-applications, write-offs, credit-notes, refunds) + voids.
- Endpoints: the Receivables module (customers, invoices, payments, credit-apps,
  write-offs, credit-notes, refunds — paged lists). Invoice form is a line grid
  like D1.
```
Receivables ▸ Invoices    customer [Acme ▼] [Open ▼]      Page 1 of 3
 INV-00007  2026-06-20  $1,200.00  Open  ($1,200 due)   ▸
 INV-00006  2026-06-12  $  800.00  Paid                 ▸
```

**I-AP. Payables** — *Backed*
- Vendors; **Bills** (draft → issue → void) paged list + form + detail; **Bill
  payments** (record + void) with allocation. Mirrors AR.

**I-PR. Payroll** — *Backed*
- **Payroll runs** (record + void) paged list + form; **Tax remittances** (record
  + void). Form = the run inputs → the module composes the GL entry.

**I-CA. Cash** — *Backed*
- **Disbursements** + **Deposits** (record + void) paged lists + forms (line +
  cash side). Quick "money in / money out" entry that isn't an invoice/bill.

---

## J. Bank Reconciliation — *Backed (NEW this session — not in the old blueprint)*

**J1. Bank statements list** — *Backed*
Paged list of recorded statements for a cash account; + "Record statement" and
"Import" actions.
- `GET /bank-statements?cashAccountId=` (paged), `POST /bank-statements`.

**J2. Statement import** — *Backed*
Upload a CSV or OFX/QFX file → **parse-to-preview** (lines + detected balances +
warnings) → review/edit → submit to create the statement. CSV needs a column
mapping; OFX is automatic.
```
Import bank statement      format ( CSV | OFX )   [ choose file ]
  [CSV] map: Date=DATE  Amount=AMOUNT  Desc=DESCRIPTION  Ref=CHECK#
            skip status = Pending
  ── Preview ───────────────────────────────────────────────
   2026-06-28  PAYROLL          +1,200.00
   2026-06-27  CHECK 1021        -300.00     ⚠ 1 row skipped
   detected closing: 900.00     opening [____]
                                  [ Create statement → ]
```
- `POST /bank-statements/import?format=csv|ofx` (multipart preview), then `POST
  /bank-statements`.

**J3. Reconciliation worksheet** — *Backed* — the heart of the module
Start a reconciliation against a statement; the worksheet shows the bank lines +
the ledger cash entries with cleared/uncleared, the **reconciled difference** and
**balanced** verdict; clear/unclear, **auto-match by amount**, and **record a
fee/interest adjustment** (which posts PendingApproval). Complete when balanced.
```
Reconciliation REC-00003   stmt closing 900.00   book 1,205.00
  diff −305.00  ✗ not balanced            [ Auto-match ] [ + Adjustment ]
┌ cleared ┬ date ───── entry ───────────── cash effect ┐
│   ☑     │ 06-28  Deposit (payroll)        +1,200.00   │
│   ☐     │ 06-27  Check 1021                 −300.00   │
│   ☐     │ 06-30  Bank fee  (adjustment, pending appr) │
└─────────┴────────────────────────────────────────────┘
                              [ Clear ] [ Unclear ] [ Complete ]
```
- `POST /reconciliations`, `GET /reconciliations/{id}` (worksheet), `…/clear`,
  `…/unclear`, `…/auto-match`(+`?apply`), `…/adjustments`(+`/void`), `…/complete`.

---

## K. Audit — *Backed*

**K1. Audit log** — *Backed* — the tamper-evident chain for the client; verify
badge.
**K2. Audit verify** — *Backed* — runs the chain integrity check, shows pass/fail.
**K3. Entry audit** — *Backed* — the audit trail for one entry (created/approved/
revised/reversed lineage).
- `GET /audit`, `GET /audit/verify`, `GET /audit/{id}`.

---

## Screen → backing summary

| Area | Screens | Backed today? |
|---|---|---|
| A Shell | nav, client switcher | nav yes; "my clients" list Needs-BE |
| B Front door | landing/login, business setup, client setup | **Needs-BE** (Entra + profile CRUD; FY-end exists) |
| C Dashboard | dashboard | Backed |
| D Journal | post, list, approvals, detail | Backed |
| E Accounts | list/tree, editor | Backed |
| F Trial balance | trial balance | Backed |
| G Statements | BS, IS, CF | Backed |
| H Periods | close month/year, reopen | Backed |
| I Subledgers | AR, AP, Payroll, Cash (list/form/detail each) | Backed |
| J Bank Rec | statements, import, worksheet | Backed (new) |
| K Audit | log, verify, entry audit | Backed |

**Headline:** the entire **accounting surface (C–K) is backed by the existing API
today** and can be built against a dev-auth stub. Only the **front door (B)** +
the per-user client list (A) need new backend.

## Proposed slicing (to agree next — NOT yet decided)

A natural sequencing once the map is agreed (each a shippable vertical):
1. **Shell + the demo critical path** (dev-auth stub): app shell, dashboard,
   journal post → approve → entry list, trial balance, balance sheet + income
   statement. Proves the maker-checker loop end-to-end in the UI.
2. **Chart of accounts + periods + cash flow** — round out core accounting.
3. **Subledgers** (AR/AP first, then Payroll/Cash) — the module workflows.
4. **Bank Reconciliation + Import** — the worksheet + the CSV/OFX upload.
5. **Audit views.**
6. **Real front door** — Entra auth + user-mapping + business/client setup wizard
   + the per-user client switcher (the Needs-BE backend + its screens).

Sequencing is deliberately accounting-first (all backed) with the greenfield
front-door backend last, so the demo is clickable against the real engine
quickly. To revisit together.

## Open questions (for the slicing discussion)

- Component library: **Angular Material vs Bootstrap** (vs a Tailwind kit).
- Auth-stub mechanism for early slices (reuse the dev-token the tests use).
- How "deep" each subledger screen goes in its first cut (e.g. AR invoices +
  payments first; credits/write-offs/refunds as a follow-up).
- Whether the front door (B) moves earlier if a live multi-tenant demo is needed
  sooner than the full accounting UI.
