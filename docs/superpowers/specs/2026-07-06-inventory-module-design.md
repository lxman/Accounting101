# Inventory Module — Design

**Date:** 2026-07-06
**Status:** Approved (brainstorm complete)
**Author:** Michael Jordan (with Claude)

## Summary

A new **Inventory** module for Accounting101: a standalone perpetual-inventory
subsystem tracking stockable items and their movements, posting its own
double-entry GL entries. It is the largest remaining gap in an otherwise complete
AR / AP / Payroll / Fixed-Assets / Banking suite.

The module is **standalone in this build** but its item/movement contracts are
designed so a later slice can wire item-linked invoice/bill lines onto them
without rework (the GRNI clearing seam, below, is the mechanism).

Structurally it **mirrors the Fixed Assets module** — the closest, most-recently-
built sibling: master-data-as-documents + evidentiary transactional movements that
each compose one balanced `PostEntryRequest`, LIFO void, server-owned derived
state, one configured account set.

## Core decisions (from brainstorm)

1. **Standalone now, integration-ready later** — own module, own GL posting; AR/AP
   untouched. Contracts shaped so item-linked invoice/bill lines can attach later.
2. **Weighted-average** valuation — carried as an `(OnHandQuantity, TotalValue)`
   pair; average unit cost is *derived*, never stored.
3. **Single stock pool** per item — no locations, no transfers.
4. **Block at 409** — any issue or downward adjustment that would drive on-hand
   below zero is rejected; on-hand can never go negative, so average cost is always
   well-defined.
5. **GRNI clearing** — a standalone receipt credits a configured "Goods Received
   Not Invoiced" account. Later AP integration posts `Dr GRNI / Cr Accounts Payable`
   and the clearing nets to zero — zero rework to receipt logic.
6. **Void latest-only (LIFO)** — a movement can be voided only if it is the most
   recent one for its item (no later movement has re-blended the cost). Older
   mistakes are corrected via a new adjustment. Matches the Fixed Assets LIFO-void
   pattern.

**Explicitly out of scope (deferred / YAGNI):** multiple locations & transfers;
per-item account overrides; value-only revaluation (NRV write-downs with unchanged
qty); FIFO / standard-cost methods; AP/AR line integration (future slice); reorder
points / purchasing suggestions.

## Architecture & module structure

New self-contained module under `Modules/Inventory/`, three projects (exact
Fixed Assets shape):

| Project | Holds |
|---|---|
| `Accounting101.Inventory` | domain: `Item`, `StockMovement`, valuation engine, posting recipes, document stores, services |
| `Accounting101.Inventory.Api` | endpoints, request DTOs, `HttpLedgerClient`, configured-accounts provider, DI extension |
| `Accounting101.Inventory.Tests` | xUnit, shared EphemeralMongo |

**Conventions inherited from the codebase (non-negotiable):**

- **Enum string-serialization.** Every enum gets
  `[JsonConverter(typeof(JsonStringEnumConverter))]`. A serialization-assertion test
  serializes each enum and asserts the wire value is a *string*, not a number.
  (This is the Banking-module bug tax — a recon module that never had a UI shipped
  enums as numbers; the smoke test was the only layer that caught it.)
- **Default-closed entitlement.** The module is gated at the single
  `ModuleAccess.AuthorizeAsync` chokepoint and does nothing until `"inventory"` is in
  the client's `EnabledModules`. Capability `inventory.write` gates all movements and
  item mutations; reads are area-membership gated.
- **Maker-checker posting.** Movements post through `ILedgerClient` → engine as
  **PendingApproval** entries; SoD applies exactly as for Fixed Assets depreciation
  runs. Each movement composes **one balanced `PostEntryRequest`** via a pure static
  posting recipe using `EntryIdentity.ForSource(...)`.
- **Server owns derived state.** `OnHandQuantity`, `TotalValue`, item `Status`, and
  every movement snapshot field are computed server-side and never accepted from the
  client — exactly as Fixed Assets owns `AccumulatedDepreciation` / `Status`.
- **Guards.** Period / monthly-close / inception-date-floor guards apply to every
  movement's `EffectiveDate`, inherited from the engine.

## Data model

Two documents, following the FA `Asset` (master) + `Disposal` / `DepreciationRun`
(evidentiary movement) split.

