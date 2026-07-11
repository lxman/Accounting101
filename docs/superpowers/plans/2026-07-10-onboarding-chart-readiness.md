# Onboarding Chart Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an advisory per-module `GET /clients/{id}/<key>/chart-readiness` endpoint (6 modules) that reports whether a client's chart satisfies that module's account requirements (existence, Active, type, and required dimensions), naming exactly what to fix — surfacing the fold-misconfig fault proactively without blocking any workflow.

**Architecture:** All logic lives in shared ModuleKit: `ChartReadinessChecker` (pure), the `AccountRequirement`/`ChartReadinessReport` DTOs (domain-safe `Accounting101.ModuleKit`), and `ModuleLedgerClient.GetAccountsAsync` (`Accounting101.ModuleKit.Api`). Each module adds a tiny `<Module>ChartRequirements` builder (from its existing `AccountsProvider` + the now-explicit type/dimension knowledge) and a ~5-line endpoint. The engine is untouched — it already exposes `GET /clients/{id}/accounts` returning `Type` + `RequiredDimensions`.

**Tech Stack:** C# / .NET 10 minimal-API modular monolith, xUnit + EphemeralMongo host fixtures, ModuleKit (merged `16687ba`).

## Global Constraints

- **Advisory, always 200.** The endpoint returns `200` with the report for any reachable chart; an unready chart is `ready:false`, never a 4xx. It blocks nothing. If `GetAccountsAsync` itself fails (engine auth/etc.), that relays as a 4xx via the existing ModuleKit middleware — no new error surface.
- **No engine change.** Nothing under `Backend/`. The check is a pure comparison over `AccountResponse` (which already carries `Type`, `RequiredDimensions`, `Active`).
- **6 modules only** — Receivables, Payables, Fixed Assets, Inventory, Cash, Payroll. **Reconciliation is excluded** (no config-fixed account contract; its accounts are runtime/per-statement; its bank account is already covered by Cash).
- **Dimension semantics = SUBSET.** An account satisfies a requirement's `RequiredDimensions` iff every declared dimension is contained in the account's `RequiredDimensions` (an account may require *more*). Status precedence: `Missing` → `Inactive` → `WrongType` → `MissingDimensions` → `Ok`. `ready == all Ok`.
- **Route segment = module key**: `/clients/{id}/{key}/chart-readiness` where key ∈ {`receivables`,`payables`,`fixedassets`,`inventory`,`cash`,`payroll`}. Added to each module's existing `MapGroup("/clients/{clientId:guid}").RequireAuthorization()`.
- **`AccountResponse` positional shape** (from `Ledger.Contracts`): `(Guid Id, string Number, string Name, string Type, Guid? ParentId, bool Postable, string? RequiredDimension, IReadOnlyList<string> RequiredDimensions, string? CashFlowActivity, bool IsRetainedEarnings, bool Active, string NormalSide, bool IsTemporary)` — use `.Id`, `.Number`, `.Type`, `.RequiredDimensions`, `.Active`.
- **VendorCredits is `Asset`** (debit-normal, deliberate — not symmetric with CustomerCredits which is `Liability`). Encode exactly as the coverage tables below.
- Branch `feat/onboarding-chart-readiness` off master. Commit per task.

## File Structure

- **Task 1 (ModuleKit shared machinery):**
  - Create `Modules/Shared/Accounting101.ModuleKit/AccountRequirement.cs` (DTOs + status enum)
  - Create `Modules/Shared/Accounting101.ModuleKit/ChartReadinessChecker.cs`
  - Modify `Modules/Shared/Accounting101.ModuleKit.Api/ModuleLedgerClient.cs` (+`GetAccountsAsync`)
  - Create `Modules/Shared/Accounting101.ModuleKit.Tests/ChartReadinessCheckerTests.cs`
  - Modify `Modules/Shared/Accounting101.ModuleKit.Tests/ModuleLedgerClientTests.cs` (+`GetAccounts` client test)
- **Tasks 2–7 (per module):** each adds `<Module>ChartRequirements.cs` + a handler in the module's `*Endpoints.cs` + a `ChartReadinessE2eTests.cs`.

Task order: T1 shared → T2 FixedAssets (pattern-setter) → T3 Inventory → T4 Receivables → T5 Payables → T6 Cash → T7 Payroll.

---

### Task 1: ModuleKit shared machinery (checker + DTOs + GetAccountsAsync)

