# Cash Ledger-First Invariant Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a Cash document's reported Posted/Void status derive from ledger-truth (its source entries), closing the crash-between-awaits gap, and prove Cash's existing conformance to the subledger invariant — with no change to Cash's recipes, accounts, or data model.

**Architecture:** Cash already stores no materialized balance — status is the only place it can drift from the GL. A pure resolver (`CashLedgerStatus`) decides Void from a doc's source entries; the `CashService` overlays it on detail and list reads (union with the document-envelope status — Void if either says so). A new batch entries-by-sourceRefs read (engine + module client) keeps the list read to one ledger round-trip per page, not N+1.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB (EphemeralMongo for tests), xUnit.

## Global Constraints

- **No change to Cash recipes, posting accounts, or the record/void lifecycle.** Only reads gain ledger-truth status. (spec §4)
- **No data-model change.** No stored field added or removed on any Cash type. (spec §4)
- **Union status rule (approved):** reported `Status` = Void if (document-envelope voided) **OR** (ledger shows the doc negated); else Posted. Ledger-truth can only *promote* to Void, never demote. (spec §3.1)
- **`sourceRefs` is a CSV query param** (`?sourceRefs=g1,g2`), a peer of the existing singular `sourceRef`; when both are supplied `sourceRef` keeps precedence. Malformed CSV → **400**; present-but-empty → empty bare array. (spec §3.2)
- **`ListEntries` filtered branches return a bare array** (internal aggregation reads), never a paged envelope. The `sourceRefs` branch matches. (spec §3.2, §7)
- **`includeVoided` list filtering stays keyed on the document envelope** — unchanged. The union rule governs only the reported `Status` field. (spec §3.1)
- Every commit leaves the whole solution building and green. (spec §8)

**Ledger lifecycle facts (from the engine, relied on throughout):**
- Withdrawn-while-pending → the single source entry's `Status` becomes `"Voided"` (no reversal spawned).
- Reversed-after-posting → a second entry appears with `ReversalOf == originalId`; the original stays `Status == "Active"`.
- `EntryResponse` fields used: `Id`, `Status` (`"Active"`/`"Voided"`), `ReversalOf` (`Guid?`), `SourceRef` (`Guid?`).

---

## File map

**Engine (create/modify):**
- Modify `Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs` — add `GetBySourceRefsAsync`.
- Modify `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` — add `sourceRefs` CSV param + parse helper to `ListEntries`.
- Test `Backend/Accounting101.Ledger.Mongo.Tests/MongoJournalStoreTests.cs` — batch-read test.
- Test (create) `Backend/Accounting101.Ledger.Api.Tests/EntriesBySourceRefsTests.cs` — endpoint union / malformed / empty.

**Module (create/modify):**
- Create `Modules/Banking/Cash/Accounting101.Banking.Cash/CashLedgerStatus.cs` — the resolver.
- Modify `Modules/Banking/Cash/Accounting101.Banking.Cash/ILedgerClient.cs` — add `GetEntriesBySourceRefsAsync`.
- Modify `Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs` — detail overlay + list methods.
- Modify `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/HttpLedgerClient.cs` — implement batch read.
- Modify `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashEndpoints.cs` — route list handlers through the service.
- Test (create) `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashLedgerStatusTests.cs` — resolver units.
- Test `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs` — implement new fake method.
- Test `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashServiceTests.cs` — overlay + list overlay proofs.
- Test `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashE2eTests.cs` — E2E list proof + Cash-scoped guard proof.

---

## Task 1: Engine batch read — `MongoJournalStore.GetBySourceRefsAsync`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs` (after `GetBySourceRefAsync`, ~line 141)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/MongoJournalStoreTests.cs`

**Interfaces:**
- Produces: `Task<IReadOnlyList<JournalEntry>> MongoJournalStore.GetBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)` — union of entries whose `SourceRef` is in `sourceRefs`; empty input → empty result with no DB round-trip.

- [ ] **Step 1: Write the failing test**

