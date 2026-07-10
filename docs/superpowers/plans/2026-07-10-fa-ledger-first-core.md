# Fixed Assets Ledger-First (Accumulated Depreciation Fold) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make per-asset accumulated depreciation a `{Asset}`-dimensioned ledger fold instead of a materialized field on the asset doc, so the fixed-asset register reconciles to the GL by construction.

**Architecture:** Mirror the AR core: depreciation/disposal post per-asset `{Asset}`-dimensioned Accumulated Depreciation lines; the Accumulated Depreciation account requires `{Asset}`; `Asset.AccumulatedDepreciation` stops being stored and is derived on read by folding that account (negated — it's a contra-asset). Reuses the engine's shipped dimensions + `includePending` subledger fold — **no engine change**.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB (EphemeralMongo for tests), xUnit.

**Template to read:** the shipped AR core is the canonical mirror for the fold mechanics. Implementers SHOULD read these as they work:
- `Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs` (the `GetSubledgerAsync` method + XML doc to copy)
- `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs` (`GetSubledgerAsync` impl to copy)
- `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs` (the `FakeLedgerClient` fold: `_linesById` tracking on post, the reversal line-flip, and the `GetSubledgerAsync` fold — copy this pattern)
- `Modules/Receivables/Accounting101.Receivables/CustomerAccountService.cs:38-41` (the contra-account **negation** pattern: `.Sum(l => -l.Balance)` — FA's accumulated depreciation uses the identical negation)

## Global Constraints

- **No engine change.** Dimensions, `RequiredDimensions`, and `AggregateSubledgerAsync(..., includePending)` are on master from AR. FA only consumes them. (spec §1, §9)
- **Dimension = `{Asset}`** on the Accumulated Depreciation account (`RequiredDimensions = ["Asset"]`). The engine rejects an untagged Accumulated Depreciation line at post (422). (spec §2.1)
- **Aggregate expense, per-asset accum only.** Only the Accumulated Depreciation *credits* carry `{Asset}`; the Depreciation Expense *debit* stays one aggregate line. A run entry is N+1 lines. (spec §2.2)
- **Per-asset accum = the fold, negated.** Accumulated Depreciation is a contra-asset (credit balance); the debit-positive fold reads negative, so `accum = −Balance` (mirror `CustomerAccountService.cs:41`). Explicit sign tests. (spec §2.3)
- **Writes see pending / reads see posted.** Compute paths (run amounts, disposal catch-up/NBV) fold `includePending: true`; report paths (asset accum/NBV on read) fold `includePending: false`. Straight-line ignores accum. (spec §2.4)
- **Disposed asset reads accum = 0** (disposal clears the fold); `finalAccumulated`/NBV/gain-loss stay on the `Disposal` doc. (spec §2.5)
- **Void auto-rolls-back via the ledger.** Reversing the run/disposal entry corrects the fold; the manual field-rollback methods are deleted; `MarkDisposedAsync`/`ReinstateAsync` keep only the `Status` flip. (spec §2.6)
- **Scope = accumulated depreciation only.** `AcquisitionCost` stays a frozen input (FA never books acquisition). (spec §1, §9)
- **Greenfield / reseed**, no backfill. (spec §2.7)
- Every commit builds and is green; test output pristine.

**Key signatures (verified):**
- `PostLineRequest(Guid AccountId, string Direction, decimal Amount, IReadOnlyDictionary<string, Guid>? Dimensions = null)` — a dimensioned line is `new PostLineRequest(accId, "Credit", amt, new Dictionary<string, Guid> { ["Asset"] = assetId })`.
- `AccountRequest { ... IReadOnlyList<string>? RequiredDimensions { get; init; } ... }`.
- `SubledgerLineResponse(Guid AccountId, Guid DimensionValue, decimal Balance)` (debit-positive).
- Module `ILedgerClient.GetSubledgerAsync(Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken ct = default, bool includePending = false)` → `Task<IReadOnlyList<SubledgerLineResponse>>`.

---

## File map

**Module production (modify):**
- `Modules/FixedAssets/Accounting101.FixedAssets/ILedgerClient.cs` — add `GetSubledgerAsync` (T1).
- `Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs` — implement `GetSubledgerAsync` (T1).
- `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPosting.cs` — per-asset dimensioned run credits (T2).
- `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalPosting.cs` — `{Asset}` on the disposal accum debit (T2).
- `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsService.cs` — report-path fold + populate accum (T4).
- `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsRunService.cs` — compute reads pending fold; drop apply/reverse-depreciation (T4/T5).
- `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalService.cs` — compute reads pending fold; drop reinstate-accum (T4/T5).
- `Modules/FixedAssets/Accounting101.FixedAssets/Asset.cs` — `AccumulatedDepreciation` becomes non-required populated field (T5).
- `Modules/FixedAssets/Accounting101.FixedAssets/AssetDocument.cs` — drop `AccumulatedDepreciation` (T5).
- `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPorts.cs` — drop apply/reverse-depreciation; simplify `MarkDisposedAsync`/`DisposeStamp`/`ReinstateAsync` (T5).
- `Modules/FixedAssets/Accounting101.FixedAssets/DocumentAssetStore.cs` — drop field stamping + mutators (T5).
- `Modules/FixedAssets/Accounting101.FixedAssets.Api/ConfiguredFixedAssetsAccountsProvider.cs` — unchanged (config is chart-side).

**Tests (modify/create):** the 14 FA test files touch `AccumulatedDepreciation`/entry-shape; each task updates the ones it breaks (named per task). `Fakes.cs` is enhanced in T1.

---

## Task 1: Add `GetSubledgerAsync` to the FA ledger client + enhance the fake to fold

**Files:**
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/ILedgerClient.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FakeLedgerFoldTests.cs` (create)

**Interfaces:**
- Produces: `ILedgerClient.GetSubledgerAsync(Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken ct = default, bool includePending = false)` → `Task<IReadOnlyList<SubledgerLineResponse>>`.

**Read first:** the AR `ILedgerClient.GetSubledgerAsync` + its `HttpLedgerClient` impl + the AR `FakeLedgerClient` fold (named in "Template to read"). This task is a verbatim port of those to FA (the only adaptations: namespace `Accounting101.FixedAssets`, and FA's `HttpLedgerClient` uses `response.EnsureSuccessStatusCode()` — matching its existing methods — not AR's `EnsureSuccessAsync`).

- [ ] **Step 1: Add the interface method**

In `ILedgerClient.cs`, after `GetEntriesBySourceRefAsync`, add (copy AR's XML doc verbatim):

```csharp
    /// <summary>Read a per-dimension control-account fold: the signed (debit-positive) balance of
    /// <paramref name="account"/> grouped by the value of dimension <paramref name="dimension"/>
    /// (e.g. "Asset"). This is how ledger-first read paths derive balances.
    /// <para>
    /// <paramref name="includePending"/> (default false) keeps the fold Posted-only, matching what is
    /// actually on the books — the correct semantics for every read (asset accumulated depreciation, NBV).
    /// Pass <c>true</c> only from a write-path compute that must include a not-yet-approved run (declining-
    /// balance basing the next period, disposal catch-up); never from a read.
    /// </para></summary>
    Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false);
```

- [ ] **Step 2: Implement it in `HttpLedgerClient`**

In `HttpLedgerClient.cs`, after `GetEntriesBySourceRefAsync`, add:

```csharp
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
        return (await response.Content.ReadFromJsonAsync<List<SubledgerLineResponse>>(cancellationToken))!;
    }
