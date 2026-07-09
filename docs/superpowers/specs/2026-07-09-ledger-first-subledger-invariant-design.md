# Ledger-first subledgers — the single-source invariant

**Date:** 2026-07-09
**Status:** Design approved (brainstorming); pending written-spec review → implementation plan
**Scope:** Foundational, cross-cutting. Sits *above* any single feature module. Reference implementation: Receivables. Generalizes to Payables, Payroll, Fixed Assets, Banking (Cash + Reconciliation), and Inventory.

---

## 1. Problem

Every feature module today keeps its **own persisted financial state** (invoice open balances, vendor bill balances, accumulated depreciation, inventory on-hand/value, cash-voucher status) *and* posts matching journal entries to the GL through a seam. Each financial fact therefore lives in **two independently-mutable stores** — the module document and the GL entry — held in agreement only by convention.

A cross-module audit (2026-07-09, `scratchpad/sot-audit/`) found this pattern is uniform across all seven modules, and that the agreement can break three ways, with **nothing detecting it**:

1. **Direct GL mutation.** `VoidEntry`/`ReverseEntry`/`ReviseEntry` check only a flat capability and that the entry exists — they never consult the `ViaModule`/`SourceRef` stamp the engine already records. Any privileged user can void a subledger-owned entry directly; the owning module is never told (no ledger→module callback exists). A void is a status flip that drops the entry from the control-account balance, so the GL silently changes while the subledger does not.
2. **Non-atomic dual-write.** Every record path is subledger-write **then** GL-post as two separate awaits across the HTTP seam — no shared transaction. A failure between them strands a "posted"-looking subledger doc with no GL entry.
3. **Approval-timing gap.** Subledgers mutate at *record* time, but the GL entry is `PendingApproval` and only counts at *approval*. A rejected/never-approved entry leaves the subledger permanently ahead of the GL.

This was demonstrated live: an inventory issue was voided at the journal directly, leaving GL Inventory = $1000 against a subledger carried value of $990 — a $10 divergence the system could neither prevent nor detect.

**Goal:** make divergence *structurally impossible*, not merely detected and repaired. Multiple sources of truth are landmines; the fix is to remove the second source, not to guard it.

## 2. Principle

> **Every fact has exactly one home. No fact is stored in two places. Derived values are computed, never stored.**

