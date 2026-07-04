# Fixed Assets FA-3 — Disposals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add asset disposals to the Fixed Assets module — retire/sell an asset, auto-catch-up depreciation to the disposal month, post one balanced gain/loss GL entry, transition the asset to `Disposed`, and support a void that reinstates it; plus fold in FA-2's deferred non-transactional-run recovery fix.

**Architecture:** Extends `Modules/FixedAssets/` (domain + `.Api` + tests). A pure catch-up schedule (`DepreciationSchedule`) and a pure disposal posting recipe (`FixedAssetsDisposalPosting`) are unit-tested in isolation. An evidentiary `disposals` document store mirrors `DocumentDepreciationRunStore`. `FixedAssetsDisposalService` orchestrates: validate → resolve accounts → compute catch-up + gain/loss → persist disposal → stamp the asset `Disposed` → post one PendingApproval entry via the existing `ILedgerClient`. Void reverses the entry (tolerating a missing one), reinstates the asset, and voids the doc. `Disposed` assets are excluded from depreciation runs and frozen against edit/deactivate/reactivate.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB via the engine document store, xUnit + EphemeralMongo (`SharedMongo`) + `WebApplicationFactory<Program>` for HTTP E2E.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-07-04-fixed-assets-fa3-disposals-design.md` — every task implements part of it.
- **Base commit:** branch off `master` at the FA-2 merge into a new branch `feat/fixed-assets-fa3`.
- **Money is `decimal`.** Never `double`/`float`. Round with `Math.Round(x, 2, MidpointRounding.ToEven)` where a computation produces new cents (the catch-up schedule already rounds inside each `IDepreciationMethod` step).
- **Stage explicit paths only — NEVER `git add -A` / `git add .`.** Each commit lists its exact files.
- **Leave pre-existing uncommitted working-tree noise untouched:** `UI/Angular/src/app/core/api/environment.ts` and the several `*.Tests.csproj` files that show as modified. Do not stage or revert them. **Do not touch `Accounting101.slnx`, `Accounting101.Host/Program.cs`, or the Host `.csproj`** — FA-3 adds no project and the `.Api` project is already wired.
- **Commit trailer, exactly:** `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Module never self-approves.** All GL posts are `PendingApproval`.
- **No hardcoded account GUIDs.** Posting accounts come from configuration.
- **Module runner:** `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`. EphemeralMongo can flake on mongod startup under load — a startup timeout is not a real failure; retry once.
- **Whole-solution baseline:** 938/938 before this branch. It must stay green at each task and end green.

**Namespaces:** domain types → `Accounting101.FixedAssets` (project references only `Accounting101.Ledger.Contracts`); web/DI → `Accounting101.FixedAssets.Api`; tests → `Accounting101.FixedAssets.Tests`.

**Existing types you build on (already on disk, post-FA-2):** `Asset` (sealed record, `Id`,`Description`,`AcquisitionCost`,`InServiceDate`,`UsefulLifeMonths`,`SalvageValue`,`Method`,`DecliningBalanceFactor`,`Status`,`AccumulatedDepreciation` — all init props), `AssetStatus {Active=0, Disposed=1}`, `AssetDocument` (sealed record with the same fields), `DepreciationMethod {StraightLine=0, DecliningBalance=1}`, `IDepreciationMethod.DepreciationForPeriod(Asset)`, `DepreciationMethodSelector.For(DepreciationMethod)`, `FixedAssetsPostingAccounts` (record, currently `DepreciationExpenseAccountId` + `AccumulatedDepreciationAccountId`), `IFixedAssetsAccountsProvider.GetAccountsAsync`, `ILedgerClient` (`PostAsync`/`ReverseAsync`/`VoidAsync`/`GetEntriesBySourceRefAsync`), `EntryIdentity.ForSource(string, Guid)`, `PostEntryRequest(Guid Id, DateOnly EffectiveDate, string? Reference, string? Memo, IReadOnlyList<PostLineRequest> Lines, Guid SourceRef, string SourceType)`, `PostLineRequest(Guid AccountId, string Direction, decimal Amount)`, `IDocumentStore`, `DocumentResult<T>` (`Id`,`State`,`Sequence`,`Body`), `DocumentLifecycle`, `PagedResponse<T>`, `ReverseRequest`, `VoidRequest`, `EntryResponse`.

---

## Task 1: Depreciation catch-up schedule (pure)

The deterministic catch-up math a disposal needs: how many months an asset should be depreciated by its disposal date, and the accumulated depreciation that implies. Pure, no Mongo, no host.

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationSchedule.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationScheduleTests.cs`

**Interfaces:**
- Consumes: `Asset`, `AssetStatus`, `DepreciationMethod`, `IDepreciationMethod`, `StraightLineDepreciation`, `DecliningBalanceDepreciation` (all existing).
- Produces: `static class DepreciationSchedule` with
  - `int MonthsBetween(DateOnly inService, DateOnly disposal)` — whole months from the in-service month up to but excluding the disposal month; floored at 0.
  - `int TargetMonths(Asset asset, DateOnly disposal)` — `Min(MonthsBetween, asset.UsefulLifeMonths)`.
  - `decimal AccumulatedAfter(IDepreciationMethod method, Asset asset, int months)` — iterates the method `months` times from `AccumulatedDepreciation = 0`, returning the accumulated depreciation.

- [ ] **Step 1: Write the failing tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationScheduleTests.cs`:

```csharp
namespace Accounting101.FixedAssets.Tests;

public sealed class DepreciationScheduleTests
{
    private static Asset Make(decimal cost, decimal salvage, int life, DepreciationMethod method,
        decimal? factor = null, DateOnly? inService = null) => new()
    {
        Id = Guid.NewGuid(),
        Description = "test",
        AcquisitionCost = cost,
        InServiceDate = inService ?? new DateOnly(2026, 1, 1),
        UsefulLifeMonths = life,
        SalvageValue = salvage,
        Method = method,
        DecliningBalanceFactor = factor,
        Status = AssetStatus.Active,
        AccumulatedDepreciation = 0m,
    };

    [Theory]
    [InlineData(2026, 1, 2026, 6, 5)]   // Jan in-service, Jun disposal → Jan..May = 5 months
    [InlineData(2026, 1, 2026, 1, 0)]   // same month → 0
    [InlineData(2026, 6, 2027, 1, 7)]   // Jun 2026 → Jan 2027 = Jun..Dec = 7 months
    [InlineData(2026, 6, 2026, 5, 0)]   // disposal before in-service → floored at 0
    public void MonthsBetween_counts_whole_months_excluding_disposal_month(
        int iy, int im, int dy, int dm, int expected)
    {
        Assert.Equal(expected, DepreciationSchedule.MonthsBetween(new DateOnly(iy, im, 15), new DateOnly(dy, dm, 3)));
    }

    [Fact]
    public void TargetMonths_caps_at_useful_life()
    {
        Asset a = Make(12000m, 0m, 24, DepreciationMethod.StraightLine, inService: new DateOnly(2026, 1, 1));
        // 60 whole months to disposal, but life is 24 → capped at 24.
        Assert.Equal(24, DepreciationSchedule.TargetMonths(a, new DateOnly(2031, 1, 1)));
    }

    [Fact]
    public void AccumulatedAfter_straight_line_equals_months_times_monthly()
    {
        // (12000-0)/24 = 500/mo; after 5 months = 2500.
        IDepreciationMethod sl = new StraightLineDepreciation();
        Asset a = Make(12000m, 0m, 24, DepreciationMethod.StraightLine);
        Assert.Equal(2500m, DepreciationSchedule.AccumulatedAfter(sl, a, 5));
    }

    [Fact]
    public void AccumulatedAfter_zero_months_is_zero()
    {
        IDepreciationMethod sl = new StraightLineDepreciation();
        Asset a = Make(12000m, 0m, 24, DepreciationMethod.StraightLine);
        Assert.Equal(0m, DepreciationSchedule.AccumulatedAfter(sl, a, 0));
    }

    [Fact]
    public void AccumulatedAfter_declining_balance_matches_iterated_single_periods()
    {
        // DB factor 2.0, life 24 → rate 1/12. Iterate 3 months by hand:
        // m1: 12000*0.083333=1000.00 -> 1000; m2: 11000*0.0833=916.67 -> 1916.67; m3: 10083.33*0.0833=840.28 -> 2756.95
        IDepreciationMethod db = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 0m, 24, DepreciationMethod.DecliningBalance, factor: 2.0m);
        decimal expected = 1000m + 916.67m + 840.28m;
        Assert.Equal(expected, DepreciationSchedule.AccumulatedAfter(db, a, 3));
    }

    [Fact]
    public void AccumulatedAfter_stops_at_the_method_floor()
    {
        // SL base 1000 over 3 months = 333.33/mo; asking for 10 months must not exceed 1000.
        IDepreciationMethod sl = new StraightLineDepreciation();
        Asset a = Make(1000m, 0m, 3, DepreciationMethod.StraightLine);
        Assert.Equal(1000m, DepreciationSchedule.AccumulatedAfter(sl, a, 10));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter DepreciationScheduleTests`
Expected: FAIL to compile — `DepreciationSchedule` does not exist.

- [ ] **Step 3: Write the schedule**

Create `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationSchedule.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>The depreciation timeline math a disposal needs. Full-month convention: the in-service
/// month depreciates, the disposal month does not. Because every depreciation run applies exactly one
/// month via the same pure IDepreciationMethod, an asset's stored AccumulatedDepreciation after K runs
/// equals iterating the method K times from zero — so a disposal can compute the depreciation the asset
/// SHOULD have by its disposal date deterministically, without tracking how many runs were posted.</summary>
public static class DepreciationSchedule
{
    /// <summary>Whole months from the in-service month up to but EXCLUDING the disposal month; floored at 0.</summary>
    public static int MonthsBetween(DateOnly inService, DateOnly disposal)
    {
        int months = (disposal.Year * 12 + disposal.Month) - (inService.Year * 12 + inService.Month);
        return Math.Max(0, months);
    }

    /// <summary>The number of months to depreciate by the disposal date, capped at the asset's useful life.</summary>
    public static int TargetMonths(Asset asset, DateOnly disposal)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Math.Min(MonthsBetween(asset.InServiceDate, disposal), asset.UsefulLifeMonths);
    }

    /// <summary>The accumulated depreciation after applying the method for <paramref name="months"/> whole
    /// months from zero. Stops early once a period yields zero (fully depreciated).</summary>
    public static decimal AccumulatedAfter(IDepreciationMethod method, Asset asset, int months)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(asset);
        decimal accumulated = 0m;
        for (int i = 0; i < months; i++)
        {
            Asset snapshot = asset with { AccumulatedDepreciation = accumulated };
            decimal step = method.DepreciationForPeriod(snapshot);
            if (step <= 0m) break;
            accumulated += step;
        }
        return accumulated;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter DepreciationScheduleTests`