**Files:**
- Create: `Modules/Shared/Accounting101.ModuleKit/AccountRequirement.cs`
- Create: `Modules/Shared/Accounting101.ModuleKit/ChartReadinessChecker.cs`
- Modify: `Modules/Shared/Accounting101.ModuleKit.Api/ModuleLedgerClient.cs`
- Test: `Modules/Shared/Accounting101.ModuleKit.Tests/ChartReadinessCheckerTests.cs`
- Test: `Modules/Shared/Accounting101.ModuleKit.Tests/ModuleLedgerClientTests.cs`

**Interfaces:**
- Produces: `AccountRequirement(Guid, string, string?, IReadOnlyList<string>)`; `AccountReadinessStatus` enum; `AccountReadinessResult`; `ChartReadinessReport(string ModuleKey, bool Ready, IReadOnlyList<AccountReadinessResult> Accounts)`; `ChartReadinessChecker.Check(reqs, chart, moduleKey)`; `ModuleLedgerClient.GetAccountsAsync(Guid, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Shared/Accounting101.ModuleKit.Tests/ChartReadinessCheckerTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.ModuleKit.Tests;

public sealed class ChartReadinessCheckerTests
{
    private static AccountResponse Acct(Guid id, string type, string[] dims, bool active = true) =>
        new(id, "1000", "Acct", type, null, true, null, dims, null, false, active, "Debit", false);

    private static AccountRequirement Req(Guid id, string? type, string[] dims) =>
        new(id, "Label", type, dims);

    [Fact]
    public void Ok_when_account_exists_active_right_type_and_covers_dims()
    {
        Guid id = Guid.NewGuid();
        ChartReadinessReport r = ChartReadinessChecker.Check(
            [Req(id, "Asset", ["Item"])], [Acct(id, "Asset", ["Item"])], "inventory");
        Assert.True(r.Ready);
        Assert.Equal(AccountReadinessStatus.Ok, Assert.Single(r.Accounts).Status);
    }

    [Fact]
    public void Missing_when_no_account_with_that_id()
    {
        ChartReadinessReport r = ChartReadinessChecker.Check(
            [Req(Guid.NewGuid(), "Asset", [])], [], "inventory");
        Assert.False(r.Ready);
        Assert.Equal(AccountReadinessStatus.Missing, Assert.Single(r.Accounts).Status);
    }

    [Fact]
    public void MissingDimensions_when_account_lacks_a_required_dimension()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, "Asset", ["Item"])], [Acct(id, "Asset", [])], "inventory").Accounts);
        Assert.Equal(AccountReadinessStatus.MissingDimensions, res.Status);
    }

    [Fact]
    public void Subset_semantics_ok_when_account_requires_more_dimensions_than_needed()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, "Asset", ["Customer"])], [Acct(id, "Asset", ["Customer", "Invoice"])], "receivables").Accounts);
        Assert.Equal(AccountReadinessStatus.Ok, res.Status);
    }

    [Fact]
    public void WrongType_when_type_differs()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, "Asset", [])], [Acct(id, "Liability", [])], "cash").Accounts);
        Assert.Equal(AccountReadinessStatus.WrongType, res.Status);
    }

    [Fact]
    public void Inactive_when_account_is_deactivated()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, "Asset", [])], [Acct(id, "Asset", [], active: false)], "cash").Accounts);
        Assert.Equal(AccountReadinessStatus.Inactive, res.Status);
    }

    [Fact]
    public void Missing_takes_precedence_and_report_not_ready_with_mixed_results()
    {
        Guid ok = Guid.NewGuid(), gone = Guid.NewGuid();
        ChartReadinessReport r = ChartReadinessChecker.Check(
            [Req(ok, "Asset", []), Req(gone, "Asset", ["Item"])],
            [Acct(ok, "Asset", [])], "inventory");
        Assert.False(r.Ready);
        Assert.Equal(AccountReadinessStatus.Ok, r.Accounts[0].Status);
        Assert.Equal(AccountReadinessStatus.Missing, r.Accounts[1].Status);
    }

    [Fact]
    public void Null_expected_type_skips_type_check()
    {
        Guid id = Guid.NewGuid();
        AccountReadinessResult res = Assert.Single(ChartReadinessChecker.Check(
            [Req(id, null, [])], [Acct(id, "Liability", [])], "cash").Accounts);
        Assert.Equal(AccountReadinessStatus.Ok, res.Status);
    }
}
```

