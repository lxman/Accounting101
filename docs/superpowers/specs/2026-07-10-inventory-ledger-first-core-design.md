# Inventory ledger-first — value as a per-item fold, quantity as a rebuildable projection

**Date:** 2026-07-10
**Status:** Design approved (brainstorming) — pending written-spec review → implementation plan
**Parent:** `docs/superpowers/specs/2026-07-09-ledger-first-subledger-invariant-design.md` (§9 step 3 — generalize per module; §6 the two-shape generalization; §7 the materialized-view / rebuildable-projection rule). Templates: `docs/superpowers/specs/2026-07-09-ar-ledger-first-core-design.md` (per-entity dimensioned fold) and `docs/superpowers/specs/2026-07-10-fa-ledger-first-core-design.md` (materialized-balance deletion). Part B (the engine entry guard) already shipped (`bc77ba1`). The engine dimension + `includePending` subledger fold (AR cycle) and the batch source-ref read (`GetBySourceRefsAsync` / `sourceRefs` CSV param, Cash cycle) this design consumes are both already on master.

---

## 1. Goal

Make Inventory the last module where subledger↔GL divergence is structurally impossible. It is the **hardest** of the seven and the reason the whole redesign exists: the merge review that spawned this invariant demonstrated an inventory issue voided at the journal leaving GL Inventory $1000 against a subledger carried value of $990 — a divergence the system could neither prevent nor detect.

Inventory is the one module with a **genuinely non-financial quantity** (parent §6). Two facts are materialized today and held in agreement only by convention, plus a live approval-timing gap (both move at record time, before the GL entry is approved):

- **Inventory value ($)** — has a GL home (the Inventory 1400 account). Becomes a `{Item}`-dimensioned **ledger fold**, exactly as AR did per-invoice and FA did per-asset.
- **On-hand quantity (units)** — has **no GL home** (it is not dollars). Per parent §6 it becomes a **non-financial attribute** carried on each movement document and folded the same way: `on-hand(item) = Σ signed quantity over that item's movement entries`. This is the §6 fallback (module-side rebuildable projection), chosen over adding a numeric-quantity attribute to the engine, because the engine treats a document body as opaque and only folds **dollar** line amounts — quantity cannot ride the existing dimension/fold surface (dimensions are `Dictionary<string,Guid>`; quantity is a signed decimal).

Fix: `Item.OnHandQuantity` and `Item.TotalValue` are **deleted** from the doc and **derived on read** — value from the ledger fold, quantity from the movement-document projection. The GL Inventory balance and the sum of per-item folds are then the *same ledger lines*, so the register reconciles to the GL by construction; and the quantity projection and the value fold both count a movement **iff its spawned entry is on the books**, so they move on one lever and cannot disagree.

**Weighted-average carrying valuation is preserved** — the module keeps single-pool weighted-average costing; only the *storage* of the running (quantity, value) pair changes from materialized fields to folds.

## 2. Settled design decisions

