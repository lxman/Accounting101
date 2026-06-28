# Void Error Relay + Negative-Credit Guard — Design

**Date:** 2026-06-28
**Status:** Approved (design)

## Problem

Two product findings surfaced by the money/settlement edge-case test library
([[accounting101-settlement-edge-library]]):

**Finding #1 — ledger errors become opaque 500s on the disposition paths.**
When a module forwards a request to the engine's ledger and the engine refuses
it (e.g. a clerk attempting a reverse → 403, a closed-period post → 409, an
unbalanced entry → 422), the caller should receive that real status and reason.
Receivables already does this for `IssueInvoice` (catches `LedgerClientException`
and relays `ex.Reason` with `ex.StatusCode`), but its other ledger-touching
handlers — the record-disposition endpoints and **all five void endpoints** —
catch only `InvalidOperationException` (→ 409/422), so a `LedgerClientException`
escapes as a 500. **Payables is worse: it has no typed ledger exception at all.**
Its `HttpLedgerClient` calls `EnsureSuccessStatusCode()` on every call, so any
ledger non-2xx throws a bare `HttpRequestException` → 500 on *every* Payables
path (enter bill, payments, voids).

**Finding #2 — voiding a payment can drive credit balance negative.** A customer
overpayment posts the remainder to the "Customer Credits" liability
(`Cr Customer Credits`). If that credit is later consumed (applied to another
invoice, or refunded as cash) and the original payment is then voided, the void
posts a reversal that debits Customer Credits *again* — driving the account to a
**debit balance on a liability** (e.g. −$50). The double-entry books stay
arithmetically balanced (every entry, including the reversal, balances), but the
Customer-Credits account/subledger is corrupt: it implies the customer owes us a
credit, an auditor red flag, and effectively a "free" amount handed to the
customer. The module's derived `GetCustomerCreditBalanceAsync`
(`created − spent − refunded`) goes negative with no guard. Same shape in
Payables (`GetVendorCreditBalanceAsync = created − spent`).

## Goal

Make the disposition paths *safe and honest*: relay the engine's real
status/reason instead of 500, and refuse any payment void that would push the
credit balance negative — with a clear message — so the corrupt state can never
be reached. Keep the change small; defer credit-lifecycle ergonomics.

## Scope decisions (and the reasoning behind them)

The credit balance is a **fungible per-customer (per-vendor) pool**:
`CreditApplication`/`Refund` records carry no source-payment id, so there is no
per-payment lineage of "which payment funded which consumption." Two facts fall
out of that, and they set the scope:

- **We don't need source lineage.** The only thing required to keep the books
  coherent is that a payment void must not drive the pool negative — a *scalar*
  check. So the fix for #2 is a **guard**, not lineage or cascade.
- **Recovery ergonomics are deferred.** When the guard blocks a void (the
  overpayment credit was already consumed), the clean recovery is "reverse the
  consuming credit-application/refund first, then void the payment." But there is
  no credit-application void today, and no discovery view to locate consumers.
  Building those — plus refund-as-cash handling and optional source lineage — is
  **more work for a case that is unlikely**, and "blocked with a clear message"
  is strictly better than "silently corrupt." So this design **blocks safely and
  stops there**; the ergonomics are documented as deferred (see Future Work).

**Cascade-unwind was rejected.** Auto-voiding the consuming documents would (a)
require the source lineage the model lacks (fabricating FIFO/LIFO attribution
with multiple overpayments), and (b) in the refund case silently assert that
mailed-back cash was recovered when it was not. Block forces a human to decide.

**In scope:** Receivables + Payables endpoints, their `HttpLedgerClient`s, a new
Payables `LedgerClientException`, and the two `VoidPaymentAsync` services.
**Out of scope:** credit-application void, discovery views, refund-as-cash
semantics, source lineage, Payroll/Banking (no credit pool), the engine.

## Architecture

### Finding #1 — relay ledger errors

