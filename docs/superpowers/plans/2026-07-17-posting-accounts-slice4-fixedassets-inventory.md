# Posting Accounts — Slice 4 (Fixed Assets + Inventory) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fan the per-client posting-accounts admin feature out to the Fixed Assets (6 slots) and Inventory (4 slots) modules — two independent, flat, Payroll-style clones — with process-config fallback.

**Architecture:** For each module: add its slots to the shared `PostingAccountSlots` registry (drives GET + PUT validation with zero endpoint/screen change), and replace `Configured{Module}AccountsProvider` with a `StoreBacked{Module}AccountsProvider` that resolves each slot store → config → throw, mirroring the merged `StoreBackedPayrollAccountsProvider`. Each provider has a single `GetAccountsAsync` returning one flat `*PostingAccounts` record — no shared-slot wrinkle, no dynamic map.

**Tech Stack:** C# / .NET, ASP.NET Core minimal APIs, xUnit.

## Global Constraints

- Slot key == config-key suffix == record field name minus the `AccountId` suffix. The store and config are keyed by the suffix.
- Fixed Assets config keys: `FixedAssets:Accounts:{slot}`; throw message `"Fixed-assets posting account 'FixedAssets:Accounts:{slot}' is not configured."`; module key `fixedassets`.
- Inventory config keys: `Inventory:Accounts:{slot}`; throw message `"Inventory posting account 'Inventory:Accounts:{slot}' is not configured."`; module key `inventory`.
- Providers registered `AddScoped` (depend on the scoped `IPostingAccountsSource`), not `AddSingleton`.
- Slot labels/types/dims are taken verbatim from each module's `*ChartRequirements` (see the tables below). Carry real dims (FA `AccumulatedDepreciation`→`["Asset"]`, Inventory `InventoryAsset`→`["Item"]`); the screen renders `expectedType` only, so non-empty dims are zero UI risk.
- No screen or endpoint code changes — the feature is data-driven from `PostingAccountSlots.All`.
- Leave both modules on config fallback after smoke (do not persist a per-client override to real books).
- Do not stage or commit `UI/Angular/src/app/core/api/environment.ts` (a pre-existing uncommitted working-tree change).

---

### Task 1: Fixed Assets fan-out

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Api/StoreBackedFixedAssetsAccountsProvider.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsServiceExtensions.cs`
- Delete: `Modules/FixedAssets/Accounting101.FixedAssets.Api/ConfiguredFixedAssetsAccountsProvider.cs`
- Test (modify): `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`
- Test (create): `Modules/FixedAssets/Accounting101.FixedAssets.Tests/StoreBackedFixedAssetsAccountsProviderTests.cs`
- Test (delete): `Modules/FixedAssets/Accounting101.FixedAssets.Tests/ConfiguredFixedAssetsAccountsProviderTests.cs`

**Interfaces:**
- Consumes: `IPostingAccountsSource.GetAsync(Guid, string, CancellationToken)` → `IReadOnlyDictionary<string, Guid>` (from `Accounting101.Ledger.Api.Control`); `IFixedAssetsAccountsProvider.GetAccountsAsync(Guid, CancellationToken)` → `FixedAssetsPostingAccounts`; record `FixedAssetsPostingAccounts` with 6 required Guid fields (`DepreciationExpenseAccountId`, `AccumulatedDepreciationAccountId`, `AssetCostAccountId`, `DisposalProceedsAccountId`, `GainOnDisposalAccountId`, `LossOnDisposalAccountId`).
- Produces: `StoreBackedFixedAssetsAccountsProvider : IFixedAssetsAccountsProvider`, registered `AddScoped`; 6 `("fixedassets", …)` registry rows.

- [ ] **Step 1: Write the failing endpoint test**

Append to `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`, immediately before the final closing `}` of the class (mirrors `Get_lists_the_three_payables_slots_and_PUT_validates_them`):

```csharp
    [Fact]
    public async Task Get_lists_the_six_fixed_assets_slots_and_PUT_validates_them()
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcctFixedAssets");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "fixedassets" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Equal(6, got.Slots.Count(s => s.ModuleKey == "fixedassets"));

        HttpResponseMessage ok = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/fixedassets",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["AccumulatedDepreciation"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage bad = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/fixedassets",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Nope"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }
```

- [ ] **Step 2: Run the endpoint test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Get_lists_the_six_fixed_assets_slots_and_PUT_validates_them"`
Expected: FAIL — GET returns 0 fixedassets slots (registry has none yet), so `Assert.Equal(6, …)` fails.