1. **Value = `{Item}`-dimensioned fold of the Inventory account.** The Inventory 1400 account gets `RequiredDimensions = ["Item"]`; the engine rejects an untagged inventory line at post (422). Uses the `RequiredDimensions` set + post-time enforcement already on master — **no engine change**. Sign is the **easy case**: Inventory is a debit-normal asset, so the debit-positive `SubledgerLineResponse.Balance` reads **positive, no negation** (unlike AR's liability and FA's contra-asset).
2. **Only the Inventory line carries `{Item}`** (YAGNI). The counter-lines — COGS (issue), GRNI Clearing (receipt), Inventory Adjustment (shrinkage/overage) — stay un-dimensioned. Only the Inventory line feeds the value fold; per-item COGS/adjustment analytics are out of scope. Entries stay two lines.
3. **Quantity = signed `Quantity` on the movement document, projected, gated by entry-on-books** (Q1 decision). `on-hand(item) = Σ movement.SignedQuantity` over that item's movements **whose spawned GL entry is currently on the books**. Because value folds the ledger and quantity gates on the same "is the entry on the books" lever, the two axes cannot diverge, and a void/reject auto-reflects in on-hand with no separate document mutation. The movement document's signed `Quantity` is the **single home** of the quantity fact (it has nowhere else to live).
4. **Writes see pending / reads see posted, with one shared gate** (Q2 decision, inherited AR principle). Weighted-average cost couples the two folds (`avg = value ÷ quantity`), so **the value fold and the quantity projection must always use the identical gate** or the ratio is meaningless:
   - **Read paths** (an item's on-hand/value/avg on `GetItem`/list): both folds `includePending: false` — posted-only, what is on the books.
   - **Write paths** (computing the next Issue's average cost + the block-negative guard): both folds `includePending: true` — Posted **and** Pending. A pending Issue reserves stock (a second issue can't over-draw it); a pending Receipt makes stock available (the clerk saw the goods arrive — Pending is an approval formality, not a physical-receipt question). Matches AR/AP/FA's uniform `includePending` — no asymmetry. Accepted edge: a Pending receipt later Rejected is the same narrow class every module tolerates.
5. **Applied unit cost / extended cost stay as frozen evidentiary snapshots** on the movement doc (the AR line-item precedent — module-side immutable metadata recording what cost was applied at the time). The **authoritative** inventory value is always the ledger fold. `AppliedUnitCost`/`ExtendedCost` are informational and never read back to compute a balance.
6. **The running-balance snapshots are deleted.** `StockMovementBody.ResultingOnHand`/`ResultingTotalValue` (materialized running balances captured after each movement) are the drift source and the void-replay crutch — both deleted. Void no longer replays them.
7. **Void auto-rolls-back via the ledger** (the FA payoff). Reversing the movement's GL entry drops it from the value fold and gates it out of the quantity projection, so the manual valuation-restore in `VoidAsync` (subtracting the movement's effect via `SetValuationAsync`) is **deleted**, along with `SetValuationAsync` itself and `StockMovement.SignedValueEffect`. **`SignedQuantityEffect` is KEPT** — it is now the quantity projection's per-movement signed-quantity input (a pure `Type`+`Quantity` derivation, not stored state, not a drift source). LIFO enforcement stays.
8. **Doc-first ordering is load-bearing** (crash safety). `RecordAsync` persists the movement document **before** posting the entry. A document without an on-books entry is inert (gated out of both folds); an *entry without a document* would diverge (value counts the ledger line, quantity has no `Quantity` to read). Doc-first + the idempotent `EntryIdentity.ForSource(movementId)` post makes a crash-then-retry safe.
9. **Greenfield / reseed.** No production inventory data to preserve; dev/demo reseeded; no backfill. The Inventory account must be configured `RequiredDimensions = ["Item"]`.

## 3. Grounding (verified current state)

