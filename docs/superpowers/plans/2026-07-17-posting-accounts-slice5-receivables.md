# Posting Accounts — Slice 5 (Receivables) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fan the per-client posting-accounts admin feature out to the Receivables module (7 fixed slots across two providers sharing Accounts Receivable) — the final fan-out module — with process-config fallback. The dynamic `RevenueAccountsByCategory` map stays config-only (deferred).

**Architecture:** Add Receivables' 7 fixed slots to the shared `PostingAccountSlots` registry (drives GET + PUT validation, zero endpoint/screen change). Replace both `ConfiguredInvoiceAccountsProvider` and `ConfiguredPaymentAccountsProvider` with `StoreBacked` equivalents that resolve each fixed slot store → config → throw, mirroring `StoreBackedPayrollAccountsProvider`. The invoice provider additionally carries over the existing `ReadCategoryMap` logic so `RevenueAccountsByCategory` is still read from config, unchanged. The shared `Receivable` slot is resolved by both providers from the same `receivables` module slot map.

**Tech Stack:** C# / .NET, ASP.NET Core minimal APIs, xUnit.

## Global Constraints

- Slot key == config-key suffix (`Receivable`/`Revenue`/`SalesTaxPayable`/`Cash`/`CustomerCredits`/`BadDebtExpense`/`SalesReturns`). Note `Revenue` (config suffix) maps to the record field `DefaultRevenueAccountId`; the store/config are keyed by the suffix.
- Config keys `Receivables:Accounts:{slot}`; throw message `"Receivables posting account 'Receivables:Accounts:{slot}' is not configured."`; module key `receivables`.
- Both providers registered `AddScoped` (depend on the scoped `IPostingAccountsSource`), not `AddSingleton`.
- The dynamic `RevenueAccountsByCategory` map is DEFERRED — still read from config (`Receivables:Accounts:RevenueByCategory`) via the carried-over `ReadCategoryMap`; NOT a slot, NOT per-client-editable. Behavior must be unchanged.
- Interface signature note: `IInvoiceAccountsProvider.GetAsync(Guid clientId, CancellationToken cancellationToken = default)` (param name `cancellationToken`); `IPaymentAccountsProvider.GetAsync(Guid clientId, CancellationToken ct = default)` (param name `ct`). Match each interface exactly.
- No screen or endpoint code changes — data-driven from `PostingAccountSlots.All`.
- Leave Receivables on config fallback after smoke (do not persist a per-client override to real books).
- Do not stage or commit `UI/Angular/src/app/core/api/environment.ts` (a pre-existing uncommitted working-tree change).

---

### Task 1: Register the 7 Receivables slots + endpoint coverage

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`

**Interfaces:**
- Consumes: `PostingAccountSlot(string ModuleKey, string SlotKey, string Label, string ExpectedType, IReadOnlyList<string> RequiredDimensions)` (existing record).
- Produces: 7 registry rows keyed `"receivables"` — consumed by the GET/PUT endpoints (already data-driven) and Task 3's smoke.

- [ ] **Step 1: Write the failing endpoint test**

Append to `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`, immediately before the final closing `}` of the class (mirrors `Get_lists_the_three_payables_slots_and_PUT_validates_them`):

```csharp
    [Fact]
    public async Task Get_lists_the_seven_receivables_slots_and_PUT_validates_them()
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcctReceivables");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "receivables" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Equal(7, got.Slots.Count(s => s.ModuleKey == "receivables"));

        HttpResponseMessage ok = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/receivables",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["CustomerCredits"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage bad = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/receivables",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Nope"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }
```

- [ ] **Step 2: Run the endpoint test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Get_lists_the_seven_receivables_slots_and_PUT_validates_them"`
Expected: FAIL — GET returns 0 receivables slots, so `Assert.Equal(7, …)` fails.

- [ ] **Step 3: Add the 7 Receivables rows to the registry**

In `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs`, append these rows to the `All` collection immediately after the four `("inventory", …)` rows (before the closing `];`):