```

- [ ] **Step 3: Enhance the fake to record posted lines and fold them**

In `Fakes.cs`, `FakeLedgerClient` currently records entries with empty lines. Port the AR fake's fold: track posted lines per entry, flip a reversal's lines, and implement `GetSubledgerAsync`. Replace the class body's post/reverse/fold parts to match AR's `FakeLedgerClient` (read it), keeping FA's existing `ReversedOrWithdrawn`/`ReturnNoEntries` flags. Concretely:

Add a line-store field and record lines on post:

```csharp
    private readonly Dictionary<Guid, IReadOnlyList<PostLineRequest>> _linesById = new();
```

In `PostAsync`, after adding to `_entries`, also `_linesById[id] = entry.Lines;`.

In `ReverseAsync`, record the reversal's flipped lines so the fold nets to zero (mirror AR):

```csharp
        _linesById[id] = original.SourceRef is null ? []
            : (_linesById.TryGetValue(entryId, out var orig)
                ? orig.Select(l => new PostLineRequest(l.AccountId, l.Direction == "Debit" ? "Credit" : "Debit", l.Amount, l.Dimensions)).ToList()
                : []);
```

Add the fold method (verbatim from AR's `FakeLedgerClient.GetSubledgerAsync`, which counts every Active entry regardless of approval in both modes — sufficient for FA unit tests; the real approval-gated fold is proven by HTTP E2E):

```csharp
    public Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false)
    {
        Dictionary<Guid, decimal> totals = new();
        foreach ((Guid id, EntryResponse response) in _entries)
        {
            if (response.Status != "Active") continue;
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
```

- [ ] **Step 4: Write the fake-fold test**

Create `FakeLedgerFoldTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FakeLedgerFoldTests
{
    [Fact]
    public async Task Fold_groups_dimensioned_lines_by_asset_and_signs_credit_negative()
    {
        var fake = new FakeLedgerClient();
        Guid client = Guid.NewGuid(), accum = Guid.NewGuid(), expense = Guid.NewGuid();
        Guid assetA = Guid.NewGuid(), assetB = Guid.NewGuid();

        await fake.PostAsync(client, new PostEntryRequest(
            null, new DateOnly(2026, 6, 30), null, null,
            [
                new PostLineRequest(expense, "Debit", 300m),
                new PostLineRequest(accum, "Credit", 200m, new Dictionary<string, Guid> { ["Asset"] = assetA }),
                new PostLineRequest(accum, "Credit", 100m, new Dictionary<string, Guid> { ["Asset"] = assetB }),
            ],
            SourceRef: Guid.NewGuid(), SourceType: "DepreciationRun"));

        IReadOnlyList<SubledgerLineResponse> fold = await fake.GetSubledgerAsync(client, accum, "Asset", null);

        // Contra-asset: credit lines read NEGATIVE in the debit-positive fold.
        Assert.Equal(-200m, fold.Single(l => l.DimensionValue == assetA).Balance);
        Assert.Equal(-100m, fold.Single(l => l.DimensionValue == assetB).Balance);
    }
}
```

- [ ] **Step 5: Run + verify**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FakeLedgerFoldTests`
Expected: PASS (1 test). Then run the whole FA project to confirm no regression from the fake changes:
Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS (all existing tests still green — the fake's post/reverse behavior is unchanged for existing assertions).

- [ ] **Step 6: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/ILedgerClient.cs Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs Modules/FixedAssets/Accounting101.FixedAssets.Tests/FakeLedgerFoldTests.cs
git commit -m "feat(fixedassets): GetSubledgerAsync fold read + fake fold enhancement"
```

---

## Task 2: Dimension the recipes per-asset (additive; field still written)

**Files:**
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPosting.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsRunService.cs` (pass lines to the recipe)
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalPosting.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalService.cs` (pass assetId to the recipe)
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsPostingTests.cs`, `FixedAssetsDisposalPostingTests.cs`, and any run/disposal service/E2E test asserting the old line shape.

**Interfaces:**
- Produces: `FixedAssetsPosting.ComposeDepreciationRun(Guid runId, IReadOnlyList<DepreciationRunLine> lines, decimal total, DateOnly effectiveDate, string? memo, FixedAssetsPostingAccounts accounts)` — one `Dr Depreciation Expense total` + one `Cr Accumulated Depreciation {Asset=line.AssetId} line.Amount` per line.
- `FixedAssetsDisposalPosting.ComposeDisposal(...)` gains a `Guid assetId` parameter and puts `{Asset=assetId}` on the accumulated-depreciation debit line.

- [ ] **Step 1: Rewrite `ComposeDepreciationRun` to per-asset credits (failing test first)**

Update `FixedAssetsPostingTests.cs` — the run entry now has `1 + N` lines. Replace the current 2-line assertion with (adapt the test's asset ids/amounts to whatever the test already sets up; the shape assertion is what changes):

```csharp
[Fact]
public void Depreciation_run_posts_aggregate_expense_debit_and_per_asset_accum_credits()
{
    var accounts = TestAccounts(); // existing helper in this file
    Guid a1 = Guid.NewGuid(), a2 = Guid.NewGuid();
    var lines = new List<DepreciationRunLine> { new(a1, 200m), new(a2, 100m) };

    PostEntryRequest entry = FixedAssetsPosting.ComposeDepreciationRun(
        Guid.NewGuid(), lines, 300m, new DateOnly(2026, 6, 30), null, accounts);

    Assert.Equal(3, entry.Lines.Count); // 1 expense debit + 2 asset credits
    PostLineRequest expense = Assert.Single(entry.Lines, l => l.AccountId == accounts.DepreciationExpenseAccountId);
    Assert.Equal("Debit", expense.Direction);
    Assert.Equal(300m, expense.Amount);
    Assert.Null(expense.Dimensions); // expense is aggregate, not per-asset

    PostLineRequest c1 = Assert.Single(entry.Lines, l => l.AccountId == accounts.AccumulatedDepreciationAccountId && l.Dimensions!["Asset"] == a1);
    Assert.Equal("Credit", c1.Direction);
    Assert.Equal(200m, c1.Amount);
    PostLineRequest c2 = Assert.Single(entry.Lines, l => l.AccountId == accounts.AccumulatedDepreciationAccountId && l.Dimensions!["Asset"] == a2);
    Assert.Equal(100m, c2.Amount);

    decimal debits = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
    decimal credits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
    Assert.Equal(debits, credits);
}
```

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter Depreciation_run_posts_aggregate_expense_debit`
Expected: FAIL to compile (`ComposeDepreciationRun` signature differs).

- [ ] **Step 2: Implement the new `ComposeDepreciationRun`**

Replace the method in `FixedAssetsPosting.cs`:

```csharp
    public static PostEntryRequest ComposeDepreciationRun(
        Guid runId, IReadOnlyList<DepreciationRunLine> lines, decimal total, DateOnly effectiveDate, string? memo, FixedAssetsPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(accounts);
        if (total <= 0m)
            throw new ArgumentException("Depreciation run total must be positive.", nameof(total));

        List<PostLineRequest> entryLines =
            [new(accounts.DepreciationExpenseAccountId, "Debit", total)];
        entryLines.AddRange(lines.Select(l =>
            new PostLineRequest(accounts.AccumulatedDepreciationAccountId, "Credit", l.Amount,
                new Dictionary<string, Guid> { ["Asset"] = l.AssetId })));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(DepreciationRunSourceType, runId),
            EffectiveDate: effectiveDate,
            Reference: null,
            Memo: memo,
            Lines: entryLines,
            SourceRef: runId,
            SourceType: DepreciationRunSourceType);
    }
```

In `FixedAssetsRunService.RunDepreciationAsync` step 7, pass `lines`:

```csharp
        PostEntryRequest entry = FixedAssetsPosting.ComposeDepreciationRun(run.Id, lines, total, effectiveDate, request.Memo, postingAccounts);
```

- [ ] **Step 3: Add `{Asset}` to the disposal accum debit (failing test first)**

Update `FixedAssetsDisposalPostingTests.cs` — the accumulated-depreciation debit line now carries `{Asset}`. Add the `assetId` argument to the test's `ComposeDisposal` call and assert the dimension:

```csharp
Guid assetId = Guid.NewGuid();
PostEntryRequest entry = FixedAssetsDisposalPosting.ComposeDisposal(
    Guid.NewGuid(), new DateOnly(2026, 6, 30), assetId, acquisitionCost, currentAccumulated, catchUp, proceeds, gainLoss, null, accounts);
PostLineRequest accum = Assert.Single(entry.Lines, l => l.AccountId == accounts.AccumulatedDepreciationAccountId);
Assert.Equal(assetId, accum.Dimensions!["Asset"]);
```

Run the disposal posting test — expected FAIL to compile (signature differs).

- [ ] **Step 4: Implement the disposal recipe change**

In `FixedAssetsDisposalPosting.cs`, add `Guid assetId` after `disposalDate` and dimension the accum debit:

```csharp
    public static PostEntryRequest ComposeDisposal(
        Guid disposalId, DateOnly disposalDate, Guid assetId, decimal acquisitionCost, decimal currentAccumulated,
        decimal catchUp, decimal proceeds, decimal gainLoss, string? memo, FixedAssetsPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (acquisitionCost <= 0m)
            throw new ArgumentException("Acquisition cost must be positive.", nameof(acquisitionCost));

        List<PostLineRequest> lines = [];
        if (catchUp > 0m) lines.Add(new(accounts.DepreciationExpenseAccountId, "Debit", catchUp));
        if (currentAccumulated > 0m) lines.Add(new(accounts.AccumulatedDepreciationAccountId, "Debit", currentAccumulated,
            new Dictionary<string, Guid> { ["Asset"] = assetId }));
        if (proceeds > 0m) lines.Add(new(accounts.DisposalProceedsAccountId, "Debit", proceeds));
        lines.Add(new(accounts.AssetCostAccountId, "Credit", acquisitionCost));
        if (gainLoss > 0m) lines.Add(new(accounts.GainOnDisposalAccountId, "Credit", gainLoss));
        else if (gainLoss < 0m) lines.Add(new(accounts.LossOnDisposalAccountId, "Debit", -gainLoss));

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(DisposalSourceType, disposalId),
            EffectiveDate: disposalDate,
            Reference: null,
            Memo: memo,
            Lines: lines,
            SourceRef: disposalId,
            SourceType: DisposalSourceType);
    }
```

In `FixedAssetsDisposalService.DisposeAsync` step 6, pass `assetId`:

```csharp
        PostEntryRequest entry = FixedAssetsDisposalPosting.ComposeDisposal(
            disposal.Id, request.DisposalDate, assetId, asset.AcquisitionCost, currentAccumulated, catchUp, request.Proceeds, gainLoss, request.Memo, postingAccounts);
```

- [ ] **Step 5: Fix the other tests that assert the old entry shape, then run the project**

Update the run/disposal service + E2E tests that assert the depreciation entry's line count or the aggregate accum line (they now see N+1 lines / dimensioned lines): `FixedAssetsRunServiceTests.cs`, `FixedAssetsRunServiceFa3Tests.cs`, `DepreciationRunE2eTests.cs`, `DisposalE2eTests.cs`, `FixedAssetsDisposalServiceTests.cs`. For each entry-shape assertion, assert the aggregate expense debit + the per-asset `{Asset}` accum credits (mirror Step 1's shape). The **field-mutation assertions are unchanged** (the field is still written this task).

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS (all green — recipes dimensioned, field still written so accum assertions unchanged).

- [ ] **Step 6: Commit**

```bash
git add Modules/FixedAssets/
git commit -m "feat(fixedassets): dimension depreciation + disposal accum lines per {Asset} (additive)"
```

---

## Task 3: Require `{Asset}` on the Accumulated Depreciation account

**Files:**
- Modify: the E2E chart-setup helpers that PUT the Accumulated Depreciation account: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunE2eTests.cs`, `DisposalE2eTests.cs` (and any shared chart helper in `FixedAssetsHostFixture.cs`).
- Modify: the dev seed / start config note (documented, greenfield — no code seed in the module).

**Interfaces:**
- Produces: the Accumulated Depreciation account is created with `RequiredDimensions = ["Asset"]`; the engine rejects an untagged Accumulated Depreciation line (422). Because Task 2 already dimensions every accum line, all posts still succeed.

- [ ] **Step 1: Add the dimension requirement to the E2E chart setup**

In each E2E test's chart-setup (where the Accumulated Depreciation account is PUT via `AccountRequest`), set `RequiredDimensions = ["Asset"]` on that account only. Example (adapt to the existing helper — the Accumulated Depreciation account is the one at `AccumulatedDepreciationAccountId`):

```csharp
await PutAccountAsync(controller, clientId, fixture.AccumulatedDepreciationAccountId,
    "1590", "Accumulated Depreciation", "Asset", requiredDimensions: ["Asset"]);
```

If the existing `PutAccountAsync` helper only takes a single `requiredDimension`, add a `requiredDimensions` parameter that sets `AccountRequest.RequiredDimensions`:

```csharp
private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
    string number, string name, string type, string? requiredDimension = null, IReadOnlyList<string>? requiredDimensions = null)
{
    (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
        new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension, RequiredDimensions = requiredDimensions }))
        .EnsureSuccessStatusCode();
}
```

- [ ] **Step 2: Add an "untagged accum line is rejected" proof**

In `DepreciationRunE2eTests.cs`, add a test that posting an Accumulated Depreciation line WITHOUT `{Asset}` (a raw manual entry to that account) is rejected 422 — proving the requirement is live:

```csharp
[Fact]
public async Task Accumulated_depreciation_account_rejects_an_untagged_line()
{
    (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
    await SetUpFixedAssetsChartAsync(controller, clientId); // sets RequiredDimensions=["Asset"] on accum

    // A raw balanced entry crediting Accumulated Depreciation with NO Asset dimension → 422.
    var entry = new
    {
        effectiveDate = "2026-06-30",
        lines = new object[]
        {
            new { accountId = fixture.DepreciationExpenseAccountId, direction = "Debit",  amount = 100m },
            new { accountId = fixture.AccumulatedDepreciationAccountId, direction = "Credit", amount = 100m },
        },
    };
    HttpResponseMessage res = await clerk.PostAsJsonAsync($"/clients/{clientId}/entries", entry);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
}
```

- [ ] **Step 3: Run + verify**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS — depreciation/disposal E2Es still post (their accum lines are dimensioned from Task 2); the new untagged-line test returns 422.

- [ ] **Step 4: Commit**

```bash
git add Modules/FixedAssets/
git commit -m "feat(fixedassets): require {Asset} on the Accumulated Depreciation account"
```

**Note for deployment (spec §2.7):** the dev/demo chart must configure the Accumulated Depreciation account with `RequiredDimensions = ["Asset"]` (greenfield/reseed; no backfill). Record this in the module's smoke note alongside AR's `{Customer,Invoice}` / AP's `{Vendor,Bill}` requirements.

---

## Task 4: Fold accumulated depreciation on read + compute (field still written)

**Files:**
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsService.cs` — report-path fold (posted-only, negated) populates `Asset.AccumulatedDepreciation` on `GetAsync`/list. Inject `ILedgerClient` + `IFixedAssetsAccountsProvider`.
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsRunService.cs` — compute path folds pending-inclusive per candidate asset before the declining-balance method.
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalService.cs` — `currentAccumulated` reads the pending-inclusive fold.
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsRunServiceTests.cs`, `FixedAssetsDisposalServiceTests.cs`, and a new `AccumulatedDepreciationFoldTests.cs`.

**Interfaces:**
- Consumes: `GetSubledgerAsync` (T1); the `{Asset}`-dimensioned lines (T2). Uses the negation `accum = −Balance` (mirror `CustomerAccountService.cs:41`).
- Produces: a private helper `FoldAccumAsync(clientId, includePending, ct)` on each service returning `Dictionary<Guid, decimal>` (assetId → accumulated, negated), and `Asset.AccumulatedDepreciation` populated from it on the report path.

- [ ] **Step 1: Write the fold-on-read test (failing)**

Create `AccumulatedDepreciationFoldTests.cs` (unit test against the run service + fake — asserts the reported accum comes from the ledger, negated, not the stored field):

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class AccumulatedDepreciationFoldTests
{
    // Uses the same in-memory harness the run-service tests use; see FixedAssetsRunServiceTests for BuildHarness.
    [Fact]
    public async Task Reported_asset_accumulated_depreciation_is_the_negated_ledger_fold()
    {
        var h = FaHarness.Build();               // shared harness factory (see Step 4)
        Guid client = Guid.NewGuid();
        Asset asset = await h.Assets.CreateAsync(client, StraightLine(cost: 12_000m, life: 12, salvage: 0m, new DateOnly(2026, 1, 1)));

        // One month depreciation run posts a {Asset} credit of 1000 to Accumulated Depreciation.
        await h.RunService.RunDepreciationAsync(client, new DepreciationRunRequest(2026, 1, null, null));

        // The service reports accumulated depreciation folded from the ledger (negated contra-asset), = 1000.
        Asset? read = await h.AssetService.GetAsync(client, asset.Id);
        Assert.Equal(1_000m, read!.AccumulatedDepreciation);
    }
}
```

(If a shared `FaHarness`/`StraightLine` helper does not exist, add a small internal one in the test project mirroring `FixedAssetsRunServiceTests`' existing `BuildHarness`; the point is the service reads the fold.)

Run the test — expected FAIL (the service returns the stored field, which the fake asset store also tracks; make the assertion meaningful by having the fold be the source — see Step 2/3).

- [ ] **Step 2: Fold on the report path in `FixedAssetsService`**

Inject the ledger client + accounts provider and populate accum from the posted-only, negated fold. Replace `GetAsync` and add the same overlay to the paged list:

```csharp
public sealed class FixedAssetsService(
    IAssetStore store, ILedgerClient ledger, IFixedAssetsAccountsProvider accounts)
{
    // ...Create/Update/Deactivate/Reactivate unchanged (no ledger dependency)...

    public async Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        Asset? asset = await store.GetAsync(clientId, assetId, ct);
        if (asset is null) return null;
        decimal accum = await FoldAccumForAssetAsync(clientId, assetId, includePending: false, ct);
        return asset with { AccumulatedDepreciation = accum };
    }

    public async Task<PagedResponse<Asset>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default)
    {
        PagedResponse<Asset> page = await store.GetByClientPagedAsync(clientId, skip, limit, descending, includeInactive, ct);
        Dictionary<Guid, decimal> accum = await FoldAccumAsync(clientId, includePending: false, ct);
        List<Asset> overlaid = page.Items.Select(a => a with { AccumulatedDepreciation = accum.GetValueOrDefault(a.Id) }).ToList();
        return new PagedResponse<Asset>(overlaid, page.Total, page.Skip, page.Limit);
    }

    // accum = −Balance (Accumulated Depreciation is a contra-asset; the debit-positive fold reads credits negative).
    private async Task<Dictionary<Guid, decimal>> FoldAccumAsync(Guid clientId, bool includePending, CancellationToken ct)
    {
        FixedAssetsPostingAccounts acc = await accounts.GetAccountsAsync(clientId, ct);
        IReadOnlyList<SubledgerLineResponse> fold =
            await ledger.GetSubledgerAsync(clientId, acc.AccumulatedDepreciationAccountId, "Asset", null, ct, includePending);
        return fold.ToDictionary(l => l.DimensionValue, l => -l.Balance);
    }

    private async Task<decimal> FoldAccumForAssetAsync(Guid clientId, Guid assetId, bool includePending, CancellationToken ct)
    {
        FixedAssetsPostingAccounts acc = await accounts.GetAccountsAsync(clientId, ct);
        return (await ledger.GetSubledgerAsync(clientId, acc.AccumulatedDepreciationAccountId, "Asset", null, ct, includePending))
            .Where(l => l.DimensionValue == assetId).Sum(l => -l.Balance);
    }
}
```

Update `FixedAssetsServiceExtensions` DI so `FixedAssetsService` receives `ILedgerClient` + `IFixedAssetsAccountsProvider` (both already registered for the run/disposal services).

- [ ] **Step 3: Fold pending-inclusive on the compute paths**

In `FixedAssetsRunService.RunDepreciationAsync`, populate each candidate asset's accum from the pending-inclusive fold before the depreciation method reads it. Replace the enumeration in step 2:

```csharp
        Dictionary<Guid, decimal> accum = await FoldAccumAsync(clientId, ct); // pending-inclusive
        List<DepreciationRunLine> lines = [];
        foreach (Asset stored in await ActiveAssetsAsync(clientId, ct))
        {
            if (stored.Status != AssetStatus.Active) continue;
            if (!period.OnOrAfterServiceMonth(stored.InServiceDate)) continue;
            Asset asset = stored with { AccumulatedDepreciation = accum.GetValueOrDefault(stored.Id) };
            decimal amount = methods.For(asset.Method).DepreciationForPeriod(asset);
            if (amount > 0m) lines.Add(new DepreciationRunLine(asset.Id, amount));
        }
