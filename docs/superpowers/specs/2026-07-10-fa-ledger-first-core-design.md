# Fixed Assets ledger-first — accumulated depreciation as a per-asset fold

**Date:** 2026-07-10
**Status:** Design approved (brainstorming) — pending written-spec review → implementation plan
**Parent:** `docs/superpowers/specs/2026-07-09-ledger-first-subledger-invariant-design.md` (§9 step 3 — generalize per module). Template: `docs/superpowers/specs/2026-07-09-ar-ledger-first-core-design.md` (the AR core, shipped to master `3af54fc`) — FA is AR-shaped (a per-entity dimensioned fold), not Cash-shaped. Part B (the engine entry guard) already shipped (`bc77ba1`). The engine dimension + `includePending` subledger fold this design consumes already shipped with the AR cycle.

---

## 1. Goal

Make the **journal entry the single source of truth for accumulated depreciation** in the Fixed Assets module, exactly as the AR core did for per-invoice A/R. Today `Asset.AccumulatedDepreciation` is a per-asset materialized field, mutated independently of the GL (a depreciation run increments the per-asset field **and** posts one *aggregate* GL credit) — two independently-mutable representations held in agreement only by convention, plus a live approval-timing gap (the field moves at record time, before the GL entry is approved). This is the exact drift class the redesign targets.

Fix: per-asset accumulated depreciation becomes a **`{Asset}`-dimensioned fold** of the Accumulated Depreciation account. `Asset.AccumulatedDepreciation` is **deleted** from the doc and **derived on read**. The aggregate account balance and every per-asset accum are then the *same ledger lines*, so subledger↔GL divergence is structurally impossible — and the fixed-asset register reconciles to the GL by construction.

**Scope is accumulated depreciation only.** `AcquisitionCost` stays a frozen input: FA does not book acquisition (`FixedAssetsService` has no ledger dependency), so cost is a reference datum, not a GL balance FA maintains. Deferred exactly as AR deferred: Angular/UI (including the disposed-asset display note in §7).

## 2. Settled design decisions