- **Engine is ready — no change.** Dimensions, the Mongo dimension index, `AggregateSubledgerAsync(dimensionType, accountId, asOf, includePending)`, the `RequiredDimensions` set + post-time enforcement (AR cycle), and the batch `GetBySourceRefsAsync` + `GET /entries?sourceRefs=` CSV param (Cash cycle) are all on master. The value read is `GET /clients/{id}/subledger?account=…&dimension=Item[&includePending=true]` → `SubledgerLineResponse(AccountId, DimensionValue, Balance)` (debit-positive).
- **Storage verified.** `StockMovementBody` decimals (`Quantity`, `AppliedUnitCost`, `ExtendedCost`, `ResultingOnHand`, `ResultingTotalValue`) persist as exact **Decimal128** (`LedgerMongoBootstrap` registers a global `DecimalSerializer(BsonType.Decimal128)`; `ScopedDocumentStore.BuildDoc` serializes the body via native `ToBsonDocument()`, symmetric `BsonSerializer.Deserialize` on read). The signed `Quantity` the projection replays is a faithful numeric source. **But** the body is **opaque** to the engine (only `Tags` are queryable; `DocumentStockMovementStore` uses no tags), so the quantity fold is necessarily a module-side in-memory replay, not a server-side aggregation.
- **Materialized fields today.** `Item.OnHandQuantity`/`Item.TotalValue` (in `Item`; `ItemBody` excludes them; `ItemDocument` carries them). `DocumentItemStore` stamps them: `CreateAsync` → 0/0; `UpdateAsync`/`ReactivateAsync` preserve; `SetValuationAsync` overwrites. `ItemView.AverageUnitCost = TotalValue / OnHandQuantity`. `DeactivateAsync` guards on `OnHandQuantity != 0` (has-stock 409).
- **Movement snapshots today.** `StockMovementBody.ResultingOnHand`/`ResultingTotalValue`; `StockMovement.SignedQuantityEffect`/`SignedValueEffect` derive the per-movement deltas for void replay.
- **Recipes today.** `InventoryPosting.Compose` emits two **un-dimensioned** lines per movement type (receipt Dr Inventory/Cr GRNI; issue Dr COGS/Cr Inventory; shrinkage Dr Adjustment/Cr Inventory; overage Dr Inventory/Cr Adjustment).
- **Service today.** `InventoryMovementService.RecordAsync`: validate → resolve item (Active) → compute effect from the item's **stored** `(OnHand, TotalValue)` via `InventoryValuation` (block-negative → 409) → reject non-positive extended cost pre-persist → resolve accounts → **persist movement** → **`SetValuationAsync` mutate item** → **post PendingApproval entry** (three writes). `VoidAsync`: LIFO check → reverse/withdraw entry → **`SetValuationAsync` restore** by subtracting `SignedQuantityEffect`/`SignedValueEffect` → void doc.
- **Module `ILedgerClient`/`HttpLedgerClient`** have `PostAsync`, `ReverseAsync`, `VoidAsync`, and singular `GetEntriesBySourceRefAsync` — they **lack** `GetSubledgerAsync` (value fold) and the batch `GetEntriesBySourceRefsAsync` (entry-status batch).
- **Guard already shipped.** A raw GL reverse of an inventory (`ViaModule = inventory`) entry is already refused (Part B). No guard work.

## 4. Data model changes

