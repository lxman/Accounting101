# Posting Accounts — Slice 3 (Payables) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fan the per-client posting-accounts admin feature out to the Payables (AP) module, so an admin configures AP's 3 posting accounts per client on the existing data-driven screen, with process-config fallback.

**Architecture:** Add AP's 3 unique slots to the shared `PostingAccountSlots` registry (drives GET + PUT validation with zero endpoint/screen change). Replace `ConfiguredBillAccountsProvider` with a `StoreBackedBillAccountsProvider` that resolves each slot store → config → throw, mirroring the merged `StoreBackedPayrollAccountsProvider`. AP's `IBillAccountsProvider` has two methods over two records that share `PayableAccountId`, so the provider resolves each config-suffix slot once and threads the shared `Payable` slot into both records.

**Tech Stack:** C# / .NET 8, ASP.NET Core minimal APIs, xUnit.

## Global Constraints

- Slot key == config-key suffix (`Payable`, `Cash`, `VendorCredits`), NOT the record field name (`PayableAccountId`). The store and config are keyed by the suffix.
- Config keys are exactly `Payables:Accounts:Payable`, `Payables:Accounts:Cash`, `Payables:Accounts:VendorCredits` (unchanged from the existing provider).
- Fallback throw message shape must match the other modules: `"Payables posting account 'Payables:Accounts:{slot}' is not configured."`
- Provider is registered `AddScoped` (depends on the scoped `IPostingAccountsSource`), not `AddSingleton`.
- No screen or endpoint code changes — the feature is data-driven from `PostingAccountSlots.All`.
- Leave AP on config fallback after smoke (do not persist a per-client override to real books).

---

### Task 1: Register the 3 Payables slots + endpoint coverage

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`

**Interfaces:**
- Consumes: `PostingAccountSlot(string ModuleKey, string SlotKey, string Label, string ExpectedType, IReadOnlyList<string> RequiredDimensions)` (existing record).
- Produces: 3 registry rows keyed `"payables"` with slot keys `Payable`, `Cash`, `VendorCredits` — consumed by the GET/PUT endpoints (already data-driven) and by Task 2's smoke.

- [ ] **Step 1: Write the failing endpoint test**

Append to `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`, immediately before the final closing `}` of the class (mirrors the existing payroll case `Get_lists_the_five_payroll_slots_and_PUT_validates_them`):

```csharp
    [Fact]
    public async Task Get_lists_the_three_payables_slots_and_PUT_validates_them()
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcctPayables");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "payables" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Equal(3, got.Slots.Count(s => s.ModuleKey == "payables"));

        HttpResponseMessage ok = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/payables",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["VendorCredits"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage bad = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/payables",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Nope"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Get_lists_the_three_payables_slots_and_PUT_validates_them"`
Expected: FAIL — GET returns 0 payables slots (registry has none yet), so `Assert.Equal(3, …)` fails.

- [ ] **Step 3: Add the 3 Payables rows to the registry**

In `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs`, update the `All` collection so it reads exactly (append the 3 payables rows after the payroll rows):

```csharp
    public static readonly IReadOnlyList<PostingAccountSlot> All =
    [
        new("cash", "Cash", "Cash / bank account", "Asset", []),
        new("payroll", "SalariesExpense",     "Salaries Expense",      "Expense",   []),
        new("payroll", "PayrollTaxExpense",   "Payroll Tax Expense",   "Expense",   []),
        new("payroll", "Cash",                "Cash",                  "Asset",     []),
        new("payroll", "WithholdingsPayable", "Withholdings Payable",  "Liability", []),
        new("payroll", "PayrollTaxesPayable", "Payroll Taxes Payable", "Liability", []),
        new("payables", "Payable",       "Accounts Payable", "Liability", ["Vendor", "Bill"]),
        new("payables", "Cash",          "Cash",             "Asset",     []),
        new("payables", "VendorCredits", "Vendor Credits",   "Asset",     ["Vendor"]),
    ];
```

Also update the class doc-comment above `public static class PostingAccountSlots` from:

```csharp
/// <summary>The declared posting-account slots, per module. Slice 1 wired Cash; other modules fan out
/// here (sourced from each module's *ChartRequirements).</summary>
```

to:

```csharp
/// <summary>The declared posting-account slots, per module (cash, payroll, payables wired). Remaining
/// modules fan out here (sourced from each module's *ChartRequirements).</summary>
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Get_lists_the_three_payables_slots_and_PUT_validates_them"`
Expected: PASS.

