# Fixed Assets FA-2 — Depreciation Runs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add period depreciation runs to the Fixed Assets module — a pluggable depreciation strategy, an evidentiary depreciation-run document that posts one aggregate GL entry, a LIFO void that rolls accumulated depreciation back, and sticky deactivation with an explicit reactivate.

**Architecture:** Extends the existing `Modules/FixedAssets/` module (domain + `.Api` + tests). Pure depreciation math (`IDepreciationMethod` + two implementations) and the pure posting recipe (`FixedAssetsPosting`) are unit-tested in isolation. An evidentiary `depreciation-runs` document store mirrors `DocumentPayrollRunStore`. `FixedAssetsRunService` orchestrates: compute → persist run → advance each asset's accumulated depreciation → post one PendingApproval entry through the `ILedgerClient` seam (the module never self-approves). Void is LIFO. The whole module posts through the engine exactly like Payroll.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB via the engine document store, xUnit + EphemeralMongo (`SharedMongo`) + `WebApplicationFactory<Program>` for HTTP E2E.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-07-04-fixed-assets-fa2-depreciation-runs-design.md` — every task implements part of it.
- **Base commit:** branch off `master` at the FA-1 merge (`228abd7`) into a new branch `feat/fixed-assets-fa2`.
- **Money is `decimal`.** Never `double`/`float`. Round depreciation amounts to cents with `Math.Round(x, 2, MidpointRounding.ToEven)`.
- **Stage explicit paths only — NEVER `git add -A` / `git add .`.** Each commit lists its exact files.
- **Leave pre-existing uncommitted working-tree noise untouched:** `UI/Angular/src/app/core/api/environment.ts` and the several `*.Tests.csproj` files that show as modified. Do not stage or revert them.
- **Commit trailer, exactly:** `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Module never self-approves.** All GL posts are `PendingApproval`; the module posts, an Approver approves out of band.
- **No hardcoded account GUIDs.** Posting accounts come from configuration (`FixedAssets:Accounts:DepreciationExpense` / `:AccumulatedDepreciation`).
- **Module runner:** `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`. EphemeralMongo can flake on mongod startup under load — a startup timeout is not a real failure; retry once.
- **Whole-solution baseline:** 890/890 before this branch. It must stay green at each task and end green.

**Namespaces:** domain types live in `Accounting101.FixedAssets` (project `Modules/FixedAssets/Accounting101.FixedAssets`, references only `Accounting101.Ledger.Contracts`); web/DI types live in `Accounting101.FixedAssets.Api` (project `Modules/FixedAssets/Accounting101.FixedAssets.Api`, references `Ledger.Api` + `Ledger.Contracts`); tests live in `Accounting101.FixedAssets.Tests`.

---

## Task 1: Pluggable depreciation strategy (pure domain)

The depreciation math, isolated and unit-tested with no Mongo and no host. Full-month convention means each eligible period is one uniform month, so the method needs only the asset's current stored state.

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/IDepreciationMethod.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/StraightLineDepreciation.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DecliningBalanceDepreciation.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationMethodSelector.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationMethodTests.cs`

**Interfaces:**
- Consumes: `Asset` (`Accounting101.FixedAssets`, existing — has `AcquisitionCost`, `SalvageValue`, `UsefulLifeMonths`, `AccumulatedDepreciation`, `Method`, `DecliningBalanceFactor`), `DepreciationMethod` enum (existing: `StraightLine = 0`, `DecliningBalance = 1`).
- Produces:
  - `interface IDepreciationMethod { DepreciationMethod Method { get; } decimal DepreciationForPeriod(Asset asset); }`
  - `sealed class StraightLineDepreciation : IDepreciationMethod`
  - `sealed class DecliningBalanceDepreciation : IDepreciationMethod`
  - `sealed class DepreciationMethodSelector` with `IDepreciationMethod For(DepreciationMethod method)` — later tasks resolve the strategy through this.

- [ ] **Step 1: Write the failing tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationMethodTests.cs`:

```csharp
namespace Accounting101.FixedAssets.Tests;

public sealed class DepreciationMethodTests
{
    private static Asset Make(decimal cost, decimal salvage, int lifeMonths, decimal accumulated,
        DepreciationMethod method, decimal? factor = null) => new()
    {
        Id = Guid.NewGuid(),
        Description = "test",
        AcquisitionCost = cost,
        InServiceDate = new DateOnly(2026, 1, 1),
        UsefulLifeMonths = lifeMonths,
        SalvageValue = salvage,
        Method = method,
        DecliningBalanceFactor = factor,
        Status = AssetStatus.Active,
        AccumulatedDepreciation = accumulated,
    };

    // ── Straight line ────────────────────────────────────────────────────────

