# AR Payments / Cash-Receipt UI — Design

**Date:** 2026-06-30
**Status:** Approved (design); spec pending user review
**Area:** `UI/Angular` (Receivables feature) + one backend read endpoint (`Modules/Receivables`)

## Goal

Let a user record a customer payment from the UI, allocate it across that customer's open
invoices (excess held as customer credit), settle invoices to PartiallyPaid / Paid, and void a
payment to correct a mistake. This closes the invoice lifecycle in the UI: invoices can currently
be issued and approved, but never paid.

## Background / current state

- The Receivables backend already has the full cash-application engine: `PaymentService.RecordPaymentAsync`
  (validates allocations, posts one balanced cash entry), `VoidPaymentAsync`, `GetCustomerCreditBalanceAsync`,
  and settlement math. Routes today: `POST /payments`, `POST /payments/{id}/void`,
  `GET /customers/{customerId}/credit-balance`. **There is no `GET /payments` route** — the store
  method `GetPaymentsByCustomerAsync` exists but is not exposed over HTTP.
- A recorded payment lands **PendingApproval** (maker-checker), exactly like an issued invoice. The
  existing Approvals screen + journal detail handle that approval; **no new approval UI is needed**.
- **Settlement is document-driven, not approval-gated.** Open balance / settlement status are derived
  from the stored allocations of non-voided payments (`PaymentService.ListInvoiceViewsAsync` /
  `GetInvoiceViewAsync`), regardless of whether the cash entry is approved yet. So recording a payment
  immediately reduces an invoice's open balance and can flip it to Paid; the cash entry separately needs
  approval to affect the trial balance / statements. The UI must reflect both truths honestly.
- Existing reusable UI pieces: `currency-input` (no-spinner money field), whole-row click navigation,
  the persisted per-client customer selection in `ReceivablesService`, `extractProblem` error relay,
  `invoice-status-badge` / `settlement-badge`.

### Contracts (already in the backend)

```csharp
record RecordPaymentRequest(Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);
record Allocation(Guid TargetId, decimal Amount);   // TargetId = invoiceId
record Payment { Guid Id; Guid CustomerId; DateOnly Date; decimal Amount; string? Method; IReadOnlyList<Allocation> Allocations; bool Voided; /* Allocated, Unapplied derived */ }
```

Backend validation already enforced by `RecordPaymentAsync`: `Amount > 0`; every allocation `> 0`;
`Σ allocations ≤ Amount`; each target is an Issued invoice of this customer; `alreadyApplied + amount ≤ invoice.Total`.

## Scope

**In:** customer-level payment screen with oldest-first auto-allocation; "Record payment" entry points
on the invoice list and invoice detail; applied-payments list + void on the invoice detail; one new
`GET /payments?customerId=` backend endpoint; UI service/model additions; tests.

**Out (deferred):** multi-currency; editing a payment (correct via void + re-record); a standalone
Payments history nav/section; credit-application, refund, and write-off screens (separate future slices).

## Architecture

### 1. Backend — new read endpoint

`GET /clients/{clientId}/payments?customerId=<guid>`

- Handler `ListPayments` mirrors `ListInvoices`: returns `400` if `customerId` is null/empty; otherwise
  returns `Results.Ok(IReadOnlyList<Payment>)` from the existing `payments.GetPaymentsByCustomerAsync`.
- Includes voided payments (with `voided: true`) so the invoice detail can show full history and grey
  voided rows.
- Same Read authorization as the other module GETs (no module credential; the endpoint is read-only).
- Registered in `ReceivablesEndpoints.MapReceivablesEndpoints` alongside the other `/payments` routes.

**Decision — customer-scoped, not invoice-scoped (chosen: A).**
- **A. `GET /payments?customerId=` + client-side filter by allocation target** *(chosen)* — one endpoint,
  reuses the existing service method, and is reusable for a future payments-history screen. The invoice
  detail filters the result to payments having an allocation whose `targetId == invoiceId`.