- [ ] **Step 3: Add the 6 Fixed Assets rows to the registry**

In `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs`, append these rows to the `All` collection immediately after the three `("payables", …)` rows (before the closing `];`):

```csharp
        new("fixedassets", "DepreciationExpense",     "Depreciation Expense",      "Expense", []),
        new("fixedassets", "AccumulatedDepreciation", "Accumulated Depreciation",  "Asset",   ["Asset"]),
        new("fixedassets", "AssetCost",               "Fixed Assets (asset cost)", "Asset",   []),
        new("fixedassets", "DisposalProceeds",        "Disposal Proceeds",         "Asset",   []),
        new("fixedassets", "GainOnDisposal",          "Gain on Disposal",          "Revenue", []),
        new("fixedassets", "LossOnDisposal",          "Loss on Disposal",          "Expense", []),
```

Also update the class doc-comment above `public static class PostingAccountSlots` to read:

```csharp
/// <summary>The declared posting-account slots, per module (cash, payroll, payables, fixedassets wired).
/// Remaining modules fan out here (sourced from each module's *ChartRequirements).</summary>
```

- [ ] **Step 4: Run the endpoint test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Get_lists_the_six_fixed_assets_slots_and_PUT_validates_them"`
Expected: PASS.

- [ ] **Step 5: Write the failing provider tests**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Tests/StoreBackedFixedAssetsAccountsProviderTests.cs`:

```csharp
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Configuration;

namespace Accounting101.FixedAssets.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedFixedAssetsAccountsProviderTests
{
    private static readonly string[] Keys =
        ["DepreciationExpense", "AccumulatedDepreciation", "AssetCost", "DisposalProceeds", "GainOnDisposal", "LossOnDisposal"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"FixedAssets:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(new() { ["AssetCost"] = stored }), AllConfigured());
        FixedAssetsPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.AssetCostAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid assetCost = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"FixedAssets:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["FixedAssets:Accounts:AssetCost"] = assetCost.ToString();
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(new()), Config(cfg));
        FixedAssetsPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(assetCost, got.AssetCostAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_stored_slot_is_empty_guid()
    {
        Guid assetCost = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"FixedAssets:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["FixedAssets:Accounts:AssetCost"] = assetCost.ToString();
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(new() { ["AssetCost"] = Guid.Empty }), Config(cfg));
        FixedAssetsPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(assetCost, got.AssetCostAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccountsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_six_slots_from_the_store()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedFixedAssetsAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        FixedAssetsPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["DepreciationExpense"],     got.DepreciationExpenseAccountId);
        Assert.Equal(map["AccumulatedDepreciation"], got.AccumulatedDepreciationAccountId);
        Assert.Equal(map["AssetCost"],               got.AssetCostAccountId);
        Assert.Equal(map["DisposalProceeds"],        got.DisposalProceedsAccountId);
        Assert.Equal(map["GainOnDisposal"],          got.GainOnDisposalAccountId);
        Assert.Equal(map["LossOnDisposal"],          got.LossOnDisposalAccountId);
    }
}
```

- [ ] **Step 6: Delete the obsolete Configured provider test, run to verify compile failure**

```bash
git rm Modules/FixedAssets/Accounting101.FixedAssets.Tests/ConfiguredFixedAssetsAccountsProviderTests.cs
```

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter "StoreBackedFixedAssetsAccountsProviderTests"`
Expected: FAIL to COMPILE — `StoreBackedFixedAssetsAccountsProvider` does not exist yet.

- [ ] **Step 7: Create the provider**

Create `Modules/FixedAssets/Accounting101.FixedAssets.Api/StoreBackedFixedAssetsAccountsProvider.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.FixedAssets.Api;

/// <summary>Resolves the six fixed-assets posting accounts per client: the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>FixedAssets:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen.</summary>
public sealed class StoreBackedFixedAssetsAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IFixedAssetsAccountsProvider
{
    public async Task<FixedAssetsPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "fixedassets", ct);
        return new FixedAssetsPostingAccounts
        {
            DepreciationExpenseAccountId     = Resolve(stored, "DepreciationExpense"),
            AccumulatedDepreciationAccountId = Resolve(stored, "AccumulatedDepreciation"),
            AssetCostAccountId               = Resolve(stored, "AssetCost"),
            DisposalProceedsAccountId        = Resolve(stored, "DisposalProceeds"),
            GainOnDisposalAccountId          = Resolve(stored, "GainOnDisposal"),
            LossOnDisposalAccountId          = Resolve(stored, "LossOnDisposal"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"FixedAssets:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Fixed-assets posting account 'FixedAssets:Accounts:{slot}' is not configured.");
}
```

