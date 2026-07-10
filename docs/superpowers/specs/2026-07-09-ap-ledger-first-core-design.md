# Payables ledger-first — core (mirror of AR)

**Date:** 2026-07-09
**Status:** Design approved (brainstorming) — pending written-spec review → implementation plan
**Parent:** `docs/superpowers/specs/2026-07-09-ledger-first-subledger-invariant-design.md` (§9 step 3 — generalize per module). Template: `docs/superpowers/specs/2026-07-09-ar-ledger-first-core-design.md` (the AR core, shipped to master `3af54fc`). Part B (the engine entry guard) already shipped.

---

## 1. Goal

Make the journal entry the single source of truth for Payables, exactly as the AR core did for Receivables: a bill's payable and a payment's per-bill allocation live only in the ledger, as `{Vendor, Bill}`-dimensioned journal lines. The module stores no monetary balance and no allocation array; every A/P balance is a fold over ledger lines on read.

Scope is the **mechanical port of the existing Payables surface** (bill enter, bill payment, vendor credit application). AP has no vendor write-off / debit-memo / refund; adding those would be a separate feature epic, out of scope here (user decision). Deferred exactly as AR deferred: fold-native aging/statement rework, closed-period reverse UX, Angular/UI.

## 2. Settled design decisions (inherited from the AR core; unchanged)

1. **Line-item detail = frozen snapshot metadata.** Bills keep their flat line descriptions + amounts + expense-account ids as an immutable post-time snapshot; dimensions are `Dictionary<string, Guid>` (entity ids only), so line detail cannot live in the ledger. The authoritative payable is always the ledger fold, never the stored lines. (Bills are simpler than invoices — no qty×price, no tax split — so this is a strict simplification of AR.)
2. **Bill tag is engine-enforced.** A/P's control account requires the set `{Vendor, Bill}`; the engine rejects at post any A/P line missing either tag. Uses the `RequiredDimensions` set + post-time enforcement already shipped with AR — **no engine change**.
3. **All A/P relievers dimensioned (the whole surface).** Both relievers of A/P — `BillPayment` and `VendorCreditApplication` — emit one `{Vendor, Bill}`-dimensioned `Dr A/P` line per allocation. The fold can only be the authoritative open balance if every reliever is dimensioned; and `Allocation[]` can't be deleted while a read still folds it.
4. **Greenfield / reseed.** No production AP data to preserve; dev/demo reseeded. No backfill.

## 3. Grounding (verified current state — see the Payables map)

- **Engine is ready.** Dimensions, the Mongo dimension index, `AggregateSubledgerAsync` (with the `includePending` option), the `RequiredDimensions` set, and post-time enforcement are all on master from the AR work. AP needs none of it changed.
- **AP is a structural mirror of pre-redesign AR.** Two-tier `bill-drafts` → `bills` lifecycle (`DocumentBillStore`); shared `Accounting101.Settlement.Allocation(TargetId, Amount)`; `Bill` stores no balance (`Total` derived from `Lines`); `BillPayment`/`VendorCreditApplication` carry `Allocation[]` on their persisted bodies (`BillPaymentBody`/`VendorCreditApplicationBody`); recipes in `BillPosting` post one aggregate `Dr A/P {Vendor}` line; `VendorAccountBuilder` + `BillPaymentService` triplicate an applied-per-bill fold over stored allocations.
- **AP `ILedgerClient`/`HttpLedgerClient` lack `GetSubledgerAsync`** — must be added (verbatim copy of AR's, `[FromKeyedServices("payables")]` credential already wired).
- **A/P account** is a control account with the legacy single `RequiredDimension = "Vendor"`; **Vendor Credits** already carries `RequiredDimension = "Vendor"`. Target: A/P → `{Vendor, Bill}`; Vendor Credits stays `{Vendor}`.

## 4. The one semantic deviation from the AR code

**Vendor Credits is an Asset (debit-normal), not a Liability.** AR's Customer Credits is a Liability, so its available-credit fold read negative on the debit-positive `AggregateSubledgerAsync` and AR **negated** it (`Sum(l => -l.Balance)`). AP's Vendor Credits available-credit fold reads **directly positive** — take `Sum(l => l.Balance)` with **no negation**. A copy-paste of AR's negation here is a sign bug. This is the only place the AP read code differs semantically from AR; everywhere else the AR code ports 1:1 (adjusting names Customer→Vendor, Invoice→Bill, A/R→A/P).

## 5. Data model changes

- **`Allocation[]` storage removed** from `BillPaymentBody` and `VendorCreditApplicationBody`. Request-command records (`BillPaymentCommand`, `VendorCreditApplicationCommand`) carry the allocations through to the recipe; the persisted bodies drop them.
- **`BillPayment.Allocated`/`Unapplied`** (computed from `Allocations` today) are recomputed from the payment's own ledger entry via an AP `SettlementRelief.ForSourceAsync` analog (sum the entry's A/P lines by `sourceRef`), with the same `postedOnly` split the AR fix introduced (reads use `postedOnly: true`; any immediacy-needing guard — e.g. a void negative-credit guard, if present — uses `postedOnly: false`).
- **`Bill`** keeps its line snapshot; no stored balance (already true). `Vendor` unchanged.
- `VendorAccountBuilder.AppliedByBill` (and the `BillPaymentService` duplicates) are replaced by the ledger fold.