```csharp
        new("receivables", "Receivable",      "Accounts Receivable", "Asset",     ["Customer", "Invoice"]),
        new("receivables", "Revenue",         "Revenue",             "Revenue",   []),
        new("receivables", "SalesTaxPayable", "Sales Tax Payable",   "Liability", []),
        new("receivables", "Cash",            "Cash",                "Asset",     []),
        new("receivables", "CustomerCredits", "Customer Credits",    "Liability", ["Customer"]),
        new("receivables", "BadDebtExpense",  "Bad Debt Expense",    "Expense",   []),
        new("receivables", "SalesReturns",    "Sales Returns",       "Revenue",   []),
```

Also update the class doc-comment above `public static class PostingAccountSlots` to read:

```csharp
/// <summary>The declared posting-account slots, per module (cash, payroll, payables, fixedassets, inventory,
/// receivables wired — the module fan-out is complete). Sourced from each module's *ChartRequirements.</summary>
```

- [ ] **Step 4: Run the endpoint test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Get_lists_the_seven_receivables_slots_and_PUT_validates_them"`
Expected: PASS.

- [ ] **Step 5: Run the full endpoint test class to guard the omit-unset invariant**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "PostingAccountEndpointTests"`
Expected: PASS (all cases, including `Get_omits_slots_for_modules_the_client_has_not_enabled`).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs
git commit -m "feat(posting-accounts): register receivables slots (7 fixed accounts)"
```

---

