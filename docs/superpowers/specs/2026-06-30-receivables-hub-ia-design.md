# Receivables Hub + Sub-Navigation (Slice A) — Design

**Date:** 2026-06-30
**Status:** Approved (design); spec pending user review
**Area:** `UI/Angular` (Receivables feature) — no backend changes

## Goal

Give the Receivables area the information architecture the screen map (`2026-06-28-ui-screen-map-design.md`, §I-AR) already calls for: a hub with sibling document-type tabs (Invoices · Payments · Customers), each with **one home**. This stops the "many places for one thing" drift — payments currently have no home and are recorded from two scattered buttons (invoice-list header + invoice-detail). After this slice, payments are recorded from exactly one place: the Payments tab.

This is **Slice A** of two. Slice B adds the Credits area (credit-apps / write-offs / credit-notes / refunds) inside this same frame.

## Background / current state

- The sidebar **Receivables** link points at `/receivables`, which renders the **invoice list** directly — there is no hub. `Customers` and `Record payment` are reachable only as buttons hung off the invoice screens. There is no Payments home and no Credits home.
- Record-payment is reachable from **two** places today: the invoice-list header button and the invoice-detail "Record payment" link (both added in the 2026-06-30 AR-payments slice).
- The app already has the hub pattern: `Statements` is a parent component with a tab nav (`RouterLink` + `RouterLinkActive`) over a `<router-outlet/>` and child routes (`statements.ts`). We mirror it.
- The sidebar highlight uses **longest-prefix** matching (`shell.ts` `activePath`), so a nested `/receivables/invoices` keeps **Receivables** lit automatically — no sidebar change needed.
- `GET /payments?customerId=` already exists (AR-payments slice); `ReceivablesService.listPayments(customerId)` returns `Payment[]`. No backend work in this slice.
- The persisted per-client customer selection lives in `ReceivablesService` (`selectedCustomerId` / `setSelectedCustomer`), so both the Invoices and Payments tabs share one selected customer.

## Scope

**In:** a `ReceivablesShell` hub with a tab nav; route re-nesting under it; a new `PaymentList` home; a shared `<app-customer-select>` used by both list tabs; removal of both Record-payment buttons (strict one-place — payments are recorded only from the Payments tab); test updates.

**Out (deferred):** the Credits area (Slice B); a payment **detail** screen and void-from-payments (voiding a payment already works on the invoice detail); any contextual deep-link back onto an invoice ("the user chose strict one-place; we can add a contextual link later"). The `PaymentEditor`'s `&invoice=` prefill support stays in code (tested, harmless) but is unused after this slice.

## Architecture

### 1. `ReceivablesShell` (new parent component)

Mirrors `Statements`. A tab nav over `<router-outlet/>`:

```
features/receivables/receivables-shell.ts
```
Tabs (each an `<a routerLink>` with `routerLinkActive` underline + `data-testid`):
- **Invoices** → `invoices`
- **Payments** → `payments`
- **Customers** → `customers`

`[routerLinkActiveOptions]="{ exact: false }"` so a child route (e.g. `invoices/:id`) keeps its tab lit. OnPush, standalone, imports `RouterLink, RouterLinkActive, RouterOutlet`.

### 2. Route restructure (`app.routes.ts`)

```ts
{ path: 'receivables', component: ReceivablesShell, children: [
  { path: '', pathMatch: 'full', redirectTo: 'invoices' },
  { path: 'invoices', component: InvoiceList },
  { path: 'invoices/new', component: InvoiceEditor },
  { path: 'invoices/:id/edit', component: InvoiceEditor },
  { path: 'invoices/:id', component: InvoiceDetail },
  { path: 'payments', component: PaymentList },        // NEW home
  { path: 'payments/new', component: PaymentEditor },
  { path: 'customers', component: CustomerList },
] },
```

`InvoiceList` moves from the bare landing (`path: ''`) to `invoices`. The sidebar `/receivables` link redirects in to `invoices`. Existing `routerLink="/receivables"` back-links (invoice-detail "← Invoices", customer-list, payment-editor "Cancel/← Invoices") still resolve via the redirect — no mass rewrite. (The "← Invoices" links read fine landing on the Invoices tab.)

All Receivables screens (including the editors/detail) sit under the shell, so the tab bar is always visible — consistent with `Statements`. Acceptable; revisit only if the tab bar feels wrong on the full-page editors.

### 3. `<app-customer-select>` (new shared component)

