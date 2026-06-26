# Slice 4 — Receivables cash-side completion (A/R clerk dispositions) — Design

**Date:** 2026-06-26
**Status:** Spec for review
**Umbrella:** [MVP Module Architecture](2026-06-26-mvp-module-architecture-design.md) — build-sequence slice 4 ("Receivables cash-application finish + route receipts through the module").

## Goal

The cash-application **core** (record receipt → allocate to invoices → over-payment spills to a Customer Credits liability → apply credit → void; settlement views) is already built, reachable (endpoints + host wired), hardened (settlement-requires-finalized), and tested. What's missing is **the rest of the A/R clerk's cash-side job**: the dispositions a clerk performs that today only exist as raw journal entries. Slice 6 removes raw `Post` from the Clerk role — so every legitimate A/R cash-side operation must first exist as a Receivables-module capability, or the clerk is stranded. This slice adds those capabilities.

This is the A/R analog of the decision that the A/P clerk keeps payroll/loans/taxes/prepay **through the Payables module** rather than as raw GL.

## Scope — four additions (purely additive)

Everything mirrors the existing `Payment` pattern: a document → an evidentiary store collection → service methods on `PaymentService` → an endpoint → a pure posting recipe. All post **`PendingApproval`** (the module never self-approves — SoD), all are voidable. The working `Payment`/`CreditApplication` path is untouched **except** the idempotency retrofit below.

| Capability | Recipe | Allocates to invoices? | Source type | New account |
|---|---|---|---|---|
| **Write-off / bad debt** | `Dr Bad Debt Expense / Cr A/R` (Customer dim) | Yes — settles the invoice's open balance | `WriteOff` | `BadDebtExpense` |
| **Credit note** | `Dr Sales Returns / Cr A/R` (Customer dim) | Yes — reduces the invoice's open balance | `CreditNote` | `SalesReturns` (contra-revenue) |
| **Customer refund** | `Dr Customer Credits` (Customer dim) `/ Cr Cash` | No — draws down the customer's credit balance | `Refund` | reuses `Cash` + `CustomerCredits` |
| **Idempotency symmetry** | retrofit `ComposePayment`/`ComposeCreditApplication` (and all new recipes) to `EntryIdentity.ForSource(sourceType, docId)` | — | — | — |

### Recipes (precise)

Let `allocated = Σ allocation amounts`.

**Write-off** (`WriteOffPosting` / extend `PaymentPosting`):
```
Dr Bad Debt Expense       = allocated
  Cr A/R (Customer dim)   = allocated
Id = EntryIdentity.ForSource("WriteOff", writeOffId); SourceRef = writeOffId; EffectiveDate = body.Date
```
A write-off is a *non-cash settlement* — its amount equals its allocations (no unapplied remainder, no credit).

**Credit note**:
```
Dr Sales Returns          = allocated
  Cr A/R (Customer dim)   = allocated
Id = EntryIdentity.ForSource("CreditNote", creditNoteId); SourceRef = creditNoteId
```
Structurally identical to a write-off; differs only in the debit account (contra-revenue, not expense) and meaning (billing/return adjustment, not uncollectibility). One configured `SalesReturns` account — **not** per-category revenue reversal (POC simplicity).

