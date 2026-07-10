# Payroll Ledger-Truth Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a Payroll document's reported Posted/Void status derive from ledger-truth (its source entries), across both doc types (payroll runs + tax remittances), detail and list reads — closing the crash-between-awaits gap with no engine change and no data-model change.

**Architecture:** Payroll already stores no materialized balance — status is the only place it can drift from the GL. A per-module pure resolver (`PayrollLedgerStatus`, a verbatim duplicate of the shipped `CashLedgerStatus`) decides Void from a doc's source entries; `PayrollService` overlays it on detail and list reads (union with the document-envelope status — Void if either says so). The list read reuses the batch entries-by-sourceRefs engine read already on master (from the Cash cycle) to stay one ledger round-trip per page.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB (EphemeralMongo for tests), xUnit.

## Global Constraints

- **No change to Payroll recipes, posting accounts, or the record/void lifecycle.** Only reads gain ledger-truth. (spec §4)
- **No data-model change.** No stored field added or removed on any Payroll type. (spec §4)
- **No engine change.** The batch read (`GetBySourceRefsAsync`) and the `sourceRefs` CSV param on `GET /entries` are already on master from the Cash cycle. (spec §1, §6)
- **Union status rule (matches Cash):** reported `Status` = Void if (document-envelope voided) **OR** (ledger shows the doc negated); else Posted. Ledger-truth can only *promote* to Void, never demote; empty entries → envelope fallback. (spec §3.1)
- **`includeVoided` list filtering stays keyed on the document envelope** — unchanged. The union rule governs only the reported `Status` field. (spec §3.1)
- **Both doc types, symmetric coverage.** Every overlay/list/test exists for both `PayrollRun` and `TaxRemittance`; both ledger-Void branches (withdrawn-while-pending AND reversed-after-posting) are covered. (spec §5, §7)
- **Per-module resolver** — `PayrollLedgerStatus` is a duplicate of `CashLedgerStatus`, not a shared import (deferred audit; brick-isolation convention). (spec §3.1)
- Every commit leaves the whole solution building and green; test output pristine. (spec §8)

**Ledger lifecycle facts (relied on throughout):**
- Withdrawn-while-pending → the single source entry's `Status` becomes `"Voided"` (no reversal spawned).
- Reversed-after-posting → a second entry appears with `ReversalOf == originalId`; the original stays `Status == "Active"`.
- `EntryResponse` fields used: `Id`, `Status` (`"Active"`/`"Voided"`), `ReversalOf` (`Guid?`), `SourceRef` (`Guid?`). Its positional constructor (used in tests) is `new(Id, SequenceNumber, EffectiveDate, Type, Status, Posting, LineCount, Supersedes, SupersededBy, ReversalOf, ReversedBy, Lines, SourceRef, SourceType)`.

---

## File map

**Module (create/modify):**
- Create `Modules/Payroll/Accounting101.Payroll/PayrollLedgerStatus.cs` — the resolver.
- Modify `Modules/Payroll/Accounting101.Payroll/ILedgerClient.cs` — add `GetEntriesBySourceRefsAsync`.
- Modify `Modules/Payroll/Accounting101.Payroll/PayrollService.cs` — detail overlay + list methods.
- Modify `Modules/Payroll/Accounting101.Payroll.Api/HttpLedgerClient.cs` — implement batch read.
- Modify `Modules/Payroll/Accounting101.Payroll.Api/PayrollEndpoints.cs` — route list handlers through the service.
- Test (create) `Modules/Payroll/Accounting101.Payroll.Tests/PayrollLedgerStatusTests.cs` — resolver units.
- Test `Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs` — implement new fake method.
- Test `Modules/Payroll/Accounting101.Payroll.Tests/PayrollServiceTests.cs` — detail + list overlay proofs.
- Test `Modules/Payroll/Accounting101.Payroll.Tests/PayrollE2eTests.cs` — E2E list proof + Payroll-scoped guard proof.

---

