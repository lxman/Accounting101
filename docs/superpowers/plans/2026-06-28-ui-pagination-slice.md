# UI-Ready List Pagination — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every module list (Payroll, Cash, Receivables, Payables, Reconciliation) and the engine `GET /entries` UI-ready: paged with a real `Total` (`PagedResponse<T>` envelope), ordered, and `includeVoided`-able — without breaking any internal consumer.

**Architecture:** A new `PagedResponse<T>` in Ledger.Contracts. `IDocumentStore.QueryAsync` gains optional `skip/limit/descending/includeVoided` (backward-compatible defaults) + a new `CountAsync`; the Mongo + Scoped layers implement them. Doc-dump lists (Payroll/Cash) page+count at the DB via a new paged store method; computed/filtered lists (Receivables/Payables/Reconciliation) page+count in-memory at the endpoint after materializing (the aggregation reads stay unbounded). `GET /entries` is dual-shape: a bare array by default (internal filtered reads unchanged), a `PagedResponse<EntryResponse>` when `skip`/`limit` are supplied (journal gains count methods for `Total`).

**Tech Stack:** C#/.NET 10, MongoDB driver, xUnit, EphemeralMongo.

## Global Constraints

- `IDocumentStore.QueryAsync` stays **backward-compatible** — new params are optional with defaults that reproduce today's behavior (unbounded, voided hidden); **every existing caller compiles and behaves unchanged**. Sorting/skip/limit apply only when `limit` is supplied; `includeVoided` defaults false.
- **The aggregation-never-pages guard:** the store methods feeding internal aggregations (`GetByCustomerAsync`/`GetByVendorAsync`, `GetEntriesTouchingAccountAsync`, `GetBySourceRefAsync`, the eligible-entries reads) MUST stay unbounded. Only the display list paths page.
- `GET /entries` returns a bare `List<EntryResponse>` when neither `skip` nor `limit` is supplied (unchanged); a `PagedResponse<EntryResponse>` when either is supplied — **only on the unfiltered + posting-only paths**; the filtered paths (`account`/`sourceRef`/`dimension`/`reference`) always return a bare array (they are aggregation reads).
- Paging params: `skip` (default 0; <0 → 0), `limit` (default 50; clamped [1, 200]), `order` (`asc|desc`, default `desc`; other → 400), `includeVoided` (default false; doc-dump lists only).
- Money is `decimal`. Build 0 warnings. Commit trailer, verbatim, on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Confirmed signatures (from the code)

- `IDocumentStore.QueryAsync<T>(Guid clientId, string collection, IReadOnlyDictionary<string,string> tagFilter, CancellationToken ct = default) → Task<IReadOnlyList<DocumentResult<T>>>` (`Backend/Accounting101.Ledger.Contracts/IDocumentStore.cs`); `DocumentResult<T>(Guid Id, DocumentLifecycle State, long? Sequence, T Body)`.
- `MongoDocumentStore.QueryAsync(string collection, IReadOnlyDictionary<string,string> tagFilter, CancellationToken ct) → Task<IReadOnlyList<ModuleDocument>>` (`Backend/Accounting101.Ledger.Mongo/MongoDocumentStore.cs:66`) — filter `b.Nin(d => d.State, HiddenStates)` (`[Inactive, Superseded, Voided]`) + tag eqs; `ModuleDocument` has `.Id`, `.State`, `.Sequence` (`long?`), `.Body`. No sort/skip/limit. `Collection(name) → IMongoCollection<ModuleDocument>`.
- `ScopedDocumentStore.QueryAsync<T>` (`Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs:60`) → `manifest.PolicyOf(collection)`; `Ctx ctx = await EnterAsync(...)`; `ctx.Store.QueryAsync(ctx.Physical, tagFilter, ct)` → map to `DocumentResult<T>`. `ctx.Store` is the `MongoDocumentStore`; `MapState(d.State)` exists.
- `MongoJournalStore` (`Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs`): `GetByClientAsync(clientId, int skip, int limit, ct)` (`:79`, `.SortBy(SequenceNumber).Skip(skip>0?skip:null).Limit(limit>0?limit:null)`) and `GetByPostingAsync(clientId, postings, int skip, int limit, ct)` (`:186`, same). `Collection`/journal collection is `IMongoCollection<JournalEntry>` (mirror the GetBy* filters for the counts). The filtered reads (`GetTouchingAccountAsync`, `GetBySourceRefAsync`, `GetByReferenceAsync`, `GetTouchingDimensionAsync`) return all (no skip/limit).
- `/entries` handler `ListEntries(Guid clientId, Guid? account, Guid? sourceRef, string? dimension, Guid? value, string? posting, string? reference, int? skip, int? limit, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken ct)` (`Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs:378`); path precedence reference→sourceRef→dimension→account→posting-only→unfiltered; returns `Results.Ok(entries.Select(ToEntryResponse).ToList())` (`:415`). `Page(skip)`/`PageLimit(limit)` helpers exist.

