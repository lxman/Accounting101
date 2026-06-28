# UI-Ready List Pagination — Implementation Design

**Date:** 2026-06-28
**Status:** Approved (design) — activates the banked
`2026-06-26-module-list-pagination-design.md` (which was DEFERRED) with the scope
resolved for build.

## Context

The module list endpoints return **all** documents unbounded (Payroll runs/
remittances, Cash disbursements/deposits, Receivables invoice views, Payables
bill views, and now Reconciliation bank-statements/adjustments) — `QueryAsync`
has no `skip`/`limit`. A UI list needs a **total** (to render "Page 3 of 12" and
jump to a page), an **order**, and to optionally **include voided** documents.
The banked design settled the technical core; this spec resolves the three open
scope decisions and is the implementable contract.

**Scope decisions (resolved):**
1. **Module lists AND the engine `GET /entries`** get UI-ready paging — but with
   the `/entries` refinement below (envelope only when paged), so the internal
   filtered reads stay unbroken.
2. **Defer concrete filtering** (date/amount range) until the UI screens define
   it; existing filters (customer/vendor, settlement status, the
   `/bank-statements?cashAccountId=` filter) stay. Add only paging + order +
   total + `includeVoided` now.
3. **`includeVoided` is in.**

## Goal

Make every list a UI list can consume: bounded, ordered, paged with a real
**total count**, and able to include voided documents — without breaking any
existing internal consumer.

## Response contract — a paged envelope

```csharp
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, long Total, int Skip, int Limit);
```
From `{Total, Skip, Limit}` the UI derives page count = `ceil(Total/Limit)`,
current page = `Skip/Limit + 1`, `hasMore` = `Skip + Items.Count < Total`, and
jump-to-page sets `Skip = (page−1)·Limit`.

**Query params** (every paged list): `?skip=` (default 0, negative → clamped to
0), `?limit=` (default 50, clamped to [1, 200]), `?order=asc|desc` (default
`desc` = newest-first; any other value → 400), `?includeVoided=` (default false;
doc-dump lists only).

## The `/entries` refinement (load-bearing — avoids breaking internal consumers)

`GET /clients/{c}/entries` is consumed not just by the UI/sim but by **every
module's ledger client** as a *filtered aggregation read* that needs ALL matching
rows and parses a **bare `List<EntryResponse>`**: `GetEntriesBySourceRefAsync`
(`?sourceRef=`) in Cash/Payables/Receivables and the Reconciliation reader's
`GetEntriesTouchingAccountAsync` (`?account=`). Those are exactly the
"never-paginate" category.