## 6. Posting recipes (all in `BillPosting`)

### Bill enter (`ComposeBill`)
```
Dr Expense (per line)
  Cr Accounts Payable   total   {Vendor=V, Bill=B}     // B = bill's own id (was Vendor-only)
```

### Bill payment (`ComposeBillPayment`)
```
Cr Cash                amount
  Dr A/P    alloc[i].Amount   {Vendor=V, Bill=alloc[i].TargetId}   // one line PER allocation
  ...
  Dr Vendor Credits     remainder   {Vendor=V}                     // overpayment prepayment; no Bill
```

### Vendor credit application (`ComposeVendorCreditApplication`)
```
Cr Vendor Credits      total   {Vendor=V}
  Dr A/P    alloc[i].Amount   {Vendor=V, Bill=alloc[i].TargetId}   // one line PER allocation
```
Allocations are consumed at compose time and never persisted. Over-application validation reads the **pending-inclusive** A/P fold by `Bill`.

## 7. Read paths (folds)

- **Bill open balance(B)** = signed fold of A/P lines where `dim.Bill = B` (A/P is a liability, credit-normal; the debit-positive fold reads the outstanding payable as a negative — normalize consistently with how the bill total is compared, i.e. `applied = total − openFold` using the correct sign; verify the sign against a known bill+payment in a test).
- **Vendor balance(V)** = fold of A/P lines where `dim.Vendor = V`.
- **Vendor credit balance(V)** = fold of the **Vendor Credits** (Asset) account by `Vendor`, **no negation** (§4).
- `VendorAccountBuilder`'s applied-per-bill dictionary is re-sourced from the `Bill` fold and fed to the unchanged `OpenBills`/`Aging`/`Statement`; over-application validation and per-document relief use folds. The triplication collapses onto one read.

> **Sign note:** A/P is credit-normal (like A/R is debit-normal but opposite). The plan's Task-7 analog must establish the correct sign for the bill-open-balance fold with an explicit test (issue a bill of 100, pay 30, assert open balance 70 from the fold) rather than assuming AR's A/R sign. Vendor Credits (Asset) uses no negation (§4). Do not copy AR's signs blind.

## 8. Scope boundary

**In (this cycle):** AP `GetSubledgerAsync` client method; `ComposeBill` emits `Bill`; `ComposeBillPayment` + `ComposeVendorCreditApplication` emit per-allocation `{Vendor, Bill}` A/P lines; A/P account flips to require `{Vendor, Bill}`; reads fold the ledger (bill/vendor/applied/credit — Vendor Credits no negation); over-application pending-inclusive; delete `Allocation[]`; proof suite + whole-solution reconciliation.

**Out:** new AP disposition types (write-off/debit-memo/refund); fold-native aging/statement rework; closed-period reverse UX; Angular/UI.

## 9. Testing / proof obligations (mirror AR §9, AP-scaled)

1. Bill enter → the bill's `Bill`-axis fold equals the total (correct sign).
2. Partial payment (approved) → the bill's fold reduces by the allocation.
3. **Split payment across two bills → each bill's fold reduces by its own allocation.**
4. Vendor-axis fold == sum of the vendor's open bills; `dimension=Vendor` AND `dimension=Bill` reconciliation `TiesOut` (both work once A/P requires both).
5. Over-application: a single allocation exceeding a bill's open balance is rejected; two unapproved payments each exceeding the same bill together → the second rejected at record (pending-inclusive guard).
6. A raw A/P line missing the `Bill` tag → 422 naming `Bill`.
7. A **vendor credit application** relieving a bill → that bill's fold reduces by the applied amount.
8. **Void the bill's entry through the module's void surface → the bill's `Bill`-axis fold drops to 0 in the same read** (drift-impossible proof; exercises the shipped guard).
9. Vendor credit balance reads **positive** for an unapplied overpayment (asset, no negation) — a dedicated sign test.
10. No `Allocation[]` is persisted after a payment or credit application.

## 10. Risks / notes

- **Sign discipline** is the top risk: A/P is credit-normal and Vendor Credits is a debit-normal asset, so the fold signs differ from AR's A/R + Customer-Credits. Every fold sign must be pinned by a test, not copied from AR.
- The residual bill two-write (metadata doc + entry) is non-financial (the doc holds no balance) and idempotent-retryable — same as AR.
- Global `IgnoreExtraElements` (shipped with AR) already tolerates legacy documents on read, so removing `Allocation[]` from the AP bodies will not 500 on pre-existing AP documents (the exact defect the AR dev smoke caught). Confirm during the AP smoke.
- **Required chart configuration** (mirroring AR's smoke finding): A/P → `{Vendor, Bill}` and Vendor Credits → `{Vendor}` must be configured on the chart, or the vendor-account read folds 422→500. Dev smoke must PUT these; onboarding/seed must set them. **The AP dev smoke (2026-07-09) also found the Vendor Credits account was ABSENT from the seeded chart entirely (not just under-dimensioned)** — so onboarding/seed must ensure the account EXISTS (Asset `1300`, `RequiredDimensions {Vendor}`) at the module's configured id, not merely reconfigure it. The global `IgnoreExtraElements` convention (shipped with AR) correctly tolerated the pre-existing AP documents with a stale `Allocations` element on read — the AR legacy-doc 500 did not recur.
