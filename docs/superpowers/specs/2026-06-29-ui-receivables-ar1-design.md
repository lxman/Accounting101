# UI Slice AR-1 — Receivables: Customers + Invoices — Design

**Date:** 2026-06-29
**Status:** Approved (scope + layout confirmed); precedes the implementation plan.

## Purpose

Build the first Receivables screen for the Accounting101 SPA: manage **customers**
and the **invoice** lifecycle (draft → issue → void), grounded in the existing
Receivables module API. Payments, credit applications, dispositions
(write-offs / credit-notes / refunds), customer subledger/aging, and any
GL-approval UI are explicitly **out of scope** — they are later AR slices.

## Decisions settled in brainstorming

1. **Scope = AR-1 (Customers + Invoices).** Payments/credits/dispositions deferred.
2. **Layout = reuse the Journal document pattern, not a new paradigm.** Invoices are
   documents with the same list → routed-detail → routed-form shape as journal
   entries. The **customer is the lead filter** on the invoice list (the role the
   posting filter plays on the journal list), *not* a master-detail pane and *not*
   tabs. This keeps the UI vocabulary consistent: Statements' tabs stay "two views
   of one thing," and every entity stays list-based.
3. **One backend addition is required:** a `GET /clients/{clientId}/customers`
   list endpoint. It does not exist today, and the invoice list endpoint requires
   a `customerId`, so the UI cannot pick a customer or list invoices without it.

## Backend addition (prerequisite, first task of the plan)

`GET /clients/{clientId}/customers` → **200** `Customer[]` ordered by `Name`
(bare array, **not paged** — the customer picker needs them wholesale; customer
counts per client are modest). `RequireAuthorization()`, client-scoped, mirrors
the other Receivables routes. Implementation adds the endpoint in
`ReceivablesEndpoints.cs` plus the supporting service/store list method
(`Customer.Name` ascending). Covered by an EphemeralMongo integration test
alongside the existing Receivables tests.

No other backend change. Customer **names** on invoices are resolved client-side
from the cached customer list (mirrors `AccountsService.label()`), so no DTO
expansion is needed.

## Architecture (Angular 22, standalone, signals, zoneless, OnPush)

### Core data layer — `core/receivables/`
- **Types** (`receivables.ts`): `Customer {id,name,email?}`,
  `InvoiceLine {description,quantity,unitPrice,taxable,revenueCategory?}` (+ derived
  `amount` computed in the UI where needed), `Invoice {id,customerId,number?,issueDate,
  dueDate?,status,taxRate,memo?,lines}`, `InvoiceView {invoice,openBalance,settlementStatus}`,
  `DraftInvoiceRequest`, `VoidInvoiceRequest`; enums `InvoiceStatus = 'Draft'|'Issued'|'Void'`,
  `SettlementStatus = 'Open'|'PartiallyPaid'|'Paid'`, `SettlementFilter = 'open'|'paid'`.
  All money is raw `number` (decimal on the wire); dates are ISO strings.
- **`ReceivablesService`** (`receivables.service.ts`): client-scoped URLs via
  `ClientContextService.clientId()` (guard `if (!id) return EMPTY`), mirrors
  `EntriesService`/`AccountsService`.
  - Customers: `customers` signal cache, `load()`, `create(name,email?)` (POST, tap
    into cache), `customerName(id)` resolver (cache → name, fallback to id).
  - Invoices: `listInvoices({customerId, settlement, skip, limit, order})` →
    `Observable<PagedResponse<InvoiceView>>`; `getInvoice(id)` → `InvoiceView`;
    `draft(req)` (POST), `updateDraft(id,req)` (PUT), `deleteDraft(id)` (DELETE),
    `issue(id)` (POST /issue), `void(id,reason?)` (POST /void).

### Screens — `features/receivables/`
- **`/receivables` → `InvoiceList`** (primary screen). Lead **customer selector**
  (`hlmSelect` with `[itemToString]` + `*hlmSelectPortal`, value = customer GUID),
  **settlement filter** (Open/Paid), paged table mirroring `entry-list`: columns
  **Number · Issue date · Due date · Total · Open balance · Status**. Reactive
  list via `toObservable(query) → switchMap → toSignal` (cancels in-flight on
  filter change), `PagedResponse` paging ("Page N of M", prev/next), `money`/
  `displayDate` for cells. **New invoice** button (→ editor, customer prefilled).
  Row → `/receivables/invoices/{id}`. A **Customers** link (→ customers screen).
  Empty states: no customer selected → "Select a customer to view invoices"; zero
  customers → prompt to add one.