### `Item` — master data (Reference document)

**Client-provided (via `ItemBody`):**

- `Sku` — required, **unique per client** (guarded; duplicate → 409)
- `Name`, `Description`
- `UnitOfMeasure` — display label only (`"each"`, `"kg"`, `"box"`); quantities are
  always `decimal`

**Server-owned (never accepted from the client):**

- `OnHandQuantity` (decimal)
- `TotalValue` (decimal, 2dp) — **the carried value is the source of truth**
- `Status` — `Active | Inactive`

**`AverageUnitCost` is derived, not stored** = `TotalValue / OnHandQuantity`
(0 when `OnHandQuantity == 0`).

**Why carry `(OnHandQuantity, TotalValue)` rather than store an average directly:**
the GL Inventory-asset balance always reconciles to `Σ TotalValue` across items with
no rounding drift, and a full issue clears value *exactly* (see valuation math). The
average is purely a display projection.

### `StockMovement` — evidentiary transaction (numbered `MV-#####`)

- `MovementType` — `Receipt | Issue | Adjustment` (string-serialized enum)
- `ItemId`
- `EffectiveDate` — period / close / inception guards apply
- `Memo`
- `Quantity` — **Receipt and Issue take a positive quantity; the `MovementType`
  determines direction** (Receipt adds, Issue subtracts). **Only `Adjustment` uses a
  signed quantity** (positive = overage, negative = shrinkage). A non-positive
  Receipt/Issue quantity, or a zero Adjustment, is rejected 400.
- `UnitCost` — **required on Receipt and on an increase (overage) Adjustment**;
  ignored on Issue and on a decrease (shrinkage) Adjustment (server costs those at
  the current average)
