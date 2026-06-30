# Payables Payments UI — Slice P-B

**Date:** 2026-06-30
**Status:** Approved design, pre-implementation
**Mirrors:** Receivables AR-payments slice (`accounting101-ui-ar-payments`)
**Builds on:** Payables foundation P-A (`accounting101-ui-payables-foundation`)

## Goal

Add vendor payments to the Payables module UI as a faithful mirror of the shipped
AR-payments slice: a **Payments** tab with a vendor-scoped payment list, a vendor-level
**BillPaymentEditor** (record cash against open bills, oldest-first auto-allocate,
overpayment → vendor credit), and an **Applied payments** section on BillDetail (void a
payment from the bill it settled). Plus the one backend endpoint the UI needs to list
payments.

## Backend (one endpoint — no store change)

The payables payment backend already exists and mirrors AR: `POST /bill-payments`
(vendor-level, allocations across bills, overpay → vendor credit, allocation ≤ bill open
balance, allocations ≤ amount), `POST /bill-payments/{id}/void` (blocked 409 if the void
would drive vendor credit negative). The payment store already exposes
`IBillPaymentStore.GetPaymentsByVendorAsync`. The only gap is a read endpoint:

1. `GET /clients/{clientId}/bill-payments?vendorId={guid}` → `200 IReadOnlyList<BillPayment>`.
   `vendorId` required (400 if missing/empty), mirror of receivables `GET /payments`. The
   handler injects `IBillPaymentStore` and returns `GetPaymentsByVendorAsync(...)` — no
   service or store change.
2. Tests (mirror `VendorListEndpointTests` / the AR payments endpoint test): returns a
   vendor's payments, 400 when `vendorId` omitted, client isolation.

No new posting accounts are required — the dev stack's `Payables__Accounts__*` block
(Payable/Cash/VendorCredits) is already wired (`accounting101-devstack-module-config`).

## Frontend

### Core (`core/payables/payables.ts` — additive)

- `PaymentAllocation { targetId: string; amount: number }`
- `BillPayment { id; vendorId; date; amount; method: string | null; allocations: PaymentAllocation[]; voided: boolean }`
- `RecordBillPaymentRequest { vendorId; date; amount; method: string | null; allocations: PaymentAllocation[] }`
- `AllocRow { billId: string; number: string | null; billDate: string; openBalance: number; allocation: number }`
- `autoAllocate(amount: number, rows: readonly AllocRow[]): AllocRow[]` — pure oldest-first
  fill (mirror of the receivables helper, kept in-module to avoid coupling): walks rows in
  order, assigning `min(remaining, openBalance)` to each, rest unallocated.

### Service (`core/payables/payables.service.ts` — additive)

- `listBillPayments(vendorId): Observable<BillPayment[]>` → `GET /bill-payments?vendorId`
- `recordBillPayment(req): Observable<BillPayment>` → `POST /bill-payments`
- `voidBillPayment(id, reason?): Observable<BillPayment>` → `POST /bill-payments/{id}/void`
  (body `{ reason: reason ?? null }`)
- `vendorCreditBalance(vendorId): Observable<number>` → `GET /vendors/{id}/credit-balance`
  (endpoint already exists; maps `{ creditBalance }`)

All guard on a selected client (`EMPTY` otherwise), mirror of the existing service methods.

### Shell + routes

- `payables-shell.ts` — add a **Payments** tab; order tabs **Bills | Payments | Vendors**
  (mirrors AR's Invoices | Payments | Customers). Default route stays `bills`.
- `app.routes.ts` — under the `payables` block add `payments` → BillPaymentList and
  `payments/new` → BillPaymentEditor (mirror of `payments` / `payments/new` under
  receivables).

### Components (`features/payables/`)

- **`bill-payment-list.ts`** (`app-bill-payment-list`) — mirror of AR `PaymentList`.
  `<app-vendor-select>` at top; when a vendor is selected, list its payments (date, amount,
  method, allocated, unapplied, Voided badge); each non-voided payment has **Void**.
  "Record payment" button → `/payables/payments/new`. Empty/loading/error states. Selection
  comes from the service's persisted `selectedVendorId`.
- **`bill-payment-editor.ts`** (`app-bill-payment-editor`) — mirror of AR `PaymentEditor`.
  Reads `?vendor=` (and optional `?bill=` to focus one bill). Header: amount / date /
  method. Loads the vendor's open bills (`listBills` settlement=open, oldest-first), one
  `AllocRow` per bill with an editable "Apply" amount; `autoAllocate` fills oldest-first
  when the amount changes. Live readout: Allocated + "Unallocated → vendor credit"; shows
  existing vendor credit if > 0. `valid` = amount > 0 && every allocation in `[0, openBalance]`
  && allocated ≤ amount. Save → `recordBillPayment` (allocations filter > 0) → navigate to
  `/payables/payments`. `takeUntilDestroyed` on every subscription.
- **`bill-detail.ts`** (modify) — when the bill is **Entered**, add an **Applied payments**
  section (mirror of invoice-detail): the vendor's payments whose allocations target this
  bill, showing the amount applied *here*, date, method; each non-voided one has **Void**,
  which calls `voidBillPayment` and reloads the bill. Loads payments via
  `listBillPayments(bill.vendorId)`.

### Posting behavior

Bill payments post under the Payables module credential (like bill enter/void). At
implementation, confirm whether the cash entry posts immediately or pending-approval and
set the editor's helper copy to match — do **not** copy AR's "needs approval" line
verbatim without verifying it against the payables payment recipe.

## Testing

Per-file `.spec.ts` mirroring the AR payment specs:
- editor: auto-allocate on amount change, overpay → unallocated-to-credit readout, record
  POST body shape (vendorId/date/amount/method/allocations), navigation;
- list: renders a vendor's payments, Void posts to `/bill-payments/{id}/void` and refreshes;
- bill-detail: applied-payments section lists payments touching the bill and Void calls the
  service + reloads.
Backend xUnit for the new endpoint. Full UI suite + payables backend suite green, tsc clean,
before merge.

## Deferred (unchanged from P-A)

Vendor **credits** management UI (apply/list — needs `GET /vendor-credit-applications`),
vendor **account 360**, bill draft **edit/discard**.

## Commit trailer

```
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
```