    [Fact]
    public void StraightLine_uniform_monthly_amount()
    {
        // (12000 - 0) / 24 = 500/mo
        IDepreciationMethod sut = new StraightLineDepreciation();
        Assert.Equal(DepreciationMethod.StraightLine, sut.Method);
        Asset a = Make(12000m, 0m, 24, accumulated: 0m, DepreciationMethod.StraightLine);
        Assert.Equal(500m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void StraightLine_honors_salvage_in_the_base()
    {
        // (12000 - 2400) / 24 = 400/mo
        IDepreciationMethod sut = new StraightLineDepreciation();
        Asset a = Make(12000m, 2400m, 24, accumulated: 0m, DepreciationMethod.StraightLine);
        Assert.Equal(400m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void StraightLine_final_period_takes_the_exact_remainder()
    {
        // base 1000, monthly ~ 333.33; after 999.99 accumulated, remainder is 0.01
        IDepreciationMethod sut = new StraightLineDepreciation();
        Asset a = Make(1000m, 0m, 3, accumulated: 999.99m, DepreciationMethod.StraightLine);
        Assert.Equal(0.01m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void StraightLine_returns_zero_when_fully_depreciated()
    {
        IDepreciationMethod sut = new StraightLineDepreciation();
        Asset a = Make(12000m, 2400m, 24, accumulated: 9600m, DepreciationMethod.StraightLine);
        Assert.Equal(0m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void StraightLine_rounds_to_cents()
    {
        // 1000 / 3 = 333.333... -> 333.33
        IDepreciationMethod sut = new StraightLineDepreciation();
        Asset a = Make(1000m, 0m, 3, accumulated: 0m, DepreciationMethod.StraightLine);
        Assert.Equal(333.33m, sut.DepreciationForPeriod(a));
    }

    // ── Declining balance ────────────────────────────────────────────────────

    [Fact]
    public void DecliningBalance_first_period_is_nbv_times_rate()
    {
        // rate = 2.0/24 = 0.08333...; nbv 12000; 12000 * 0.083333 = 1000.00
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Assert.Equal(DepreciationMethod.DecliningBalance, sut.Method);
        Asset a = Make(12000m, 0m, 24, accumulated: 0m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(1000m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void DecliningBalance_second_period_uses_reduced_book_value()
    {
        // after 1000 accumulated, nbv 11000; 11000 * 0.083333 = 916.67
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 0m, 24, accumulated: 1000m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(916.67m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void DecliningBalance_never_depreciates_below_salvage()
    {
        // nbv 1100, salvage 1000 -> floor remaining is 100; raw would be 1100*0.0833=91.67 < 100, so 91.67
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 1000m, 24, accumulated: 10900m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(91.67m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void DecliningBalance_crossing_period_takes_exact_remainder_to_salvage()
    {
        // nbv 1050, salvage 1000 -> floor remaining 50; raw 1050*0.0833=87.5 > 50, so take 50
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 1000m, 24, accumulated: 10950m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(50m, sut.DepreciationForPeriod(a));
    }

    [Fact]
    public void DecliningBalance_returns_zero_once_at_salvage()
    {
        IDepreciationMethod sut = new DecliningBalanceDepreciation();
        Asset a = Make(12000m, 1000m, 24, accumulated: 11000m, DepreciationMethod.DecliningBalance, factor: 2.0m);
        Assert.Equal(0m, sut.DepreciationForPeriod(a));
    }

    // ── Selector ─────────────────────────────────────────────────────────────

    [Fact]
    public void Selector_returns_the_matching_strategy()
    {
        DepreciationMethodSelector selector = new(
            [new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        Assert.IsType<StraightLineDepreciation>(selector.For(DepreciationMethod.StraightLine));
        Assert.IsType<DecliningBalanceDepreciation>(selector.For(DepreciationMethod.DecliningBalance));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter DepreciationMethodTests`
Expected: FAIL to compile — `IDepreciationMethod`, `StraightLineDepreciation`, `DecliningBalanceDepreciation`, `DepreciationMethodSelector` do not exist.

- [ ] **Step 3: Write the interface**

Create `Modules/FixedAssets/Accounting101.FixedAssets/IDepreciationMethod.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>One depreciation method. Pure: given an asset's current stored state it returns the
/// depreciation for ONE period (full-month convention — every eligible period is one uniform month).
/// Never negative; never drives AccumulatedDepreciation past the method's floor.</summary>
public interface IDepreciationMethod
{
    DepreciationMethod Method { get; }

    /// <summary>Depreciation for one month given the asset's AcquisitionCost, SalvageValue,
    /// UsefulLifeMonths, AccumulatedDepreciation and (for declining balance) DecliningBalanceFactor.</summary>
    decimal DepreciationForPeriod(Asset asset);
}
```

- [ ] **Step 4: Write the straight-line strategy**

Create `Modules/FixedAssets/Accounting101.FixedAssets/StraightLineDepreciation.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>Straight-line: an equal share of the depreciable base (cost − salvage) each month over the
/// useful life. The final period takes the exact remainder so accumulated never exceeds the base.</summary>
public sealed class StraightLineDepreciation : IDepreciationMethod
{
    public DepreciationMethod Method => DepreciationMethod.StraightLine;

    public decimal DepreciationForPeriod(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        decimal depreciableBase = asset.AcquisitionCost - asset.SalvageValue;
        decimal remaining = depreciableBase - asset.AccumulatedDepreciation;
        if (remaining <= 0m) return 0m;
        decimal monthly = Math.Round(depreciableBase / asset.UsefulLifeMonths, 2, MidpointRounding.ToEven);
        return Math.Min(monthly, remaining);
    }
}
```

- [ ] **Step 5: Write the declining-balance strategy**

Create `Modules/FixedAssets/Accounting101.FixedAssets/DecliningBalanceDepreciation.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>Declining-balance: net book value × (factor / life) each month, floored at salvage. The
/// period that would cross salvage takes exactly the remainder down to salvage; once at salvage it
/// returns 0. No straight-line crossover (FA-2 decision). DecliningBalanceFactor is guaranteed present
/// and positive by asset validation.</summary>
public sealed class DecliningBalanceDepreciation : IDepreciationMethod
{
    public DepreciationMethod Method => DepreciationMethod.DecliningBalance;

    public decimal DepreciationForPeriod(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        decimal nbv = asset.AcquisitionCost - asset.AccumulatedDepreciation;
        decimal floorRemaining = nbv - asset.SalvageValue;
        if (floorRemaining <= 0m) return 0m;
        decimal factor = asset.DecliningBalanceFactor ?? 0m;
        decimal rate = factor / asset.UsefulLifeMonths;
        decimal raw = Math.Round(nbv * rate, 2, MidpointRounding.ToEven);
        return Math.Min(raw, floorRemaining);
    }
}
```

- [ ] **Step 6: Write the selector**

Create `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationMethodSelector.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>Resolves the depreciation strategy for an asset's stored method. Built once from the
/// registered strategies (DI passes all IDepreciationMethod implementations).</summary>
public sealed class DepreciationMethodSelector
{
    private readonly Dictionary<DepreciationMethod, IDepreciationMethod> _byMethod;

    public DepreciationMethodSelector(IEnumerable<IDepreciationMethod> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        _byMethod = methods.ToDictionary(m => m.Method);
    }

    public IDepreciationMethod For(DepreciationMethod method) =>
        _byMethod.TryGetValue(method, out IDepreciationMethod? m)
            ? m
            : throw new InvalidOperationException($"No depreciation strategy registered for {method}.");
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter DepreciationMethodTests`
Expected: PASS (12 tests).

- [ ] **Step 8: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/IDepreciationMethod.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/StraightLineDepreciation.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/DecliningBalanceDepreciation.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/DepreciationMethodSelector.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationMethodTests.cs
git commit -m "feat(fixedassets): pluggable depreciation strategy (straight-line + declining-balance)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Depreciation-run domain types + posting recipe (pure domain)

The evidentiary run's data shapes, and the pure recipe that composes the single aggregate GL entry — mirroring `PayrollPosting` / `PayrollPostingTests`.

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationPeriod.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationRun.cs` (holds `DepreciationRunLine`, `DepreciationRunBody`, `DepreciationRunStatus`, `DepreciationRun`, `DepreciationRunView` — small related records in one file)
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPostingAccounts.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPosting.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsPostingTests.cs`

**Interfaces:**
- Consumes: `Accounting101.Ledger.Contracts` — `PostEntryRequest(Guid Id, DateOnly EffectiveDate, string? Reference, string? Memo, IReadOnlyList<PostLineRequest> Lines, Guid SourceRef, string SourceType)`, `PostLineRequest(Guid AccountId, string Direction, decimal Amount)`, `EntryIdentity.ForSource(string, Guid)`.
- Produces:
  - `readonly record struct DepreciationPeriod(int Year, int Month)` with `DateOnly LastDay()`.
  - `sealed record DepreciationRunLine(Guid AssetId, decimal Amount)`.
  - `sealed record DepreciationRunBody(DepreciationPeriod Period, DateOnly EffectiveDate, string? Memo, IReadOnlyList<DepreciationRunLine> Lines, decimal Total)`.
  - `enum DepreciationRunStatus { Posted = 0, Voided = 1 }`.
  - `sealed record DepreciationRun` (`Id`, `Number` (string?), `Period`, `EffectiveDate`, `Memo`, `Lines`, `Total`, `Status`).
  - `sealed record DepreciationRunView(DepreciationRun Run)`.
  - `sealed record FixedAssetsPostingAccounts` (`DepreciationExpenseAccountId`, `AccumulatedDepreciationAccountId`, both required `Guid`).
  - `static class FixedAssetsPosting` with `const string DepreciationRunSourceType = "DepreciationRun"` and `PostEntryRequest ComposeDepreciationRun(Guid runId, decimal total, DateOnly effectiveDate, string? memo, FixedAssetsPostingAccounts accounts)`.

- [ ] **Step 1: Write the failing tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsPostingTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsPostingTests
{
    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    private static FixedAssetsPostingAccounts MakeAccounts(out Guid expense, out Guid accumulated)
    {
        expense = Guid.NewGuid();
        accumulated = Guid.NewGuid();
        return new FixedAssetsPostingAccounts
        {
            DepreciationExpenseAccountId = expense,
            AccumulatedDepreciationAccountId = accumulated,
        };
    }

    [Fact]
    public void Compose_debits_expense_and_credits_accumulated_balanced()
    {
        Guid runId = Guid.NewGuid();
        FixedAssetsPostingAccounts accounts = MakeAccounts(out Guid expense, out Guid accumulated);

        PostEntryRequest entry = FixedAssetsPosting.ComposeDepreciationRun(
            runId, total: 1416.67m, effectiveDate: new DateOnly(2026, 1, 31), memo: "Jan depreciation", accounts);

        Assert.Equal(2, entry.Lines.Count);
        PostLineRequest expLine = entry.Lines.Single(l => l.AccountId == expense);
        Assert.Equal("Debit", expLine.Direction);
        Assert.Equal(1416.67m, expLine.Amount);
        PostLineRequest accLine = entry.Lines.Single(l => l.AccountId == accumulated);
        Assert.Equal("Credit", accLine.Direction);
        Assert.Equal(1416.67m, accLine.Amount);
        Assert.Equal(0m, entry.Lines.Sum(Signed)); // balanced
        Assert.Equal(new DateOnly(2026, 1, 31), entry.EffectiveDate);
        Assert.Equal("Jan depreciation", entry.Memo);
    }

    [Fact]
    public void Compose_carries_source_type_and_deterministic_id()
    {
        Guid runId = Guid.NewGuid();
        FixedAssetsPostingAccounts accounts = MakeAccounts(out _, out _);

        PostEntryRequest a = FixedAssetsPosting.ComposeDepreciationRun(runId, 500m, new DateOnly(2026, 1, 31), null, accounts);
        PostEntryRequest b = FixedAssetsPosting.ComposeDepreciationRun(runId, 500m, new DateOnly(2026, 1, 31), null, accounts);

        Assert.Equal("DepreciationRun", a.SourceType);
        Assert.Equal(runId, a.SourceRef);
        Assert.Equal(EntryIdentity.ForSource(FixedAssetsPosting.DepreciationRunSourceType, runId), a.Id);
        Assert.Equal(a.Id, b.Id); // deterministic
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Compose_throws_when_total_not_positive(int total)
    {
        FixedAssetsPostingAccounts accounts = MakeAccounts(out _, out _);
        Assert.Throws<ArgumentException>(() =>
            FixedAssetsPosting.ComposeDepreciationRun(Guid.NewGuid(), total, new DateOnly(2026, 1, 31), null, accounts));
    }

    [Fact]
    public void Period_last_day_handles_february()
    {
        Assert.Equal(new DateOnly(2026, 2, 28), new DepreciationPeriod(2026, 2).LastDay());
        Assert.Equal(new DateOnly(2028, 2, 29), new DepreciationPeriod(2028, 2).LastDay()); // leap
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FixedAssetsPostingTests`
Expected: FAIL to compile — the new types do not exist.

- [ ] **Step 3: Write `DepreciationPeriod`**

Create `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationPeriod.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>A calendar month a depreciation run targets. The run's default GL effective date is the
/// last day of this month.</summary>
public readonly record struct DepreciationPeriod(int Year, int Month)
{
    /// <summary>The last calendar day of the period (handles month length + leap years).</summary>
    public DateOnly LastDay() => new(Year, Month, DateTime.DaysInMonth(Year, Month));

    /// <summary>True when this period is on or after the asset's in-service month.</summary>
    public bool OnOrAfterServiceMonth(DateOnly inService) =>
        Year > inService.Year || (Year == inService.Year && Month >= inService.Month);
}
```

- [ ] **Step 4: Write the run records**

Create `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationRun.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>One asset's depreciation within a run.</summary>
public sealed record DepreciationRunLine(Guid AssetId, decimal Amount);

/// <summary>The stored body of a depreciation run (the evidentiary document body).</summary>
public sealed record DepreciationRunBody(
    DepreciationPeriod Period,
    DateOnly EffectiveDate,
    string? Memo,
    IReadOnlyList<DepreciationRunLine> Lines,
    decimal Total);

/// <summary>Lifecycle of a run: posted, or voided (LIFO).</summary>
public enum DepreciationRunStatus
{
    Posted = 0,
    Voided = 1,
}

/// <summary>A depreciation run — the engine assigns the number; status is derived from the document
/// lifecycle.</summary>
public sealed record DepreciationRun
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }
    public required DepreciationPeriod Period { get; init; }
    public required DateOnly EffectiveDate { get; init; }
    public string? Memo { get; init; }
    public required IReadOnlyList<DepreciationRunLine> Lines { get; init; }
    public required decimal Total { get; init; }
    public required DepreciationRunStatus Status { get; init; }
}

/// <summary>Read model for a depreciation run.</summary>
public sealed record DepreciationRunView(DepreciationRun Run);
```

- [ ] **Step 5: Write the posting accounts**

Create `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPostingAccounts.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>The two chart accounts a depreciation run posts to. Supplied by configuration; no hardcoded
/// account numbers.</summary>
public sealed record FixedAssetsPostingAccounts
{
    /// <summary>Depreciation Expense — debited for the run total.</summary>
    public required Guid DepreciationExpenseAccountId { get; init; }

    /// <summary>Accumulated Depreciation (contra-asset) — credited for the run total.</summary>
    public required Guid AccumulatedDepreciationAccountId { get; init; }
}
```

- [ ] **Step 6: Write the posting recipe**

Create `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPosting.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The depreciation recipe: one run composes into one balanced two-line journal entry
/// (Dr Depreciation Expense / Cr Accumulated Depreciation) for the run total. Pure — leaving sequencing,
/// approval, and persistence to the engine.</summary>
public static class FixedAssetsPosting
{
    public const string DepreciationRunSourceType = "DepreciationRun";

    /// <summary>Composes the two-line entry for a depreciation run. Throws <see cref="ArgumentException"/>
    /// when the total is not positive (the run service guards against empty/zero runs upstream).</summary>
    public static PostEntryRequest ComposeDepreciationRun(
        Guid runId, decimal total, DateOnly effectiveDate, string? memo, FixedAssetsPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (total <= 0m)
            throw new ArgumentException("Depreciation run total must be positive.", nameof(total));

        List<PostLineRequest> lines =
        [
            new(accounts.DepreciationExpenseAccountId,     "Debit",  total),
            new(accounts.AccumulatedDepreciationAccountId, "Credit", total),
        ];

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(DepreciationRunSourceType, runId),
            EffectiveDate: effectiveDate,
            Reference: null,
            Memo: memo,
            Lines: lines,
            SourceRef: runId,
            SourceType: DepreciationRunSourceType);
    }
}
```

- [ ] **Step 7: Run to verify pass**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FixedAssetsPostingTests`
Expected: PASS (5 tests).

- [ ] **Step 8: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/DepreciationPeriod.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/DepreciationRun.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPostingAccounts.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPosting.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsPostingTests.cs
git commit -m "feat(fixedassets): depreciation-run domain types + aggregate posting recipe

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Depreciation-run evidentiary store

Persist runs as evidentiary documents through the engine document store, mirroring `DocumentPayrollRunStore`, plus the two queries the run service's period-guard and LIFO-void need. Tested against a real `ScopedDocumentStore` on EphemeralMongo, mirroring the FA-1 `AssetDocumentStoreFixture`/`AssetDocumentStoreTests`.

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationRunPorts.cs` (the `IDepreciationRunStore` interface)
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DocumentDepreciationRunStore.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunStoreFixture.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunStoreTests.cs`

**Interfaces:**
- Consumes: `Accounting101.Ledger.Contracts` — `IDocumentStore` (`CreateAsync(clientId, collection, body, tags, ct) -> Guid`, `FinalizeAsync`, `VoidAsync`, `GetAsync<T>(clientId, collection, id, ct) -> DocumentResult<T>?`, `QueryAsync<T>(clientId, collection, tags, skip, limit, descending, includeVoided, ct)`, `CountAsync`), `DocumentResult<T>` (`Id`, `Body`, `Sequence` (long?), `State` (`DocumentLifecycle`)), `DocumentLifecycle`, `PagedResponse<T>`. The run types from Task 2.
- Produces:
  - `interface IDepreciationRunStore` with `RecordAsync`, `VoidAsync`, `GetAsync`, `GetByClientPagedAsync`, `GetByPeriodAsync(Guid clientId, DepreciationPeriod period, CancellationToken ct) -> Task<DepreciationRun?>`, `GetLatestAsync(Guid clientId, CancellationToken ct) -> Task<DepreciationRun?>`.
  - `sealed class DocumentDepreciationRunStore : IDepreciationRunStore`.

- [ ] **Step 1: Write the store fixture**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunStoreFixture.cs`. This mirrors the FA-1 `AssetDocumentStoreFixture` (read it) but keeps the `ControlStore` as a field so it can register a **fresh client per test** (`NewClient()`) — the run-store tests assert on "latest" and absolute counts, so each needs its own client for isolation. The manifest declares both collections; `ModuleManifestBuilder` is fluent (`Backend/.../Documents/ModuleManifest.cs`: `.Reference(...)` and `.Evidentiary(...)` both return the builder):

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

/// <summary>A disposable EphemeralMongo instance wired as the host wires the fixed-assets module: the
/// "fixedassets" module registered, and a real ScopedDocumentStore bound to that identity with a
/// .Reference("assets").Evidentiary("depreciation-runs") manifest. NewClient() registers a fresh client
/// + member each call so run-store "latest"/count assertions are isolated.</summary>
public sealed class DepreciationRunStoreFixture : IAsyncLifetime
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

        ModuleManifest manifest = new ModuleManifestBuilder().Reference("assets").Evidentiary("depreciation-runs").Build();
        Store = new ScopedDocumentStore(
            new ModuleIdentity("fixedassets"),
            manifest,
            new ClientDatabaseResolver(mongo, _control),
            new FixedActor(UserId),
            new ModuleAccess(_control));
    }

    /// <summary>Register a fresh client (entitled to fixedassets) + a Controller member; return its id.</summary>
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

> Implementer note: `FixedActor` already exists in `AssetDocumentStoreFixture.cs` (same test namespace) — reuse it, do NOT redefine it (a duplicate type will not compile). If the async-over-sync `.GetAwaiter().GetResult()` in `NewClient()` is undesirable, make the tests call an `async Task<Guid> NewClientAsync()` instead and `await` it — either is fine; keep it simple.

- [ ] **Step 2: Write the failing store tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunStoreTests.cs`:

```csharp
namespace Accounting101.FixedAssets.Tests;

public sealed class DepreciationRunStoreTests(DepreciationRunStoreFixture fixture)
    : IClassFixture<DepreciationRunStoreFixture>
{
    private DocumentDepreciationRunStore Store() => new(fixture.Store);

    private static DepreciationRunBody Body(int year, int month, params (Guid asset, decimal amt)[] lines)
    {
        List<DepreciationRunLine> runLines = lines.Select(l => new DepreciationRunLine(l.asset, l.amt)).ToList();
        return new DepreciationRunBody(
            new DepreciationPeriod(year, month),
            new DepreciationPeriod(year, month).LastDay(),
            Memo: null,
            Lines: runLines,
            Total: runLines.Sum(l => l.Amount));
    }

    [Fact]
    public async Task Record_assigns_a_number_and_posted_status_and_round_trips()
    {
        DocumentDepreciationRunStore store = Store();
        Guid clientId = fixture.NewClient();
        Guid asset = Guid.NewGuid();

        DepreciationRun run = await store.RecordAsync(clientId, Body(2026, 1, (asset, 500m)), default);

        Assert.NotNull(run.Number);
        Assert.Equal(DepreciationRunStatus.Posted, run.Status);
        Assert.Equal(500m, run.Total);
        DepreciationRun? fetched = await store.GetAsync(clientId, run.Id, default);
        Assert.NotNull(fetched);
        Assert.Equal(new DepreciationPeriod(2026, 1), fetched!.Period);
        Assert.Equal(asset, Assert.Single(fetched.Lines).AssetId);
    }

    [Fact]
    public async Task GetByPeriod_finds_a_non_voided_run_and_ignores_voided()
    {
        DocumentDepreciationRunStore store = Store();
        Guid clientId = fixture.NewClient();

        DepreciationRun jan = await store.RecordAsync(clientId, Body(2026, 1, (Guid.NewGuid(), 100m)), default);
        Assert.NotNull(await store.GetByPeriodAsync(clientId, new DepreciationPeriod(2026, 1), default));
        Assert.Null(await store.GetByPeriodAsync(clientId, new DepreciationPeriod(2026, 2), default));

        await store.VoidAsync(clientId, jan.Id, default);
        Assert.Null(await store.GetByPeriodAsync(clientId, new DepreciationPeriod(2026, 1), default));
    }

    [Fact]
    public async Task GetLatest_returns_the_most_recent_non_voided_run()
    {
        DocumentDepreciationRunStore store = Store();
        Guid clientId = fixture.NewClient();

        DepreciationRun jan = await store.RecordAsync(clientId, Body(2026, 1, (Guid.NewGuid(), 100m)), default);
        DepreciationRun feb = await store.RecordAsync(clientId, Body(2026, 2, (Guid.NewGuid(), 100m)), default);

        DepreciationRun? latest = await store.GetLatestAsync(clientId, default);
        Assert.Equal(feb.Id, latest!.Id);

        await store.VoidAsync(clientId, feb.Id, default);
        DepreciationRun? afterVoid = await store.GetLatestAsync(clientId, default);
        Assert.Equal(jan.Id, afterVoid!.Id);
    }

    [Fact]
    public async Task Paged_list_excludes_voided_unless_requested()
    {
        DocumentDepreciationRunStore store = Store();
        Guid clientId = fixture.NewClient();

        DepreciationRun a = await store.RecordAsync(clientId, Body(2026, 1, (Guid.NewGuid(), 100m)), default);
        await store.RecordAsync(clientId, Body(2026, 2, (Guid.NewGuid(), 100m)), default);
        await store.VoidAsync(clientId, a.Id, default);

        PagedResponse<DepreciationRun> excl = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.Equal(1, excl.Total);
        PagedResponse<DepreciationRun> incl = await store.GetByClientPagedAsync(clientId, 0, 50, true, true, default);
        Assert.Equal(2, incl.Total);
    }
}
```

> Implementer note: `PagedResponse<T>` is in `Accounting101.Ledger.Contracts` — the test file already has `using Accounting101.Ledger.Contracts;`? It does not yet; add it. The FA-1 `AssetDocumentStoreTests` uses plain constructor injection + `IClassFixture<...>` and no `[Collection]` attribute — match that (already reflected above).

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter DepreciationRunStoreTests`
Expected: FAIL to compile — `IDepreciationRunStore` / `DocumentDepreciationRunStore` do not exist.

- [ ] **Step 4: Write the port**

Create `Modules/FixedAssets/Accounting101.FixedAssets/DepreciationRunPorts.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The depreciation-run store — evidentiary documents backed by the engine's document store.
/// Numbered + finalized on record, voidable. Adds the period-guard and LIFO-void queries the run
/// service needs.</summary>
public interface IDepreciationRunStore
{
    Task<DepreciationRun> RecordAsync(Guid clientId, DepreciationRunBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default);
    Task<DepreciationRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default);
    Task<PagedResponse<DepreciationRun>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default);

    /// <summary>The non-voided run for a period, if one exists (period guard).</summary>
    Task<DepreciationRun?> GetByPeriodAsync(Guid clientId, DepreciationPeriod period, CancellationToken ct = default);

    /// <summary>The most recent non-voided run, if any (LIFO void guard).</summary>
    Task<DepreciationRun?> GetLatestAsync(Guid clientId, CancellationToken ct = default);
}
```

- [ ] **Step 5: Write the store**

Create `Modules/FixedAssets/Accounting101.FixedAssets/DocumentDepreciationRunStore.cs`. Mirror `Modules/Payroll/Accounting101.Payroll/DocumentPayrollRunStore.cs` (read it): evidentiary create-then-finalize, `Map` from `DocumentResult`, `Number = result.Sequence is { } seq ? $"DR-{seq:D5}" : null`, status from `DocumentLifecycle.Voided or Superseded -> Voided else Posted`. Add the two new queries as in-memory filters over `QueryAsync` (non-voided only):

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Persists depreciation runs as evidentiary documents (created, immediately finalized into a
/// numbered append-only document, voidable). Number + status derive from the engine envelope. The
/// module owns no database connection.</summary>
public sealed class DocumentDepreciationRunStore(IDocumentStore documents) : IDepreciationRunStore
{
    private const string Collection = "depreciation-runs";

    public async Task<DepreciationRun> RecordAsync(Guid clientId, DepreciationRunBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<DepreciationRunBody>? result = await documents.GetAsync<DepreciationRunBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid runId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, runId, ct);

    public async Task<DepreciationRun?> GetAsync(Guid clientId, Guid runId, CancellationToken ct = default)
    {
        DocumentResult<DepreciationRunBody>? result = await documents.GetAsync<DepreciationRunBody>(clientId, Collection, runId, ct);
        return result is null ? null : Map(result);
    }

    public async Task<PagedResponse<DepreciationRun>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeVoided, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<DepreciationRunBody>> page =
            await documents.QueryAsync<DepreciationRunBody>(clientId, Collection, Tags(), skip, limit, descending, includeVoided, ct);
        long total = await documents.CountAsync(clientId, Collection, Tags(), includeVoided, ct);
        return new PagedResponse<DepreciationRun>(page.Select(Map).ToList(), total, skip, limit);
    }

    public async Task<DepreciationRun?> GetByPeriodAsync(Guid clientId, DepreciationPeriod period, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<DepreciationRunBody>> all =
            await documents.QueryAsync<DepreciationRunBody>(clientId, Collection, Tags(), 0, int.MaxValue, true, includeVoided: false, ct);
        DocumentResult<DepreciationRunBody>? hit = all.FirstOrDefault(r => r.Body.Period == period);
        return hit is null ? null : Map(hit);
    }

    public async Task<DepreciationRun?> GetLatestAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<DepreciationRunBody>> all =
            await documents.QueryAsync<DepreciationRunBody>(clientId, Collection, Tags(), 0, 1, descending: true, includeVoided: false, ct);
        DocumentResult<DepreciationRunBody>? latest = all.FirstOrDefault();
        return latest is null ? null : Map(latest);
    }

    private static Dictionary<string, string> Tags() => new();

    private static DepreciationRun Map(DocumentResult<DepreciationRunBody> r) => new()
    {
        Id = r.Id,
        Number = r.Sequence is { } seq ? $"DR-{seq:D5}" : null,
        Period = r.Body.Period,
        EffectiveDate = r.Body.EffectiveDate,
        Memo = r.Body.Memo,
        Lines = r.Body.Lines,
        Total = r.Body.Total,
        Status = r.State switch
        {
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => DepreciationRunStatus.Voided,
            _ => DepreciationRunStatus.Posted,
        },
    };
}
```

> Implementer note: `GetLatestAsync` relies on the engine's default ordering being by sequence/insertion when `descending: true`. Verify against `DocumentPayrollRunStore` usage / the engine's `QueryAsync` contract; if descending order is not sequence-stable, sort the in-memory result by `Sequence` descending before taking the first. The `GetByPeriodAsync` `int.MaxValue` limit is acceptable for the run volume here (one run per month); if the engine rejects `int.MaxValue`, use a large fixed bound (e.g. 100000) and add a `// bounded: one run per month, see plan` comment.

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter DepreciationRunStoreTests`
Expected: PASS (4 tests).

- [ ] **Step 7: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/DepreciationRunPorts.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/DocumentDepreciationRunStore.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunStoreFixture.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunStoreTests.cs
git commit -m "feat(fixedassets): evidentiary depreciation-run store with period + latest queries

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Asset store lifecycle — sticky deactivation, reactivate, apply/reverse depreciation

Changes span store → service → endpoints together so the module stays green. Sticky update: editing a deactivated asset now returns 409; a new reactivate endpoint is the only way back. Two new server-owned mutations advance/roll-back `AccumulatedDepreciation` (consumed by the run service in Task 5).

**Files:**
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPorts.cs` (extend `IAssetStore`, add `ReactivateResult` + `UpdateResult`)
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/DocumentAssetStore.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsService.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetLifecycleStoreTests.cs` (new — store-level apply/reverse/reactivate/sticky)
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsEndpointsTests.cs` (add sticky-409 + reactivate E2E; adjust any existing update assertion for the new result mapping)

**Interfaces:**
- Consumes: existing `IAssetStore`, `DocumentAssetStore`, `Asset`, `AssetDocument`, `DeactivateResult`, `IDocumentStore` (`GetAsync<T>`, `PutAsync`, `DeactivateAsync`, and a **reactivate** operation — see note), `DocumentLifecycle`.
- Produces (new on `IAssetStore`):
  - `Task<UpdateResult> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct)` — **return type changes** from `Task<Asset?>` to `Task<UpdateResult>`.
  - `Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid assetId, CancellationToken ct)`
  - `Task ApplyDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct)`
  - `Task ReverseDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct)`
  - `readonly record struct UpdateResult(UpdateOutcome Outcome, Asset? Asset)` + `enum UpdateOutcome { NotFound, Inactive, Updated }`
  - `enum ReactivateResult { NotFound, AlreadyActive, Reactivated }`
- On `FixedAssetsService`: `UpdateAsync` returns `UpdateResult`; add `Task<ReactivateResult> ReactivateAsync(...)`.

- [ ] **Step 1: Write the failing store tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetLifecycleStoreTests.cs`. Use the SAME store fixture the FA-1 `AssetDocumentStoreTests` uses (read that file for the fixture type + wiring; reuse it here):

```csharp
namespace Accounting101.FixedAssets.Tests;

// Mirror the fixture wiring of AssetDocumentStoreTests exactly (constructor + attributes).
public sealed class AssetLifecycleStoreTests(AssetDocumentStoreFixture fixture)
    : IClassFixture<AssetDocumentStoreFixture>
{
    private DocumentAssetStore Store() => new(fixture.Store);

    private static AssetBody Body(decimal cost = 12000m, decimal salvage = 0m, int life = 24) =>
        new("Van", cost, new DateOnly(2026, 1, 1), life, salvage, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Update_on_a_deactivated_asset_returns_Inactive_and_does_not_resurrect()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.DeactivateAsync(clientId, a.Id, default);

        UpdateResult result = await store.UpdateAsync(clientId, a.Id, Body(cost: 99999m), default);
        Assert.Equal(UpdateOutcome.Inactive, result.Outcome);

        // Still excluded from the active list (not resurrected).
        PagedResponse<Asset> active = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.DoesNotContain(active.Items, x => x.Id == a.Id);
    }

    [Fact]
    public async Task Update_on_missing_asset_returns_NotFound()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        UpdateResult result = await store.UpdateAsync(clientId, Guid.NewGuid(), Body(), default);
        Assert.Equal(UpdateOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Update_on_active_asset_updates_and_returns_the_asset()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(cost: 12000m), default);
        UpdateResult result = await store.UpdateAsync(clientId, a.Id, Body(cost: 15000m), default);
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(15000m, result.Asset!.AcquisitionCost);
    }

    [Fact]
    public async Task Reactivate_restores_a_deactivated_asset()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        await store.DeactivateAsync(clientId, a.Id, default);

        Assert.Equal(ReactivateResult.Reactivated, await store.ReactivateAsync(clientId, a.Id, default));
        // Now editable again.
        UpdateResult upd = await store.UpdateAsync(clientId, a.Id, Body(cost: 13000m), default);
        Assert.Equal(UpdateOutcome.Updated, upd.Outcome);
        // And back in the active list.
        PagedResponse<Asset> active = await store.GetByClientPagedAsync(clientId, 0, 50, true, false, default);
        Assert.Contains(active.Items, x => x.Id == a.Id);
    }

    [Fact]
    public async Task Reactivate_an_active_asset_returns_AlreadyActive()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        Assert.Equal(ReactivateResult.AlreadyActive, await store.ReactivateAsync(clientId, a.Id, default));
    }

    [Fact]
    public async Task Reactivate_missing_asset_returns_NotFound()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Assert.Equal(ReactivateResult.NotFound, await store.ReactivateAsync(clientId, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task ApplyDepreciation_then_reverse_round_trips_accumulated()
    {
        DocumentAssetStore store = Store();
        Guid clientId = fixture.ClientId;
        Asset a = await store.CreateAsync(clientId, Body(), default);
        Asset b = await store.CreateAsync(clientId, Body(), default);

        await store.ApplyDepreciationAsync(clientId,
            [new DepreciationRunLine(a.Id, 500m), new DepreciationRunLine(b.Id, 400m)], default);
        Assert.Equal(500m, (await store.GetAsync(clientId, a.Id, default))!.AccumulatedDepreciation);
        Assert.Equal(400m, (await store.GetAsync(clientId, b.Id, default))!.AccumulatedDepreciation);

        await store.ReverseDepreciationAsync(clientId,
            [new DepreciationRunLine(a.Id, 500m), new DepreciationRunLine(b.Id, 400m)], default);
        Assert.Equal(0m, (await store.GetAsync(clientId, a.Id, default))!.AccumulatedDepreciation);
        Assert.Equal(0m, (await store.GetAsync(clientId, b.Id, default))!.AccumulatedDepreciation);
    }
}
```

> Implementer note: reuse the FA-1 `AssetDocumentStoreFixture` as-is — it exposes `IDocumentStore Store { get; }` and a single shared `Guid ClientId` (there is NO `NewClient()`; all tests in this class share one client and isolate by fresh asset ids, exactly like `AssetDocumentStoreTests`). Add `using Accounting101.Ledger.Contracts;` for `PagedResponse<T>`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter AssetLifecycleStoreTests`
Expected: FAIL to compile — new members/types don't exist; `UpdateAsync` still returns `Task<Asset?>`.

- [ ] **Step 3: Extend the port**

Edit `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPorts.cs` — change `UpdateAsync`'s signature and add the new members + result types:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

public interface IAssetStore
{
    Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default);
    Task<UpdateResult> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default);
    Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<PagedResponse<Asset>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default);

    /// <summary>Advance each named asset's AccumulatedDepreciation by its line amount (run post).</summary>
    Task ApplyDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default);

    /// <summary>Roll each named asset's AccumulatedDepreciation back by its line amount (run void).</summary>
    Task ReverseDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default);
}

public enum DeactivateResult { NotFound, AlreadyInactive, Deactivated }

public enum ReactivateResult { NotFound, AlreadyActive, Reactivated }

public enum UpdateOutcome { NotFound, Inactive, Updated }

/// <summary>Outcome of an asset update: not found, refused because inactive (reactivate first), or the
/// updated asset.</summary>
public readonly record struct UpdateResult(UpdateOutcome Outcome, Asset? Asset)
{
    public static readonly UpdateResult NotFound = new(UpdateOutcome.NotFound, null);
    public static readonly UpdateResult Inactive = new(UpdateOutcome.Inactive, null);
    public static UpdateResult Updated(Asset asset) => new(UpdateOutcome.Updated, asset);
}
```

- [ ] **Step 4: Implement the store changes**

Edit `Modules/FixedAssets/Accounting101.FixedAssets/DocumentAssetStore.cs`:
- Rewrite `UpdateAsync` to check lifecycle state and return `UpdateResult`.
- Add `ReactivateAsync`, `ApplyDepreciationAsync`, `ReverseDepreciationAsync`.

```csharp
    public async Task<UpdateResult> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return UpdateResult.NotFound;
        if (existing.State == DocumentLifecycle.Inactive) return UpdateResult.Inactive; // sticky: reactivate first
        AssetDocument doc = ToDocument(body, existing.Body.Status, existing.Body.AccumulatedDepreciation);
        await documents.PutAsync(clientId, Collection, assetId, doc, NoTags, ct);
        return UpdateResult.Updated(Map(assetId, doc));
    }

    public async Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return ReactivateResult.NotFound;
        if (existing.State != DocumentLifecycle.Inactive) return ReactivateResult.AlreadyActive;
        // The engine has no explicit reactivate primitive; a Put on a reference doc rebuilds it Active
        // (ScopedDocumentStore.PutReferenceAsync always sets DocumentState.Active). Re-put the SAME body
        // (preserving server-owned Status + AccumulatedDepreciation) so only the lifecycle flips.
        await documents.PutAsync(clientId, Collection, assetId, existing.Body, NoTags, ct);
        return ReactivateResult.Reactivated;
    }

    public async Task ApplyDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default) =>
        await AdjustAccumulatedAsync(clientId, lines, sign: +1m, ct);

    public async Task ReverseDepreciationAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, CancellationToken ct = default) =>
        await AdjustAccumulatedAsync(clientId, lines, sign: -1m, ct);

    private async Task AdjustAccumulatedAsync(Guid clientId, IReadOnlyList<DepreciationRunLine> lines, decimal sign, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (DepreciationRunLine line in lines)
        {
            DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, line.AssetId, ct);
            if (existing is null) continue; // asset gone; skip (run void tolerates missing assets)
            decimal updated = existing.Body.AccumulatedDepreciation + sign * line.Amount;
            AssetDocument doc = existing.Body with { AccumulatedDepreciation = updated };
            await documents.PutAsync(clientId, Collection, line.AssetId, doc, NoTags, ct);
        }
    }
```

> Implementer note — **reactivate mechanism (confirmed)**: the engine has NO `ReactivateAsync` primitive on `IDocumentStore`. Reactivation is done by re-`PutAsync`ing the existing body: `ScopedDocumentStore.PutReferenceAsync` unconditionally builds the document with `DocumentState.Active` (verified: `Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs:259`), so a Put on an inactive reference doc flips it Active (audited as `DocumentUpdated`). This is the SAME mechanism the sticky-update guard exists to prevent accidentally — here we invoke it deliberately. The `Reactivate_restores_a_deactivated_asset` store test proves the doc returns to the active list. Do not look for a nonexistent primitive.

- [ ] **Step 5: Update the service**

Edit `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsService.cs` — `UpdateAsync` now returns `UpdateResult`, and add `ReactivateAsync`:

```csharp
    public Task<UpdateResult> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default)
    {
        if (AssetValidation.Validate(body) is { } error) throw new ArgumentException(error);
        return store.UpdateAsync(clientId, assetId, body, ct);
    }

    public Task<ReactivateResult> ReactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default) =>
        store.ReactivateAsync(clientId, assetId, ct);
```

- [ ] **Step 6: Update the endpoints**

Edit `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`:
- Map `UpdateAsset` off `UpdateResult`.
- Add `POST /assets/{assetId:guid}/reactivate`.

```csharp
        clients.MapPost("/assets/{assetId:guid}/deactivate", DeactivateAsset);
        clients.MapPost("/assets/{assetId:guid}/reactivate", ReactivateAsset);
```

```csharp
    private static async Task<IResult> UpdateAsset(
        Guid clientId, Guid assetId, SaveAssetRequest request, FixedAssetsService service, CancellationToken cancellationToken)
    {
        try
        {
            UpdateResult result = await service.UpdateAsync(clientId, assetId, request.ToBody(), cancellationToken);
            return result.Outcome switch
            {
                UpdateOutcome.Updated => Results.Ok(new AssetView(result.Asset!)),
                UpdateOutcome.NotFound => Results.NotFound(),
                UpdateOutcome.Inactive => Results.Problem(
                    "Asset is inactive; reactivate it before editing.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem("Unexpected update result.", statusCode: StatusCodes.Status500InternalServerError),
            };
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> ReactivateAsset(
        Guid clientId, Guid assetId, FixedAssetsService service, CancellationToken cancellationToken)
    {
        ReactivateResult result = await service.ReactivateAsync(clientId, assetId, cancellationToken);
        if (result == ReactivateResult.NotFound) return Results.NotFound();
        if (result == ReactivateResult.AlreadyActive)
            return Results.Problem("Asset is already active.", statusCode: StatusCodes.Status409Conflict);
        Asset? asset = await service.GetAsync(clientId, assetId, cancellationToken);
        return asset is null ? Results.NotFound() : Results.Ok(new AssetView(asset));
    }
```

- [ ] **Step 7: Add the endpoint-level tests**

Edit `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsEndpointsTests.cs` — add (read the file first to match its helper style: how it seeds a client, creates an asset, and its `SaveAssetRequest` usage):

```csharp
    [Fact]
    public async Task Editing_a_deactivated_asset_returns_409_until_reactivated()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller by default

        // Create + deactivate.
        AssetView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets",
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AssetView>())!;
        Guid assetId = created.Asset.Id;
        (await http.PostAsync($"/clients/{clientId}/assets/{assetId}/deactivate", null)).EnsureSuccessStatusCode();

        // Update now 409.
        HttpResponseMessage edit = await http.PutAsJsonAsync($"/clients/{clientId}/assets/{assetId}",
            new SaveAssetRequest("Van 2", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);

        // Reactivate, then update succeeds.
        (await http.PostAsync($"/clients/{clientId}/assets/{assetId}/reactivate", null)).EnsureSuccessStatusCode();
        HttpResponseMessage edit2 = await http.PutAsJsonAsync($"/clients/{clientId}/assets/{assetId}",
            new SaveAssetRequest("Van 2", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        Assert.Equal(HttpStatusCode.OK, edit2.StatusCode);
    }

    [Fact]
    public async Task Reactivating_an_active_asset_returns_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        AssetView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets",
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AssetView>())!;
        HttpResponseMessage reactivate = await http.PostAsync($"/clients/{clientId}/assets/{created.Asset.Id}/reactivate", null);
        Assert.Equal(HttpStatusCode.Conflict, reactivate.StatusCode);
    }
```

> Implementer note: match the existing `FixedAssetsEndpointsTests` conventions (namespace, `using` set incl. `System.Net`, `System.Net.Http.Json`, the fixture field name — likely `fixture`). If any existing test asserted the old `UpdateAsync` returning a bare 404/200, it still holds (NotFound → 404, Updated → 200); only the inactive path is new. Do not change unrelated existing tests.

- [ ] **Step 8: Run the whole module suite**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS — all prior FA tests plus the new lifecycle store + endpoint tests. Report the count.

- [ ] **Step 9: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPorts.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/DocumentAssetStore.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsService.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetLifecycleStoreTests.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsEndpointsTests.cs
git commit -m "feat(fixedassets): sticky deactivation + reactivate + accumulated-depreciation mutations

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Run orchestration (service) — unit-tested with fakes

`FixedAssetsRunService` composes the whole run: period guard → enumerate eligible assets → compute → persist → advance accumulated → post one PendingApproval entry; and LIFO void → reverse entry + roll back. Unit-tested against in-memory fakes (no Mongo, no host), mirroring `PayrollServiceTests` + `Fakes.cs`.

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/ILedgerClient.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/IFixedAssetsAccountsProvider.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsRunService.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsRunServiceTests.cs`

**Interfaces:**
- Consumes: `IAssetStore` (Task 4, incl. `ApplyDepreciationAsync`/`ReverseDepreciationAsync`/`GetByClientPagedAsync`), `IDepreciationRunStore` (Task 3), `DepreciationMethodSelector` (Task 1), `FixedAssetsPosting` + `FixedAssetsPostingAccounts` (Task 2), `Accounting101.Ledger.Contracts` (`PostEntryRequest`, `PostEntryResponse`, `EntryResponse`, `ReverseRequest`, `VoidRequest`).
- Produces:
  - `interface ILedgerClient` — copy `Modules/Payroll/Accounting101.Payroll/ILedgerClient.cs` verbatim except the namespace (`Accounting101.FixedAssets`): `PostAsync`, `ReverseAsync`, `VoidAsync`, `GetEntriesBySourceRefAsync`.
  - `interface IFixedAssetsAccountsProvider { Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default); }`
  - `sealed class FixedAssetsRunService` with:
    - `Task<DepreciationRun> RunDepreciationAsync(Guid clientId, DepreciationRunRequest request, CancellationToken ct = default)`
    - `Task<DepreciationRun> VoidRunAsync(Guid clientId, Guid runId, string? reason, CancellationToken ct = default)`
    - `Task<DepreciationRun?> GetRunAsync(Guid clientId, Guid runId, CancellationToken ct = default)`
  - `sealed record DepreciationRunRequest(int Year, int Month, DateOnly? EffectiveDate, string? Memo)` (domain-level orchestration input; the Api DTO in Task 6 maps to this).

- [ ] **Step 1: Write the fakes**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs`. Read `Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs` for the `ILedgerClient` fake shape and mirror it. Provide:
- `FakeLedgerClient : ILedgerClient` — records posted `PostEntryRequest`s in a public list; `PostAsync` returns a `PostEntryResponse` with a fresh entry id and echoes the request; `GetEntriesBySourceRefAsync` returns an `EntryResponse` for the last post of that source ref with `Posting = "PendingApproval"`, `Status = "Active"`, `ReversalOf = null` (so the void path finds it); `ReverseAsync`/`VoidAsync` record the call + flip a flag.
- `InMemoryAssetStore : IAssetStore` — a `Dictionary<Guid, Asset>` (active) + a set of deactivated ids; `CreateAsync`, `GetByClientPagedAsync` (respects `includeInactive`), `GetAsync`, `ApplyDepreciationAsync`/`ReverseDepreciationAsync` (mutate `AccumulatedDepreciation`), plus the other members as minimal stubs.
- `InMemoryDepreciationRunStore : IDepreciationRunStore` — a `List<DepreciationRun>`; `RecordAsync` assigns an incrementing `DR-#####` number + `Posted`; `VoidAsync` flips `Voided`; `GetByPeriodAsync`/`GetLatestAsync`/`GetAsync`/`GetByClientPagedAsync` filter the list (non-voided for period/latest).
- `FixedAccountsProvider : IFixedAssetsAccountsProvider` — returns a fixed pair of GUIDs exposed as public properties.

> Keep the fakes minimal but honest — the period-guard and LIFO tests depend on `GetByPeriodAsync`/`GetLatestAsync` correctly excluding voided runs.

- [ ] **Step 2: Write the failing service tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsRunServiceTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsRunServiceTests
{
    private static (FixedAssetsRunService svc, InMemoryAssetStore assets, InMemoryDepreciationRunStore runs,
        FakeLedgerClient ledger, FixedAccountsProvider accounts) Build()
    {
        InMemoryAssetStore assets = new();
        InMemoryDepreciationRunStore runs = new();
        FakeLedgerClient ledger = new();
        FixedAccountsProvider accounts = new();
        DepreciationMethodSelector selector = new([new StraightLineDepreciation(), new DecliningBalanceDepreciation()]);
        FixedAssetsRunService svc = new(assets, runs, selector, accounts, ledger);
        return (svc, assets, runs, ledger, accounts);
    }

    private static AssetBody Sl(decimal cost, int life, DateOnly inService) =>
        new("SL asset", cost, inService, life, 0m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Run_computes_lines_advances_assets_and_posts_one_aggregate_entry()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, FakeLedgerClient ledger, FixedAccountsProvider accounts) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default); // 500/mo
        Asset b = await assets.CreateAsync(clientId, Sl(6000m, 24, new DateOnly(2026, 1, 1)), default);  // 250/mo

        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, "Jan"), default);

        Assert.Equal(750m, run.Total);
        Assert.Equal(2, run.Lines.Count);
        Assert.Equal(500m, (await assets.GetAsync(clientId, a.Id, default))!.AccumulatedDepreciation);
        Assert.Equal(250m, (await assets.GetAsync(clientId, b.Id, default))!.AccumulatedDepreciation);

        PostEntryRequest posted = Assert.Single(ledger.Posted);
        Assert.Equal("DepreciationRun", posted.SourceType);
        Assert.Equal(750m, posted.Lines.Single(l => l.AccountId == accounts.DepreciationExpenseAccountId).Amount);
        Assert.Equal(new DateOnly(2026, 1, 31), posted.EffectiveDate); // default = last day of period
    }

    [Fact]
    public async Task Second_run_for_the_same_period_is_rejected()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default));
    }

