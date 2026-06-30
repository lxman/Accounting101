# Receivables Credits Area (Slice B) — Design

**Date:** 2026-06-30
**Status:** Approved (design); spec pending user review
**Area:** `UI/Angular` (Receivables feature) + one backend read endpoint (`Modules/Receivables`)

## Goal

Add the **Credits** capability to the Receivables hub: record the three allocation-based dispositions that clear or reduce an invoice's open balance — **credit note** (contra-revenue), **write-off** (bad debt), and **apply credit** (draw the customer's existing credit) — through one unified "Adjust" form, and list/void them from a new Credits tab. This is **Slice B**, built inside the hub frame shipped in Slice A.

## Background / current state

- The Receivables hub (`ReceivablesShell`) has tabs Invoices · Payments · Customers; this slice adds a 4th tab, **Credits**, following the same list-home + record-form pattern.
- The backend has all three write paths and their store reads already:
  - `POST /clients/{id}/credit-notes` (+ `…/{id}/void`), `RecordCreditNoteAsync`, `CreditNote` (fields: `Id, CustomerId, Date, Allocations, Voided`, computed `Total = Σ allocations`).
  - `POST /clients/{id}/write-offs` (+ `…/{id}/void`), `RecordWriteOffAsync`, `WriteOff` (same shape, `Total`).
  - `POST /clients/{id}/credit-applications` (**no void endpoint**), `RecordCreditApplicationAsync`, `CreditApplication` (fields: `Id, CustomerId, Date, Allocations, Voided`, computed `Applied = Σ allocations`).
  - Store reads exist for all three: `GetCreditNotesByCustomerAsync`, `GetWriteOffsByCustomerAsync`, `GetCreditApplicationsByCustomerAsync`.
- **There is no GET endpoint for any disposition** (only `GET /payments` exists). A Credits list needs a new read endpoint.
- **`credit-applications` has no void** — confirmed in the route map. The Credits list shows credit-applications read-only; voiding one would need a new backend endpoint (deferred). Credit-notes and write-offs have void.
- Backend validation (all three, in `PaymentService`): allocations non-empty and each `> 0`; each target an **Issued** invoice of this customer with `alreadyApplied + amount ≤ invoice.Total`; each posts one balanced entry that lands **PendingApproval**. Apply-credit additionally requires `Σ allocations ≤ GetCustomerCreditBalanceAsync(customer)`.
- Request contracts (existing):
  - `CreditNoteRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo)`
  - `WriteOffRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo)`
  - `CreditApplicationRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations)` — **no memo**.
  - `Allocation(Guid TargetId, decimal Amount)`.
- Settlement is **document-driven**: recording any of these immediately reduces the targeted invoice's open balance; the posted GL entry separately needs approval (existing Approvals flow) to hit the statements. Same two-track model as invoices/payments; the form says so.
- Reusable UI: `<app-customer-select>` (persisted selection), `currency-input`, `extractProblem`, `ReceivablesService.listInvoices`/`creditBalance`, the shell tab pattern, `money`/`displayDate`.

## Scope

**In:** a unified `GET /credits?customerId=` read endpoint; a Credits tab (`CreditList` home + `AdjustmentEditor` form); per-doc void for credit-notes/write-offs; UI service/model; tests.

**Out (deferred):** refund (cash-out vs credit — different shape); AR aging / customer statement; **credit-application void** (no backend endpoint); any contextual "Adjust" deep-link from the invoice detail (one place: the Credits tab, matching the payment decision).

## Architecture

### 1. Backend — `GET /clients/{id}/credits?customerId=`

- A new handler `ListCredits` mirroring `ListPayments`: returns `400` when `customerId` is null/empty; otherwise `200` with `IReadOnlyList<CreditDocument>`.
- A new unified DTO (in `Accounting101.Receivables.Api`, the wire layer):
  ```csharp
  public sealed record CreditDocument(
      string Type,            // "credit-note" | "write-off" | "credit-application"
      Guid Id, Guid CustomerId, DateOnly Date,
      decimal Amount,         // Σ allocations
      string? Memo,           // null for credit-application
      IReadOnlyList<Allocation> Allocations,
      bool Voided);
  ```
- A `PaymentService.GetCreditsByCustomerAsync(clientId, customerId, ct)` passthrough that calls the three store reads, maps each to `CreditDocument` with its `Type` tag (`Memo` null for credit-applications; `Amount` from `Total`/`Applied`), concatenates, and orders by `Date` descending.
- Same Read authorization as the other module GETs (read-only; no module credential).
- Registered next to the other `/credits`-adjacent routes in `MapReceivablesEndpoints`.

