# Invoicing: cash application (complete A/R)

**Date:** 2026-06-24
**Status:** Approved (design), pending implementation
**Repo:** Accounting101 — `Modules/Accounting101.Invoicing*`

## Problem

The invoicing module issues and voids invoices but cannot record what happens next: a customer paying.
There is no way to apply a payment to an invoice, track an open balance, or mark an invoice settled. The
dog-food simulation papers over this by having the AR clerk post cash receipts as raw journal entries,
which means the module owns only the front half of the A/R lifecycle. Completing A/R means owning
settlement: payments, allocation across invoices, unapplied customer credit, and the derived open
balance / settlement status.

## Goal

Let the module record customer payments, allocate them across one or more invoices, hold any
over-payment as customer credit, apply that credit to invoices later, and derive each invoice's open
balance and settlement status — all posting balanced journal entries through the engine under the normal
maker-checker flow. Everything balance-like is derived from documents, never stored, per the engine's
own rules.

## Non-goals

- **A/R aging** (buckets by days overdue). A separate follow-on slice; it reports on top of the open
  balances this slice produces.
- **Multi-currency.** USD-only by decision (see `docs/design-principles.md`).
- **Automatic credit consumption.** Credit is applied only by an explicit `CreditApplication`.
- **Refunds, write-offs (bad debt), early-payment discounts, payment-method → account mapping.** Later.

## Design

### Allocation is the atom

The unit that reduces an invoice's open balance is an **allocation**: `{ invoiceId, amount }`. Two
documents produce allocations; the open balance derives from the sum of allocations against an invoice,
regardless of which document funded them.

### Documents

- **Payment** — cash received from a customer. Body:
  `{ customerId, date, amount, method?, allocations: [{ invoiceId, amount }] }`.
  Allocations may sum to **≤** `amount`; the remainder becomes customer credit. A payment has no draft
  state — it is recorded and posts in one step, landing `PendingApproval` for a separate approver
  (mirrors invoice *issue*). It is an evidentiary document in the engine's document store.
- **CreditApplication** — applies a customer's existing unapplied credit to invoices. Body:
  `{ customerId, date, allocations: [{ invoiceId, amount }] }`. Funded from credit; no cash line.

### Derived, never stored

- **Invoice open balance** = `Total − Σ allocations against it` (across both document types).
- **Invoice settlement status** = `Open | PartiallyPaid | Paid`, derived from the open balance. This is a
  **separate axis** from the existing `Draft | Issued | Void` lifecycle (mirroring the engine's
  `status` vs `posting` split). An issued invoice is `Issued × {Open|PartiallyPaid|Paid}`. `Paid` is not
  added to `InvoiceStatus`.