### Task 2: Store-backed Receivables providers (invoice + payment)

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables.Api/StoreBackedInvoiceAccountsProvider.cs`
- Create: `Modules/Receivables/Accounting101.Receivables.Api/StoreBackedPaymentAccountsProvider.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs`
- Delete: `Modules/Receivables/Accounting101.Receivables.Api/ConfiguredInvoiceAccountsProvider.cs`
- Delete: `Modules/Receivables/Accounting101.Receivables.Api/ConfiguredPaymentAccountsProvider.cs`
- Create: `Modules/Receivables/Accounting101.Receivables.Tests/StoreBackedInvoiceAccountsProviderTests.cs`
- Create: `Modules/Receivables/Accounting101.Receivables.Tests/StoreBackedPaymentAccountsProviderTests.cs`
- Delete: `Modules/Receivables/Accounting101.Receivables.Tests/ConfiguredInvoiceAccountsProviderTests.cs`
- Delete: `Modules/Receivables/Accounting101.Receivables.Tests/ConfiguredPaymentAccountsProviderTests.cs`

**Interfaces:**
- Consumes: `IPostingAccountsSource.GetAsync(Guid, string, CancellationToken)` → `IReadOnlyDictionary<string, Guid>` (from `Accounting101.Ledger.Api.Control`); `IInvoiceAccountsProvider` / `IPaymentAccountsProvider` (from `Accounting101.Receivables`); records `InvoicePostingAccounts` (`ReceivableAccountId`, `DefaultRevenueAccountId`, `SalesTaxPayableAccountId`, `RevenueAccountsByCategory`) and `PaymentPostingAccounts` (`ReceivableAccountId`, `CashAccountId`, `CustomerCreditsAccountId`, `BadDebtExpenseAccountId`, `SalesReturnsAccountId`).
- Produces: `StoreBackedInvoiceAccountsProvider : IInvoiceAccountsProvider` and `StoreBackedPaymentAccountsProvider : IPaymentAccountsProvider`, both `AddScoped`.

- [ ] **Step 1: Write the failing provider tests**

Create `Modules/Receivables/Accounting101.Receivables.Tests/StoreBackedInvoiceAccountsProviderTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Receivables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Receivables.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedInvoiceAccountsProviderTests
{
    private static readonly string[] Keys = ["Receivable", "Revenue", "SalesTaxPayable"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(new() { ["Revenue"] = stored }), AllConfigured());
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(stored, got.DefaultRevenueAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid rev = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:Revenue"] = rev.ToString();
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(new()), Config(cfg));
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(rev, got.DefaultRevenueAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_stored_slot_is_empty_guid()
    {
        Guid rev = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:Revenue"] = rev.ToString();
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(new() { ["Revenue"] = Guid.Empty }), Config(cfg));
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(rev, got.DefaultRevenueAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_three_fixed_slots_from_the_store()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(map["Receivable"],      got.ReceivableAccountId);
        Assert.Equal(map["Revenue"],         got.DefaultRevenueAccountId);
        Assert.Equal(map["SalesTaxPayable"], got.SalesTaxPayableAccountId);
    }

    [Fact]
    public async Task Reads_revenue_category_map_from_config_even_with_stored_fixed_slots()
    {
        Guid license = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:RevenueByCategory:License"] = license.ToString();
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid()); // store overrides fixed slots
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(map), Config(cfg));
        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(license, got.RevenueAccountsByCategory["License"]);
        Assert.Single(got.RevenueAccountsByCategory);
    }
}
```

Create `Modules/Receivables/Accounting101.Receivables.Tests/StoreBackedPaymentAccountsProviderTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Receivables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Receivables.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedPaymentAccountsProviderTests
{
    private static readonly string[] Keys = ["Receivable", "Cash", "CustomerCredits", "BadDebtExpense", "SalesReturns"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(new() { ["Cash"] = stored }), AllConfigured());
        PaymentPostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid cash = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:Cash"] = cash.ToString();
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(new()), Config(cfg));
        PaymentPostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(cash, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_stored_slot_is_empty_guid()
    {
        Guid cash = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:Cash"] = cash.ToString();
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(new() { ["Cash"] = Guid.Empty }), Config(cfg));
        PaymentPostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(cash, got.CashAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_five_slots_from_the_store_including_shared_receivable()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedPaymentAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        PaymentPostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(map["Receivable"],      got.ReceivableAccountId);
        Assert.Equal(map["Cash"],            got.CashAccountId);
        Assert.Equal(map["CustomerCredits"], got.CustomerCreditsAccountId);
        Assert.Equal(map["BadDebtExpense"],  got.BadDebtExpenseAccountId);
        Assert.Equal(map["SalesReturns"],    got.SalesReturnsAccountId);
    }
}
```

- [ ] **Step 2: Delete the obsolete Configured provider tests**

```bash
git rm Modules/Receivables/Accounting101.Receivables.Tests/ConfiguredInvoiceAccountsProviderTests.cs Modules/Receivables/Accounting101.Receivables.Tests/ConfiguredPaymentAccountsProviderTests.cs
```

- [ ] **Step 3: Run the new tests to verify they fail to compile**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "StoreBacked"`
Expected: FAIL to COMPILE — `StoreBackedInvoiceAccountsProvider` / `StoreBackedPaymentAccountsProvider` do not exist yet.

- [ ] **Step 4: Create the invoice provider**

Create `Modules/Receivables/Accounting101.Receivables.Api/StoreBackedInvoiceAccountsProvider.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Receivables.Api;

/// <summary>Resolves the invoice posting accounts per client: each fixed account is the one configured on
/// the posting-accounts admin screen if set, else the process config value (<c>Receivables:Accounts:*</c>)
/// — so behavior is unchanged until a per-client account is chosen. The dynamic
/// <c>RevenueAccountsByCategory</c> map is NOT per-client-configurable; it is still read from config
/// (<c>Receivables:Accounts:RevenueByCategory</c>) unchanged.</summary>
public sealed class StoreBackedInvoiceAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IInvoiceAccountsProvider
{
    public async Task<InvoicePostingAccounts> GetAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "receivables", cancellationToken);
        return new InvoicePostingAccounts
        {
            ReceivableAccountId       = Resolve(stored, "Receivable"),
            DefaultRevenueAccountId   = Resolve(stored, "Revenue"),
            SalesTaxPayableAccountId  = Resolve(stored, "SalesTaxPayable"),
            RevenueAccountsByCategory = ReadCategoryMap("Receivables:Accounts:RevenueByCategory"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Receivables:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Receivables posting account 'Receivables:Accounts:{slot}' is not configured.");

    /// <summary>Bind a category → account-id section. Absent section yields an empty map; a malformed
    /// value fails loud, the same as a required account.</summary>
    private IReadOnlyDictionary<string, Guid> ReadCategoryMap(string sectionKey) =>
        configuration.GetSection(sectionKey).GetChildren().ToDictionary(
            child => child.Key,
            child => Guid.TryParse(child.Value, out Guid id)
                ? id
                : throw new InvalidOperationException(
                    $"Receivables revenue category '{child.Key}' has a malformed account id '{child.Value}'."));
}
```