Add a `GetAccounts` test method to `Modules/Shared/Accounting101.ModuleKit.Tests/ModuleLedgerClientTests.cs` (reuse the file's existing `CapturingHandler`, `ContextWith`, `DummyCredential`, and `TestLedgerClient`):

```csharp
    [Fact]
    public async Task GetAccounts_forwards_auth_and_targets_the_accounts_endpoint()
    {
        CapturingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<AccountResponse>()),
            },
        };
        HttpClient http = new(handler) { BaseAddress = new Uri("http://engine.local") };
        TestLedgerClient client = new(http, ContextWith("DevToken abc"), DummyCredential());

        Guid clientId = Guid.NewGuid();
        await client.GetAccountsAsync(clientId);

        Assert.Equal(HttpMethod.Get, handler.Last!.Method);
        Assert.Equal($"http://engine.local/clients/{clientId}/accounts", handler.Last.RequestUri!.ToString());
        Assert.Equal("DevToken abc", handler.Last.Headers.GetValues("Authorization").Single());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests`
Expected: FAIL to compile — `ChartReadinessChecker`, the DTOs, and `GetAccountsAsync` don't exist yet.

- [ ] **Step 3: Create the DTOs**

`Modules/Shared/Accounting101.ModuleKit/AccountRequirement.cs`:

```csharp
namespace Accounting101.ModuleKit;

/// <summary>The status of one required account against a client's chart.</summary>
public enum AccountReadinessStatus { Ok, Missing, Inactive, WrongType, MissingDimensions }

/// <summary>A module's declared expectation for one chart account it posts to or folds.
/// <paramref name="ExpectedType"/> null = don't check type; <paramref name="RequiredDimensions"/>
/// empty = the account only needs to exist.</summary>
public sealed record AccountRequirement(
    Guid AccountId,
    string Label,
    string? ExpectedType,
    IReadOnlyList<string> RequiredDimensions);

/// <summary>The evaluation of one <see cref="AccountRequirement"/> against the chart.</summary>
public sealed record AccountReadinessResult(
    Guid AccountId,
    string Label,
    string? ExpectedType,
    IReadOnlyList<string> RequiredDimensions,
    AccountReadinessStatus Status,
    string? ActualType,
    IReadOnlyList<string>? ActualRequiredDimensions,
    string Detail);

/// <summary>The readiness of a client's chart for one module. <see cref="Ready"/> is true iff every
/// account is <see cref="AccountReadinessStatus.Ok"/>.</summary>
public sealed record ChartReadinessReport(
    string ModuleKey,
    bool Ready,
    IReadOnlyList<AccountReadinessResult> Accounts);
```

- [ ] **Step 4: Create the checker**

`Modules/Shared/Accounting101.ModuleKit/ChartReadinessChecker.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.ModuleKit;

/// <summary>
/// Pure comparison of a module's declared <see cref="AccountRequirement"/>s against a client's chart.
/// A required account must exist at its id, be Active, be the expected type (when declared), and — for
/// folded accounts — carry the required dimensions (subset: the account may require more). Reports the
/// most fundamental problem first (Missing → Inactive → WrongType → MissingDimensions → Ok).
/// </summary>
public static class ChartReadinessChecker
{
    public static ChartReadinessReport Check(
        IReadOnlyList<AccountRequirement> requirements,
        IReadOnlyList<AccountResponse> chart,
        string moduleKey)
    {
        Dictionary<Guid, AccountResponse> byId = chart.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        List<AccountReadinessResult> results = requirements.Select(req => Evaluate(req, byId)).ToList();
        return new ChartReadinessReport(moduleKey, results.All(r => r.Status == AccountReadinessStatus.Ok), results);
    }

    private static AccountReadinessResult Evaluate(AccountRequirement req, IReadOnlyDictionary<Guid, AccountResponse> byId)
    {
        if (!byId.TryGetValue(req.AccountId, out AccountResponse? a))
            return Result(req, AccountReadinessStatus.Missing, null, null,
                $"No account with id {req.AccountId} ('{req.Label}') exists in the chart.");

        if (!a.Active)
            return Result(req, AccountReadinessStatus.Inactive, a.Type, a.RequiredDimensions,
                $"Account '{req.Label}' ({a.Number}) exists but is inactive.");

        if (req.ExpectedType is { } expected && !string.Equals(a.Type, expected, StringComparison.OrdinalIgnoreCase))
            return Result(req, AccountReadinessStatus.WrongType, a.Type, a.RequiredDimensions,
                $"Account '{req.Label}' ({a.Number}) is {a.Type}, expected {expected}.");

        List<string> missing = req.RequiredDimensions.Where(d => !a.RequiredDimensions.Contains(d)).ToList();
        if (missing.Count > 0)
            return Result(req, AccountReadinessStatus.MissingDimensions, a.Type, a.RequiredDimensions,
                $"Account '{req.Label}' ({a.Number}) must require the " +
                $"{string.Join(", ", missing.Select(d => $"'{d}'"))} dimension(s) for the module's fold.");

        return Result(req, AccountReadinessStatus.Ok, a.Type, a.RequiredDimensions, "OK.");
    }

    private static AccountReadinessResult Result(
        AccountRequirement req, AccountReadinessStatus status,
        string? actualType, IReadOnlyList<string>? actualDims, string detail) =>
        new(req.AccountId, req.Label, req.ExpectedType, req.RequiredDimensions, status, actualType, actualDims, detail);
}
```

- [ ] **Step 5: Add `GetAccountsAsync` to the base client**

In `Modules/Shared/Accounting101.ModuleKit.Api/ModuleLedgerClient.cs`, add this read method (alongside the other reads; it forwards the bearer, attaches no credential):

```csharp
    public async Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/accounts");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<List<AccountResponse>>(cancellationToken))!;
    }
```

(`AccountResponse` is already in `Accounting101.Ledger.Contracts`, which this file already uses.)

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests`
Expected: PASS — checker truth table (8) + the existing 11 + the new `GetAccounts` client test.

- [ ] **Step 7: Build the whole solution**

Run: `dotnet build Accounting101.slnx -m:1`
Expected: SUCCESS (no consumers yet; additive).

- [ ] **Step 8: Commit**

```bash
git add Modules/Shared/Accounting101.ModuleKit Modules/Shared/Accounting101.ModuleKit.Api Modules/Shared/Accounting101.ModuleKit.Tests
git commit -m "feat(modulekit): ChartReadinessChecker + AccountRequirement DTOs + GetAccountsAsync"
```

---

### Task 2: Fixed Assets chart-readiness (pattern-setter)

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsChartRequirements.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsEndpoints.cs`
- Test: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/ChartReadinessE2eTests.cs`

**Interfaces:**
- Consumes: `IFixedAssetsAccountsProvider.GetAccountsAsync(clientId)` → `FixedAssetsPostingAccounts` (props `DepreciationExpenseAccountId`, `AccumulatedDepreciationAccountId`, `AssetCostAccountId`, `DisposalProceedsAccountId`, `GainOnDisposalAccountId`, `LossOnDisposalAccountId`); `ModuleLedgerClient.GetAccountsAsync`; `ChartReadinessChecker.Check`; `ILedgerClient` (the module's, satisfied by the base).

- [ ] **Step 1: Write the failing E2E test**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/ChartReadinessE2eTests.cs`. Use `FixedAssetsHostFixture`. One test: a correctly-configured chart → `ready:true`; a second: the Accumulated Depreciation account set up WITHOUT `["Asset"]` → `ready:false` with that account's status `MissingDimensions`. Mirror `LedgerErrorRelayE2eTests`'s chart-setup helpers (it already builds both correct and misconfigured charts).

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.FixedAssets.Tests;

public sealed class ChartReadinessE2eTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    [Fact]
    public async Task Correct_chart_is_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, accumDims: ["Asset"]);

        ChartReadinessReport report = (await http.GetFromJsonAsync<ChartReadinessReport>(
            $"/clients/{clientId}/fixedassets/chart-readiness"))!;

        Assert.Equal("fixedassets", report.ModuleKey);
        Assert.True(report.Ready);
        Assert.All(report.Accounts, a => Assert.Equal(AccountReadinessStatus.Ok, a.Status));
    }

    [Fact]
    public async Task Accum_account_without_asset_dimension_is_not_ready()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId, accumDims: null); // misconfig

        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/fixedassets/chart-readiness");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // advisory — 200 even when not ready
        ChartReadinessReport report = (await resp.Content.ReadFromJsonAsync<ChartReadinessReport>())!;

        Assert.False(report.Ready);
        AccountReadinessResult accum = Assert.Single(report.Accounts,
            a => a.AccountId == fixture.AccumulatedDepreciationAccountId);
        Assert.Equal(AccountReadinessStatus.MissingDimensions, accum.Status);
    }

    private async Task SetUpChartAsync(HttpClient http, Guid clientId, IReadOnlyList<string>? accumDims)
    {
        await Put(http, clientId, fixture.DepreciationExpenseAccountId,     "6200", "Depreciation Expense",     "Expense", null);
        await Put(http, clientId, fixture.AccumulatedDepreciationAccountId, "1590", "Accumulated Depreciation", "Asset",   accumDims);
        await Put(http, clientId, fixture.AssetCostAccountId,        "1500", "Fixed Assets",     "Asset",   null);
        await Put(http, clientId, fixture.DisposalProceedsAccountId, "1000", "Cash",             "Asset",   null);
        await Put(http, clientId, fixture.GainOnDisposalAccountId,   "7100", "Gain on Disposal", "Revenue", null);
        await Put(http, clientId, fixture.LossOnDisposalAccountId,   "7200", "Loss on Disposal", "Expense", null);
    }

    private static async Task Put(HttpClient http, Guid clientId, Guid id, string number, string name, string type,
        IReadOnlyList<string>? dims) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimensions = dims }))
            .EnsureSuccessStatusCode();
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter ChartReadinessE2eTests`
Expected: FAIL — the `/fixedassets/chart-readiness` route doesn't exist (404).

- [ ] **Step 3: Create the requirements builder**

`Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsChartRequirements.cs`:

```csharp
using Accounting101.ModuleKit;