Expected: PASS (9 cases: 4 `MonthsBetween` + 5 others).

- [ ] **Step 5: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/DepreciationSchedule.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationScheduleTests.cs
git commit -m "feat(fixedassets): depreciation catch-up schedule for disposals

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Disposal posting accounts + domain types + posting recipe (pure)

Widen the posting-accounts record to the six accounts a disposal touches (updating every construction site so the module stays green), add the disposal document data shapes, and the pure recipe that composes the one balanced disposal entry.

**Files:**
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPostingAccounts.cs`
- Modify (construction sites — MUST all be updated together or the build breaks): `Modules/FixedAssets/Accounting101.FixedAssets.Api/ConfiguredFixedAssetsAccountsProvider.cs`, `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsHostFixture.cs`, `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsPostingTests.cs`, `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/Disposal.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalPosting.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsDisposalPostingTests.cs`

**Interfaces:**
- Consumes: `PostEntryRequest`, `PostLineRequest`, `EntryIdentity` (existing).
- Produces:
  - `FixedAssetsPostingAccounts` gains four required `Guid` init props: `AssetCostAccountId`, `DisposalProceedsAccountId`, `GainOnDisposalAccountId`, `LossOnDisposalAccountId`.
  - `sealed record DisposalBody(Guid AssetId, DateOnly DisposalDate, decimal Proceeds, decimal CatchUpDepreciation, decimal AccumulatedBeforeDisposal, decimal AccumulatedAtDisposal, decimal NetBookValue, decimal GainLoss, string? Memo)`.
  - `enum DisposalStatus { Posted = 0, Voided = 1 }`.
  - `sealed record Disposal` (`Id`, `Number` (string?), all `DisposalBody` fields, `Status`).
  - `sealed record DisposalView(Disposal Disposal)`.
  - `static class FixedAssetsDisposalPosting` with `const string DisposalSourceType = "Disposal"` and `PostEntryRequest ComposeDisposal(Guid disposalId, DateOnly disposalDate, decimal acquisitionCost, decimal currentAccumulated, decimal catchUp, decimal proceeds, decimal gainLoss, string? memo, FixedAssetsPostingAccounts accounts)`.

- [ ] **Step 1: Write the failing posting tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsDisposalPostingTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsDisposalPostingTests
{
    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    private static FixedAssetsPostingAccounts Accounts() => new()
    {
        DepreciationExpenseAccountId = Guid.NewGuid(),
        AccumulatedDepreciationAccountId = Guid.NewGuid(),
        AssetCostAccountId = Guid.NewGuid(),
        DisposalProceedsAccountId = Guid.NewGuid(),
        GainOnDisposalAccountId = Guid.NewGuid(),
        LossOnDisposalAccountId = Guid.NewGuid(),
    };

    [Fact]
    public void Sale_at_a_gain_produces_a_balanced_entry_with_a_gain_credit()
    {
        FixedAssetsPostingAccounts a = Accounts();
        // cost 12000, currentAccum 5000, catchUp 500 -> finalAccum 5500, NBV 6500; proceeds 8000 -> gain 1500.
        PostEntryRequest e = FixedAssetsDisposalPosting.ComposeDisposal(
            Guid.NewGuid(), new DateOnly(2026, 6, 30), 12000m, 5000m, 500m, 8000m, 1500m, "sold", a);

        Assert.Equal(0m, e.Lines.Sum(Signed)); // balanced
        Assert.Equal(500m, e.Lines.Single(l => l.AccountId == a.DepreciationExpenseAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(5000m, e.Lines.Single(l => l.AccountId == a.AccumulatedDepreciationAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(8000m, e.Lines.Single(l => l.AccountId == a.DisposalProceedsAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(12000m, e.Lines.Single(l => l.AccountId == a.AssetCostAccountId && l.Direction == "Credit").Amount);
        Assert.Equal(1500m, e.Lines.Single(l => l.AccountId == a.GainOnDisposalAccountId && l.Direction == "Credit").Amount);
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.LossOnDisposalAccountId);
    }

    [Fact]
    public void Sale_at_a_loss_produces_a_balanced_entry_with_a_loss_debit()
    {
        FixedAssetsPostingAccounts a = Accounts();
        // cost 12000, currentAccum 3000, catchUp 0 -> finalAccum 3000, NBV 9000; proceeds 7000 -> loss 2000.
        PostEntryRequest e = FixedAssetsDisposalPosting.ComposeDisposal(
            Guid.NewGuid(), new DateOnly(2026, 6, 30), 12000m, 3000m, 0m, 7000m, -2000m, null, a);

        Assert.Equal(0m, e.Lines.Sum(Signed));
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.DepreciationExpenseAccountId); // no catch-up line
        Assert.Equal(2000m, e.Lines.Single(l => l.AccountId == a.LossOnDisposalAccountId && l.Direction == "Debit").Amount);
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.GainOnDisposalAccountId);
    }

    [Fact]
    public void Retirement_with_zero_proceeds_omits_the_cash_line_and_balances()
    {
        FixedAssetsPostingAccounts a = Accounts();
        // cost 12000, currentAccum 12000, catchUp 0 -> NBV 0; proceeds 0 -> gain/loss 0.
        PostEntryRequest e = FixedAssetsDisposalPosting.ComposeDisposal(
            Guid.NewGuid(), new DateOnly(2026, 6, 30), 12000m, 12000m, 0m, 0m, 0m, "scrapped", a);

        Assert.Equal(0m, e.Lines.Sum(Signed));
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.DisposalProceedsAccountId); // no cash line
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.GainOnDisposalAccountId);
        Assert.DoesNotContain(e.Lines, l => l.AccountId == a.LossOnDisposalAccountId);
        // Dr Accum 12000 / Cr AssetCost 12000
        Assert.Equal(2, e.Lines.Count);
    }

    [Fact]
    public void Carries_source_type_and_deterministic_id()
    {
        FixedAssetsPostingAccounts a = Accounts();
        Guid id = Guid.NewGuid();
        PostEntryRequest x = FixedAssetsDisposalPosting.ComposeDisposal(id, new DateOnly(2026, 6, 30), 12000m, 5000m, 0m, 8000m, 1000m, null, a);
        PostEntryRequest y = FixedAssetsDisposalPosting.ComposeDisposal(id, new DateOnly(2026, 6, 30), 12000m, 5000m, 0m, 8000m, 1000m, null, a);
        Assert.Equal("Disposal", x.SourceType);
        Assert.Equal(id, x.SourceRef);
        Assert.Equal(EntryIdentity.ForSource(FixedAssetsDisposalPosting.DisposalSourceType, id), x.Id);
        Assert.Equal(x.Id, y.Id);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FixedAssetsDisposalPostingTests`
Expected: FAIL to compile — the new accounts, `Disposal` types, and `FixedAssetsDisposalPosting` do not exist.

- [ ] **Step 3: Widen the posting-accounts record**

Replace `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPostingAccounts.cs` with:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>The chart accounts the fixed-assets module posts to. The first two are used by depreciation
/// runs (FA-2); all six are used by disposals (FA-3). Supplied by configuration; no hardcoded numbers.</summary>
public sealed record FixedAssetsPostingAccounts
{
    /// <summary>Depreciation Expense — debited for a run total, and for a disposal's catch-up depreciation.</summary>
    public required Guid DepreciationExpenseAccountId { get; init; }

    /// <summary>Accumulated Depreciation (contra-asset) — credited by runs; debited on disposal to clear it.</summary>
    public required Guid AccumulatedDepreciationAccountId { get; init; }

    /// <summary>Fixed-asset cost account — credited on disposal to remove the asset's cost from the books.</summary>
    public required Guid AssetCostAccountId { get; init; }

    /// <summary>Cash / disposal proceeds — debited for sale proceeds (omitted on a zero-proceeds retirement).</summary>
    public required Guid DisposalProceedsAccountId { get; init; }

    /// <summary>Gain on Disposal — credited when proceeds exceed net book value.</summary>
    public required Guid GainOnDisposalAccountId { get; init; }

    /// <summary>Loss on Disposal — debited when net book value exceeds proceeds.</summary>
    public required Guid LossOnDisposalAccountId { get; init; }
}
```

- [ ] **Step 4: Update every construction site so the module compiles**

There are four places that build a `FixedAssetsPostingAccounts` with only the two FA-2 accounts; each must set the four new ids. Find them: `grep -rn "new FixedAssetsPostingAccounts\|FixedAssetsPostingAccounts$" Modules/FixedAssets` (and check the provider). Update each:

**`Modules/FixedAssets/Accounting101.FixedAssets.Api/ConfiguredFixedAssetsAccountsProvider.cs`** — add the four keys:

```csharp
    public Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new FixedAssetsPostingAccounts
        {
            DepreciationExpenseAccountId = Read("FixedAssets:Accounts:DepreciationExpense"),
            AccumulatedDepreciationAccountId = Read("FixedAssets:Accounts:AccumulatedDepreciation"),
            AssetCostAccountId = Read("FixedAssets:Accounts:AssetCost"),
            DisposalProceedsAccountId = Read("FixedAssets:Accounts:DisposalProceeds"),
            GainOnDisposalAccountId = Read("FixedAssets:Accounts:GainOnDisposal"),
            LossOnDisposalAccountId = Read("FixedAssets:Accounts:LossOnDisposal"),
        });
```

Also update the class doc-comment's config-key list to mention all six.

**`Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsHostFixture.cs`** — add four account-id properties and publish them via `UseSetting` (so FA-2 depreciation-run E2E, which resolves the provider, keeps working now that the four keys are required):

```csharp
    // The four additional disposal posting accounts.
    public Guid AssetCostAccountId { get; } = Guid.NewGuid();
    public Guid DisposalProceedsAccountId { get; } = Guid.NewGuid();
    public Guid GainOnDisposalAccountId { get; } = Guid.NewGuid();
    public Guid LossOnDisposalAccountId { get; } = Guid.NewGuid();