---

### Task 1: Shared layer — `PagedResponse<T>` + `QueryAsync` paging + `CountAsync`

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/PagedResponse.cs`
- Modify: `Backend/Accounting101.Ledger.Contracts/IDocumentStore.cs` (QueryAsync params + CountAsync)
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoDocumentStore.cs` (QueryAsync params + CountAsync)
- Modify: `Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs` (pass-through + CountAsync)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/` — a new `DocumentStorePagingTests.cs` (mirror an existing Mongo doc-store test's fixture usage).

**Interfaces:**
- Produces: `PagedResponse<T>`; `IDocumentStore.QueryAsync(..., int? skip, int? limit, bool descending, bool includeVoided)`; `IDocumentStore.CountAsync(...)` — consumed by Tasks 2-4.

- [ ] **Step 1: `PagedResponse<T>`**

`PagedResponse.cs`:
```csharp
namespace Accounting101.Ledger.Contracts;

/// <summary>A page of a list plus the total matching the filter, so a UI can render page counts and jump to
/// a page. page count = ceil(Total/Limit); current page = Skip/Limit + 1; hasMore = Skip + Items.Count &lt; Total.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, long Total, int Skip, int Limit);
```

- [ ] **Step 2: `IDocumentStore` — add paging params + `CountAsync`**

In `IDocumentStore.cs`, replace the `QueryAsync` signature with the back-compatible paged one and add `CountAsync`:
```csharp
    Task<IReadOnlyList<DocumentResult<T>>> QueryAsync<T>(Guid clientId, string collection,
        IReadOnlyDictionary<string, string> tagFilter,
        int? skip = null, int? limit = null, bool descending = true, bool includeVoided = false,
        CancellationToken cancellationToken = default);

    /// <summary>Count the documents matching the tag filter (for a paged list's Total). Honors includeVoided.</summary>
    Task<long> CountAsync(Guid clientId, string collection,
        IReadOnlyDictionary<string, string> tagFilter, bool includeVoided = false,
        CancellationToken cancellationToken = default);
```
(The `CancellationToken` stays last; the new params are optional so every existing `QueryAsync(clientId, collection, tags, ct)` call still binds — `ct` matches the trailing optional `CancellationToken`.)

- [ ] **Step 3: `MongoDocumentStore` — sort/skip/limit + includeVoided + count**

In `MongoDocumentStore.cs`, replace `QueryAsync` and add `CountAsync`:
```csharp
    public async Task<IReadOnlyList<ModuleDocument>> QueryAsync(
        string collection, IReadOnlyDictionary<string, string> tagFilter,
        int? skip = null, int? limit = null, bool descending = true, bool includeVoided = false,
        CancellationToken cancellationToken = default)
    {
        IFindFluent<ModuleDocument, ModuleDocument> find = Collection(collection).Find(MatchFilter(tagFilter, includeVoided));
        if (limit is not null)
        {
            SortDefinition<ModuleDocument> sort = descending
                ? Builders<ModuleDocument>.Sort.Descending(d => d.Sequence)
                : Builders<ModuleDocument>.Sort.Ascending(d => d.Sequence);
            find = find.Sort(sort).Skip(skip > 0 ? skip : 0).Limit(Math.Clamp(limit.Value, 1, 200));
        }
        return await find.ToListAsync(cancellationToken);
    }

    public Task<long> CountAsync(
        string collection, IReadOnlyDictionary<string, string> tagFilter, bool includeVoided = false,
        CancellationToken cancellationToken = default) =>
        Collection(collection).CountDocumentsAsync(MatchFilter(tagFilter, includeVoided), cancellationToken: cancellationToken);

    private static FilterDefinition<ModuleDocument> MatchFilter(IReadOnlyDictionary<string, string> tagFilter, bool includeVoided)
    {
        FilterDefinitionBuilder<ModuleDocument> b = Builders<ModuleDocument>.Filter;
        List<FilterDefinition<ModuleDocument>> clauses = [];
        if (!includeVoided) clauses.Add(b.Nin(d => d.State, HiddenStates));   // default: hide Inactive/Superseded/Voided
        clauses.AddRange(tagFilter.Select(t => b.Eq("Tags." + t.Key, t.Value)));
        return clauses.Count > 0 ? b.And(clauses) : b.Empty;
    }