**Decision — one unified endpoint (chosen):** a single `GET /credits` with a `Type` discriminator gives the list one sorted result and one UI call. Rejected: three separate GETs merged client-side (more round-trips, client-side sorting/merging) and embedding credits in another read model (coupling).

### 2. UI model & service additions

`core/receivables/receivables.ts`:
```ts
export type CreditType = 'credit-note' | 'write-off' | 'credit-application';
export interface CreditDocument {
  type: CreditType; id: string; customerId: string; date: string;
  amount: number; memo: string | null; allocations: PaymentAllocation[]; voided: boolean;
}
export interface CreditNoteRequest    { customerId: string; date: string; allocations: PaymentAllocation[]; memo: string | null; }
export interface WriteOffRequest      { customerId: string; date: string; allocations: PaymentAllocation[]; memo: string | null; }
export interface CreditApplyRequest   { customerId: string; date: string; allocations: PaymentAllocation[]; }
```
(`PaymentAllocation { targetId; amount }` already exists.)

`core/receivables/receivables.service.ts` (reuse `base()` + `clientId` guards):
```ts
listCredits(customerId: string): Observable<CreditDocument[]>      // GET  /credits?customerId=
recordCreditNote(req: CreditNoteRequest): Observable<unknown>      // POST /credit-notes
recordWriteOff(req: WriteOffRequest): Observable<unknown>          // POST /write-offs
applyCredit(req: CreditApplyRequest): Observable<unknown>          // POST /credit-applications
voidCredit(type: CreditType, id: string, reason?: string | null): Observable<unknown>
  // POST /credit-notes/{id}/void | /write-offs/{id}/void  (credit-application: not callable — no endpoint)
```
`voidCredit` maps `type` → the `/credit-notes` or `/write-offs` path; it is never called for `credit-application` (the list hides the Void button for that type).

### 3. Credits tab (route + shell)

- Add a 4th tab to `ReceivablesShell`: **Credits** → `routerLink="credits"`, `data-testid="tab-credits"` (same markup as the others).
- Routes under `receivables`: `{ path: 'credits', component: CreditList }`, `{ path: 'credits/new', component: AdjustmentEditor }`.

### 4. `CreditList` (the Credits home)

`features/receivables/credit-list.ts` — mirrors `PaymentList`:
- `<app-customer-select>` + **Record adjustment** button (`routerLink="/receivables/credits/new"`, `[queryParams]="{ customer: customerId() }"`, disabled-styled when no customer).
- Reactive load `toObservable(customerId) → switchMap(cid ? listCredits(cid) : of([])) → toSignal`, with `extractProblem`→`listError`.
- Table: **Date · Type · Amount · Memo · Status**. `Type` shown as a readable label (Credit note / Write-off / Apply credit). Voided rows greyed + "Voided". A **Void** button on non-voided credit-notes and write-offs only (`@if (c.type !== 'credit-application' && !c.voided)`); credit-applications show no Void (with a muted "—"). Void → `voidCredit(c.type, c.id)` → reload; inline 4xx relay.
- Empty states: no customers → "add one first"; none selected → "Select a customer to view credits."; none → "No credits recorded."
- Rows not clickable (no credit-detail screen this slice).

### 5. `AdjustmentEditor` (the unified Adjust form)

`features/receivables/adjustment-editor.ts`. Route `credits/new?customer=<id>` (redirect to `/receivables/credits` if `customer` absent).

**Form state (signals):**
- `type: CreditType` (default `'credit-note'`), set by a radio group (Credit note · Write-off · Apply credit).
- `date` (today), `memo` (string).
- `rows: AdjustRow[]` — one per open invoice: `{ invoiceId, number, issueDate, openBalance, included: boolean, amount: number }`.
- `creditBalance` (loaded for apply-credit display/cap).

**Load:** read `customer` (redirect if absent); `listInvoices({ customerId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })` → rows (all `included:false`, `amount:0`); `creditBalance(customer)`.

**Interaction:**
- Ticking a row sets `included:true` and `amount = openBalance`; unticking sets `included:false, amount:0`. The amount input is editable when included and capped at `openBalance` (never above; min > 0 to count).
- `total = Σ rows where included (amount)`.
- **Type-specific:** memo input shown for `credit-note`/`write-off`, hidden for `credit-application`. For `credit-application`, show **Available credit `{{ money(creditBalance()) }}`** and include it in validation.