- **Financial facts** (amounts, balances, what's owed/paid/on-hand-in-dollars) live **only in the ledger**, expressed as journal-entry lines tagged with **dimensions**.
- **Non-financial facts** the ledger has no home for (customer, due date, terms, human-readable line text, inventory *quantity*) live **only in the owning module**, keyed to their entries.
- **Anything derivable** (an invoice's open balance, a customer's AR balance, aging, on-hand valuation) is **a fold over the ledger on read** — never a stored, separately-mutated number.

Because the ledger already stamps every line with `ViaModule`/`SourceRef` and already folds a control account by dimension (this is exactly what `GET /subledger/reconciliation` reads), this is **evolutionary, not new infrastructure**.

## 3. The two-part invariant

### Part A — Ledger-first: the entry *is* the transaction

The module stores **no financial amount**. A business document becomes a **view over its own dimensioned entries**.

- **Issue an invoice** → post *one* entry `Dr AR / Cr Revenue / Cr Tax`; the AR line carries dimensions `invoice=X, customer=C`.
- **Receive a payment** → post `Dr Cash / Cr AR(dim invoice=X)`. Splitting one payment across two invoices = two AR lines, each dimensioned to its invoice. **The dimension *is* the allocation** — it replaces the `Allocation { TargetId, Amount }` that lives in a payment document today (`CustomerAccountBuilder.AppliedByInvoice`).
- **Credit memo / write-off / refund** → same shape: an entry whose AR line is dimensioned to the invoice it relieves.

Everything is then derived:
- **Invoice open balance** = net of AR lines where `dim.invoice = X` (debits − credits).
- **Customer balance** = net of AR lines where `dim.customer = C`.
- **Aging** = open-invoice nets bucketed by due date (due date is module metadata).

The module keeps only: **customer master**, **per-invoice non-financial metadata** (number, issue/due date, terms), and **draft state** (see §5).

**Why this removes drift:** there is no stored AR amount to strand. Void the invoice's entry at the ledger and the invoice's open balance moves *in the same read* — the balance was never anything but a fold over entries. Drift stops *existing*; it isn't guarded against. This directly answers the demonstrated failure: the module reflects a journal void automatically on its next scan, because voided entries drop from every balance fold (`OnBooks = Active && Posted`).

### Part B — The guard: module-owned entries are corrected only through their module

Ledger-first makes the numbers un-*divergeable*. It does **not** stop *illegal* corrections — e.g. voiding an invoice that still has payments applied, which nets the invoice dimension negative (a consistent-but-nonsensical state). Preventing that is a workflow rule, and **only the module has the domain context to enforce it**: the raw ledger sees individual entries, not "these entries form invoice #1042" or "voiding an invoice with applied payments is illegal."

Therefore the ledger's mutation endpoints must **honor the `ViaModule` stamp they already record**:

- `VoidEntry`/`ReverseEntry`/`ReviseEntry` **refuse** a raw-caller mutation of a `ViaModule`-stamped entry, and point the caller at the owning module's correction surface.
- The mutation is permitted only when invoked **with the owning module's credential** (reuse `ModuleAccess`, match `module.Key == entry.Audit.ViaModule`), i.e. the module is driving its own reverse.
- **Admin escape hatch:** a break-glass path (deployment admin + step-up) for genuinely orphaned entries, audited.

The guard's job in the ledger-first world is **rule enforcement**, not drift prevention:
- **Ledger-first** → the numbers cannot *disagree*.
- **The guard** → the module is the single gate deciding what corrections are *allowed*.

Both are required; neither alone is sufficient.

## 4. How the historical drift classes die

| Drift class (from the audit) | Fate under this design |
|---|---|
| Direct GL void/reverse of a module entry | **Prevented** by Part B (guard refuses raw mutation); and even if reached via break-glass, **harmless to consistency** because Part A means the module has no separate copy to strand. |
| Non-atomic dual-write partial write | **Eliminated at the source** — there is only *one* write (the entry). No subledger amount is written separately, so there is no second write to fail. Drafts (§5) are non-financial scratch, not a partial GL state. |
| Approval-timing gap (record vs approve) | **Eliminated** — status is derived from the entry's *actual* posting state (§5), so a `PendingApproval` invoice reads as pending, not as posted. There is no record-time subledger mutation to run ahead of approval. |
| No subledger↔GL reconciliation | **Largely moot** — with nothing stored twice there is nothing to reconcile. A control-total self-check (§7) remains available as cheap defense-in-depth, but it is no longer load-bearing. |

## 5. Lifecycle: drafts, status, corrections

- **Drafts** are the one thing that legitimately isn't a ledger entry (they are incomplete, unbalanced, freely editable). They live in a **module scratch store** and hold *tentative* numbers that are explicitly **not financial truth** — nothing is posted. Issuing an invoice consumes the draft, posts the entry, and deletes the draft. (AR already does drafts-as-scratch.)
- **Status is a pure function of the entry(s), reflecting real posting state:** no entry → `Draft`; entry `PendingApproval` → `Pending`; posted, open = full → `Issued`; `0 < open < full` → `Partly paid`; open = 0 → `Paid`; entry voided/reversed → `Void`. Never stored.
- **Posted = immutable.** An invoice's amounts are never edited in place; corrections are **new entries** (credit memo, reversal). Because the entry is append-only, the "invoice" it backs is frozen for amounts the instant it posts.
- **Closed-period corrections** reverse **into an open period** (the engine's `ReverseAsync` already checks the *reversal* date, leaving the original frozen). The module's void surface must therefore reverse at an open-period date for a closed original — never at the original's own (closed) date, which the freeze would reject.

## 6. Generalization

The rule "financial → ledger dimensioned lines; non-financial → module; derived → folded" applies to every module. Two shapes:

- **Purely financial subledgers (AR, AP, Payroll, Cash):** fully ledger-first. All balances become dimension folds; modules keep master data + document metadata + drafts. AR and AP already *derive* balances, so the change is "move the allocation link and amounts out of the documents and onto dimensioned AR/AP lines."
- **Subledgers with a genuine non-financial quantity (Inventory on-hand units; FA has none — accumulated depreciation is financial):** the *dollar* facts go to the ledger exactly as above (Inventory 1400, COGS, GRNI dimensioned by item). The **quantity** has no GL home, so it is a **non-financial attribute** — carried as a typed tag on each movement entry and folded the same way: `on-hand(item) = Σ quantity-delta over that item's movement entries`. Weighted-average value = ledger Inventory-dimension balance for the item ÷ folded quantity. This keeps **one store** (the ledger entries carry both the dollar lines and the quantity attribute); the item document holds only SKU/name/UoM/status-derivation. If carrying a quantity attribute on entries proves impractical, the fallback is a module-side **rebuildable projection** of quantity from the movement entries (§7) — never an independently-mutated on-hand field.

## 7. Materialized views (performance escape hatch)

Compute-on-read (fold the journal every query) is the purest form. If reads become too expensive, a **materialized view / read-model** is permitted **only** as a *pure, rebuildable derivation* of the journal: it can be deleted and recomputed exactly, is invalidated/rebuilt from ledger changes, and is **never authoritative and never mutated in parallel**. This is the line between a safe cache and today's broken stored-state. Add it only when reads demonstrably hurt.

## 8. What stays, what changes at the engine

- **Stays:** the GL engine as the authoritative double-entry book — sequencing/counter-fence, maker-checker approval, immutable append-only entries, audit chain, period close/freeze, dimensions. Ledger-first *leans on* all of it.
- **Changes:**
  - Mutation endpoints honor `ViaModule` (Part B guard) + admin escape hatch.
  - Dimensions become a **first-class, indexed query surface** for per-dimension control-account folds (invoice, customer, item), if not already sufficiently indexed.
  - Each module drops its financial-amount storage and its allocation store; its read paths fold ledger dimensions instead of module documents.

## 9. Sequencing

1. **Receivables reference implementation** — prove the whole invariant end-to-end on the simplest module (issue, pay, allocate-by-dimension, derive balance/aging/statement, correct-through-module, closed-period reverse).
2. **Engine guard (Part B)** — honor `ViaModule` on void/reverse/revise + escape hatch; land alongside or just before (1) so AR's corrections are the only mutation path.
3. **Generalize** module-by-module (AP, Cash, Payroll, FA, Inventory), each its own spec→plan→implementation cycle, converting stored financial state to dimension folds.
4. **Optional** control-total self-check as defense-in-depth.

## 10. Decision this forces on the in-flight Inventory branch

The Inventory module (`feat/inventory-module`, complete, smoke-clean, merge held) is **consistent with its six siblings** — it stores materialized on-hand/value like FA stores accumulated depreciation. It did not introduce the flaw; it made it visible. Two options:

- **(a) Merge Inventory as-is**, matching the current (flawed) pattern, and adopt this invariant as a separate foundational effort that converts *all* modules together. Keeps the shipped work; avoids a one-off half-migrated module.
- **(b) Hold Inventory** and make it the *second* module converted (after the AR reference), so it lands already ledger-first.

Recommendation: **(a)** — merge Inventory consistent-with-siblings, then convert the whole system to the invariant in sequence order §9, so no module is a lonely exception. (Decision pending.)

## 11. Open questions (resolve during the AR spec)

- **Line-item granularity:** an invoice's product lines (10 widgets @ $5) are *finer* than the GL entry's account lines (Cr Revenue $100). The product-line breakdown is domain detail with no per-line GL home. Decide: split Revenue into per-line dimensioned lines in the entry, or keep the product-line breakdown as immutable module metadata that must sum to the entry at post (a snapshot that is safe *because* both freeze together). Leaning: module metadata (descriptions + quantities) with the authoritative *receivable* always from the ledger.
- **Dimension cardinality/indexing:** confirm the engine's dimension store indexes per-(account,dimension,value) folds efficiently at invoice/customer/item cardinality; add indexes if not.
- **Break-glass audit shape** for the admin escape hatch.
- Whether the control-total self-check (§7/§4) is worth building at all once nothing is stored twice.
