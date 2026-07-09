# Receivables ledger-first — core slice

**Date:** 2026-07-09
**Status:** Design approved (brainstorming) — pending written-spec review → implementation plan
**Parent:** `docs/superpowers/specs/2026-07-09-ledger-first-subledger-invariant-design.md` (§9 step 1; Part A reference implementation). Part B (the engine entry guard) already shipped and merged (`bc77ba1`).

---

## 1. Goal

Prove the ledger-first invariant end-to-end on Receivables, on its load-bearing core: an invoice's receivable and a payment's allocation live **only** in the ledger, as dimension-tagged journal lines. The module stores no monetary balance and no allocation array; every AR balance is a fold over ledger lines on read. This is the reference pattern every other module later copies.

Scope is the **core slice** (see §8). Dispositions (credit memo / write-off / credit note / refund / credit application), aging + statement, and richer correct-through-module + closed-period reverse are deliberately deferred to follow-on spec→plan cycles.

## 2. Settled design decisions (from brainstorming)

1. **Line-item detail = frozen snapshot metadata.** Product-line descriptions, quantities, and unit prices cannot live in the ledger (dimensions are `Dictionary<string, Guid>` — entity ids only). They stay module-side as an **immutable post-time snapshot** on the issued invoice. A posted invoice is immutable, so the snapshot cannot drift; the authoritative receivable is always the ledger fold, never the stored lines.
2. **Invoice tag is engine-enforced, not convention.** A control account's required dimension becomes a **set**; A/R requires `{Customer, Invoice}`; the engine rejects at post time any A/R line missing either tag. A non-foldable (unfoldable) AR line is structurally impossible, not merely discouraged.
3. **Core slice = all A/R relievers (single-source-consistent).** The ledger fold can only be the authoritative open balance if *every* reliever of an A/R line emits an Invoice-dimensioned line — payments **and** the four dispositions (credit memo/write-off/credit note/credit application) all post `Cr A/R` against specific invoices via `Allocation[]` today. Converting only payments while dispositions stayed on the aggregate recipe would fold any disposition-touched invoice to a wrong balance, and `Allocations` could not be deleted while `CustomerAccountBuilder.AppliedByInvoice` still folds disposition allocations. So the core converts issue + payment + all four dispositions to Invoice-dimensioned lines, switches every read path to ledger folds, and deletes `Allocation[]` everywhere — the smallest internally consistent unit with one source of truth at every commit. Aging/statement/credit-activity keep working unchanged: they consume the applied-per-invoice dictionary, which is simply re-sourced from the ledger fold (re-fed, not rebuilt). Fold-native rework of aging/statement, closed-period reverse UX, and UI follow in later cycles.
4. **Greenfield / reseed.** No production AR data to preserve. Dev/demo data is reseeded fresh after the change (consistent with prior epics). No backfill/migration workstream.

## 3. Grounding (verified current state)

- **Dimensions + fold-on-read already exist and are generic.** `PostLineRequest.Dimensions` and the domain `Line.Dimensions` are `IReadOnlyDictionary<string, Guid>` (arbitrary `{type: entityId}`), persisted as an indexed array of `{Type, Value}` sub-docs (`MongoJournalStore` multikey index over `Lines.Dimensions.Type`/`.Value`). `MongoJournalStore.AggregateSubledgerAsync` folds "balance of account X grouped by dimension value" — calling it with `dimensionType: "Invoice"` yields per-invoice open balances with **no new engine code**.
- **Today AR lines carry only `Customer`, never `Invoice`.** The per-invoice split lives solely in the module's `Allocation[]` arrays (`PaymentBody`/etc.), invisible to any ledger fold. `PaymentPosting.ComposePayment` posts one **aggregate** Customer-tagged `Cr AR` line for the allocation sum — not one line per allocation.
- **Invoices already store no balance.** `Invoice.Total/Subtotal/Tax` are computed from `qty × price` lines; there is no stored open-balance field. Balances are already derived — via folds over module `Allocation[]` (`CustomerAccountBuilder`), with the same applied-per-invoice fold **duplicated** in `PaymentService`.
- **`RequiredDimension` is a single `string?` per account** (`AccountContracts.AccountRequest`/`AccountResponse`, `Account`, `AccountDocument`), used by the subledger fold/reconciliation endpoints. **Post-time enforcement CONFIRMED to exist:** `ChartFieldViolationsAsync` (structured, → 422 via `ValidationProblem`; called by both `PostEntry` and `ValidateEntry` through `ValidateForPostAsync`) and `ChartViolationsAsync` (non-structured, → 422; called by `ReviseEntry` and `Onboard`) both check `if (account.RequiredDimension is { } dimension && !line.Dimensions.ContainsKey(dimension))`. These two methods are the exact sites to extend from a single-string check to a set-iteration. Both skip when the chart is empty (bootstrap).
- **Ownership stamp:** `AuditStamp.ViaModule` (`"receivables"`) is set when the POST carries the module credential; `SourceRef`/`SourceType` back-link the business document. `InvoiceService.VoidAsync` correlates via `GetEntriesBySourceRefAsync`. The shipped guard (Part B) already refuses raw mutation of a `ViaModule`-stamped entry.