## Task 1: The resolver — `PayrollLedgerStatus`

**Files:**
- Create: `Modules/Payroll/Accounting101.Payroll/PayrollLedgerStatus.cs`
- Test: `Modules/Payroll/Accounting101.Payroll.Tests/PayrollLedgerStatusTests.cs` (create)

**Interfaces:**
- Produces: `static bool PayrollLedgerStatus.ShowsVoided(IReadOnlyList<EntryResponse> entriesForOneDoc)` — true when the doc's source entries show it negated (primary entry `Voided`, or a reversal of the primary exists). Empty input → false (caller falls back to the envelope).

- [ ] **Step 1: Write the failing test**

Create `PayrollLedgerStatusTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll.Tests;

public sealed class PayrollLedgerStatusTests
{
    private static EntryResponse Entry(Guid id, string status, Guid? reversalOf, Guid sourceRef) =>
        new(id, 0, default, "Standard", status, "Posted", 0, null, null, reversalOf, null, [], sourceRef, "PayrollRun");

    [Fact]
    public void Active_posted_entry_is_not_voided()
    {
        Guid src = Guid.NewGuid();
        var entries = new[] { Entry(Guid.NewGuid(), "Active", null, src) };
        Assert.False(PayrollLedgerStatus.ShowsVoided(entries));
    }

    [Fact]
    public void Withdrawn_pending_primary_is_voided()
    {
        Guid src = Guid.NewGuid();
        var entries = new[] { Entry(Guid.NewGuid(), "Voided", null, src) };
        Assert.True(PayrollLedgerStatus.ShowsVoided(entries));
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
        Assert.True(PayrollLedgerStatus.ShowsVoided(entries));
    }

    [Fact]
    public void No_entries_is_not_voided_so_caller_falls_back_to_envelope()
    {
        Assert.False(PayrollLedgerStatus.ShowsVoided([]));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter PayrollLedgerStatusTests`
Expected: FAIL — `PayrollLedgerStatus` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `PayrollLedgerStatus.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payroll;

/// <summary>
/// Ledger-truth for a payroll document's Void state, read from its source journal entries. A document is
/// negated when its <em>primary</em> entry (the one that is not a reversal) has been withdrawn while
/// pending (engine sets its <c>Status</c> to <c>Voided</c>), or when a reversal of that primary exists
/// (<c>ReversalOf</c> points at it). This is the single home of the rule in the Payroll module; the
/// service unions it with the document-envelope status so a read can only ever be <em>promoted</em> to
/// Void, never demoted. (A verbatim mirror of the Cash module's CashLedgerStatus; a shared home is a
/// deferred audit.)
/// </summary>
public static class PayrollLedgerStatus
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

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter PayrollLedgerStatusTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Modules/Payroll/Accounting101.Payroll/PayrollLedgerStatus.cs Modules/Payroll/Accounting101.Payroll.Tests/PayrollLedgerStatusTests.cs
git commit -m "feat(payroll): PayrollLedgerStatus resolver — ledger-truth Void from source entries"
```

---

## Task 2: Service detail overlay — `GetRunAsync` / `GetRemittanceAsync`

**Files:**
- Modify: `Modules/Payroll/Accounting101.Payroll/PayrollService.cs` (`GetRunAsync` ~line 48; `GetRemittanceAsync` ~line 85)
- Test: `Modules/Payroll/Accounting101.Payroll.Tests/PayrollServiceTests.cs`

**Interfaces:**
- Consumes: `PayrollLedgerStatus.ShowsVoided` (Task 1); existing `ILedgerClient.GetEntriesBySourceRefAsync` (singular); existing `FakeLedgerClient.VoidAsync`/`ReverseAsync`.
- Produces: `GetRunAsync`/`GetRemittanceAsync` now return the doc with `Status` overlaid to Void when the envelope OR the ledger says voided.

- [ ] **Step 1: Write the failing tests**

