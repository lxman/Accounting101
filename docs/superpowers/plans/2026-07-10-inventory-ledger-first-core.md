# Inventory Ledger-First Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the Inventory module to ledger-first — per-item carried value becomes a `{Item}`-dimensioned fold of the Inventory account, on-hand quantity becomes a rebuildable projection over movement documents gated by entry-on-books, and the materialized `Item.OnHandQuantity`/`TotalValue` + movement `Resulting*` snapshots are deleted so no financial fact is stored twice.

**Architecture:** Value has a GL home → dimensioned ledger fold (AR/FA precedent; Inventory is debit-normal so the fold reads positive, **no negation**). Quantity has no GL home → module-side projection of each movement's signed quantity, counted iff its spawned GL entry is on the books — the same lever the value fold uses, so the two cannot diverge. Weighted-average cost couples them (`avg = value ÷ quantity`), so both folds always use the **identical gate**: reads posted-only, writes pending-inclusive.

**Tech Stack:** C# / .NET 10, ASP.NET minimal APIs, MongoDB (engine document store), xUnit + EphemeralMongo (shared), Angular (movement-view cleanup only).

## Global Constraints

- **Zero engine change.** `AggregateSubledgerAsync` + `RequiredDimensions` + post-time enforcement (AR cycle) and the batch `GetBySourceRefsAsync` + `GET /entries?sourceRefs=` CSV param (Cash cycle) are already on master. Consume only.
- **Green at every commit.** Dimension the recipe before requiring the dimension; fold on read before deleting the stored fields; never delete a field before its reads fold (spec §8 ordering).
- **Sign is the easy case:** Inventory is a debit-normal asset → the debit-positive `SubledgerLineResponse.Balance` reads **positive**. Do NOT negate (the opposite of AR's liability / FA's contra-asset).
- **Shared-gate invariant:** the value fold and the quantity projection must use the identical `includePending` and the identical on-books movement set, or weighted-average is meaningless. This is the #1 correctness property.
- **Doc-first ordering:** `RecordAsync` persists the movement document before posting the entry; a doc without an on-books entry is inert, an entry without a doc diverges. The idempotent `EntryIdentity.ForSource(movementId)` post covers retries.
- **Read-model vs stored body:** `Item` (read model) KEEPS `OnHandQuantity`/`TotalValue` (populated from the folds on read); `ItemDocument` (stored body) DROPS them. `ItemView(Item)` is unchanged → item UI untouched. (Mirrors FA: `AssetView` still reads `Asset.AccumulatedDepreciation`, folded on read; `AssetDocument` dropped it.)
- **`SignedQuantityEffect` survives** — it is the projection's per-movement signed-quantity input. Only `SignedValueEffect` (the old void-restore machinery) is deleted.
- **Greenfield / reseed, no backfill.**
- Spec: `docs/superpowers/specs/2026-07-10-inventory-ledger-first-core-design.md`. Branch: `redesign/inventory-ledger-first-core`.

---

## Task 1: Ledger client seam — subledger fold + batch source-ref read

Add the two read methods the folds need to the module's `ILedgerClient`, its `HttpLedgerClient`, and the test `FakeLedgerClient`. Additive — no consumer yet. The fake is made richer than FA's (the fold gates on posting, and an `ApproveAll()` helper flips pending→posted) so later tasks can prove posted-vs-pending semantics in unit tests, coherently across the value fold and the quantity projection.

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory/ILedgerClient.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs`
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/FakeLedgerClientTests.cs` (new)

**Interfaces:**
- Produces:
  - `Task<IReadOnlyList<SubledgerLineResponse>> ILedgerClient.GetSubledgerAsync(Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default, bool includePending = false)`
  - `Task<IReadOnlyList<EntryResponse>> ILedgerClient.GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)`
  - `FakeLedgerClient.ApproveAll()` (test helper: PendingApproval → Posted)
- Consumes: `SubledgerLineResponse(Guid AccountId, Guid DimensionValue, decimal Balance)` and `EntryResponse` (both in `Accounting101.Ledger.Contracts`).

- [ ] **Step 1: Write the failing test**

Create `Modules/Inventory/Accounting101.Inventory.Tests/FakeLedgerClientTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Inventory.Tests;

public class FakeLedgerClientTests
{
    private static readonly Guid Client = Guid.NewGuid();

    [Fact]
    public async Task Subledger_fold_is_debit_positive_grouped_by_dimension_and_gated_by_posting()
    {
        var ledger = new FakeLedgerClient();
        Guid inv = Guid.NewGuid();
        Guid itemA = Guid.NewGuid();
        var dims = new Dictionary<string, Guid> { ["Item"] = itemA };

        await ledger.PostAsync(Client, new PostEntryRequest(
            Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null,
            [new PostLineRequest(inv, "Debit", 100m, dims), new PostLineRequest(Guid.NewGuid(), "Credit", 100m)],
            SourceRef: Guid.NewGuid(), SourceType: "StockMovement"));

        // Pending is invisible to a posted-only fold, visible to a pending-inclusive one.
        Assert.Empty(await ledger.GetSubledgerAsync(Client, inv, "Item", null));
        IReadOnlyList<SubledgerLineResponse> pending =
            await ledger.GetSubledgerAsync(Client, inv, "Item", null, default, includePending: true);
        Assert.Equal(100m, pending.Single(l => l.DimensionValue == itemA).Balance);

        ledger.ApproveAll();
        IReadOnlyList<SubledgerLineResponse> posted = await ledger.GetSubledgerAsync(Client, inv, "Item", null);
        Assert.Equal(100m, posted.Single(l => l.DimensionValue == itemA).Balance);
    }

    [Fact]
    public async Task GetEntriesBySourceRefs_returns_entries_for_all_requested_refs()
    {
        var ledger = new FakeLedgerClient();
        Guid refA = Guid.NewGuid(), refB = Guid.NewGuid();
        await ledger.PostAsync(Client, Req(refA));
        await ledger.PostAsync(Client, Req(refB));

        IReadOnlyList<EntryResponse> got = await ledger.GetEntriesBySourceRefsAsync(Client, [refA, refB]);
        Assert.Equal(2, got.Count);
        Assert.Empty(await ledger.GetEntriesBySourceRefsAsync(Client, []));
    }

    private static PostEntryRequest Req(Guid sourceRef) => new(
        Guid.NewGuid(), new DateOnly(2026, 1, 1), null, null,
        [new PostLineRequest(Guid.NewGuid(), "Debit", 1m), new PostLineRequest(Guid.NewGuid(), "Credit", 1m)],
        SourceRef: sourceRef, SourceType: "StockMovement");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter FakeLedgerClientTests`