Add to `MongoJournalStoreTests.cs` (mirrors the existing `Source_back_link_round_trips…` test's builder usage):

```csharp
[Fact]
public async Task GetBySourceRefs_returns_the_union_across_refs_and_excludes_others()
{
    MongoJournalStore store = fixture.NewStore();
    var clientId = Guid.NewGuid();
    var ar = Guid.NewGuid();
    var revenue = Guid.NewGuid();
    var invoiceA = Guid.NewGuid();
    var invoiceB = Guid.NewGuid();
    var unrelated = Guid.NewGuid();

    JournalEntryBuilder a = Builder(clientId, 1);
    a.SourceRef = invoiceA; a.SourceType = "Invoice";
    await store.AppendAsync(a.Debit(ar, 100m).Credit(revenue, 100m).Build());

    JournalEntryBuilder b = Builder(clientId, 2);
    b.SourceRef = invoiceB; b.SourceType = "Invoice";
    await store.AppendAsync(b.Debit(ar, 50m).Credit(revenue, 50m).Build());

    JournalEntryBuilder u = Builder(clientId, 3);
    u.SourceRef = unrelated; u.SourceType = "Invoice";
    await store.AppendAsync(u.Debit(ar, 25m).Credit(revenue, 25m).Build());

    IReadOnlyList<JournalEntry> union = await store.GetBySourceRefsAsync(clientId, [invoiceA, invoiceB]);

    Assert.Equal(2, union.Count);
    Assert.Contains(union, e => e.SourceRef == invoiceA);
    Assert.Contains(union, e => e.SourceRef == invoiceB);
    Assert.DoesNotContain(union, e => e.SourceRef == unrelated);
}

[Fact]
public async Task GetBySourceRefs_with_empty_input_returns_empty()
{
    MongoJournalStore store = fixture.NewStore();
    IReadOnlyList<JournalEntry> union = await store.GetBySourceRefsAsync(Guid.NewGuid(), []);
    Assert.Empty(union);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter GetBySourceRefs`
Expected: FAIL — `MongoJournalStore` does not contain a definition for `GetBySourceRefsAsync` (compile error).

- [ ] **Step 3: Write minimal implementation**

In `MongoJournalStore.cs`, immediately after `GetBySourceRefAsync` (the method ending ~line 141):

```csharp
/// <summary>
/// Every entry spawned by any of the given source documents — the batch form of
/// <see cref="GetBySourceRefAsync"/>, used by a module that folds ledger-truth status across a
/// page of documents in one round-trip. Empty input returns empty without querying. Served by the
/// same (client, sourceRef) index.
/// </summary>
public async Task<IReadOnlyList<JournalEntry>> GetBySourceRefsAsync(
    Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
{
    if (sourceRefs.Count == 0) return [];

    FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
    FilterDefinition<JournalEntryDocument> filter = f.And(
        f.Eq(e => e.ClientId, clientId),
        f.In(e => e.SourceRef, sourceRefs.Select(r => (Guid?)r)));

    List<JournalEntryDocument> docs = await _entries.Find(filter).ToListAsync(cancellationToken);
    return docs.Select(d => d.ToDomain()).ToList();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter GetBySourceRefs`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs Backend/Accounting101.Ledger.Mongo.Tests/MongoJournalStoreTests.cs
git commit -m "feat(ledger): batch GetBySourceRefsAsync on the journal store"
```

---

## Task 2: Engine endpoint — `sourceRefs` CSV param on `ListEntries`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`ListEntries`, ~lines 393-449; add a parse helper nearby)
- Test: `Backend/Accounting101.Ledger.Api.Tests/EntriesBySourceRefsTests.cs` (create)

**Interfaces:**
- Consumes: `MongoJournalStore.GetBySourceRefsAsync` (Task 1), reached via `ctx.Ledger.Journal.GetBySourceRefsAsync(...)`.
- Produces: `GET /clients/{clientId}/entries?sourceRefs=<csv>` → bare `EntryResponse[]` union across the listed refs; malformed CSV → 400; empty → `[]`. When both `sourceRef` and `sourceRefs` are present, `sourceRef` wins.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/EntriesBySourceRefsTests.cs` (mirrors `SourceLinkTests` fixture usage):

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The batch source-back-link read: a caller resolves the journal entries for several source
/// documents in one request via the CSV <c>sourceRefs</c> filter. Malformed input is a 400; an empty
/// list is an empty bare array; the branch is a peer of the singular <c>sourceRef</c>.
/// </summary>
public sealed class EntriesBySourceRefsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<Guid> PostWithSource(SeededClient c, Guid sourceRef)
    {
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        PostEntryRequest entry = new(
            null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(debit, "Debit", 100m), new PostLineRequest(credit, "Credit", 100m)],
            SourceRef: sourceRef, SourceType: "Invoice");
        PostEntryResponse created = (await (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", entry))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        return created.Id;
    }

    [Fact]
    public async Task Batch_sourceRefs_returns_the_union_across_documents()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid docA = Guid.NewGuid(), docB = Guid.NewGuid(), docUnrelated = Guid.NewGuid();
        Guid entryA = await PostWithSource(c, docA);
        Guid entryB = await PostWithSource(c, docB);
        await PostWithSource(c, docUnrelated);

        List<EntryResponse> union = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?sourceRefs={docA},{docB}"))!;

        Assert.Equal(2, union.Count);
        Assert.Contains(union, e => e.Id == entryA);
        Assert.Contains(union, e => e.Id == entryB);
    }

    [Fact]
    public async Task Malformed_sourceRefs_is_a_400()
    {
        SeededClient c = await fixture.SeedClientAsync();
        HttpResponseMessage response = await c.Http.GetAsync(
            $"/clients/{c.ClientId}/entries?sourceRefs={Guid.NewGuid()},not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Empty_sourceRefs_returns_empty_array()
    {
        SeededClient c = await fixture.SeedClientAsync();
        List<EntryResponse> union = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?sourceRefs="))!;
        Assert.Empty(union);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter EntriesBySourceRefsTests`
Expected: FAIL — `Batch_sourceRefs_returns_the_union…` returns 0 items (param ignored today), and `Malformed…` returns 200 not 400.

- [ ] **Step 3: Add the `sourceRefs` parameter and parse helper**

In `LedgerEndpoints.cs`, change the `ListEntries` signature to accept the new param (add `string? sourceRefs` after `sourceRef`):

```csharp
    private static async Task<IResult> ListEntries(
        Guid clientId, Guid? account, Guid? sourceRef, string? sourceRefs, string? dimension, Guid? value,
        string? posting, string? reference,
        int? skip, int? limit,
        LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // Validate posting FIRST — an unrecognised value is always a 400, never silently ignored.
        PostingState? postingState = ParsePosting(posting, out IResult? badPosting);
        if (badPosting is not null) return badPosting;

        // Validate the batch sourceRefs CSV up front (present-but-empty is a valid empty list).
        List<Guid>? sourceRefList = null;
        if (sourceRefs is not null && !TryParseGuidCsv(sourceRefs, out sourceRefList, out IResult? badRefs))
            return badRefs;
```

Then add the new branch in the precedence chain, immediately **after** the singular `sourceRef` branch (so `sourceRef` keeps precedence when both are supplied):

```csharp
        else if (sourceRef is { } source)
            entries = await ctx.Ledger.Journal.GetBySourceRefAsync(clientId, source, cancellationToken);
        else if (sourceRefList is not null)
            entries = await ctx.Ledger.Journal.GetBySourceRefsAsync(clientId, sourceRefList, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(dimension) && value is { } dimValue)
```

Add the parse helper next to `ParsePosting`:

```csharp
    /// <summary>
    /// Parses a comma-separated Guid list (the <c>sourceRefs</c> batch filter). A present-but-empty or
    /// whitespace value is a valid empty list. Any non-empty element that is not a Guid yields a 400 in
    /// <paramref name="error"/> and a null list.
    /// </summary>
    private static bool TryParseGuidCsv(string raw, out List<Guid>? parsed, out IResult? error)
    {
        parsed = [];
        error = null;
        foreach (string part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(part, out Guid id))
            {
                parsed = null;
                error = Results.Problem($"'{part}' is not a valid Guid in sourceRefs.", statusCode: StatusCodes.Status400BadRequest);
                return false;
            }
            parsed.Add(id);
        }
        return true;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter EntriesBySourceRefsTests`
Expected: PASS (3 tests). Also run the existing filter tests to confirm no regression:
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter EntriesListFilterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/EntriesBySourceRefsTests.cs
git commit -m "feat(ledger): sourceRefs CSV batch filter on GET /entries"
```

---

## Task 3: The resolver — `CashLedgerStatus`

**Files:**
- Create: `Modules/Banking/Cash/Accounting101.Banking.Cash/CashLedgerStatus.cs`
- Test: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashLedgerStatusTests.cs` (create)

**Interfaces:**
- Produces: `static bool CashLedgerStatus.ShowsVoided(IReadOnlyList<EntryResponse> entriesForOneDoc)` — true when the doc's source entries show it negated (primary entry `Voided`, or a reversal of the primary exists). Empty input → false (caller falls back to the envelope).

- [ ] **Step 1: Write the failing test**

Create `CashLedgerStatusTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash.Tests;

public sealed class CashLedgerStatusTests
{
    private static EntryResponse Entry(Guid id, string status, Guid? reversalOf, Guid sourceRef) =>
        new(id, 0, default, "Standard", status, "Posted", 0, null, null, reversalOf, null, [], sourceRef, "CashDeposit");

    [Fact]
    public void Active_posted_entry_is_not_voided()
    {
        Guid src = Guid.NewGuid();
        var entries = new[] { Entry(Guid.NewGuid(), "Active", null, src) };
        Assert.False(CashLedgerStatus.ShowsVoided(entries));
    }

    [Fact]
    public void Withdrawn_pending_primary_is_voided()
    {
        Guid src = Guid.NewGuid();
        var entries = new[] { Entry(Guid.NewGuid(), "Voided", null, src) };
        Assert.True(CashLedgerStatus.ShowsVoided(entries));
    }

    [Fact]
    public void Reversed_after_posting_is_voided()
    {
        Guid src = Guid.NewGuid();
        Guid primary = Guid.NewGuid();
        var entries = new[]
        {
            Entry(primary, "Active", null, src),                 // original stays Active
            Entry(Guid.NewGuid(), "Active", primary, src),       // reversal points at the original
        };
        Assert.True(CashLedgerStatus.ShowsVoided(entries));
    }

    [Fact]
    public void No_entries_is_not_voided_so_caller_falls_back_to_envelope()
    {
        Assert.False(CashLedgerStatus.ShowsVoided([]));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter CashLedgerStatusTests`
Expected: FAIL — `CashLedgerStatus` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `CashLedgerStatus.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash;

/// <summary>
/// Ledger-truth for a cash document's Void state, read from its source journal entries. A document is
/// negated when its <em>primary</em> entry (the one that is not a reversal) has been withdrawn while
/// pending (engine sets its <c>Status</c> to <c>Voided</c>), or when a reversal of that primary exists
/// (<c>ReversalOf</c> points at it). This is the single home of the rule; the service unions it with the
/// document-envelope status so a read can only ever be <em>promoted</em> to Void, never demoted.
/// </summary>
public static class CashLedgerStatus
{
    public static bool ShowsVoided(IReadOnlyList<EntryResponse> entriesForOneDoc)
    {
        List<EntryResponse> primaries = entriesForOneDoc.Where(e => e.ReversalOf is null).ToList();
        if (primaries.Count == 0) return false;                       // no entry → fall back to envelope
        if (primaries.Any(p => p.Status == "Voided")) return true;    // withdrawn while pending
        HashSet<Guid> primaryIds = primaries.Select(p => p.Id).ToHashSet();
        return entriesForOneDoc.Any(e => e.ReversalOf is { } r && primaryIds.Contains(r)); // reversed
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter CashLedgerStatusTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Modules/Banking/Cash/Accounting101.Banking.Cash/CashLedgerStatus.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashLedgerStatusTests.cs
git commit -m "feat(cash): CashLedgerStatus resolver — ledger-truth Void from source entries"
```

---

## Task 4: Service detail overlay — `GetDepositAsync` / `GetDisbursementAsync`

**Files:**
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs` (`GetDisbursementAsync` ~line 48; `GetDepositAsync` ~line 85)
- Test: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashServiceTests.cs`

**Interfaces:**
- Consumes: `CashLedgerStatus.ShowsVoided` (Task 3); existing `ILedgerClient.GetEntriesBySourceRefAsync` (singular); existing `FakeLedgerClient.VoidAsync`.
- Produces: `GetDepositAsync`/`GetDisbursementAsync` now return the doc with `Status` overlaid to Void when the envelope OR the ledger says voided.

- [ ] **Step 1: Write the failing test**

Add to `CashServiceTests.cs` — the crash-between-awaits simulation (envelope stays Posted; the ledger entry is withdrawn directly, bypassing the store):

```csharp
// ── Ledger-truth status overlay (detail reads) ──────────────────────────

[Fact]
public async Task GetDeposit_reports_Void_when_ledger_entry_is_withdrawn_even_if_envelope_stays_Posted()
{
    Harness h = BuildHarness();
    Guid clientId = Guid.NewGuid();
    Guid capitalAccount = Guid.NewGuid();
    CashDeposit doc = await h.Service.RecordDepositAsync(clientId,
        new CashDepositBody([new CashLine(capitalAccount, 25_000m)], new DateOnly(2026, 1, 2), null, null));

    // Simulate the crash: the GL entry is withdrawn, but the document envelope was never marked void.
    IReadOnlyList<EntryResponse> spawned = await h.Ledger.GetEntriesBySourceRefAsync(clientId, doc.Id);
    Guid entryId = Assert.Single(spawned).Id;
    await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

    // Envelope still says Posted…
    CashDeposit? envelope = await h.DepositStore.GetAsync(clientId, doc.Id);
    Assert.Equal(CashDepositStatus.Posted, envelope!.Status);

    // …but the service read reports ledger-truth Void.
    CashDeposit? read = await h.Service.GetDepositAsync(clientId, doc.Id);
    Assert.Equal(CashDepositStatus.Void, read!.Status);
}

[Fact]
public async Task GetDisbursement_reports_Void_when_ledger_entry_is_withdrawn_even_if_envelope_stays_Posted()
{
    Harness h = BuildHarness();
    Guid clientId = Guid.NewGuid();
    Guid expenseAccount = Guid.NewGuid();
    CashDisbursement doc = await h.Service.RecordDisbursementAsync(clientId,
        new CashDisbursementBody([new CashLine(expenseAccount, 1_000m)], new DateOnly(2026, 6, 15), null, null));

    IReadOnlyList<EntryResponse> spawned = await h.Ledger.GetEntriesBySourceRefAsync(clientId, doc.Id);
    Guid entryId = Assert.Single(spawned).Id;
    await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

    CashDisbursement? envelope = await h.DisbursementStore.GetAsync(clientId, doc.Id);
    Assert.Equal(CashDisbursementStatus.Posted, envelope!.Status);

    CashDisbursement? read = await h.Service.GetDisbursementAsync(clientId, doc.Id);
    Assert.Equal(CashDisbursementStatus.Void, read!.Status);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter "GetDeposit_reports_Void|GetDisbursement_reports_Void"`
Expected: FAIL — service returns Posted (no overlay yet).

- [ ] **Step 3: Overlay ledger-truth in the service**

In `CashService.cs`, replace the two one-line getters. `GetDisbursementAsync` (~line 48):

```csharp
    public async Task<CashDisbursement?> GetDisbursementAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        CashDisbursement? doc = await disbursements.GetAsync(clientId, id, ct);
        if (doc is null) return null;
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, id, ct);
        return doc.Status == CashDisbursementStatus.Void || CashLedgerStatus.ShowsVoided(entries)
            ? doc with { Status = CashDisbursementStatus.Void }
            : doc;
    }
```

`GetDepositAsync` (~line 85):

```csharp
    public async Task<CashDeposit?> GetDepositAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        CashDeposit? doc = await deposits.GetAsync(clientId, id, ct);
        if (doc is null) return null;
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, id, ct);
        return doc.Status == CashDepositStatus.Void || CashLedgerStatus.ShowsVoided(entries)
            ? doc with { Status = CashDepositStatus.Void }
            : doc;
    }
```

(`using Accounting101.Ledger.Contracts;` is already present at the top of `CashService.cs`.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter CashServiceTests`
Expected: PASS (all — the two new overlay tests plus the unchanged existing ones; note `Void_a_deposit_withdraws_pending_entry_and_marks_doc_void` still passes because envelope-void alone yields Void).

- [ ] **Step 5: Commit**

```bash
git add Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashServiceTests.cs
git commit -m "feat(cash): detail reads overlay ledger-truth Void status"
```

---

## Task 5: Batch client method + list overlay + endpoint reroute

**Files:**
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/ILedgerClient.cs`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/HttpLedgerClient.cs`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashEndpoints.cs`
- Test: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs`
- Test: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashServiceTests.cs`

**Interfaces:**
- Consumes: engine `GET /entries?sourceRefs=` (Task 2); `CashLedgerStatus.ShowsVoided` (Task 3).
- Produces:
  - `ILedgerClient.GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken ct = default)` → all entries across the given refs; empty input → empty, no HTTP.
  - `CashService.ListDepositsAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)` → `PagedResponse<CashDeposit>` with per-row ledger-truth `Status`.
  - `CashService.ListDisbursementsAsync(...)` → `PagedResponse<CashDisbursement>` likewise.

- [ ] **Step 1: Add the batch method to the module ledger client interface + HTTP client + fake**

In `ILedgerClient.cs`, add after `GetEntriesBySourceRefAsync`:

```csharp
    /// <summary>Every entry tied to any of the given source documents, in one round-trip — how a list
    /// read folds ledger-truth status across a page without an N+1 of singular lookups.</summary>
    Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default);
```

In `HttpLedgerClient.cs`, add after `GetEntriesBySourceRefAsync`:

```csharp
    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
    {
        if (sourceRefs.Count == 0) return [];
        string csv = string.Join(',', sourceRefs);
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?sourceRefs={csv}");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(cancellationToken))!;
    }
```

In `Fakes.cs`, add to `FakeLedgerClient` (after `GetEntriesBySourceRefAsync`):

```csharp
    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntryResponse>>(
            _entries.Values.Where(e => e.SourceRef is { } s && sourceRefs.Contains(s)).ToList());
```

- [ ] **Step 2: Write the failing list-overlay test**

Add to `CashServiceTests.cs`:

```csharp
// ── Ledger-truth status overlay (list reads, batched) ────────────────────

[Fact]
public async Task ListDeposits_reports_Void_per_row_from_ledger_truth()
{
    Harness h = BuildHarness();
    Guid clientId = Guid.NewGuid();
    Guid capitalAccount = Guid.NewGuid();

    CashDeposit stayPosted = await h.Service.RecordDepositAsync(clientId,
        new CashDepositBody([new CashLine(capitalAccount, 10_000m)], new DateOnly(2026, 1, 1), null, null));
    CashDeposit goVoid = await h.Service.RecordDepositAsync(clientId,
        new CashDepositBody([new CashLine(capitalAccount, 20_000m)], new DateOnly(2026, 1, 2), null, null));

    // Withdraw the second deposit's entry directly — envelope stays Posted.
    Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, goVoid.Id)).Id;
    await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

    PagedResponse<CashDeposit> page = await h.Service.ListDepositsAsync(clientId, 0, 50, descending: false, includeVoided: true);

    Assert.Equal(CashDepositStatus.Posted, page.Items.Single(d => d.Id == stayPosted.Id).Status);
    Assert.Equal(CashDepositStatus.Void, page.Items.Single(d => d.Id == goVoid.Id).Status);
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter ListDeposits_reports_Void_per_row`
Expected: FAIL — `CashService` has no `ListDepositsAsync` (compile error).

- [ ] **Step 4: Add the list methods to the service**

In `CashService.cs`, add a shared helper and the two list methods (place near the getters). The helper batches one ledger call for the whole page and overlays each row:

```csharp
    // ── List reads (ledger-truth status, one batched ledger call per page) ─────

    public async Task<PagedResponse<CashDeposit>> ListDepositsAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        PagedResponse<CashDeposit> page = await deposits.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
        ILookup<Guid, EntryResponse> byRef = await EntriesByRefAsync(clientId, page.Items.Select(d => d.Id), ct);
        List<CashDeposit> overlaid = page.Items
            .Select(d => d.Status == CashDepositStatus.Void || CashLedgerStatus.ShowsVoided(byRef[d.Id].ToList())
                ? d with { Status = CashDepositStatus.Void }
                : d)
            .ToList();
        return new PagedResponse<CashDeposit>(overlaid, page.Total, page.Skip, page.Limit);
    }

    public async Task<PagedResponse<CashDisbursement>> ListDisbursementsAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        PagedResponse<CashDisbursement> page = await disbursements.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
        ILookup<Guid, EntryResponse> byRef = await EntriesByRefAsync(clientId, page.Items.Select(d => d.Id), ct);
        List<CashDisbursement> overlaid = page.Items
            .Select(d => d.Status == CashDisbursementStatus.Void || CashLedgerStatus.ShowsVoided(byRef[d.Id].ToList())
                ? d with { Status = CashDisbursementStatus.Void }
                : d)
            .ToList();
        return new PagedResponse<CashDisbursement>(overlaid, page.Total, page.Skip, page.Limit);
    }

    private async Task<ILookup<Guid, EntryResponse>> EntriesByRefAsync(Guid clientId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        List<Guid> refs = ids.ToList();
        IReadOnlyList<EntryResponse> entries = refs.Count == 0
            ? []
            : await ledger.GetEntriesBySourceRefsAsync(clientId, refs, ct);
        return entries.Where(e => e.SourceRef is not null).ToLookup(e => e.SourceRef!.Value);
    }