```
(`MatchFilter` factors the shared filter; the default unpaged call — `limit == null` — keeps today's behavior: no sort, no skip/limit, voided hidden.)

- [ ] **Step 4: `ScopedDocumentStore` — pass through + count**

In `ScopedDocumentStore.cs`, replace `QueryAsync<T>` and add `CountAsync`:
```csharp
    public async Task<IReadOnlyList<DocumentResult<T>>> QueryAsync<T>(Guid clientId, string collection,
        IReadOnlyDictionary<string, string> tagFilter,
        int? skip = null, int? limit = null, bool descending = true, bool includeVoided = false,
        CancellationToken cancellationToken = default)
    {
        manifest.PolicyOf(collection);
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        IReadOnlyList<ModuleDocument> docs = await ctx.Store.QueryAsync(ctx.Physical, tagFilter, skip, limit, descending, includeVoided, cancellationToken);
        return docs.Select(d => new DocumentResult<T>(d.Id, MapState(d.State), d.Sequence, BsonSerializer.Deserialize<T>(d.Body))).ToList();
    }

    public async Task<long> CountAsync(Guid clientId, string collection,
        IReadOnlyDictionary<string, string> tagFilter, bool includeVoided = false, CancellationToken cancellationToken = default)
    {
        manifest.PolicyOf(collection);
        Ctx ctx = await EnterAsync(clientId, collection, cancellationToken);
        return await ctx.Store.CountAsync(ctx.Physical, tagFilter, includeVoided, cancellationToken);
    }
```

- [ ] **Step 5: Test the shared layer**

Create `DocumentStorePagingTests.cs` in `Backend/Accounting101.Ledger.Mongo.Tests` (mirror an existing test in that project for the fixture/`ScopedDocumentStore` or `MongoDocumentStore` construction — find one that already creates a doc store against the Mongo fixture and copy its setup). Assert, against a collection seeded with N (e.g. 5) finalized evidentiary docs (and 1 voided):
- a default `QueryAsync(clientId, collection, emptyTags)` returns all non-voided (back-compat: unbounded, voided hidden);
- `QueryAsync(..., skip: 0, limit: 2, descending: true)` returns the 2 highest-`Sequence` docs; `skip: 2, limit: 2` the next 2; `descending: false` flips the order;
- `QueryAsync(..., includeVoided: true)` includes the voided doc;
- `CountAsync(...)` returns the non-voided count, and the full count with `includeVoided: true`.

- [ ] **Step 6: Build the whole solution (back-compat) + run the new + a sampling of existing doc-store tests**

Run: `dotnet build Accounting101.slnx --nologo` — Expected: Build succeeded (every existing `QueryAsync` caller still compiles via the optional params).
Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests/Accounting101.Ledger.Mongo.Tests.csproj --nologo` — Expected: all pass (existing + new).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/PagedResponse.cs Backend/Accounting101.Ledger.Contracts/IDocumentStore.cs \
        Backend/Accounting101.Ledger.Mongo/MongoDocumentStore.cs Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs \
        Backend/Accounting101.Ledger.Mongo.Tests/DocumentStorePagingTests.cs
git commit -m "$(cat <<'EOF'
feat(store): PagedResponse + QueryAsync paging + CountAsync (backward-compatible)