Expected: FAIL — compile error (`GetSubledgerAsync`/`GetEntriesBySourceRefsAsync`/`ApproveAll` do not exist).

- [ ] **Step 3: Add the interface methods**

In `Modules/Inventory/Accounting101.Inventory/ILedgerClient.cs`, add inside the interface (after `GetEntriesBySourceRefAsync`):

```csharp
    /// <summary>Batch of <see cref="GetEntriesBySourceRefAsync"/> — every entry tied to any of the given
    /// source documents, in one call (the quantity projection checks each movement's entry status).</summary>
    Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(
        Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default);

    /// <summary>Subsidiary-ledger balances for a control account grouped by one dimension type (e.g. the
    /// Inventory account by "Item"). Debit-positive. <paramref name="includePending"/> admits not-yet-approved
    /// entries — used by write paths (next-issue cost, block-negative); reads pass false (posted-only).</summary>
    Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false);
```

- [ ] **Step 4: Implement the HTTP client methods**

In `Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs`, add after `GetEntriesBySourceRefAsync`:

```csharp
    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(
        Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
    {
        if (sourceRefs.Count == 0) return [];
        string csv = string.Join(',', sourceRefs);
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?sourceRefs={csv}");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false)
    {
        string url = $"clients/{clientId}/subledger?account={account}&dimension={Uri.EscapeDataString(dimension)}";
        if (asOf is { } d) url += $"&asOf={d:yyyy-MM-dd}";
        if (includePending) url += "&includePending=true";
        // A plain member read — like GetEntriesBySourceRefAsync, no module credential is attached.
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, url);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        SubledgerResponse body = (await response.Content.ReadFromJsonAsync<SubledgerResponse>(cancellationToken))!;
        return body.Lines;
    }
```

`SubledgerResponse` lives in `Accounting101.Ledger.Contracts` (already imported via `using Accounting101.Ledger.Contracts;`).

- [ ] **Step 5: Replace the FakeLedgerClient with the richer version**

In `Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs`, replace the entire `FakeLedgerClient` class with:

```csharp
/// <summary>In-memory stand-in for the engine: records posts, tracks each entry's lines for a real
/// per-dimension fold, models approve/reverse/void, and resolves entries by source back-link. The fold
/// gates on posting state (Active + Posted, or +PendingApproval when includePending) so the value fold and
/// the quantity projection see a consistent posted/pending world; ApproveAll() flips pending → posted.</summary>
internal sealed class FakeLedgerClient : ILedgerClient
{
    private readonly Dictionary<Guid, EntryResponse> _entries = new();
    private readonly Dictionary<Guid, IReadOnlyList<PostLineRequest>> _linesById = new();
    private readonly List<PostEntryRequest> _posted = [];

    public IReadOnlyList<PostEntryRequest> Posted => _posted;
    public bool ReversedOrWithdrawn { get; private set; }
    public bool ReturnNoEntries { get; set; }

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        _posted.Add(entry);
        var id = Guid.NewGuid();
        _entries[id] = Entry(id, entry.SourceRef, entry.SourceType, posting: "PendingApproval", reversalOf: null, lines: entry.Lines);
        _linesById[id] = entry.Lines;
        return Task.FromResult(new PostEntryResponse(id, "Active", "PendingApproval"));
    }

    /// <summary>Test helper: approve every pending entry so posted-only reads see them.</summary>
    public void ApproveAll()
    {
        foreach (Guid id in _entries.Keys.ToList())
            if (_entries[id].Posting == "PendingApproval")
                _entries[id] = _entries[id] with { Posting = "Posted" };
    }

    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        ReversedOrWithdrawn = true;
        EntryResponse original = _entries[entryId];
        var id = Guid.NewGuid();
        // Negated lines, posted immediately so the pair nets to zero under a posted-only fold; the original
        // stays Active (still counted) exactly like the real engine.
        IReadOnlyList<PostLineRequest> reversedLines = _linesById.TryGetValue(entryId, out IReadOnlyList<PostLineRequest>? originalLines)
            ? originalLines.Select(l => l with { Direction = Flip(l.Direction) }).ToList()
            : [];
        EntryResponse reversal = Entry(id, original.SourceRef, original.SourceType, posting: "Posted", reversalOf: entryId, lines: reversedLines);
        _entries[id] = reversal;
        _linesById[id] = reversedLines;
        return Task.FromResult(reversal);
    }

    public Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        ReversedOrWithdrawn = true;
        EntryResponse voided = _entries[entryId] with { Status = "Voided" };
        _entries[entryId] = voided;
        return Task.FromResult(voided);
    }

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntryResponse>>(ReturnNoEntries
            ? []
            : _entries.Values.Where(e => e.SourceRef == sourceRef).ToList());

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntryResponse>>(ReturnNoEntries
            ? []
            : _entries.Values.Where(e => e.SourceRef is { } s && sourceRefs.Contains(s)).ToList());

    public Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false)
    {
        Dictionary<Guid, decimal> totals = new();
        foreach ((Guid id, EntryResponse response) in _entries)
        {
            if (response.Status != "Active") continue;
            if (!includePending && response.Posting != "Posted") continue;
            if (!_linesById.TryGetValue(id, out IReadOnlyList<PostLineRequest>? lines)) continue;
            foreach (PostLineRequest line in lines)
            {
                if (line.AccountId != account) continue;
                if (line.Dimensions is null || !line.Dimensions.TryGetValue(dimension, out Guid dimValue)) continue;
                decimal signed = line.Direction == "Debit" ? line.Amount : -line.Amount;
                totals[dimValue] = totals.GetValueOrDefault(dimValue) + signed;
            }
        }
        return Task.FromResult<IReadOnlyList<SubledgerLineResponse>>(
            totals.Select(kv => new SubledgerLineResponse(account, kv.Key, kv.Value)).ToList());
    }

    private static string Flip(string direction) => direction == "Debit" ? "Credit" : "Debit";

    private static EntryResponse Entry(
        Guid id, Guid? sourceRef, string? sourceType, string posting, Guid? reversalOf,
        IReadOnlyList<PostLineRequest>? lines = null)
    {
        IReadOnlyList<EntryLineResponse> mapped = (lines ?? []).Select(l =>
            new EntryLineResponse(l.AccountId, l.Direction, l.Amount, l.Dimensions ?? new Dictionary<string, Guid>(), null)).ToList();
        return new(id, 0, default, "Standard", "Active", posting, mapped.Count, null, null, reversalOf, null, mapped, sourceRef, sourceType);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS — `FakeLedgerClientTests` green; the rest of the suite still green (the fake's existing members are preserved; `ResultingOnHand`/`ResultingTotalValue` still on the bodies at this point).

- [ ] **Step 7: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/ILedgerClient.cs Modules/Inventory/Accounting101.Inventory.Api/HttpLedgerClient.cs Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs Modules/Inventory/Accounting101.Inventory.Tests/FakeLedgerClientTests.cs
git commit -m "feat(inventory): ledger client seam — subledger fold + batch source-ref read (T1)"
```