## 4. Data model changes

- **`Allocation[]` storage is removed** from `PaymentBody` **and** the three disposition bodies that carry it (`WriteOffBody`, `CreditNoteBody`, `CreditApplicationBody`). The allocation is expressed as dimensioned A/R lines. Request DTOs still carry allocations (the caller chooses which invoices to relieve); they are consumed into ledger dimensions at compose time and never persisted. `Payment.Allocated`/`Unapplied` (computed from `Allocations` today) are recomputed from the payment entry's own lines / folds. This is the primary deletion.
- **`Invoice`** keeps its line snapshot (`Description`, `Quantity`, `UnitPrice`, `Taxable`, `RevenueCategory`) as immutable post-time metadata, plus number/customer/dates/terms. No stored balance (already true). Its computed `Total` is used only for the post-time balancing check and for presentation — never as an authoritative or mutated receivable.
- **`Customer`** unchanged.
- The applied-per-invoice fold logic in `CustomerAccountBuilder` and the duplicate in `PaymentService` are replaced by a single ledger-fold read.

## 5. Engine change: required-dimension set + post-time enforcement

- `Account.RequiredDimension` (`string?`) → **`RequiredDimensions`** (a set of strings). Backward-compatible: existing single-dimension accounts become single-element sets; AP stays `{Vendor}`, etc. The subledger fold/reconciliation endpoints, account store, account seeding, and any account DTOs adapt to the set shape while preserving current single-dimension behavior for unconverted accounts.
- **Post-time enforcement:** for each line touching a control account, every dimension type in `RequiredDimensions` must be present in the line's `Dimensions`, else the post is rejected 422 (naming the missing dimension). If a post-time gate already exists for the single-dimension case, extend it to iterate the set; if none exists, add it.
- Configure **A/R** with `RequiredDimensions = {Customer, Invoice}`.
- **Inception opening balances:** an opening AR balance seeded at `OpenAsync` must be modeled so its AR line carries an `Invoice` dimension (e.g. an opening-balance pseudo-invoice) rather than an untagged AR line — otherwise enforcement rejects it. The plan handles the seed path explicitly.

## 6. Posting recipes

### Issue (`InvoicePosting.Compose`)
```
Dr A/R      total        {Customer=C, Invoice=X}     // X = invoice's own id (was Customer-only)
  Cr Revenue  ...         (per RevenueCategory, untagged)
  Cr Sales Tax Payable    (if nonzero, untagged)
```
Ordering unchanged: promote draft (assigns number, module metadata) → post entry `SourceType="Invoice", SourceRef=X`, id `EntryIdentity.ForSource("Invoice", X)` (idempotent). The residual module write is non-financial; a crash between promote and post reads as an un-issued invoice (status is derived from entry state, §3 parent spec §5) and the post is safely retryable.