    [Fact]
    public async Task A_period_with_no_eligible_assets_throws_ArgumentException()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        // Asset goes into service AFTER the run period → not eligible.
        await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 6, 1)), default);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default));
    }

    [Fact]
    public async Task Asset_in_its_in_service_month_earns_a_full_month()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 15)), default); // mid-month
        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);
        Assert.Equal(500m, run.Total); // full month despite Jan-15 in-service (full-month convention)
    }

    [Fact]
    public async Task Void_latest_run_reverses_entry_and_rolls_back_accumulated()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, FakeLedgerClient ledger, _) = Build();
        Guid clientId = Guid.NewGuid();
        Asset a = await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        DepreciationRun run = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);
        Assert.Equal(500m, (await assets.GetAsync(clientId, a.Id, default))!.AccumulatedDepreciation);

        DepreciationRun voided = await svc.VoidRunAsync(clientId, run.Id, "oops", default);
        Assert.Equal(DepreciationRunStatus.Voided, voided.Status);
        Assert.True(ledger.ReversedOrWithdrawn);
        Assert.Equal(0m, (await assets.GetAsync(clientId, a.Id, default))!.AccumulatedDepreciation);
    }

    [Fact]
    public async Task Voiding_a_non_latest_run_is_rejected()
    {
        (FixedAssetsRunService svc, InMemoryAssetStore assets, _, _, _) = Build();
        Guid clientId = Guid.NewGuid();
        await assets.CreateAsync(clientId, Sl(12000m, 24, new DateOnly(2026, 1, 1)), default);
        DepreciationRun jan = await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 1, null, null), default);
        await svc.RunDepreciationAsync(clientId, new DepreciationRunRequest(2026, 2, null, null), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.VoidRunAsync(clientId, jan.Id, null, default)); // not latest
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FixedAssetsRunServiceTests`
Expected: FAIL to compile — `ILedgerClient`, `IFixedAssetsAccountsProvider`, `FixedAssetsRunService`, `DepreciationRunRequest` do not exist.

- [ ] **Step 4: Write the ledger port + accounts provider port**

Create `Modules/FixedAssets/Accounting101.FixedAssets/ILedgerClient.cs` by copying `Modules/Payroll/Accounting101.Payroll/ILedgerClient.cs` and changing only the namespace to `Accounting101.FixedAssets` and the doc comment's module name.

Create `Modules/FixedAssets/Accounting101.FixedAssets/IFixedAssetsAccountsProvider.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>Supplies the two depreciation posting accounts for a client.</summary>
public interface IFixedAssetsAccountsProvider
{
    Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default);
}
```

- [ ] **Step 5: Write the run service**

Create `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsRunService.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Orchestrates depreciation: compute one period across all eligible assets, persist the
/// evidentiary run, advance each asset's accumulated depreciation, and post one PendingApproval GL
/// entry. Void is LIFO — only the latest non-voided run may be voided; it reverses the entry (or
/// withdraws it if still pending) and rolls each asset's accumulated depreciation back. The module
/// never self-approves.</summary>
public sealed class FixedAssetsRunService(
    IAssetStore assets,
    IDepreciationRunStore runs,
    DepreciationMethodSelector methods,
    IFixedAssetsAccountsProvider accounts,
    ILedgerClient ledger)
{
    public async Task<DepreciationRun> RunDepreciationAsync(
        Guid clientId, DepreciationRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DepreciationPeriod period = new(request.Year, request.Month);

        // 1. Period guard — one non-voided run per period.
        if (await runs.GetByPeriodAsync(clientId, period, ct) is not null)
            throw new InvalidOperationException($"A depreciation run already exists for {period.Year}-{period.Month:D2}.");

        // 2. Enumerate eligible active assets (in service on/before the period, not fully depreciated).
        List<DepreciationRunLine> lines = [];
        foreach (Asset asset in await ActiveAssetsAsync(clientId, ct))
        {
            if (!period.OnOrAfterServiceMonth(asset.InServiceDate)) continue;
            decimal amount = methods.For(asset.Method).DepreciationForPeriod(asset);
            if (amount > 0m) lines.Add(new DepreciationRunLine(asset.Id, amount));
        }

        // 3. Nothing to depreciate → 422 (no doc, no entry).
        if (lines.Count == 0)
            throw new ArgumentException($"No assets to depreciate for {period.Year}-{period.Month:D2}.");

        decimal total = lines.Sum(l => l.Amount);
        DateOnly effectiveDate = request.EffectiveDate ?? period.LastDay();

        // 4. Persist the evidentiary run.
        DepreciationRun run = await runs.RecordAsync(clientId,
            new DepreciationRunBody(period, effectiveDate, request.Memo, lines, total), ct);

        // 5. Advance each asset's accumulated depreciation.
        await assets.ApplyDepreciationAsync(clientId, lines, ct);

        // 6. Compose + post one PendingApproval aggregate entry.
        FixedAssetsPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);
        PostEntryRequest entry = FixedAssetsPosting.ComposeDepreciationRun(run.Id, total, effectiveDate, request.Memo, postingAccounts);
        await ledger.PostAsync(clientId, entry, ct);

        return run;
    }

    public async Task<DepreciationRun> VoidRunAsync(Guid clientId, Guid runId, string? reason, CancellationToken ct = default)
    {
        DepreciationRun run = await runs.GetAsync(clientId, runId, ct)
            ?? throw new InvalidOperationException($"Depreciation run {runId} not found.");
        if (run.Status != DepreciationRunStatus.Posted)
            throw new InvalidOperationException($"Only a posted depreciation run can be voided; {runId} is {run.Status}.");

        // LIFO — only the most recent non-voided run may be voided.
        DepreciationRun? latest = await runs.GetLatestAsync(clientId, ct);
        if (latest is null || latest.Id != run.Id)
            throw new InvalidOperationException("Only the most recent depreciation run can be voided.");

        // Reverse the posted entry (or withdraw it if still pending) — Payroll precedent.
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, runId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for depreciation run {run.Number} to void.");
        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(run.EffectiveDate, reason ?? $"Voided depreciation run {runId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided depreciation run {runId}"), ct);

        // Roll each asset's accumulated depreciation back, then void the doc.
        await assets.ReverseDepreciationAsync(clientId, run.Lines, ct);
        await runs.VoidAsync(clientId, runId, ct);
        return (await runs.GetAsync(clientId, runId, ct))!;
    }

    public Task<DepreciationRun?> GetRunAsync(Guid clientId, Guid runId, CancellationToken ct = default) =>
        runs.GetAsync(clientId, runId, ct);

    /// <summary>All active (non-deactivated) assets for the client — the depreciation candidate set.</summary>
    private async Task<IReadOnlyList<Asset>> ActiveAssetsAsync(Guid clientId, CancellationToken ct)
    {
        List<Asset> all = [];
        int skip = 0;
        const int page = 200;
        while (true)
        {
            PagedResponse<Asset> batch = await assets.GetByClientPagedAsync(clientId, skip, page, descending: false, includeInactive: false, ct);
            all.AddRange(batch.Items);
            skip += page;
            if (all.Count >= batch.Total || batch.Items.Count == 0) break;
        }
        return all;
    }
}

