# Vendor Account 360 UI — Slice P-D

**Date:** 2026-06-30
**Status:** Approved design, pre-implementation
**Mirrors:** Receivables Customer Account 360 (`accounting101-ui-customer-account`)
**Completes:** the Payables module's receivables-parity for the vendor lifecycle

## Goal

A read-only per-vendor drill-in at `/payables/vendors/:id` mirroring the customer 360:
header balances (AP owed + available vendor credit), an AP aging strip, an open-bills table,
an AP running-balance statement, and a credit-activity ledger — assembled by one aggregate
backend endpoint built from pure folds. Vendor row clicks open this screen.

## Why narrower than the customer 360

The customer 360 folds invoices + payments + credit-applications + write-offs + credit-notes
+ refunds. Payables has only **bills**, **bill-payments**, and **vendor-credit-applications**
— no write-offs, credit-notes, or refunds. So the vendor builder is the same shape minus
those document types.

## Backend (all in `Modules/Payables`)

### `VendorAccountBuilder` (pure static, core project `Accounting101.Payables`)

Mirror of `CustomerAccountBuilder`, ignoring voided documents, deterministic given inputs:

- `AppliedByBill(IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps) : Dictionary<Guid,decimal>`
  — Σ non-voided allocation amounts per bill id (across both document types).
- `OpenBills(IReadOnlyList<Bill> bills, IReadOnlyDictionary<Guid,decimal> applied, DateOnly asOf) : IReadOnlyList<OpenBillLine>`
  — `Entered` bills with `OpenBalance = Settlement.OpenBalance(bill.Total, applied[id]) > 0`,
  `daysOverdue = bill.DueDate is {} due ? Max(0, asOf.DayNumber - due.DayNumber) : 0`, ordered by `BillDate`.
- `Aging(IReadOnlyList<OpenBillLine> openBills) : AgingBuckets` — current (≤0 overdue) / 1–30 / 31–60 / 61–90 / 90+ by `daysOverdue`.
- `ApBalance(IReadOnlyList<OpenBillLine> openBills) : decimal` — Σ `OpenBalance`.
- `Statement(IReadOnlyList<Bill> bills, IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps) : IReadOnlyList<StatementLine>`
  — a charge per `Entered` bill (`Type="Bill"`, `Reference=bill.Number`, `Charge=bill.Total`); a settlement
  per non-voided payment (`Type="Payment"`, `Payment=Σ allocations`) and credit-application
  (`Type="Credit applied"`, `Payment=Σ allocations`); ordered by date with charges (Order 0) before
  settlements (Order 1) on the same date; running AP balance `+= Charge - Payment`.
- `CreditActivity(IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps) : IReadOnlyList<CreditActivityLine>`
  — overpayment remainders (`Type="Overpayment"`, `Amount=+payment.Unapplied` where `Unapplied>0`);
  credit-applications (`Type="Credit applied"`, `Amount=-creditApp.Applied`); ordered by date; running
  credit balance.

### View records (payables namespace — own copies, no coupling to receivables)

```
VendorAccountView(Vendor Vendor, decimal ApBalance, decimal CreditBalance, AgingBuckets Aging,
                  IReadOnlyList<OpenBillLine> OpenBills, IReadOnlyList<StatementLine> StatementLines,
                  IReadOnlyList<CreditActivityLine> CreditLines)
AgingBuckets(decimal Current, decimal D1To30, decimal D31To60, decimal D61To90, decimal D90Plus)
OpenBillLine(Guid BillId, string? Number, DateOnly BillDate, DateOnly? DueDate, decimal OpenBalance, int DaysOverdue)
StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance)
CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance)
```

### `VendorAccountService` (core)

`GetAccountAsync(Guid clientId, Guid vendorId, DateOnly asOf, CancellationToken) : Task<VendorAccountView?>`:
- `IVendorStore.GetAsync` → null ⇒ return null (endpoint maps to 404).
- reads bills (`IBillStore.GetByVendorAsync`), payments + credit-apps (`IBillPaymentStore.GetPaymentsByVendorAsync`, `GetCreditApplicationsByVendorAsync`).
- `applied = AppliedByBill(payments, creditApps)`; `open = OpenBills(bills, applied, asOf)`.
- **`creditBalance = Σ non-voided payment.Unapplied − Σ non-voided creditApp.Applied`** — the SAME formula as
  `BillPaymentService.GetVendorCreditBalanceAsync`, so the credit ledger's ending balance reconciles.
- assembles `VendorAccountView(vendor, ApBalance(open), creditBalance, Aging(open), open, Statement(...), CreditActivity(...))`.
- Registered `AddScoped` in `PayablesServiceExtensions`.

### Endpoint

