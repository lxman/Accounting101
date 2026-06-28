# Money/Settlement Edge-Case Scenario Library — Design

**Date:** 2026-06-27
**Status:** Approved (design)

## Problem

The Accounting101 money/settlement surface (payments, credit applications,
write-offs, credit notes, refunds, voids, and their Payables equivalents) is
thoroughly covered at the **service level** — the domain services throw on
over-allocation, settled/nonexistent/wrong-party targets, exceeds-credit, and
the like, and those throws are unit-tested against fakes.

What is **not** covered: almost none of those negative/boundary paths are
exercised through the **HTTP host**. The endpoint layer maps domain failures to
specific HTTP outcomes — record-disposition endpoints map
`InvalidOperationException` → **422 Unprocessable Entity**, void endpoints map it
→ **409 Conflict**, a missing `customerId` → **400** — and the exception message
is relayed verbatim in the ProblemDetails `detail`. That status-code mapping is
the contract every API consumer and the future UI depends on, and it is
essentially unverified end-to-end. Two adjacent boundary areas are likewise
unverified end-to-end: arithmetic rounding (tax/derivation) and full
statement/subledger integrity across a messy disposition sequence.

This is a different hunt from the dog-food simulation. The sim is a happy-path
*volume* instrument; it floods the system and asks "did anything break?" Edge
cases need *deterministic, exact* assertions ("this action → exactly this status
and this message", "this sequence → exactly these balances"), and the LLM clerk
introduces variance. This library is the boundary-precision instrument.

## Goal

A deterministic, CI-runnable library of HTTP-level edge-case scenarios for the
money/settlement surface, asserting exact outcomes (HTTP status + message
substring for rejections; precise balances + subledger tie-out for accepted
sequences). It doubles as regression armor for the settlement contract.

## Scope

**In scope:** Receivables (AR) and Payables (AP), mirrored where the shape
applies.

- **AR** disposition types: payment, credit application, write-off, credit note,
  refund, and the void of each.
- **AP** disposition types: bill payment, vendor credit application, and the
  void of each. AP has **no** write-off / credit note / refund — those are
  AR-only (bad-debt, sales-returns, customer-refund). The AP mirror therefore
  covers allocation boundaries, over-allocation, exceeds-vendor-credit, void,
  and the integrity sweep, **not** the AR-only disposition types.

**Out of scope:** new product features; the agentic simulation; temporal/period
and authz/SoD edge surfaces (separate future libraries); Payroll and
Banking/Cash modules.

## Decisions

- **Assertion strictness:** HTTP status code **plus a stable message
  substring** (e.g. `"exceeds its open balance"`). Robust to copy edits while
  still pinning the contract. Not full-string-verbatim (brittle), not
  status-only (misses wrong/mis-routed messages).
- **Form:** organized xUnit E2E tests in the existing module test projects,
  reusing the existing `WebApplicationFactory` + EphemeralMongo fixtures. No new
  runner or framework. Plain `[Fact]` per scenario; `[Theory]` only for the
  homogeneous "bad allocation → 422" family.
- **Discoveries are findings, not silent skips.** Each test encodes the
  **correct** expected behavior. If a scenario reveals the system does the wrong
  thing (a path that 500s instead of 422, a rounding drift, a mis-routed error
  reason), that is surfaced and decided per-case — fix the product, or (rarely)
  adjust the expectation only if the current behavior is actually correct. Tests
  are never written to lock in known-wrong behavior, and no scenario is silently
  skipped to make the suite green.

## Architecture

Each scenario drives the real host through a full **setup → act → assert**
sequence:

1. **Setup:** seed a SoD-enabled client (`SeedSodClientAsync`), seed a matching
   chart of accounts, create the party (customer/vendor), issue + approve the
   document(s) under test using the existing `IssueInvoiceAsync` /
   `ApproveBySourceRefAsync` helpers.
2. **Act:** perform the action under test (the disposition or void) as the
   Clerk, capturing the raw `HttpResponseMessage`.
3. **Assert:**
   - *Rejection scenarios:* assert the HTTP status and that ProblemDetails
     `detail` contains the expected substring.
   - *Accepted / integrity scenarios:* approve via the SoD flow, then GET the
     invoice/bill view, credit balance, financial statements, and subledger
     reconciliation, and assert exact numbers + tie-out.

### Shared helpers (small, one set per module test project)

- `AssertProblem(response, expectedStatus, substring)` — parse ProblemDetails,
  assert HTTP status **and** `detail` contains `substring`.
- `AssertBalanced(asOf)` — assert balance sheet `isBalanced == true` **and**
  trial balance nets to zero at `asOf`.
- Reuse existing helpers: `IssueInvoiceAsync`, `ApproveBySourceRefAsync`, and the
  subledger-reconciliation tie-out assertion already present in the E2E tests.

## Components (file structure)

### `Accounting101.Receivables.Tests/Settlement/`

- **`AllocationBoundaryE2eTests.cs`** — rejection family, each → **422 +
  substring**:
  - allocations sum exceeds payment amount → `"cannot exceed the payment amount"`
  - allocation exceeds invoice open balance → `"exceeds its open balance"`
  - allocation targets a nonexistent invoice → `"does not exist"`
  - allocation targets a draft invoice → `"only issued invoices can be paid"`
  - allocation targets a voided invoice → `"only issued invoices can be paid"`
  - allocation targets another customer's invoice → `"belongs to a different customer"`
  - zero or negative payment amount → `"greater than zero"`
  - zero or negative allocation amount → `"greater than zero"`
- **`DispositionLimitE2eTests.cs`**:
  - write-off over open balance → **422** `"exceeds its open balance"`
  - credit note over open balance → **422** `"exceeds its open balance"`
  - refund exceeds available credit → **422** `"exceeds available credit"`
  - credit application exceeds available credit → **422** `"exceeds available credit"`
  - void an already-voided payment → **409** `"already voided"`
  - void a nonexistent payment → **409** `"not found"`
  - void-of-void (void, then void again) → **409** `"already voided"`
- **`RoundingE2eTests.cs`**:
  - taxable invoice whose tax does not divide evenly: assert posted A/R and
    sales-tax-payable to the cent.
  - payment split across multiple invoices with messy decimals: assert each
    derived open balance is exact and the A/R subledger reconciliation ties to
    zero.
  - (Money is `decimal`; real rounding risk lives in tax/derivation, not
    allocation. Scenarios target where it can actually bite.)
- **`SettlementIntegrityE2eTests.cs`** — one or two *messy sequences*, e.g.
  issue → partial payment → credit note → write off remainder → void one
  disposition → resulting reversal. After each **approved** step assert:
  - balance sheet balanced and trial balance nets zero (`AssertBalanced`)
  - A/R and Customer-Credits subledgers tie out
  - the derived invoice open balance equals the hand-computed expected value
  - Kept focused (one or two well-chosen sequences), not combinatorial.

### `Accounting101.Payables.Tests/Settlement/`

Mirror where the shape applies, using the Payables E2E fixture and
bills / vendor-credits / bill-payments:

- **`BillAllocationBoundaryE2eTests.cs`** — over-allocation, exceeds open
  balance, nonexistent bill, draft/voided bill, wrong-vendor bill, zero/negative
  amounts → **422 + substring**.
- **`BillDispositionLimitE2eTests.cs`** — vendor credit application exceeds
  available credit → **422**; void already-voided / nonexistent / void-of-void
  → **409 + substring**.
- **`BillRoundingE2eTests.cs`** — multi-bill payment split with messy decimals;
  assert exact open balances and A/P subledger ties to zero.
- **`BillSettlementIntegrityE2eTests.cs`** — messy sequence (enter → partial pay
  → overpay-to-vendor-credit → apply credit → void one); after each approved
  step assert BS balanced, TB nets zero, A/P and Vendor-Credits subledgers tie
  out, derived open balance exact.

## Data flow

```
SeedSodClient → seed chart → create party → issue/enter + approve doc(s)
   → ACT (disposition or void) as Clerk → capture HttpResponseMessage
   → rejection: AssertProblem(status, substring)
   → accepted: approve via SoD → GET views/statements/reconciliation
               → assert exact balances + tie-out + AssertBalanced
```

## Implementation notes (to confirm during planning)

- The Payables host fixture name and exactly which AP disposition endpoints are
  exposed (confirm against `PayablesE2eTests` and the Payables endpoints).
- The financial-statement, trial-balance, and subledger-reconciliation route
  shapes and response DTOs (confirm against `FinancialStatementTests` and
  `SubledgerTests`).
- The "settled target" nuance: a fully-paid invoice is still `Issued` status, so
  paying it again surfaces via the **open-balance** guard
  (`"exceeds its open balance"`), not the status guard. Encode the message the
  system actually produces.

## Success criteria

- `dotnet test` is green for `Accounting101.Receivables.Tests` and
  `Accounting101.Payables.Tests`.
- Every rejection scenario pins HTTP status **and** message substring.
- Every integrity scenario pins exact balances **and** subledger tie-out.
- Any real bug surfaced by a scenario is fixed (not skipped) before the suite is
  considered complete; findings are reported.