Add to `PayrollServiceTests.cs` — four proofs, covering **both** ledger-Void branches for **both** doc types. Each simulates the crash-between-awaits gap: the GL entry is withdrawn/reversed directly via the fake, so the document envelope stays Posted, and the service read must still report Void.

```csharp
// ── Ledger-truth status overlay (detail reads) ──────────────────────────

[Fact]
public async Task GetRun_reports_Void_when_ledger_entry_is_withdrawn_even_if_envelope_stays_Posted()
{
    Harness h = BuildHarness();
    Guid clientId = Guid.NewGuid();
    PayrollRun run = await h.Service.RecordRunAsync(clientId,
        new PayrollRunBody(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null));

    Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, run.Id)).Id;
    await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

    PayrollRun? envelope = await h.RunStore.GetAsync(clientId, run.Id);
    Assert.Equal(PayrollRunStatus.Posted, envelope!.Status);

    PayrollRun? read = await h.Service.GetRunAsync(clientId, run.Id);
    Assert.Equal(PayrollRunStatus.Void, read!.Status);
}

[Fact]
public async Task GetRun_reports_Void_when_ledger_entry_is_reversed_even_if_envelope_stays_Posted()
{
    Harness h = BuildHarness();
    Guid clientId = Guid.NewGuid();
    PayrollRun run = await h.Service.RecordRunAsync(clientId,
        new PayrollRunBody(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null));

    Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, run.Id)).Id;
    await h.Ledger.ReverseAsync(clientId, entryId, new ReverseRequest(new DateOnly(2026, 7, 1), "reversed directly"));

    PayrollRun? envelope = await h.RunStore.GetAsync(clientId, run.Id);
    Assert.Equal(PayrollRunStatus.Posted, envelope!.Status);

    PayrollRun? read = await h.Service.GetRunAsync(clientId, run.Id);
    Assert.Equal(PayrollRunStatus.Void, read!.Status);
}

[Fact]
public async Task GetRemittance_reports_Void_when_ledger_entry_is_withdrawn_even_if_envelope_stays_Posted()
{
    Harness h = BuildHarness();
    Guid clientId = Guid.NewGuid();
    TaxRemittance remittance = await h.Service.RecordRemittanceAsync(clientId,
        new TaxRemittanceBody(1_700m, 1_240m, new DateOnly(2026, 7, 15), null));

    Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, remittance.Id)).Id;
    await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

    TaxRemittance? envelope = await h.RemittanceStore.GetAsync(clientId, remittance.Id);
    Assert.Equal(TaxRemittanceStatus.Posted, envelope!.Status);

    TaxRemittance? read = await h.Service.GetRemittanceAsync(clientId, remittance.Id);
    Assert.Equal(TaxRemittanceStatus.Void, read!.Status);
}

[Fact]
public async Task GetRemittance_reports_Void_when_ledger_entry_is_reversed_even_if_envelope_stays_Posted()
{
    Harness h = BuildHarness();
    Guid clientId = Guid.NewGuid();
    TaxRemittance remittance = await h.Service.RecordRemittanceAsync(clientId,
        new TaxRemittanceBody(1_700m, 1_240m, new DateOnly(2026, 7, 15), null));

    Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, remittance.Id)).Id;
    await h.Ledger.ReverseAsync(clientId, entryId, new ReverseRequest(new DateOnly(2026, 7, 16), "reversed directly"));

    TaxRemittance? envelope = await h.RemittanceStore.GetAsync(clientId, remittance.Id);
    Assert.Equal(TaxRemittanceStatus.Posted, envelope!.Status);

    TaxRemittance? read = await h.Service.GetRemittanceAsync(clientId, remittance.Id);
    Assert.Equal(TaxRemittanceStatus.Void, read!.Status);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter "GetRun_reports_Void|GetRemittance_reports_Void"`
Expected: FAIL — service returns Posted (no overlay yet).

- [ ] **Step 3: Overlay ledger-truth in the service**

