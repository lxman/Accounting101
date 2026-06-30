# Payables Vendor Credits UI — Slice P-C

**Date:** 2026-06-30
**Status:** Approved design, pre-implementation
**Builds on:** Payables payments P-B (`accounting101-ui-payables-payments`)
**Loosely mirrors:** Receivables credits area (`accounting101-ui-credits-area`), but much narrower

## Goal

Close the payments↔credits loop in the Payables UI: a **Credits** tab showing a vendor's
available credit balance and a read-only list of credit applications, plus an
**Apply-credit editor** that allocates the available vendor credit across the vendor's open
bills. One small backend read endpoint completes it.

## Why this is narrower than AR's Credits area

In Payables, vendor credit has exactly one source and one sink:
- **Created** by bill-payment **overpayment** (the unapplied remainder of a `BillPayment`),
  which already surfaces on the Payments tab.
- **Consumed** by `POST /vendor-credit-applications` (apply existing credit to bills).

There are **no vendor credit-notes, no write-offs**, and credit applications have **no void
endpoint**. So unlike AR's Credits tab (which listed credit-notes + write-offs + applications
via a unified `CreditDocument`, with void on the first two), the Payables credits list is
**single-type and read-only**, and there is no multi-mode adjustment editor — just a focused
apply-credit editor.

## Backend (one endpoint — no store change)

`POST /vendor-credit-applications` and `GET /vendors/{id}/credit-balance` already exist. The
store already exposes `IBillPaymentStore.GetCreditApplicationsByVendorAsync`. The only gap is
a read endpoint:

1. `GET /clients/{clientId}/vendor-credit-applications?vendorId={guid}` →
   `200 IReadOnlyList<VendorCreditApplication>`; `400` if `vendorId` missing/empty;
   client-isolated. Handler injects `IBillPaymentStore` and returns
   `GetCreditApplicationsByVendorAsync(...)` — exact mirror of P-B's `GET /bill-payments`.
2. Tests (mirror `BillPaymentListEndpointTests`): returns a vendor's credit applications
   (record one via the existing flow, then GET), 400 without `vendorId`, client isolation.

The `VendorCreditApplication` domain type is `{ Id, VendorId, Date, Allocations, Voided }`
plus a computed `Applied = Σ Allocations.Amount` (serializes to camelCase).

## Frontend

### Core (`core/payables/payables.ts` — additive)

- `VendorCreditApplication { id: string; vendorId: string; date: string; allocations: PaymentAllocation[]; voided: boolean }`
- `ApplyVendorCreditRequest { vendorId: string; date: string; allocations: PaymentAllocation[] }`

(`PaymentAllocation` and `AllocRow`/`autoAllocate` already exist from P-B and are reused.)

### Service (`core/payables/payables.service.ts` — additive)

- `listVendorCreditApplications(vendorId): Observable<VendorCreditApplication[]>` →
  `GET /vendor-credit-applications?vendorId`
- `applyVendorCredit(req: ApplyVendorCreditRequest): Observable<VendorCreditApplication>` →
  `POST /vendor-credit-applications`

(`vendorCreditBalance(vendorId)` already exists from P-B.) Both guard on a selected client
(`EMPTY` otherwise), mirror of the existing methods.

### Shell + routes

- `payables-shell.ts` — add a **Credits** tab; order **Bills | Payments | Vendors | Credits**
  (mirrors AR's Invoices | Payments | Customers | Credits — Credits last). Default route
  stays `bills`.
- `app.routes.ts` — under the `payables` block add `credits` → VendorCreditList and
  `credits/new` → VendorCreditApplyEditor (after `vendors`).

### Components (`features/payables/`)

- **`vendor-credit-list.ts`** (`app-vendor-credit-list`) — vendor-scoped (`<app-vendor-select>`).
  Header shows **Available credit: $X** (`vendorCreditBalance`). Lists the vendor's credit
  applications: date, total applied (`Σ allocations`), number of bills. **Read-only — no void**
  (backend has none). An **"Apply credit"** button → `/payables/credits/new?vendor=<id>`,
  disabled (pointer-events-none/opacity-50) when no vendor selected **or** available credit is 0.
  Reactive list via `toObservable(selectedVendorId) → switchMap (of([]) when no vendor) →
  toSignal`, plus a separate fetch of the credit balance for the header.
- **`vendor-credit-apply-editor.ts`** (`app-vendor-credit-apply-editor`) — vendor-level. Reads
  `?vendor=` (required → redirect `/payables` if absent). Loads the available credit balance
  and the vendor's open bills (`listBills` settlement=open, oldest-first). One `AllocRow` per
  bill; on load, `autoAllocate(availableCredit, rows)` fills oldest-first capped per bill.
  Editable per row. **No cash-amount field** — the pool is the fixed available credit.
  Live readout: Allocated + Remaining credit (available − allocated). `valid` = allocated > 0
  && every allocation in `[0, openBalance]` && allocated ≤ availableCredit. Save →
  `applyVendorCredit({ vendorId, date, allocations: rows.filter(>0).map(targetId=billId) })`
  → navigate `/payables/credits`. `takeUntilDestroyed` on every subscription. Helper copy:
  applying credit posts an entry that needs approval before it affects the statements; the
  bill's open balance updates immediately (consistent with P-B; verify at implementation).

## Testing

Per-file `.spec.ts` mirroring the P-B specs:
- list: renders the available-credit header + a vendor's credit applications; Apply-credit
  button disabled when credit is 0;
- editor: auto-allocates available credit oldest-first, `valid` blocks over-allocation beyond
  available credit, POST body shape (vendorId/date/allocations with targetId=billId) +
  navigation.
Backend xUnit for the new endpoint. Full UI suite + payables backend suite green, tsc clean,
before merge.

## Deferred (unchanged)

Vendor **account 360** (incl. the credit-activity running-balance ledger — overpayment
sources + applications), bill draft **edit/discard**.

## Decisions taken (not asked)

- **Apply-credit editor has no amount field** — the allocatable pool is the vendor's available
  credit balance; the user only chooses how much of it to apply to which bills.
- **Credits list is read-only** — credit applications have no void endpoint; mirroring AR's
  treatment of `credit-application` (which AR also excluded from void).

## Commit trailer

```
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
```
