# Receivables Refunds Area (Slice C) — Design

**Date:** 2026-06-30
**Status:** Approved (design)
**Area:** `UI/Angular` (Receivables feature) + one backend read endpoint + memo surfacing (`Modules/Receivables`)

## Goal

Add the **Refunds** capability to the Receivables hub: record and list **refunds** — cash paid back to a customer against their unapplied credit balance — through a new **Refunds** tab (5th tab), following the same list-home + record-form pattern as Payments and Credits. A refund is the cash-out mirror of "apply credit": both draw down the customer's credit balance, but a refund pays cash instead of reducing an invoice.

## Background / current state

- The Receivables hub (`ReceivablesShell`) has tabs Invoices · Payments · Customers · Credits (Slice B). This slice adds a 5th tab, **Refunds**.
- The backend write paths and store reads already exist:
  - `POST /clients/{id}/refunds` (`RecordRefundAsync`) and `POST /clients/{id}/refunds/{id}/void` (`VoidRefundAsync`).
  - `RefundBody(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo)`; request contract `RefundRequest(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo)`.
  - Domain `Refund` record (`Disposition.cs`): `Id, CustomerId, Date, Amount, Voided` — **amount-based, no allocations**. Note: it currently has **no `Memo`** field even though `RefundBody.Memo` is persisted (same memo-drop as the Slice B dispositions).
  - Store read `GetRefundsByCustomerAsync` exists (used inside `GetCustomerCreditBalanceAsync`); `GetRefundAsync` (single) and `VoidRefundAsync` exist.
- **There is no GET endpoint for refunds** (only `POST /refunds` + its void). A Refunds list needs a new read endpoint — exactly the gap Slice B filled for credits.
- Backend validation (`PaymentService.RecordRefundAsync`): `Amount > 0`; `Amount ≤ GetCustomerCreditBalanceAsync(customer)` (else `InvalidOperationException` → 422 at the endpoint); posts **one** balanced entry (`PaymentPosting.ComposeRefund`: Dr Customer Credits / Cr Cash) that lands **PendingApproval**.
- Credit balance = non-voided payment remainders − non-voided credit-applications − non-voided refunds. So recording a refund **reduces available credit immediately**; voiding one **restores** it.
- Refunds **have a real void endpoint** (unlike credit-applications), so no type-narrowing dance is needed — `voidRefund(id)` is unconditional.
- Reusable UI: `<app-customer-select>` (persisted selection), `currency-input`, `extractProblem`, `ReceivablesService.creditBalance`, the shell tab pattern, `money`/`displayDate`, and the `PaymentEditor`/`CreditList` patterns to mirror.

## Scope

**In:** a `GET /refunds?customerId=` read endpoint; surface `Refund.Memo`; a Refunds tab (`RefundList` home + `RefundEditor` form); per-doc void; UI model/service; tests.

**Out (deferred):** a customer-credit overview / "who has credit" screen; partial-credit analytics; any contextual refund deep-link. Refund is recorded only from the Refunds tab (matching the one-place pattern).

## Architecture

### 1. Backend — `GET /clients/{id}/refunds?customerId=` + surface memo

- New handler `ListRefunds` mirroring `ListPayments`/`ListCredits`: returns `400` (Problem) when `customerId` is null/empty; otherwise `200` with `IReadOnlyList<Refund>` ordered **date-descending**.
- New `PaymentService.GetRefundsByCustomerAsync(clientId, customerId, ct)` passthrough → `payments.GetRefundsByCustomerAsync(...)`, ordered `OrderByDescending(r => r.Date)`.
- **Surface memo:** add `public string? Memo { get; init; }` to the `Refund` domain record (`Disposition.cs`), placed after `Amount`, before `Voided`; populate it in `MapRefund` (`DocumentPaymentStore.cs`) from `r.Body.Memo`. Additive/init-only/default-null — backward-compatible (the existing `VoidRefundAsync` return path and any consumer just gain an optional field).
- Same Read authorization as the other module GETs (under the existing `RequireAuthorization()` `clients` group; no module credential).
- Registered next to `clients.MapPost("/refunds", ...)` in `MapReceivablesEndpoints`.

**Decision — return the domain `Refund` directly (chosen):** like `ListPayments` returns `Payment`, `ListRefunds` returns the domain `Refund`. No DTO needed (unlike Slice B's `CreditDocument`, which unified three types). Rejected: a `RefundView`/wrapper (no extra computed fields to add — open balance / settlement don't apply to a refund).

### 2. UI model & service additions

`core/receivables/receivables.ts`:
```ts
export interface Refund { id: string; customerId: string; date: string; amount: number; memo: string | null; voided: boolean; }
export interface RefundRequest { customerId: string; date: string; amount: number; memo: string | null; }
```

`core/receivables/receivables.service.ts` (reuse `base()` + `clientId` guards):
```ts
listRefunds(customerId: string): Observable<Refund[]>     // GET  /refunds?customerId=
recordRefund(req: RefundRequest): Observable<unknown>     // POST /refunds
voidRefund(id: string, reason?: string | null): Observable<unknown>  // POST /refunds/{id}/void
```

### 3. Refunds tab (route + shell)

- Add a 5th tab to `ReceivablesShell`: **Refunds** → `routerLink="refunds"`, `data-testid="tab-refunds"` (markup identical to the other tabs).
- Routes under `receivables`: `{ path: 'refunds', component: RefundList }`, `{ path: 'refunds/new', component: RefundEditor }`.

### 4. `RefundList` (the Refunds home)