Therefore `/entries` is **dual-shape, opt-in by paging params**:
- **No `skip`/`limit` supplied → bare `List<EntryResponse>`** (today's shape) —
  every internal filtered read (`?sourceRef=`, `?account=`) and back-compat caller
  is unchanged.
- **`limit` (or `skip`) supplied → `PagedResponse<EntryResponse>`** — the UI's
  paged display list, with `Total` over the matching filter.

This preserves the aggregation-never-pages principle at the engine level (a
filtered read for "all entries on account X" must not page) and limits the
breaking change to **paging** consumers only. The only such consumer today is the
**sim reconciler** (local sim repo), which pages `/entries` and parses a bare
array — it must be updated to read the envelope when it sends paging params (a
documented consequence; the sim is a manual dog-food tool, not CI).

The existing `?posting=PendingApproval|Posted` filter on `/entries` is retained
and composes with paging (the dog-food month-6 stranding — page 1 mistaken for
the whole set — is precisely why a paged entries list must expose `Total` + the
pending filter).

## The two list shapes (banked core)

1. **Unfiltered doc-dumps** — all docs in a collection for the client: Payroll
   `ListRuns`/`ListRemittances`, Cash `ListDisbursements`/`ListDeposits` (via
   `store.GetByClientAsync` → `QueryAsync`). **Page + count at the DB** (Mongo
   `Skip`/`Limit` + `CountDocuments`).
2. **Computed-view & filtered lists** — Receivables `ListInvoiceViewsAsync` /
   Payables `ListBillViewsAsync` (settlement views computed over all docs), and
   Reconciliation `ListStatements`(by account) / `ListAdjustments`(by
   reconciliation) (a `QueryAsync`-all then in-C# filter). These **already
   materialize** the full filtered/computed list, so **page + total are applied
   in-memory** on the final list (Total = its length before paging), ordered by
   the document's assigned `Number` (computed views) / `Sequence` (others).

## The aggregation-never-pages guard (banked, load-bearing)

`GetByCustomerAsync`/`GetByVendorAsync` (and the Reconciliation/Cash equivalents)
also feed internal aggregations (`AppliedToInvoiceAsync`,
`GetCustomerCreditBalanceAsync`, `ValidateAllocationsAsync`, settlement
computations) that need **every** document. **Paging is applied ONLY on the
display paths**, never on the store methods the aggregations call. A test asserts
an aggregation over >1 page of underlying docs still sees them all.

## Shared layer — `IDocumentStore`

- `QueryAsync<T>` gains **optional** `int? skip = null, int? limit = null, bool
  descending = true, bool includeVoided = false`. Defaults reproduce today's
  behavior exactly (unbounded, `Sequence`-sorted, voided hidden) — **every
  existing caller compiles and behaves unchanged**. When `limit` is non-null:
  `.Sort(Sequence asc/desc).Skip(max(0, skip ?? 0)).Limit(clamp(limit, 1, 200))`
  at the Mongo `Find`; `includeVoided` widens the lifecycle filter.
- New `CountAsync(Guid clientId, string collection, IReadOnlyDictionary<string,string>
  tagFilter, bool includeVoided = false)` → `long` (Mongo `CountDocumentsAsync`),
  for the doc-dump totals.
- The in-memory test `IDocumentStore` fakes implement the same surface (sort,
  skip, limit, count, includeVoided).

## Endpoint wiring

Each paged list endpoint binds `int? skip, int? limit, string? order, bool?
includeVoided`, validates `order` (→ 400 on a bad value), and returns
`PagedResponse<T>`:
- **Doc-dump lists:** the store gains a paged overload returning `(items, total)`
  — `GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided)` using
  `QueryAsync(..., skip, limit, descending, includeVoided)` + `CountAsync`.
- **Computed/filtered lists:** the service/endpoint materializes the full
  filtered list (unchanged), then `Total = list.Count`, `Items =
  list.Order(...).Skip(skip).Take(limit)`.
- `/entries`: `ListEntries` returns a bare array when `skip`/`limit` are both
  absent, else a `PagedResponse<EntryResponse>` (Total over the filtered set).

## Error handling

- `order` not in {asc, desc} (case-insensitive) → 400.
- `skip` < 0 → treated as 0; `limit` ≤ 0 or > 200 → clamped to [1, 200] (not an
  error — a UI may pass odd values; clamp rather than reject).
- Existing list filter errors (e.g. the Reconciliation `cashAccountId` required →
  400) are unchanged and compose with paging.

## Testing

- **`IDocumentStore` (Mongo + fakes):** `QueryAsync` with `skip`/`limit` returns
  the right window in `Sequence` order (asc + desc); default call is still
  unbounded + voided-hidden; `includeVoided` surfaces a voided doc; `CountAsync`
  counts the filter (with/without voided).
- **Per list shape:** a doc-dump list (Cash deposits) paged — `Total` is the full
  count, `Items` is one page, page 2 is the next window, `order` flips it,
  `includeVoided` changes `Total`; a computed-view list (Receivables invoices)
  paged — `Total` reflects the filtered set, paging is in-memory, an invalid
  `order` → 400.
- **The guard:** an aggregation (e.g. `GetCustomerCreditBalanceAsync` or a
  settlement total) computed over more than one page of underlying docs returns
  the all-docs result (paging did not leak into it).
- **`/entries` dual shape:** `?sourceRef=` (no paging) still returns a bare array
  (an existing module-client/E2E path proves it — e.g. `ApproveBySourceRefAsync`
  still works); `?limit=` returns a `PagedResponse` with the right `Total` and
  window; `?posting=` composes with paging.
- Full suites stay green; back-compat asserted by every unchanged caller still
  passing.

## Out of scope

- Concrete date/amount-range filtering (a follow-up scoped with the UI screens).
- Cursor-based pagination (offset/total suffices at these volumes and is what
  jump-to-page needs).
- Full-text search.
- Updating the local sim reconciler's `/entries` parse — a **required follow-up
  in the sim repo** before the next sim run (documented here; out of this repo's
  scope), since `/entries` now returns the envelope when paged.

## Success criteria

- Every module list (Payroll, Cash, Receivables, Payables, Reconciliation) returns
  a `PagedResponse<T>` with a real `Total`, supports `skip`/`limit`/`order`, and
  `includeVoided` on the doc-dump lists.
- `/entries` returns the paged envelope when paging params are present and a bare
  array otherwise — no internal module consumer breaks.
- The aggregation-never-pages guard holds (proven by a test).
- `IDocumentStore.QueryAsync` stays backward-compatible (every existing caller
  unchanged); `CountAsync` added.
- New tests green; existing suites stay green; 0 warnings.