**Refund** (against the customer's credit balance, no invoice allocations):
```
Dr Customer Credits (Customer dim) = body.Amount
  Cr Cash                          = body.Amount
Id = EntryIdentity.ForSource("Refund", refundId); SourceRef = refundId
```

**Idempotency retrofit:** `ComposePayment` and `ComposeCreditApplication` currently post with `Id: null`; change them to `Id: EntryIdentity.ForSource(PaymentSourceType, paymentId)` / `ForSource(CreditApplicationSourceType, id)`, matching `InvoicePosting`. (`EntryIdentity` lives in Contracts; `InvoicePosting` already uses it, so it's available in the domain project.)

## Shared machinery update (the one place existing code changes)

Settlement is derived, never stored. Today `AppliedToInvoiceAsync` and `ListInvoiceViewsAsync` sum **payments + credit-applications**; they must also sum **write-offs + credit-notes**, so an invoice cleared by any of the four shows the correct `OpenBalance`/`SettlementStatus`. And `GetCustomerCreditBalanceAsync` must also subtract **refunds** (a refund spends credit, exactly as a credit-application does):
```
customer credit = Σ payment.Unapplied (non-voided)
                − Σ creditApplication.Applied (non-voided)
                − Σ refund.Amount (non-voided)
```

## Documents, stores, service

- **Bodies:** `WriteOffBody(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo)`, `CreditNoteBody(... Allocations ...)`, `RefundBody(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo)` (no allocations).
- **Documents:** `WriteOff`, `CreditNote` (each carry `Allocations`, `Voided`), `Refund` (`Amount`, `Voided`). Mirror `Payment`/`CreditApplication`.
- **Stores:** new evidentiary collections `write-offs`, `credit-notes`, `refunds`. Extend `IPaymentStore` + `DocumentPaymentStore` with `Record*/Get*/GetByCustomer*/Void*` for each (the store already serves both `Payment` and `CreditApplication`, tagged by Customer — follow that exactly).
- **Service:** add to `PaymentService` — `RecordWriteOffAsync`/`VoidWriteOffAsync`, `RecordCreditNoteAsync`/`VoidCreditNoteAsync`, `RecordRefundAsync`/`VoidRefundAsync`. Keeping them on `PaymentService` is deliberate: the applied-to-invoice and customer-credit aggregations live there and must see all sources. If the file grows unwieldy, extract the read-side aggregation into a private helper — do not split the service across the shared state.

### Validation
- **Write-off & credit note:** reuse `ValidateAllocationsAsync` (target is an **Issued** invoice of the right customer; `alreadyApplied + amount ≤ invoice.Total`, where `alreadyApplied` now includes all four sources); at least one allocation; every allocation amount > 0.
- **Refund:** `Amount > 0`; `Amount ≤ GetCustomerCreditBalanceAsync(customer)` — cannot refund more credit than exists.

### Void
Each `Void*Async` mirrors `VoidPaymentAsync`: find the entry by `SourceRef`, `ReverseAsync` if `Posted` / `VoidAsync` if pending, mark the document `Voided`. Voided dispositions drop out of every aggregation (they're already filtered by `!Voided`).

## Endpoints (`MapReceivablesEndpoints`)
- `POST /clients/{clientId}/write-offs` (+ `/{id}/void`)
- `POST /clients/{clientId}/credit-notes` (+ `/{id}/void`)
- `POST /clients/{clientId}/refunds` (+ `/{id}/void`)

Thin handlers mirroring `RecordPayment`/`VoidPayment` (same auth, same `Results.Created`, same `LedgerClientException` relay).

## Accounts
`PaymentPostingAccounts` gains `BadDebtExpenseAccountId` and `SalesReturnsAccountId`. `ConfiguredPaymentAccountsProvider` reads `Receivables:Accounts:BadDebtExpense` and `Receivables:Accounts:SalesReturns` (no hardcoded numbers). Refund reuses the existing `CashAccountId` + `CustomerCreditsAccountId`.

## Scope boundaries (staying on the roadmap)
- **No `viaModule`/credential wiring.** These entries post exactly as `Payment` does today (forwarded user token, `ViaModule = null`). Migrating *all* of Receivables (invoices + every disposition) to its `receivables` credential is **slice 5**, in one pass.
- **No A/R aging report** — read-side reporting, not a posting path; out of MVP.
- **No sim changes** — switching the AR clerk's brief to these endpoints is **slice 7**.
- **No new revenue-category reversal** for credit notes — one `SalesReturns` account.

## Testing
- **Pure posting** (`PaymentPostingTests` / new): write-off, credit-note, refund recipes balanced with the right accounts + Customer dimension; `EntryIdentity.ForSource` ids stable and distinct across all five source types; payment + credit-application now carry deterministic (non-null) ids.
- **Service** (vs fakes, mirror `PaymentServiceTests`): write-off settles an invoice (`AppliedToInvoiceAsync` includes it → view shows `Paid`); credit note reduces open balance; refund reduces the customer credit balance and is rejected when it exceeds available credit; over-allocation / cross-customer / non-Issued-invoice all rejected (shared guard); void restores the prior state across all aggregations.
- **E2E** (real host, mirror `CashApplicationTests`): a full A/R clerk cash-side lifecycle — issue invoice → partial receipt → **write off the remainder** → invoice `Paid`; **credit note** against another invoice reduces its balance; over-payment → credit → **refund** the credit → customer credit balance returns to zero; subledger ties out to the A/R control + Customer Credits balances.

## Global constraints
- .NET 10; build 0 warnings; commit per task; TDD; EphemeralMongo (run test classes individually).
- All dispositions post `PendingApproval`; the module never approves its own entries.
- Configured account ids (no hardcoded numbers); reuse `Allocation`/`Settlement` (shared lib) — do not duplicate the settlement math.
- Additive only: the existing `Payment`/`CreditApplication`/`Invoice` paths keep working; the sole edit to existing behavior is the idempotency retrofit + the aggregation widening.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