```

and, in `ConfigureWebHost` after the two existing `FixedAssets:Accounts:*` settings:

```csharp
        builder.UseSetting("FixedAssets:Accounts:AssetCost", AssetCostAccountId.ToString());
        builder.UseSetting("FixedAssets:Accounts:DisposalProceeds", DisposalProceedsAccountId.ToString());
        builder.UseSetting("FixedAssets:Accounts:GainOnDisposal", GainOnDisposalAccountId.ToString());
        builder.UseSetting("FixedAssets:Accounts:LossOnDisposal", LossOnDisposalAccountId.ToString());
```

**`Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsPostingTests.cs`** — its `MakeAccounts` helper builds a `FixedAssetsPostingAccounts` with two ids; add the four new ones (each `Guid.NewGuid()`), so it compiles. Do not change the FA-2 run-posting assertions.

**`Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs`** — the `FixedAccountsProvider` fake builds a `FixedAssetsPostingAccounts`; add the four new ids (each `Guid.NewGuid()`, exposed as public properties if the fake exposes the others, else inline `Guid.NewGuid()`). Match the fake's existing style.

> Implementer note: after this step the whole module suite must still build. Any missed construction site is a compile error naming the missing required property — fix it the same way.

- [ ] **Step 5: Write the disposal domain types**

Create `Modules/FixedAssets/Accounting101.FixedAssets/Disposal.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>The stored body of a disposal (the evidentiary document body). The breakdown is retained for
/// audit and to roll the asset back on void (AccumulatedBeforeDisposal).</summary>
public sealed record DisposalBody(
    Guid AssetId,
    DateOnly DisposalDate,
    decimal Proceeds,
    decimal CatchUpDepreciation,
    decimal AccumulatedBeforeDisposal,
    decimal AccumulatedAtDisposal,
    decimal NetBookValue,
    decimal GainLoss,
    string? Memo);

/// <summary>Lifecycle of a disposal: posted, or voided.</summary>
public enum DisposalStatus
{
    Posted = 0,
    Voided = 1,
}

/// <summary>A disposal — the engine assigns the number; status is derived from the document lifecycle.</summary>
public sealed record Disposal
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }
    public required Guid AssetId { get; init; }
    public required DateOnly DisposalDate { get; init; }
    public required decimal Proceeds { get; init; }
    public required decimal CatchUpDepreciation { get; init; }
    public required decimal AccumulatedBeforeDisposal { get; init; }
    public required decimal AccumulatedAtDisposal { get; init; }
    public required decimal NetBookValue { get; init; }
    public required decimal GainLoss { get; init; }
    public string? Memo { get; init; }
    public required DisposalStatus Status { get; init; }
}

/// <summary>Read model for a disposal.</summary>
public sealed record DisposalView(Disposal Disposal);
```

- [ ] **Step 6: Write the disposal posting recipe**

Create `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalPosting.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The disposal recipe: one asset removal composes into one balanced journal entry that clears
/// the asset's cost and accumulated depreciation, records any final catch-up depreciation and proceeds,
/// and books the gain or loss. Zero-amount lines are omitted (a disposal's shape varies by proceeds /
/// catch-up / gain-vs-loss). Pure — sequencing, approval, and persistence are the engine's.
/// <para>The Accumulated Depreciation line debits only <c>currentAccumulated</c>: the catch-up's own
/// contribution to accumulated depreciation and its immediate clearing on disposal net to zero, leaving
/// the depreciation expense in P&amp;L and only the pre-existing accumulated cleared.</para></summary>
public static class FixedAssetsDisposalPosting
{
    public const string DisposalSourceType = "Disposal";

    public static PostEntryRequest ComposeDisposal(
        Guid disposalId, DateOnly disposalDate, decimal acquisitionCost, decimal currentAccumulated,
        decimal catchUp, decimal proceeds, decimal gainLoss, string? memo, FixedAssetsPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (acquisitionCost <= 0m)
            throw new ArgumentException("Acquisition cost must be positive.", nameof(acquisitionCost));

        List<PostLineRequest> lines = [];
        if (catchUp > 0m) lines.Add(new(accounts.DepreciationExpenseAccountId, "Debit", catchUp));
        if (currentAccumulated > 0m) lines.Add(new(accounts.AccumulatedDepreciationAccountId, "Debit", currentAccumulated));
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
}
```

- [ ] **Step 7: Run the whole module suite**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS — the new disposal-posting tests plus all FA-1/FA-2 tests (the account widening did not break the FA-2 run E2E, because the host fixture now supplies the four new config keys). Report the count.

- [ ] **Step 8: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPostingAccounts.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/Disposal.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalPosting.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/ConfiguredFixedAssetsAccountsProvider.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsHostFixture.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsPostingTests.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsDisposalPostingTests.cs
git commit -m "feat(fixedassets): disposal posting accounts + domain types + aggregate recipe

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Disposal evidentiary store

Persist disposals as evidentiary documents, mirroring `DocumentDepreciationRunStore`, plus the by-asset lookup the disposal service needs (re-dispose guard + locating the doc to void).

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DisposalPorts.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DocumentDisposalStore.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DisposalStoreFixture.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DisposalStoreTests.cs`

**Interfaces:**
- Consumes: `IDocumentStore`, `DocumentResult<T>`, `DocumentLifecycle`, `PagedResponse<T>`, the Task-2 disposal types.
- Produces:
  - `interface IDisposalStore` with `RecordAsync`, `VoidAsync`, `GetAsync`, `GetByClientPagedAsync`, `GetActiveByAssetAsync(Guid clientId, Guid assetId, CancellationToken ct) -> Task<Disposal?>` (the non-voided disposal for an asset, else null).
  - `sealed class DocumentDisposalStore : IDisposalStore` — collection `"disposals"`, number `DP-{seq:D5}`.

- [ ] **Step 1: Write the store fixture**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DisposalStoreFixture.cs` by mirroring `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunStoreFixture.cs` (read it): same `ControlStore`-field + `NewClient()` + `IDocumentStore Store` shape, reusing the existing internal `FixedActor` (do NOT redefine it). The only change: the manifest declares the disposals collection — `new ModuleManifestBuilder().Reference("assets").Evidentiary("depreciation-runs").Evidentiary("disposals").Build()`.

```csharp
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.FixedAssets.Tests;

/// <summary>Disposable EphemeralMongo wired for the disposals collection: a ScopedDocumentStore bound to
/// the "fixedassets" identity with the disposals evidentiary manifest. NewClient() registers a fresh
/// client per call so by-asset / count assertions are isolated.</summary>
public sealed class DisposalStoreFixture : IAsyncLifetime
{
    private ControlStore _control = null!;
    public IDocumentStore Store { get; private set; } = null!;
    private Guid UserId { get; set; }

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);

        _control = new ControlStore(mongo.GetDatabase("control_" + Guid.NewGuid().ToString("N")));
        UserId = Guid.NewGuid();
        await _control.RegisterModuleAsync(new ModuleRegistration { Key = "fixedassets", Name = "Fixed Assets", Enabled = true });

        ModuleManifest manifest = new ModuleManifestBuilder()
            .Reference("assets").Evidentiary("depreciation-runs").Evidentiary("disposals").Build();
        Store = new ScopedDocumentStore(
            new ModuleIdentity("fixedassets"),
            manifest,
            new ClientDatabaseResolver(mongo, _control),
            new FixedActor(UserId),
            new ModuleAccess(_control));
    }

    public Guid NewClient()
    {
        Guid clientId = Guid.NewGuid();
        _control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Acme", DatabaseName = "client_" + clientId.ToString("N"),
            EnabledModules = ["fixedassets"],
        }).GetAwaiter().GetResult();
        _control.AddMembershipAsync(UserId, clientId, LedgerRole.Controller).GetAwaiter().GetResult();
        return clientId;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
```

> Implementer note: `FixedActor` already exists in `DepreciationRunStoreFixture.cs`/`AssetDocumentStoreFixture.cs` (same test namespace) — reuse it, do not redefine.

- [ ] **Step 2: Write the failing store tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DisposalStoreTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class DisposalStoreTests(DisposalStoreFixture fixture) : IClassFixture<DisposalStoreFixture>
{
    private DocumentDisposalStore Store() => new(fixture.Store);

    private static DisposalBody Body(Guid assetId, decimal proceeds = 8000m) =>
        new(assetId, new DateOnly(2026, 6, 30), proceeds, CatchUpDepreciation: 500m,
            AccumulatedBeforeDisposal: 5000m, AccumulatedAtDisposal: 5500m,
            NetBookValue: 6500m, GainLoss: proceeds - 6500m, Memo: null);

    [Fact]
    public async Task Record_assigns_a_number_and_posted_status_and_round_trips()
    {
        DocumentDisposalStore store = Store();
        Guid clientId = fixture.NewClient();
        Guid asset = Guid.NewGuid();

        Disposal d = await store.RecordAsync(clientId, Body(asset), default);
        Assert.NotNull(d.Number);
        Assert.Equal(DisposalStatus.Posted, d.Status);
        Assert.Equal(asset, d.AssetId);
        Assert.Equal(1500m, d.GainLoss);

        Disposal? fetched = await store.GetAsync(clientId, d.Id, default);
        Assert.NotNull(fetched);
        Assert.Equal(5000m, fetched!.AccumulatedBeforeDisposal);
    }

    [Fact]
    public async Task GetActiveByAsset_finds_a_non_voided_disposal_and_ignores_voided()
    {
        DocumentDisposalStore store = Store();
        Guid clientId = fixture.NewClient();
        Guid asset = Guid.NewGuid();

        Disposal d = await store.RecordAsync(clientId, Body(asset), default);
        Assert.NotNull(await store.GetActiveByAssetAsync(clientId, asset, default));
        Assert.Null(await store.GetActiveByAssetAsync(clientId, Guid.NewGuid(), default));

        await store.VoidAsync(clientId, d.Id, default);
        Assert.Null(await store.GetActiveByAssetAsync(clientId, asset, default));
    }

    [Fact]
    public async Task Paged_list_excludes_voided_unless_requested()
    {
        DocumentDisposalStore store = Store();
        Guid clientId = fixture.NewClient();

        Disposal a = await store.RecordAsync(clientId, Body(Guid.NewGuid()), default);
        await store.RecordAsync(clientId, Body(Guid.NewGuid()), default);
        await store.VoidAsync(clientId, a.Id, default);

        PagedResponse<Disposal> excl = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.Equal(1, excl.Total);
        PagedResponse<Disposal> incl = await store.GetByClientPagedAsync(clientId, 0, 50, true, true, default);
        Assert.Equal(2, incl.Total);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter DisposalStoreTests`
