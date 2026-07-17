# Posting Accounts — Slice 4 (Fixed Assets + Inventory) — Design

**Date:** 2026-07-17
**Epic:** Per-client, UI-driven posting-account configuration (module fan-out)
**Prior slices:** Slice 1 Cash (`962b9b7`), Slice 2 Payroll (`4913027`), Slice 3 Payables (`52e22c5`)

## Goal

Fan the per-client posting-accounts admin feature out to the **Fixed Assets** and **Inventory**
modules — two independent, flat, Payroll-style clones — so an admin configures each module's posting
accounts per client on the existing data-driven screen, with process-config fallback. No screen or
endpoint change: the screen renders whatever slots `GET /clients/{id}/posting-accounts` returns.

Both modules are done on one branch (independent, same recipe). Neither has the AP shared-slot
wrinkle nor a dynamic account map — each has a single `GetAccountsAsync` returning one flat
`*PostingAccounts` record, exactly like Payroll.

## Fixed Assets — 6 slots

Provider `IFixedAssetsAccountsProvider.GetAccountsAsync(clientId)` → `FixedAssetsPostingAccounts`
(6 required Guid fields). Config keys `FixedAssets:Accounts:<slot>`. Labels/types/dims taken verbatim
from `FixedAssetsChartRequirements`:

| Slot key (== config suffix == field − "AccountId") | Label | Type | Required dims |
|-----------------------------------------------------|-------|------|---------------|
| `DepreciationExpense`     | Depreciation Expense       | Expense | `[]`        |
| `AccumulatedDepreciation` | Accumulated Depreciation   | Asset   | `["Asset"]` |
| `AssetCost`               | Fixed Assets (asset cost)  | Asset   | `[]`        |
| `DisposalProceeds`        | Disposal Proceeds          | Asset   | `[]`        |
| `GainOnDisposal`          | Gain on Disposal           | Revenue | `[]`        |
| `LossOnDisposal`          | Loss on Disposal           | Expense | `[]`        |

Module key: `fixedassets` (from `new ModuleIdentity("fixedassets")`).

## Inventory — 4 slots

Provider `IInventoryAccountsProvider.GetAccountsAsync(clientId)` → `InventoryPostingAccounts`
(4 required Guid fields). Config keys `Inventory:Accounts:<slot>`. Labels/types/dims verbatim from
`InventoryChartRequirements`:

| Slot key (== config suffix == field − "AccountId") | Label | Type | Required dims |
|-----------------------------------------------------|-------|------|---------------|
| `InventoryAsset`       | Inventory Asset       | Asset     | `["Item"]` |
| `Cogs`                 | Cost of Goods Sold    | Expense   | `[]`       |
| `GrniClearing`         | GRNI Clearing         | Liability | `[]`       |
| `InventoryAdjustment`  | Inventory Adjustment  | Expense   | `[]`       |

Module key: `inventory` (from `new ModuleIdentity("inventory")`).

## Changes (per module — mirrors the merged Payroll/Payables recipe)

1. **Slot registry** — `Backend/.../Control/PostingAccountSlots.cs`: append the module's rows to
   `PostingAccountSlots.All` with the labels/types/dims in the tables above. Update the class
   doc-comment to include the newly-wired modules.

2. **`StoreBacked{Module}AccountsProvider`** (new, in each module's `.Api`) — implements the
   module's `I{Module}AccountsProvider` off `IPostingAccountsSource` + `IConfiguration`. Single
   `GetAccountsAsync` builds the flat record; one private `Resolve(stored, slot)` helper mirroring
   `StoreBackedPayrollAccountsProvider`: prefer stored (non-empty Guid), else
   `{Module}:Accounts:{slot}`, else throw the same-shaped `InvalidOperationException`
   (`"Fixed-assets posting account 'FixedAssets:Accounts:{slot}' is not configured."` /
   `"Inventory posting account 'Inventory:Accounts:{slot}' is not configured."`).

3. **`{Module}ServiceExtensions`** — swap
   `AddSingleton<I{Module}AccountsProvider, Configured{Module}AccountsProvider>` →
   `AddScoped<…, StoreBacked{Module}AccountsProvider>` (Scoped: depends on the scoped
   `IPostingAccountsSource`). Update the class doc-comment ("config-backed" → store-backed with
   config fallback). FA line 38; Inventory line 31.

4. **Delete `Configured{Module}AccountsProvider`** + its test once grep confirms no other references.

5. **No screen/endpoint change** — data-driven.

## Tests (per module)

- **`StoreBacked{Module}AccountsProviderTests`** (replaces `Configured{Module}AccountsProviderTests`,
  same `file sealed class FakeSource` pattern as the Payroll/Payables tests):
  - Prefers stored over config for a slot.
  - Falls back to config when a slot is unset.
  - Falls back to config when a stored slot is `Guid.Empty` (carries slice-3's final-review learning
    up front, rather than waiting for review to add it).
  - Throws when a slot has neither store nor config.
  - Maps all N slots from the store — asserting every field of the returned record.
- **`PostingAccountEndpointTests`** — one case per module mirroring the payables case: seed a client
  with the module enabled, assert `GET` lists exactly N module slots, `PUT
  .../posting-accounts/{moduleKey}` with a valid slot → 200, unknown slot (`"Nope"`) → 422.

## Verification

- `dotnet test Accounting101.slnx` green (both module test projects + `Ledger.Api.Tests`).
- One dev-stack SMOKE against JordanSoft covering **both** modules (per the fan-out recipe):
  record current `enabledModules` (expected `[cash, reconciliation]` via
  `GET /clients/{id}/me/capabilities`), temporarily enable `fixedassets` + `inventory` via
  `PUT /admin/clients/{id}/modules` (full-replace `{"moduleKeys":[...]}`); `GET` shows 6 FA + 4
  Inventory slots (null) with correct dims on the wire; `PUT` one slot of each (body property is
  `Slots`) → 200 and re-GET reflects; unknown-slot `PUT` → 422 each; then clear both overrides
  (`{"slots":{}}`) and RESTORE the exact prior module set, verifying back to baseline. Leave both on
  config fallback (null). Zero footprint.

## Out of scope / deferred (unchanged from prior slices)

- Save-time validation is advisory-only (raw PUT accepts any Guid; no chart-existence check).
- No client-404 test.
- Receivables (dynamic revenue-by-category map) remains the last module and needs its own design
  tweak — not part of this slice.