`features/receivables/refund-list.ts` — mirrors `CreditList`:
- `<app-customer-select>` + **Issue refund** button (`routerLink="/receivables/refunds/new"`, `[queryParams]="{ customer: customerId() }"`, disabled-styled when no customer).
- Reactive load `combineLatest([toObservable(customerId), refresh$]) → switchMap(cid ? listRefunds(cid) : of([])) → toSignal`, with `extractProblem`→`listError`. (`refresh$` a `BehaviorSubject(0)` so a void reloads synchronously, matching the Slice B `CreditList` design.)
- Table: **Date · Amount · Memo · Status**. Voided rows greyed (`opacity-50`) + "Voided"; a **Void** button on every non-voided refund (`@if (!r.voided)`) → `voidRefund(r.id)` → `refresh$.next()`; inline 4xx relay. (Use `doVoid` as the method name — `void` is reserved as the JS operator in Angular template expressions, the Slice B gotcha.)
- Empty states: no customers → "add one first"; none selected → "Select a customer to view refunds."; none → "No refunds recorded."
- Rows not clickable (no refund-detail screen this slice).

### 5. `RefundEditor` (the form)

`features/receivables/refund-editor.ts`. Route `refunds/new?customer=<id>` (redirect to `/receivables/refunds` if `customer` absent). Mirrors `PaymentEditor` but **amount-only** (no allocation rows):

**Form state (signals):** `amount` (number), `date` (today), `memo` (string), `creditBalance` (loaded).

**Load:** read `customer` (redirect if absent); `creditBalance(customer)` → set `creditBalance`, and **default `amount` to the full available credit** (refund-the-whole-balance is the common case; editable, capped).

**Interaction:** amount via `<app-currency-input>`; always show **"Available credit `{{ money(creditBalance()) }}`"** (the cap), turned destructive when `amount > creditBalance`.

**Validation (`valid`):** `amount > 0 && amount ≤ creditBalance`.

**Submit:** `recordRefund({ customerId, date, amount, memo: memo || null })` → on success `router.navigate(['/receivables/refunds'])`. Inline `extractProblem` relay (the over-available-credit 422; any engine 4xx).

**Honest note (static):** "Issuing a refund posts a cash entry that needs approval before it affects the statements. The customer's credit balance updates immediately."

**Edge case:** `creditBalance = 0` → `amount` defaults to 0, `valid` stays false, the cap message shows — the user sees there is no credit to refund.

### 6. Dedupe / consistency

Reuses `<app-customer-select>` and the persisted selection (the chosen customer carries across all five tabs). No new customer-select. No contextual entry points — refunds are recorded from the Refunds tab only. No `method` field (refunds carry none, unlike payments).

## Data flow

1. Receivables → **Refunds** tab → pick customer → `GET /refunds?customerId=` → list.
2. **Issue refund** → `RefundEditor` → amount (defaults to full credit) → submit → `POST /refunds` (PendingApproval; credit balance drops immediately) → back to the Refunds list.
3. The pending entry appears in **Approvals**; approval posts it to the GL (Dr Customer Credits / Cr Cash).
4. Correction: Refunds list → **Void** → `POST /refunds/{id}/void` → reload. Voiding restores the customer's credit (always safe — increasing credit can't drive a balance negative).

## Error handling

- Writes relay the backend problem detail inline via `extractProblem` (the over-available-credit 422; any engine 4xx).
- `RefundEditor` redirects to `/receivables/refunds` if reached without `customer`.
- `GET /refunds` without `customerId` → backend 400; the UI never calls it without one.

## Testing

**Backend** (`Accounting101.Receivables.Tests`): `GET /refunds?customerId=` returns a date-desc list with `Amount` + surfaced `Memo` + `Voided` reflected; `400` when `customerId` absent. (Refund records require available credit — create it by overpaying an invoice, then refunding.)

**Frontend:**
- `receivables.service.spec.ts`: `listRefunds` GETs `/refunds?customerId=`; `recordRefund` POSTs the body; `voidRefund` POSTs `{ reason }` to `/refunds/{id}/void`.
- `refund-list.spec.ts`: loads refunds for the selected customer; Void shows on non-voided rows and posts to the right path + reloads; the three empty states; Issue-refund link queryParam + disabled state.
- `refund-editor.spec.ts`: redirect without `customer`; amount defaults to the loaded available credit; amount capped at available credit (invalid above it); `creditBalance = 0` → invalid; submit posts the correct payload; error relay on 422.

Reuses `<app-customer-select>`, `currency-input`, `money`/`displayDate`, persisted selection.

## Files touched

- `Modules/Receivables/Accounting101.Receivables/Disposition.cs` — add `Memo` to `Refund`.
- `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs` — populate `Memo` in `MapRefund`.
- `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` — `GetRefundsByCustomerAsync` passthrough.
- `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` — `ListRefunds` + route.
- `Modules/Receivables/Accounting101.Receivables.Tests/…` — endpoint test.
- `UI/Angular/src/app/core/receivables/receivables.ts` — `Refund`, `RefundRequest`.
- `UI/Angular/src/app/core/receivables/receivables.service.ts` — `listRefunds`/`recordRefund`/`voidRefund`.
- `UI/Angular/src/app/features/receivables/refund-list.ts` (+ `.spec.ts`).
- `UI/Angular/src/app/features/receivables/refund-editor.ts` (+ `.spec.ts`).
- `UI/Angular/src/app/features/receivables/receivables-shell.ts` (+ `.spec.ts`) — add the Refunds tab.
- `UI/Angular/src/app/app.routes.ts` — `refunds` + `refunds/new` routes.
