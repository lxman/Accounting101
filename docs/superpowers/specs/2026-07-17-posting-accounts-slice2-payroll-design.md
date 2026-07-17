# Posting accounts — Slice 2 (Payroll fan-out) — design

**Date:** 2026-07-17
**Status:** Approved (design)
**Area:** Admin (posting accounts) / Payroll module

## Goal

Fan the per-client posting-accounts feature out to the **Payroll** module (5 slots),
following the slice-1 (Cash) pattern, and do the mandatory pre-fan-out hardening:
make `PostingAccountStore.SetModuleAsync` atomic per module so a second module can
ship safely. The admin screen needs no change (it is data-driven); this slice also
closes slice-1's deferred multi-slot guard test.

## Background

Slice 1 established: control-DB `PostingAccountStore` (module→slot→accountId), host
`IPostingAccountsSource`, `PostingAccountSlots` registry, `admin.postingAccounts`
GET/PUT endpoints, and a `StoreBacked{Module}AccountsProvider` per module (store →
config fallback → throw). The screen renders whatever slots the GET returns, so a new
module = registry rows + a store-backed provider + DI swap; no endpoint or UI change.

Payroll's five slots (from `PayrollChartRequirements` / `PayrollPostingAccounts`):

| Slot key | Label | Expected type | Config fallback key |
|---|---|---|---|
| `SalariesExpense` | Salaries Expense | Expense | `Payroll:Accounts:SalariesExpense` |
| `PayrollTaxExpense` | Payroll Tax Expense | Expense | `Payroll:Accounts:PayrollTaxExpense` |
| `Cash` | Cash | Asset | `Payroll:Accounts:Cash` |
| `WithholdingsPayable` | Withholdings Payable | Liability | `Payroll:Accounts:WithholdingsPayable` |
| `PayrollTaxesPayable` | Payroll Taxes Payable | Liability | `Payroll:Accounts:PayrollTaxesPayable` |

No dimensions; no dynamic category map.

## 1. Atomic `SetModuleAsync` (mandatory, before a 2nd module exists)

Replace the read-modify-write of the whole doc with a targeted per-module update:

```csharp
await _accounts.UpdateOneAsync(
    d => d.ClientId == clientId,
    Builders<PostingAccountsDoc>.Update.Set($"Accounts.{moduleKey}", new Dictionary<string, Guid>(slots)),
    new UpdateOptions { IsUpsert = true },
    cancellationToken);
```

On upsert the filter sets `ClientId`; only the `Accounts.<moduleKey>` sub-document is
written, so concurrent different-module writes for the same client can't clobber each
other. Remove the now-obsolete non-atomic `<remarks>`. Behavior of `GetAsync` is
unchanged. The existing module-isolation test stays green; add a test asserting a
second module's write leaves the first module's slots intact under the atomic path.

## 2. Payroll fan-out (backend)

- **Registry:** add the five Payroll rows to `PostingAccountSlots.All` (keys/labels/types
  above). `ForModule("payroll")` returns the five; `ModuleKeys` gains `"payroll"`.
- **Provider:** new `StoreBackedPayrollAccountsProvider` in `Accounting101.Payroll.Api`,
  injecting `IPostingAccountsSource` + `IConfiguration`. For each field, return the stored
  `payroll`→`<slot>` account if set (non-`Guid.Empty`), else the `Payroll:Accounts:<slot>`
  config value, else throw `InvalidOperationException("Payroll posting account
  'Payroll:Accounts:<slot>' is not configured.")` (same message as today). A small private
  helper resolves each slot (store key + config key).
- **DI:** in `PayrollServiceExtensions`, swap
  `AddSingleton<IPayrollAccountsProvider, ConfiguredPayrollAccountsProvider>()` →
  `AddScoped<IPayrollAccountsProvider, StoreBackedPayrollAccountsProvider>()`. Delete
  `ConfiguredPayrollAccountsProvider` if no longer referenced (grep first).
- **No endpoint/contract change** — GET now includes payroll slots for a client with
  payroll enabled; PUT `/posting-accounts/payroll` validates against the five registry keys.

## 3. Frontend — close the deferred multi-slot guard test (no component change)

The screen already renders multiple module sections and multiple slots per section, and
`save()` omits unset slots. Add the slice-1-deferred guard test to
`posting-accounts.spec.ts`: mock a GET returning a module with **two** slots, set one and
leave the other at "deployment default", Save, and assert the PUT body contains only the
chosen slot (unset omitted). This guards the omit-unset logic that a single-slot fixture
couldn't.

## Testing

- **Backend:**
  - `SetModuleAsync` atomic: setting `payroll` after `cash` leaves `cash` intact (and vice
    versa); a fresh client upserts correctly; overwriting a module replaces only its map.
  - `StoreBackedPayrollAccountsProvider`: prefers stored over config (representative slot);
    falls back to config when unset; throws with the right message when neither; maps all
    five slots correctly when all stored.
  - Endpoint: a client with payroll enabled lists the five payroll slots (current null then
    saved after a PUT); PUT rejects an unknown payroll slot (422). (Reuses the existing
    endpoint code — a focused add.)
- **Frontend:** multi-slot omit-unset guard test (above).
- **Dev-stack smoke (JordanSoft):** if payroll is enabled, the screen shows a Payroll
  section with five slots; if not, temporarily enable payroll (`PUT /admin/clients/{id}/modules`),
  verify the GET lists five payroll slots + set/clear one reversibly, then restore the
  original `EnabledModules`. Leave JordanSoft on config fallback (no residual overrides).

## Out of scope

The remaining four modules (Receivables — incl. its revenue-by-category dynamic slots —
Payables, Fixed Assets, Inventory) are later slices. No type/dimension enforcement at save
(advisory only). Bank Reconciliation stays off the screen.

## Files (indicative)

**Backend**
- `Backend/Accounting101.Ledger.Api/Control/PostingAccountStore.cs` — atomic `SetModuleAsync`.
- `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs` — 5 payroll rows.
- `Backend/Accounting101.Ledger.Api.Tests/PostingAccountStoreTests.cs` — atomic/isolation tests.
- `Modules/Payroll/Accounting101.Payroll.Api/StoreBackedPayrollAccountsProvider.cs` (new) + `PayrollServiceExtensions.cs` (swap) − `ConfiguredPayrollAccountsProvider.cs` (delete if unref).
- Payroll provider tests (in the Payroll module test project; else Api.Tests).
- Optionally `PostingAccountEndpointTests.cs` — a payroll-slots assertion.

**Frontend**
- `UI/Angular/src/app/features/admin/posting-accounts.spec.ts` — multi-slot omit-unset test (no component change).