IDocumentStore.QueryAsync gains optional skip/limit/descending/includeVoided
(defaults reproduce today's unbounded, voided-hidden behavior) + a CountAsync;
Mongo sorts by Sequence and pages only when limit is supplied. New
PagedResponse<T> envelope. Existing callers unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Doc-dump module lists (Payroll + Cash) — page at the DB

**Files (per the enumeration below):** the four module stores (interface + `Document*Store` impl + the `InMemory*Store` test fake) and the four list endpoints, across Payroll + Cash. Plus the relevant module test files.

**Interfaces:**
- Consumes: `IDocumentStore.QueryAsync(..., skip, limit, descending, includeVoided)` + `CountAsync` + `PagedResponse<T>` (Task 1).

The transformation is identical at each of the four doc-dump lists. **The pattern (shown for Cash deposits) then applied verbatim to each enumerated site:**

- [ ] **Step 1: Add a paged store method to the interface + `Document*Store` + the in-memory fake**

Interface (`ICashDepositStore`): add
```csharp
    Task<PagedResponse<CashDeposit>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);
```
`DocumentCashDepositStore`: implement it via the paged `QueryAsync` + `CountAsync` (the collection const + `Tags()` already exist in the store; `Map` already maps a `DocumentResult` → the domain type):
```csharp
    public async Task<PagedResponse<CashDeposit>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<CashDepositBody>> page =
            await documents.QueryAsync<CashDepositBody>(clientId, Collection, Tags(), skip, limit, descending, includeVoided, ct);
        long total = await documents.CountAsync(clientId, Collection, Tags(), includeVoided, ct);
        return new PagedResponse<CashDeposit>(page.Select(Map).ToList(), total, skip, limit);
    }
```
`InMemoryCashDepositStore` (test fake): implement it by ordering its in-memory dictionary values by the document's `Number`/sequence and applying skip/take + total (it already stores the full set; honor `includeVoided` by including/excluding `Status == Void`; `descending` flips the order):
```csharp
    public Task<PagedResponse<CashDeposit>> GetByClientPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IEnumerable<CashDeposit> all = _store.Values.Where(d => includeVoided || d.Status != CashDepositStatus.Void);
        List<CashDeposit> ordered = (descending ? all.OrderByDescending(d => d.Number) : all.OrderBy(d => d.Number)).ToList();
        List<CashDeposit> items = ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return Task.FromResult(new PagedResponse<CashDeposit>(items, ordered.Count, skip, limit));
    }
```

- [ ] **Step 2: Page the endpoint**

`CashEndpoints.ListDeposits` — change the handler to bind paging params, validate `order`, call the paged store method, and return the `PagedResponse` of views:
```csharp
    private static async Task<IResult> ListDeposits(
        Guid clientId, int? skip, int? limit, string? order, bool? includeVoided,
        ICashDepositStore store, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        PagedResponse<CashDeposit> page = await store.GetByClientPagedAsync(
            clientId, skip ?? 0, limit ?? 50, descending, includeVoided ?? false, cancellationToken);
        return Results.Ok(new PagedResponse<CashDepositView>(
            page.Items.Select(d => new CashDepositView(d)).ToList(), page.Total, page.Skip, page.Limit));
    }
```
Add a shared `TryOrder` helper in this endpoints class (and one per endpoints file that needs it):
```csharp
    private static bool TryOrder(string? order, out bool descending)
    {
        descending = true;
        if (string.IsNullOrEmpty(order)) return true;
        if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) { descending = true; return true; }
        if (string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)) { descending = false; return true; }
        return false;
    }
```

- [ ] **Step 3: Apply the identical transformation to the other three doc-dump sites**

| Site | Store interface / impl / fake | Endpoint | Body/view types |
|---|---|---|---|
| **Cash disbursements** | `ICashDisbursementStore` / `DocumentCashDisbursementStore` / `InMemoryCashDisbursementStore`; collection + `Map` exist | `CashEndpoints.ListDisbursements` (CashEndpoints.cs:66) | `CashDisbursement` / `CashDisbursementView`; status `CashDisbursementStatus.Void` |
| **Payroll runs** | `IPayrollRunStore` / `DocumentPayrollRunStore` / its fake | `PayrollEndpoints.ListRuns` (PayrollEndpoints.cs:63) | `PayrollRun` / `PayrollRunView`; the run's void status field |
| **Payroll remittances** | `ITaxRemittanceStore` / `DocumentTaxRemittanceStore` / its fake | `PayrollEndpoints.ListRemittances` (PayrollEndpoints.cs:102) | `TaxRemittance` / `TaxRemittanceView`; its void status field |

For each: add `GetByClientPagedAsync` to the interface, implement it in the `Document*Store` (paged `QueryAsync` + `CountAsync` over that store's `Collection` + `Tags()` + `Map`), implement it in the in-memory fake (order by `Number`, `includeVoided` filters the void status, skip/take + total), and rewrite the list endpoint to the paged shape above (bind `skip/limit/order/includeVoided`, `TryOrder`, return `PagedResponse<View>`). Add the `TryOrder` helper to `PayrollEndpoints` too. (If a domain type lacks a `Number` to order the fake by, order by a stable field the fake already has — e.g. the sequence it assigns — matching the real store's `Sequence` order.)

- [ ] **Step 4: Run the Payroll + Cash test projects**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Accounting101.Banking.Cash.Tests.csproj --nologo`
Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests/Accounting101.Payroll.Tests.csproj --nologo`
Expected: all pass. Update any existing list test that asserted a bare array to read `PagedResponse<T>.Items` (and assert `Total`). Add a focused test per module: a list of 3 deposits paged `limit=2` → `Total==3`, `Items.Count==2`, page 2 → the third; `includeVoided` changes `Total`.

- [ ] **Step 5: Commit**

```bash
git add Modules/Payroll Modules/Banking/Cash
git commit -m "$(cat <<'EOF'
feat(payroll,cash): paged list endpoints (PagedResponse + total + includeVoided)

The doc-dump lists (payroll runs/remittances, cash disbursements/deposits) page
+ count at the DB via GetByClientPagedAsync (QueryAsync skip/limit + CountAsync),
return PagedResponse<View> with order + includeVoided. In-memory fakes mirror it.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Computed/filtered module lists (Receivables, Payables, Reconciliation) — page in-memory; the guard

**Files:** the list endpoints (Receivables `ListInvoices`, Payables `ListBills`, Reconciliation `ListStatements` + `ListAdjustments`) and their service list methods; the four module test projects.

**Interfaces:**
- Consumes: `PagedResponse<T>` (Task 1). The aggregation store methods (`GetByCustomerAsync`/`GetByVendorAsync`/`GetByAccountAsync`/`GetByReconciliationAsync`) are UNCHANGED (stay unbounded).

These lists already materialize the full filtered/computed list. Page **in-memory** on that list — the underlying store query stays unbounded so the aggregations that share it see everything.

- [ ] **Step 1: Page the computed-view list methods + endpoints**

For **Receivables `ListInvoiceViewsAsync`**: keep the method that computes `List<InvoiceView>` over all the customer's docs unchanged, but add a paged wrapper (or page at the endpoint). Page at the endpoint to keep the service method reusable:
- `ReceivablesEndpoints.ListInvoices`: after `service.ListInvoiceViewsAsync(...)` returns the full filtered `IReadOnlyList<InvoiceView>`, bind `skip/limit/order`, `TryOrder` (→ 400), then:
```csharp
        IReadOnlyList<InvoiceView> all = await service.ListInvoiceViewsAsync(clientId, customerId.Value, filter, cancellationToken);
        IEnumerable<InvoiceView> ordered = descending
            ? all.OrderByDescending(v => v.Invoice.Number) : all.OrderBy(v => v.Invoice.Number);
        List<InvoiceView> items = ordered.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 200)).ToList();
        return Results.Ok(new PagedResponse<InvoiceView>(items, all.Count, skip ?? 0, limit ?? 50));