```

Add the helper to the run service:

```csharp
    private async Task<Dictionary<Guid, decimal>> FoldAccumAsync(Guid clientId, CancellationToken ct)
    {
        FixedAssetsPostingAccounts acc = await accounts.GetAccountsAsync(clientId, ct);
        return (await ledger.GetSubledgerAsync(clientId, acc.AccumulatedDepreciationAccountId, "Asset", null, ct, includePending: true))
            .ToDictionary(l => l.DimensionValue, l => -l.Balance);
    }
```

In `FixedAssetsDisposalService.DisposeAsync`, replace `decimal currentAccumulated = asset.AccumulatedDepreciation;` with the pending-inclusive fold for that asset:

```csharp
        decimal currentAccumulated = (await ledger.GetSubledgerAsync(clientId, postingAccounts.AccumulatedDepreciationAccountId, "Asset", null, ct, includePending: true))
            .Where(l => l.DimensionValue == assetId).Sum(l => -l.Balance);
```

(The `postingAccounts` are already resolved in step 2 of `DisposeAsync`.)

- [ ] **Step 4: Run + verify**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS. Update any service test that asserted accum via the stored field to read it via the service (now the fold). The stored field is still written by the store, so store-level tests remain green; service-level reads now come from the fold.

- [ ] **Step 5: Commit**

```bash
git add Modules/FixedAssets/
git commit -m "feat(fixedassets): fold accumulated depreciation on read + compute (negated contra-asset)"
```

---

## Task 5: Delete the stored field, its mutators, and the manual void rollback

**Files:**
- Modify: `Asset.cs`, `AssetDocument.cs`, `FixedAssetsPorts.cs`, `DocumentAssetStore.cs`, `FixedAssetsRunService.cs`, `FixedAssetsDisposalService.cs`, and the test doubles (`Fakes.cs` `InMemoryAssetStore`).

**Interfaces:**
- Produces: `Asset.AccumulatedDepreciation` is a non-required `decimal` (default 0, populated on read); `AssetDocument` no longer carries it; `IAssetStore` loses `ApplyDepreciationAsync`/`ReverseDepreciationAsync`; `MarkDisposedAsync(Guid, Guid, CancellationToken)` (no accum param) just flips `Status`; `DisposeStamp(DisposeOutcome, Asset?)` drops `PriorAccumulated`; `ReinstateAsync(Guid, Guid, CancellationToken)` just flips `Status`.

- [ ] **Step 1: Make `Asset.AccumulatedDepreciation` non-required + drop it from `AssetDocument`**

`Asset.cs` — change `public required decimal AccumulatedDepreciation { get; init; }` to `public decimal AccumulatedDepreciation { get; init; }` (populated on read; default 0).

`AssetDocument.cs` — remove the `AccumulatedDepreciation` field:

```csharp
public sealed record AssetDocument(
    string Description,
    decimal AcquisitionCost,
    DateOnly InServiceDate,
    int UsefulLifeMonths,
    decimal SalvageValue,
    DepreciationMethod Method,
    decimal? DecliningBalanceFactor,
    AssetStatus Status);