/// <summary>Orchestration input for a depreciation run — the caller supplies only the period and optional
/// overrides; amounts are server-computed.</summary>
public sealed record DepreciationRunRequest(int Year, int Month, DateOnly? EffectiveDate, string? Memo);
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FixedAssetsRunServiceTests`
Expected: PASS (6 tests).

- [ ] **Step 7: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/ILedgerClient.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/IFixedAssetsAccountsProvider.cs \
        Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsRunService.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsRunServiceTests.cs
git commit -m "feat(fixedassets): depreciation run orchestration (period guard + LIFO void)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Host wiring + run endpoints + HTTP E2E

Wire the module for posting (evidentiary manifest, strategies, provider, named `HttpLedgerClient`, run service), expose the run endpoints, and prove the whole thing end-to-end through the real host — the module's first GL posts.

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Api/ConfiguredFixedAssetsAccountsProvider.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Api/DepreciationRunRequests.cs` (the `RunDepreciationRequest` + `VoidReasonRequest` API DTOs)
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsServiceExtensions.cs` (expand `AddFixedAssets`)
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs` (map the run endpoints)
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsHostFixture.cs` (posting accounts + ledger repoint)
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunE2eTests.cs`

**Interfaces:**
- Consumes: `AddModule` + `ModuleIdentity` (`Accounting101.Ledger.Api.Hosting`), `ModuleCredential` + `IHttpContextAccessor` (for `HttpLedgerClient`, `Accounting101.Ledger.Api.Auth`), `FixedAssetsRunService`, `IDepreciationRunStore` + `DocumentDepreciationRunStore`, `IFixedAssetsAccountsProvider`, `IDepreciationMethod` impls + `DepreciationMethodSelector`, `ILedgerClient` + `HttpLedgerClient`, `DepreciationRunRequest` (domain).
- Produces: `RunDepreciationRequest(int Year, int Month, DateOnly? EffectiveDate, string? Memo)` (Api DTO) with `DepreciationRunRequest ToRequest()`; `VoidReasonRequest(string? Reason)` (Api DTO); the expanded `AddFixedAssets`; the four run endpoints.