- **Server-owned snapshot** (for audit + LIFO-void reconstruction):
  - `AppliedUnitCost` — the unit cost actually used
  - `ExtendedCost` — the exact amount posted to the GL (cents, banker's-rounded)
  - `ResultingOnHand`, `ResultingTotalValue` — item state *after* this movement
  - `LedgerEntryId` — the posted entry
  - `MovementNumber` — `MV-#####`
  - `Status` — `Posted | Void`

### Valuation math (weighted-average, carried-value)

All GL amounts are cents, banker's-rounded (`MidpointRounding.ToEven`), matching the
Fixed Assets cent convention.

- **Receipt:** `ext = round(qty × unitCost, 2)`; `OnHand += qty`;
  `TotalValue += ext`. (Re-blends the implied average.)
- **Issue:** `unit = TotalValue / OnHand`; `cost = round(qty × unit, 2)`; **but if
  `qty == OnHand`, `cost = TotalValue`** (clears value exactly, no rounding residue);
  `OnHand -= qty`; `TotalValue -= cost`. **Blocked 409 if `qty > OnHand`.**
- **Adjustment (signed):**
  - *Decrease (shrinkage):* costs at current average (`unit = TotalValue / OnHand`,
    exact-clear on full); **blocked 409 if it would drive on-hand negative.**
  - *Increase (overage):* requires `UnitCost`; re-blends like a receipt.

### Item lifecycle

- **Deactivate:** allowed only when `OnHandQuantity == 0` (can't retire stock you
  still hold) → otherwise 409. Deactivated items are hidden from new-movement pickers
  but retained.
- **Reactivate:** always allowed; returns item to `Active`.

### Configured accounts — `InventoryPostingAccounts`

One configured set for all items (per FA precedent). Wired via `Inventory__Accounts__*`
environment variables in `.localdev/start.ps1`.

| Account | Role |
|---|---|
| `InventoryAsset` | Dr on receipt / Cr on issue |
| `Cogs` | Dr on issue |
| `GrniClearing` | Cr on receipt (the GRNI seam) |
| `InventoryAdjustment` | shrinkage (Dr) / overage (Cr) offset |

## Posting recipes

Pure static composers, one balanced entry each. All post as PendingApproval with
`EntryIdentity.ForSource("StockMovement", movementId)`, `SourceType="StockMovement"`,
`SourceRef = movementId`.

| Movement | Debit | Credit |
|---|---|---|
| **Receipt** | Inventory `ext` | GRNI `ext` |
| **Issue** | COGS `cost` | Inventory `cost` |
| **Adjustment ↓** (shrinkage) | InventoryAdjustment `cost` | Inventory `cost` |
| **Adjustment ↑** (overage) | Inventory `ext` | InventoryAdjustment `ext` |
| **Void** | reverse of the original entry (LIFO-only). Because only the latest movement can be voided, the item's current state equals this movement's `ResultingOnHand` / `ResultingTotalValue`, so the void simply subtracts this movement's deltas — restoring `(OnHand, TotalValue)` exactly to their pre-movement values |

## API surface

`InventoryEndpoints`. All mutations gated on `inventory.write`; reads on area
membership.

```
POST   /inventory/items                  create item                    → ItemView
GET    /inventory/items                  list (paged, app-paginator envelope)
GET    /inventory/items/{id}             detail                         → ItemView
PUT    /inventory/items/{id}             edit Sku/Name/Desc/UoM (not derived state)
POST   /inventory/items/{id}/deactivate  guard: OnHand==0 else 409
POST   /inventory/items/{id}/reactivate
POST   /inventory/movements              record receipt/issue/adjustment → StockMovementView
GET    /inventory/movements?itemId=      list movements for an item (paged)
GET    /inventory/movements/{id}         detail                         → StockMovementView
POST   /inventory/movements/{id}/void    LIFO-only else 409
```

`ItemView` / `StockMovementView` carry the derived fields (`AverageUnitCost`,
resulting balances, etc.). Item list supports the paged envelope the shared
`app-paginator` component consumes.

## Angular UI (`features/inventory/`, `/inventory` area)

Every module ships a UI. Mirrors `core/banking` / `features/banking`.

- `core/inventory` service + models (string-union enums)
- OnPush / signals components:
  - **item-list** — paged, whole-row-click to detail, `app-paginator`
  - **item-editor** — create / edit master fields
  - **item-detail** — on-hand, derived average cost, total value, status +
    movement history for the item
  - **movement-editor** — type-driven form: Receipt & increase-Adjustment show a
    unit-cost field; Issue & shrinkage-Adjustment do not
  - **movement-detail** — full snapshot + void action (LIFO-gated in UI)
- Item-centric spine (movements hang off an item); a light `/inventory` shell if a
  second top-level tab (Movements) proves useful.
- Nav leaf gated on the `inventory` capability; `/inventory` added to
  `app.routes.ts` `built` array. Route order: specific-before-`:id`.

## Slice decomposition (mirrors FA-1..FA-4 + UI)

- **INV-1** — module scaffold (3 projects, DI, endpoints stub) + `Item` master CRUD
  + deactivate/reactivate guard + Sku-unique guard. No GL yet.
- **INV-2** — **Receipt** movements: valuation re-blend, GRNI posting, PendingApproval
  entry, `MV-#####` numbering. First GL posts.
- **INV-3** — **Issue** movements: average costing, exact-clear-on-full, block-negative
  409, COGS posting.
- **INV-4** — **Adjustment** movements (both directions) + **LIFO void** across all
  movement types.
- **INV-5** — Angular UI (all screens) + `Inventory__Accounts__*` in start.ps1 +
  mandatory visual smoke test (verifies enum string-serialization against the live
  server).

## Testing

- xUnit on shared EphemeralMongo, per slice.
- **Enum-serialization assertion** test: each enum serializes to a string, not a number.
- **Valuation-math unit tests:** re-blend on receipt; exact-clear on full issue;
  block-negative on issue and shrinkage; overage re-blend; LIFO-void reconstruction
  restores `(OnHand, TotalValue)` exactly.
- **Sku-uniqueness** and **deactivate-with-stock** guard tests (409s).
- Angular Vitest per component.
- Whole-branch opus review before merge.
- **Visual smoke test in the dev stack before merge** — the standing, non-optional
  gate; the only layer that observes real serialization.

## Future integration seam (not built here)

When AR/AP integration is later scoped:

- An optional `ItemId` on invoice/bill lines links a sale/purchase to an item.
- A billed purchase posts `Dr GRNI / Cr Accounts Payable`, clearing the receipt's
  GRNI credit to zero (the seam this design reserves).
- A sold invoice line auto-issues stock (`Dr COGS / Cr Inventory` at average),
  reusing the INV-3 issue path.

No changes to the item/movement contracts are required to add this later.