```
Add the `TryOrder` helper to `ReceivablesEndpoints`. (`includeVoided` does NOT apply here — the computed view's visibility is governed by its settlement filter; do not add it to these lists.) Order by the invoice's `Number` (the document's stable assigned identifier).

- [ ] **Step 2: Apply the identical in-memory paging to the other computed/filtered sites**

| Site | Endpoint | Full-list source (unchanged) | Order key |
|---|---|---|---|
| **Payables bills** | `PayablesEndpoints.ListBills` (PayablesEndpoints.cs:93) | `service.ListBillViewsAsync(...)` → `IReadOnlyList<BillView>` | `v.Bill.Number` |
| **Reconciliation bank-statements** | `ReconciliationEndpoints.ListStatements` (ReconciliationEndpoints.cs:57) | `service.ListStatementsAsync(clientId, cashAccountId, ct)` → `IReadOnlyList<BankStatement>` | `s.Number` |
| **Reconciliation adjustments** | `ReconciliationEndpoints.ListAdjustments` (ReconciliationEndpoints.cs:127) | `service.ListAdjustmentsAsync(clientId, reconciliationId, ct)` → `IReadOnlyList<BankAdjustment>` | `a.Number` |

For each: keep the service list method (and the underlying `GetBy*` store call) UNCHANGED, bind `skip/limit/order` on the endpoint, `TryOrder` (→ 400), page the materialized list in-memory (`OrderBy[Descending](key).Skip(skip).Take(clamp(limit))`, `Total = all.Count`), return `PagedResponse<T>`. Add `TryOrder` to `PayablesEndpoints` (`ReconciliationEndpoints` if not already present from Task 2's pattern — it isn't; add it). The Reconciliation `ListStatements` keeps its existing `cashAccountId` required-filter → 400 check; `ListAdjustments` keeps reading by reconciliation id.

- [ ] **Step 3: The aggregation-never-pages guard test**

Add a test (Receivables, against its fakes) that proves the aggregation still sees all docs even when the display list would page: seed > one page of a customer's documents (e.g. 3 invoices + payments), call an aggregation that reads them all (`GetCustomerCreditBalanceAsync` or assert a settlement total / `AppliedToInvoiceAsync`) and assert it reflects EVERY document — i.e. the aggregation never received skip/limit. (The store methods it calls were not changed in this task, so this asserts the boundary holds.) Mirror an existing Receivables service test's fake setup.

- [ ] **Step 4: Run the four module test projects**

Run the Receivables, Payables, and Reconciliation test projects. Expected: all pass. Update any existing list test that asserted a bare array to read `PagedResponse<T>.Items` + assert `Total`. Add a focused paging test per computed list (3 items, `limit=2` → `Total==3`, page 1 has 2, page 2 has 1; invalid `order` → 400).

- [ ] **Step 5: Commit**

```bash
git add Modules/Receivables Modules/Payables Modules/Banking/Reconciliation
git commit -m "$(cat <<'EOF'
feat(receivables,payables,reconciliation): paged computed/filtered lists