In `PayrollService.cs`, replace the two one-line getters. `GetRunAsync` (~line 48):

```csharp
    public async Task<PayrollRun?> GetRunAsync(Guid clientId, Guid runId, CancellationToken ct = default)
    {
        PayrollRun? run = await runs.GetAsync(clientId, runId, ct);
        if (run is null) return null;
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, runId, ct);
        return run.Status == PayrollRunStatus.Void || PayrollLedgerStatus.ShowsVoided(entries)
            ? run with { Status = PayrollRunStatus.Void }
            : run;
    }
```

`GetRemittanceAsync` (~line 85):

```csharp
    public async Task<TaxRemittance?> GetRemittanceAsync(Guid clientId, Guid remittanceId, CancellationToken ct = default)
    {
        TaxRemittance? remittance = await remittances.GetAsync(clientId, remittanceId, ct);
        if (remittance is null) return null;
        IReadOnlyList<EntryResponse> entries = await ledger.GetEntriesBySourceRefAsync(clientId, remittanceId, ct);
        return remittance.Status == TaxRemittanceStatus.Void || PayrollLedgerStatus.ShowsVoided(entries)
            ? remittance with { Status = TaxRemittanceStatus.Void }
            : remittance;
    }
```

(`using Accounting101.Ledger.Contracts;` is already at the top of `PayrollService.cs`.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter PayrollServiceTests`
Expected: PASS (all — the 4 new overlay tests plus the unchanged existing ones; the existing `Void_a_run…marks_doc_void`/`Void_a_remittance…` tests still pass because envelope-void alone yields Void under the union).

- [ ] **Step 5: Commit**

```bash
git add Modules/Payroll/Accounting101.Payroll/PayrollService.cs Modules/Payroll/Accounting101.Payroll.Tests/PayrollServiceTests.cs
git commit -m "feat(payroll): detail reads overlay ledger-truth Void status (runs + remittances)"
```

---

## Task 3: Batch client method + list overlay + endpoint reroute

**Files:**
- Modify: `Modules/Payroll/Accounting101.Payroll/ILedgerClient.cs`
- Modify: `Modules/Payroll/Accounting101.Payroll.Api/HttpLedgerClient.cs`
- Modify: `Modules/Payroll/Accounting101.Payroll/PayrollService.cs`
- Modify: `Modules/Payroll/Accounting101.Payroll.Api/PayrollEndpoints.cs`
- Test: `Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs`
- Test: `Modules/Payroll/Accounting101.Payroll.Tests/PayrollServiceTests.cs`

**Interfaces:**
- Consumes: engine `GET /entries?sourceRefs=` (already on master); `PayrollLedgerStatus.ShowsVoided` (Task 1).
- Produces:
  - `ILedgerClient.GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken ct = default)` → all entries across the given refs; empty input → empty, no HTTP.
  - `PayrollService.ListRunsAsync(Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)` → `PagedResponse<PayrollRun>` with per-row ledger-truth `Status`.
  - `PayrollService.ListRemittancesAsync(...)` → `PagedResponse<TaxRemittance>` likewise.

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

- [ ] **Step 2: Write the failing list-overlay tests**

Add to `PayrollServiceTests.cs` — one per doc type:

```csharp
// ── Ledger-truth status overlay (list reads, batched) ────────────────────

[Fact]
public async Task ListRuns_reports_Void_per_row_from_ledger_truth()
{
    Harness h = BuildHarness();
    Guid clientId = Guid.NewGuid();

    PayrollRun stayPosted = await h.Service.RecordRunAsync(clientId,
        new PayrollRunBody(10_000m, 620m, 620m, 200m, 1_500m, new DateOnly(2026, 6, 30), null));
    PayrollRun goVoid = await h.Service.RecordRunAsync(clientId,
        new PayrollRunBody(20_000m, 1_240m, 1_240m, 0m, 3_000m, new DateOnly(2026, 7, 31), null));

    Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, goVoid.Id)).Id;
    await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

    PagedResponse<PayrollRun> page = await h.Service.ListRunsAsync(clientId, 0, 50, descending: false, includeVoided: true);

    Assert.Equal(PayrollRunStatus.Posted, page.Items.Single(r => r.Id == stayPosted.Id).Status);
    Assert.Equal(PayrollRunStatus.Void, page.Items.Single(r => r.Id == goVoid.Id).Status);
}

