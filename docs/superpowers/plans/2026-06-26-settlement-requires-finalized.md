# Settlement Requires a Finalized Document — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Only a finalized document is settleable — an **Issued** invoice or an **Entered** bill. Exclude drafts (and voided) from the settlement/open lists, and reject payment allocations whose target is not finalized.

**Architecture:** Two symmetric module changes. In each module's payment service: tighten `ValidateAllocationsAsync` from "reject only Void" to "require finalized status," and tighten the settlement-view filter from `!= Void` to `== <finalized>`.

**Tech Stack:** C#/.NET 10, xUnit + EphemeralMongo (+ in-memory stores where the tests use them).

## Global Constraints
- .NET 10; build **0 warnings**; TDD.
- Reject Draft/Void allocation targets AND don't block legitimate Issued/Entered payments.
- Spec: `docs/superpowers/specs/2026-06-26-settlement-requires-finalized-design.md`.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit file lists; do NOT commit in a worktree.

---

## Task 1: Gate settlement on finalized status (both modules)

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` (`ValidateAllocationsAsync`, `ListInvoiceViewsAsync`)
- Modify: `Modules/Payables/Accounting101.Payables/BillPaymentService.cs` (`ValidateAllocationsAsync`, `ListBillViewsAsync`)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/` (add settlement-gating tests)
- Test: `Modules/Payables/Accounting101.Payables.Tests/` (add settlement-gating tests)

**Interfaces:** behavior-only change; no signature changes. `InvoiceStatus { Draft, Issued, Void }`; `BillStatus { Draft, Entered, Void }`.

- [ ] **Step 1: Write the failing tests** (per the spec's test list), in whichever existing harness the modules' payment/settlement tests already use (look at `CashApplicationTests` / `PaymentServiceTests` for Receivables and the Payables equivalent):
  - Receivables: Draft excluded from `ListInvoiceViewsAsync(Open)`; allocate-to-Draft rejected (message contains "issued"); allocate-to-Issued succeeds; Void excluded/rejected.
  - Payables: Draft excluded from `ListBillViewsAsync`; allocate-to-Draft rejected (message contains "entered"); Entered succeeds; Void excluded/rejected.
  - The allocate-to-Draft test is the key RED: today it is *accepted* (only Void is rejected).

- [ ] **Step 2: Run, confirm fail** — allocate-to-Draft currently succeeds; Draft currently appears in the views. Run each module's test class individually (EphemeralMongo/host-boot flakiness).

- [ ] **Step 3: Implement** the four edits exactly as the spec shows:
  - `PaymentService.ValidateAllocationsAsync`: `if (invoice.Status == InvoiceStatus.Void)` guard → `if (invoice.Status != InvoiceStatus.Issued)` with message `$"Invoice {a.TargetId} is {invoice.Status} — only issued invoices can be paid."`
  - `PaymentService.ListInvoiceViewsAsync`: `.Where(inv => inv.Status != InvoiceStatus.Void)` → `.Where(inv => inv.Status == InvoiceStatus.Issued)`
  - `BillPaymentService.ValidateAllocationsAsync`: `if (bill.Status == BillStatus.Void)` guard → `if (bill.Status != BillStatus.Entered)` with message `$"Bill {a.TargetId} is {bill.Status} — only entered bills can be paid."`
  - `BillPaymentService.ListBillViewsAsync`: `.Where(bill => bill.Status != BillStatus.Void)` → `.Where(bill => bill.Status == BillStatus.Entered)`
  - Do NOT change any other guard (the over-allocation/open-balance check, the customer/vendor-mismatch check, the exists check all stay).

- [ ] **Step 4: Run, confirm pass; regression-sweep** — new tests green. Then run the full `Accounting101.Receivables.Tests` and `Accounting101.Payables.Tests` (class by class). Fix any existing settlement/cash-application test that built its fixture by allocating against a **Draft** (it must now issue/enter first). Record each test touched and confirm the fixture was wrong (paying a draft), not a real behavior we're losing.

- [ ] **Step 5: Build clean, commit**
```bash
git add Modules/Receivables/Accounting101.Receivables/PaymentService.cs \
        Modules/Payables/Accounting101.Payables/BillPaymentService.cs \
        <new/updated test files>
git commit -m "feat(settlement): only finalized documents are settleable (no paying drafts)"
```

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run individually: the new Receivables + Payables settlement tests, plus `CashApplicationTests`/`PaymentServiceTests` and the Payables equivalents — all green.
- [ ] Confirm: Draft excluded from both views; allocate-to-Draft rejected in both modules with a clear message; Issued/Entered payments unaffected; Void still excluded/rejected.
- [ ] Whole-branch review (mid-tier sufficient — small, symmetric, money-movement), then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- Spec coverage: both ValidateAllocations guards tightened, both view filters tightened, tests for Draft-excluded + allocate-to-Draft-rejected + Issued/Entered-works + Void-still-handled, in both modules.
- Consistency: Receivables→Issued, Payables→Entered; messages name the actual status; no other guard touched.
- Open implementer check: which existing cash-application tests build fixtures by paying a not-yet-finalized document (Step 4 sweep) — those fixtures must issue/enter first.