- **`OnHandQuantity`/`TotalValue` removed** from `Item` (read model) and `ItemDocument` (stored body). `DocumentItemStore` stops stamping them (`CreateAsync`/`UpdateAsync`/`ReactivateAsync` drop the valuation params); **`SetValuationAsync` deleted**. The global `IgnoreExtraElementsConvention` tolerates legacy bodies on read (greenfield anyway). `Item` becomes pure master data (Sku/Name/Description/UoM/derived-Status).
- **On-hand, value, and average cost surface only through `ItemView`**, computed by a valuation service from the folds (§5) — the FA `AssetView` precedent. `ItemView`'s **JSON shape stays stable** (`onHandQuantity`, `totalValue`, `averageUnitCost`) so item UI screens are untouched.
- **`ResultingOnHand`/`ResultingTotalValue` removed** from `StockMovementBody` and `StockMovement`; **`SignedValueEffect` deleted**; **`SignedQuantityEffect` KEPT** (the projection's per-movement signed-quantity input). `Quantity` (authoritative), `AppliedUnitCost`/`ExtendedCost` (frozen evidentiary), `Type`, `EffectiveDate`, `Memo` stay. `StockMovementView` loses the two `Resulting*` fields (minor movement-screen touch if displayed — confirm in planning).
- **`ILedgerClient`/`HttpLedgerClient`/`FakeLedgerClient`** gain `GetSubledgerAsync` (AR/AP/FA copy) and `GetEntriesBySourceRefsAsync` (Cash/Payroll copy). Both are plain member reads against endpoints already on master — no module credential, no engine change.
- **Has-stock deactivation guard** (`DocumentItemStore.DeactivateAsync`) moves from reading the stored `OnHandQuantity` to the projected on-hand (posted-only) — computed by the service and passed in, keeping the store free of a ledger dependency.

## 5. Read + compute folds

The valuation service exposes per-item `(onHand, totalValue, averageUnitCost)`, all keyed off entry-on-books, with the **shared gate** invariant (§2.4):

- **value(item)** = the item's `{Item}` fold of the Inventory account (`GetSubledgerAsync`, positive, **no negation**). One call returns a whole page (group by `DimensionValue`), not per-item N+1.
- **quantity(item)** = Σ `movement.SignedQuantity` over the item's movements whose spawned entry is on the books. Load the item's movements (existing unbounded scan), batch-fetch their entry statuses in one `GetEntriesBySourceRefsAsync` call, keep movements whose entry is on the books (Posted; Posted **or** PendingApproval on the write path), sum their signed quantity.
- **averageUnitCost** = `quantity == 0 ? 0 : value / quantity`.

`GetItem` **and** `ListItems` route through this service (watch the FA `ListAssets`-endpoint-bypass bug — the list endpoint must fold, not read the store directly). A `ListItems` page is ≈ 2–3 ledger calls total (one subledger fold + one batch entry-status read over the page's movements).

## 6. Posting recipe + service changes

- **`InventoryPosting.Compose`** tags the **Inventory line** with `Dimensions: { "Item": itemId }` in all four recipes; counter-lines unchanged. Signature gains `itemId` (already available at the call site).
- **`RecordAsync`** new flow: validate shape → resolve item (Active) → resolve accounts (before side effects) → **fold current `(value, quantity)` with `includePending: true`, identical gate for both** → compute the effect via `InventoryValuation` over the **folded** pair (block-negative → 409; non-positive extended cost rejected pre-persist) → **persist the movement document first** (Quantity + frozen `AppliedUnitCost`/`ExtendedCost`; no `Resulting*`) → post one `PendingApproval` entry, Inventory line `{Item}`-dimensioned, `EntryIdentity.ForSource(movementId)`. **No `SetValuationAsync`** — the dual-write is gone; the only writes are the document (quantity fact) and the entry (dollar fact), neither a copy of the other.
- **`VoidAsync`** new flow: load movement (must be on-books) → LIFO check (latest non-void for the item) → reverse entry if Posted / withdraw if Pending (existing logic; tolerate a stranded doc with no entry) → mark document Void. **No manual valuation restore** — reversal drops the entry from the value fold and gates the movement out of the quantity projection.
- **`InventoryValuation`** is unchanged in math — it already operates on a passed-in `(OnHand, TotalValue)` `Valuation`; the caller now supplies the **folded** pair instead of the stored one. (`MovementEffect.ResultingOnHand`/`ResultingTotalValue` are still computed for the block-negative/round math but no longer persisted.)

## 7. Testing / proof

Mirror AR/FA's proof suite, Inventory-flavored:
- **Value fold + sign:** an item with receipts folds to a **positive** value (debit-normal asset, no negation); an item with no movements → 0; two items fold independently.
- **Quantity projection:** `on-hand = Σ signed Quantity` over on-books movements; a receipt then a partial issue nets correctly; weighted-average `avg = value ÷ quantity` matches the pre-conversion figure.
- **Dimensioned entry:** every movement's Inventory line carries `{Item}`; the Inventory account rejects an untagged line (422).
- **Shared-gate / writes-see-pending / reads-see-posted:** a second Issue computes its average off the pending-inclusive fold (both value and quantity pending-inclusive, coherent ratio); an item's reported on-hand/value reflects posted-only; a Pending receipt reserves stock for the write path but is invisible to reads until approved.
- **Block-negative:** an Issue exceeding on-books available quantity → 409; a pending Issue reserves stock against a second Issue.
- **Void auto-rollback:** voiding the latest movement reverses the entry and both value and on-hand return to their prior values with **no manual restore step**; a voided movement drops out of both folds.
- **Doc-first crash safety:** a movement document with no on-books entry contributes 0 to both folds (inert); re-posting is idempotent (`EntryIdentity.ForSource`).
- **Inventory-scoped guard proof:** a raw GL reverse of a movement entry (no module credential) → 409.
- **Whole-solution reconciliation:** full suite green at the final commit.

## 8. Sequencing (green at every commit)

Order is the safety net — dimension the recipe before requiring the dimension; fold on read before deleting the stored fields; never delete a field before its reads fold. Mirrors AR's "reads fold before `Allocation[]` deleted" and FA's field-deletion ordering.

1. **Client seam methods** — `GetSubledgerAsync` + `GetEntriesBySourceRefsAsync` on `ILedgerClient`/`HttpLedgerClient`/fake (additive; no consumer yet).
2. **Recipe gains `{Item}`** — `InventoryPosting.Compose` dimensions the Inventory line (additive; the stored fields are still written, so behavior is unchanged and the suite stays green).
3. **Flip the Inventory account to `RequiredDimensions = ["Item"]`** (after the recipe is dimensioned) + reseed/config.
4. **Reads + compute fold** — the valuation service folds value (posted-only) + projects quantity (posted-only), populating `ItemView`; `RecordAsync` computes the effect from the pending-inclusive folds; `DeactivateAsync`'s has-stock guard reads the projection. Stored fields still written but no longer read.
5. **Delete the stored state + manual restore** — remove `OnHandQuantity`/`TotalValue` from `Item`/`ItemDocument`, delete `SetValuationAsync`, remove `ResultingOnHand`/`ResultingTotalValue` + `SignedQuantityEffect`/`SignedValueEffect`, drop the `SetValuationAsync` calls (record + void) and the manual valuation-restore from `VoidAsync`.
6. **Proof suite + whole-solution reconciliation** (value fold/sign, quantity projection, dimensioned entry, shared-gate pending-vs-posted, block-negative, void-auto-rollback, doc-first crash safety, guard).

## 9. Non-goals

- No engine change (dimensions + `includePending` fold + batch source-ref read all already on master).
- No numeric-quantity attribute on the engine (parent §6 route a) — quantity is the module-side rebuildable projection (route b), by decision.
- No per-item COGS/GRNI/Adjustment fold (YAGNI — only the Inventory line is dimensioned).
- `AppliedUnitCost`/`ExtendedCost` stay frozen evidentiary snapshots (not read back for balances).
- No change to weighted-average costing math, single-pool model, LIFO-only void, or block-negative policy.
- No backfill / migration (greenfield / reseed).
- UI: `ItemView` JSON stays stable (item screens untouched); `StockMovementView` loses `Resulting*` (minor movement-screen touch if displayed — a UI follow-up, not this cycle's focus).

## 10. Risks

- **Shared-gate coherence** — the value fold and the quantity projection MUST use the identical `includePending` and the identical on-books movement set, or weighted-average `value ÷ quantity` is meaningless. This is the single most important invariant; tested explicitly (§7).
- **Doc-first ordering** — persist the document before posting the entry; an entry-without-doc diverges. Never reorder. Idempotent post covers the retry. Tested (§7).
- **Quantity projection N+1** — folding quantity needs each movement's entry status; use the **batch** `GetEntriesBySourceRefsAsync` (one call per page), never a per-movement singular read. A `ListItems` page must stay ≈ constant ledger calls.
- **List-endpoint bypass** — `ListItems` must fold through the valuation service, not read the store's (now-absent) valuation. The FA review caught exactly this on `ListAssets`; guard with a list-level fold test.
- **Transition safety** — step 4 switches reads to the folds while the fields are still written; step 5 deletes them only after. Never reorder.
- **Fold-on-read on unconfigured chart** (carried fast-follow from FA) — a fold when the Inventory account lacks `{Item}` config can 500; degrade-to-0 or validate-at-onboarding. Deferred, noted.
- **Sign is the easy case here** — debit-normal asset, positive fold, no negation. The opposite of AR/FA; do **not** copy their negation.

Onboarding: the Inventory (1400) account must be configured `RequiredDimensions = ["Item"]`; the dev seed (`.localdev/start.ps1`) already sets the four inventory account ids — add the PUT that sets this on 1400.