1. **Dimension = `{Asset}` on the Accumulated Depreciation account.** The account gets `RequiredDimensions = ["Asset"]`; the engine rejects an untagged Accumulated Depreciation line at post (422). Uses the `RequiredDimensions` set + post-time enforcement already shipped with AR — **no engine change**.
2. **Aggregate expense, per-asset accum only** (user decision). Only the Accumulated Depreciation *credits* carry `{Asset}` (that is what we fold per asset); the Depreciation Expense *debit* stays one aggregate line — expense is P&L, reported in aggregate, never folded per asset. A run entry is therefore **N+1 lines** (N per-asset credits + one expense debit), not 2.
3. **Per-asset accum = the fold, negated** (contra-asset sign care). Accumulated Depreciation is a contra-asset (credit balance); `SubledgerLineResponse.Balance` is debit-positive, so a credit balance reads negative → `accum = −Balance`. This is the same sign move AR made for its liability; the AP cycle proved sign is the #1 bug risk, so it gets explicit sign tests.
4. **Writes see pending / reads see posted** (inherited AR principle). Straight-line depreciation is `(cost − salvage)/life` — accum-independent, unaffected. Declining-balance *reads* accum to compute the next period, so:
   - **Compute paths** (a *write*: `RunDepreciationAsync` computing a run's amounts; `DisposeAsync` computing catch-up/NBV) read the **pending-inclusive** fold (`includePending: true`), so consecutive runs progress correctly before approval — preserving today's behavior (the field moved at record time).
   - **Report paths** (a *read*: an asset's accum/NBV on `GetAsync`/list) read the **posted-only** fold (`includePending: false`), matching what is on the books.
5. **Disposed asset reads accum = 0** (user decision, auditor-aligned). Disposal clears the asset's accum on the books (its `{Asset}` fold nets to zero), so a disposed asset reads accum = 0 — which is exactly what makes the register reconcile to the GL (a disposed asset contributes 0 to the GL Accumulated Depreciation balance). The `finalAccumulated`/NBV/gain-loss history is preserved on the evidentiary `Disposal` doc, where disposal accounting belongs. (UI legibility — surfacing `Status = Disposed` + the disposal record so 0 reads as "off the books" — is deferred with the FA UI.)
6. **Void auto-rolls-back via the ledger** (the payoff). Reversing the run/disposal GL entry auto-corrects every per-asset fold, so the manual field-rollback methods (`DocumentAssetStore.ReverseDepreciationAsync`, and `ReinstateAsync`'s accum-restore) are **deleted**. `MarkDisposedAsync`/`ReinstateAsync` keep only their `Status` flip (a non-financial lifecycle fact that stays on the doc).
7. **Greenfield / reseed.** No production FA data to preserve; dev/demo reseeded; no backfill. The Accumulated Depreciation account must be configured `RequiredDimensions = ["Asset"]`.

## 3. Grounding (verified current state)

- **Engine is ready.** Dimensions, the Mongo dimension index, `AggregateSubledgerAsync` (with `includePending`), the `RequiredDimensions` set, and post-time enforcement are all on master from the AR work. FA needs none of it changed. The read is `GET /clients/{id}/subledger?account=…&dimension=Asset[&includePending=true]` → `SubledgerLineResponse(AccountId, DimensionValue, Balance)` (debit-positive).
- **FA `ILedgerClient`/`HttpLedgerClient` lack `GetSubledgerAsync`** — add it (verbatim copy of AR/AP's method + XML doc; a plain member read, no module credential). The test `FakeLedgerClient` gains a folding implementation.
- **`Asset.AccumulatedDepreciation`** is the materialized field (in `Asset`, `AssetBody` excludes it, `AssetDocument` carries it). `DocumentAssetStore` stamps it: `CreateAsync` → 0; `UpdateAsync`/`ReactivateAsync` preserve it; `ApplyDepreciationAsync`/`ReverseDepreciationAsync` (via `AdjustAccumulatedAsync`) ± it per run line; `MarkDisposedAsync` → `finalAccumulated`; `ReinstateAsync` → `restoreAccumulated`.
- **Recipes today:** `FixedAssetsPosting.ComposeDepreciationRun(runId, total, …)` posts 2 aggregate lines. `FixedAssetsDisposalPosting.ComposeDisposal(…)` debits `AccumulatedDepreciationAccountId` by `currentAccumulated` (aggregate). One configured `AccumulatedDepreciationAccountId` for all assets.
- **Compute reads the field:** `DecliningBalanceDepreciation.DepreciationForPeriod(asset)` uses `asset.AccumulatedDepreciation`; `StraightLineDepreciation` does not. `DisposeAsync` reads `asset.AccumulatedDepreciation` as `currentAccumulated`.
- **Run/disposal services** post one PendingApproval entry; void reverses (or withdraws) and manually rolls the field back.

## 4. Data model changes

- **`AccumulatedDepreciation` removed** from `Asset` (the read model) and `AssetDocument` (the stored body). It is no longer stamped by `DocumentAssetStore` (`CreateAsync`/`UpdateAsync`/`ReactivateAsync` stop carrying it).
- **`AdjustAccumulatedAsync` + `ApplyDepreciationAsync` + `ReverseDepreciationAsync` deleted.** `MarkDisposedAsync` keeps only the `Status = Disposed` flip — the disposal body already carries `currentAccumulated`/`finalAccumulated`, computed from the fold in `DisposeAsync` before the stamp, so `MarkDisposedAsync` no longer needs the accum parameter or to return a prior value. `ReinstateAsync` drops the accum restore (keeps the `Status = Active` flip).
- **`Asset.AccumulatedDepreciation` becomes a computed value** the read path populates from the fold (§5), not a stored field. `AcquisitionCost`, `Status`, and all editable params are unchanged. The evidentiary `DepreciationRun.Lines` and `Disposal` body are unchanged.
- **`ILedgerClient`/`HttpLedgerClient`/`FakeLedgerClient`** gain `GetSubledgerAsync`.

## 5. Read + compute folds

- **Read path (report, posted-only, negated).** `FixedAssetsService.GetAsync`/list fold the Accumulated Depreciation account by `{Asset}` (`includePending: false`), negate (`accum = −Balance`), and populate `Asset.AccumulatedDepreciation` per asset (an asset with no depreciation lines folds to 0; a disposed asset folds to 0). A single `GetSubledgerAsync` call covers a page (group by `DimensionValue`), not per-asset N+1.
- **Compute path (write, pending-inclusive, negated).** `RunDepreciationAsync` populates each candidate asset's accum from the pending-inclusive fold before calling the declining-balance method; `DisposeAsync` reads the pending-inclusive fold as `currentAccumulated` for catch-up/NBV. Straight-line ignores accum (no fold needed for SL).

## 6. Posting recipe + service changes

- **`ComposeDepreciationRun`** takes the per-asset `lines` (already computed by the service) and emits one `Cr Accumulated Depreciation {Asset = line.AssetId} line.Amount` per line + one `Dr Depreciation Expense total`. Balanced: `total == Σ line.Amount`.
- **`ComposeDisposal`** adds `{Asset = assetId}` to the `Dr Accumulated Depreciation currentAccumulated` line (all other lines unchanged; expense/proceeds/cost/gain-loss carry no asset dimension).
- **`RunDepreciationAsync`** drops the `assets.ApplyDepreciationAsync` step (the dimensioned post *is* the accum change); its compute reads the pending-inclusive fold for DB assets.
- **`VoidRunAsync`** drops `assets.ReverseDepreciationAsync` — the entry reversal rolls the fold back. **`VoidDisposalAsync`** drops the accum-restore — the entry reversal restores the fold; the `Status` flip stays via `ReinstateAsync`.
- **`DisposeAsync`** reads `currentAccumulated` from the pending-inclusive fold; `MarkDisposedAsync` stamps only `Status = Disposed`.

## 7. Testing / proof

Mirror AR's proof suite, FA-flavored:
- **Fold + sign:** a depreciated asset's accum reads as `−Balance` (contra-asset negation); an asset with no runs → 0; two assets with different amounts fold independently.
- **Dimensioned run entry:** an N-asset run posts N `{Asset}` credits + one aggregate expense debit, balanced; the Accumulated Depreciation account rejects an untagged line (422).
- **Writes-see-pending / reads-see-posted:** a second DB run computes off the pending-inclusive fold (correct base before approval); an asset's reported accum reflects posted-only.
- **Disposal:** clears the asset's fold to 0 (`{Asset}` debit of `currentAccumulated`); a disposed asset reads accum 0; the Disposal doc retains `finalAccumulated`/NBV/gain-loss.
- **Void auto-rollback:** run void reverses the entry and the per-asset fold returns to its prior value with **no manual rollback step**; disposal void restores the fold and flips `Status` back.
- **FA-scoped guard proof:** a raw GL reverse of a depreciation/disposal entry (no module credential) → 409.
- **Whole-solution reconciliation:** full suite green at the final commit.

## 8. Sequencing (green at every commit)

Order is the safety net — dimension the recipes before requiring the dimension; fold on read before deleting the stored field; never delete the field before reads fold.

1. **Read-fold client method** — `GetSubledgerAsync` on `ILedgerClient`/`HttpLedgerClient`/fake (additive; no consumer yet).
2. **Recipes gain `{Asset}`** — `ComposeDepreciationRun` per-asset credits + `ComposeDisposal` asset-dimensioned Accum debit (additive; the field is still written for now, so behavior is unchanged and the suite stays green).
3. **Flip the Accumulated Depreciation account to `RequiredDimensions = ["Asset"]`** (after the recipes are dimensioned) + reseed/config.
4. **Reads + compute fold** — report path folds posted-only (negated) into `Asset.AccumulatedDepreciation`; compute paths (DB run, disposal) read the pending-inclusive fold. The stored field is still written but no longer read.
5. **Delete the stored field + its mutators + manual void rollback** — remove `AccumulatedDepreciation` from `Asset`/`AssetDocument`, delete `AdjustAccumulatedAsync`/`ApplyDepreciationAsync`/`ReverseDepreciationAsync`, strip the accum stamp/restore from `MarkDisposedAsync`/`ReinstateAsync`, drop the `ApplyDepreciationAsync`/`ReverseDepreciationAsync` calls from the services.
6. **Proof suite + whole-solution reconciliation** (fold/sign, dimensioned entry, pending-vs-posted, disposal-clears, void-auto-rollback, guard).

## 9. Non-goals

- No engine change (dimensions + `includePending` fold already on master).
- `AcquisitionCost` stays a frozen input (FA does not book acquisition; out of scope).
- No per-asset depreciation-expense fold (YAGNI — expense stays aggregate).
- No UI/Angular (deferred), including the disposed-asset display legibility note (§2.5).
- No backfill / migration (greenfield / reseed).
- Not touching Inventory (the other materialized-balance module; separate cycle — and the genuinely harder one, since inventory *quantity* has no GL home, unlike accumulated depreciation).

## 10. Risks

- **Contra-asset sign** — accum = `−Balance`; a copy of AR's negation without checking the sign would be a bug. Explicit sign tests (§7).
- **Declining-balance pending-inclusive correctness** — the compute path must read `includePending: true` or consecutive pre-approval runs mis-base. Straight-line is immune. Tested (§7).
- **N+1-line entries at scale** — a client with many assets produces a large depreciation entry. The engine handles multi-line entries; noted, not blocking. (If it ever bites, per-asset-class Accum accounts or batched runs are future levers — out of scope.)
- **Transition safety** — step 4 switches reads to the fold while the field is still written; step 5 deletes the field only after. Never reorder (mirrors AR's "reads fold before Allocation[] deleted").
- **Disposed-asset display** — accum 0 could misread as "never depreciated" until the deferred UI surfaces `Status = Disposed` + the disposal record. Behavior is correct; legibility is a UI follow-up.