Expected: FAIL to compile — `IDisposalStore` / `DocumentDisposalStore` do not exist.

- [ ] **Step 4: Write the port**

Create `Modules/FixedAssets/Accounting101.FixedAssets/DisposalPorts.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The disposal store — evidentiary documents backed by the engine's document store. Numbered +
/// finalized on record, voidable. Adds the by-asset lookup the disposal service uses to guard re-disposal
/// and to locate a disposal to void.</summary>
public interface IDisposalStore
{
    Task<Disposal> RecordAsync(Guid clientId, DisposalBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid disposalId, CancellationToken ct = default);
    Task<Disposal?> GetAsync(Guid clientId, Guid disposalId, CancellationToken ct = default);
    Task<PagedResponse<Disposal>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);

    /// <summary>The non-voided disposal for an asset, if one exists.</summary>
    Task<Disposal?> GetActiveByAssetAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
}
```

- [ ] **Step 5: Write the store**

Create `Modules/FixedAssets/Accounting101.FixedAssets/DocumentDisposalStore.cs` (mirror `DocumentDepreciationRunStore`; `GetActiveByAssetAsync` uses the UNBOUNDED no-limit query — the FA-2 lesson: a supplied limit is clamped to 200):

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Persists disposals as evidentiary documents (created, immediately finalized into a numbered
/// append-only document, voidable). Number + status derive from the engine envelope.</summary>
public sealed class DocumentDisposalStore(IDocumentStore documents) : IDisposalStore
{
    private const string Collection = "disposals";

    public async Task<Disposal> RecordAsync(Guid clientId, DisposalBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<DisposalBody>? result = await documents.GetAsync<DisposalBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid disposalId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, disposalId, ct);

    public async Task<Disposal?> GetAsync(Guid clientId, Guid disposalId, CancellationToken ct = default)
    {
        DocumentResult<DisposalBody>? result = await documents.GetAsync<DisposalBody>(clientId, Collection, disposalId, ct);
        return result is null ? null : Map(result);
    }

    public async Task<PagedResponse<Disposal>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<DisposalBody>> page =
            await documents.QueryAsync<DisposalBody>(clientId, Collection, Tags(), skip, limit, descending, includeVoided, ct);
        long total = await documents.CountAsync(clientId, Collection, Tags(), includeVoided, ct);
        return new PagedResponse<Disposal>(page.Select(Map).ToList(), total, skip, limit);
    }

    public async Task<Disposal?> GetActiveByAssetAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        // Unbounded query (no limit): MongoDocumentStore clamps any supplied limit to 200. includeVoided
        // defaults false → non-voided disposals only.
        IReadOnlyList<DocumentResult<DisposalBody>> all =
            await documents.QueryAsync<DisposalBody>(clientId, Collection, Tags(), cancellationToken: ct);
        DocumentResult<DisposalBody>? hit = all.FirstOrDefault(r => r.Body.AssetId == assetId);
        return hit is null ? null : Map(hit);
    }

    private static Dictionary<string, string> Tags() => new();

    private static Disposal Map(DocumentResult<DisposalBody> r) => new()
    {
        Id = r.Id,
        Number = r.Sequence is { } seq ? $"DP-{seq:D5}" : null,
        AssetId = r.Body.AssetId,
        DisposalDate = r.Body.DisposalDate,
        Proceeds = r.Body.Proceeds,
        CatchUpDepreciation = r.Body.CatchUpDepreciation,
        AccumulatedBeforeDisposal = r.Body.AccumulatedBeforeDisposal,
        AccumulatedAtDisposal = r.Body.AccumulatedAtDisposal,
        NetBookValue = r.Body.NetBookValue,
        GainLoss = r.Body.GainLoss,
        Memo = r.Body.Memo,
        Status = r.State switch
        {
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => DisposalStatus.Voided,
            _ => DisposalStatus.Posted,
        },
    };
}
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter DisposalStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/DisposalPorts.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/DocumentDisposalStore.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/DisposalStoreFixture.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/DisposalStoreTests.cs
git commit -m "feat(fixedassets): evidentiary disposal store with by-asset lookup

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Asset store disposed-lifecycle + guards

The server-owned mutations a disposal needs (`MarkDisposedAsync` / `ReinstateAsync`), and the guards that freeze a `Disposed` asset against edit/deactivate/reactivate. Store + endpoints change together so the module stays green.

**Files:**
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPorts.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/DocumentAssetStore.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs` (its `InMemoryAssetStore` implements `IAssetStore`; the two new interface methods must be added or the test project won't compile)
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetDisposalLifecycleTests.cs`

**Interfaces:**
- Consumes: existing `IAssetStore`/`DocumentAssetStore`, `Asset`, `AssetDocument`, `AssetStatus`, `UpdateResult`/`UpdateOutcome`, `DeactivateResult`, `ReactivateResult`, `IDocumentStore`, `DocumentLifecycle`.
- Produces (new on `IAssetStore`):
  - `Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, decimal finalAccumulated, CancellationToken ct)`
  - `Task ReinstateAsync(Guid clientId, Guid assetId, decimal restoreAccumulated, CancellationToken ct)`
  - `enum DisposeOutcome { NotFound, NotActive, Disposed }`
  - `readonly record struct DisposeStamp(DisposeOutcome Outcome, Asset? Asset, decimal PriorAccumulated)`
  - `UpdateOutcome` gains `Disposed`; `UpdateResult` gains a `Disposed` static.

- [ ] **Step 1: Write the failing store tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetDisposalLifecycleTests.cs` (reuse the FA-1 `AssetDocumentStoreFixture` — `IDocumentStore Store` + single `Guid ClientId`, no `NewClient()`; local `Store()` helper):

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class AssetDisposalLifecycleTests(AssetDocumentStoreFixture fixture)
    : IClassFixture<AssetDocumentStoreFixture>
{
    private DocumentAssetStore Store() => new(fixture.Store);

    private static AssetBody Body() =>
        new("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task MarkDisposed_sets_disposed_and_final_accumulated_and_returns_prior()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        // Give it some prior accumulated via a depreciation apply.
        await store.ApplyDepreciationAsync(clientId, [new DepreciationRunLine(a.Id, 2000m)], default);

        DisposeStamp stamp = await store.MarkDisposedAsync(clientId, a.Id, finalAccumulated: 2500m, default);
        Assert.Equal(DisposeOutcome.Disposed, stamp.Outcome);
        Assert.Equal(2000m, stamp.PriorAccumulated);
        Assert.Equal(AssetStatus.Disposed, stamp.Asset!.Status);
        Assert.Equal(2500m, stamp.Asset.AccumulatedDepreciation);

        Asset? reread = await store.GetAsync(clientId, a.Id, default);
        Assert.Equal(AssetStatus.Disposed, reread!.Status);
    }

    [Fact]
    public async Task MarkDisposed_refuses_a_missing_or_already_disposed_asset()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Assert.Equal(DisposeOutcome.NotFound, (await store.MarkDisposedAsync(clientId, Guid.NewGuid(), 0m, default)).Outcome);

        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.MarkDisposedAsync(clientId, a.Id, 0m, default);
        Assert.Equal(DisposeOutcome.NotActive, (await store.MarkDisposedAsync(clientId, a.Id, 0m, default)).Outcome);
    }

    [Fact]
    public async Task Reinstate_restores_active_and_accumulated()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.MarkDisposedAsync(clientId, a.Id, 2500m, default);

        await store.ReinstateAsync(clientId, a.Id, restoreAccumulated: 2000m, default);
        Asset? reread = await store.GetAsync(clientId, a.Id, default);
        Assert.Equal(AssetStatus.Active, reread!.Status);
        Assert.Equal(2000m, reread.AccumulatedDepreciation);
    }

    [Fact]
    public async Task A_disposed_asset_is_frozen_against_update_deactivate_reactivate()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.MarkDisposedAsync(clientId, a.Id, 0m, default);

        Assert.Equal(UpdateOutcome.Disposed, (await store.UpdateAsync(clientId, a.Id, Body(), default)).Outcome);
        Assert.Equal(DeactivateResult.Disposed, await store.DeactivateAsync(clientId, a.Id, default));
        Assert.Equal(ReactivateResult.Disposed, await store.ReactivateAsync(clientId, a.Id, default));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter AssetDisposalLifecycleTests`
Expected: FAIL to compile — the new members / enum values don't exist.

- [ ] **Step 3: Extend the port**

Edit `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPorts.cs`:
- Add to `IAssetStore` (after `ReverseDepreciationAsync`):

```csharp
    /// <summary>Stamp an Active asset Disposed with its final accumulated depreciation; refuse a missing or
    /// non-Active asset. Returns the prior accumulated (for the disposal's roll-back record).</summary>
    Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, decimal finalAccumulated, CancellationToken ct = default);

    /// <summary>Return a disposed asset to Active with the given accumulated depreciation (disposal void).</summary>
    Task ReinstateAsync(Guid clientId, Guid assetId, decimal restoreAccumulated, CancellationToken ct = default);
```

- Add `Disposed` to `DeactivateResult` and `ReactivateResult`:

```csharp
public enum DeactivateResult { NotFound, AlreadyInactive, Deactivated, Disposed }

public enum ReactivateResult { NotFound, AlreadyActive, Reactivated, Disposed }
```

- Add `Disposed` to `UpdateOutcome` and a `UpdateResult.Disposed` static:

```csharp
public enum UpdateOutcome { NotFound, Inactive, Updated, Disposed }
```

and inside `UpdateResult`:

```csharp
    public static readonly UpdateResult Disposed = new(UpdateOutcome.Disposed, null);
```

- Add the dispose result types:

```csharp
/// <summary>Outcome of a dispose stamp: asset missing, not in an Active state, or newly disposed.</summary>
public enum DisposeOutcome { NotFound, NotActive, Disposed }

/// <summary>Result of MarkDisposedAsync — the outcome, the stamped asset (when Disposed), and the
/// accumulated depreciation the asset had immediately before disposal.</summary>
public readonly record struct DisposeStamp(DisposeOutcome Outcome, Asset? Asset, decimal PriorAccumulated);
```