**Receivables (mechanism already exists; widen its use).** AR's `HttpLedgerClient`
already throws `LedgerClientException(StatusCode, Reason)` via its
`EnsureSuccessAsync`/`ReasonFrom` helpers, and `IssueInvoice` relays it. Add the
same relay catch to every other AR handler that posts to / transitions the
ledger:

```csharp
catch (LedgerClientException ex) // the engine refused — relay its real status + reason, not a 500
{
    return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
}
```

placed alongside the existing `catch (InvalidOperationException …)` in:
`RecordPayment`, `ApplyCredit`, `RecordWriteOff`, `RecordCreditNote`,
`RecordRefund`, `VoidPayment`, `VoidWriteOff`, `VoidCreditNote`, `VoidRefund`,
`VoidInvoice`. (`LedgerClientException` is not an `InvalidOperationException`, so
the two catches coexist.) The finding named the void endpoints; the
record-disposition endpoints have the identical latent gap (they post to the
ledger and catch only `InvalidOperationException`), so they are fixed in the same
pass for consistency.

**Payables (introduce the mechanism, mirroring AR).**
1. New `Accounting101.Payables/LedgerClientException.cs` — a copy of AR's type
   (`int StatusCode`, `string Reason`).
2. `Accounting101.Payables.Api/HttpLedgerClient.cs` — replace every
   `response.EnsureSuccessStatusCode()` with an `await EnsureSuccessAsync(response, ct)`
   that throws `LedgerClientException((int)status, ReasonFrom(body, response))`,
   porting AR's `EnsureSuccessAsync` + `ReasonFrom` private helpers verbatim
   (ValidationProblemDetails `errors` → ProblemDetails `detail` → raw body →
   status phrase).
3. `Accounting101.Payables.Api/PayablesEndpoints.cs` — add the relay catch to
   `EnterBill`, `RecordPayment`, `ApplyCredit`, `VoidBill`, `VoidPayment`.

### Finding #2 — negative-credit guard on payment void

Only payments create credit (the overpayment remainder); credit-applications and
refunds *consume* it (and voiding a consumer only restores credit, never
negative); write-offs/credit-notes/invoices don't touch the pool. So the guard
lives on **payment void only**, in both modules.

In `PaymentService.VoidPaymentAsync` (AR) and `BillPaymentService.VoidPaymentAsync`
(AP), after the existing not-found / already-voided checks and **before** the
ledger reverse + `payments.VoidAsync`:

```csharp
// A void reverses the whole payment, including the overpayment that landed as customer/vendor credit.
// If that credit has since been applied or refunded, removing it would drive the credit balance negative
// (a debit balance on a liability) — a corrupt state. Refuse; the user must reverse the consuming
// application/refund first, then void this payment.
if (payment.Unapplied > 0m)
{
    decimal creditBalance = await GetCustomerCreditBalanceAsync(clientId, payment.CustomerId, ct); // AR
    // (AP: GetVendorCreditBalanceAsync(clientId, payment.VendorId, ct))
    if (creditBalance - payment.Unapplied < 0m)
        throw new InvalidOperationException(
            $"Cannot void payment {paymentId}: its overpayment credit ({payment.Unapplied:C}) has already " +
            $"been applied or refunded (available credit is only {creditBalance:C}). Reverse the credit " +
            $"application(s)/refund(s) first, then void this payment.");
}
```

The endpoints already map `InvalidOperationException` → **409**, so no endpoint
change is needed for the guard.

**Why the arithmetic is exactly right (fungible-correct):** the current balance
includes this payment's `Unapplied` (it isn't voided yet). `creditBalance −
payment.Unapplied` is the pool *after* removing this payment's contribution. It
is `< 0` precisely when this payment's overpayment is needed to keep the pool
non-negative — i.e. when (some of) it has been consumed and no *other* credit
covers it. With multiple overpayments, voiding one is correctly **allowed** when
the remaining credit still covers what was spent (the pool stays ≥ 0); it's only
blocked when this payment's portion is genuinely required. Partial consumption is
handled by the same inequality.