- [ ] **Step 5: Run the full endpoint test class to guard the omit-unset-modules invariant**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "PostingAccountEndpointTests"`
Expected: PASS (all cases, including `Get_omits_slots_for_modules_the_client_has_not_enabled`).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs
git commit -m "feat(posting-accounts): register payables slots (Payable/Cash/VendorCredits)"
```

---

### Task 2: Store-backed Payables accounts provider

**Files:**
- Create: `Modules/Payables/Accounting101.Payables.Api/StoreBackedBillAccountsProvider.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesServiceExtensions.cs`
- Delete: `Modules/Payables/Accounting101.Payables.Api/ConfiguredBillAccountsProvider.cs`
- Create: `Modules/Payables/Accounting101.Payables.Tests/StoreBackedBillAccountsProviderTests.cs`
- Delete: `Modules/Payables/Accounting101.Payables.Tests/ConfiguredBillAccountsProviderTests.cs`

**Interfaces:**
- Consumes: `IPostingAccountsSource.GetAsync(Guid clientId, string moduleKey, CancellationToken)` → `IReadOnlyDictionary<string, Guid>` (from `Accounting101.Ledger.Api.Control`); `IBillAccountsProvider` with `Task<BillPostingAccounts> GetBillAccountsAsync(...)` and `Task<BillPaymentPostingAccounts> GetPaymentAccountsAsync(...)`; records `BillPostingAccounts { Guid PayableAccountId }` and `BillPaymentPostingAccounts { Guid PayableAccountId, Guid CashAccountId, Guid VendorCreditsAccountId }`.
- Produces: `StoreBackedBillAccountsProvider : IBillAccountsProvider`, registered `AddScoped`.

- [ ] **Step 1: Write the failing provider tests**

Create `Modules/Payables/Accounting101.Payables.Tests/StoreBackedBillAccountsProviderTests.cs` (mirrors `StoreBackedPayrollAccountsProviderTests`, with the two-method / shared-`Payable` assertions):

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Payables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Payables.Tests;

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedBillAccountsProviderTests
{
    private static readonly string[] Keys = ["Payable", "Cash", "VendorCredits"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Payables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedBillAccountsProvider(new FakeSource(new() { ["Cash"] = stored }), AllConfigured());
        BillPaymentPostingAccounts got = await provider.GetPaymentAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid cash = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Payables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Payables:Accounts:Cash"] = cash.ToString();
        var provider = new StoreBackedBillAccountsProvider(new FakeSource(new()), Config(cfg));
        BillPaymentPostingAccounts got = await provider.GetPaymentAccountsAsync(Guid.NewGuid());
        Assert.Equal(cash, got.CashAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedBillAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetBillAccountsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_three_slots_from_the_store_into_both_records()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedBillAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));

        BillPostingAccounts bill = await provider.GetBillAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["Payable"], bill.PayableAccountId);

        BillPaymentPostingAccounts pay = await provider.GetPaymentAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["Payable"], pay.PayableAccountId);
        Assert.Equal(map["Cash"], pay.CashAccountId);
        Assert.Equal(map["VendorCredits"], pay.VendorCreditsAccountId);
    }
}
```

- [ ] **Step 2: Delete the obsolete Configured provider test**

```bash
git rm Modules/Payables/Accounting101.Payables.Tests/ConfiguredBillAccountsProviderTests.cs
```

- [ ] **Step 3: Run the new tests to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "StoreBackedBillAccountsProviderTests"`
Expected: FAIL to COMPILE — `StoreBackedBillAccountsProvider` does not exist yet.

- [ ] **Step 4: Create the provider**

Create `Modules/Payables/Accounting101.Payables.Api/StoreBackedBillAccountsProvider.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Payables.Api;

/// <summary>Resolves the three payables posting accounts per client: the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>Payables:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen. The <c>Payable</c> slot is shared:
/// the same resolved account flows into both the bill and payment recipes.</summary>
public sealed class StoreBackedBillAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IBillAccountsProvider
{
    public async Task<BillPostingAccounts> GetBillAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "payables", ct);
        return new BillPostingAccounts { PayableAccountId = Resolve(stored, "Payable") };
    }

    public async Task<BillPaymentPostingAccounts> GetPaymentAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "payables", ct);
        return new BillPaymentPostingAccounts
        {
            PayableAccountId       = Resolve(stored, "Payable"),
            CashAccountId          = Resolve(stored, "Cash"),
            VendorCreditsAccountId = Resolve(stored, "VendorCredits"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Payables:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Payables posting account 'Payables:Accounts:{slot}' is not configured.");
}
```

- [ ] **Step 5: Swap the DI registration and delete the old provider**