- [ ] **Step 4: Implement the store changes**

Edit `Modules/FixedAssets/Accounting101.FixedAssets/DocumentAssetStore.cs`:
- Add the disposed guard to `UpdateAsync` (before the Inactive check is fine; a disposed asset is reference-Active):

```csharp
        if (existing is null) return UpdateResult.NotFound;
        if (existing.Body.Status == AssetStatus.Disposed) return UpdateResult.Disposed; // frozen until void
        if (existing.State == DocumentLifecycle.Inactive) return UpdateResult.Inactive; // sticky: reactivate first
```

- Add the disposed guard to `DeactivateAsync` and `ReactivateAsync` (after the null check):

```csharp
        // in DeactivateAsync, after `if (existing is null) return DeactivateResult.NotFound;`
        if (existing.Body.Status == AssetStatus.Disposed) return DeactivateResult.Disposed;
```

```csharp
        // in ReactivateAsync, after `if (existing is null) return ReactivateResult.NotFound;`
        if (existing.Body.Status == AssetStatus.Disposed) return ReactivateResult.Disposed;
```

- Add the two new mutations (near the Apply/Reverse helpers):

```csharp
    public async Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, decimal finalAccumulated, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return new DisposeStamp(DisposeOutcome.NotFound, null, 0m);
        if (existing.Body.Status != AssetStatus.Active) return new DisposeStamp(DisposeOutcome.NotActive, null, existing.Body.AccumulatedDepreciation);
        decimal prior = existing.Body.AccumulatedDepreciation;
        AssetDocument doc = existing.Body with { Status = AssetStatus.Disposed, AccumulatedDepreciation = finalAccumulated };
        await documents.PutAsync(clientId, Collection, assetId, doc, NoTags, ct);
        return new DisposeStamp(DisposeOutcome.Disposed, Map(assetId, doc), prior);
    }

    public async Task ReinstateAsync(Guid clientId, Guid assetId, decimal restoreAccumulated, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return; // asset gone; nothing to reinstate (disposal void tolerates it)
        AssetDocument doc = existing.Body with { Status = AssetStatus.Active, AccumulatedDepreciation = restoreAccumulated };
        await documents.PutAsync(clientId, Collection, assetId, doc, NoTags, ct);
    }
```

- [ ] **Step 5: Map the new outcomes at the endpoints**

Edit `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`:
- In `UpdateAsset`'s switch, add a `Disposed` arm:

```csharp
                UpdateOutcome.Disposed => Results.Problem(
                    "Asset is disposed; void the disposal before editing.", statusCode: StatusCodes.Status409Conflict),
```

- In `DeactivateAsset`'s switch, add:

```csharp
            DeactivateResult.Disposed => Results.Problem(
                "Asset is disposed; void the disposal first.", statusCode: StatusCodes.Status409Conflict),
```

- In `ReactivateAsset`, after the `AlreadyActive` check:

```csharp
        if (result == ReactivateResult.Disposed)
            return Results.Problem("Asset is disposed; void the disposal first.", statusCode: StatusCodes.Status409Conflict);
```

- [ ] **Step 6: Implement the two new members in the test fake**

Adding `MarkDisposedAsync`/`ReinstateAsync` to `IAssetStore` breaks `InMemoryAssetStore` in `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs` (a fake must implement every interface member). Add both, honoring business `Status` (do NOT touch the `_deactivated` set — a disposed asset stays reference-Active so the run service's own `Status` filter is what excludes it):

```csharp
    public Task<DisposeStamp> MarkDisposedAsync(Guid clientId, Guid assetId, decimal finalAccumulated, CancellationToken ct = default)
    {
        if (!_assets.TryGetValue(assetId, out Asset? a))
            return Task.FromResult(new DisposeStamp(DisposeOutcome.NotFound, null, 0m));
        if (a.Status != AssetStatus.Active)
            return Task.FromResult(new DisposeStamp(DisposeOutcome.NotActive, null, a.AccumulatedDepreciation));
        decimal prior = a.AccumulatedDepreciation;
        Asset disposed = a with { Status = AssetStatus.Disposed, AccumulatedDepreciation = finalAccumulated };
        _assets[assetId] = disposed;
        return Task.FromResult(new DisposeStamp(DisposeOutcome.Disposed, disposed, prior));
    }

    public Task ReinstateAsync(Guid clientId, Guid assetId, decimal restoreAccumulated, CancellationToken ct = default)
    {
        if (_assets.TryGetValue(assetId, out Asset? a))
            _assets[assetId] = a with { Status = AssetStatus.Active, AccumulatedDepreciation = restoreAccumulated };
        return Task.CompletedTask;
    }
```

- [ ] **Step 7: Run the module suite**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS — new lifecycle tests plus all prior tests (the test project now compiles with the fake implementing the new members). Report the count.

- [ ] **Step 8: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPorts.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/DocumentAssetStore.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetDisposalLifecycleTests.cs
git commit -m "feat(fixedassets): asset disposed-lifecycle (mark/reinstate) + frozen guards

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Run-service edits — exclude disposed assets + fold in the FA-2 recovery fix

Two changes to `FixedAssetsRunService`: exclude `Disposed` assets from depreciation runs, and close FA-2's deferred non-transactional-run gap (resolve accounts before persist/apply; tolerate a missing entry on void).

**Files:**
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsRunService.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs` (add the ability to make the ledger fake report no spawned entry, and a throwing accounts provider — see notes)
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsRunServiceFa3Tests.cs`

**Interfaces:**
- Consumes: existing `FixedAssetsRunService`, `IAssetStore`, `IDepreciationRunStore`, `DepreciationMethodSelector`, `IFixedAssetsAccountsProvider`, `ILedgerClient`, `AssetStatus`, the FA-2 fakes.
- Produces: no new public types; behavioral changes to `RunDepreciationAsync` and `VoidRunAsync`.

- [ ] **Step 1: Write the failing tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsRunServiceFa3Tests.cs`. It reuses the FA-2 fakes (`InMemoryAssetStore`, `InMemoryDepreciationRunStore`, `FakeLedgerClient`, `FixedAccountsProvider`) — read `Fakes.cs` for their exact shape and construction. Three behaviors:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsRunServiceFa3Tests
{
    private static AssetBody Sl(decimal cost, int life, DateOnly inService) =>
        new("SL", cost, inService, life, 0m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Disposed_assets_are_excluded_from_a_depreciation_run()
    {
        InMemoryAssetStore assets = new();
        InMemoryDepreciationRunStore runs = new();
        FakeLedgerClient ledger = new();
        FixedAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        FixedAssetsRunService svc = new(assets, runs, selector, accounts, ledger);
        Guid clientId = Guid.NewGuid();

        Asset active = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default); // 500/mo
        Asset disposed = await assets.CreateAsync(clientId, Sl(6000m, 24, new DateOnly(2026, 1, 1)), default);
        await assets.MarkDisposedAsync(clientId, disposed.Id, 0m, default); // Disposed → excluded

        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);
        Assert.Equal(500m, run.Total);                 // only the active asset
        Assert.Equal(active.Id, Assert.Single(run.Lines).AssetId);
    }

    [Fact]
    public async Task A_run_that_cannot_resolve_accounts_persists_nothing_and_advances_nothing()
    {
        InMemoryAssetStore assets = new();
        InMemoryDepreciationRunStore runs = new();
        FakeLedgerClient ledger = new();
        ThrowingAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        FixedAssetsRunService svc = new(assets, runs, selector, accounts, ledger);
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default));

        // No run persisted, asset not advanced, nothing posted.
        Assert.Null(await runs.GetByPeriodAsync(clientId, new DepreciationPeriod(2026, 1), default));
        Assert.Equal(0m, (await assets.GetAsync(clientId, a.Id, default))!.AccumulatedDepreciation);
        Assert.Empty(ledger.Posted);
    }

    [Fact]
    public async Task Voiding_a_run_with_no_spawned_entry_still_rolls_back_and_marks_voided()
    {
        InMemoryAssetStore assets = new();
        InMemoryDepreciationRunStore runs = new();
        FakeLedgerClient ledger = new() { ReturnNoEntries = true }; // simulate a post that never landed
        FixedAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        FixedAssetsRunService svc = new(assets, runs, selector, accounts, ledger);
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);

        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);
        Assert.Equal(500m, (await assets.GetAsync(clientId, a.Id, default))!.AccumulatedDepreciation);

        DepreciationRun voided = await svc.VoidRunAsync(clientId, run.Id, "recover", default);
        Assert.Equal(DepreciationRunStatus.Voided, voided.Status);
        Assert.Equal(0m, (await assets.GetAsync(clientId, a.Id, default))!.AccumulatedDepreciation); // rolled back
    }
}

/// <summary>An accounts provider that always throws — to prove RunDepreciationAsync resolves accounts
/// before any persistence/mutation.</summary>
internal sealed class ThrowingAccountsProvider : IFixedAssetsAccountsProvider
{
    public Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        throw new InvalidOperationException("Fixed-assets posting account is not configured.");
}
```

> Implementer note: `FakeLedgerClient` (in `Fakes.cs`) returns entries by source ref from a dictionary populated on `PostAsync`. Add a `public bool ReturnNoEntries { get; set; }` property; when true, `GetEntriesBySourceRefAsync` returns an empty list (simulating a post that never landed). Do not change its other behavior. `InMemoryAssetStore.MarkDisposedAsync` was already added in Task 4 (it sets `Status = AssetStatus.Disposed` and keeps the asset in the active/reference list) — the run-exclusion test relies on that, so no further fake change is needed for it.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FixedAssetsRunServiceFa3Tests`
Expected: FAIL — disposed assets currently depreciate; accounts are resolved late; void throws on a missing entry.

- [ ] **Step 3: Edit the run service**

In `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsRunService.cs`:

- Add the `Disposed` exclusion in the eligibility loop (inside `RunDepreciationAsync`, first line of the `foreach`):

```csharp
        foreach (Asset asset in await ActiveAssetsAsync(clientId, ct))
        {
            if (asset.Status != AssetStatus.Active) continue; // disposed assets don't depreciate
            if (!period.OnOrAfterServiceMonth(asset.InServiceDate)) continue;
            decimal amount = methods.For(asset.Method).DepreciationForPeriod(asset);
            if (amount > 0m) lines.Add(new DepreciationRunLine(asset.Id, amount));
        }
```

- Resolve accounts BEFORE persistence. Move the `GetAccountsAsync` call up to right after the nothing-to-depreciate guard, and use the resolved `postingAccounts` at compose time:

```csharp
        // 3. Nothing to depreciate → 422 (no doc, no entry).
        if (lines.Count == 0)
            throw new ArgumentException($"No assets to depreciate for {period.Year}-{period.Month:D2}.");

        // 4. Resolve posting accounts BEFORE any persistence — a config error must fail before side effects.
        FixedAssetsPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);

        decimal total = lines.Sum(l => l.Amount);
        DateOnly effectiveDate = request.EffectiveDate ?? period.LastDay();

        // 5. Persist the evidentiary run.
        DepreciationRun run = await runs.RecordAsync(clientId,
            new DepreciationRunBody(period, effectiveDate, request.Memo, lines, total), ct);

        // 6. Advance each asset's accumulated depreciation.
        await assets.ApplyDepreciationAsync(clientId, lines, ct);

        // 7. Compose + post one PendingApproval aggregate entry.
        PostEntryRequest entry = FixedAssetsPosting.ComposeDepreciationRun(run.Id, total, effectiveDate, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return run;
```

(Delete the old step-6 `GetAccountsAsync` line so it is resolved only once, up front.)

- Make `VoidRunAsync` tolerate a missing spawned entry — replace the `?? throw` block with a null-tolerant one:

```csharp
        // Reverse the posted entry (or withdraw it if still pending). Tolerate a missing entry — a run
        // stranded by a failed post has no entry, but must still be recoverable: roll back + void the doc.
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, runId, ct);
        EntryResponse? entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });
        if (entry is not null)
        {
            if (entry.Posting == "Posted")
                await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(run.EffectiveDate, reason ?? $"Voided depreciation run {runId}"), ct);
            else
                await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided depreciation run {runId}"), ct);
        }

        // Roll each asset's accumulated depreciation back, then void the doc.
        await assets.ReverseDepreciationAsync(clientId, run.Lines, ct);
        await runs.VoidAsync(clientId, runId, ct);
        return (await runs.GetAsync(clientId, runId, ct))!;