- B. `GET /invoices/{id}/payments` (server-filtered) — narrower, a second special-purpose route, less reusable.
- C. Embed `appliedPayments` in `InvoiceView` — couples and bloats the read model (the list endpoint
  returns many views). Rejected.

### 2. UI model & service additions

`core/receivables/receivables.ts`:

```ts
export interface PaymentAllocation { targetId: string; amount: number; }
export interface Payment {
  id: string; customerId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[]; voided: boolean;
}
export interface RecordPaymentRequest {
  customerId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[];
}
```

`core/receivables/receivables.service.ts` (reuse the existing `base()` + `clientId` guards):

```ts
listPayments(customerId: string): Observable<Payment[]>          // GET  /payments?customerId=
recordPayment(req: RecordPaymentRequest): Observable<Payment>    // POST /payments
voidPayment(id: string, reason?: string | null): Observable<Payment> // POST /payments/{id}/void
creditBalance(customerId: string): Observable<number>           // GET  /customers/{id}/credit-balance → maps { creditBalance }
```

### 3. Payment screen — `PaymentEditor`

Route: `receivables/payments/new` (query params `customer` required, `invoice` optional). File
`features/receivables/payment-editor.ts`.

**Load:** read `customer` (redirect to `/receivables` if absent) and optional `invoice`. Fetch the
customer's open invoices via `listInvoices({ customerId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })`
(ascending invoice number ≈ oldest-first). Fetch `creditBalance(customerId)` for display. If launched with
`invoice=`, move that invoice to the front of the list.

**Form state (signals):**
- `amount: number` (currency-input). Default: if launched from an invoice → that invoice's open balance;
  else `0`.
- `date: string` default today (`new Date().toISOString().slice(0,10)`).
- `method: string | null` optional (free-text input for now).
- `rows`: one per open invoice `{ invoiceId, number, issueDate, openBalance, allocation: number }`.

**Auto-allocation:** a pure function `autoAllocate(amount, rows)` distributes `amount` oldest-first,
each row capped at its `openBalance`, returning the per-row allocations. Called when `amount` changes and
on initial load. Editing a row's allocation directly overrides that row (no re-spread) until `amount`
changes again. (Simple, predictable: amount-driven spread + manual per-row override.)

**Derived (computed):**
- `allocated = Σ rows.allocation`
- `unallocated = max(0, amount - allocated)` → shown as "→ customer credit"
- `valid = amount > 0 && every allocation ∈ [0, openBalance] && allocated ≤ amount`

**Submit:** `recordPayment({ customerId, date, amount, method: method || null, allocations: rows.filter(r => r.allocation > 0).map(r => ({ targetId: r.invoiceId, amount: r.allocation })) })`.
On 201 → `router.navigate(['/receivables'])` (the customer selection persists, so the list reloads for them).
On error → inline message via `extractProblem` (relays 422 validation / 409 / other 4xx).

**Honest note (static text):** "Recording a payment posts a cash entry that needs approval before it
affects the statements. The invoice's open balance updates immediately."

**Edge cases:**
- No open invoices → show "No open invoices for this customer." The amount (if > 0) is still recordable
  as pure customer credit (allocations empty).
- `amount` greater than the sum of open balances → excess stays in `unallocated` → credit (allowed).
- Allocation typed above a row's open balance → row invalid, submit disabled, inline hint.

### 4. Invoice detail — additions (`features/receivables/invoice-detail.ts`)

- **Record payment** button shown for `Issued` invoices (beside Void) → `routerLink`
  `/receivables/payments/new` with `queryParams { customer: v.invoice.customerId, invoice: id }`.