namespace Accounting101.FixedAssets.Api;

/// <summary>Declares the chart accounts the fixed-assets recipes post to and fold, for readiness checks.
/// The Accumulated Depreciation account must require the "Asset" dimension its per-asset fold reads.</summary>
public sealed class FixedAssetsChartRequirements(IFixedAssetsAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        FixedAssetsPostingAccounts a = await accounts.GetAccountsAsync(clientId, ct);
        return
        [
            new(a.AccumulatedDepreciationAccountId, "Accumulated Depreciation", "Asset", ["Asset"]),
            new(a.DepreciationExpenseAccountId,     "Depreciation Expense",     "Expense", []),
            new(a.AssetCostAccountId,               "Fixed Assets (asset cost)", "Asset",  []),
            new(a.DisposalProceedsAccountId,        "Disposal Proceeds",        "Asset",   []),
            new(a.GainOnDisposalAccountId,          "Gain on Disposal",         "Revenue", []),
            new(a.LossOnDisposalAccountId,          "Loss on Disposal",         "Expense", []),
        ];
    }
}
```

Register it in `FixedAssetsServiceExtensions.cs` (`services.AddScoped<FixedAssetsChartRequirements>();`).

- [ ] **Step 4: Add the endpoint**

In `FixedAssetsEndpoints.cs`, add to the `MapGroup("/clients/{clientId:guid}")` block:

```csharp
clients.MapGet("/fixedassets/chart-readiness", ChartReadiness);
```

and the handler:

```csharp
private static async Task<IResult> ChartReadiness(
    Guid clientId, FixedAssetsChartRequirements requirements, ILedgerClient ledger, CancellationToken cancellationToken)
{
    IReadOnlyList<AccountRequirement> reqs = await requirements.ForAsync(clientId, cancellationToken);
    IReadOnlyList<AccountResponse> chart = await ledger.GetAccountsAsync(clientId, cancellationToken);
    return Results.Ok(ChartReadinessChecker.Check(reqs, chart, "fixedassets"));
}
```

Add `using Accounting101.ModuleKit;` and `using Accounting101.Ledger.Contracts;` if not present. **Note:** `ILedgerClient` must expose `GetAccountsAsync` — since the concrete client is a `ModuleLedgerClient` subclass, add `Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default);` to the module's `ILedgerClient` interface (`Modules/FixedAssets/Accounting101.FixedAssets/ILedgerClient.cs`) so the handler can call it through the interface. The inherited base method satisfies it.

- [ ] **Step 5: Run the E2E to verify it passes**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter ChartReadinessE2eTests`
Expected: PASS — correct chart `ready:true`; missing-dimension chart `ready:false` with `MissingDimensions` on accum.