Invoice/bill views and reconciliation statements/adjustments page in-memory at
the endpoint after materializing (PagedResponse + total + order); the underlying
aggregation store reads stay unbounded. A guard test proves an aggregation over
>1 page still sees every document.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `GET /entries` dual-shape envelope + journal counts

**Files:**
- Modify: the journal interface + `Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs` (add `CountByClientAsync` + `CountByPostingAsync`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`ListEntries` dual-shape)
- Test: `Backend/Accounting101.Ledger.Api.Tests/` — extend the entries-list tests.

**Interfaces:**
- Consumes: `PagedResponse<T>` (Task 1).

- [ ] **Step 1: Add journal count methods**

In the journal interface (the `IJournal`/journal abstraction `MongoJournalStore` implements) add:
```csharp
    Task<long> CountByClientAsync(Guid clientId, CancellationToken cancellationToken = default);
    Task<long> CountByPostingAsync(Guid clientId, IReadOnlyList<string> postings, CancellationToken cancellationToken = default);
```
In `MongoJournalStore.cs`, implement them by mirroring the filter of `GetByClientAsync`/`GetByPostingAsync` (same client + posting predicates) with `CountDocumentsAsync` instead of the find/sort/skip/limit. (Read those two methods to copy their exact filter builders; `postings` maps the same way `GetByPostingAsync` maps its posting argument.)

- [ ] **Step 2: `ListEntries` dual-shape**

In `LedgerEndpoints.cs`, change `ListEntries` so the **unfiltered** and **posting-only** paths return a `PagedResponse<EntryResponse>` when `skip` or `limit` is supplied, and a bare array otherwise; the **filtered** paths (`reference`/`sourceRef`/`dimension`/`account`) always return a bare array (unchanged). Concretely:
- Keep the existing precedence + queries. Track which path was taken (a `bool pageable` = true only for the posting-only and unfiltered branches).
- After building `entries` and applying the existing in-memory posting refinement:
```csharp
        List<EntryResponse> items = entries.Select(ToEntryResponse).ToList();
        bool paged = skip is not null || limit is not null;
        if (pageable && paged)
        {
            long total = postingFilterApplied
                ? await ctx.Ledger.Journal.CountByPostingAsync(clientId, ps, cancellationToken)
                : await ctx.Ledger.Journal.CountByClientAsync(clientId, cancellationToken);
            return Results.Ok(new PagedResponse<EntryResponse>(items, total, Page(skip), PageLimit(limit)));
        }
        return Results.Ok(items);
```
(Use the same `ps`/posting set the posting-only branch built, and the same `Page(skip)`/`PageLimit(limit)` the query used, so `Skip`/`Limit` in the envelope match the window actually returned. The filtered paths set `pageable = false`, so they always return the bare array — internal `?sourceRef=`/`?account=` consumers are unchanged.)