- [ ] **Step 8: Swap the DI registration and delete the old provider**

In `Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsServiceExtensions.cs`, replace the line (was line 38):

```csharp
        services.AddSingleton<IFixedAssetsAccountsProvider, ConfiguredFixedAssetsAccountsProvider>();
```

with:

```csharp
        services.AddScoped<IFixedAssetsAccountsProvider, StoreBackedFixedAssetsAccountsProvider>();
```

And in the class doc-comment, change `the config-backed posting-accounts provider` to `the store-backed posting-accounts provider (per-client, config fallback)`.

Then delete the obsolete provider:

```bash
git rm Modules/FixedAssets/Accounting101.FixedAssets.Api/ConfiguredFixedAssetsAccountsProvider.cs
```

- [ ] **Step 9: Confirm no dangling references to the deleted type**

Run: `grep -rn "ConfiguredFixedAssetsAccountsProvider" Modules Backend`
Expected: no output.

- [ ] **Step 10: Run the Fixed Assets test project + the endpoint test class**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS (including the 5 new provider tests).

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "PostingAccountEndpointTests"`
Expected: PASS (all cases, including the omit-unset-modules guard).

- [ ] **Step 11: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs Modules/FixedAssets/Accounting101.FixedAssets.Api/StoreBackedFixedAssetsAccountsProvider.cs Modules/FixedAssets/Accounting101.FixedAssets.Api/FixedAssetsServiceExtensions.cs Modules/FixedAssets/Accounting101.FixedAssets.Tests/StoreBackedFixedAssetsAccountsProviderTests.cs
git commit -m "feat(fixed-assets): resolve posting accounts per client with config fallback"
```

---

### Task 2: Inventory fan-out

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs`
- Create: `Modules/Inventory/Accounting101.Inventory.Api/StoreBackedInventoryAccountsProvider.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs`
- Delete: `Modules/Inventory/Accounting101.Inventory.Api/ConfiguredInventoryAccountsProvider.cs`
- Test (modify): `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`
- Test (create): `Modules/Inventory/Accounting101.Inventory.Tests/StoreBackedInventoryAccountsProviderTests.cs`
- Test (delete): `Modules/Inventory/Accounting101.Inventory.Tests/ConfiguredInventoryAccountsProviderTests.cs`

**Interfaces:**
- Consumes: `IPostingAccountsSource.GetAsync(...)`; `IInventoryAccountsProvider.GetAccountsAsync(Guid, CancellationToken)` → `InventoryPostingAccounts`; record `InventoryPostingAccounts` with 4 required Guid fields (`InventoryAssetAccountId`, `CogsAccountId`, `GrniClearingAccountId`, `InventoryAdjustmentAccountId`).
- Produces: `StoreBackedInventoryAccountsProvider : IInventoryAccountsProvider`, registered `AddScoped`; 4 `("inventory", …)` registry rows.

- [ ] **Step 1: Write the failing endpoint test**

Append to `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`, immediately before the final closing `}` of the class:

```csharp
    [Fact]
    public async Task Get_lists_the_four_inventory_slots_and_PUT_validates_them()
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcctInventory");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "inventory" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Equal(4, got.Slots.Count(s => s.ModuleKey == "inventory"));

        HttpResponseMessage ok = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/inventory",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["InventoryAsset"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage bad = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/inventory",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Nope"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }
```

- [ ] **Step 2: Run the endpoint test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Get_lists_the_four_inventory_slots_and_PUT_validates_them"`
Expected: FAIL — GET returns 0 inventory slots, so `Assert.Equal(4, …)` fails.

- [ ] **Step 3: Add the 4 Inventory rows to the registry**

In `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs`, append these rows to the `All` collection immediately after the six `("fixedassets", …)` rows (before the closing `];`):

```csharp
        new("inventory", "InventoryAsset",      "Inventory Asset",      "Asset",     ["Item"]),
        new("inventory", "Cogs",                "Cost of Goods Sold",   "Expense",   []),
        new("inventory", "GrniClearing",        "GRNI Clearing",        "Liability", []),
        new("inventory", "InventoryAdjustment", "Inventory Adjustment", "Expense",   []),
```

Also update the class doc-comment above `public static class PostingAccountSlots` to read:

```csharp
/// <summary>The declared posting-account slots, per module (cash, payroll, payables, fixedassets, inventory
/// wired). Remaining modules fan out here (sourced from each module's *ChartRequirements).</summary>
```

- [ ] **Step 4: Run the endpoint test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Get_lists_the_four_inventory_slots_and_PUT_validates_them"`
Expected: PASS.

- [ ] **Step 5: Write the failing provider tests**

Create `Modules/Inventory/Accounting101.Inventory.Tests/StoreBackedInventoryAccountsProviderTests.cs`:

```csharp
using Accounting101.Inventory.Api;
using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Inventory.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedInventoryAccountsProviderTests
{
    private static readonly string[] Keys =
        ["InventoryAsset", "Cogs", "GrniClearing", "InventoryAdjustment"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Inventory:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(new() { ["Cogs"] = stored }), AllConfigured());
        InventoryPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CogsAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid cogs = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Inventory:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Inventory:Accounts:Cogs"] = cogs.ToString();
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(new()), Config(cfg));
        InventoryPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(cogs, got.CogsAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_stored_slot_is_empty_guid()
    {
        Guid cogs = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Inventory:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Inventory:Accounts:Cogs"] = cogs.ToString();
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(new() { ["Cogs"] = Guid.Empty }), Config(cfg));
        InventoryPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(cogs, got.CogsAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccountsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_four_slots_from_the_store()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedInventoryAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        InventoryPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["InventoryAsset"],      got.InventoryAssetAccountId);
        Assert.Equal(map["Cogs"],                got.CogsAccountId);
        Assert.Equal(map["GrniClearing"],        got.GrniClearingAccountId);
        Assert.Equal(map["InventoryAdjustment"], got.InventoryAdjustmentAccountId);
    }
}
```

- [ ] **Step 6: Delete the obsolete Configured provider test, run to verify compile failure**

```bash
git rm Modules/Inventory/Accounting101.Inventory.Tests/ConfiguredInventoryAccountsProviderTests.cs
```

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter "StoreBackedInventoryAccountsProviderTests"`
Expected: FAIL to COMPILE — `StoreBackedInventoryAccountsProvider` does not exist yet.

- [ ] **Step 7: Create the provider**

Create `Modules/Inventory/Accounting101.Inventory.Api/StoreBackedInventoryAccountsProvider.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Inventory.Api;

