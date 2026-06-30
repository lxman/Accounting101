# Payables Module UI — Foundation Slice (P-A)

**Date:** 2026-06-30
**Status:** Approved design, pre-implementation
**Mirrors:** Receivables AR-1 foundation (`accounting101-ui-receivables-ar1`)

## Goal

Stand up the Payables module in the Angular UI by mirroring the proven Receivables
**AR-1** foundation: a module shell with two tabs — **Bills** and **Vendors** — covering
the vendor + bill lifecycle end to end (create vendor, draft bill, enter, void). Bill
payments and vendor credits UI are deferred to later slices.

## Backend additions (minimal — only what this slice requires)

The Payables backend (`Modules/Payables/`) is at AR-1 maturity. The host already mounts
all five modules (`Accounting101.Host/Program.cs`), and the one-module-per-host gotcha is
resolved (keyed `ModuleCredential`, named `PayablesLedgerClient`). The Payables HTTP
surface today: `POST /vendors`, `POST /bills`, `POST /bills/{id}/enter`,
`POST /bills/{id}/void`, `GET /bills/{id}`, `GET /bills?vendorId=…` (vendorId **required**),
plus bill-payments and vendor-credit-application POSTs and `GET /vendors/{id}/credit-balance`.

There is **no vendor list** — `IVendorStore` has only `SaveAsync` + `GetAsync` (by id), and
there is no `GET /vendors` endpoint. A vendor dropdown / Vendors tab is impossible without
one. So this slice adds exactly:

1. `IVendorStore.ListAsync(Guid clientId, CancellationToken)` returning `IReadOnlyList<Vendor>`,
   implemented in `DocumentVendorStore` (mirror `DocumentCustomerStore`'s list over the
   document store).
2. `GET /clients/{clientId}/vendors` → `IReadOnlyList<Vendor>` in `PayablesEndpoints`
   (mirror the receivables `GET /customers` shape).
3. Backend xUnit tests: vendor-store list + endpoint, mirroring the customer equivalents
   (`DocumentVendorStoreTests`, `PayablesE2eTests`).

**Explicitly NOT added:** bill draft edit/delete (`PUT`/`DELETE /bills/{id}` do not exist).
The bill editor is therefore **create-only** this slice — a draft can be entered or left in
place; editing/discarding a draft is a deferred follow-up (exactly as AR-1 deferred
drafts-as-scratch).

## Frontend structure

Mirrors `core/receivables` + `features/receivables`. The UI calls
`{apiBaseUrl}/clients/{clientId}/…` directly (same host).

### Core (`core/payables/`)

- **`payables.ts`** — models:
  - `Vendor { id; name; email }`
  - `BillStatus = 'Draft' | 'Entered' | 'Void'`
  - `BillLine { description; amount; expenseAccountId }`
  - `Bill { id; vendorId; number; billDate; dueDate; vendorReference; memo; status; lines }`
  - `BillView { bill; openBalance; settlementStatus }`
  - `DraftBillRequest { vendorId; billDate; dueDate; vendorReference; memo; lines }`
  - `BillListQuery { vendorId; settlement?; skip; limit; order? }`
- **`payables.service.ts`** (root singleton) —
  - Vendors: `load()` (GET /vendors → `vendors` signal), `create(name,email?)`,
    `vendorName(id)`, `byId`.
  - Bills: `listBills(query)` (GET /bills, returns `PagedResponse<BillView>`),
    `getBill(id)`, `draftBill(req)`, `enter(id)`, `void(id, reason?)`.
  - `selectedVendorId` signal persisted per-client in localStorage (copy of the
    receivables `selectedCustomerId` pattern incl. the restore-on-client-change effect and
    drop-stale-selection on load).

### Features (`features/payables/`)

- **`payables-shell.ts`** — tab bar (Bills | Vendors), `<router-outlet/>`. Default child
  route → `bills`. Mirror `receivables-shell.ts` (routerLinkActive styling, `data-testid`).
- **`vendor-list.ts`** — vendor table + inline create form (name + optional email).
  **Whole-row click** sets the selected vendor and routes to `/payables/bills` (our
  row-click standard; no 360 yet). Mirror `customer-list.ts`.
- **`bill-list.ts`** — `<app-vendor-select>` at top (bills are vendor-scoped — `vendorId`
  required), settlement filter (open/paid/all), bill table (number, bill date, vendor ref,
  total, settlement status, open balance), **whole-row click → `/payables/bills/:id`**.
  Mirror `invoice-list.ts`. New-bill button → `/payables/bills/new`.
- **`bill-editor.ts`** — create a bill:
  - Header: vendor (`<app-vendor-select>` or hlm-select), bill date, due date,
    vendor reference, memo.
  - Line grid: each line = **Description · Amount · Expense account**. The expense-account
    dropdown is sourced from `AccountsService` filtered to `type === 'Expense' && postable
    && active`. Add/remove line.
  - Total = sum of line amounts (no qty/unitPrice/tax — bills are simpler than invoices).
  - Save → `draftBill` → navigate to the new bill's detail. Create-only (no edit path).
  - `canSave` = vendor set, ≥1 line, each line has description + amount > 0 + an expense
    account, total > 0.
- **`bill-detail.ts`** — header (vendor name, number, status badge, bill/due dates, vendor
  ref, memo) + lines (description, resolved account name, amount) + total + settlement
  (`openBalance`, `settlementStatus`). Actions: **Enter** when `Draft`; **Void** (with
  optional reason) when `Entered`. Surface backend problem-details on failure
  (`extractProblem`). Mirror `invoice-detail.ts`.
- **`shared/vendor-select.ts`** — `<app-vendor-select>`, mirror of `<app-customer-select>`
  (hlm-select with `*hlmSelectPortal` + `[itemToString]`, loads vendors via the service).

### Wiring

- Payables child routes under a `/payables` shell route (lazy, matching how receivables is
  registered in the app routes).
- A **Payables** sidebar nav link (mirror the Receivables link incl.
  `routerLinkActiveOptions {exact:true}` gotcha).

## Testing

Per-file `.spec.ts` for every component and the service (codebase standard), mirroring the
receivables specs — render, interaction, navigation, error surfacing. Backend xUnit tests
for the new store method + endpoint. UI suite and backend suite both green before merge.

## Deferred (out of scope this slice)

- Bill **payments** UI (needs `GET /bill-payments` list endpoint first).
- Vendor **credits** UI (needs `GET /vendor-credit-applications` list + unified view first).
- Vendor **account (360)** aggregate.
- Bill draft **edit/discard** (needs backend `PUT`/`DELETE /bills/{id}`).
- Dev-stack `Payables__Accounts__*` env block — needed only when *entering* bills against
  real posting accounts; flagged here. The `start.ps1` block is gitignored and added
  out-of-band (mirror the `Receivables__Accounts__*` block; see
  `accounting101-devstack-module-config`).

## Decisions taken (not asked)

- **Vendor row click → Bills filtered to that vendor** (vs. a non-clickable list): keeps the
  whole-row-click standard and is the only useful drill-in without a 360.
- **Bill editor is create-only**: matches the current backend (no PUT/DELETE for drafts) and
  AR-1's initial maturity.

## Commit trailer

```
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
```
