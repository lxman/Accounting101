# Receivables Revenue-by-Category Editor — Design

**Date:** 2026-07-17
**Follows:** Posting-accounts epic (complete, 6/6 modules; final slice `58749bc`). This is the deferred
follow-up called out in the slice-5 design: per-client editing of the dynamic
`RevenueAccountsByCategory` map, which until now is config-only.

## Goal

Let an admin configure, per client, which revenue account each invoice-line `RevenueCategory` credits.
Today `InvoicePostingAccounts.RevenueAccountsByCategory` comes only from process config
(`Receivables:Accounts:RevenueByCategory`); a category with no mapping falls through to the single
default Revenue account. After this feature, a client-specific map stored in the control DB wins over
the config map, editable from the existing `/admin/posting-accounts` screen.

## 1. Store (`Backend/.../Control/PostingAccountStore.cs`)

Add a parallel module-keyed field to `PostingAccountsDoc`:

```csharp
public Dictionary<string, Dictionary<string, Guid>> CategoryMaps { get; set; } = new();
```

`{ moduleKey → { category → accountId } }`. Module-keyed for symmetry with `Accounts`, even though
only receivables uses it today.

New atomic setter mirroring `SetModuleAsync`:

```csharp
public Task SetCategoryMapAsync(Guid clientId, string moduleKey, IReadOnlyDictionary<string, Guid> map, CancellationToken ct = default)
```

— a targeted `$set` on `CategoryMaps.{moduleKey}` with `IsUpsert = true` (upsert seeds `ClientId` from
the filter). Concurrent writes to `Accounts.*`, or to another module's category map, cannot clobber it.

**Presence semantics:** a client "has" a stored map for a module when `CategoryMaps` contains that
module key — **including an empty map**. A stored empty map is meaningful: it lets an admin clear the
deployment-config categories for this client.

## 2. Module capability flag (near `PostingAccountSlots`)

A small registry in `Backend/.../Control/` declaring which modules support a category map and where
their config fallback section lives:

```csharp
public static class PostingAccountCategoryMaps
{
    // moduleKey → config section holding the deployment-default category map
    // Only receivables today.
    public static string? ConfigSectionFor(string moduleKey) =>
        moduleKey == "receivables" ? "Receivables:Accounts:RevenueByCategory" : null;
}
```

(Exact shape at implementer's discretion — a record list like `PostingAccountSlots` is fine — but the
two facts it must answer are *does this module support a category map* and *what config section is the
fallback*.)

## 3. API (`PostingAccountEndpoints.cs` — same route group, same gate)

Two new endpoints on the existing `/clients/{clientId:guid}/posting-accounts` group, both gated by
`admin.postingAccounts` via the existing `AdminAuthorization.MayAsync` pattern, both 404 on unknown
client:

### `GET /clients/{id}/posting-accounts/{moduleKey}/revenue-categories`

- 422 if the module doesn't support a category map (anything but `receivables` today) — mirrors the
  unknown-module 422 on the slots PUT.
- Returns the **stored** map when the client has one (presence per §1, empty counts), else the
  **config** map read from the module's config section, with a source marker:

```json
{ "moduleKey": "receivables", "categories": { "Consulting": "guid", … }, "source": "stored" | "config" }
```

