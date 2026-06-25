# Accounts Payable (Bills) module

**Date:** 2026-06-24
**Status:** Approved (design), pending implementation
**Repo:** Accounting101 — new `Modules/Accounting101.Payables*`; shared `Modules/Accounting101.Settlement`; refactor of `Modules/Accounting101.Invoicing*`.

## Problem

The platform has an A/R module (invoicing + cash application) but no A/P. A business can't run its books while hand-keying every vendor bill and disbursement — the dog-food simulation fakes A/P with raw journal entries today. A/P is also the architectural proof that the module platform is genuinely multi-module rather than invoicing-special-cased.

## Goal

Build the full accounts-payable cycle as a new module, mirroring A/R flipped to the liability/expense side: vendors, bills (Dr Expense / Cr A/P), bill payments allocated across bills with over-payment held as vendor credit, explicit vendor-credit application, derived bill open balance + settlement status, and void-with-restore — all posting balanced maker-checker entries through the engine, with the A/P and Vendor Credits subledgers tying out. Extract the settlement/allocation primitives shared with A/R into one library so the math can't diverge.

## Non-goals

A/P aging, multi-currency, recurring bills, purchase orders / 3-way match, 1099 tracking, payment-method → account mapping. (Mirrors A/R's deferrals.)

---

## 1. Shared settlement library

New project **`Modules/Accounting101.Settlement`** — pure, zero external dependencies. Holds the domain-agnostic primitives currently living in `Accounting101.Invoicing`:

- `record Allocation(Guid TargetId, decimal Amount)` — `TargetId` is the settled document (an invoice or a bill). (Renamed from invoicing's `Allocation(Guid InvoiceId, …)`.)
- `enum SettlementStatus { Open, PartiallyPaid, Paid }`
- `enum SettlementFilter { Open, Paid }`
- `static class Settlement` — `OpenBalance(decimal total, decimal applied)`, `Status(decimal total, decimal applied)`.

Namespace `Accounting101.Settlement`. The four types move verbatim except `Allocation`'s field rename.

## 2. Invoicing refactor (consume the shared library)

Delete the local `Allocation`, `Settlement`, `SettlementStatus`, `SettlementFilter` from `Accounting101.Invoicing`; add a project reference to `Accounting101.Settlement`; repoint every usage. The only behavioral change is the `Allocation.InvoiceId` → `Allocation.TargetId` rename, which touches `PaymentPosting`, `PaymentService`, the `RecordPaymentRequest`/`CreditApplicationRequest` DTOs, and tests. The existing 48 invoicing tests must stay green — they are the refactor's safety net. No new behavior.

---

## 3. Payables module (`Modules/Accounting101.Payables` + `.Api` + `.Tests`)

Mirrors the invoicing module's structure. Depends on `Accounting101.Ledger.Contracts` and `Accounting101.Settlement`; no ASP.NET in the pure module.

### Reference + documents

- **Vendor** — AP's reference entity (mirrors `Customer`): `{ Id, Name, Email? }`, stored via the document store (`vendors` collection, reference tier).
- **Bill** — evidentiary document, lifecycle `Draft → Entered → Void` (mirrors invoice `Draft → Issued → Void`; "enter" = finalize the document + post the A/P entry). Fields: `{ VendorId, BillDate, DueDate?, VendorReference?, Memo?, Lines }` where each line is `{ Description, Amount, ExpenseAccountId }`. The internal bill number comes from finalize's gapless sequence; `VendorReference` carries the vendor's own invoice number.
- **BillPayment** — `{ VendorId, Date, Amount, Method?, Allocations }` (allocations target bills). Recorded-and-posted in one step (no draft); voidable.
- **VendorCreditApplication** — `{ VendorId, Date, Allocations }`; applies existing vendor credit to bills; no cash; voidable.

### Ledger effects

Each posts one balanced entry, lands `PendingApproval`, `SourceRef` = document id:

- **Bill (enter)** → `Dr each line's ExpenseAccountId (line amount)` / `Cr A/P (Σ lines, Vendor dim)`. Lines sharing an expense account collapse to one debit (deterministic by account id). `SourceType = "Bill"`.
- **BillPayment** → `Dr A/P (Σ allocations, Vendor)` + `Dr Vendor Credits (remainder, Vendor)` (only when remainder > 0) / `Cr Cash (Amount)`. `SourceType = "BillPayment"`.
- **VendorCreditApplication** → `Dr A/P (Σ, Vendor)` / `Cr Vendor Credits (Σ, Vendor)`. `SourceType = "VendorCreditApplication"`.

**Vendor Credits is a `Vendor`-dimensioned _asset_ control account** — an over-payment is a prepayment the vendor owes back, so it debits (increases) the asset; applying it credits (decreases) it. This is the asset-side mirror of A/R's liability Customer Credits, and it ties out through the engine's Vendor subledger.

### Derived, never stored

- **Bill open balance** = `Total − Σ allocations against it` (payments + credit applications, non-voided).
- **Settlement status** = `Open | PartiallyPaid | Paid` (shared `Settlement`), a separate axis from the `Draft | Entered | Void` lifecycle.
- **Vendor credit balance** = `Σ non-voided BillPayment remainders − Σ non-voided VendorCreditApplication amounts`. Ties out to the Vendor Credits subledger (asserted in the e2e), but computed from the module's documents — same approach as A/R.

### Posting accounts (configured, single id each)

- Bill entry needs only **`PayableAccountId`** (the expense accounts come from the bill lines).
- Payments need **`PayableAccountId` + `CashAccountId` + `VendorCreditsAccountId`**.

A `BillPostingAccounts { PayableAccountId }` and a `BillPaymentPostingAccounts { PayableAccountId, CashAccountId, VendorCreditsAccountId }`, resolved by a config-backed provider (`Payables:Accounts:Payable|Cash|VendorCredits`), failing loud on a missing account.

### Validation (422)

- Bill: at least one line; every line amount > 0; every `ExpenseAccountId` non-empty.
- Allocation to a missing/voided bill, or a bill of a different vendor.
- Allocation exceeding a single bill's current open balance.
- `Σ allocations > payment.Amount`; non-positive payment/allocation amounts.
- `VendorCreditApplication` exceeding the vendor's available credit.

### Web surface (`.Api`)

Mirrors invoicing; each forwards the caller's token; settlement entries land `PendingApproval`:
`POST /vendors`, `POST /bills`, `POST /bills/{billId}/enter`, `POST /bills/{billId}/void`, `POST /bill-payments`, `POST /bill-payments/{paymentId}/void`, `POST /vendor-credit-applications`, `GET /bills/{billId}` (→ a `BillView` with open balance + settlement status), `GET /vendors/{vendorId}/credit-balance`, `GET /bills?vendorId=&settlement=open|paid`.

## Ports / components

Mirrors the invoicing module:
- `IVendorStore` / `DocumentVendorStore` (reference tier).
- `IBillStore` / `DocumentBillStore` (evidentiary; `CreateDraft`/`Finalize`/`Void`/`Get`/`GetByVendor`).
- `IBillPaymentStore` / `DocumentBillPaymentStore` (records `bill-payments` + `vendor-credit-applications`, void, get, get-by-vendor).
- `BillPosting` (pure recipe: bill → entry; payment → entry; credit application → entry).
- `BillService` (draft/enter/void a bill) and `BillPaymentService` (record payment / apply credit / void + derived reads: `GetBillViewAsync`, `GetVendorCreditBalanceAsync`, `ListBillViewsAsync`).
- `BillPostingAccounts` / `BillPaymentPostingAccounts` + `ConfiguredBillAccountsProvider`.
- `PayablesServiceExtensions.AddPayables` — module identity `"payables"`; manifest declares reference `vendors` and evidentiary `bills` / `bill-payments` / `vendor-credit-applications` (all `Vendor`-tagged); registers the stores/services/provider and the loopback `ILedgerClient`.

## Testing (TDD)

- **Shared `Settlement`** — unit tests for `OpenBalance`/`Status` boundaries (may move the existing invoicing tests into the shared project).
- **Pure recipes** — bill (multi-expense-line split → one A/P credit; lines sharing an account collapse; entry balances; Vendor dim on A/P), bill payment (Dr A/P allocated + Dr Vendor Credits remainder / Cr Cash; remainder line only when > 0), credit application (Dr A/P / Cr Vendor Credits).
- **Services vs fakes** — validation rejections; record bill → open balance; partial/over payment → settlement status + vendor credit; apply credit; void restores; settlement-filtered list excludes voided bills.
- **End-to-end through the real host (EphemeralMongo)** — enter a bill (Dr expense / Cr A/P) under SoD, approve; over-pay it → bill Paid + vendor credit rises; enter a second bill; apply the credit → it settles; void a payment → balances restore; **A/P and Vendor Credits subledger reconciliations both `TiesOut`**.

## Risks / mitigations

- **Invoicing refactor breaks shipped code** — caught at compile time + the 48 existing tests; the rename is mechanical.
- **Over-allocation race** — open balance recomputed at validation; worst case a transient over-application caught by the per-bill open-balance check (same as A/R; stronger guard deferred with a projection).
- **Vendor Credits provisioned as the wrong normal side** — it is an asset control account; the e2e provisions it as `Asset` with `RequiredDimension = "Vendor"` and asserts the subledger ties out, which would fail if mis-modeled.
- **New chart accounts a deployment must provision** — A/P, Cash, Vendor Credits; the configured provider fails loud if missing.