```
shared/customer-select.ts
```
Encapsulates the customer `hlmSelect` both list tabs need, bound to the **shared** persisted selection:
- value = `svc.selectedCustomerId()`; `(valueChange)` → `svc.setSelectedCustomer($event)`.
- options = `svc.customers()`; `[itemToString]` → `svc.customerName` (so the trigger shows the name, not the GUID); content wrapped in `*hlmSelectPortal` (both required per the spartan-select gotchas).
- Injects `ReceivablesService`; no inputs/outputs (it reads and writes the shared signal directly).

Used by `InvoiceList` (replacing its inline select) and `PaymentList`. Because the selection is service-held, picking a customer on one tab carries to the other.

### 4. `PaymentList` (new — the Payments home)

```
features/receivables/payment-list.ts
```
- `<app-customer-select>` in the header + a **Record payment** button (`routerLink="/receivables/payments/new"`, `[queryParams]="{ customer: customerId() }"`, disabled-styled when no customer — same pattern as New-invoice).
- Reactive load mirroring `InvoiceList`: `toObservable(customerId) → switchMap(cid => cid ? svc.listPayments(cid).pipe(catchError(...)) : of([])) → toSignal`, `initialValue: []`.
- Table columns: **Date · Amount · Method · Allocated · Unapplied · Status**. `Allocated` = Σ allocations; `Unapplied` = amount − allocated; voided rows greyed with a "Voided" marker. Money via `money()`, dates via `displayDate()`.
- Empty states: no customers → "No customers yet — add one first" (link to customers tab); no customer selected → "Select a customer to view payments"; customer with none → "No payments recorded."
- Rows are **not** clickable (no payment-detail screen this slice; voiding stays on the invoice detail). A future payment-detail can add whole-row navigation then.

### 5. Dedupe (the point of the slice)

- **`InvoiceList`** (`invoice-list.ts`): replace the inline customer `hlmSelect` with `<app-customer-select>`; **remove** the "Record payment" header button (New-invoice stays). Move the skip-reset that lived in the old `onCustomerChange` into an `effect` that resets `skip` to 0 when `customerId()` changes (the shared select no longer calls a component method).
- **`InvoiceDetail`** (`invoice-detail.ts`): **remove** the "Record payment" `<a>` from the `@case ('Issued')` block (the void controls + applied-payments list remain).

Net: Record-payment is reachable from exactly one place — the Payments tab.

## Data flow

1. Sidebar **Receivables** → `/receivables` → redirect → `/receivables/invoices` (Invoices tab, lit).
2. Tabs switch among Invoices / Payments / Customers; the chosen customer (shared signal) persists across tabs and reloads.
3. **Payments tab** → pick customer (or inherit the one already selected) → `GET /payments?customerId=` → table. **Record payment** → `PaymentEditor` (unchanged) → on success returns to `/receivables` (Invoices) as today.
4. Voiding a payment is unchanged (invoice detail → applied-payments → Void).

## Error handling

- `PaymentList` relays a failed `listPayments` via `extractProblem` into an inline error line (mirrors `InvoiceList`'s `listError`).
- Redirect handles bare `/receivables`; no dead landing.

## Testing

- **`customer-select.spec.ts`**: renders options from `svc.customers()`; selecting writes `svc.setSelectedCustomer`; trigger shows the name (itemToString) not the GUID.
- **`receivables-shell.spec.ts`**: renders the three tabs with correct `routerLink`s + `data-testid`s.
- **`payment-list.spec.ts`**: with a customer selected, fires `GET /payments?customerId=` and renders rows (amount/method/allocated/unapplied); Record-payment link present with the customer queryParam and disabled-styled when no customer; the three empty states.
- **`invoice-list.spec.ts`**: remove the "Record payment link" test (button gone); confirm the existing selection/persistence/list tests still pass with `<app-customer-select>` in place (they drive selection via `svc.setSelectedCustomer`, so they are unaffected); add a check that no "Record payment" control exists in the invoice-list header.
- **`invoice-detail.spec.ts`**: no test asserted the Record-payment link, so removal needs no test change; add an assertion that the Issued detail has no "Record payment" control (guards the dedupe).
- Route redirect (`'' → invoices`) verified by config + the shell render test; full UI suite green.

## Files touched

- Create: `shared/customer-select.ts` (+ `.spec.ts`)
- Create: `features/receivables/receivables-shell.ts` (+ `.spec.ts`)
- Create: `features/receivables/payment-list.ts` (+ `.spec.ts`)
- Modify: `app.routes.ts` (re-nest under `ReceivablesShell`)
- Modify: `features/receivables/invoice-list.ts` (+ `.spec.ts`) — shared select, remove Record-payment button, skip-reset effect
- Modify: `features/receivables/invoice-detail.ts` (+ `.spec.ts`) — remove Record-payment link