- [ ] **Step 3: Test the dual shape**

In the Api.Tests entries-list test file: keep/confirm a test that `GET /entries?sourceRef={x}` (no paging) returns a bare array and an internal-style read still works; add tests that `GET /entries?limit=2` returns a `PagedResponse<EntryResponse>` with `Total` == the client's entry count and `Items.Count == 2`, `?skip=2&limit=2` the next window, and `?posting=Posted&limit=...` returns a `PagedResponse` whose `Total` is the posted count. Mirror the existing entries-list test setup (seed several posted/pending entries). Confirm `ApproveBySourceRefAsync`-style helpers (which parse a bare array from `?sourceRef=`) still compile/pass.

- [ ] **Step 4: Run the Ledger.Api tests + whole solution build**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --nologo` — Expected: all pass (the bare-array internal reads unchanged; the new paged shape covered).
Run: `dotnet build Accounting101.slnx --nologo` — Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Mongo Backend/Accounting101.Ledger.Api Backend/Accounting101.Ledger.Api.Tests
git commit -m "$(cat <<'EOF'
feat(entries): dual-shape GET /entries — bare array, or paged envelope when paged

ListEntries returns PagedResponse<EntryResponse> (with a journal Total) on the
unfiltered + posting-only paths when skip/limit is supplied, and the bare array
otherwise. The filtered reads (sourceRef/account/dimension/reference) always
return a bare array, so the module ledger clients are unchanged. Journal gains
CountByClientAsync/CountByPostingAsync.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- `PagedResponse<T>` + `QueryAsync` paging + `CountAsync` (back-compat) → Task 1. ✓
- Doc-dump lists page at DB (Payroll/Cash) → Task 2. ✓
- Computed/filtered lists page in-memory (Receivables/Payables/Reconciliation) → Task 3. ✓
- Aggregation-never-pages guard (store reads unchanged + a test) → Task 3. ✓
- `/entries` dual-shape (bare default, envelope when paged) + journal counts → Task 4. ✓
- `order` 400 / `skip`,`limit` clamping / `includeVoided` on doc-dumps only → Tasks 1-2 helpers. ✓
- Back-compat (every existing caller unchanged) → Task 1 optional params + Steps 4/6 builds. ✓
- Sim-reconciler consequence is documented in the spec (out of this repo's scope). ✓

**2. Placeholder scan:** No TBD/TODO. Full code for the shared layer, the doc-dump pattern, the computed-view pattern, and `/entries`. The Task 2/3 per-site tables are an explicit sweep (one transformation, enumerated sites with their exact types/methods/lines), not "similar to Task N" — the representative code is given in full and each site lists its concrete substitutions. The "mirror an existing test's fixture setup" instructions point at concrete existing files in the same project.

**3. Type consistency:** `PagedResponse<T>(IReadOnlyList<T> Items, long Total, int Skip, int Limit)` (Task 1) used by every endpoint (Tasks 2-4). `QueryAsync(..., int? skip, int? limit, bool descending, bool includeVoided, CancellationToken)` + `CountAsync(..., bool includeVoided, CancellationToken)` defined in Task 1 (interface + Mongo + Scoped), consumed by the `Document*Store.GetByClientPagedAsync` methods (Task 2). `GetByClientPagedAsync(Guid, int, int, bool, bool, CancellationToken)→Task<PagedResponse<Domain>>` is the uniform doc-dump store signature. `TryOrder(string?, out bool)` is added per endpoints class that pages. Journal `CountByClientAsync`/`CountByPostingAsync` (Task 4) match the `GetByClientAsync`/`GetByPostingAsync` filter shapes. The computed-view endpoints reuse the unchanged service list methods + page in-memory. ✓