```

- [ ] **Step 4: Add the fake changes**

In `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs`: add `public bool ReturnNoEntries { get; set; }` to `FakeLedgerClient` and honor it in `GetEntriesBySourceRefAsync` (empty list when true). Ensure `InMemoryAssetStore.MarkDisposedAsync` sets the asset's `Status` to `Disposed` (add it if missing, per the Task-4 `IAssetStore` shape) and that `GetByClientPagedAsync` still returns disposed assets in the active list (they are reference-Active — the run service's own `Status` filter excludes them, not the store).

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter "FixedAssetsRunServiceFa3Tests|FixedAssetsRunServiceTests"`
Expected: PASS — the three new tests, and the FA-2 run-service tests still green (the account-resolution reorder and void-tolerance don't change their outcomes). Then run the whole module suite once and report the count.

- [ ] **Step 6: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsRunService.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsRunServiceFa3Tests.cs
git commit -m "feat(fixedassets): exclude disposed assets from runs + FA-2 run recovery fix

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Disposal orchestration service (unit-tested with fakes)

`FixedAssetsDisposalService` composes the whole disposal: validate → resolve accounts → compute catch-up + gain/loss → persist → stamp Disposed → post; and void → reverse (tolerating a missing entry) → reinstate → void doc. Unit-tested with the in-memory fakes.

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalService.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs` (add `InMemoryDisposalStore`)
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsDisposalServiceTests.cs`

**Interfaces:**
- Consumes: `IAssetStore` (incl. `MarkDisposedAsync`/`ReinstateAsync`/`GetAsync`), `IDisposalStore`, `DepreciationMethodSelector`, `IFixedAssetsAccountsProvider`, `ILedgerClient`, `DepreciationSchedule`, `FixedAssetsDisposalPosting`, `AssetStatus`.
- Produces:
  - `sealed record DisposeRequest(DateOnly DisposalDate, decimal Proceeds, string? Memo)`.
  - `sealed class FixedAssetsDisposalService(IAssetStore assets, IDisposalStore disposals, DepreciationMethodSelector methods, IFixedAssetsAccountsProvider accounts, ILedgerClient ledger)` with:
    - `Task<Disposal> DisposeAsync(Guid clientId, Guid assetId, DisposeRequest request, CancellationToken ct = default)`
    - `Task<Disposal> VoidDisposalAsync(Guid clientId, Guid disposalId, string? reason, CancellationToken ct = default)`
    - `Task<Disposal?> GetDisposalAsync(Guid clientId, Guid disposalId, CancellationToken ct = default)`

- [ ] **Step 1: Write the failing tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsDisposalServiceTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsDisposalServiceTests
{
    private static (FixedAssetsDisposalService svc, InMemoryAssetStore assets, InMemoryDisposalStore disposals, FakeLedgerClient ledger)
        Build()
    {
        InMemoryAssetStore assets = new();
        InMemoryDisposalStore disposals = new();
        FakeLedgerClient ledger = new();
        FixedAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        return (new FixedAssetsDisposalService(assets, disposals, selector, accounts, ledger), assets, disposals, ledger);
    }

    private static AssetBody Sl(decimal cost, int life, DateOnly inService) =>
        new("SL", cost, inService, life, 0m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Dispose_catches_up_depreciation_computes_gain_advances_asset_and_posts_one_entry()
    {
        (FixedAssetsDisposalService svc, InMemoryAssetStore assets, _, FakeLedgerClient ledger) = Build();
        Guid clientId = Guid.NewGuid();
        // 12000 cost, 24mo, 500/mo. In service Jan 2026, dispose Jun 2026 → 5 months → target accum 2500.
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);

        Disposal d = await svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 6, 30), 10000m, "sold"), default);

        Assert.Equal(2500m, d.CatchUpDepreciation);
        Assert.Equal(0m, d.AccumulatedBeforeDisposal);
        Assert.Equal(2500m, d.AccumulatedAtDisposal);
        Assert.Equal(9500m, d.NetBookValue);           // 12000 - 2500
        Assert.Equal(500m, d.GainLoss);                // 10000 - 9500 = gain 500
        Asset after = (await assets.GetAsync(clientId, a.Id, default))!;
        Assert.Equal(AssetStatus.Disposed, after.Status);
        Assert.Equal(2500m, after.AccumulatedDepreciation);
        Assert.Single(ledger.Posted);
        Assert.Equal("Disposal", ledger.Posted[0].SourceType);
    }

    [Fact]
    public async Task Disposing_a_non_active_asset_is_rejected()
    {
        (FixedAssetsDisposalService svc, InMemoryAssetStore assets, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        await svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 6, 30), 1000m, null), default);
        // Second dispose → already disposed.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 7, 31), 1000m, null), default));
    }

    [Fact]
    public async Task Disposal_date_before_in_service_is_rejected()
    {
        (FixedAssetsDisposalService svc, InMemoryAssetStore assets, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 6, 1)), default);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 1, 1), 1000m, null), default));
    }

    [Fact]
    public async Task Void_reverses_the_entry_reinstates_the_asset_and_rolls_accumulated_back()
    {
        (FixedAssetsDisposalService svc, InMemoryAssetStore assets, _, FakeLedgerClient ledger) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        Disposal d = await svc.DisposeAsync(clientId, a.Id, new DisposeRequest(new DateOnly(2026, 6, 30), 10000m, null), default);

        Disposal voided = await svc.VoidDisposalAsync(clientId, d.Id, "unwind", default);
        Assert.Equal(DisposalStatus.Voided, voided.Status);
        Assert.True(ledger.ReversedOrWithdrawn);
        Asset after = (await assets.GetAsync(clientId, a.Id, default))!;
        Assert.Equal(AssetStatus.Active, after.Status);        // reinstated
        Assert.Equal(0m, after.AccumulatedDepreciation);       // rolled back to pre-disposal
    }
}
```

> Implementer note: add `InMemoryDisposalStore : IDisposalStore` to `Fakes.cs` — a `List<Disposal>` with `RecordAsync` (assign `DP-#####` + `Posted`), `VoidAsync` (flip `Voided`), `GetAsync`, `GetByClientPagedAsync`, `GetActiveByAssetAsync` (first non-voided with matching `AssetId`). Mirror the existing `InMemoryDepreciationRunStore`. `FakeLedgerClient` already exposes `Posted` and `ReversedOrWithdrawn` (the latter added conceptually in FA-2 / confirm it exists; if not, add a bool set by `ReverseAsync`/`VoidAsync`).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FixedAssetsDisposalServiceTests`
Expected: FAIL to compile — `FixedAssetsDisposalService`, `DisposeRequest`, `InMemoryDisposalStore` don't exist.

- [ ] **Step 3: Write the service**

