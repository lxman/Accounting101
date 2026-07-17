# Posting accounts — Slice 1 (Cash, end-to-end) — design

**Date:** 2026-07-17
**Status:** Approved (design)
**Area:** Admin (posting accounts) / Cash module

## Goal

Stand up a per-client posting-accounts store and wire it end-to-end for the
**Cash** module: an account chosen on a new `/admin/posting-accounts` screen
becomes the cash account the Cash module posts to **for that client**, with a
config **fallback** so nothing changes until an account is explicitly set. The
store, endpoints, and UI are built generically (module-keyed) so the remaining
five modules are pure fan-out in later slices.

## Background (current state)

Posting accounts are process-global today: each module resolves them from
`IConfiguration` via a `Configured*AccountsProvider` singleton that ignores
`clientId`. There is **no** per-client persistence. The `clientId` parameter is
already threaded through every provider. The module `.Api` projects already
reference the host assembly `Accounting101.Ledger.Api` (e.g.
`CashServiceExtensions` uses `ModuleIdentity`, `AddModule`), so a host-defined
port can be injected into a module provider without new cross-assembly plumbing.

The `admin.postingAccounts` capability, its Admin-preset membership, and the
seeded "Posting-Accounts Admin" narrow set already exist.

## Architecture

### 1. Per-client store (control DB)

`PostingAccountStore` — a new `IMongoCollection` `posting_accounts` in the control
database, one document per client:

```
{ ClientId: Guid, Accounts: { <moduleKey>: { <slotKey>: Guid } } }
```

Registered as a singleton like `ControlStore`. Methods:
- `Task<PostingAccountsDoc?> GetAsync(Guid clientId, CancellationToken)`
- `Task SetModuleAsync(Guid clientId, string moduleKey, IReadOnlyDictionary<string, Guid> slots, CancellationToken)` — upserts the doc, replacing that module's slot map.

### 2. Host port for modules to read per-client values

Interface in the host assembly (`Accounting101.Ledger.Api.Control`):

```csharp
public interface IPostingAccountsSource
{
    Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default);
}
```

Host implementation `StorePostingAccountsSource(PostingAccountStore store)` returns
the client's stored slot map for the module (empty dict when unset). Registered in
the host.

### 3. Cash provider becomes per-client (config fallback)

New `StoreBackedCashAccountsProvider` in `Accounting101.Banking.Cash.Api`, injecting
`IPostingAccountsSource` + `IConfiguration`:

```
cash = store.GetAsync(clientId, "cash")["Cash"]  // if present
     ?? Guid.Parse(configuration["Cash:Accounts:Cash"])  // fallback (existing behavior)
```

Registered in `CashServiceExtensions` in place of `ConfiguredCashAccountsProvider`
(scoped). If neither store nor config supplies the account, throw the same
`InvalidOperationException` the configured provider throws today (no silent
zero-Guid). **Behavior is unchanged until an account is stored** — JordanSoft keeps
posting to its configured cash account.

### 4. Slot registry + endpoints (host, gated by `admin.postingAccounts`)

A lightweight slot registry — `PostingAccountSlot(string ModuleKey, string SlotKey,
string Label, string ExpectedType, IReadOnlyList<string> RequiredDimensions)` — seeded
with the Cash slot (`"cash"`, `"Cash"`, `"Cash / bank account"`, `"Asset"`, `[]`).
Fan-out adds each module's slots (sourced from their existing `*ChartRequirements`).

Endpoints (host, per-client, in-handler `AdminAuthorization.MayAsync(admin.postingAccounts)`):
- `GET /clients/{id}/posting-accounts` → for each **enabled** module that has
  registered slots, its slots as `{ moduleKey, slotKey, label, expectedType,
  requiredDimensions, currentAccountId (Guid?) }` (current from the store, null when
  unset). Slice 1: Cash only (the registry has only Cash).
- `PUT /clients/{id}/posting-accounts/{moduleKey}` → body `{ slots: { <slotKey>: Guid } }`;
  validates the moduleKey is known to the registry and each slotKey belongs to it
  (422 otherwise); persists via `SetModuleAsync`; returns the updated module's slots.