[Fact]
public async Task ListRemittances_reports_Void_per_row_from_ledger_truth()
{
    Harness h = BuildHarness();
    Guid clientId = Guid.NewGuid();

    TaxRemittance stayPosted = await h.Service.RecordRemittanceAsync(clientId,
        new TaxRemittanceBody(1_700m, 1_240m, new DateOnly(2026, 7, 15), null));
    TaxRemittance goVoid = await h.Service.RecordRemittanceAsync(clientId,
        new TaxRemittanceBody(900m, 500m, new DateOnly(2026, 8, 15), null));

    Guid entryId = Assert.Single(await h.Ledger.GetEntriesBySourceRefAsync(clientId, goVoid.Id)).Id;
    await h.Ledger.VoidAsync(clientId, entryId, new VoidRequest("withdrawn directly"));

    PagedResponse<TaxRemittance> page = await h.Service.ListRemittancesAsync(clientId, 0, 50, descending: false, includeVoided: true);

    Assert.Equal(TaxRemittanceStatus.Posted, page.Items.Single(r => r.Id == stayPosted.Id).Status);
    Assert.Equal(TaxRemittanceStatus.Void, page.Items.Single(r => r.Id == goVoid.Id).Status);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter "ListRuns_reports_Void_per_row|ListRemittances_reports_Void_per_row"`
Expected: FAIL — `PayrollService` has no `ListRunsAsync`/`ListRemittancesAsync` (compile error).

- [ ] **Step 4: Add the list methods to the service**

In `PayrollService.cs`, add the two list methods and a shared helper (place near the getters):

```csharp
    // ── List reads (ledger-truth status, one batched ledger call per page) ─────

    public async Task<PagedResponse<PayrollRun>> ListRunsAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        PagedResponse<PayrollRun> page = await runs.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
        ILookup<Guid, EntryResponse> byRef = await EntriesByRefAsync(clientId, page.Items.Select(r => r.Id), ct);
        List<PayrollRun> overlaid = page.Items
            .Select(r => r.Status == PayrollRunStatus.Void || PayrollLedgerStatus.ShowsVoided(byRef[r.Id].ToList())
                ? r with { Status = PayrollRunStatus.Void }
                : r)
            .ToList();
        return new PagedResponse<PayrollRun>(overlaid, page.Total, page.Skip, page.Limit);
    }

    public async Task<PagedResponse<TaxRemittance>> ListRemittancesAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        PagedResponse<TaxRemittance> page = await remittances.GetByClientPagedAsync(clientId, skip, limit, descending, includeVoided, ct);
        ILookup<Guid, EntryResponse> byRef = await EntriesByRefAsync(clientId, page.Items.Select(r => r.Id), ct);
        List<TaxRemittance> overlaid = page.Items
            .Select(r => r.Status == TaxRemittanceStatus.Void || PayrollLedgerStatus.ShowsVoided(byRef[r.Id].ToList())
                ? r with { Status = TaxRemittanceStatus.Void }
                : r)
            .ToList();
        return new PagedResponse<TaxRemittance>(overlaid, page.Total, page.Skip, page.Limit);
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

In `PayrollEndpoints.cs`, change `ListRuns` to use `PayrollService` instead of the store directly:

```csharp
    private static async Task<IResult> ListRuns(
        Guid clientId, int? skip, int? limit, string? order, bool? includeVoided,
        PayrollService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        PagedResponse<PayrollRun> page = await service.ListRunsAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeVoided ?? false, cancellationToken);
        return Results.Ok(new PagedResponse<PayrollRunView>(
            page.Items.Select(r => new PayrollRunView(r)).ToList(), page.Total, page.Skip, page.Limit));
    }
```