- [ ] **Step 5: Create the payment provider**

Create `Modules/Receivables/Accounting101.Receivables.Api/StoreBackedPaymentAccountsProvider.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Receivables.Api;

/// <summary>Resolves the five payment posting accounts per client: each is the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>Receivables:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen. The <c>Receivable</c> slot is shared
/// with the invoice provider (both resolve the same slot).</summary>
public sealed class StoreBackedPaymentAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IPaymentAccountsProvider
{
    public async Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "receivables", ct);
        return new PaymentPostingAccounts
        {
            ReceivableAccountId      = Resolve(stored, "Receivable"),
            CashAccountId            = Resolve(stored, "Cash"),
            CustomerCreditsAccountId = Resolve(stored, "CustomerCredits"),
            BadDebtExpenseAccountId  = Resolve(stored, "BadDebtExpense"),
            SalesReturnsAccountId    = Resolve(stored, "SalesReturns"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Receivables:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Receivables posting account 'Receivables:Accounts:{slot}' is not configured.");
}
```

- [ ] **Step 6: Swap the DI registrations and update the doc-comment**

In `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs`, replace the two lines:

```csharp
        services.AddSingleton<IInvoiceAccountsProvider, ConfiguredInvoiceAccountsProvider>();
        services.AddSingleton<IPaymentAccountsProvider, ConfiguredPaymentAccountsProvider>();
```

with:

```csharp
        services.AddScoped<IInvoiceAccountsProvider, StoreBackedInvoiceAccountsProvider>();
        services.AddScoped<IPaymentAccountsProvider, StoreBackedPaymentAccountsProvider>();
```

And in the class doc-comment, change `the config-backed accounts provider` to `the store-backed accounts providers (per-client, config fallback)`.

- [ ] **Step 7: Delete the obsolete Configured providers**

```bash
git rm Modules/Receivables/Accounting101.Receivables.Api/ConfiguredInvoiceAccountsProvider.cs Modules/Receivables/Accounting101.Receivables.Api/ConfiguredPaymentAccountsProvider.cs
```

- [ ] **Step 8: Confirm no dangling references to the deleted types**

Run: `grep -rn "ConfiguredInvoiceAccountsProvider\|ConfiguredPaymentAccountsProvider" Modules Backend`
Expected: no output.

- [ ] **Step 9: Run the new provider tests to verify they pass**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "StoreBacked"`
Expected: PASS (all 12 new cases — 6 invoice + 6 payment).

- [ ] **Step 10: Run the full Receivables test project**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests`
Expected: PASS.