- **Customer credit balance** = derived from the customer's documents — `Σ non-voided payment remainders
  − Σ non-voided credit applications`. No separate balance store. This equals the engine subledger balance
  of the Customer-dimensioned Customer Credits account (the end-to-end test asserts the two tie out), but
  the module computes it from its own documents rather than querying the subledger, mirroring how invoice
  open balances are derived; the credit-availability check trusts that same document-derived figure.

### Ledger effects

Each document posts one balanced entry, `SourceRef` = the document id, `SourceType` =
`"Payment"` / `"CreditApplication"`, landing `PendingApproval`:

- **Payment** → `Dr Cash (amount)` / `Cr A/R (Σ allocations, Customer dim)` /
  `Cr Customer Credits (remainder, Customer dim)` (the credit line is emitted only when remainder > 0).
- **CreditApplication** → `Dr Customer Credits (Σ, Customer dim)` / `Cr A/R (Σ, Customer dim)`.

**Customer Credits is a control account requiring the `Customer` dimension**, so per-customer credit
balances tie out through the engine's existing subledger with no new machinery. A/R continues to tie by
customer as it does today; invoice-level application is module bookkeeping layered on top of the
customer-level GL.

### Posting accounts

A new **`PaymentPostingAccounts`** contract carries the **Receivable**, **Cash**, and **Customer Credits**
(liability control) account ids, resolved from configuration like the existing invoice accounts (single
configured id each for now). It is kept separate from `InvoicePostingAccounts` because the invoice recipe
has no need of a Cash account; the two contracts share the Receivable id by configuration, not by type.

### Corrections

A payment can be voided/reversed via the same reverse-if-posted / withdraw-if-pending pattern
`ILedgerClient.VoidAsync` already implements. Because allocations live on the (now-voided) document,
voiding a payment automatically restores the affected invoices' open balances and unwinds any credit it
created. A `CreditApplication` is voidable the same way.

### Validation (422)

- Allocation to a missing or voided invoice.
- Allocation exceeding a single invoice's current open balance (no over-applying one invoice).
- `Σ allocations > payment.amount`.
- `CreditApplication` whose `Σ allocations` exceeds the customer's available credit.
- Non-positive payment amount or allocation amount.

### Web surface

Mirrors the invoice endpoints; each forwards the caller's token:

- `POST /clients/{clientId}/payments` → record a payment (with allocations).
- `POST /clients/{clientId}/payments/{paymentId}/void`.
- `POST /clients/{clientId}/credit-applications` → apply existing credit.
- `GET /clients/{clientId}/invoices/{invoiceId}` — now returns `openBalance` + `settlementStatus`.
- `GET /clients/{clientId}/customers/{customerId}/credit-balance`.
- `GET /clients/{clientId}/invoices?customerId=&settlement=open|paid` — list with a settlement filter.

## Ports / components

- **`IPaymentStore`** — evidentiary store for payments and credit applications (record, void, get,
  get-by-customer, get-by-invoice for the allocation roll-up), backed by the document store like
  `IInvoiceStore`.
- **`PaymentPosting`** — pure recipe: a payment (or credit application) → one balanced `PostEntryRequest`.
  Mirrors `InvoicePosting.Compose`; unit-tested directly.
- **`PaymentService`** — orchestrates: validate, record the document, compose, post (lands
  `PendingApproval`); void reverses. Open-balance / settlement-status derivation reads allocations from
  `IPaymentStore` and the invoice from `IInvoiceStore`.
- **`PaymentPostingAccounts`** + provider config for Receivable, Cash, and Customer Credits.

## Testing (TDD)

**Pure unit:**
- Open-balance derivation: no allocations → full; partial → reduced; fully allocated → zero.
- Settlement-status derivation: `Open` / `PartiallyPaid` / `Paid` at the boundaries.
- Payment recipe: cash debit, allocated-A/R credit (Customer dim), remainder-to-credit line present only
  when remainder > 0; entry balances; correct accounts.
- CreditApplication recipe: `Dr Customer Credits / Cr A/R`, balanced, Customer dim on both.
- Validation rejections (each 422 case above).

**End-to-end through the real host (EphemeralMongo):**
- Partial payment → invoice `PartiallyPaid`, open balance correct.
- Full payment (across two invoices in one payment) → both `Paid`.
- Overpayment → remainder raises the customer credit balance (read endpoint + subledger agree).
- Explicit `CreditApplication` → target invoice settles, credit balance falls.
- Void a posted payment → affected invoices' open balances restore; A/R and Customer Credits subledgers
  both tie out (`/subledger/reconciliation`).

## Risks / mitigations

- **Open-balance roll-up cost** (summing allocations per invoice): for MVP volumes a query over a
  customer's payments is fine; if it ever matters, the engine's subledger already gives customer-level
  totals and a maintained per-invoice projection can be added without changing the contract.
- **Allocation race** (two payments allocating the last of an invoice's balance concurrently): the open
  balance is recomputed at validation time and the settlement entries post independently; worst case is a
  transient over-application caught by the per-invoice open-balance check on the second post. Acceptable
  at human scale; a stronger guard is deferred with the projection.
- **Customer Credits as a new required chart account:** clients must provision it; the configured-account
  provider fails loud if it is missing, consistent with the existing three accounts.