### Payment (`PaymentPosting.ComposePayment`)
```
Dr Cash                  amount                       (untagged)
  Cr A/R    alloc[i].Amount   {Customer=C, Invoice=alloc[i].TargetId}   // one line PER allocation
  ...
  Cr Customer Credits   unapplied remainder   {Customer=C}              // no Invoice — unapplied
```
The allocation list is consumed at compose time to emit the dimensioned lines and is **not persisted**. Over-application validation (an allocation may not exceed an invoice's open balance) reads the **ledger fold**, not module allocations.

### Dispositions (same recipe, IN core)
`ComposeWriteOff`, `ComposeCreditNote`, `ComposeCreditApplication` apply the identical change: emit one `Cr A/R {Customer=C, Invoice=Ti}` line per allocation (write-off pairs it with `Dr Bad Debt`; credit note with `Dr Sales Returns`; credit application with `Dr Customer Credits`), and stop persisting `Allocation[]`. **`ComposeRefund` is unchanged** — a refund pays cash out against customer credits (`Dr Customer Credits / Cr Cash`) and relieves no A/R line, so it carries no Invoice dimension.

## 7. Read paths (folds)

- **Invoice open balance(X)** = signed fold of A/R lines where `dim.Invoice = X` (Dr − Cr) via `AggregateSubledgerAsync(account=AR, dimensionType="Invoice")`, taking value X.
- **Customer balance(C)** = signed fold of A/R lines where `dim.Customer = C`. Same lines, different axis.
- `CustomerAccountService`/`CustomerAccountBuilder` and `PaymentService`'s invoice-view derivation both read these ledger folds instead of scanning module `Allocation[]`, collapsing the current duplication onto one source.
- If a single-invoice targeted read is needed frequently, a narrow "balance of account where dimension=value" query may be added, but the grouped fold suffices for correctness.

## 8. Scope boundary

**In (core slice, this cycle):**
- Engine: `RequiredDimensions` set + post-time enforcement (extend the existing `ChartFieldViolationsAsync`/`ChartViolationsAsync` single-dimension check to iterate the set); A/R = `{Customer, Invoice}`; opening-balance seed path.
- A new read-fold client method on the module's `ILedgerClient`/`HttpLedgerClient` (`GET /clients/{id}/subledger?account=&dimension=`) — confirmed absent today.
- Invoice issue recipe emits the `Invoice` dimension.
- Payment **and all four A/R-relieving dispositions** (write-off, credit note, credit application; refund unchanged) emit one dimensioned A/R line per allocation; `Allocation[]` deleted from all their bodies.
- Every read path folds the ledger: open-balance (Invoice fold), customer-balance (Customer fold), applied-per-invoice (re-sourced dictionary feeding OpenInvoices/Aging/Statement unchanged), customer-credit balance (Customer Credits fold). Over-application validation reads folds. The three duplicated module-side folds collapse onto the one ledger read.
- Proof tests (§9).

**Out (deferred to later spec→plan cycles):**
- Fold-native rework of aging + statement (they keep working in the core via the re-fed applied dictionary; a later cycle may bucket directly from ledger dimensions).
- Richer correct-through-module surfaces and closed-period reverse-into-open-period (beyond the single void-reflects-in-fold proof).
- Angular/UI changes (the read-model the UI consumes is unchanged in shape where possible; UI work is its own follow-on).

## 9. Testing / proof obligations

The core plan must prove, via the ledger (not module state):
1. Issue an invoice → open-balance fold = full amount.
2. Partial payment (one allocation) → the invoice's fold reduces by the allocation.
3. **Split payment across two invoices → each invoice's fold reduces by its own allocation** (the "dimension is the allocation" proof).
4. Customer-balance fold = sum of that customer's open invoices.
5. Over-application (allocation > invoice open balance) → rejected, reading the ledger fold.
6. **Engine rejects an A/R line missing the `Invoice` tag** (enforcement proof).
7. A **disposition** (e.g. write-off) relieving an invoice → that invoice's Invoice fold reduces by the disposition amount (proves dispositions are dimensioned, not just payments).
8. **Void the invoice's entry through the module's void surface → the open-balance fold drops to 0 in the same read** (drift-is-impossible proof; exercises the shipped guard).
9. No `Allocation[]` is persisted anywhere after a payment or disposition.

## 10. Risks / notes

- The `RequiredDimension → RequiredDimensions` change is cross-cutting (contract, store, seeding, subledger endpoints). It must preserve exact current behavior for every unconverted single-dimension account — the plan sequences it as an isolated, backward-compatible engine task first, fully green, before the AR recipes depend on it.
- Post-time required-dimension enforcement exists (§3); the engine task extends the two existing check sites (`ChartFieldViolationsAsync`/`ChartViolationsAsync`) to iterate the set. A read-fold client method is confirmed absent and is added.
- The residual invoice two-write (metadata doc + entry) is not a financial dual-write — the doc holds no balance — but the plan states the ordering and idempotent-retry contract explicitly so the failure mode stays "un-issued invoice," never "balance with no metadata."
- Greenfield: dev/demo AR data is reseeded; no historical AR lines carry the `Invoice` tag, so enforcement can flip on without a backfill.
