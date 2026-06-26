# Settlement requires a finalized document — Design

**Date:** 2026-06-26
**Status:** Spec for review

## Context & principle

Cash application (payments/credits) settles open documents. Today both modules treat a document as settleable unless it is **Void** — so a **Draft** (un-issued invoice / un-entered bill) wrongly:
1. **appears in the settlement/open list** (`ListInvoiceViewsAsync` / `ListBillViewsAsync` filter only `!= Void`), showing a not-yet-real document as an open balance to collect/pay; and
2. **accepts a payment allocation** (`ValidateAllocationsAsync` rejects only `== Void`), so a payment can be aimed at a draft — which has **no posted ledger entry**, making the allocation accounting nonsense.

**Principle (user's words):** *"You shouldn't be digging around in the clerk's scratchpad finding bills to pay. You should only pay them once they are entered in the system."* A draft is scratch; only a **finalized** document is settleable: an **Issued** invoice or an **Entered** bill. (Mirrors the drafts-as-scratch model: only Issue/Void/Enter are real events.)

This is symmetric across the two modules. Lifecycle states:
- `InvoiceStatus { Draft, Issued, Void }` — settleable = **Issued**.
- `BillStatus { Draft, Entered, Void }` — settleable = **Entered**.

(A fully-paid document keeps its finalized status — Issued/Entered — with a zero open balance; the existing open-balance check handles over-allocation. So "require finalized status" does not block legitimate payment of a partly/fully settled document.)

## Change

### Receivables — `PaymentService`

- **`ValidateAllocationsAsync`:** replace the void-only guard
  ```csharp
  if (invoice.Status == InvoiceStatus.Void)
      throw new InvalidOperationException($"Invoice {a.TargetId} is voided.");
  ```
  with a finalized-required guard:
  ```csharp
  if (invoice.Status != InvoiceStatus.Issued)
      throw new InvalidOperationException(
          $"Invoice {a.TargetId} is {invoice.Status} — only issued invoices can be paid.");
  ```
  (Rejects both Draft and Void, naming the actual state.)
- **`ListInvoiceViewsAsync`:** change the filter
  ```csharp
  .Where(inv => inv.Status != InvoiceStatus.Void)
  ```
  to
  ```csharp
  .Where(inv => inv.Status == InvoiceStatus.Issued)
  ```
  so drafts (and voided) never appear as open/settleable.

### Payables — `BillPaymentService` (symmetric)

- **`ValidateAllocationsAsync`:** replace
  ```csharp
  if (bill.Status == BillStatus.Void) throw ...
  ```
  with
  ```csharp
  if (bill.Status != BillStatus.Entered)
      throw new InvalidOperationException(
          $"Bill {a.TargetId} is {bill.Status} — only entered bills can be paid.");
  ```
- **`ListBillViewsAsync`:** change `.Where(bill => bill.Status != BillStatus.Void)` to `.Where(bill => bill.Status == BillStatus.Entered)`.

## Out of scope
- The drafts-as-scratch model itself (already shipped for Receivables; Payables bills retain their evidentiary Draft→Entered lifecycle — unchanged here, we only gate settlement on it).
- The separate Payables double-pay / mis-route-to-Vendor-Credits ergonomics gap (different concern).
- Any change to how documents are created/finalized.

## Testing

Receivables (`Accounting101.Receivables.Tests`):
- A **Draft** invoice does **not** appear in `ListInvoiceViewsAsync` (Open filter) — only Issued do.
- Allocating a payment to a **Draft** invoice id → rejected with the "only issued invoices can be paid" message (naming `Draft`).
- Allocating a payment to an **Issued** invoice still succeeds and settles it (no regression).
- A **Void** invoice is still excluded from the list and rejected for allocation.

Payables (`Accounting101.Payables.Tests`):
- A **Draft** bill does **not** appear in `ListBillViewsAsync`.
- A payment allocated to a **Draft** bill id → rejected ("only entered bills can be paid", naming `Draft`).
- An **Entered** bill still pays/settles normally.
- A **Void** bill is still excluded/rejected.

## Global constraints
- .NET 10; build 0 warnings; commit per task; TDD.
- Money-movement guard — must reject Draft/Void targets AND must not block legitimate Issued/Entered payments.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