- [ ] **Step 1: Write the HttpLedgerClient**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs` by copying `Modules/Payroll/Accounting101.Payroll.Api/HttpLedgerClient.cs` and changing: the namespace to `Accounting101.FixedAssets.Api`; the keyed credential attribute to `[FromKeyedServices("fixedassets")]`; the doc-comment module name to fixed-assets. Everything else (forwarding the bearer token, attaching `X-Module-Key`/`X-Module-Secret` on `PostAsync` only) is identical.

- [ ] **Step 2: Write the accounts provider**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Api/ConfiguredFixedAssetsAccountsProvider.cs`:

```csharp
using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Api;

/// <summary>Supplies the two depreciation posting accounts from configuration
/// (<c>FixedAssets:Accounts:DepreciationExpense|AccumulatedDepreciation</c>). No hardcoded numbers.</summary>
public sealed class ConfiguredFixedAssetsAccountsProvider(IConfiguration configuration) : IFixedAssetsAccountsProvider
{
    public Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new FixedAssetsPostingAccounts
        {
            DepreciationExpenseAccountId = Read("FixedAssets:Accounts:DepreciationExpense"),
            AccumulatedDepreciationAccountId = Read("FixedAssets:Accounts:AccumulatedDepreciation"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Fixed-assets posting account '{key}' is not configured.");
}
```