In `Modules/Payables/Accounting101.Payables.Api/PayablesServiceExtensions.cs`, replace the line:

```csharp
        services.AddSingleton<IBillAccountsProvider, ConfiguredBillAccountsProvider>();
```

with:

```csharp
        services.AddScoped<IBillAccountsProvider, StoreBackedBillAccountsProvider>();
```

And update the class doc-comment: change `the config-backed accounts provider` to `the store-backed accounts provider (per-client posting accounts, with config fallback)`.

Then delete the obsolete provider:

```bash
git rm Modules/Payables/Accounting101.Payables.Api/ConfiguredBillAccountsProvider.cs
```

- [ ] **Step 6: Confirm no dangling references to the deleted type**

Run: `grep -rn "ConfiguredBillAccountsProvider" Modules Backend`
Expected: no output (all references removed).

- [ ] **Step 7: Run the provider tests to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "StoreBackedBillAccountsProviderTests"`
Expected: PASS (all 4 cases).

- [ ] **Step 8: Run the full Payables test project**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Api/StoreBackedBillAccountsProvider.cs Modules/Payables/Accounting101.Payables.Api/PayablesServiceExtensions.cs
git commit -m "feat(payables): resolve bill posting accounts per client with config fallback"
```

---

### Task 3: Full-suite verification + dev-stack smoke

**Files:** none (verification only).

- [ ] **Step 1: Build + run the whole solution's tests**

Run: `dotnet test Accounting101.sln`
Expected: PASS (backend engine, Ledger.Api.Tests, Payables.Tests, all modules).

- [ ] **Step 2: Dev-stack SMOKE against JordanSoft**

Follow the fan-out recipe (memory `accounting101-posting-accounts-slice1-cash.md`). Deploy via `C:\Users\jorda\OneDrive\Documents\JordanSoft\deploy\update.ps1`. Auth: `Authorization: DevToken <base64url of {sub:00000000-0000-0000-0000-000000000005,name:Owner,claims:[role=Admin,admin=true]}>`. Client `761f80b1-f0b5-4927-b8de-dedf84477e59`.

  1. Record the current EnabledModules (expected `[cash, reconciliation]` via `GET /me/capabilities`).
  2. `PUT /admin/clients/761f80b1-.../modules` adding `payables` to the existing set.
  3. `GET /clients/761f80b1-.../posting-accounts` → assert 3 `payables` slots, each `currentAccountId: null`.
  4. `PUT /clients/761f80b1-.../posting-accounts/payables` with `{ "VendorCredits": "<any guid>" }` → 200; re-GET → that slot reflects the value.
  5. `PUT .../posting-accounts/payables` with `{ "Nope": "<guid>" }` → 422.
  6. **RESTORE:** clear the override (PUT payables with an empty map or per the recipe's clear step so all 3 slots return to null), then `PUT .../modules` restoring the EXACT prior module set from step 1. Re-GET `/me/capabilities` to confirm the module set matches step 1. Leave AP on config fallback (null).

- [ ] **Step 3: Report smoke results and offer finish options**

Summarize the smoke output. Present finish options (merge-and-push per user's usual flow, or PR). Do not merge without the user's go-ahead.

---

## Self-Review

**1. Spec coverage:**
- Slot registry 3 rows with real dims → Task 1, Step 3. ✓
- `StoreBackedBillAccountsProvider` (two methods, shared Payable) → Task 2, Step 4. ✓
- DI `AddSingleton`→`AddScoped` swap → Task 2, Step 5. ✓
- Delete `ConfiguredBillAccountsProvider` + test → Task 2, Steps 2 & 5, grep guard Step 6. ✓
- Provider tests (prefer-stored / fallback / throw / map-all-3 both records) → Task 2, Step 1. ✓
- Endpoint test (3 slots + 422 unknown) → Task 1, Step 1. ✓
- No screen/endpoint change → nothing touches the endpoint or Angular. ✓
- Verification + smoke with restore → Task 3. ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows complete code; commands have expected output. ✓

**3. Type consistency:** `StoreBackedBillAccountsProvider(IPostingAccountsSource, IConfiguration)`, `Resolve(IReadOnlyDictionary<string,Guid>, string)`, slot keys `Payable`/`Cash`/`VendorCredits`, config keys `Payables:Accounts:{slot}`, records `BillPostingAccounts.PayableAccountId` / `BillPaymentPostingAccounts.{PayableAccountId,CashAccountId,VendorCreditsAccountId}` — consistent across Tasks 1–2 and match the existing source files. ✓