/// <summary>Resolves the four inventory posting accounts per client: the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>Inventory:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen.</summary>
public sealed class StoreBackedInventoryAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IInventoryAccountsProvider
{
    public async Task<InventoryPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "inventory", ct);
        return new InventoryPostingAccounts
        {
            InventoryAssetAccountId      = Resolve(stored, "InventoryAsset"),
            CogsAccountId                = Resolve(stored, "Cogs"),
            GrniClearingAccountId        = Resolve(stored, "GrniClearing"),
            InventoryAdjustmentAccountId = Resolve(stored, "InventoryAdjustment"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Inventory:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Inventory posting account 'Inventory:Accounts:{slot}' is not configured.");
}
```

- [ ] **Step 8: Swap the DI registration and delete the old provider**

In `Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs`, replace the line (was line 31):

```csharp
        services.AddSingleton<IInventoryAccountsProvider, ConfiguredInventoryAccountsProvider>();
```

with:

```csharp
        services.AddScoped<IInventoryAccountsProvider, StoreBackedInventoryAccountsProvider>();
```

And in the class doc-comment, change `the config-backed posting-accounts provider` to `the store-backed posting-accounts provider (per-client, config fallback)`.

Then delete the obsolete provider:

```bash
git rm Modules/Inventory/Accounting101.Inventory.Api/ConfiguredInventoryAccountsProvider.cs
```

- [ ] **Step 9: Confirm no dangling references to the deleted type**

Run: `grep -rn "ConfiguredInventoryAccountsProvider" Modules Backend`
Expected: no output.

- [ ] **Step 10: Run the Inventory test project + the endpoint test class**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS (including the 5 new provider tests).

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "PostingAccountEndpointTests"`
Expected: PASS (all cases: cash, payroll, payables, fixedassets, inventory, plus the omit-unset guard).

- [ ] **Step 11: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs Modules/Inventory/Accounting101.Inventory.Api/StoreBackedInventoryAccountsProvider.cs Modules/Inventory/Accounting101.Inventory.Api/InventoryServiceExtensions.cs Modules/Inventory/Accounting101.Inventory.Tests/StoreBackedInventoryAccountsProviderTests.cs
git commit -m "feat(inventory): resolve posting accounts per client with config fallback"
```

---

### Task 3: Full-suite verification + dev-stack smoke

**Files:** none (verification only).

- [ ] **Step 1: Build + run the whole solution's tests**

Run: `dotnet test Accounting101.slnx`
Expected: PASS (all projects, 0 failures).

- [ ] **Step 2: Dev-stack SMOKE against JordanSoft (both modules)**

Follow the fan-out recipe (memory `accounting101-posting-accounts-slice1-cash.md`). Deploy via `C:\Users\jorda\OneDrive\Documents\JordanSoft\deploy\update.ps1`. Auth: `Authorization: DevToken <base64url of {"sub":"00000000-0000-0000-0000-000000000005","name":"Owner","claims":[{"type":"role","value":"Admin"},{"type":"admin","value":"true"}]}>`. Client `761f80b1-f0b5-4927-b8de-dedf84477e59`. Capabilities route is `GET /clients/{id}/me/capabilities`. PUT body property is `Slots`.

  1. Record current `enabledModules` (expected `[cash, reconciliation]`).
  2. `PUT /admin/clients/761f80b1-.../modules` with `{"moduleKeys":["cash","reconciliation","fixedassets","inventory"]}`.
  3. `GET /clients/761f80b1-.../posting-accounts` → assert 6 `fixedassets` + 4 `inventory` slots, each `currentAccountId: null`; spot-check dims on the wire (`fixedassets.AccumulatedDepreciation`→`["Asset"]`, `inventory.InventoryAsset`→`["Item"]`).
  4. `PUT .../posting-accounts/fixedassets` with `{"slots":{"AccumulatedDepreciation":"<guid>"}}` → 200; `PUT .../posting-accounts/inventory` with `{"slots":{"InventoryAsset":"<guid>"}}` → 200; re-GET → those slots reflect the values, all others null.
  5. `PUT .../posting-accounts/fixedassets` with `{"slots":{"Nope":"<guid>"}}` → 422; same for `inventory` → 422.
  6. **RESTORE:** clear both overrides (`PUT .../posting-accounts/fixedassets` and `.../inventory` each with `{"slots":{}}`), then `PUT .../modules` restoring the EXACT prior module set from step 1. Re-GET `/me/capabilities` to confirm the module set matches step 1, and re-GET `/posting-accounts` to confirm only `cash:1` (null) remains. Leave both modules on config fallback.

- [ ] **Step 3: Report smoke results and offer finish options**

Summarize the smoke output. Present finish options (merge-and-push per the user's usual flow, or PR). Do not merge without the user's go-ahead.

---

## Self-Review

**1. Spec coverage:**
- FA registry 6 rows with real dims → Task 1, Step 3. ✓
- FA `StoreBackedFixedAssetsAccountsProvider` → Task 1, Step 7. ✓
- FA DI swap + doc-comment + delete Configured (+ grep guard) → Task 1, Steps 8–9. ✓
- FA provider tests incl. Guid.Empty → Task 1, Step 5. ✓
- FA endpoint test (6 slots + 422) → Task 1, Step 1. ✓
- Inventory registry 4 rows with real dims → Task 2, Step 3. ✓
- Inventory `StoreBackedInventoryAccountsProvider` → Task 2, Step 7. ✓
- Inventory DI swap + doc-comment + delete Configured (+ grep guard) → Task 2, Steps 8–9. ✓
- Inventory provider tests incl. Guid.Empty → Task 2, Step 5. ✓
- Inventory endpoint test (4 slots + 422) → Task 2, Step 1. ✓
- No screen/endpoint change → nothing touches the endpoint or Angular. ✓
- Full-suite verify + combined smoke with restore → Task 3. ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows complete code; commands have expected output. ✓

**3. Type consistency:** FA provider ctor `(IPostingAccountsSource, IConfiguration)`, slots `DepreciationExpense`/`AccumulatedDepreciation`/`AssetCost`/`DisposalProceeds`/`GainOnDisposal`/`LossOnDisposal` → fields `*AccountId`; Inventory slots `InventoryAsset`/`Cogs`/`GrniClearing`/`InventoryAdjustment` → fields `*AccountId`; config keys `{Module}:Accounts:{slot}`; throw messages verbatim from the deleted providers. Registry module keys `fixedassets`/`inventory`. Consistent across Tasks 1–3 and match the existing source files. ✓