**Validation (`valid`):** at least one included row with `amount > 0`; every included row `0 < amount ≤ openBalance`; for `credit-application`, `total ≤ creditBalance`.

**Submit:** build `allocations = rows.filter(r => r.included && r.amount > 0).map(r => ({ targetId: r.invoiceId, amount: r.amount }))`, then dispatch by type:
- `credit-note` → `recordCreditNote({ customerId, date, allocations, memo: memo || null })`
- `write-off` → `recordWriteOff({ customerId, date, allocations, memo: memo || null })`
- `credit-application` → `applyCredit({ customerId, date, allocations })`
On success → `router.navigate(['/receivables/credits'])`. Inline `extractProblem` relay (422 validation / 4xx — e.g. apply-credit exceeding available credit returns the backend message).

**Honest note (static):** "Recording an adjustment posts an entry that needs approval before it affects the statements. The invoice's open balance updates immediately."

**Edge cases:** no open invoices → "No open invoices to adjust."; apply-credit with `creditBalance = 0` → rows can be ticked but `valid` stays false (total would exceed 0) and the inline cap message shows — the user sees they have no credit to apply.

### 6. Dedupe / consistency

Reuses `<app-customer-select>` and the persisted selection (the chosen customer carries across all four tabs). No new customer-select. No contextual entry points — Credits is recorded from the Credits tab only.

## Data flow

1. Receivables → **Credits** tab → pick customer → `GET /credits?customerId=` → list.
2. **Record adjustment** → `AdjustmentEditor` → choose type, tick invoices, submit → `POST /credit-notes|/write-offs|/credit-applications` (PendingApproval; invoice open balances drop immediately) → back to the Credits list.
3. The pending entry appears in **Approvals**; approval posts it to the GL (Dr Sales Returns | Bad Debt | Customer Credits / Cr A/R).
4. Correction: Credits list → **Void** (credit-note / write-off) → `POST /{type}s/{id}/void` → reload. (Credit-application void unavailable — deferred.)

## Error handling

- All writes relay the backend problem detail inline via `extractProblem` (422 validation; the apply-credit over-available-credit 422; the per-invoice over-open-balance 422).
- `AdjustmentEditor` redirects to `/receivables/credits` if reached without `customer`.
- `GET /credits` without `customerId` → backend 400; the UI never calls it without one.

## Testing

**Backend** (`Accounting101.Receivables.Tests`): `GET /credits?customerId=` returns a unified, date-desc list spanning a credit-note + write-off + credit-application (correct `type`, `amount` = Σ allocations, `memo` null for credit-application, `voided` reflected); `400` when `customerId` absent.

**Frontend:**
- `credit-list.spec.ts`: loads credits for the selected customer; Void shows for credit-note/write-off and is absent for credit-application; void posts to the right path and reloads; the three empty states; Record-adjustment link queryParam + disabled state.
- `adjustment-editor.spec.ts`: redirect without `customer`; ticking a row fills its open balance and counts toward total, unticking clears it; amount capped at open balance; memo hidden for apply-credit and shown otherwise; apply-credit caps total at available credit (invalid above it); submit posts the correct payload to the correct endpoint per type (3 cases); error relay on 422.

Reuses `<app-customer-select>`, `currency-input`, `money`/`displayDate`, persisted selection.

## Files touched

- `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` — `ListCredits` + route.
- `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesRequests.cs` (or a small new file) — `CreditDocument` DTO.
- `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` — `GetCreditsByCustomerAsync` passthrough/mapper.
- `Modules/Receivables/Accounting101.Receivables.Tests/…` — endpoint test.
- `UI/Angular/src/app/core/receivables/receivables.ts` — `CreditType`, `CreditDocument`, the three request types.
- `UI/Angular/src/app/core/receivables/receivables.service.ts` — `listCredits`/`recordCreditNote`/`recordWriteOff`/`applyCredit`/`voidCredit`.
- `UI/Angular/src/app/features/receivables/credit-list.ts` (+ `.spec.ts`).
- `UI/Angular/src/app/features/receivables/adjustment-editor.ts` (+ `.spec.ts`).
- `UI/Angular/src/app/features/receivables/receivables-shell.ts` (+ `.spec.ts`) — add the Credits tab.
- `UI/Angular/src/app/app.routes.ts` — `credits` + `credits/new` routes.