- [ ] **Step 6: Run the full FA suite**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS (previous 112 + 2 new).

- [ ] **Step 7: Commit**

```bash
git add Modules/FixedAssets
git commit -m "feat(fixedassets): chart-readiness endpoint"
```

---

### Task 3: Inventory chart-readiness

**Files:**
- Create: `Modules/Inventory/Accounting101.Inventory.Api/InventoryChartRequirements.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/InventoryEndpoints.cs`, `.../InventoryServiceExtensions.cs`, `Modules/Inventory/Accounting101.Inventory/ILedgerClient.cs`
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/ChartReadinessE2eTests.cs`

**Interfaces:** Consumes `IInventoryAccountsProvider.GetAccountsAsync(clientId)` → `InventoryPostingAccounts` (`InventoryAssetAccountId`, `CogsAccountId`, `GrniClearingAccountId`, `InventoryAdjustmentAccountId`). Follows Task 2's pattern exactly.

- [ ] **Step 1: Write the failing E2E** — `ChartReadinessE2eTests` in the Inventory test project, `InventoryHostFixture`. Correct chart (Inventory Asset with `["Item"]`) → `ready:true`; Inventory Asset without `["Item"]` → `ready:false` with `MissingDimensions` on `fixture.InventoryAssetAccountId`. Mirror the Inventory `LedgerErrorRelayE2eTests` chart-setup (accounts `1400` Asset/`["Item"]`, `5000` COGS/Expense, `2100` GRNI/Liability, `5100` Adjustment/Expense). Route: `/clients/{id}/inventory/chart-readiness`, `moduleKey` `"inventory"`.

- [ ] **Step 2: Run to verify it fails** — Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter ChartReadinessE2eTests` → FAIL (404).