- **Applied payments** section: on load (when Issued), call `listPayments(customerId)` and filter to
  payments with an allocation `targetId === id`. For each, show date · amount applied to *this* invoice
  (the matching allocation's amount) · method · a **Void** button for non-voided payments (voided rows
  greyed, no button). Void → `voidPayment(payment.id)` then reload the invoice view **and** the payments
  list. Relay the backend 409 ("would drive customer credit negative") inline via `extractProblem`.
- Reloading after void updates the settlement badge and open balance.

### 5. Invoice list — addition (`features/receivables/invoice-list.ts`)

- **Record payment** button in the header (beside "New invoice"), `routerLink`
  `/receivables/payments/new` with `queryParams { customer: customerId() }`, disabled-styled
  (`pointer-events-none opacity-50`) when no customer is selected — same pattern as the New-invoice button.

### 6. Routing

Add `{ path: 'receivables/payments/new', loadComponent: () => import(...).then(m => m.PaymentEditor) }`
adjacent to the existing receivables routes (match the file's lazy-load style).

## Data flow

1. Invoice list (customer selected) **or** Issued invoice detail → **Record payment** → `PaymentEditor`
   with `customer` (+ optional `invoice`).
2. `PaymentEditor` loads open invoices + credit balance, auto-allocates the amount, user adjusts, submits.
3. `POST /payments` → backend validates, stores the payment, posts the cash entry (**PendingApproval**),
   returns 201. Invoice open balances drop immediately (settlement is document-driven).
4. Navigate to the invoice list for the customer → settlement badges reflect Paid / PartiallyPaid.
5. The pending cash entry appears in **Approvals**; an Approver approves it (existing flow) → it hits the
   trial balance / statements (Dr Cash / Cr A/R).
6. Correction: invoice detail → Applied payments → **Void** → `POST /payments/{id}/void` → invoice reopens;
   the void's ledger effect follows the existing reverse/withdraw rules.

## Error handling

- All write calls relay the backend problem detail inline via `extractProblem` (422 validation, 409 void
  guard, other 4xx). No raw 500s surfaced as success.
- `PaymentEditor` redirects to `/receivables` if reached without a `customer` query param.
- Missing `customerId` on `GET /payments` → backend `400`; the UI never calls it without one.

## Testing

**Backend** (`Accounting101.Receivables.Tests`): `GET /payments?customerId=` returns the customer's
payments (including a voided one flagged); `400` when `customerId` is absent; respects Read auth.

**Frontend:**
- `payment-editor.spec.ts`: auto-allocates oldest-first capped at open balances; manual row edit overrides;
  `unallocated → credit` readout; submit posts the expected `{ allocations }` payload (rows with 0 dropped);
  validation disables submit (amount ≤ 0, allocation > open balance, allocated > amount); error relay on
  422; redirect when `customer` param missing; launched-from-invoice prefires amount + ordering.
- `invoice-detail.spec.ts`: Record-payment link present for Issued with correct queryParams; applied-payments
  list filters to this invoice; Void posts and reloads invoice + payments; 409 relayed inline.
- `invoice-list.spec.ts`: Record-payment button disabled with no customer, enabled + correct route with one.

Reuse `currency-input`, whole-row click, persisted customer selection, and existing badges.

## Files touched

- `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` — add `ListPayments` + route.
- `Modules/Receivables/Accounting101.Receivables.Tests/...` — endpoint test.
- `UI/Angular/src/app/core/receivables/receivables.ts` — `Payment`, `PaymentAllocation`, `RecordPaymentRequest`.
- `UI/Angular/src/app/core/receivables/receivables.service.ts` — `listPayments` / `recordPayment` / `voidPayment` / `creditBalance`.
- `UI/Angular/src/app/features/receivables/payment-editor.ts` (+ `.spec.ts`) — new screen.
- `UI/Angular/src/app/features/receivables/invoice-detail.ts` (+ `.spec.ts`) — record button + applied-payments/void.
- `UI/Angular/src/app/features/receivables/invoice-list.ts` (+ `.spec.ts`) — record button.
- Routes file — `receivables/payments/new`.