- Config parse mirrors the provider's strict `ReadCategoryMap` semantics: absent section → empty map;
  malformed Guid value → fail loud (500 — a deployment config error, same as the provider's behavior).

### `PUT /clients/{id}/posting-accounts/{moduleKey}/revenue-categories`

- Body `{ "categories": { "<category>": "<accountId>", … } }` — **full replace** of the client's
  stored map for that module.
- 422 if the module doesn't support a category map.
- 422 if any category key is empty/whitespace.
- 422 if any category key contains `.` or starts with `$` — the stored map's inner keys are BSON
  element names under `CategoryMaps.<moduleKey>`, and dot/dollar keys are unsafe there. (Small
  tightening over the approved free-text rule, forced by the storage shape; the UI mirrors it.)
- Duplicate keys cannot survive dictionary binding on the wire; duplicate-row prevention
  (case-sensitive compare — `Consulting` and `consulting` are *distinct, valid* categories) is the
  UI's job (§5).
- Account Guids are **advisory** — no chart-existence check, consistent with the slots PUT.
- Empty `categories` is valid (stores an empty map, which wins over config per §1/§4).
- On success: 200 echoing the same shape as GET with `source: "stored"` (after a PUT the map *is*
  stored) — one response contract for both verbs.

New contracts in `Accounting101.Ledger.Contracts` (e.g. `RevenueCategoriesResponse(string ModuleKey,
IReadOnlyDictionary<string, Guid> Categories, string Source)` and
`SetRevenueCategoriesRequest(IReadOnlyDictionary<string, Guid> Categories)`).

## 4. Provider (`StoreBackedInvoiceAccountsProvider.cs`)

`RevenueAccountsByCategory` changes from config-only to **store-first, wholesale**:

- New port method on `IPostingAccountsSource` (in `Control/PostingAccountsSource.cs`):

```csharp
Task<IReadOnlyDictionary<string, Guid>?> GetCategoryMapAsync(Guid clientId, string moduleKey, CancellationToken ct = default)
    => Task.FromResult<IReadOnlyDictionary<string, Guid>?>(null);
```

  Declared with a **default implementation returning `null`** so the existing `file sealed class
  FakeSource` fakes across all six module test projects keep compiling untouched. `null` = "client has
  no stored map"; distinct from an empty map. `StorePostingAccountsSource` overrides it to read
  `doc.CategoryMaps`.

- `StoreBackedInvoiceAccountsProvider.GetAsync` sets
  `RevenueAccountsByCategory = storedMap ?? ReadCategoryMap("Receivables:Accounts:RevenueByCategory")`.
  A stored map wins **wholesale even when empty** (per-key merge is explicitly NOT the semantics —
  the stored map is the complete per-client truth once it exists). The existing config-fallback test
  stays green (no stored map → config, unchanged).

- Update the class doc-comment (it currently states the map is NOT per-client-configurable).

Fixed-slot resolution (`Receivable`/`Revenue`/`SalesTaxPayable`) is untouched.
`StoreBackedPaymentAccountsProvider` is untouched.

## 5. UI (`features/admin/posting-accounts.ts` + `core/posting-accounts/*`)

A **"Revenue by category"** sub-section rendered inside the existing Receivables module group on the
`/admin/posting-accounts` screen — only when receivables is enabled (i.e. only when the receivables
group renders at all; the group's presence is already driven by the slots GET). The 7 fixed RX slots
render above it, unchanged. This is the one place the screen stops being purely slot-driven —
**intentional**, per the approved design.

- **Service** (`posting-accounts.service.ts`): `revenueCategories()` → GET, `setRevenueCategories(map)`
  → PUT, both against `…/posting-accounts/receivables/revenue-categories`. Wire types in
  `posting-accounts.ts` (`{ moduleKey, categories, source }`).
- **Rows**: `[category text input] [account dropdown] [delete]`. The dropdown reuses
  `postableAccounts()` (all postable accounts, same as the slots — advisory, no type filter).
- **"Add category"** button appends an empty row.
- **Dedicated Save** for the sub-section (separate from the slots Save) → PUT full-replace built from
  the rows.
- **Client-side validation before save**: block Save (with a visible message) when any row has an
  empty/whitespace category name or when two rows have the same name (case-sensitive compare —
  differing case is allowed).
- **Source note**: when the GET returned `source: "config"`, show a muted note that the categories
  shown are deployment defaults until saved for this client.
- Load the categories after the slots GET resolves and a receivables group exists (avoids a guaranteed
  404/422-shaped call for clients without receivables). Reuse the existing load-gate/`[selected]`
  native-`<select>` pattern for the dropdowns.

## 6. Scope decisions (user-chosen)

- **Free-text categories.** No usage-suggestion query (mining existing invoice lines for category
  names) — deferred.
- **Advisory account validation** only; chart-existence validation deferred.
- **No revert-to-config affordance** (no DELETE endpoint / `$unset`); once a client has a stored map,
  clearing all rows and saving stores an empty map (which suppresses config categories — every line
  then credits the default Revenue account). Acceptable; a true revert is future work if ever needed.

## Tests

- **Store** (`Ledger.Api.Tests`): `SetCategoryMapAsync` upserts (seeds `ClientId`), replaces only the
  targeted module's map, and does not clobber `Accounts` (set a slots map, then a category map, assert
  both survive) nor another module's category map.
- **Endpoints** (`PostingAccountEndpointTests`):
  - GET with no stored map → config map, `source: "config"` (seed the test host config section).
  - PUT valid map → 200 echo; re-GET → stored map, `source: "stored"`.
  - PUT empty categories → 200; re-GET → empty, `source: "stored"` (stored-empty wins).
  - PUT whitespace category key → 422.
  - GET/PUT for a non-category-map module (`cash`) → 422.
  - Unauthorized caller → 403 (mirror the existing gating case).
- **Provider** (`StoreBackedInvoiceAccountsProviderTests`):
  - Stored map wins over config (config section populated with different keys — proves wholesale, not
    merge).
  - Stored **empty** map wins over a populated config section (returns empty).
  - No stored map (`null` from source) → config map (the existing fallback test, still green).
  - Fixed-slot resolution unaffected by presence of a stored category map.
- **UI spec** (`posting-accounts.spec.ts` or sibling): renders rows from GET; add/delete row; Save
  PUTs full-replace body `{ categories }`; duplicate name (case-sensitive) blocks save; empty name
  blocks save; config-source note shows when `source === "config"`; sub-section absent when
  receivables not among the slot groups.

## Verification

- `dotnet test Accounting101.slnx` green; PROD `ng build` green (budget gate).
- **Live JordanSoft smoke** (zero-footprint recipe): record `enabledModules` via
  `GET /clients/{id}/me/capabilities`; temporarily enable `receivables` (full-replace modules PUT);
  GET revenue-categories → `source: "config"`, empty (JordanSoft config has no RevenueByCategory);
  PUT `{ "categories": { "Consulting": "<some guid>" } }` → 200; re-GET → stored; exercise the screen
  sub-section in the browser (rows render, add/save works); GET/PUT on `cash` → 422; then RESTORE:
  `$unset CategoryMaps.receivables` on the client's control-DB `posting_accounts` doc via the mongo
  tool (no DELETE endpoint — this is the one smoke step that goes under the API, precisely so the
  restore is truly zero-footprint), restore the exact prior module set, and verify GET is back to
  `source: "config"` before modules are disabled again.

## Out of scope / deferred

- Usage-suggestion query for category names (mine `InvoiceLine.RevenueCategory` values).
- Chart-existence / account-type validation at save time (advisory Guids, same as slots).
- Revert-to-config endpoint (DELETE / `$unset`).
- Category maps for any module other than receivables (registry makes adding one cheap).