- [ ] **Step 11: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Api/StoreBackedInvoiceAccountsProvider.cs Modules/Receivables/Accounting101.Receivables.Api/StoreBackedPaymentAccountsProvider.cs Modules/Receivables/Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs Modules/Receivables/Accounting101.Receivables.Tests/StoreBackedInvoiceAccountsProviderTests.cs Modules/Receivables/Accounting101.Receivables.Tests/StoreBackedPaymentAccountsProviderTests.cs
git commit -m "feat(receivables): resolve posting accounts per client with config fallback"
```

---

### Task 3: Full-suite verification + dev-stack smoke

**Files:** none (verification only).

- [ ] **Step 1: Build + run the whole solution's tests**

Run: `dotnet test Accounting101.slnx`
Expected: PASS (all projects, 0 failures).

- [ ] **Step 2: Dev-stack SMOKE against JordanSoft**

Follow the fan-out recipe (memory `accounting101-posting-accounts-slice1-cash.md`). Deploy via `C:\Users\jorda\OneDrive\Documents\JordanSoft\deploy\update.ps1`. Auth: `Authorization: DevToken <base64url of {"sub":"00000000-0000-0000-0000-000000000005","name":"Owner","claims":[{"type":"role","value":"Admin"},{"type":"admin","value":"true"}]}>`. Client `761f80b1-f0b5-4927-b8de-dedf84477e59`. Capabilities route `GET /clients/{id}/me/capabilities`. PUT body property is `Slots`.

  1. Record current `enabledModules` (expected `[cash, reconciliation]`).
  2. `PUT /admin/clients/761f80b1-.../modules` with `{"moduleKeys":["cash","reconciliation","receivables"]}`.
  3. `GET /clients/761f80b1-.../posting-accounts` → assert 7 `receivables` slots, each `currentAccountId: null`; spot-check dims on the wire (`receivables.Receivable`→`["Customer","Invoice"]`, `receivables.CustomerCredits`→`["Customer"]`).
  4. `PUT .../posting-accounts/receivables` with `{"slots":{"CustomerCredits":"<guid>"}}` → 200; re-GET → that slot reflects the value, others null.
  5. `PUT .../posting-accounts/receivables` with `{"slots":{"Nope":"<guid>"}}` → 422.
  6. **RESTORE:** clear the override (`PUT .../posting-accounts/receivables` with `{"slots":{}}`), then `PUT .../modules` restoring the EXACT prior module set from step 1. Re-GET `/me/capabilities` to confirm the module set matches step 1, and re-GET `/posting-accounts` to confirm only `cash:1` (null) remains. Leave Receivables on config fallback.

- [ ] **Step 3: Report smoke results and offer finish options**

Summarize the smoke output. Note this closes the posting-accounts epic (6/6 modules). Present finish options (merge-and-push per the user's usual flow, or PR). Do not merge without the user's go-ahead.

---

## Self-Review

**1. Spec coverage:**
- Registry 7 rows with real dims → Task 1, Step 3. ✓
- Endpoint test (7 slots + 422) → Task 1, Step 1. ✓
- `StoreBackedInvoiceAccountsProvider` (3 fixed slots + category map from config) → Task 2, Step 4. ✓
- `StoreBackedPaymentAccountsProvider` (5 fixed slots, shared Receivable) → Task 2, Step 5. ✓
- DI swap both Singleton→Scoped + doc-comment → Task 2, Step 6. ✓
- Delete both Configured providers + both tests (+ grep guard) → Task 2, Steps 2, 7, 8. ✓
- Invoice tests incl. Guid.Empty + category-map-from-config → Task 2, Step 1. ✓
- Payment tests incl. Guid.Empty + shared Receivable → Task 2, Step 1. ✓
- Deferred category map (config-only, unchanged) → invoice provider `ReadCategoryMap` carried over (Step 4) + guarded by test. ✓
- No screen/endpoint change → nothing touches the endpoint or Angular. ✓
- Full-suite verify + smoke with restore → Task 3. ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows complete code; commands have expected output. ✓

**3. Type consistency:** Both providers ctor `(IPostingAccountsSource, IConfiguration)`, `Resolve(IReadOnlyDictionary<string,Guid>, string)`; invoice `GetAsync(…, CancellationToken cancellationToken = default)`, payment `GetAsync(…, CancellationToken ct = default)` (matching each interface); slot keys `Receivable`/`Revenue`/`SalesTaxPayable`/`Cash`/`CustomerCredits`/`BadDebtExpense`/`SalesReturns`; config keys `Receivables:Accounts:{slot}`; `Revenue` slot → `DefaultRevenueAccountId` field; records' fields match what each provider populates. Registry module key `receivables`. Consistent across Tasks 1–3 and match the existing source files. ✓