And `ListRemittances` likewise:

```csharp
    private static async Task<IResult> ListRemittances(
        Guid clientId, int? skip, int? limit, string? order, bool? includeVoided,
        PayrollService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        PagedResponse<TaxRemittance> page = await service.ListRemittancesAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeVoided ?? false, cancellationToken);
        return Results.Ok(new PagedResponse<TaxRemittanceView>(
            page.Items.Select(r => new TaxRemittanceView(r)).ToList(), page.Total, page.Skip, page.Limit));
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter "PayrollServiceTests|PayrollPagingTests"`
Expected: PASS (the 2 new list-overlay tests + all existing service/paging tests).

- [ ] **Step 7: Commit**

```bash
git add Modules/Payroll/Accounting101.Payroll/ILedgerClient.cs Modules/Payroll/Accounting101.Payroll.Api/HttpLedgerClient.cs Modules/Payroll/Accounting101.Payroll/PayrollService.cs Modules/Payroll/Accounting101.Payroll.Api/PayrollEndpoints.cs Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs Modules/Payroll/Accounting101.Payroll.Tests/PayrollServiceTests.cs
git commit -m "feat(payroll): list reads fold ledger-truth Void via batch sourceRefs read"
```

---

## Task 4: E2E list proof + Payroll-scoped guard proof + reconciliation

**Files:**
- Test: `Modules/Payroll/Accounting101.Payroll.Tests/PayrollE2eTests.cs`

**Interfaces:**
- Consumes: the full stack through the real host (`PayrollHostFixture`), including Task 3's list path and the shipped module-entry guard. Reuses the existing private `SetUpPayrollChartAsync(HttpClient controller, Guid clientId)` helper and the `fixture.SeedSodClientAsync()` / account-id members already in the file.

- [ ] **Step 1: Write the E2E list-truth proof**

Add to `PayrollE2eTests.cs`:

```csharp
[Fact]
public async Task Run_list_reflects_module_void_across_the_page()
{
    (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
    await SetUpPayrollChartAsync(controller, clientId);

    RecordPayrollRunRequest r1 = new(Gross, EmployeeFica, EmployerFica, Deductions, IncomeTax, new DateOnly(2026, 6, 30), null);
    RecordPayrollRunRequest r2 = new(Gross, EmployeeFica, EmployerFica, Deductions, IncomeTax, new DateOnly(2026, 7, 31), null);

    PayrollRun run1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", r1)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PayrollRun>())!;
    PayrollRun run2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", r2)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PayrollRun>())!;

    // Void the second via the module (Controller holds payroll.write + gl.void under SoD).
    (await controller.PostAsJsonAsync($"/clients/{clientId}/payroll-runs/{run2.Id}/void", new Api.VoidReasonRequest("error"))).EnsureSuccessStatusCode();

    PagedResponse<PayrollRunView> page = (await clerk.GetFromJsonAsync<PagedResponse<PayrollRunView>>(
        $"/clients/{clientId}/payroll-runs?includeVoided=true&order=asc"))!;

    Assert.Equal(PayrollRunStatus.Posted, page.Items.Single(v => v.Run.Id == run1.Id).Run.Status);
    Assert.Equal(PayrollRunStatus.Void, page.Items.Single(v => v.Run.Id == run2.Id).Run.Status);
}
```

- [ ] **Step 2: Write the Payroll-scoped guard proof**

Add to `PayrollE2eTests.cs` — a raw GL reverse of a payroll-owned entry must be refused:

```csharp
[Fact]
public async Task Raw_gl_reverse_of_a_payroll_entry_is_refused_by_the_guard()
{
    (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
    await SetUpPayrollChartAsync(controller, clientId);

    RecordPayrollRunRequest request = new(Gross, EmployeeFica, EmployerFica, Deductions, IncomeTax, new DateOnly(2026, 6, 30), null);
    PayrollRun run = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payroll-runs", request)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PayrollRun>())!;

    // Find and approve the spawned entry, so a reversal (not a withdrawal) is what a raw caller would attempt.
    EntryResponse entry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>($"/clients/{clientId}/entries?sourceRef={run.Id}"))!);
    (await approver.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();

    // Raw reverse — a plain user request carrying no module credential — is refused: correct it through the module.
    HttpResponseMessage rawReverse = await controller.PostAsJsonAsync(
        $"/clients/{clientId}/entries/{entry.Id}/reverse",
        new ReverseRequest(new DateOnly(2026, 7, 1), "raw reversal attempt"));
    Assert.Equal(HttpStatusCode.Conflict, rawReverse.StatusCode);
}
```

- [ ] **Step 3: Run the new E2E tests**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter "Run_list_reflects_module_void|Raw_gl_reverse_of_a_payroll_entry"`
Expected: PASS (2 tests).

- [ ] **Step 4: Whole-solution reconciliation**

Run: `dotnet test`
Expected: PASS — entire solution green (no regressions in engine, other modules, or Payroll).

- [ ] **Step 5: Commit**

```bash
git add Modules/Payroll/Accounting101.Payroll.Tests/PayrollE2eTests.cs
git commit -m "test(payroll): E2E list-truth proof + Payroll-scoped guard proof; whole-solution green"
```

---

## Self-Review

**1. Spec coverage:**
- §1 goal (verify + prove + ledger-truth status, both doc types, no engine change) → Tasks 1-4.
- §2 verify finding (Payroll conforms; guard covers Payroll) → Task 4 guard proof + "no data-model change" discipline.
- §3.1 resolver + union rule + no-entries fallback → Task 1 + the union expressions in Tasks 2/3.
- §3.2 module batch client method → Task 3; detail overlay → Task 2; list overlay + endpoint reroute → Task 3. (Engine batch read/`sourceRefs` param already on master — no task, per §6.)
- §4 no data-model change → no task adds/removes a stored field (Global Constraints).
- §5 tests: resolver units (T1); detail overlay BOTH branches BOTH doc types (T2); list overlay BOTH doc types (T3); E2E list proof + guard proof (T4); reconciliation (T4).
- §7 risk (union subtlety, doubled surface) → symmetric tests in T2/T3; T4 runs full suite.
- §8 sequencing (green at every commit) → four tasks, each ends green.

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to". Every code step shows full code.

**3. Type consistency:** `PayrollLedgerStatus.ShowsVoided(IReadOnlyList<EntryResponse>)` defined T1, consumed T2/T3. `GetEntriesBySourceRefsAsync(Guid, IReadOnlyList<Guid>, CancellationToken)` defined on the module `ILedgerClient` T3, implemented in `HttpLedgerClient` + `FakeLedgerClient` T3. `ListRunsAsync`/`ListRemittancesAsync` defined T3, called by `PayrollEndpoints` T3. `EntryResponse` constructor arity matches the codebase (14 positional args in the test factory; `ReversalOf` at position 10, `SourceRef` at 13). `SourceRef` is `Guid?` — the module lookup guards `SourceRef is not null`. `PayrollRunStatus`/`TaxRemittanceStatus` are `{ Posted, Void }` (the union only ever promotes to Void). Request/view types (`RecordPayrollRunRequest`, `RecordTaxRemittanceRequest`, `PayrollRunView(Run)`, `TaxRemittanceView(Remittance)`, `Api.VoidReasonRequest`) match the existing module surface used in the E2E file.

Note: this plan has no engine task because the engine batch read (`MongoJournalStore.GetBySourceRefsAsync`) and the `sourceRefs` CSV endpoint param shipped with the Cash cycle (master `6367aaa`). Task 3's `HttpLedgerClient` call to `entries?sourceRefs=<csv>` targets that existing endpoint.