```

- [ ] **Step 2: Delete the store's field stamping + mutators**

`DocumentAssetStore.cs`:
- `ToDocument` drops the `accumulated` parameter; `CreateAsync`/`UpdateAsync`/`ReactivateAsync` stop passing it. `Map` drops `AccumulatedDepreciation = d.AccumulatedDepreciation` (the property defaults to 0; the service overlays the fold).
- Delete `ApplyDepreciationAsync`, `ReverseDepreciationAsync`, `AdjustAccumulatedAsync`.
- `MarkDisposedAsync` drops the `finalAccumulated` param and the accum stamp — it only sets `Status = Disposed`:

```csharp
    public async Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return new DisposeStamp(DisposeOutcome.NotFound, null);
        if (existing.Body.Status != AssetStatus.Active) return new DisposeStamp(DisposeOutcome.NotActive, null);
        AssetDocument doc = existing.Body with { Status = AssetStatus.Disposed };
        await documents.PutAsync(clientId, Collection, assetId, doc, NoTags, ct);
        return new DisposeStamp(DisposeOutcome.Disposed, Map(assetId, doc));
    }
```
- `ReinstateAsync` drops the `restoreAccumulated` param and the accum restore — it only sets `Status = Active`.

- [ ] **Step 3: Update the port contracts**

`FixedAssetsPorts.cs`:
- Delete `ApplyDepreciationAsync` + `ReverseDepreciationAsync` from `IAssetStore`.
- `MarkDisposedAsync(Guid clientId, Guid assetId, CancellationToken ct = default)` (no accum param).
- `ReinstateAsync(Guid clientId, Guid assetId, CancellationToken ct = default)` (no accum param).
- `DisposeStamp` becomes `public readonly record struct DisposeStamp(DisposeOutcome Outcome, Asset? Asset);` (drop `PriorAccumulated`).

- [ ] **Step 4: Drop the manual rollback calls from the services**

`FixedAssetsRunService`:
- `RunDepreciationAsync` deletes step 6 (`await assets.ApplyDepreciationAsync(...)`) — the dimensioned post is the accum change.
- `VoidRunAsync` deletes `await assets.ReverseDepreciationAsync(clientId, run.Lines, ct);` — the entry reversal rolls the fold back.

`FixedAssetsDisposalService`:
- `DisposeAsync` step 5 calls `MarkDisposedAsync(clientId, assetId, ct)` (no `finalAccumulated`); `finalAccumulated`/`currentAccumulated` remain computed for the `Disposal` body only.
- `VoidDisposalAsync` calls `ReinstateAsync(clientId, disposal.AssetId, ct)` (no accum) — the entry reversal restores the fold.

- [ ] **Step 5: Update the test doubles + all remaining field assertions**

`Fakes.cs` `InMemoryAssetStore`: delete `ApplyDepreciationAsync`/`ReverseDepreciationAsync`; `MarkDisposedAsync`/`ReinstateAsync` drop the accum param + field write (only flip `Status`); `CreateAsync` stops setting `AccumulatedDepreciation`. Any test asserting `asset.AccumulatedDepreciation` off the STORE must now assert it off the SERVICE (the fold) or off the evidentiary `Disposal` doc (`AccumulatedAtDisposal`). Update: `AssetLifecycleStoreTests.cs`, `AssetDocumentStoreTests.cs`, `AssetDisposalLifecycleTests.cs`, `FixedAssetsRunServiceTests.cs`, `FixedAssetsRunServiceFa3Tests.cs`, `FixedAssetsDisposalServiceTests.cs`, `DepreciationRunE2eTests.cs`, `DisposalE2eTests.cs`, `DepreciationMethodTests.cs`, `DepreciationScheduleTests.cs` (the method/schedule tests construct `Asset` with an accum value — that still works since `Asset` keeps the property; they are unaffected except for the dropped `required`).

- [ ] **Step 6: Run + verify**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS (all — the field is gone from storage; reads fold; void auto-rolls-back).

- [ ] **Step 7: Commit**

```bash
git add Modules/FixedAssets/
git commit -m "refactor(fixedassets): delete stored AccumulatedDepreciation + manual rollback — the fold is the source"
```

---

## Task 6: Proof suite + FA-scoped guard proof + reconciliation

**Files:**
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FaLedgerFirstProofTests.cs` (create), `DepreciationRunE2eTests.cs` (guard proof).