- **`/receivables/customers` → `CustomerList`.** Plain list (Name · Email) with a
  small inline **New customer** form (name required, email optional) calling
  `create()`. List-based, consistent with the rest of the app.
- **`/receivables/invoices/new` and `/receivables/invoices/:id/edit` →
  `InvoiceEditor`** (Signal Forms, mirrors `entry-form`/`account-editor`).
  Fields: **customer** (prefilled from the list filter on `new`; read-only once
  set on an existing draft), **line items** array (description, quantity,
  unitPrice, taxable, optional revenueCategory) with add/remove, **taxRate**,
  **issueDate**, optional **dueDate**, optional **memo**. Live-computed
  **Subtotal / Tax / Total** shown as the user edits (UI mirrors the backend
  formula: `subtotal = Σ qty·unitPrice`, `tax = round(taxRate · taxableBase, 2)`,
  `total = subtotal + tax`). **Save** persists the draft (POST on new, PUT on
  edit) and returns to the list/detail. Validation: ≥1 line, positive total,
  required fields gate Save.
- **`/receivables/invoices/:id` → `InvoiceDetail`** (mirrors `entry-detail`).
  Header: number (or "Draft"), customer name, issue/due dates, **status badge**,
  **settlement badge**, totals. Footed line table (Subtotal, Tax, Total) and
  **Open balance**. State-driven actions:
  - **Draft** → Edit (→ editor), Delete (DELETE, confirm), **Issue** (POST /issue).
  - **Issued** → **Void** (reason input → POST /void). Read-only otherwise.
  - **Void** → read-only.
  On issue, the module posts a `PendingApproval` GL entry via module identity; the
  invoice's own status is what this screen reflects. GL approval lives in the
  existing approval queue and is **not** part of this slice.

### Shared display
- Reuse `money` / `displayDate` (`core/format/display`).
- Add a small **`InvoiceStatusBadge`** (`shared/`) for `InvoiceStatus` and a
  **settlement** cue (Open / PartiallyPaid / Paid). Mirrors `posting-badge`
  (OnPush, `input.required`, `data-testid`).

### Routing
Replace the `/receivables` placeholder. Add the `receivables` route subtree
(`''` → InvoiceList, `customers` → CustomerList, `invoices/new`,
`invoices/:id/edit` → InvoiceEditor, `invoices/:id` → InvoiceDetail) and remove
`/receivables` from the placeholder filter in `app.routes.ts` (same shape as the
`accounts` subtree).

## Error handling
- All writes surface server `ProblemDetails` via `extractProblem(e).detail`
  (422 invalid customer / empty lines / total ≤ 0; 409 not-editable state /
  closed-period relay from the engine). Shown inline on the editor/detail.
- List/customer load failures show an inline error; the screen still renders.

## Testing (Vitest + EphemeralMongo)
- **Backend:** integration test for `GET /customers` (returns created customers,
  ordered, client-scoped, empty when none).
- **Service:** `ReceivablesService` HTTP tests — customer load/create + cache,
  invoice list with query params, get, draft/update/delete/issue/void hit the
  right method+URL+body; `customerName` resolves and falls back.
- **Components:** InvoiceList (customer filter drives the query; paging; empty
  states; row link), InvoiceEditor (validation gate; live totals; POST vs PUT;
  422 surfaced), InvoiceDetail (state-driven actions call the right endpoints;
  footing), CustomerList (create posts and appears), InvoiceStatusBadge.

## Out of scope (later AR slices)
Payments + allocations, credit applications, credit balance, write-offs /
credit-notes / refunds, customer detail / subledger / aging (needs aggregate
endpoints), and any GL-approval UI.

## Self-review
- **Placeholders:** none.
- **Consistency:** invoice list/detail/form mirror journal entry list/detail/form;
  customer filter mirrors the journal posting filter; badges mirror posting-badge;
  service mirrors EntriesService/AccountsService; routing mirrors the accounts
  subtree. No new layout paradigm introduced.
- **Scope:** single slice — one backend endpoint + customers screen + invoice
  list/detail/editor. Payments and beyond explicitly deferred.
- **Ambiguity:** customer is read-only once an invoice exists (renumber/reassign
  not supported in AR-1); customers list is unpaged by deliberate choice (picker
  needs all); settlement filter values are `open|paid` per the API.