```

- [ ] **Step 5: Route the list endpoints through the service**

In `CashEndpoints.cs`, change `ListDisbursements` to use `CashService` instead of the store directly:

```csharp
    private static async Task<IResult> ListDisbursements(
        Guid clientId, int? skip, int? limit, string? order, bool? includeVoided,
        CashService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        PagedResponse<CashDisbursement> page = await service.ListDisbursementsAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeVoided ?? false, cancellationToken);
        return Results.Ok(new PagedResponse<CashDisbursementView>(
            page.Items.Select(d => new CashDisbursementView(d)).ToList(), page.Total, page.Skip, page.Limit));
    }
```

And `ListDeposits` likewise:

```csharp
    private static async Task<IResult> ListDeposits(
        Guid clientId, int? skip, int? limit, string? order, bool? includeVoided,
        CashService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        PagedResponse<CashDeposit> page = await service.ListDepositsAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeVoided ?? false, cancellationToken);
        return Results.Ok(new PagedResponse<CashDepositView>(
            page.Items.Select(d => new CashDepositView(d)).ToList(), page.Total, page.Skip, page.Limit));
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter "CashServiceTests|CashPagingTests"`
Expected: PASS (new list-overlay test + all existing service/paging tests — `CashPagingHttpTests` still green because the empty page yields an empty batch call).

- [ ] **Step 7: Commit**

```bash
git add Modules/Banking/Cash/Accounting101.Banking.Cash/ILedgerClient.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Api/HttpLedgerClient.cs Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashEndpoints.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashServiceTests.cs
git commit -m "feat(cash): list reads fold ledger-truth Void via batch sourceRefs read"
```

---

## Task 6: E2E list proof + Cash-scoped guard proof + reconciliation

**Files:**
- Test: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashE2eTests.cs`

