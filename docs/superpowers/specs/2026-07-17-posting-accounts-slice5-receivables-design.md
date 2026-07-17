# Posting Accounts — Slice 5 (Receivables) — Design

**Date:** 2026-07-17
**Epic:** Per-client, UI-driven posting-account configuration (module fan-out) — **FINAL module**
**Prior slices:** Cash (`962b9b7`), Payroll (`4913027`), Payables (`52e22c5`), Fixed Assets + Inventory (`f6f4d7e`)

## Goal

Fan the per-client posting-accounts admin feature out to the **Receivables** module — the last of six.
An admin configures Receivables' posting accounts per client on the existing data-driven screen, with
process-config fallback. No screen or endpoint change: the screen renders whatever slots
`GET /clients/{id}/posting-accounts` returns.

## Shape (why Receivables is the richest module)

Receivables has **two** provider interfaces over two records that **share** the Accounts Receivable
account (like Payables' shared `Payable`):

- `IInvoiceAccountsProvider.GetAsync(clientId)` → `InvoicePostingAccounts`
  (`ReceivableAccountId`, `DefaultRevenueAccountId`, `SalesTaxPayableAccountId`, and a dynamic
  `RevenueAccountsByCategory` map — see below).
- `IPaymentAccountsProvider.GetAsync(clientId)` → `PaymentPostingAccounts`
  (`ReceivableAccountId`, `CashAccountId`, `CustomerCreditsAccountId`, `BadDebtExpenseAccountId`,
  `SalesReturnsAccountId`).

Collapsing the shared `Receivable`, there are **7 unique fixed slots**.

### Decision: the dynamic `RevenueAccountsByCategory` map is DEFERRED (Option A)

`InvoicePostingAccounts.RevenueAccountsByCategory` is a variable-length `category → account` map
(arbitrary user-defined category strings; empty by default, in which case every invoice line credits
the single default Revenue account). It is sourced **only** from process config today
(`Receivables:Accounts:RevenueByCategory` section) — there is no per-client mechanism, and this slice
does not add one.

The `StoreBacked` invoice provider **still populates `RevenueAccountsByCategory` from config,
unchanged** (carries over the existing `ReadCategoryMap` logic), so behavior is identical — no
regression. Per-client editing of the category map is a separate future feature (needs a dynamic
add/remove-row UI, a store-shape change, and reworked PUT validation to accept arbitrary keys); it is
explicitly **out of scope** here. This keeps the "zero screen/endpoint change" invariant that held
across all four prior slices.

## The 7 fixed slots

Labels/types/dims taken verbatim from `ReceivablesChartRequirements`. Slot key == config-key suffix
(the provider maps slot → record field internally; note `Revenue` → `DefaultRevenueAccountId`).

| Slot key (== `Receivables:Accounts:` suffix) | Label | Type | Required dims | Record field |
|-----------------------------------------------|-------|------|---------------|--------------|
| `Receivable`      | Accounts Receivable | Asset     | `["Customer", "Invoice"]` | `ReceivableAccountId` (both records) |
| `Revenue`         | Revenue             | Revenue   | `[]`                      | `DefaultRevenueAccountId` |
| `SalesTaxPayable` | Sales Tax Payable   | Liability | `[]`                      | `SalesTaxPayableAccountId` |
| `Cash`            | Cash                | Asset     | `[]`                      | `CashAccountId` |
| `CustomerCredits` | Customer Credits    | Liability | `["Customer"]`            | `CustomerCreditsAccountId` |
| `BadDebtExpense`  | Bad Debt Expense    | Expense   | `[]`                      | `BadDebtExpenseAccountId` |
| `SalesReturns`    | Sales Returns       | Revenue   | `[]`                      | `SalesReturnsAccountId` |

Module key: `receivables` (from `new ModuleIdentity("receivables")`).

## Changes (mirrors the merged Payables recipe, ×2 providers)

1. **Slot registry** — `Backend/.../Control/PostingAccountSlots.cs`: append the 7 `("receivables", …)`
   rows with the labels/types/dims above. Update the class doc-comment to include `receivables`
   (all six modules now wired).

2. **`StoreBackedInvoiceAccountsProvider`** (new, `Receivables.Api`) — implements
   `IInvoiceAccountsProvider` off `IPostingAccountsSource` + `IConfiguration`. `GetAsync` resolves
   `Receivable`/`Revenue`/`SalesTaxPayable` via a private `Resolve(stored, slot)` helper (prefer
   stored non-empty Guid, else `Receivables:Accounts:{slot}`, else throw
   `"Receivables posting account 'Receivables:Accounts:{slot}' is not configured."`), and sets
   `RevenueAccountsByCategory` from the carried-over `ReadCategoryMap("Receivables:Accounts:RevenueByCategory")`.

3. **`StoreBackedPaymentAccountsProvider`** (new, `Receivables.Api`) — implements
   `IPaymentAccountsProvider` the same way, resolving
   `Receivable`/`Cash`/`CustomerCredits`/`BadDebtExpense`/`SalesReturns` with the same `Resolve`
   helper and throw-message shape.

4. **`ReceivablesServiceExtensions`** — swap both registrations (lines 34–35)
   `AddSingleton<IInvoiceAccountsProvider, ConfiguredInvoiceAccountsProvider>` and
   `AddSingleton<IPaymentAccountsProvider, ConfiguredPaymentAccountsProvider>` →
   `AddScoped<…, StoreBacked…>`. Update any doc-comment mentioning config-backed providers.

5. **Delete** `ConfiguredInvoiceAccountsProvider` + `ConfiguredPaymentAccountsProvider` and their
   test classes (`ConfiguredInvoiceAccountsProviderTests`, `ConfiguredPaymentAccountsProviderTests`)
   once grep confirms no other references.

6. **No screen/endpoint change** — data-driven.

## Tests

- **`StoreBackedInvoiceAccountsProviderTests`** (replaces `ConfiguredInvoiceAccountsProviderTests`,
  same `file sealed class FakeSource` pattern):
  - Prefers stored over config for a fixed slot.
  - Falls back to config when a slot is unset.
  - Falls back to config when a stored slot is `Guid.Empty`.
  - Throws when a fixed slot has neither store nor config.
  - Maps all 3 invoice fixed slots (`Receivable`/`Revenue`/`SalesTaxPayable`) from the store.
  - **Reads `RevenueAccountsByCategory` from config even when the store has fixed-slot overrides**
    (proves the deferred map still comes from config, unchanged).
- **`StoreBackedPaymentAccountsProviderTests`** (replaces `ConfiguredPaymentAccountsProviderTests`):
  - Prefers stored / fallback / Guid.Empty fallback / throw.
  - Maps all 5 payment fixed slots, asserting the shared `Receivable` resolves here too.
- **`PostingAccountEndpointTests`** — one case mirroring the payables case: seed a client with
  `receivables` enabled, assert `GET` lists exactly 7 receivables slots, `PUT
  .../posting-accounts/receivables` with a valid slot → 200, unknown slot (`"Nope"`) → 422.

## Verification

- `dotnet test Accounting101.slnx` green (Receivables test project + `Ledger.Api.Tests`).
- One dev-stack SMOKE against JordanSoft (per the recipe): record current `enabledModules`
  (expected `[cash, reconciliation]` via `GET /clients/{id}/me/capabilities`), temporarily enable
  `receivables` via `PUT /admin/clients/{id}/modules` (full-replace `{"moduleKeys":[...]}`); `GET`
  shows 7 receivables slots (null) with correct dims on the wire (`Receivable`→`["Customer","Invoice"]`,
  `CustomerCredits`→`["Customer"]`); `PUT` one slot (body property `Slots`) → 200 and re-GET reflects;
  unknown-slot `PUT` → 422; then clear the override (`{"slots":{}}`) and RESTORE the exact prior
  module set, verifying back to baseline. Leave Receivables on config fallback. Zero footprint.

## Out of scope / deferred

- Per-client `RevenueAccountsByCategory` editing (its own future feature — see the decision above).
- Save-time validation is advisory-only (raw PUT accepts any Guid; no chart-existence check).
- No client-404 test.

## Epic status

This is the **final** fan-out module. On merge, all six modules (Cash, Payroll, Payables, Fixed
Assets, Inventory, Receivables) are per-client configurable; the posting-accounts admin epic closes.