**Interfaces:**
- Consumes: the full stack through `FixedAssetsHostFixture` + the shipped module-entry guard.

- [ ] **Step 1: Fold/sign + dimensioned-entry + void-auto-rollback proofs (E2E)**

Create `FaLedgerFirstProofTests.cs` with real-host proofs (mirror the AR proof suite shape, using `FixedAssetsHostFixture.SeedSodClientAsync` + the chart setup with `{Asset}` on accum):
- Record an asset, run one month → the asset's reported accumulated depreciation equals the run amount (fold, positive after negation); a second asset with a different amount folds independently.
- The depreciation entry has one aggregate expense debit + one `{Asset}` accum credit per asset, balanced.
- **Void auto-rollback:** run the month, approve the entry, void the run → the asset's reported accumulated depreciation returns to its prior value **with no manual rollback** (the entry reversal did it). Assert via the service read (the fold), not a stored field.
- Disposal clears the fold: dispose an asset → its reported accumulated depreciation reads 0; the `Disposal` doc's `AccumulatedAtDisposal` holds the final value.

(Full test bodies follow the `DepreciationRunE2eTests`/`DisposalE2eTests` patterns already in the project — reuse their `SetUpFixedAssetsChartAsync` + amount constants; assert accum via `GET /assets/{id}` → `AssetView.Asset.AccumulatedDepreciation`.)