**Interfaces:**
- Consumes: the full stack through the real host (`CashHostFixture`), including Task 5's list path and the shipped module-entry guard.

- [ ] **Step 1: Write the E2E list-truth proof**

Add to `CashE2eTests.cs` (uses the existing `SeedSodClientAsync` + `SetUpCashChartAsync` helpers):

```csharp
[Fact]
public async Task Deposit_list_reflects_module_void_across_the_page()
{
    (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
    await SetUpCashChartAsync(controller, clientId);

    RecordCashDepositRequest r1 = new([new CashLineRequest(fixture.MembersCapitalAccountId, 10_000m)], new DateOnly(2026, 1, 2), null, null);
    RecordCashDepositRequest r2 = new([new CashLineRequest(fixture.MembersCapitalAccountId, 20_000m)], new DateOnly(2026, 1, 3), null, null);

    CashDeposit d1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits", r1)).Content.ReadFromJsonAsync<CashDeposit>())!;
    CashDeposit d2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits", r2)).Content.ReadFromJsonAsync<CashDeposit>())!;

    // Void the second via the module (Controller holds cash.write + gl.void under SoD).
    (await controller.PostAsJsonAsync($"/clients/{clientId}/cash-deposits/{d2.Id}/void", new Api.VoidReasonRequest("error"))).EnsureSuccessStatusCode();

    PagedResponse<CashDepositView> page = (await clerk.GetFromJsonAsync<PagedResponse<CashDepositView>>(
        $"/clients/{clientId}/cash-deposits?includeVoided=true&order=asc"))!;

    Assert.Equal(CashDepositStatus.Posted, page.Items.Single(v => v.Deposit.Id == d1.Id).Deposit.Status);
    Assert.Equal(CashDepositStatus.Void, page.Items.Single(v => v.Deposit.Id == d2.Id).Deposit.Status);
}
```