---

## Task 2: Dimension the Inventory line with `{Item}`

`InventoryPosting.Compose` gains an `itemId` and tags the Inventory-account line with `Dimensions: { "Item": itemId }` in all four recipes. Counter-lines stay un-dimensioned. Additive — the stored valuation fields are still written, so behavior is unchanged and the suite stays green.

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryPosting.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs:59-60` (the `Compose` call site)
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/InventoryPostingTests.cs`

**Interfaces:**
- Produces: `InventoryPosting.Compose(MovementType type, decimal signedQuantity, Guid movementId, Guid itemId, decimal extendedCost, DateOnly effectiveDate, string? memo, InventoryPostingAccounts accounts)` — note the new `Guid itemId` parameter (5th positional).

- [ ] **Step 1: Write the failing test**

Add to `InventoryPostingTests.cs`:

```csharp
[Fact]
public void Receipt_dimensions_the_inventory_line_by_item_only()
{
    Guid itemId = Guid.NewGuid();
    var accounts = new InventoryPostingAccounts
    {
        InventoryAssetAccountId = Guid.NewGuid(), CogsAccountId = Guid.NewGuid(),
        GrniClearingAccountId = Guid.NewGuid(), InventoryAdjustmentAccountId = Guid.NewGuid(),
    };

    PostEntryRequest entry = InventoryPosting.Compose(
        MovementType.Receipt, 5m, Guid.NewGuid(), itemId, 100m, new DateOnly(2026, 1, 1), null, accounts);

    PostLineRequest invLine = entry.Lines.Single(l => l.AccountId == accounts.InventoryAssetAccountId);
    Assert.Equal(itemId, invLine.Dimensions!["Item"]);
    PostLineRequest grniLine = entry.Lines.Single(l => l.AccountId == accounts.GrniClearingAccountId);
    Assert.Null(grniLine.Dimensions);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter InventoryPostingTests`
Expected: FAIL — `Compose` has no `itemId` parameter (compile error).

- [ ] **Step 3: Add `itemId` and dimension the Inventory line**

In `InventoryPosting.cs`, change the signature and the two Inventory-line constructions. The Inventory line appears in every recipe; tag exactly it:

```csharp
    public static PostEntryRequest Compose(
        MovementType type, decimal signedQuantity, Guid movementId, Guid itemId, decimal extendedCost,
        DateOnly effectiveDate, string? memo, InventoryPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (extendedCost <= 0m)
            throw new ArgumentException("Extended cost must be positive.", nameof(extendedCost));

        Dictionary<string, Guid> itemDim = new() { ["Item"] = itemId };

        List<PostLineRequest> lines = type switch
        {
            MovementType.Receipt =>
            [
                new(accounts.InventoryAssetAccountId, "Debit", extendedCost, itemDim),
                new(accounts.GrniClearingAccountId, "Credit", extendedCost),
            ],
            MovementType.Issue =>
            [
                new(accounts.CogsAccountId, "Debit", extendedCost),
                new(accounts.InventoryAssetAccountId, "Credit", extendedCost, itemDim),
            ],
            MovementType.Adjustment when signedQuantity < 0m =>   // shrinkage
            [
                new(accounts.InventoryAdjustmentAccountId, "Debit", extendedCost),
                new(accounts.InventoryAssetAccountId, "Credit", extendedCost, itemDim),
            ],
            MovementType.Adjustment =>                            // overage
            [
                new(accounts.InventoryAssetAccountId, "Debit", extendedCost, itemDim),
                new(accounts.InventoryAdjustmentAccountId, "Credit", extendedCost),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(StockMovementSourceType, movementId),
            EffectiveDate: effectiveDate,
            Reference: null,
            Memo: memo,
            Lines: lines,
            SourceRef: movementId,
            SourceType: StockMovementSourceType);
    }
```

- [ ] **Step 4: Update the call site**

In `InventoryMovementService.cs`, the `Compose` call (currently lines 59-60) passes `movement.Id` then `effect.ExtendedCost`; insert `request.ItemId`:

```csharp
        PostEntryRequest entry = InventoryPosting.Compose(
            request.Type, request.Quantity, movement.Id, request.ItemId, effect.ExtendedCost, request.EffectiveDate, request.Memo, postingAccounts);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS — new posting test green; existing composition/E2E tests still green (the extra dimension is additive; no account requires it yet).

- [ ] **Step 6: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/InventoryPosting.cs Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs Modules/Inventory/Accounting101.Inventory.Tests/InventoryPostingTests.cs
git commit -m "feat(inventory): dimension the Inventory line by {Item} (T2)"
```

---

## Task 3: Require the `{Item}` dimension on the Inventory account

Configure the Inventory account with `RequiredDimensions = ["Item"]` in the E2E chart setup (so posts must carry the tag — Task 2 ensures they do) and prove the engine rejects an untagged Inventory line (422). Only enforcement changes; the fold works with or without it.

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/MovementReceiptE2eTests.cs` (the shared chart-setup helper these E2E tests use — locate the `PUT /clients/{id}/accounts/{InventoryAssetAccountId}` call and add `RequiredDimensions = ["Item"]`)
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/InventoryLedgerFirstProofTests.cs` (new — the untagged-line enforcement test; grows in Task 7)

> **Note for the implementer:** the four inventory E2E test classes (`MovementReceiptE2eTests`, `MovementIssueE2eTests`, `MovementAdjustmentE2eTests`, `MovementVoidE2eTests`) each set up the chart before posting. Find their common account-PUT helper (grep the test project for `accounts/{` and `AccountRequest`). Add `RequiredDimensions = ["Item"]` to the **Inventory** account's `AccountRequest` only. The `AccountRequest` shape is `{ Number, Name, Type, RequiredDimensions }` (see `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsEndpointsTests.cs:17-20`). If each class has its own copy, update each; if they share a helper, update once.

- [ ] **Step 1: Write the failing test**