- [ ] **Step 3: Create `InventoryChartRequirements.cs`:**

```csharp
using Accounting101.ModuleKit;

namespace Accounting101.Inventory.Api;

public sealed class InventoryChartRequirements(IInventoryAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        InventoryPostingAccounts a = await accounts.GetAccountsAsync(clientId, ct);
        return
        [
            new(a.InventoryAssetAccountId,       "Inventory Asset",     "Asset",     ["Item"]),
            new(a.CogsAccountId,                 "Cost of Goods Sold",  "Expense",   []),
            new(a.GrniClearingAccountId,         "GRNI Clearing",       "Liability", []),
            new(a.InventoryAdjustmentAccountId,  "Inventory Adjustment","Expense",   []),
        ];
    }
}
```

Register `services.AddScoped<InventoryChartRequirements>();` in `InventoryServiceExtensions.cs`.

- [ ] **Step 4: Add the endpoint + interface method** — in `InventoryEndpoints.cs` add `clients.MapGet("/inventory/chart-readiness", ChartReadiness);` and the handler (identical shape to Task 2 Step 4, `moduleKey` `"inventory"`, inject `InventoryChartRequirements`). Add `GetAccountsAsync` to `Modules/Inventory/Accounting101.Inventory/ILedgerClient.cs`.

- [ ] **Step 5: Run the E2E** — PASS.

- [ ] **Step 6: Full Inventory suite** — Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests` → PASS (94 + 2).

- [ ] **Step 7: Commit** — `git add Modules/Inventory && git commit -m "feat(inventory): chart-readiness endpoint"`.

---

### Task 4: Receivables chart-readiness

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesChartRequirements.cs`
- Modify: `.../ReceivablesEndpoints.cs`, `.../ReceivablesServiceExtensions.cs`, `Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/ChartReadinessE2eTests.cs`

**Interfaces:** Consumes `IInvoiceAccountsProvider.GetAsync(clientId, ct)` (→ `InvoicePostingAccounts`: `ReceivableAccountId`, `DefaultRevenueAccountId`, `SalesTaxPayableAccountId`) and `IPaymentAccountsProvider.GetAsync(clientId, ct)` (→ `PaymentPostingAccounts`: `ReceivableAccountId`, `CashAccountId`, `CustomerCreditsAccountId`, `BadDebtExpenseAccountId`, `SalesReturnsAccountId`). Note: the AR providers use `GetAsync` (not `GetAccountsAsync` like the other modules).

- [ ] **Step 1: Write the failing E2E** — Correct chart → `ready:true`; A/R (Receivable) account set up WITHOUT `["Customer","Invoice"]` → `ready:false` with `MissingDimensions` on the receivable account. Also assert Customer Credits requires `["Customer"]`. Use `ReceivablesHostFixture`; mirror the AR proof/relay chart-setup (Receivable `["Customer","Invoice"]`, Customer Credits `["Customer"]`, Revenue/SalesTax/Cash/BadDebt/SalesReturns plain). Route `/clients/{id}/receivables/chart-readiness`, key `"receivables"`.

- [ ] **Step 2: Run to verify it fails** — FAIL (404).

- [ ] **Step 3: Create `ReceivablesChartRequirements.cs`** — injects BOTH providers, dedupes the shared Receivable account by id:

```csharp
using Accounting101.ModuleKit;

namespace Accounting101.Receivables.Api;

public sealed class ReceivablesChartRequirements(
    IInvoiceAccountsProvider invoiceAccounts, IPaymentAccountsProvider paymentAccounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        InvoicePostingAccounts inv = await invoiceAccounts.GetAsync(clientId, ct);
        PaymentPostingAccounts pay = await paymentAccounts.GetAsync(clientId, ct);
        return
        [
            new(inv.ReceivableAccountId,       "Accounts Receivable", "Asset",     ["Customer", "Invoice"]),
            new(pay.CustomerCreditsAccountId,  "Customer Credits",    "Liability", ["Customer"]),
            new(inv.DefaultRevenueAccountId,   "Revenue",             "Revenue",   []),
            new(inv.SalesTaxPayableAccountId,  "Sales Tax Payable",   "Liability", []),
            new(pay.CashAccountId,             "Cash",                "Asset",     []),
            new(pay.BadDebtExpenseAccountId,   "Bad Debt Expense",    "Expense",   []),
            new(pay.SalesReturnsAccountId,     "Sales Returns",       "Revenue",   []),
        ];
    }
}
```

