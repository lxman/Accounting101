# Module List Pagination (+ includeVoided) — Design

**Date:** 2026-06-26
**Status:** Spec for review

## Goal

Module list endpoints currently return **all** documents (`IDocumentStore.QueryAsync` has no `skip`/`limit`), so a list grows unboundedly over time (e.g. 24 months of payroll runs / cash disbursements). Add **pagination** to the module list endpoints, mirroring the engine's existing `GET /entries` convention, with a caller-chosen sort order. Bundle the related fix that list queries **silently omit voided documents** behind an opt-in `includeVoided` flag.

## Existing convention (mirror it)

The engine's `GET /entries` (`LedgerEndpoints.cs`) already paginates: query params `int? skip, int? limit`; `Page(skip)` = `skip` if `>0` else `0`; `PageLimit(limit)` = `Math.Clamp(limit ?? 200, 1, 1000)` (default **200**, cap **1000**); the response is a **bare array** (no total-count envelope). DB-level paging in the journal store. This slice reuses that convention verbatim, adding a `sort` order param (the engine entries list is fixed-ascending; module lists let the caller choose).

## The pivotal distinction — two list shapes

The module list endpoints are NOT uniform:

1. **Doc-dump lists** — return raw documents straight from the store: Payroll `ListRuns`/`ListRemittances` and Cash `ListDisbursements`/`ListDeposits`, all via `store.GetByClientAsync` → `QueryAsync`. These grow with time and are the high-value pagination target. They page at the **DB**.
2. **Computed-view lists** — compute a settlement view over *all* a customer's/vendor's documents: Receivables `ListInvoiceViewsAsync` and Payables `ListBillViewsAsync`. The store query feeding them **must stay unbounded** (the view needs every doc to compute applied/open balance), so these page **in-memory on the final view list**, after the existing settlement filter.

### The load-bearing guard
`GetByCustomerAsync` / `GetByVendorAsync` are *also* consumed by the internal aggregations — `AppliedToInvoiceAsync`, `GetCustomerCreditBalanceAsync`, `ValidateAllocationsAsync` (Receivables) and the Payables equivalents. **Those must always receive ALL documents.** Pagination must never touch the aggregation queries — if a paged result reached an aggregation, it would silently sum only the first page and corrupt settlement math. **Pagination is applied only on the display paths.** This is the one thing the implementation must not get wrong; the test suite must assert an aggregation over >1 page still sees everything.

## The change

### 1. Shared layer — `IDocumentStore.QueryAsync` (`Backend/Accounting101.Ledger.Contracts` + `Backend/Accounting101.Ledger.Mongo/MongoDocumentStore.cs`)
Add optional parameters, with defaults that reproduce today's behavior for every existing caller:
```csharp
Task<IReadOnlyList<DocumentResult<T>>> QueryAsync<T>(
    Guid clientId, string collection, IReadOnlyDictionary<string, string> tagFilter,
    int? skip = null, int? limit = null, bool descending = false, bool includeVoided = false,
    CancellationToken cancellationToken = default);
```
- **Sort:** always sort by the envelope's `ModuleDocument.Sequence` (the monotonic per-collection number assigned at finalize — a stable, meaningful key). `descending` flips ascending↔descending. (Today `QueryAsync` has no explicit sort — adding a deterministic order is a latent-correctness improvement; aggregation callers are order-insensitive, so they're unaffected.)
- **Paging:** when `limit` is non-null, apply `.Skip(skip ?? 0).Limit(Math.Clamp(limit.Value, 1, 1000))` at the Mongo `Find`. When `limit` is null → unbounded (current behavior; what aggregations use).
- **includeVoided:** when `true`, drop the `Nin(State, HiddenStates)` clause so Voided/Superseded/Inactive documents are returned too. Default `false` = current behavior.
- Any in-memory `IDocumentStore` fakes used by module tests get the same params.

### 2. Doc-dump lists (Payroll, Cash) — page at the DB
- `Payroll` store `GetByClientAsync` and `Cash` store `GetByClientAsync` gain `(int? skip, int? limit, bool descending, bool includeVoided)`, forwarded to `QueryAsync`.
- `MapPayrollEndpoints` (`ListRuns`, `ListRemittances`) and `MapCashEndpoints` (`ListDisbursements`, `ListDeposits`) accept `?skip=&limit=&order=&includeVoided=` and pass them through. Use the engine's `Page`/`PageLimit` clamp helpers (or equivalents) so an unbounded scan can't be requested.

### 3. Computed-view lists (Receivables, Payables) — page the result in-memory
- `ListInvoiceViewsAsync` / `ListBillViewsAsync` gain `(int? skip, int? limit, bool descending)`. They compute + settlement-filter over **all** docs as today, then sort the **final view list** by the document's assigned **`Number`** (monotonic, carried on the `Invoice`/`Bill` in the view — the envelope `Sequence` isn't on the mapped view) and `Skip().Take(clampedLimit)`. The store call feeding them stays unbounded.
- `includeVoided` is **out of scope** for these two: their visibility is already governed by the settlement filter (Issued/Entered only; voided excluded by design). Only the doc-dump lists get `includeVoided`.
- `ListInvoices` / `ListBills` endpoints accept `?skip=&limit=&order=` (alongside the existing `settlement=` filter).

### Parameters (final)
- **All paged list endpoints:** `?skip=` (default 0), `?limit=` (default 200, clamped [1,1000]), `?order=asc|desc` (default **desc** = newest first; invalid value → 400).
- **Doc-dump lists only (Payroll, Cash):** additionally `?includeVoided=true|false` (default false).
- Response: bare JSON array (unchanged shape), now bounded + ordered.

## Out of scope
- Total-count / `nextPage` envelope — the engine returns bare arrays; stay consistent. (A `hasMore`/count envelope can be a separate slice if a UI needs it.)
- Cursor-based pagination — skip/limit matches the house style; fine at these volumes.
- `includeVoided` on the computed-view (settlement) lists.
- Pagination on the engine `GET /entries` — already paged.

## Testing
- **`MongoDocumentStore`:** `QueryAsync` with `limit` returns ≤ limit, `skip` offsets, `descending` reverses, sorted by `Sequence`; `includeVoided` surfaces Voided/Superseded docs that the default hides; default call (no params) is byte-equivalent to today (unbounded, voided hidden).
- **Aggregation-safety (critical):** seed > one page of a customer's payments/bills, then assert `AppliedToInvoiceAsync` / `GetCustomerCreditBalanceAsync` / `ValidateAllocationsAsync` (and Payables equivalents) still sum across **all** of them — i.e. the aggregation path never paginates.
- **Doc-dump endpoints (Payroll, Cash):** seed > limit documents; assert page 1 returns `limit`, `skip` advances, `order` flips first/last, `includeVoided=true` includes a voided run/disbursement that the default omits.
- **Computed-view endpoints (Receivables, Payables):** seed > limit invoices/bills for a customer/vendor; assert the view list is paged + ordered and the settlement totals on each page are still correct (the underlying computation saw all docs).
- Existing module list/aggregation tests stay green (defaults unchanged).

## Global constraints
- .NET 10; build 0 warnings; commit per task; TDD; EphemeralMongo.
- Backward-compatible: `QueryAsync`'s new params default to today's behavior; every existing caller compiles and behaves identically.
- Mirror the engine's `Page`/`PageLimit` clamp (default 200, cap 1000) and bare-array response.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