- [ ] **Step 2: FA-scoped guard proof**

In `DepreciationRunE2eTests.cs`, add: record + approve a depreciation run, then a RAW GL reverse of its entry via the plain user HttpClient (no module credential) → **409 Conflict** (mirror the Cash/Payroll guard proofs — controller client, approve via approver, raw reverse, assert `HttpStatusCode.Conflict`).

- [ ] **Step 3: Run the new proofs**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter "FaLedgerFirstProofTests|Raw_gl_reverse"`
Expected: PASS.

- [ ] **Step 4: Whole-solution reconciliation**

Run: `dotnet test`
Expected: PASS — entire solution green.

- [ ] **Step 5: Commit**

```bash
git add Modules/FixedAssets/
git commit -m "test(fixedassets): ledger-first proof suite (fold/sign, dimensioned entry, void-auto-rollback, guard); whole-solution green"
```

---

## Self-Review

**1. Spec coverage:**
- §1 goal (per-asset accum → ledger fold; delete field; derive on read; no engine change) → T1-T5.
- §2.1 `{Asset}` dimension + 422 on untagged → T2 (dimension) + T3 (require + 422 proof).
- §2.2 aggregate expense, per-asset accum credits → T2.
- §2.3 accum = −fold (contra-asset sign) → T1 fold test, T4 negation, T6 sign proof.
- §2.4 writes-pending / reads-posted → T4 (report posted-only; compute pending-inclusive).
- §2.5 disposed accum = 0 → T2 disposal `{Asset}` debit clears the fold; T6 disposal proof.
- §2.6 void auto-rollback; delete manual rollback → T5 (delete) + T6 (proof).
- §2.7 greenfield/reseed + Accum RequiredDimensions → T3 (+ deployment note).
- §3 grounding (GetSubledgerAsync add; field/mutators inventory) → T1/T5.
- §4 data-model deletions → T5.
- §5 read + compute folds → T4.
- §6 recipe + service changes → T2/T4/T5.
- §7 testing → T1/T3/T4/T6.
- §8 sequencing (green at every commit) → T1 additive → T2 dimension (field still written) → T3 require → T4 reads fold (field still written) → T5 delete field → T6 proof. **Never reorder.**
- §10 risks: contra-asset sign (T1/T4/T6 tests); DB pending-inclusive (T4 compute); N-line entry (T2/T6); transition safety (T4 before T5).

**2. Placeholder scan:** Production code steps carry full code. The AR-mirror pieces (T1 `GetSubledgerAsync`, fake fold) reference the exact AR source file to copy with named FA adaptations — grounded, reviewable, not a vague "similar to." Test-update steps name the exact files and the specific assertion shape that changes (entry line-count / accum-via-service); the existing per-test setup is reused rather than reproduced.

**3. Type consistency:** `GetSubledgerAsync(Guid, Guid, string, DateOnly?, CancellationToken, bool)` matches AR's contract (T1) and every call site (T4). `ComposeDepreciationRun(Guid, IReadOnlyList<DepreciationRunLine>, decimal, DateOnly, string?, FixedAssetsPostingAccounts)` (T2) matches the run-service call. `ComposeDisposal(..., Guid assetId, ...)` (T2) matches the disposal-service call. `MarkDisposedAsync(Guid, Guid, CancellationToken)` + `DisposeStamp(DisposeOutcome, Asset?)` + `ReinstateAsync(Guid, Guid, CancellationToken)` (T5) match the service call sites. `accum = −Balance` negation is consistent across T4 report/compute and the T1/T6 sign tests. `Asset.AccumulatedDepreciation` stays a property throughout (used by the depreciation methods + `DepreciationSchedule`), only its source changes (stored → folded/populated).

Note: this plan has no engine task — the dimensions, `RequiredDimensions`, and `includePending` subledger fold shipped with the AR cycle (master). FA consumes them via the `GetSubledgerAsync` client method (T1) and the `RequiredDimensions` account config (T3).