- [ ] **Step 2: Write the Cash-scoped guard proof**

Add to `CashE2eTests.cs` — a raw GL reverse of a cash-owned entry must be refused (the invariant's enforcement arm):

```csharp
[Fact]
public async Task Raw_gl_reverse_of_a_cash_entry_is_refused_by_the_guard()
{
    (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
    await SetUpCashChartAsync(controller, clientId);

    RecordCashDepositRequest request = new([new CashLineRequest(fixture.MembersCapitalAccountId, 5_000m)], new DateOnly(2026, 1, 2), null, null);
    CashDeposit deposit = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits", request)).Content.ReadFromJsonAsync<CashDeposit>())!;

    // Find and approve the spawned entry, so a reversal (not a withdrawal) is what a raw caller would attempt.
    EntryResponse entry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>($"/clients/{clientId}/entries?sourceRef={deposit.Id}"))!);
    (await approver.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();

    // Raw reverse — a plain user request carrying no module credential — is refused: correct it through the module.
    HttpResponseMessage rawReverse = await controller.PostAsJsonAsync(
        $"/clients/{clientId}/entries/{entry.Id}/reverse",
        new ReverseRequest(new DateOnly(2026, 1, 3), "raw reversal attempt"));
    Assert.Equal(HttpStatusCode.Conflict, rawReverse.StatusCode);
}
```

- [ ] **Step 3: Run the new E2E tests**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter "Deposit_list_reflects_module_void|Raw_gl_reverse_of_a_cash_entry"`
Expected: PASS (2 tests).

- [ ] **Step 4: Whole-solution reconciliation**

Run: `dotnet test`
Expected: PASS — entire solution green (no regressions in engine, other modules, or Cash).

- [ ] **Step 5: Commit**

```bash
git add Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashE2eTests.cs
git commit -m "test(cash): E2E list-truth proof + Cash-scoped guard proof; whole-solution green"
```

---

## Self-Review

**1. Spec coverage:**
- §1 goal (verify + prove + ledger-truth status) → Tasks 3-6.
- §2 verify finding (Cash conforms; guard covers Cash) → Task 6 guard proof + this plan's documentation of "no data-model change."
- §3.1 resolver + union rule + no-entries fallback → Task 3 + the union expressions in Tasks 4/5.
- §3.2 engine batch read → Task 1; `sourceRefs` CSV param / precedence / malformed-400 / empty → Task 2; module client method → Task 5; detail overlay → Task 4; list overlay + endpoint reroute → Task 5.
- §4 no data-model change → no task adds/removes a stored field (Global Constraints).
- §5 tests: resolver units (T3), engine batch + endpoint (T1/T2), service overlay proof (T4), E2E list proof (T6), guard proof (T6), reconciliation (T6).
- §7 risk (ListEntries regression) → T2 Step 4 runs `EntriesListFilterTests`; T6 runs full suite.
- §8 sequencing (green at every commit) → six tasks, each ends green.

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to". Every code step shows full code.

**3. Type consistency:** `CashLedgerStatus.ShowsVoided(IReadOnlyList<EntryResponse>)` defined T3, consumed T4/T5. `GetEntriesBySourceRefsAsync(Guid, IReadOnlyList<Guid>, CancellationToken)` defined on the module `ILedgerClient` T5, implemented in `HttpLedgerClient` + `FakeLedgerClient` T5. `MongoJournalStore.GetBySourceRefsAsync(Guid, IReadOnlyList<Guid>, CancellationToken)` defined T1, called by `ListEntries` T2. `ListDepositsAsync`/`ListDisbursementsAsync` defined T5, called by `CashEndpoints` T5. `EntryResponse` constructor arity matches the codebase (16 params, `ReversalOf` at position 10, `SourceRef` at 13). `SourceRef` is `Guid?` — the `In` filter casts via `(Guid?)r`; the module lookup guards `SourceRef is not null`.

Note: spec §3.2 mentioned an "in-memory test store mirror" engine-side — there is none (`MongoJournalStore` is a concrete class with no interface; the engine tests it against EphemeralMongo). Task 1 tests it that way; no interface work needed. This is a simplification of the spec, not a gap.
