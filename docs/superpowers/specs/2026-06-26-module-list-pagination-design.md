# UI-Ready Module List Endpoints (paging + total + filter + includeVoided) — Design

**Date:** 2026-06-26
**Status:** **DEFERRED** — its own slice, to be implemented **with or just ahead of the UI**. The ultimate consumer is a UI: a list view is expected to show a **total count**, support **jump-to-page**, and **filter** the list. Building a minimal bare-array `skip`/`limit` now and re-doing it for the UI would be wasted work, so the full UI-ready contract is captured here and picked up when the UI is scoped.

## Goal

Make the module list endpoints **UI-ready**: bounded, ordered, **paged with a total count** (so a UI can render "Page 3 of 12" and jump to any page), **filterable**, and able to **include voided** documents on request. Today these endpoints return *all* documents (`IDocumentStore.QueryAsync` has no `skip`/`limit`), which is neither bounded nor UI-friendly.

Scope covers the module list endpoints: Payroll (runs, remittances), Cash (disbursements, deposits), Receivables (invoice views), Payables (bill views) — and, for UI uniformity, the engine `GET /entries` list (see Consistency).

## Response contract — an envelope (not a bare array)

A UI needs the total to compute page count and a jump target. So paged lists return an envelope, not a bare array:
```jsonc
{ "items": [ ... ],   // this page
  "total": 137,        // total matching the filter (across all pages)
  "skip": 100,         // offset of this page
  "limit": 50 }        // page size used
```
From `{total, skip, limit}` the UI derives everything: page count = `ceil(total/limit)`, current page = `skip/limit + 1`, `hasMore` = `skip + items.length < total`. **Jump-to-page** is the UI setting `skip = (targetPage − 1) × limit`. (This supersedes the earlier bare-array decision — bare arrays can't carry a total.)

- Params: `?skip=` (default 0), `?limit=` (default 50, clamped [1, 200] for a UI page; revisit the cap with the UI), `?order=asc|desc` (default desc = newest-first; invalid → 400).
- `total` is a real count: a `CountAsync(clientId, collection, tagFilter, includeVoided)` seam on `IDocumentStore` (+ Mongo `CountDocuments`) for the doc-dump lists; **free** for the computed-view lists (they already materialize the full filtered list — total is its length before paging).

## The two list shapes (unchanged technical core)

1. **Doc-dump lists** — raw documents from the store: Payroll `ListRuns`/`ListRemittances`, Cash `ListDisbursements`/`ListDeposits` (via `store.GetByClientAsync` → `QueryAsync`). Page + count at the **DB**.
2. **Computed-view lists** — settlement views over *all* a customer's/vendor's docs: Receivables `ListInvoiceViewsAsync`, Payables `ListBillViewsAsync`. The feeding store query **stays unbounded** (it must compute over every doc); page + total are applied **in-memory on the final view list** after filtering, sorted by the document's assigned `Number`.

### The load-bearing guard (carry forward)
`GetByCustomerAsync` / `GetByVendorAsync` are *also* consumed by the internal aggregations (`AppliedToInvoiceAsync`, `GetCustomerCreditBalanceAsync`, `ValidateAllocationsAsync`, and Payables equivalents), which need **all** documents. **Pagination must never touch the aggregation queries** — only the display paths page. A test must assert an aggregation over >1 page still sees everything. (Live precedent for why this matters: in month-6 of an early dog-food, the Accountant read only page 1 of `GET /entries` and falsely reported "0 remaining," closing a period with pending entries stranded. Bounded lists *must* expose total + a pending filter so a caller can't mistake page 1 for the whole set.)

## Filtering (the UI-ready addition — detail with the UI)

A UI list filters. The exact filter surface depends on the UI's concrete needs and will be specified when the UI slice is scoped, but the expected shape per list:
- **Invoices / Bills:** by customer/vendor (already there), settlement status (already there), **date range** (issue/bill date), and likely amount range.
- **Payroll runs / remittances, Cash disbursements / deposits:** by **date range**, and `includeVoided`.
- Filters are equality/range predicates pushed to the Mongo query (tags + body fields) for doc-dumps, or applied in-memory for computed views — same split as paging. The `total` reflects the filtered set.
- `includeVoided` (default false) surfaces Voided/Superseded documents that the default `QueryAsync` hides — scoped to the doc-dump lists (the computed views' visibility is governed by their settlement filter).

## Shared layer — `IDocumentStore`
- `QueryAsync<T>` gains optional `int? skip, int? limit, bool descending, bool includeVoided` (defaults reproduce today's behavior: unbounded, sorted by `Sequence`, voided hidden). When `limit` is non-null, `.Sort(Sequence).Skip(skip ?? 0).Limit(clamp(limit))` at the Mongo `Find`.
- New `CountAsync(clientId, collection, tagFilter, bool includeVoided)` → Mongo `CountDocumentsAsync` (for doc-dump totals).
- In-memory `IDocumentStore` fakes get the same surface.

## Consistency with the engine `/entries`
For a UI, the engine `GET /entries` list wants the same treatment (total + envelope) — it's currently a bare array with `skip`/`limit` and no total. The UI-ready slice should **unify the envelope across the module lists AND `/entries`**, which also means updating the **sim reconciler** (it pages `/entries` and parses a bare array) and the engine entry tests. This is the larger part of the slice's surface and the reason it's its own slice rather than polish.

## Out of scope (even for the future slice, unless the UI needs it)
- Cursor-based pagination — offset/total is fine at these volumes and is what jump-to-page needs.
- Full-text search — equality/range filters only.

## Why deferred
Done minimally now (bare `skip`/`limit`, no total) it would be redone for the UI. The total-count envelope, jump-to-page, filtering, and the `/entries` unification only make sense against concrete UI requirements. The hard technical core (two list shapes; the aggregation-never-pages guard; `Sequence` sort; `includeVoided`; `QueryAsync` backward-compatible defaults) is settled here so the future slice starts from a real design.

## Global constraints (when implemented)
- .NET 10; build 0 warnings; commit per task; TDD; EphemeralMongo.
- Backward-compatible `QueryAsync`/store defaults — every existing caller unchanged.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