- [ ] **Step 3: Write the API request DTOs**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Api/DepreciationRunRequests.cs`:

```csharp
using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Api;

/// <summary>Run depreciation for a period. Amounts are server-computed; the caller supplies only the
/// period and optional overrides.</summary>
public sealed record RunDepreciationRequest(int Year, int Month, DateOnly? EffectiveDate, string? Memo)
{
    public DepreciationRunRequest ToRequest() => new(Year, Month, EffectiveDate, Memo);
}

/// <summary>Optional reason on a void.</summary>
public sealed record VoidReasonRequest(string? Reason);
```

- [ ] **Step 4: Expand AddFixedAssets**

Edit `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsServiceExtensions.cs`:

```csharp
using Accounting101.FixedAssets;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Api;

/// <summary>Installs the fixed-assets module: identity + manifest (reference "assets" + evidentiary
/// "depreciation-runs"), the document-store-backed stores, the depreciation strategies + selector, the
/// config-backed posting-accounts provider, the run service, and the loopback ledger HttpClient (FA-2 is
/// the first slice that posts).</summary>
public static class FixedAssetsServiceExtensions
{
    public static IServiceCollection AddFixedAssets(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("fixedassets"), "Fixed Assets", manifest =>
        {
            manifest.Reference("assets");
            manifest.Evidentiary("depreciation-runs");
        });