Create `Modules/Inventory/Accounting101.Inventory.Tests/InventoryLedgerFirstProofTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Inventory.Tests;

[Collection("inventory-host")]
public class InventoryLedgerFirstProofTests(InventoryHostFixture fixture)
{
    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name,
        string type, IReadOnlyList<string>? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = requiredDimensions }))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task Inventory_account_rejects_an_untagged_line()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await PutAccountAsync(http, clientId, fixture.InventoryAssetAccountId, "1400", "Inventory", "Asset", ["Item"]);
        await PutAccountAsync(http, clientId, fixture.GrniClearingAccountId, "2050", "GRNI", "Liability");

        PostEntryRequest untagged = new(null, new DateOnly(2026, 6, 30), "R", "m",
        [
            new PostLineRequest(fixture.InventoryAssetAccountId, "Debit", 100m),   // no {Item}
            new PostLineRequest(fixture.GrniClearingAccountId, "Credit", 100m),
        ]);

        HttpResponseMessage response = await http.PostAsJsonAsync($"/clients/{clientId}/entries", untagged);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
```

If `InventoryHostFixture` is not already an xUnit collection fixture, add `[CollectionDefinition("inventory-host")] public class InventoryHostCollection : ICollectionFixture<InventoryHostFixture>;` (check the existing E2E classes for the collection name they use and reuse it).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter InventoryLedgerFirstProofTests`
Expected: FAIL — the Inventory account is not yet configured to require `{Item}`, so the untagged post succeeds (200/201) instead of 422.

- [ ] **Step 3: Configure `RequiredDimensions = ["Item"]` in the E2E chart setup**

The test above sets it directly. Now make the *existing* movement E2E tests configure the Inventory account with `RequiredDimensions = ["Item"]` too, so they exercise the enforced path. In each movement E2E class's chart-setup, change the Inventory account's `AccountRequest` to include `RequiredDimensions = ["Item"]` (Task 2 already dimensions every posted Inventory line, so they stay green).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS — the untagged-line test 422s; all movement E2E tests still green (their posts carry `{Item}`).

- [ ] **Step 5: Document the onboarding requirement**

Append to the spec's deployment note (bottom of `docs/superpowers/specs/2026-07-10-inventory-ledger-first-core-design.md`), one line: *"Onboarding: the Inventory (1400) account must be configured `RequiredDimensions = [\"Item\"]`; the dev seed (`.localdev/start.ps1`) already sets the four inventory account ids — add the PUT that sets this on 1400."*

- [ ] **Step 6: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory.Tests docs/superpowers/specs/2026-07-10-inventory-ledger-first-core-design.md
git commit -m "feat(inventory): require {Item} on the Inventory account + untagged-line 422 proof (T3)"
```

---

## Task 4: `ItemValuationService` — value fold + quantity projection

The service that derives per-item `(onHand, totalValue, averageUnitCost)` from the ledger, keyed off entry-on-books. No consumer yet (Task 5 wires it). Also adds `IStockMovementStore.GetAllByItemAsync` (unbounded, all statuses) — the projection's input.

**Files:**
- Create: `Modules/Inventory/Accounting101.Inventory/ItemValuationService.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryPorts.cs` (add `GetAllByItemAsync` to `IStockMovementStore`)
- Modify: `Modules/Inventory/Accounting101.Inventory/DocumentStockMovementStore.cs` (implement it)
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs` (`InMemoryStockMovementStore.GetAllByItemAsync`)
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs` (register the service)
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/ItemValuationServiceTests.cs` (new)

**Interfaces:**
- Produces:
  - `readonly record struct ItemValuation(decimal OnHand, decimal TotalValue) { decimal AverageUnitCost }`
  - `ItemValuationService.GetAsync(Guid clientId, Guid itemId, bool includePending, CancellationToken ct = default) : Task<ItemValuation>`
  - `IStockMovementStore.GetAllByItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default) : Task<IReadOnlyList<StockMovement>>`
- Consumes: `ILedgerClient.GetSubledgerAsync`, `ILedgerClient.GetEntriesBySourceRefsAsync` (Task 1); `StockMovement.SignedQuantityEffect`.

- [ ] **Step 1: Write the failing test**

Create `Modules/Inventory/Accounting101.Inventory.Tests/ItemValuationServiceTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;
using Xunit;

namespace Accounting101.Inventory.Tests;

public class ItemValuationServiceTests
{
    private static readonly Guid Client = Guid.NewGuid();

    private static (ItemValuationService svc, InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider accounts) Build()
    {
        var movements = new InMemoryStockMovementStore();
        var ledger = new FakeLedgerClient();
        var accounts = new FixedInventoryAccountsProvider();
        return (new ItemValuationService(movements, accounts, ledger), movements, ledger, accounts);
    }

    // Records a movement doc AND posts its dimensioned entry, mirroring what the movement service will do.
    private static async Task Post(InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider acct,
        Guid itemId, MovementType type, decimal qty, decimal ext)
    {
        StockMovement m = await movements.RecordAsync(Client, new StockMovementBody(
            itemId, type, new DateOnly(2026, 1, 1), null, qty, ext / Math.Abs(qty), ext, 0m, 0m));
        await ledger.PostAsync(Client, InventoryPosting.Compose(type, qty, m.Id, itemId, ext, new DateOnly(2026, 1, 1), null,
            new InventoryPostingAccounts
            {
                InventoryAssetAccountId = acct.InventoryAssetAccountId, CogsAccountId = acct.CogsAccountId,
                GrniClearingAccountId = acct.GrniClearingAccountId, InventoryAdjustmentAccountId = acct.InventoryAdjustmentAccountId,
            }));
    }

    [Fact]
    public async Task Posted_receipt_folds_positive_value_and_projects_quantity()
    {
        (ItemValuationService svc, InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider acct) = Build();
        Guid item = Guid.NewGuid();
        await Post(movements, ledger, acct, item, MovementType.Receipt, 10m, 100m);
        ledger.ApproveAll();

        ItemValuation v = await svc.GetAsync(Client, item, includePending: false);
        Assert.Equal(10m, v.OnHand);           // projected signed quantity
        Assert.Equal(100m, v.TotalValue);      // debit-normal asset → POSITIVE, no negation
        Assert.Equal(10m, v.AverageUnitCost);
    }

    [Fact]
    public async Task Pending_movement_is_posted_only_invisible_but_write_visible()
    {
        (ItemValuationService svc, InMemoryStockMovementStore movements, FakeLedgerClient ledger, FixedInventoryAccountsProvider acct) = Build();
        Guid item = Guid.NewGuid();
        await Post(movements, ledger, acct, item, MovementType.Receipt, 10m, 100m);   // left PendingApproval

        Assert.Equal(0m, (await svc.GetAsync(Client, item, includePending: false)).OnHand);
        ItemValuation write = await svc.GetAsync(Client, item, includePending: true);
        Assert.Equal(10m, write.OnHand);
        Assert.Equal(100m, write.TotalValue);
    }

    [Fact]
    public async Task Empty_item_folds_to_zero()
    {
        (ItemValuationService svc, _, _, _) = Build();
        ItemValuation v = await svc.GetAsync(Client, Guid.NewGuid(), includePending: false);
        Assert.Equal(0m, v.OnHand);
        Assert.Equal(0m, v.TotalValue);
        Assert.Equal(0m, v.AverageUnitCost);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter ItemValuationServiceTests`
Expected: FAIL — compile error (`ItemValuationService`, `GetAllByItemAsync` do not exist).

- [ ] **Step 3: Add `GetAllByItemAsync` to the port + both stores**

In `InventoryPorts.cs`, add to `IStockMovementStore` (after `GetLatestForItemAsync`):

```csharp
    /// <summary>Every movement for the item, all statuses, unbounded — the quantity projection's input
    /// (it gates on each movement's ENTRY state, not the document state).</summary>
    Task<IReadOnlyList<StockMovement>> GetAllByItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default);
```

In `DocumentStockMovementStore.cs`, add:

```csharp
    public async Task<IReadOnlyList<StockMovement>> GetAllByItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        // Unbounded scan (the store already relies on this pattern): all statuses for the item.
        IReadOnlyList<DocumentResult<StockMovementBody>> all =
            await documents.QueryAsync<StockMovementBody>(clientId, Collection, Tags(), includeVoided: true, cancellationToken: ct);
        return all.Where(r => r.Body.ItemId == itemId).Select(Map).ToList();
    }
```

In `Fakes.cs`, add to `InMemoryStockMovementStore`:

```csharp
    public Task<IReadOnlyList<StockMovement>> GetAllByItemAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StockMovement>>(_movements.Where(m => m.ItemId == itemId).ToList());
```

- [ ] **Step 4: Create `ItemValuationService`**

Create `Modules/Inventory/Accounting101.Inventory/ItemValuationService.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory;

/// <summary>An item's derived on-hand quantity and carried value — value from the {Item} fold of the
/// Inventory account (debit-normal → positive), quantity from the movement documents' signed quantity.</summary>
public readonly record struct ItemValuation(decimal OnHand, decimal TotalValue)
{
    public decimal AverageUnitCost => OnHand == 0m ? 0m : TotalValue / OnHand;
}

/// <summary>Computes an item's valuation from the ledger fold + movement projection, both keyed off
/// entry-on-books so subledger and GL cannot diverge. Reads pass <c>includePending: false</c> (posted-only);
/// writes (next-issue cost, block-negative) pass <c>true</c> — the SAME gate for value and quantity so the
/// weighted-average ratio stays coherent.</summary>
public sealed class ItemValuationService(
    IStockMovementStore movements, IInventoryAccountsProvider accounts, ILedgerClient ledger)
{
    public const string ItemDimension = "Item";

    public async Task<ItemValuation> GetAsync(Guid clientId, Guid itemId, bool includePending, CancellationToken ct = default)
    {
        InventoryPostingAccounts acct = await accounts.GetAccountsAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> lines =
            await ledger.GetSubledgerAsync(clientId, acct.InventoryAssetAccountId, ItemDimension, null, ct, includePending);
        // Inventory is debit-normal → the debit-positive fold reads POSITIVE. No negation (unlike AR/FA).
        decimal value = lines.Where(l => l.DimensionValue == itemId).Sum(l => l.Balance);
        decimal onHand = await ProjectQuantityAsync(clientId, itemId, includePending, ct);
        return new ItemValuation(onHand, value);
    }

    private async Task<decimal> ProjectQuantityAsync(Guid clientId, Guid itemId, bool includePending, CancellationToken ct)
    {
        IReadOnlyList<StockMovement> all = await movements.GetAllByItemAsync(clientId, itemId, ct);
        if (all.Count == 0) return 0m;
        IReadOnlyList<EntryResponse> entries =
            await ledger.GetEntriesBySourceRefsAsync(clientId, all.Select(m => m.Id).ToList(), ct);
        ILookup<Guid, EntryResponse> bySource = entries.Where(e => e.SourceRef is not null).ToLookup(e => e.SourceRef!.Value);

        decimal onHand = 0m;
        foreach (StockMovement m in all)
            if (OnBooks(bySource[m.Id], includePending))
                onHand += m.SignedQuantityEffect;
        return onHand;
    }

    /// <summary>A movement counts iff its spawned primary entry is on the books: Active, not itself a
    /// reversal, not reversed by another, and — unless pending-inclusive — Posted. Mirrors the cross-module
    /// void resolver; the value fold nets a reversal to zero the same way, so the two axes agree.</summary>
    private static bool OnBooks(IEnumerable<EntryResponse> forSource, bool includePending)
    {
        List<EntryResponse> list = forSource.ToList();
        EntryResponse? primary = list.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });
        if (primary is null) return false;                                 // stranded post / no entry
        if (list.Any(e => e.ReversalOf == primary.Id)) return false;       // reversed → off the books
        return includePending || primary.Posting == "Posted";             // reads: posted-only
    }
}
```

- [ ] **Step 5: Register the service**

In `InventoryServiceExtensions.cs`, after the `InventoryMovementService` registration, add:

```csharp
        services.AddScoped<ItemValuationService>();
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS — `ItemValuationServiceTests` green; whole module suite green.

- [ ] **Step 7: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/ItemValuationService.cs Modules/Inventory/Accounting101.Inventory/InventoryPorts.cs Modules/Inventory/Accounting101.Inventory/DocumentStockMovementStore.cs Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs Modules/Inventory/Accounting101.Inventory.Tests/ItemValuationServiceTests.cs
git commit -m "feat(inventory): ItemValuationService — {Item} value fold + entry-gated quantity projection (T4)"
```

---

## Task 5: Wire reads and the record path through the folds

Route every read and the record/deactivate paths through the folds, while the stored fields are still written (deleted in Task 6). This is the behavioral cut-over — after it, the stored valuation fields are write-only.

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryService.cs` (fold on read; deactivate guard from projection)
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs` (compute effect from pending-inclusive folds; the record path no longer reads `item.OnHandQuantity`/`TotalValue`)
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/InventoryEndpoints.cs` (`GetItem`/`ListItems` route through the service fold, not the raw store)
- Modify: `Modules/Inventory/Accounting101.Inventory/DocumentItemStore.cs` (drop the has-stock read of `OnHandQuantity` from `DeactivateAsync` — the service guards now)
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/InventoryMovementServiceTests.cs`, `ItemDocumentStoreTests.cs`, `InventoryEndpointsTests.cs`

**Interfaces:**
- Produces:
  - `InventoryService.GetAsync` returns an `Item` whose `OnHandQuantity`/`TotalValue` are the posted-only folds.
  - `InventoryService.GetPagedAsync(Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct) : Task<PagedResponse<Item>>` — page of items with folded valuation.
  - `InventoryService.DeactivateAsync` returns `DeactivateResult.HasStock` when the posted-only projected on-hand ≠ 0.
- Consumes: `ItemValuationService.GetAsync` (Task 4).

- [ ] **Step 1: Write the failing test (record path computes from the fold, not the stored item)**

Add to `InventoryMovementServiceTests.cs` — an Issue after a Receipt must cost at the folded weighted-average even though the stored item valuation is never read for the math:

```csharp
[Fact]
public async Task Issue_costs_at_the_folded_weighted_average()
{
    (InventoryMovementService svc, InMemoryItemStore items, InMemoryStockMovementStore movements, FakeLedgerClient ledger, _) = Build();
    Guid clientId = Guid.NewGuid();
    Item item = await items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "ea"));

    await svc.RecordAsync(clientId, new RecordMovement(item.Id, MovementType.Receipt, 10m, 10m, new DateOnly(2026, 1, 1), null));
    await svc.RecordAsync(clientId, new RecordMovement(item.Id, MovementType.Receipt, 10m, 20m, new DateOnly(2026, 1, 2), null));
    // On-hand 20 @ avg 15. Issue 4 → COGS 60.
    StockMovement issue = await svc.RecordAsync(clientId, new RecordMovement(item.Id, MovementType.Issue, 4m, null, new DateOnly(2026, 1, 3), null));

    Assert.Equal(60m, issue.ExtendedCost);
    Assert.Equal(15m, issue.AppliedUnitCost);
}
```

> `Build()` in `InventoryMovementServiceTests` must construct the `InventoryMovementService` with its new dependency (`ItemValuationService`). Update the helper to build an `ItemValuationService(movements, accounts, ledger)` and pass it in. The movement service's `RecordAsync` uses `includePending: true` folds, and the fake counts pending entries, so no `ApproveAll()` is needed for the compute path.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter InventoryMovementServiceTests`
Expected: FAIL — `RecordAsync` still computes from `item.OnHandQuantity`/`TotalValue` (the second receipt's stored value works today, but once wired to the fold the constructor signature changes; initially a compile error on `Build()` until the service takes `ItemValuationService`).

- [ ] **Step 3: Compute the effect from the pending-inclusive fold**

In `InventoryMovementService.cs`, add `ItemValuationService valuation` to the primary constructor parameters:

```csharp
public sealed class InventoryMovementService(
    IItemStore items, IStockMovementStore movements, IInventoryAccountsProvider accounts, ILedgerClient ledger,
    ItemValuationService valuation)
```

Replace the `Valuation current = new(item.OnHandQuantity, item.TotalValue);` line (step 3 of `RecordAsync`) with the pending-inclusive fold:

```csharp
        // Current valuation is the pending-inclusive fold (writes see pending claims), NOT the stored item.
        ItemValuation folded = await valuation.GetAsync(clientId, request.ItemId, includePending: true, ct);
        Valuation current = new(folded.OnHand, folded.TotalValue);
```

Leave the rest of `RecordAsync` intact for now (it still calls `SetValuationAsync` and persists `Resulting*` — removed in Task 6). Doc-first ordering is already in place (persist movement at step 5, post entry at step 7).

- [ ] **Step 4: Fold on read + page fold + deactivate guard**

In `InventoryService.cs`, take the valuation service and overlay folded valuation onto every returned `Item`:

```csharp
public sealed class InventoryService(IItemStore store, ItemValuationService valuation)
{
    // CreateAsync / UpdateAsync / ReactivateAsync unchanged (they return freshly-written master data;
    // a new item folds to zero anyway).

    public async Task<Item?> GetAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        Item? item = await store.GetAsync(clientId, itemId, ct);
        return item is null ? null : await WithValuationAsync(clientId, item, ct);
    }

    public async Task<PagedResponse<Item>> GetPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default)
    {
        PagedResponse<Item> page = await store.GetByClientPagedAsync(clientId, skip, limit, descending, includeInactive, ct);
        List<Item> folded = [];
        foreach (Item item in page.Items) folded.Add(await WithValuationAsync(clientId, item, ct));
        return new PagedResponse<Item>(folded, page.Total, page.Skip, page.Limit);
    }

    public async Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        // Has-stock guard reads the posted-only projection (the store no longer stores on-hand).
        ItemValuation v = await valuation.GetAsync(clientId, itemId, includePending: false, ct);
        if (v.OnHand != 0m && await store.GetAsync(clientId, itemId, ct) is not null)
            return DeactivateResult.HasStock;
        return await store.DeactivateAsync(clientId, itemId, ct);
    }

    private async Task<Item> WithValuationAsync(Guid clientId, Item item, CancellationToken ct)
    {
        ItemValuation v = await valuation.GetAsync(clientId, item.Id, includePending: false, ct);
        return item with { OnHandQuantity = v.OnHand, TotalValue = v.TotalValue };
    }
}
```

Keep the existing `CreateAsync`/`UpdateAsync`/`ReactivateAsync` methods as they are.

- [ ] **Step 5: Route the list + get endpoints through the fold**

In `InventoryEndpoints.cs`:
- `GetItem` already calls `service.GetAsync` → now folded. No change.
- `ListItems` currently takes `IItemStore store` and calls `store.GetByClientPagedAsync`. Change it to take `InventoryService service` and call `service.GetPagedAsync` (this is the FA `ListAssets`-bypass guard — the list MUST fold):

```csharp
    private static async Task<IResult> ListItems(
        Guid clientId, int? skip, int? limit, string? order, bool? includeInactive,
        InventoryService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

        PagedResponse<Item> page = await service.GetPagedAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeInactive ?? false, cancellationToken);

        return Results.Ok(new PagedResponse<ItemView>(
            page.Items.Select(i => new ItemView(i)).ToList(), page.Total, page.Skip, page.Limit));
    }
```

- [ ] **Step 6: Drop the stored has-stock read from the store**

In `DocumentItemStore.DeactivateAsync`, remove the `if (existing.Body.OnHandQuantity != 0m) return DeactivateResult.HasStock;` line — the service guards from the projection now. (The `OnHandQuantity` field is still on `ItemDocument` at this point; it is simply no longer read here.)

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS. Fix fallout: any test asserting a deactivate `HasStock` via the store now needs a posted movement to make the projection non-zero (or asserts via the endpoint/service); `InventoryEndpointsTests` list assertions read folded values (approve entries in the E2E path). Update those tests to drive the fold.

- [ ] **Step 8: Commit**

```bash
git add Modules/Inventory/
git commit -m "feat(inventory): reads + record path fold the ledger; deactivate guard from projection (T5)"
```

---

## Task 6: Delete the materialized state

Remove the stored valuation fields and the manual void-restore now that nothing reads them. `Item` (read model) keeps `OnHandQuantity`/`TotalValue`; only `ItemDocument` and the movement bodies lose their materialized fields.

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory/ItemDocument.cs` (drop `OnHandQuantity`/`TotalValue`)
- Modify: `Modules/Inventory/Accounting101.Inventory/DocumentItemStore.cs` (stop stamping; delete `SetValuationAsync`)
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryPorts.cs` (remove `SetValuationAsync` from `IItemStore`)
- Modify: `Modules/Inventory/Accounting101.Inventory/StockMovement.cs` (drop `ResultingOnHand`/`ResultingTotalValue` from body + view; delete `SignedValueEffect`; **keep `SignedQuantityEffect`**)
- Modify: `Modules/Inventory/Accounting101.Inventory/DocumentStockMovementStore.cs` (`Map` no longer sets `Resulting*`)
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs` (drop `SetValuationAsync` calls; strip the manual valuation-restore from `VoidAsync`; stop passing `Resulting*` into the body)
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryValuation.cs` (`MovementEffect` keeps `ResultingOnHand`/`ResultingTotalValue` for the block-negative/round math — do NOT delete those; they are compute-time, not persisted)
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs` (`InMemoryItemStore`: drop `SetValuationAsync`, `CreateAsync` stops setting valuation; `InMemoryStockMovementStore.RecordAsync`/`Map` drop `Resulting*`)

**Interfaces:**
- Produces: `StockMovementBody(Guid ItemId, MovementType Type, DateOnly EffectiveDate, string? Memo, decimal Quantity, decimal AppliedUnitCost, decimal ExtendedCost)` — no `Resulting*`. `StockMovement` keeps `SignedQuantityEffect`, loses `SignedValueEffect`. `IItemStore` loses `SetValuationAsync`. `ItemDocument(string Sku, string Name, string? Description, string UnitOfMeasure)` — no valuation fields.

- [ ] **Step 1: Write the failing test (void auto-rolls-back with no manual restore)**

Add to `MovementVoidE2eTests.cs` (or the movement-service unit tests) a check that after void the item folds back to its prior on-hand/value with no `SetValuationAsync` in the path:

```csharp
[Fact]
public async Task Void_returns_on_hand_and_value_via_the_ledger_with_no_stored_restore()
{
    (InventoryMovementService svc, InMemoryItemStore items, InMemoryStockMovementStore movements, FakeLedgerClient ledger, _) = Build();
    var valuation = new ItemValuationService(movements, new FixedInventoryAccountsProvider(), ledger); // or reuse Build()'s
    Guid clientId = Guid.NewGuid();
    Item item = await items.CreateAsync(clientId, new ItemBody("SKU1", "Widget", null, "ea"));

    await svc.RecordAsync(clientId, new RecordMovement(item.Id, MovementType.Receipt, 10m, 10m, new DateOnly(2026, 1, 1), null));
    StockMovement issue = await svc.RecordAsync(clientId, new RecordMovement(item.Id, MovementType.Issue, 4m, null, new DateOnly(2026, 1, 2), null));
    await svc.VoidAsync(clientId, issue.Id, "oops");

    ItemValuation v = await valuation.GetAsync(clientId, item.Id, includePending: true);
    Assert.Equal(10m, v.OnHand);      // back to the receipt-only state
    Assert.Equal(100m, v.TotalValue);
}
```

- [ ] **Step 2: Run test to verify it fails / compile-breaks after edits**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter MovementVoid`
Expected: FAIL — until the manual restore is removed the reversed entry AND the stored restore both move the numbers (double-count); the assertion catches the divergence. (After the deletions in Step 3-8 it passes.)

- [ ] **Step 3: Drop the movement snapshots + `SignedValueEffect`**

In `StockMovement.cs`: remove `decimal ResultingOnHand, decimal ResultingTotalValue` from `StockMovementBody`; remove the `ResultingOnHand`/`ResultingTotalValue` `required` properties from `StockMovement`; delete the `SignedValueEffect` property; **keep `SignedQuantityEffect`** (its doc-comment becomes "The signed on-hand delta this movement contributes to the quantity projection"). Update the `StockMovementBody` XML comment to drop the `Resulting*` sentences.

- [ ] **Step 4: Stop persisting/mapping the snapshots**

- In `DocumentStockMovementStore.Map`, remove the `ResultingOnHand`/`ResultingTotalValue` assignments.
- In `InventoryMovementService.RecordAsync`, the `new StockMovementBody(...)` drops the two `effect.Resulting*` arguments; remove the `await items.SetValuationAsync(...)` call (step 6) entirely.
- In `InventoryMovementService.VoidAsync`, remove the block that loads the item and calls `SetValuationAsync` with the restored values (the `restoredOnHand`/`restoredValue` computation and the `SetValuationAsync` call). Keep the entry reverse/withdraw and `movements.VoidAsync`.

- [ ] **Step 5: Delete `SetValuationAsync` from the port + stores**

- In `InventoryPorts.cs`, remove `SetValuationAsync` from `IItemStore`.
- In `DocumentItemStore.cs`, delete the `SetValuationAsync` method; change `ToDocument` to drop the valuation params; `CreateAsync`/`UpdateAsync`/`ReactivateAsync`/`Map` stop passing/reading valuation.
- In `Fakes.cs`, `InMemoryItemStore`: delete `SetValuationAsync`; `CreateAsync` drops `OnHandQuantity`/`TotalValue` initializers (the `Item` read-model still HAS the properties — they default to 0 from the store and are overlaid by the fold on read); `InMemoryStockMovementStore.RecordAsync`/`Map` drop the `Resulting*` initializers.

- [ ] **Step 6: Drop the valuation fields from `ItemDocument`**

In `ItemDocument.cs`, change the record to `ItemDocument(string Sku, string Name, string? Description, string UnitOfMeasure)` (drop `decimal OnHandQuantity, decimal TotalValue`). The global `IgnoreExtraElementsConvention` tolerates any legacy body on read; greenfield reseed regardless.

> Keep `Item.OnHandQuantity`/`Item.TotalValue` and `ItemView` exactly as they are — the read model and its JSON are unchanged; the store simply returns them as 0 and the service overlays the folds.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS — the void test folds back correctly; all movement/valuation tests green. Fix any remaining references to the deleted members (`SetValuationAsync`, `Resulting*`, `SignedValueEffect`) surfaced by the compiler.

- [ ] **Step 8: Commit**

```bash
git add Modules/Inventory/
git commit -m "refactor(inventory): delete stored on-hand/value + Resulting* snapshots + manual void restore (T6)"
```

---

## Task 7: Ledger-first proof suite + Angular movement-view cleanup + reconciliation

Grow the proof suite to the full ledger-first matrix, remove the now-absent `Resulting*` from the Angular movement model, and confirm the whole solution is green.

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/InventoryLedgerFirstProofTests.cs` (grow)
- Modify: `UI/Angular/src/app/core/inventory/inventory.ts` (drop `resultingOnHand`/`resultingTotalValue` from `StockMovement`)
- Modify: any Angular component/template binding those two fields (grep first)
- Test: Angular unit suite

- [ ] **Step 1: Grow the E2E proof suite**

Add to `InventoryLedgerFirstProofTests.cs` (real engine host, so it proves the posted-vs-pending gate the fake only simulates). Use the E2E chart-setup helper and an approver identity to move entries to Posted. Cover:

```
- Receipt then Issue: item on-hand + value fold to the expected weighted-average after approval.
- Reads are posted-only: a just-recorded (unapproved) movement leaves GET /items/{id} on-hand 0; after approval it reflects.
- Block-negative: an Issue exceeding available on-books quantity → 409.
- Void auto-rollback: voiding the latest movement returns the item's folded on-hand + value to prior, and the movement drops out of the fold.
- Inventory-scoped guard: a raw GL reverse of a movement entry (no module credential) → 409.
```

Write these as real E2E tests mirroring `MovementReceiptE2eTests`/`MovementVoidE2eTests` structure (post via `/clients/{id}/movements`, approve via the engine's approve endpoint, read via `/clients/{id}/items/{id}` and assert `ItemView.item.onHandQuantity`/`averageUnitCost`). Each is one `[Fact]`.

- [ ] **Step 2: Run the backend suite**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS — full proof matrix green.

- [ ] **Step 3: Remove the snapshot fields from the Angular model**

In `UI/Angular/src/app/core/inventory/inventory.ts`, change the `StockMovement` interface to drop the two fields:

```typescript
export interface StockMovement {
  id: string; number: string | null; itemId: string; type: MovementType;
  effectiveDate: string; memo: string | null; quantity: number;
  appliedUnitCost: number; extendedCost: number; status: MovementStatus;
}
```

- [ ] **Step 4: Remove template/component usages**

Run: `grep -rn "resultingOnHand\|resultingTotalValue" UI/Angular/src`
For each hit in a component/template (e.g. a movement-detail or movement-list column), remove that binding/column. If a spec asserts those fields, drop the assertion.

- [ ] **Step 5: Run the Angular suite**

Run: `cd UI/Angular && npm test -- --watch=false`
Expected: PASS — no references to the removed fields; movement screens render without the two columns.

- [ ] **Step 6: Whole-solution reconciliation**

Run: `dotnet test` (whole solution)
Expected: PASS — all projects green (target ≥ the current 1137 + the new Inventory tests).

- [ ] **Step 7: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory.Tests/InventoryLedgerFirstProofTests.cs UI/Angular/src/app/core/inventory/
git commit -m "test(inventory): ledger-first proof suite + Angular movement-view cleanup; whole-solution green (T7)"
```

---

## Self-Review

**Spec coverage:**
- §2.1 value = {Item} fold, positive → T2 (dimension) + T4 (fold, no negation). ✓
- §2.2 only Inventory line dimensioned → T2 (counter-lines un-dimensioned) + test. ✓
- §2.3 quantity = signed-quantity projection, entry-gated → T4 (`ProjectQuantityAsync` + `OnBooks`). ✓
- §2.4 shared gate, writes-pending/reads-posted → T4 (includePending param, same gate both folds) + T5 (record uses pending; reads use posted) + T7 (E2E gate). ✓
- §2.5 frozen `AppliedUnitCost`/`ExtendedCost` → kept on the body (T6 drops only `Resulting*`). ✓
- §2.6 `Resulting*` deleted → T6. ✓
- §2.7 void auto-rollback, `SetValuationAsync`/`SignedValueEffect` deleted, `SignedQuantityEffect` kept → T6 + refinement noted in Global Constraints. ✓
- §2.8 doc-first ordering → already in `RecordAsync`; unchanged; noted. ✓ (T5 keeps persist-before-post.)
- §2.9 greenfield, RequiredDimensions=["Item"] → T3. ✓
- §4 read-model keeps fields, `ItemDocument` drops them, seam methods added → T1 + T5 + T6. ✓
- §5 page fold ≈ 2-3 calls, ListItems folds → T4 (`GetAllByItemAsync`, batch entries) + T5 (`GetPagedAsync`, endpoint routed). ✓
- §7 proof suite → T7. ✓
- §8 sequence → T1..T7 map to steps 1..6 (+ UI). ✓
- §10 list-endpoint bypass guarded → T5 Step 5. ✓

**Placeholder scan:** No TBD/TODO. T3 and T7 Step-1/Step-4 reference "grep the test project / grep UI" for exact locations rather than inline code — this is deliberate (the shared E2E chart-setup helper and the Angular template bindings vary and must be located), and each gives the exact edit to make. Acceptable per "find the sibling and follow it."

**Type consistency:** `GetSubledgerAsync`/`GetEntriesBySourceRefsAsync` signatures identical across ILedgerClient/HttpLedgerClient/Fake (T1). `Compose(... Guid itemId ...)` new param used consistently in T2 call site and T4 test helper. `ItemValuation(OnHand, TotalValue)` + `AverageUnitCost` consistent T4→T5→T6. `GetAllByItemAsync` consistent port/store/fake (T4). `SignedQuantityEffect` kept, `SignedValueEffect` removed — consistent T6. `ItemView(Item)` unchanged throughout.

> **Note on the spec refinement:** spec §2.7/§4 say delete `SignedQuantityEffect`/`SignedValueEffect` together. The plan keeps `SignedQuantityEffect` (it is the projection's per-movement signed-quantity input — a pure `Type`+`Quantity` derivation, not stored state, not a drift source) and deletes only `SignedValueEffect`. Flagged in Global Constraints; worth a one-line spec touch-up.
