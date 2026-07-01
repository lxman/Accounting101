# AR/AP Polish Batch — Design

**Date:** 2026-07-01
**Status:** Approved (design)
**Modules:** Receivables (AR) + Payables (AP)

## Goal

Close three long-deferred cross-module polish items, batched because each has an
identical AR/AP twin. A fourth item ("keyboard-bypassable disabled buttons") was
investigated and **dropped** — the audit found no concrete broken case (the
styled anchors are Cancel or draft-only Edit; real action buttons use native
`<button [disabled]>`, which the keyboard already respects).

## Global Constraints

- USD-only; camelCase on the wire.
- TDD: every change is covered by a test that fails first.
- Backend: .NET 10 / C# 13, xUnit. Frontend: Angular 22 + Signal Forms, vitest via `ng test`.
- AR and AP fixes are line-for-line mirrors — keep them symmetric.
- Pure-fold builders stay pure and deterministic given their inputs.
- No behavior change beyond what each item specifies; no unrelated refactoring.

## Item 3 — Same-date row ordering (backend)

**Problem.** `OrderBy` in LINQ-to-Objects is a *stable* sort, so same-date rows
currently retain their upstream Mongo query order — which is not guaranteed
stable across reads. Result: rows sharing a date can reorder between requests.
The `Statement` fold already tiebreaks; two folds do not.

**Files:** `Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs`,
`Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs` (mirror).

**Fix.**
- `OpenInvoices` (`.OrderBy(l => l.IssueDate)`) → `.ThenBy(l => l.Number)`.
  `OpenBills` (`.OrderBy(l => l.BillDate)`) → `.ThenBy(l => l.Number)`.
  Issued invoices / entered bills always carry a unique number that sorts in
  issue order — deterministic and human-meaningful.
- `CreditActivity` (`.OrderBy(r => r.Date)`, mixed-type rows) → mirror the
  `Statement` pattern: carry a per-type `Order` (AR: Overpayment=0, Credit
  applied=1, Refund=2; AP: the analogous vendor-credit event ordering) and the
  source document `Id` into the raw tuple, then
  `.OrderBy(r => r.Date).ThenBy(r => r.Order).ThenBy(r => r.Id)`. The `Id` is an
  opaque-but-stable ultimate tiebreaker for same-date, same-type events.

**Behavior note.** The public row DTOs (`OpenInvoiceLine`, `CreditActivityLine`,
vendor equivalents) are unchanged; `Order`/`Id` are internal sort keys only.

**Tests.** In each builder's fold tests, feed two same-date rows in **reversed**
input order and assert the output order is identical (and matches the intended
Number / type-Order). One test per affected fold per module.

## Item 4 — asOf culture-parse (backend)

**Problem.** `DateOnly.TryParse(asOf, out date)` uses the current thread culture,
so an ambiguous string parses differently by locale, and the accepted format
drifts from the documented `yyyy-MM-dd`.

**Files:** `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs:189`,
`Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs:258`.

**Fix.** Replace with
`DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)`.
Culture-independent and enforces the ISO format the existing 400 message already
advertises. The rejection path (Problem 400, same message) is unchanged.

**Tests.** Endpoint/handler test: a valid `yyyy-MM-dd` asOf parses and returns
the account view regardless of thread culture; a non-ISO/ambiguous string (e.g.
`13/01/2026` or `2026/01/13`) → 400. One per module.

## Item 2 — Over-allocation summary line (frontend)

**Problem.** Over-allocation is already *prevented* — each editor's `valid()`
caps allocations and disables Save — but it disables **silently**, with no
explanation of why Save is greyed out.

**Files (four editors):**
- AR: `payment-editor.ts`, `adjustment-editor.ts`
- AP: `bill-payment-editor.ts`, `vendor-credit-apply-editor.ts`

**Fix.** Add a computed `allocationWarning: string | null` to each editor that
returns a human message **only when the invalid reason is an over-allocation**
(total allocated exceeds the cap, or a single row exceeds its open balance),
and `null` otherwise — so it stays quiet for benign incomplete states
("nothing selected yet", "amount not entered"). Render it in red immediately
next to the disabled Save button. No change to `valid()` or the Save gate; this
is purely additive explanation.

Per-editor cap and message:
- `payment-editor` / `bill-payment-editor`: cap = payment `amount()`.
  → "Allocated {allocated} exceeds the payment amount by {over}."
  (Also covers a row exceeding its own open balance with a row-scoped message
  if that is the binding reason: "A line is allocated more than its open balance.")
- `adjustment-editor` (credit-application mode) / `vendor-credit-apply-editor`:
  cap = available credit (`creditBalance()` / `available()`).
  → "Applied {total} exceeds available credit by {over}."

Money is formatted with the existing display helper. The message reflects the
*first binding* over-allocation reason so only one line ever shows.

**Tests.** Per editor: drive an over-allocation, assert (a) the specific message
text is rendered and (b) Save remains disabled. Also assert the message is
absent in a valid state.

## Out of Scope

- Item 1 (keyboard-bypassable disabled buttons) — dropped, no real defect.
- Any change to allocation *validation* logic or server-side allocation rules.
- Statement fold ordering (already deterministic).

## Testing & Verification

- Backend: `dotnet test` for Receivables + Payables test projects.
- Frontend: `ng test --watch=false`.
- Execution: one branch, subagent-driven (fresh implementer per task, task
  review after each, final whole-branch review before merge).