Create `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalService.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Orchestrates a disposal: validate, resolve accounts, catch depreciation up to the disposal
/// month, compute gain/loss vs net book value, persist the evidentiary disposal, stamp the asset
/// Disposed, and post one PendingApproval GL entry. Void reverses the entry (tolerating a missing one),
/// reinstates the asset to its pre-disposal accumulated depreciation, and voids the doc. The module never
/// self-approves.</summary>
public sealed class FixedAssetsDisposalService(
    IAssetStore assets,
    IDisposalStore disposals,
    DepreciationMethodSelector methods,
    IFixedAssetsAccountsProvider accounts,
    ILedgerClient ledger)
{
    public async Task<Disposal> DisposeAsync(Guid clientId, Guid assetId, DisposeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Proceeds < 0m)
            throw new ArgumentException("Proceeds cannot be negative.", nameof(request));

        // 1. Load + guard.
        Asset asset = await assets.GetAsync(clientId, assetId, ct)
            ?? throw new InvalidOperationException($"Asset {assetId} not found or not disposable.");
        if (asset.Status != AssetStatus.Active)
            throw new InvalidOperationException($"Asset {assetId} is {asset.Status}; only an active asset can be disposed.");
        if (request.DisposalDate < asset.InServiceDate)
            throw new ArgumentException("Disposal date is before the asset's in-service date.", nameof(request));
        if (await disposals.GetActiveByAssetAsync(clientId, assetId, ct) is not null)
            throw new InvalidOperationException($"Asset {assetId} already has an active disposal.");

        // 2. Resolve accounts BEFORE any persistence.
        FixedAssetsPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);

        // 3. Compute catch-up + gain/loss.
        IDepreciationMethod method = methods.For(asset.Method);
        int targetMonths = DepreciationSchedule.TargetMonths(asset, request.DisposalDate);
        decimal targetAccumulated = DepreciationSchedule.AccumulatedAfter(method, asset, targetMonths);
        decimal currentAccumulated = asset.AccumulatedDepreciation;
        decimal catchUp = Math.Max(0m, targetAccumulated - currentAccumulated);
        decimal finalAccumulated = currentAccumulated + catchUp;
        decimal nbv = asset.AcquisitionCost - finalAccumulated;
        decimal gainLoss = request.Proceeds - nbv;

        // 4. Persist the evidentiary disposal.
        Disposal disposal = await disposals.RecordAsync(clientId, new DisposalBody(
            assetId, request.DisposalDate, request.Proceeds, catchUp, currentAccumulated, finalAccumulated, nbv, gainLoss, request.Memo), ct);

        // 5. Stamp the asset Disposed with its final accumulated depreciation.
        DisposeStamp stamp = await assets.MarkDisposedAsync(clientId, assetId, finalAccumulated, ct);
        if (stamp.Outcome != DisposeOutcome.Disposed)
            throw new InvalidOperationException($"Asset {assetId} could not be disposed ({stamp.Outcome}).");

        // 6. Compose + post one PendingApproval entry.
        PostEntryRequest entry = FixedAssetsDisposalPosting.ComposeDisposal(
            disposal.Id, request.DisposalDate, asset.AcquisitionCost, currentAccumulated, catchUp, request.Proceeds, gainLoss, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return disposal;
    }

    public async Task<Disposal> VoidDisposalAsync(Guid clientId, Guid disposalId, string? reason, CancellationToken ct = default)
    {
        Disposal disposal = await disposals.GetAsync(clientId, disposalId, ct)
            ?? throw new InvalidOperationException($"Disposal {disposalId} not found.");
        if (disposal.Status != DisposalStatus.Posted)
            throw new InvalidOperationException($"Only a posted disposal can be voided; {disposalId} is {disposal.Status}.");

        // Reverse the posted entry (or withdraw if still pending); tolerate a missing entry.
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, disposalId, ct);
        EntryResponse? entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });
        if (entry is not null)
        {
            if (entry.Posting == "Posted")
                await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(disposal.DisposalDate, reason ?? $"Voided disposal {disposalId}"), ct);
            else
                await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided disposal {disposalId}"), ct);
        }

        // Reinstate the asset to its pre-disposal accumulated depreciation, then void the doc.
        await assets.ReinstateAsync(clientId, disposal.AssetId, disposal.AccumulatedBeforeDisposal, ct);
        await disposals.VoidAsync(clientId, disposalId, ct);
        return (await disposals.GetAsync(clientId, disposalId, ct))!;
    }

    public Task<Disposal?> GetDisposalAsync(Guid clientId, Guid disposalId, CancellationToken ct = default) =>
        disposals.GetAsync(clientId, disposalId, ct);
}

/// <summary>Input for disposing an asset — the caller supplies the disposal date, proceeds (0 = retirement),
/// and an optional memo; amounts are server-computed.</summary>
public sealed record DisposeRequest(DateOnly DisposalDate, decimal Proceeds, string? Memo);
```

- [ ] **Step 4: Add the fake disposal store**

Add `InMemoryDisposalStore : IDisposalStore` to `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs`, mirroring `InMemoryDepreciationRunStore` (incrementing `DP-#####`, `Posted`/`Voided`, `GetActiveByAssetAsync` = first non-voided with matching `AssetId`).

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FixedAssetsDisposalServiceTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsDisposalService.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsDisposalServiceTests.cs
git commit -m "feat(fixedassets): disposal orchestration service (catch-up, gain/loss, void)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: Host wiring + disposal endpoints + HTTP E2E

Register the disposal store + service and the `disposals` manifest, expose the endpoints, and prove disposals end-to-end through the real host.

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Api/DisposalRequests.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsServiceExtensions.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DisposalE2eTests.cs`

**Interfaces:**
- Consumes: `AddModule` manifest builder, `FixedAssetsDisposalService`, `IDisposalStore`/`DocumentDisposalStore`, `DisposeRequest`, `VoidReasonRequest` (existing Api DTO), the disposal endpoints, `FixedAssetsHostFixture` (now with the four disposal accounts).
- Produces: `sealed record DisposeAssetRequest(DateOnly DisposalDate, decimal Proceeds, string? Memo)` with `DisposeRequest ToRequest()`; the expanded `AddFixedAssets`; the four disposal endpoints.

- [ ] **Step 1: Write the request DTO**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Api/DisposalRequests.cs`:

```csharp
using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Api;

/// <summary>Dispose an asset. Proceeds of 0 is a retirement/scrap; amounts (catch-up, gain/loss) are
/// server-computed.</summary>
public sealed record DisposeAssetRequest(DateOnly DisposalDate, decimal Proceeds, string? Memo)
{
    public DisposeRequest ToRequest() => new(DisposalDate, Proceeds, Memo);
}
```

- [ ] **Step 2: Expand AddFixedAssets**

Edit `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsServiceExtensions.cs`:
- Add the disposals manifest line inside the `AddModule` manifest lambda:

```csharp
            manifest.Reference("assets");
            manifest.Evidentiary("depreciation-runs");
            manifest.Evidentiary("disposals");
```

- Register the disposal store + service (next to the run store/service):

```csharp
        services.AddScoped<IDisposalStore>(sp =>
            new DocumentDisposalStore(sp.GetRequiredKeyedService<IDocumentStore>("fixedassets")));
        services.AddScoped<FixedAssetsDisposalService>();
```

- [ ] **Step 3: Map the disposal endpoints**

Edit `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`:
- Register the routes in `MapFixedAssetsEndpoints` (after the depreciation-run routes):

```csharp
        clients.MapPost("/assets/{assetId:guid}/dispose", DisposeAsset);
        clients.MapPost("/disposals/{disposalId:guid}/void", VoidDisposal);
        clients.MapGet("/disposals/{disposalId:guid}", GetDisposal);
        clients.MapGet("/disposals", ListDisposals);
```

- Add the handlers (mirror the depreciation-run handlers; `IDisposalStore` injected for the list):

```csharp
    private static async Task<IResult> DisposeAsset(
        Guid clientId, Guid assetId, DisposeAssetRequest request, FixedAssetsDisposalService service, CancellationToken cancellationToken)
    {
        try
        {
            Disposal disposal = await service.DisposeAsync(clientId, assetId, request.ToRequest(), cancellationToken);
            return Results.Created($"/clients/{clientId}/disposals/{disposal.Id}", new DisposalView(disposal));
        }
        catch (InvalidOperationException ex) // not found / not active / already disposed
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex) // negative proceeds / date before in-service
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidDisposal(
        Guid clientId, Guid disposalId, VoidReasonRequest? request, FixedAssetsDisposalService service, CancellationToken cancellationToken)
    {
        try
        {
            Disposal voided = await service.VoidDisposalAsync(clientId, disposalId, request?.Reason, cancellationToken);
            return Results.Ok(new DisposalView(voided));
        }
        catch (InvalidOperationException ex) // not found / not posted
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetDisposal(
        Guid clientId, Guid disposalId, FixedAssetsDisposalService service, CancellationToken cancellationToken)
    {
        Disposal? disposal = await service.GetDisposalAsync(clientId, disposalId, cancellationToken);
        return disposal is null ? Results.NotFound() : Results.Ok(new DisposalView(disposal));
    }

    private static async Task<IResult> ListDisposals(
        Guid clientId, int? skip, int? limit, string? order, bool? includeVoided,
        IDisposalStore store, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        PagedResponse<Disposal> page = await store.GetByClientPagedAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeVoided ?? false, cancellationToken);
        return Results.Ok(new PagedResponse<DisposalView>(
            page.Items.Select(d => new DisposalView(d)).ToList(), page.Total, page.Skip, page.Limit));
    }
```

> Note: `VoidReasonRequest` already exists in the Api project (used by depreciation-run void) — reuse it, do not redefine.