## Data flow

```
Module endpoint → service → ledger HttpClient → engine
  engine refuses (403/409/422) → HttpClient throws LedgerClientException(status, reason)
     → endpoint catch relays Results.Problem(reason, statusCode: status)   [#1]

VoidPayment → service: not-found? already-voided? → NEW: would-go-negative? → 409 if so   [#2]
            → else reverse the posted entry + mark voided (unchanged)
```

## Error handling

- `LedgerClientException` → relayed with the engine's own status code + reason
  (no more 500 on a ledger refusal).
- Negative-credit guard → `InvalidOperationException` → 409 with a message that
  names the overpayment amount, the available credit, and the remedy.

## Testing

E2E through the existing host fixtures (shared-Mongo infra
[[accounting101-shared-test-mongo]]); no new product behavior beyond the guard
and the relay.

- **#1 relay (AR):** drive a void/record path into a ledger refusal and assert
  the relayed status + reason, not 500. Natural case: a Clerk voids a *posted*
  invoice/payment (reverse requires Approver) → engine 403 → assert the response
  is the relayed 403 (or the engine's status) with its reason, not 500. (This is
  exactly the path the settlement library hit as a 500.)
- **#1 relay (AP):** same shape through a Payables void; assert relayed status,
  not 500. Plus a direct `HttpLedgerClient` unit test mirroring AR's
  `HttpLedgerClientTests` (a non-2xx response → `LedgerClientException` with the
  parsed reason).
- **#2 guard (AR + AP), blocked:** overpay an invoice (creates credit) → apply
  that credit to a second invoice → void the original payment → assert **409**
  with the guard message; assert Customer/Vendor Credits did **not** go negative.
- **#2 guard, allowed:** void a payment whose overpayment credit is still
  available (unspent) → succeeds; and (multiple-overpayment case) voiding one
  payment while another's credit still covers the spend → succeeds.

## Success criteria

- No disposition path returns 500 on a ledger refusal in either module; the
  engine's status + reason are relayed.
- A payment void that would drive Customer/Vendor Credits negative is refused
  with a clear 409; the corrupt (negative-liability) state is unreachable.
- No change to product behavior beyond the guard + relay; the settlement
  edge-case suite and both module suites stay green.

## Future Work (deferred — documented so the analysis isn't lost)

Built only if real usage hits the wall (judged unlikely):

- **Credit-application void** (`VoidCreditApplication` AR / `VoidVendorCreditApplication`
  AP + endpoints) — the missing operation that lets a user *recover* from a #2
  block by reversing the consuming application. Would reuse the existing
  reverse/void helper; voiding an application restores credit to the pool and
  re-opens exactly the invoices in its own `Allocations` (deterministic — the
  application records its target invoices).
- **Discovery view** — e.g. `GET /customers/{id}/credit-activity` (or folding the
  consumer list into the guard's 409 message) so a user can locate which
  application/refund to reverse. Per-payment source attribution is *not*
  available (fungible pool) and is not needed: the user reverses consumers by
  amount to refill the pool.
- **Refund-as-cash semantics** — when the consumed credit left as a cash refund,
  reversing it asserts the cash was recovered. If the cash is truly gone,
  "void the payment" is the wrong tool; the accountant posts a correcting
  transaction. A distinct "refunded as cash" warning in the block message would
  help. The system guarantees coherence (never negative) but cannot conjure
  mailed cash back — and this is exactly why cascade-unwind is unsafe here.
- **Source lineage** — recording which payment funded each consumption. This is a
  lot-tracking/costing layer (one consumption can draw from several overpayments,
  forcing a FIFO/LIFO policy and a per-consumption breakdown), and the GL stays a
  single pool account, so the lineage would live only in the module — a
  module-vs-ledger split. Significant work; only worth it for customers who need
  credit-source *reporting*.