Contracts (`AdminContracts.cs` or a new file):
- `PostingAccountSlotResponse(string ModuleKey, string SlotKey, string Label, string ExpectedType, IReadOnlyList<string> RequiredDimensions, Guid? CurrentAccountId)`
- `PostingAccountsResponse(IReadOnlyList<PostingAccountSlotResponse> Slots)`
- `SetPostingAccountsRequest(IReadOnlyDictionary<string, Guid> Slots)`

### 5. UI — `/admin/posting-accounts`

Replaces the placeholder. Data-driven from the GET:
- One section per module (group the `Slots` by `moduleKey`), heading = a friendly
  module name.
- Each slot: label + a native `<select>` of the client's chart accounts (from the
  existing `GET /accounts`, filtered to `postable`), current value preselected; the
  slot's `expectedType` shown as a hint (accounts of a different type are still
  selectable — readiness stays advisory).
- Per-module **Save** button gated by `*appCan="'admin.postingAccounts'"`, PUTs that
  module's slot→accountId map; shows "Saved." / error.
- Because it renders whatever slots the GET returns, fan-out needs no UI change.
- Route: `canWrite` + `requiredCapability: 'admin.postingAccounts'`, `fallback:
  '/admin/users'`; add `/admin/posting-accounts` to the `built` array.

## Testing

- **Backend:**
  - `PostingAccountStore` round-trip (set module slots → get reflects them; second
    module's slots don't clobber the first).
  - `StorePostingAccountsSource` returns stored map / empty when unset.
  - `StoreBackedCashAccountsProvider` prefers the stored account over config; falls
    back to config when unset; throws when neither present.
  - `GET /posting-accounts` returns the Cash slot with `currentAccountId` null then
    (after PUT) the stored value; only enabled modules appear.
  - `PUT` persists and validates unknown moduleKey/slotKey (422).
  - Cap-gating: 403 without `admin.postingAccounts`.
- **Frontend:** screen renders the Cash slot from a mocked GET; selecting an account
  and Save PUTs `{ slots: { Cash: <guid> } }`; Save hidden without the cap.
- **Dev-stack smoke (JordanSoft):** open the screen, confirm the Cash slot shows the
  current (fallback) account context; set the Cash account via the screen; confirm
  the GET reflects it and a Cash disbursement/deposit posts to the chosen account;
  then clear it (restore config fallback). Cash postings on JordanSoft are real —
  prefer a reversible check (e.g. set to the same configured account id, or verify via
  the GET + one posting then void), leaving JordanSoft on config fallback.

## Out of scope (this slice)

The other five modules (Receivables, Payables, Payroll, Fixed Assets, Inventory —
fan-out slices); Bank Reconciliation (no fixed slots); any type/dimension
*enforcement* at save time (readiness stays advisory).

## Files (indicative)

**Backend**
- `Backend/Accounting101.Ledger.Api/Control/PostingAccountStore.cs` (new: store + `PostingAccountsDoc`).
- `Backend/Accounting101.Ledger.Api/Control/IPostingAccountsSource.cs` + `StorePostingAccountsSource.cs` (new).
- `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs` (new: `PostingAccountSlot` + registry seeded with Cash).
- `Backend/Accounting101.Ledger.Api/Endpoints/PostingAccountEndpoints.cs` (new: GET + PUT).
- `Backend/Accounting101.Ledger.Contracts/*` (new response/request records).
- Host DI registration (wherever `ControlStore`/endpoints are wired).
- `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/StoreBackedCashAccountsProvider.cs` (new) + `CashServiceExtensions.cs` (swap registration).
- Backend tests (store, source, provider, endpoints).

**Frontend**
- `UI/Angular/src/app/core/posting-accounts/*` (model + service).
- `UI/Angular/src/app/features/admin/posting-accounts.ts` (+ spec).
- `UI/Angular/src/app/app.routes.ts` (route + `built`).