`GET /clients/{clientId}/vendors/{vendorId:guid}/account?asOf=` → `200 VendorAccountView`; `404` if the
vendor doesn't exist; `400` on an unparseable `asOf`; `asOf` defaults to today. Mirror of the receivables
`GetCustomerAccount` handler. Registered after the existing `GET /vendors/{vendorId}/credit-balance`.

### Tests (`Accounting101.Payables.Tests`)

- **`VendorAccountBuilderTests`** (pure): fold math; voided payments/credit-apps excluded from
  `AppliedByBill`/`Statement`/`CreditActivity`; aging fencepost boundaries (0/1/30/31/60/61/90/91 days);
  statement orders charges-before-settlements on the same date; credit ledger ends at the running total.
- **`VendorAccountEndpointE2eTests`**: drive the real host (vendor + bills + overpay→credit + apply) and
  reconcile invariants — `ApBalance == Σ OpenBalance`; statement final balance == `ApBalance`; credit ledger
  final balance == `GetVendorCreditBalanceAsync`; voided excluded; `404` for an unknown vendor.
- **`PayablesAgingBucketsSerializationTests`** (wire-contract guard): `JsonSerializer.Serialize(new AgingBuckets(1,2,3,4,5), Web)`
  contains `"current"`, `"d1To30"`, `"d31To60"`, `"d61To90"`, `"d90Plus"` — pins the exact keys the UI must
  mirror (the camelCase trap: interior capital `T` is preserved, so `d1To30` not `d1to30`).

## Frontend

### Core (`core/payables/payables.ts` — additive)

```
interface AgingBuckets { current: number; d1To30: number; d31To60: number; d61To90: number; d90Plus: number; }
interface OpenBillLine { billId: string; number: string | null; billDate: string; dueDate: string | null; openBalance: number; daysOverdue: number; }
interface StatementLine { date: string; type: string; reference: string | null; charge: number; payment: number; balance: number; }
interface CreditActivityLine { date: string; type: string; reference: string | null; amount: number; creditBalance: number; }
interface VendorAccountView {
  vendor: Vendor; apBalance: number; creditBalance: number; aging: AgingBuckets;
  openBills: OpenBillLine[]; statementLines: StatementLine[]; creditLines: CreditActivityLine[];
}
```

The aging keys MUST be `d1To30`/`d31To60`/`d61To90`/`d90Plus` (hand-verified against the serialization
test — interior capital preserved by `JsonNamingPolicy.CamelCase`).

### Service

`getVendorAccount(vendorId: string): Observable<VendorAccountView>` → `GET /vendors/{vendorId}/account`
(client-guarded `EMPTY`).

### `vendor-account.ts` (`app-vendor-account`) at `vendors/:id`

Read-only screen mirroring `customer-account.ts`: header (vendor name/email, AP balance, credit); aging
strip (90+ emphasised when > 0); **Open bills** table (Number / Bill date / Due / Open / Overdue, overdue
rows emphasised); **Statement of account** table (Date / Type / Ref / Charge / Payment / Balance); **Credit
activity** strip (Date / Type / Amount / Balance, negative amounts emphasised). Loading state; error state
(404 → `extractProblem`); per-section empty messages; back link to `/payables/vendors`. Reads `:id` from the
route; `takeUntilDestroyed` on the fetch.

### `vendor-list.ts` (modify)

Change `open(id)` from `setSelectedVendor(id)` + `navigate(['/payables/bills'])` to
`navigate(['/payables/vendors', id])` (open the 360). Update the existing nav test to assert the new target.

### Routes

In the `payables` block add `{ path: 'vendors/:id', component: VendorAccount }` — placed AFTER the bare
`{ path: 'vendors', component: VendorList }` so it doesn't shadow the list.

## Testing

Per-file `.spec.ts` mirroring the customer-account specs: a DOM render test (flush a `VendorAccountView`,
assert the five sections + key values render) and a 404/error-path test; the vendor-list nav test updated to
assert `['/payables/vendors', id]`. Backend xUnit as above. Full UI suite + payables backend suite green,
tsc clean, before merge.

## Deferred

Bill draft **edit/discard**. With P-D, Payables reaches full receivables-parity for the vendor lifecycle
(vendors · bills · payments · credits · 360).

## Decisions taken (not asked)

- **Vendor row click opens the 360** (`/payables/vendors/:id`), replacing the P-A behavior (jump to Bills) —
  mirrors how customer rows open the customer 360.
- **The 360 reuses the AP credit-balance formula** (`Σ overpayments − Σ applications`) so the credit ledger
  reconciles with `GetVendorCreditBalanceAsync` (E2E-asserted invariant).
- **Payables owns its `AgingBuckets`/`StatementLine`/`CreditActivityLine` records** (not shared with
  receivables) to keep the modules decoupled; a serialization guard test pins the wire keys.

## Commit trailer

```
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
```
