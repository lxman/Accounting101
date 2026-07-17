# Posting Accounts — Slice 3 (Payables) — Design

**Date:** 2026-07-17
**Epic:** Per-client, UI-driven posting-account configuration (module fan-out)
**Prior slices:** Slice 1 Cash (`962b9b7`), Slice 2 Payroll (`4913027`)

## Goal

Fan the posting-accounts admin feature out to the **Payables (AP)** module: let an admin
configure AP's posting accounts per client on the existing data-driven screen, with the module
falling back to process config until a per-client account is chosen. No screen or endpoint change —
the screen renders whatever slots `GET /clients/{id}/posting-accounts` returns.

## The wrinkle (why AP differs from Payroll)

Payroll had one provider method returning one flat `PayrollPostingAccounts`. Payables'
`IBillAccountsProvider` has **two** methods returning **two** records that share an account:

- `GetBillAccountsAsync` → `BillPostingAccounts { PayableAccountId }`
- `GetPaymentAccountsAsync` → `BillPaymentPostingAccounts { PayableAccountId, CashAccountId, VendorCreditsAccountId }`

`PayableAccountId` is the **same** account referenced by both recipes. So AP collapses to exactly
**3 unique slots**, not 4:

| Slot key (== config suffix) | Label            | Type      | Required dims    |
|-----------------------------|------------------|-----------|------------------|
| `Payable`                   | Accounts Payable | Liability | `[Vendor, Bill]` |
| `Cash`                      | Cash             | Asset     | `[]`             |
| `VendorCredits`             | Vendor Credits   | Asset     | `[Vendor]`       |

Config keys: `Payables:Accounts:Payable|Cash|VendorCredits` (unchanged; the existing
`ConfiguredBillAccountsProvider` already reads exactly these).

**Dimensions decision:** carry the real dims from `PayablesChartRequirements` (faithful "sourced
from *ChartRequirements" reading). These are the first non-empty `RequiredDimensions` rows in the
registry. Verified zero UI risk: the admin screen renders `expectedType` but does **not** render
`requiredDimensions`, so the value is faithful on the wire (and consumed by chart-readiness
elsewhere) without changing the screen.

Note: slot key equals the **config-key suffix** (`Payable`), not the record field name
(`PayableAccountId`). The store is keyed by the config suffix, so the provider maps slot → field.

## Changes

1. **Slot registry** — `Backend/.../Control/PostingAccountSlots.cs`: append 3 `("payables", …)`
   rows with the labels/types/dims in the table above. Update the class doc-comment (currently
   "Slice 1 wired Cash … other modules fan out here") to mention payables as done.

2. **`StoreBackedBillAccountsProvider`** (new, `Modules/Payables/.../Api/`) — implements
   `IBillAccountsProvider` off `IPostingAccountsSource` + `IConfiguration`. One private
   `Resolve(stored, slot)` helper mirroring `StoreBackedPayrollAccountsProvider`: prefer stored
   (non-empty Guid), else `Payables:Accounts:{slot}`, else throw the same-shaped
   `InvalidOperationException`. `GetBillAccountsAsync` resolves `Payable`; `GetPaymentAccountsAsync`
   resolves all three, reusing the same `Payable` resolution.

3. **`PayablesServiceExtensions`** — swap
   `AddSingleton<IBillAccountsProvider, ConfiguredBillAccountsProvider>` →
   `AddScoped<IBillAccountsProvider, StoreBackedBillAccountsProvider>`. (Scoped because it depends on
   the scoped `IPostingAccountsSource`.) Update the class doc-comment ("config-backed accounts
   provider" → store-backed with config fallback).

4. **Delete `ConfiguredBillAccountsProvider`** + `ConfiguredBillAccountsProviderTests` once grep
   confirms no other references.

5. **No screen/endpoint change** — data-driven.

## Tests

- **`StoreBackedBillAccountsProviderTests`** (replaces `ConfiguredBillAccountsProviderTests`, same
  `file sealed class FakeSource` pattern as the Payroll test):
  - Prefers stored over config for a slot.
  - Falls back to config when a slot is unset.
  - Throws when a slot has neither store nor config.
  - Maps all 3 slots from the store — asserting **both** `GetBillAccountsAsync().PayableAccountId`
    and all three fields of `GetPaymentAccountsAsync()`, proving the shared `Payable` slot lands in
    both records.
- **`PostingAccountEndpointTests`** — one case mirroring the payroll case: seed a client with
  `payables` enabled, assert `GET` lists 3 payables slots, `PUT .../posting-accounts/payables` with
  a valid slot → 200, with an unknown slot (`"Nope"`) → 422.

## Verification

- `dotnet test` green (module tests + `Accounting101.Ledger.Api.Tests`).
- Dev-stack SMOKE against JordanSoft (per the fan-out recipe): temporarily enable `payables`,
  `GET` shows 3 slots (null), `PUT` one, `GET` reflects it, unknown-slot `PUT` → 422; then RESTORE
  the exact prior module set and clear overrides. Leave AP on config fallback (null).

## Out of scope / deferred (unchanged from prior slices)

- Save-time validation is advisory-only (raw PUT accepts any Guid; no chart-existence check).
- No client-404 test.
- Receivables (dynamic revenue-by-category map) remains last and needs its own design tweak.