- [ ] **Step 4: Write the E2E tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DisposalE2eTests.cs`. Read the FA-2 `DepreciationRunE2eTests.cs` for the exact chart-seeding helper (`PUT /clients/{id}/accounts/{accountId}` with an anonymous `{Number,Name,Type,RequiredDimension}`), the asset-create helper, and the capability/entitlement seeding — reuse them. Seed the six posting accounts (the four disposal accounts as: AssetCost = "Asset" type, DisposalProceeds = "Asset" (cash), GainOnDisposal = "Revenue", LossOnDisposal = "Expense"; plus DepreciationExpense/AccumulatedDepreciation from FA-2). Tests:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

/// <summary>Proves disposals end-to-end through the real host: a sale posts one balanced PendingApproval
/// entry via fixedassets, the asset goes Disposed with its final accumulated depreciation, disposed assets
/// are excluded from depreciation runs and frozen against edits, and a void reverses the entry and
/// reinstates the asset.</summary>
public sealed class DisposalE2eTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        await PutAccountAsync(http, clientId, fixture.DepreciationExpenseAccountId,     "6200", "Depreciation Expense",     "Expense");
        await PutAccountAsync(http, clientId, fixture.AccumulatedDepreciationAccountId, "1590", "Accumulated Depreciation", "Asset");
        await PutAccountAsync(http, clientId, fixture.AssetCostAccountId,               "1500", "Fixed Assets",             "Asset");
        await PutAccountAsync(http, clientId, fixture.DisposalProceedsAccountId,        "1000", "Cash",                     "Asset");
        await PutAccountAsync(http, clientId, fixture.GainOnDisposalAccountId,          "7100", "Gain on Disposal",         "Revenue");
        await PutAccountAsync(http, clientId, fixture.LossOnDisposalAccountId,          "7200", "Loss on Disposal",         "Expense");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new { Number = number, Name = name, Type = type, RequiredDimension = (string?)null })).EnsureSuccessStatusCode();

    private static async Task<AssetView> CreateAssetAsync(HttpClient http, Guid clientId, SaveAssetRequest req) =>
        (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets", req)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<AssetView>())!;

    [Fact]
    public async Task Sale_disposes_the_asset_and_posts_one_balanced_pending_entry_via_fixedassets()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        // Dispose Jun 2026: 5 months catch-up = 2500; NBV 9500; proceeds 10000 → gain 500.
        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 6, 30), 10000m, "sold"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DisposalView disposal = (await created.Content.ReadFromJsonAsync<DisposalView>())!;
        Assert.Equal(500m, disposal.Disposal.GainLoss);

        // Asset is Disposed with final accumulated.
        AssetView after = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{asset.Asset.Id}"))!;
        Assert.Equal(AssetStatus.Disposed, after.Asset.Status);
        Assert.Equal(2500m, after.Asset.AccumulatedDepreciation);

        // One balanced PendingApproval entry via fixedassets.
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disposal.Disposal.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal("fixedassets", entry.ViaModule);
        Assert.Equal("PendingApproval", entry.Posting);
        decimal debits = entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount);
        decimal credits = entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount);
        Assert.Equal(debits, credits);
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == fixture.GainOnDisposalAccountId && l.Direction == "Credit").Amount);
    }

    [Fact]
    public async Task Retirement_with_zero_proceeds_posts_a_loss()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Rig", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        // Dispose same in-service month → 0 catch-up, NBV 12000, proceeds 0 → loss 12000.
        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 1, 15), 0m, "scrapped"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DisposalView disposal = (await created.Content.ReadFromJsonAsync<DisposalView>())!;
        Assert.Equal(-12000m, disposal.Disposal.GainLoss);

        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disposal.Disposal.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal(12000m, entry.Lines.Single(l => l.AccountId == fixture.LossOnDisposalAccountId && l.Direction == "Debit").Amount);
        Assert.DoesNotContain(entry.Lines, l => l.AccountId == fixture.DisposalProceedsAccountId);
    }

    [Fact]
    public async Task A_disposed_asset_is_excluded_from_a_depreciation_run_and_frozen_against_edits()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        (await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 3, 31), 5000m, null))).EnsureSuccessStatusCode();

        // A run for a period with only this (now disposed) asset → 422 nothing to depreciate.
        HttpResponseMessage run = await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 4, null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, run.StatusCode);

        // Edit / deactivate a disposed asset → 409.
        HttpResponseMessage edit = await http.PutAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}",
            new SaveAssetRequest("Van 2", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        HttpResponseMessage deact = await http.PostAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/deactivate", null);
        Assert.Equal(HttpStatusCode.Conflict, deact.StatusCode);
    }

    [Fact]
    public async Task Re_dispose_is_rejected_and_void_reinstates_the_asset()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView asset = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        DisposalView disposal = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 6, 30), 10000m, null))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DisposalView>())!;

        // Re-dispose → 409.
        HttpResponseMessage second = await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 7, 31), 1000m, null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Void → asset back to Active with accumulated restored, entry reversed.
        HttpResponseMessage voided = await http.PostAsJsonAsync(
            $"/clients/{clientId}/disposals/{disposal.Disposal.Id}/void", new VoidReasonRequest("unwind"));
        voided.EnsureSuccessStatusCode();
        AssetView after = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{asset.Asset.Id}"))!;
        Assert.Equal(AssetStatus.Active, after.Asset.Status);
        Assert.Equal(0m, after.Asset.AccumulatedDepreciation);

        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disposal.Disposal.Id}"))!;
        Assert.Contains(entries, e => e.Status == "Voided" || e.ReversalOf != null);

        // Now disposable again.
        HttpResponseMessage redispose = await http.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 8, 31), 2000m, null));
        Assert.Equal(HttpStatusCode.Created, redispose.StatusCode);
    }

    [Fact]
    public async Task A_member_without_write_cannot_dispose_and_an_unentitled_client_is_forbidden()
    {
        (Guid clientId, HttpClient controller) = await fixture.SeedClientAsync();
        await SetUpChartAsync(controller, clientId);
        AssetView asset = await CreateAssetAsync(controller, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        // Read-only Auditor on the SAME client → 403 (reuse the FA-2 capability seeding pattern).
        Guid auditorUserId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(auditorUserId, clientId, Accounting101.Ledger.Api.Control.LedgerRole.Auditor);
        HttpClient auditor = fixture.ClientFor(auditorUserId, "Acme Auditor", Accounting101.Ledger.Api.Control.LedgerRole.Auditor);
        HttpResponseMessage denied = await auditor.PostAsJsonAsync($"/clients/{clientId}/assets/{asset.Asset.Id}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 6, 30), 1000m, null));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Unentitled client → 403.
        (Guid noModuleClient, HttpClient noModule) = await fixture.SeedClientAsync(enabledModules: []);
        HttpResponseMessage ent = await noModule.PostAsJsonAsync($"/clients/{noModuleClient}/assets/{Guid.NewGuid()}/dispose",
            new DisposeAssetRequest(new DateOnly(2026, 6, 30), 1000m, null));
        Assert.Equal(HttpStatusCode.Forbidden, ent.StatusCode);
    }
}
```

> Implementer note: match the exact capability/entitlement seeding the FA-2 `DepreciationRunE2eTests` uses (read it) — the `AddMembershipAsync` + `ClientFor` calls above mirror it, but confirm the method signatures against the current `FixedAssetsHostFixture` and adjust if they differ. The disposed-asset run-exclusion test relies on the disposal advancing accumulated so the asset is both `Disposed` (filtered) — the run returns 422 because it's the only asset.

- [ ] **Step 5: Run the module suite + confirm slnx/Program.cs untouched**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS — all FA-1/FA-2/FA-3 tests. Report the count.
Run: `git status --short Accounting101.slnx Accounting101.Host/Program.cs`
Expected: no output (both unchanged).

- [ ] **Step 6: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets.Api/DisposalRequests.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsServiceExtensions.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/DisposalE2eTests.cs
git commit -m "feat(fixedassets): host wiring + disposal endpoints + HTTP E2E

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 8: Whole-solution green + whole-branch review + memory

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build Accounting101.slnx -c Debug --nologo`
Expected: Build succeeded, 0 errors (pre-existing NuGet advisory warnings on `Accounting101.TestSupport` are unrelated and allowed).

- [ ] **Step 2: Run the whole solution suite**

Run: `dotnet test Accounting101.slnx -c Debug --no-build --nologo`
Expected: all assemblies green. Baseline was 938; FA-3 adds ~30 tests. Report the total.

- [ ] **Step 3: Whole-branch review**

Dispatch the whole-branch reviewer (opus) over `master..HEAD`. Focus: the disposal entry balances for gain/loss/retirement; the catch-up schedule is deterministic and never over-depreciates; `AccumulatedDepreciation`/`Status` server-owned integrity through dispose + void (never client-settable, roll-back exact); disposed assets excluded from runs and frozen; the folded-in FA-2 fix (accounts-before-apply + void-tolerates-missing-entry) is correct and didn't regress FA-2; the account-record widening updated all construction sites; slnx/Program.cs untouched; scope discipline (no UI, no partial-month, no bulk). Address Critical/Important before merge; record Minor/Nit as deferred.

- [ ] **Step 4: Merge, push, delete branch, update memory**

After review passes: merge `feat/fixed-assets-fa3` into `master` with `--no-ff`, push, delete the branch, and update the FA memory file (`accounting101-fixed-assets-fa1.md` → note FA-3 shipped, mark FA-2 deferral #1 resolved, list any new deferrals) + the `MEMORY.md` pointer.

---

## Self-Review

**Spec coverage:**
- Auto-catch-up schedule (deterministic iteration, full-month convention, disposal month excluded) → Task 1. ✓
- Six-account posting set + disposal domain types + combined balanced recipe (gain/loss/retirement, omit zero lines) → Task 2. ✓
- Evidentiary `disposals` store + `GetActiveByAssetAsync` (unbounded) → Task 3. ✓
- Asset disposed-lifecycle (`MarkDisposedAsync`/`ReinstateAsync`) + frozen guards on update/deactivate/reactivate → Task 4. ✓
- Disposed assets excluded from runs + folded-in FA-2 fix (accounts-before-apply, void-tolerates-missing-entry) → Task 5. ✓
- Disposal orchestration (validate → resolve accounts → compute → persist → stamp → post; void → reverse → reinstate → void doc) → Task 6 (unit) + Task 7 (E2E). ✓
- Endpoints (dispose/void/get/list) + host wiring (`disposals` manifest + store + service) + config keys → Task 7. ✓
- Capability/entitlement on the new endpoints → Task 7 E2E. ✓
- Whole-solution green + review + memory → Task 8. ✓

**Type consistency:** `FixedAssetsPostingAccounts` gains four ids in Task 2; every construction site (provider, host fixture, `FixedAssetsPostingTests`, `Fakes.cs`) is updated in the same task. `DisposalBody`/`Disposal` (Task 2) flow through the store (Task 3), the service (Task 6), and the E2E (Task 7). `DisposeStamp`/`DisposeOutcome` + `UpdateResult.Disposed` + `DeactivateResult.Disposed`/`ReactivateResult.Disposed` (Task 4) are consumed by the endpoints (Task 4) and the disposal service (Task 6). `DepreciationSchedule.{MonthsBetween,TargetMonths,AccumulatedAfter}` (Task 1) is consumed by `FixedAssetsDisposalService` (Task 6). `FixedAssetsDisposalPosting.ComposeDisposal(Guid,DateOnly,decimal,decimal,decimal,decimal,decimal,string?,FixedAssetsPostingAccounts)` (Task 2) is called with those exact args in the service (Task 6). `DisposeRequest` (domain, Task 6) ← `DisposeAssetRequest.ToRequest()` (Api, Task 7). The run-service `AssetStatus.Active` filter + FA-2-fix edits (Task 5) modify the existing `FixedAssetsRunService` without changing its public signature.

**Placeholder scan:** the "Implementer note" callouts (reuse `FixedActor`; grep all `FixedAssetsPostingAccounts` construction sites; add `FakeLedgerClient.ReturnNoEntries` + `InMemoryDisposalStore`; match FA-2's capability seeding) are concrete instructions pointing at named files/members, not deferred work. All code steps carry complete code. No "TBD"/"handle edge cases"/"similar to Task N". The one behavioral edge deliberately accepted (a reference-*deactivated* asset with `Status == Active` could still be disposed, since `MarkDisposedAsync` guards on business `Status`, not document lifecycle) is negligible and documented in the spec — not a gap.