        services.AddScoped<IAssetStore>(sp =>
            new DocumentAssetStore(sp.GetRequiredKeyedService<IDocumentStore>("fixedassets")));
        services.AddScoped<IDepreciationRunStore>(sp =>
            new DocumentDepreciationRunStore(sp.GetRequiredKeyedService<IDocumentStore>("fixedassets")));
        services.AddScoped<FixedAssetsService>();
        services.AddScoped<FixedAssetsRunService>();

        services.AddSingleton<IDepreciationMethod, StraightLineDepreciation>();
        services.AddSingleton<IDepreciationMethod, DecliningBalanceDepreciation>();
        services.AddSingleton(sp => new DepreciationMethodSelector(sp.GetServices<IDepreciationMethod>()));
        services.AddSingleton<IFixedAssetsAccountsProvider, ConfiguredFixedAssetsAccountsProvider>();

        // Explicit client name to avoid the ILedgerClient short-name collision across modules.
        services.AddHttpClient("FixedAssetsLedgerClient", client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<ILedgerClient, HttpLedgerClient>();

        return services;
    }
}
```

> Implementer note: confirm the manifest builder exposes both `Reference(...)` and `Evidentiary(...)` (the FA-1 extension already calls `manifest.Reference("assets")`; Payroll calls `manifest.Evidentiary(...)`). `HttpLedgerClient` needs `IHttpContextAccessor`; the host already registers it (Payroll relies on the same). If a build error says it's missing, add `services.AddHttpContextAccessor();` — but check it isn't already added by the engine first to avoid a duplicate.

- [ ] **Step 5: Map the run endpoints**

Edit `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs` — register the run routes in `MapFixedAssetsEndpoints` and add handlers (mirror `PayrollEndpoints` run handlers):

```csharp
        clients.MapPost("/depreciation-runs", RunDepreciation);
        clients.MapPost("/depreciation-runs/{runId:guid}/void", VoidRun);
        clients.MapGet("/depreciation-runs/{runId:guid}", GetRun);
        clients.MapGet("/depreciation-runs", ListRuns);
```

```csharp
    private static async Task<IResult> RunDepreciation(
        Guid clientId, RunDepreciationRequest request, FixedAssetsRunService service, CancellationToken cancellationToken)
    {
        try
        {
            DepreciationRun run = await service.RunDepreciationAsync(clientId, request.ToRequest(), cancellationToken);
            return Results.Created($"/clients/{clientId}/depreciation-runs/{run.Id}", new DepreciationRunView(run));
        }
        catch (InvalidOperationException ex) // period already run
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex) // nothing to depreciate
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidRun(
        Guid clientId, Guid runId, VoidReasonRequest? request, FixedAssetsRunService service, CancellationToken cancellationToken)
    {
        try
        {
            DepreciationRun voided = await service.VoidRunAsync(clientId, runId, request?.Reason, cancellationToken);
            return Results.Ok(new DepreciationRunView(voided));
        }
        catch (InvalidOperationException ex) // not found, not posted, not latest, or no entry
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetRun(
        Guid clientId, Guid runId, FixedAssetsRunService service, CancellationToken cancellationToken)
    {
        DepreciationRun? run = await service.GetRunAsync(clientId, runId, cancellationToken);
        return run is null ? Results.NotFound() : Results.Ok(new DepreciationRunView(run));
    }

    private static async Task<IResult> ListRuns(
        Guid clientId, int? skip, int? limit, string? order, bool? includeVoided,
        IDepreciationRunStore store, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        PagedResponse<DepreciationRun> page = await store.GetByClientPagedAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeVoided ?? false, cancellationToken);
        return Results.Ok(new PagedResponse<DepreciationRunView>(
            page.Items.Select(r => new DepreciationRunView(r)).ToList(), page.Total, page.Skip, page.Limit));
    }
```

> Note: a `VoidRun` that hits the "not the latest run" or "not posted" path returns 409, matching the Payroll void's 409 mapping. `GetRun` returns 404 when missing.

- [ ] **Step 6: Extend the host fixture**

Edit `Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsHostFixture.cs` — add the two posting-account ids, publish them via `UseSetting`, and repoint the named ledger client at the in-memory server. Mirror `PayrollHostFixture`'s `ConfigureWebHost` (the `builder.UseSetting("Payroll:Accounts:...")` + `ConfigureTestServices` repoint of `"PayrollLedgerClient"`):

```csharp
    // The two depreciation posting accounts.
    public Guid DepreciationExpenseAccountId { get; } = Guid.NewGuid();
    public Guid AccumulatedDepreciationAccountId { get; } = Guid.NewGuid();
```

Add to `ConfigureWebHost` (after the Mongo settings):

```csharp
        builder.UseSetting("FixedAssets:Accounts:DepreciationExpense", DepreciationExpenseAccountId.ToString());
        builder.UseSetting("FixedAssets:Accounts:AccumulatedDepreciation", AccumulatedDepreciationAccountId.ToString());

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("FixedAssetsLedgerClient", c => c.BaseAddress = new Uri("http://localhost"))
                    .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