Register `services.AddScoped<ReceivablesChartRequirements>();`.

- [ ] **Step 4: Endpoint + interface method** — `clients.MapGet("/receivables/chart-readiness", ChartReadiness);` + handler (key `"receivables"`, inject `ReceivablesChartRequirements`). Add `GetAccountsAsync` to the Receivables `ILedgerClient`.

- [ ] **Step 5: Run the E2E** — PASS.

- [ ] **Step 6: Full AR suite** — Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests` → PASS (171 + 2).

- [ ] **Step 7: Commit** — `git add Modules/Receivables && git commit -m "feat(receivables): chart-readiness endpoint"`.

---

### Task 5: Payables chart-readiness

**Files:**
- Create: `Modules/Payables/Accounting101.Payables.Api/PayablesChartRequirements.cs`
- Modify: `.../PayablesEndpoints.cs`, `.../PayablesServiceExtensions.cs`, `Modules/Payables/Accounting101.Payables/ILedgerClient.cs`
- Test: `Modules/Payables/Accounting101.Payables.Tests/ChartReadinessE2eTests.cs`

**Interfaces:** Consumes `IBillAccountsProvider.GetPaymentAccountsAsync(clientId)` → `BillPaymentPostingAccounts` (`PayableAccountId`, `CashAccountId`, `VendorCreditsAccountId`).

- [ ] **Step 1: Write the failing E2E** — Correct chart → `ready:true`; A/P (Payable) without `["Vendor","Bill"]` → `ready:false` with `MissingDimensions`. Assert Vendor Credits requires `["Vendor"]` **and is expected type `Asset`**. Use `PayablesHostFixture`; mirror AP proof/relay chart-setup (Payable `["Vendor","Bill"]`, Vendor Credits Asset/`["Vendor"]`, Cash plain). Route `/clients/{id}/payables/chart-readiness`, key `"payables"`.

- [ ] **Step 2: Run to verify it fails** — FAIL (404).

- [ ] **Step 3: Create `PayablesChartRequirements.cs`:**

```csharp
using Accounting101.ModuleKit;

namespace Accounting101.Payables.Api;

public sealed class PayablesChartRequirements(IBillAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        BillPaymentPostingAccounts a = await accounts.GetPaymentAccountsAsync(clientId, ct);
        return
        [
            new(a.PayableAccountId,       "Accounts Payable", "Liability", ["Vendor", "Bill"]),
            new(a.VendorCreditsAccountId, "Vendor Credits",   "Asset",     ["Vendor"]), // debit-normal — Asset, not Liability
            new(a.CashAccountId,          "Cash",             "Asset",     []),
        ];
    }
}
```

Register `services.AddScoped<PayablesChartRequirements>();`.

- [ ] **Step 4: Endpoint + interface method** — `clients.MapGet("/payables/chart-readiness", ChartReadiness);` + handler (key `"payables"`). Add `GetAccountsAsync` to the Payables `ILedgerClient`.

- [ ] **Step 5: Run the E2E** — PASS.

- [ ] **Step 6: Full AP suite** — Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests` → PASS (112 + 2).

- [ ] **Step 7: Commit** — `git add Modules/Payables && git commit -m "feat(payables): chart-readiness endpoint"`.

---

### Task 6: Cash chart-readiness

**Files:**
- Create: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashChartRequirements.cs`
- Modify: `.../CashEndpoints.cs`, `.../CashServiceExtensions.cs`, `Modules/Banking/Cash/Accounting101.Banking.Cash/ILedgerClient.cs`
- Test: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/ChartReadinessE2eTests.cs`

**Interfaces:** Consumes `ICashAccountsProvider.GetAccountsAsync(clientId)` → `CashPostingAccounts` (`CashAccountId`). No dimensioned requirement.

