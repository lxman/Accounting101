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
3. **Core slice first.** The first plan ships the engine change + issue + payment-allocation-as-dimension + open/customer balance folds + deletion of `Payment.Allocations`, plus the void-reflects-in-fold proof. The rest of the AR surface follows in later cycles.
4. **Greenfield / reseed.** No production AR data to preserve. Dev/demo data is reseeded fresh after the change (consistent with prior epics). No backfill/migration workstream.

## 3. Grounding (verified current state)

- **Dimensions + fold-on-read already exist and are generic.** `PostLineRequest.Dimensions` and the domain `Line.Dimensions` are `IReadOnlyDictionary<string, Guid>` (arbitrary `{type: entityId}`), persisted as an indexed array of `{Type, Value}` sub-docs (`MongoJournalStore` multikey index over `Lines.Dimensions.Type`/`.Value`). `MongoJournalStore.AggregateSubledgerAsync` folds "balance of account X grouped by dimension value" — calling it with `dimensionType: "Invoice"` yields per-invoice open balances with **no new engine code**.
- **Today AR lines carry only `Customer`, never `Invoice`.** The per-invoice split lives solely in the module's `Allocation[]` arrays (`PaymentBody`/etc.), invisible to any ledger fold. `PaymentPosting.ComposePayment` posts one **aggregate** Customer-tagged `Cr AR` line for the allocation sum — not one line per allocation.
- **Invoices already store no balance.** `Invoice.Total/Subtotal/Tax` are computed from `qty × price` lines; there is no stored open-balance field. Balances are already derived — via folds over module `Allocation[]` (`CustomerAccountBuilder`), with the same applied-per-invoice fold **duplicated** in `PaymentService`.
- **`RequiredDimension` is a single `string` per account** (`AccountContracts`), used by the subledger fold/reconciliation endpoints. Post-time enforcement that a line touching a control account actually carries the required dimension is **unconfirmed** — the first plan task must establish whether it exists and extend it, or add it.
- **Ownership stamp:** `AuditStamp.ViaModule` (`"receivables"`) is set when the POST carries the module credential; `SourceRef`/`SourceType` back-link the business document. `InvoiceService.VoidAsync` correlates via `GetEntriesBySourceRefAsync`. The shipped guard (Part B) already refuses raw mutation of a `ViaModule`-stamped entry.

## 4. Data model changes

- **`Payment.Allocations` is removed** (and the `Allocation`-array storage on `PaymentBody`). The allocation is expressed as dimensioned AR lines. This is the primary deletion.
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

The same one-line-per-allocation shape is the template for the deferred dispositions (credit memo / write-off / etc.), but those are out of scope here.

## 7. Read paths (folds)

- **Invoice open balance(X)** = signed fold of A/R lines where `dim.Invoice = X` (Dr − Cr) via `AggregateSubledgerAsync(account=AR, dimensionType="Invoice")`, taking value X.
- **Customer balance(C)** = signed fold of A/R lines where `dim.Customer = C`. Same lines, different axis.
- `CustomerAccountService`/`CustomerAccountBuilder` and `PaymentService`'s invoice-view derivation both read these ledger folds instead of scanning module `Allocation[]`, collapsing the current duplication onto one source.
- If a single-invoice targeted read is needed frequently, a narrow "balance of account where dimension=value" query may be added, but the grouped fold suffices for correctness.

## 8. Scope boundary

**In (core slice, this cycle):**
- Engine: `RequiredDimensions` set + post-time enforcement; A/R = `{Customer, Invoice}`; opening-balance seed path.
- Invoice issue recipe emits the `Invoice` dimension.
- Payment recipe emits one dimensioned A/R line per allocation; `Payment.Allocations` deleted.
- Open-balance + customer-balance read paths fold the ledger; duplication collapsed; over-application validation reads folds.
- Proof tests (§9).

**Out (deferred to later spec→plan cycles):**
- Credit memo, write-off, credit note, refund, credit application (same recipe shape, applied later).
- Aging + statement derived from folds.
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
7. **Void the invoice's entry through the module's void surface → the open-balance fold drops to 0 in the same read** (drift-is-impossible proof; exercises the shipped guard).
8. No `Payment.Allocations` is persisted anywhere after a payment.

## 10. Risks / notes

- The `RequiredDimension → RequiredDimensions` change is cross-cutting (contract, store, seeding, subledger endpoints). It must preserve exact current behavior for every unconverted single-dimension account — the plan sequences it as an isolated, backward-compatible engine task first, fully green, before the AR recipes depend on it.
- Whether post-time required-dimension enforcement exists today is unconfirmed; the first engine task establishes this and either extends or adds it.
- The residual invoice two-write (metadata doc + entry) is not a financial dual-write — the doc holds no balance — but the plan states the ordering and idempotent-retry contract explicitly so the failure mode stays "un-issued invoice," never "balance with no metadata."
- Greenfield: dev/demo AR data is reseeded; no historical AR lines carry the `Invoice` tag, so enforcement can flip on without a backfill.