```

> Implementer note: `ConfigureTestServices` needs `using Microsoft.AspNetCore.TestHost;` and `using Microsoft.Extensions.DependencyInjection;` (see `PayrollHostFixture`). `Server.CreateHandler()` is the `WebApplicationFactory` in-memory handler.

- [ ] **Step 7: Write the run E2E tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunE2eTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

/// <summary>Proves depreciation runs end-to-end through the real host: a run advances each asset's
/// accumulated depreciation and posts one balanced PendingApproval entry stamped ViaModule="fixedassets";
/// the period guard and nothing-to-depreciate rules hold; a LIFO void reverses the entry and rolls
/// accumulated depreciation back.</summary>
public sealed class DepreciationRunE2eTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        await PutAccountAsync(http, clientId, fixture.DepreciationExpenseAccountId,     "6200", "Depreciation Expense",     "Expense");
        await PutAccountAsync(http, clientId, fixture.AccumulatedDepreciationAccountId, "1590", "Accumulated Depreciation", "Asset");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new { Number = number, Name = name, Type = type, RequiredDimension = (string?)null }))
            .EnsureSuccessStatusCode();

    private static async Task<AssetView> CreateAssetAsync(HttpClient http, Guid clientId, SaveAssetRequest req) =>
        (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets", req)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<AssetView>())!;

    [Fact]
    public async Task Run_advances_accumulated_and_posts_one_pending_entry_via_fixedassets()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(); // Controller (holds fixedassets.write)
        await SetUpChartAsync(http, clientId);

        AssetView sl = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("SL Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null)); // 500/mo
        AssetView db = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("DB Rig", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.DecliningBalance, 2.0m)); // 1000 first mo

        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, "January depreciation"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DepreciationRunView run = (await created.Content.ReadFromJsonAsync<DepreciationRunView>())!;
        Assert.Equal(1500m, run.Run.Total); // 500 + 1000

        // Assets advanced.
        AssetView slAfter = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{sl.Asset.Id}"))!;
        AssetView dbAfter = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{db.Asset.Id}"))!;
        Assert.Equal(500m, slAfter.Asset.AccumulatedDepreciation);
        Assert.Equal(1000m, dbAfter.Asset.AccumulatedDepreciation);

        // One balanced PendingApproval entry via fixedassets.
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Run.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal("fixedassets", entry.ViaModule);
        Assert.Equal("PendingApproval", entry.Posting);
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(1500m, entry.Lines.Single(l => l.AccountId == fixture.DepreciationExpenseAccountId && l.Direction == "Debit").Amount);
        Assert.Equal(1500m, entry.Lines.Single(l => l.AccountId == fixture.AccumulatedDepreciationAccountId && l.Direction == "Credit").Amount);
    }

    [Fact]
    public async Task Second_run_for_the_same_period_returns_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null)))
            .EnsureSuccessStatusCode();

        HttpResponseMessage second = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_period_with_no_eligible_assets_returns_422()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        // Asset goes into service after the run period.
        await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 6, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        HttpResponseMessage run = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, run.StatusCode);
    }

    [Fact]
    public async Task Void_latest_run_reverses_entry_and_rolls_back_accumulated()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        AssetView sl = await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        DepreciationRunView run = (await (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, null))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DepreciationRunView>())!;

        HttpResponseMessage voided = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs/{run.Run.Id}/void", new VoidReasonRequest("entered in error"));
        voided.EnsureSuccessStatusCode();
        DepreciationRunView voidedRun = (await voided.Content.ReadFromJsonAsync<DepreciationRunView>())!;
        Assert.Equal(DepreciationRunStatus.Voided, voidedRun.Run.Status);

        // Accumulated rolled back.
        AssetView after = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{sl.Asset.Id}"))!;
        Assert.Equal(0m, after.Asset.AccumulatedDepreciation);

        // Entry voided/reversed on the books.
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={run.Run.Id}"))!;
        // A pending entry is withdrawn (single, Voided); a posted one leaves the original + a reversal.
        Assert.Contains(entries, e => e.Status == "Voided" || e.ReversalOf != null);
    }

    [Fact]
    public async Task Voiding_a_non_latest_run_returns_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        await CreateAssetAsync(http, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));
        DepreciationRunView jan = (await (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs",
            new RunDepreciationRequest(2026, 1, null, null))).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<DepreciationRunView>())!;
        (await http.PostAsJsonAsync($"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 2, null, null)))
            .EnsureSuccessStatusCode();

        HttpResponseMessage voidJan = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs/{jan.Run.Id}/void", new VoidReasonRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, voidJan.StatusCode);
    }

    [Fact]
    public async Task A_member_without_write_cannot_run_depreciation()
    {
        (Guid clientId, HttpClient controller) = await fixture.SeedClientAsync(); // Controller sets up chart
        await SetUpChartAsync(controller, clientId);
        await CreateAssetAsync(controller, clientId,
            new SaveAssetRequest("Van", 12000m, new DateOnly(2026, 1, 1), 24, 0m, DepreciationMethod.StraightLine, null));

        // An Auditor (read-only) attempts a run.
        (Guid _, HttpClient auditor) = await fixture.SeedClientAsync(Accounting101.Ledger.Api.Control.LedgerRole.Auditor);
        // NOTE: auditor is a DIFFERENT client here; instead assert on the same client with a read-only member.
        // Use the capability test seam the FA-1 capability test uses (read that test for the exact seeding).
        HttpResponseMessage run = await auditor.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
    }

    [Fact]
    public async Task A_client_not_entitled_to_fixedassets_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(enabledModules: []); // no fixedassets entitlement
        HttpResponseMessage run = await http.PostAsJsonAsync(
            $"/clients/{clientId}/depreciation-runs", new RunDepreciationRequest(2026, 1, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
    }
}
```

> Implementer note — capability/entitlement tests: read the FA-1 `FixedAssetsEndpointsTests` capability/entitlement cases (`A_client_not_entitled_to_fixedassets_is_403`, the Auditor read-only case) and reuse their EXACT seeding approach. The `A_member_without_write...` test above is a sketch — replace it with the FA-1 pattern of seeding a single client with a read-only member on the SAME client (not a second client) so the 403 is a capability denial, not a wrong-client denial. Keep the entitlement test (`enabledModules: []`) — `SeedClientAsync` already supports the `enabledModules` parameter (FA-1 fixture). The account `Type` for Accumulated Depreciation is `"Asset"` (a contra-asset credited normally; the engine validates balance, not normal-balance sign).

- [ ] **Step 8: Run the whole module suite**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS — all FA-1 + FA-2 tests. Report the count.

- [ ] **Step 9: Build + verify the slnx is unaffected**

The `.Api` project is already in the slnx (FA-1). No new project, so **do not touch `Accounting101.slnx`**. Confirm:

Run: `dotnet build Accounting101.slnx -c Debug --nologo`
Expected: Build succeeded, 0 errors.
Run: `git status --short Accounting101.slnx`
Expected: no output (slnx unchanged).

- [ ] **Step 10: Commit**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets.Api/HttpLedgerClient.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/ConfiguredFixedAssetsAccountsProvider.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/DepreciationRunRequests.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsServiceExtensions.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsHostFixture.cs \
        Modules/FixedAssets/Accounting101.FixedAssets.Tests/DepreciationRunE2eTests.cs
git commit -m "feat(fixedassets): host wiring + depreciation-run endpoints + HTTP E2E

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: Whole-solution green + whole-branch review + memory

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build Accounting101.slnx -c Debug --nologo`
Expected: Build succeeded, 0 errors (pre-existing NuGet advisory warnings on `Accounting101.TestSupport` are unrelated and allowed).

- [ ] **Step 2: Run the whole solution suite**

Run: `dotnet test Accounting101.slnx -c Debug --no-build --nologo`
Expected: all assemblies green. Baseline was 890; FA-2 adds ~40 tests (12 strategy + 5 posting + 4 run-store + 7 lifecycle-store + 6 run-service + ~7 run-E2E + endpoint additions). Report the total.

- [ ] **Step 3: Whole-branch review**

Dispatch the whole-branch reviewer (opus) over `master..HEAD`. Focus: server-owned accumulated-depreciation integrity (advance on run / roll back on void, never client-settable); LIFO void coherence; period-guard idempotency; the depreciation math (SL remainder cap, DB salvage floor, cent rounding); full-month eligibility; sticky-deactivation correctness; the module posts PendingApproval via `fixedassets` with the named-client credential; slnx untouched; scope discipline (no disposals/UI). Address any Critical/Important findings before merge; record Minor/Nit as deferred.

- [ ] **Step 4: Merge, push, delete branch, update memory**

After review passes: merge `feat/fixed-assets-fa2` into `master` with `--no-ff`, push, delete the branch, and update the FA memory file (`accounting101-fixed-assets-fa1.md` → note FA-2 shipped, resolve the deactivation-stickiness deferral, list new deferrals) + the `MEMORY.md` pointer.

---

## Self-Review

**Spec coverage:**
- Pluggable strategy (SL + DB, salvage floor, no crossover, cent rounding) → Task 1. ✓
- Run domain + aggregate posting recipe (period, lines, LIFO status, `DepreciationRunSourceType`) → Task 2. ✓
- Evidentiary run store + period-guard + latest queries → Task 3. ✓
- Sticky deactivation + reactivate + apply/reverse accumulated mutations → Task 4. ✓
- Orchestration (period guard, eligibility incl. full-month + in-service, nothing-to-depreciate 422, advance, PendingApproval post, LIFO void reverse+rollback) → Task 5 (unit) + Task 6 (E2E). ✓
- Configured account pair + provider + named `HttpLedgerClient` + `AddFixedAssets` manifest `Evidentiary("depreciation-runs")` → Task 6. ✓
- Endpoints (run/void/get/list + reactivate + sticky 409) → Tasks 4 + 6. ✓
- Capability/entitlement enforcement on new endpoints (inherited via chokepoint) → Task 6 E2E. ✓
- Whole-solution green + review + memory → Task 7. ✓

**Type consistency:** `IDepreciationMethod.DepreciationForPeriod(Asset)` (T1) is consumed by `FixedAssetsRunService` via `DepreciationMethodSelector.For` (T1→T5). `DepreciationRunLine`/`DepreciationRunBody`/`DepreciationRun` (T2) flow through the store (T3), the asset apply/reverse (T4 `IReadOnlyList<DepreciationRunLine>`), and the service (T5). `FixedAssetsPosting.ComposeDepreciationRun(Guid,decimal,DateOnly,string?,FixedAssetsPostingAccounts)` (T2) is called with those exact args in T5. `IAssetStore.UpdateAsync` return type change to `Task<UpdateResult>` (T4) is consumed by the service+endpoint in the SAME task (stays green). `ILedgerClient` (T5) is implemented by `HttpLedgerClient` (T6) and registered under the typed client. `RunDepreciationRequest` (Api, T6) maps to `DepreciationRunRequest` (domain, T5) via `ToRequest()`.

**Placeholder scan:** all code steps carry complete code. No "TBD"/"handle edge cases"/"similar to Task N" placeholders. Two engine-dependency questions that could have derailed execution were resolved against the real engine before finalizing this plan: (1) the manifest builder is fluent — `ModuleManifestBuilder().Reference("assets").Evidentiary("depreciation-runs").Build()` (verified `Backend/.../Documents/ModuleManifest.cs:49-50`), reflected in the T3 fixture; (2) there is NO `ReactivateAsync` primitive — reactivation is a deliberate re-`Put` because `PutReferenceAsync` always sets `Active` (verified `ScopedDocumentStore.cs:259`), reflected in T4's `ReactivateAsync` + its implementer note. The remaining "Implementer note" callouts (capability-seeding reuse in T6, fixture accessor shapes) point at named template files for idiom-matching, not deferred work.