- [ ] **Step 1: Write the failing E2E** — Correct chart (a Cash Asset account at `fixture.<cashAccountId>` — check the fixture for the configured cash account id) → `ready:true`; a chart WITHOUT that account (don't PUT it) → `ready:false` with `Missing` on the cash account. Route `/clients/{id}/cash/chart-readiness`, key `"cash"`. (The Cash fixture configures `Cash:Accounts:Cash` to a known GUID — assert against it.)

- [ ] **Step 2: Run to verify it fails** — FAIL (404).

- [ ] **Step 3: Create `CashChartRequirements.cs`:**

```csharp
using Accounting101.ModuleKit;

namespace Accounting101.Banking.Cash.Api;

public sealed class CashChartRequirements(ICashAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        CashPostingAccounts a = await accounts.GetAccountsAsync(clientId, ct);
        return [ new(a.CashAccountId, "Cash", "Asset", []) ];
    }
}
```

Register `services.AddScoped<CashChartRequirements>();`.

- [ ] **Step 4: Endpoint + interface method** — `clients.MapGet("/cash/chart-readiness", ChartReadiness);` + handler (key `"cash"`). Add `GetAccountsAsync` to the Cash `ILedgerClient`.

- [ ] **Step 5: Run the E2E** — PASS.

- [ ] **Step 6: Full Cash suite** — Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests` → PASS (43 + 2).

- [ ] **Step 7: Commit** — `git add Modules/Banking/Cash && git commit -m "feat(cash): chart-readiness endpoint"`.

---

### Task 7: Payroll chart-readiness

**Files:**
- Create: `Modules/Payroll/Accounting101.Payroll.Api/PayrollChartRequirements.cs`
- Modify: `.../PayrollEndpoints.cs`, `.../PayrollServiceExtensions.cs`, `Modules/Payroll/Accounting101.Payroll/ILedgerClient.cs`
- Test: `Modules/Payroll/Accounting101.Payroll.Tests/ChartReadinessE2eTests.cs`

**Interfaces:** Consumes `IPayrollAccountsProvider.GetAccountsAsync(clientId)` → `PayrollPostingAccounts` (`SalariesExpenseAccountId`, `PayrollTaxExpenseAccountId`, `CashAccountId`, `WithholdingsPayableAccountId`, `PayrollTaxesPayableAccountId`). No dimensioned requirement.

- [ ] **Step 1: Write the failing E2E** — Correct chart (all 5 accounts configured with the types below) → `ready:true`; a chart missing one account (don't PUT `WithholdingsPayable`) → `ready:false` with `Missing` on it. Use `PayrollHostFixture`; check the fixture for its configured account GUIDs. Route `/clients/{id}/payroll/chart-readiness`, key `"payroll"`.

- [ ] **Step 2: Run to verify it fails** — FAIL (404).

- [ ] **Step 3: Create `PayrollChartRequirements.cs`:**

```csharp
using Accounting101.ModuleKit;

namespace Accounting101.Payroll.Api;

public sealed class PayrollChartRequirements(IPayrollAccountsProvider accounts)
{
    public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
    {
        PayrollPostingAccounts a = await accounts.GetAccountsAsync(clientId, ct);
        return
        [
            new(a.SalariesExpenseAccountId,     "Salaries Expense",      "Expense",   []),
            new(a.PayrollTaxExpenseAccountId,   "Payroll Tax Expense",   "Expense",   []),
            new(a.CashAccountId,                "Cash",                  "Asset",     []),
            new(a.WithholdingsPayableAccountId, "Withholdings Payable",  "Liability", []),
            new(a.PayrollTaxesPayableAccountId, "Payroll Taxes Payable", "Liability", []),
        ];
    }
}
```

Register `services.AddScoped<PayrollChartRequirements>();`.

- [ ] **Step 4: Endpoint + interface method** — `clients.MapGet("/payroll/chart-readiness", ChartReadiness);` + handler (key `"payroll"`). Add `GetAccountsAsync` to the Payroll `ILedgerClient`.

- [ ] **Step 5: Run the E2E** — PASS.

- [ ] **Step 6: Full Payroll suite** — Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests` → PASS (36 + 2).

- [ ] **Step 7: Commit** — `git add Modules/Payroll && git commit -m "feat(payroll): chart-readiness endpoint"`.

---

### Final verification (after all tasks)

- [ ] **Whole-solution build + test**

Run: `dotnet test Accounting101.slnx -m:1`
Expected: PASS — whole solution green, including ModuleKit checker tests and the 6 module E2E pairs. (Parallel-node OOM/MSB4166 is a known flake; `-m:1` is the arbiter.)

## Success criteria (from spec)

- Each of the 6 modules answers `GET /clients/{id}/<key>/chart-readiness` with a `200` report; a misconfigured chart yields `ready:false` naming the exact account + fix; a correct chart yields `ready:true`.
- The declared `RequiredDimensions` match the fold call sites exactly; VendorCredits is `Asset`, CustomerCredits is `Liability`.
- Engine untouched; ModuleKit holds all the logic; the runtime relay remains the backstop; whole solution green.
- Reconciliation has no endpoint (documented exclusion — no static account contract).
