# Posting Accounts — Slice 2 (Payroll fan-out) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `PostingAccountStore.SetModuleAsync` atomic per module, then wire the Payroll module (5 slots) into the per-client posting-accounts feature following the slice-1 (Cash) pattern; close the deferred multi-slot FE guard test. The admin screen needs no change (data-driven).

**Architecture:** Registry rows + a `StoreBackedPayrollAccountsProvider` (store → config fallback → throw) + a DI swap. No endpoint/contract/UI change. The atomic store fix is mandatory before a second module's slots exist.

**Tech Stack:** ASP.NET Core minimal APIs + Mongo + xUnit (backend); Angular (zoneless, standalone, OnPush signals) + Vitest/TestBed (frontend).

**Spec:** `docs/superpowers/specs/2026-07-17-posting-accounts-slice2-payroll-design.md`

## Global Constraints

- `SetModuleAsync` becomes a targeted atomic update: `UpdateOneAsync(d => d.ClientId == clientId, Update.Set($"Accounts.{moduleKey}", new Dictionary<string,Guid>(slots)), IsUpsert=true)`. Only the `Accounts.<moduleKey>` sub-document is written; `GetAsync` unchanged.
- Payroll slot keys/labels/types (registry + provider config-fallback keys `Payroll:Accounts:<slotKey>`):
  `SalariesExpense`→"Salaries Expense"/Expense, `PayrollTaxExpense`→"Payroll Tax Expense"/Expense, `Cash`→"Cash"/Asset, `WithholdingsPayable`→"Withholdings Payable"/Liability, `PayrollTaxesPayable`→"Payroll Taxes Payable"/Liability. No dimensions.
- Provider: stored `payroll`→`<slot>` if set (non-`Guid.Empty`), else `Payroll:Accounts:<slot>` config, else throw `InvalidOperationException("Payroll posting account 'Payroll:Accounts:<slot>' is not configured.")`. Registration `AddScoped`.
- Test runner (FE): `npx ng test --include=<path> --watch=false`; prod build `npx ng build --configuration production` (< 2MB). Backend: `dotnet test <project>`.
- TDD: failing test first; commit after each green task. Do NOT push. Do NOT stage `UI/Angular/src/app/core/api/environment.ts`.

---

### Task 1: Backend — atomic `SetModuleAsync`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/PostingAccountStore.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PostingAccountStoreTests.cs`

**Interfaces:**
- Produces: same `SetModuleAsync(clientId, moduleKey, slots)` signature; now atomic per module.

- [ ] **Step 1: Write the failing tests**

Add to `PostingAccountStoreTests.cs`:

```csharp
    [Fact]
    public async Task Set_upserts_a_fresh_client()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        Guid clientId = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "payroll", new Dictionary<string, Guid> { ["Cash"] = cash });
        PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
        Assert.Equal(cash, doc.Accounts["payroll"]["Cash"]);
    }

    [Fact]
    public async Task Overwriting_a_module_replaces_only_its_map_and_keeps_others()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        Guid clientId = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = Guid.NewGuid() });
        await store.SetModuleAsync(clientId, "payroll", new Dictionary<string, Guid> { ["Cash"] = Guid.NewGuid() });
        Guid newPay = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "payroll", new Dictionary<string, Guid> { ["Cash"] = newPay });

        PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
        Assert.Equal(newPay, doc.Accounts["payroll"]["Cash"]);   // payroll replaced
        Assert.True(doc.Accounts.ContainsKey("cash"));           // cash preserved by the targeted update
    }
```

(The existing `Setting_one_module_does_not_clobber_another` and `Set_then_get_round_trips_a_modules_slots` tests must still pass after the change.)

- [ ] **Step 2: Run tests to verify current state**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountStoreTests"`
Expected: the two new tests currently PASS against the read-modify-write too (they assert behavior, not atomicity) — that's fine; they lock the behavior the atomic rewrite must preserve. If either fails, stop and diagnose before changing the store.

- [ ] **Step 3: Rewrite `SetModuleAsync` atomically**

In `PostingAccountStore.cs`, replace the method body (and drop the non-atomic `<remarks>`, keep the `<summary>`):

```csharp
    /// <summary>Upsert the client's posting accounts, replacing the given module's slot map (other
    /// modules untouched).</summary>
    public async Task SetModuleAsync(
        Guid clientId, string moduleKey, IReadOnlyDictionary<string, Guid> slots, CancellationToken cancellationToken = default)
    {
        // Targeted per-module update: writes only the Accounts.<moduleKey> sub-document, so concurrent
        // writes for different modules on the same client cannot clobber each other. Upsert seeds ClientId
        // from the filter on insert.
        await _accounts.UpdateOneAsync(
            d => d.ClientId == clientId,
            Builders<PostingAccountsDoc>.Update.Set($"Accounts.{moduleKey}", new Dictionary<string, Guid>(slots)),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountStoreTests"`
Expected: PASS (all store tests: the 5 original + 2 new). If the `$"Accounts.{moduleKey}"` upsert on a brand-new client fails to seed `ClientId`, adjust by adding `.SetOnInsert(d => d.ClientId, clientId)` to the update — but the equality filter normally seeds it; verify via `Set_upserts_a_fresh_client`.

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/PostingAccountStore.cs Backend/Accounting101.Ledger.Api.Tests/PostingAccountStoreTests.cs
git commit -m "refactor(posting-accounts): atomic per-module SetModuleAsync"
```

---

### Task 2: Backend — Payroll fan-out (registry + provider + DI)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs` (add 5 payroll rows)
- Create: `Modules/Payroll/Accounting101.Payroll.Api/StoreBackedPayrollAccountsProvider.cs`
- Modify: `Modules/Payroll/Accounting101.Payroll.Api/PayrollServiceExtensions.cs` (swap line 26; fix stale doc-comment line 9)
- Delete (if unreferenced): `Modules/Payroll/Accounting101.Payroll.Api/ConfiguredPayrollAccountsProvider.cs`
- Test: `Modules/Payroll/Accounting101.Payroll.Tests/StoreBackedPayrollAccountsProviderTests.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs` (add a payroll case)

**Interfaces:**
- Consumes: `IPostingAccountsSource` (host), `IPayrollAccountsProvider`/`PayrollPostingAccounts` (existing), `PostingAccountSlots` (host).
- Produces: payroll slots in the registry; per-client payroll account resolution with config fallback.

- [ ] **Step 1: Write the failing tests**

Provider tests — create `Modules/Payroll/Accounting101.Payroll.Tests/StoreBackedPayrollAccountsProviderTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Configuration;

// namespace: match the Payroll test project's convention

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedPayrollAccountsProviderTests
{
    private static readonly string[] Keys =
        ["SalariesExpense", "PayrollTaxExpense", "Cash", "WithholdingsPayable", "PayrollTaxesPayable"];

    private static IConfiguration Config(IDictionary<string, string?> entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries).Build();

    private static IConfiguration AllConfigured() =>
        Config(Keys.ToDictionary(k => $"Payroll:Accounts:{k}", k => (string?)Guid.NewGuid().ToString()));

    [Fact]
    public async Task Prefers_stored_over_config_for_a_slot()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedPayrollAccountsProvider(new FakeSource(new() { ["Cash"] = stored }), AllConfigured());
        PayrollPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_a_slot_is_unset()
    {
        Guid cash = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Payroll:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Payroll:Accounts:Cash"] = cash.ToString();
        var provider = new StoreBackedPayrollAccountsProvider(new FakeSource(new()), Config(cfg));
        PayrollPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(cash, got.CashAccountId);
    }

    [Fact]
    public async Task Throws_when_a_slot_has_neither_store_nor_config()
    {
        var provider = new StoreBackedPayrollAccountsProvider(new FakeSource(new()), Config(new Dictionary<string, string?>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccountsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Maps_all_five_slots_from_the_store()
    {
        Dictionary<string, Guid> map = Keys.ToDictionary(k => k, _ => Guid.NewGuid());
        var provider = new StoreBackedPayrollAccountsProvider(new FakeSource(map), Config(new Dictionary<string, string?>()));
        PayrollPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(map["SalariesExpense"], got.SalariesExpenseAccountId);
        Assert.Equal(map["PayrollTaxExpense"], got.PayrollTaxExpenseAccountId);
        Assert.Equal(map["Cash"], got.CashAccountId);
        Assert.Equal(map["WithholdingsPayable"], got.WithholdingsPayableAccountId);
        Assert.Equal(map["PayrollTaxesPayable"], got.PayrollTaxesPayableAccountId);
    }
}
```

Endpoint payroll case — add to `PostingAccountEndpointTests.cs`:

```csharp
    [Fact]
    public async Task Get_lists_the_five_payroll_slots_and_PUT_validates_them()
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcctPayroll");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "payroll" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Equal(5, got.Slots.Count(s => s.ModuleKey == "payroll"));

        HttpResponseMessage ok = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/payroll",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["SalariesExpense"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage bad = await http.PutAsJsonAsync(
            $"/clients/{c.ClientId}/posting-accounts/payroll",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Nope"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter "FullyQualifiedName~StoreBackedPayrollAccountsProviderTests"` (FAIL — provider missing) and `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~Get_lists_the_five_payroll_slots"` (FAIL — registry has no payroll slots yet).

- [ ] **Step 3: Add the payroll registry rows**

In `PostingAccountSlots.cs`, extend `All`:

```csharp
    public static readonly IReadOnlyList<PostingAccountSlot> All =
    [
        new("cash", "Cash", "Cash / bank account", "Asset", []),
        new("payroll", "SalariesExpense",     "Salaries Expense",      "Expense",   []),
        new("payroll", "PayrollTaxExpense",   "Payroll Tax Expense",   "Expense",   []),
        new("payroll", "Cash",                "Cash",                  "Asset",     []),
        new("payroll", "WithholdingsPayable", "Withholdings Payable",  "Liability", []),
        new("payroll", "PayrollTaxesPayable", "Payroll Taxes Payable", "Liability", []),
    ];
```

- [ ] **Step 4: Create the store-backed payroll provider**

Create `StoreBackedPayrollAccountsProvider.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Payroll.Api;

/// <summary>Resolves the five payroll posting accounts per client: the account configured on the
/// posting-accounts admin screen if set, else the process config value (<c>Payroll:Accounts:*</c>) —
/// so behavior is unchanged until a per-client account is chosen.</summary>
public sealed class StoreBackedPayrollAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : IPayrollAccountsProvider
{
    public async Task<PayrollPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "payroll", ct);
        return new PayrollPostingAccounts
        {
            SalariesExpenseAccountId     = Resolve(stored, "SalariesExpense"),
            PayrollTaxExpenseAccountId   = Resolve(stored, "PayrollTaxExpense"),
            CashAccountId                = Resolve(stored, "Cash"),
            WithholdingsPayableAccountId = Resolve(stored, "WithholdingsPayable"),
            PayrollTaxesPayableAccountId = Resolve(stored, "PayrollTaxesPayable"),
        };
    }

    private Guid Resolve(IReadOnlyDictionary<string, Guid> stored, string slot) =>
        stored.TryGetValue(slot, out Guid id) && id != Guid.Empty
            ? id
            : Guid.TryParse(configuration[$"Payroll:Accounts:{slot}"], out Guid cfg)
                ? cfg
                : throw new InvalidOperationException($"Payroll posting account 'Payroll:Accounts:{slot}' is not configured.");
}
```

- [ ] **Step 5: Swap the registration + fix the stale doc-comment**

In `PayrollServiceExtensions.cs`: change line 26 to
`services.AddScoped<IPayrollAccountsProvider, StoreBackedPayrollAccountsProvider>();`
and update the class doc-comment (line 9) phrase "the config-backed accounts provider" → "the store-backed accounts provider (per-client posting accounts, with config fallback)".

Then `grep -rn "ConfiguredPayrollAccountsProvider" Backend/ Modules/` — if the only hit is the class file, delete `ConfiguredPayrollAccountsProvider.cs`; else leave it. Report the result.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter "FullyQualifiedName~StoreBackedPayrollAccountsProviderTests"` (4/4) and `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountEndpointTests"` (existing 5 + payroll). Then `dotnet build Accounting101.slnx` (DI swap compiles).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs Modules/Payroll/Accounting101.Payroll.Api/ Modules/Payroll/Accounting101.Payroll.Tests/ Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs
git commit -m "feat(payroll): resolve payroll posting accounts per client with config fallback"
```

---

### Task 3: Frontend — multi-slot omit-unset guard test (no component change)

**Files:**
- Test: `UI/Angular/src/app/features/admin/posting-accounts.spec.ts`

**Interfaces:** Consumes the existing `PostingAccountsScreen` (unchanged).

- [ ] **Step 1: Add the multi-slot test**

In `posting-accounts.spec.ts`, add a test that reuses the module-level `base` and `accounts` consts. It flushes a GET with a **two-slot** module, sets one slot, leaves the other, and asserts the PUT body omits the unset slot:

```ts
  it('omits an unset slot from the PUT for a multi-slot module', () => {
    seed('admin.postingAccounts'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(PostingAccountsScreen);
    f.detectChanges();
    const twoSlots = [
      { moduleKey: 'payroll', slotKey: 'Cash', label: 'Cash', expectedType: 'Asset', requiredDimensions: [], currentAccountId: null },
      { moduleKey: 'payroll', slotKey: 'SalariesExpense', label: 'Salaries Expense', expectedType: 'Expense', requiredDimensions: [], currentAccountId: null },
    ];
    http.expectOne(`${base}/posting-accounts`).flush({ slots: twoSlots });
    http.expectOne(`${base}/accounts`).flush(accounts);
    f.detectChanges();

    const c = f.componentInstance as PostingAccountsScreen;
    c.selectAccount('payroll', 'Cash', 'a1');   // set Cash; leave SalariesExpense at default
    c.save('payroll');
    const req = http.expectOne(`${base}/posting-accounts/payroll`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ slots: { Cash: 'a1' } });   // SalariesExpense omitted
    req.flush({ moduleKey: 'payroll', slots: { Cash: 'a1' } });
  });
```

(If `base` / `accounts` are not module-scoped consts in the current spec, define the payload inline consistently with the existing tests.)

- [ ] **Step 2: Run the spec**

Run: `cd UI/Angular && npx ng test --include=src/app/features/admin/posting-accounts.spec.ts --watch=false`
Expected: PASS (existing tests + the new multi-slot guard). The screen is data-driven, so no component change is needed — if the test fails, it has found a real omit-unset bug; fix the component's `save()` accordingly.

- [ ] **Step 3: Commit**

```bash
git add UI/Angular/src/app/features/admin/posting-accounts.spec.ts
git commit -m "test(ui): guard omit-unset-slots for a multi-slot posting-accounts module"
```

---

### Task 4: Dev-stack SMOKE (JordanSoft — Payroll)

**Files:** none (verification only).

- [ ] **Step 1: Deploy the branch**

Run `C:\Users\jorda\OneDrive\Documents\JordanSoft\deploy\update.ps1`. Owner DevToken; client `761f80b1-f0b5-4927-b8de-dedf84477e59`.

- [ ] **Step 2: Ensure payroll is enabled + GET lists the five slots**

```bash
curl -s -H "Authorization: DevToken <token>" http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/posting-accounts
```
If no `payroll` slots appear, payroll is not enabled. Record the current `EnabledModules` (via `GET /admin/clients` or the modules endpoint), then enable payroll: `PUT /admin/clients/761f80b1…/modules` with the existing set **plus** `"payroll"`. Re-GET → expect five `moduleKey:"payroll"` slots (all `currentAccountId:null`).

- [ ] **Step 3: Set + clear one payroll slot (reversible)**

Set one slot to a real chart account id (e.g. an Expense account from `GET /clients/{id}/accounts`), GET reflects it, then clear (`PUT .../payroll {"slots":{}}`) → back to null.

- [ ] **Step 4: Confirm the screen (browser)**

Open `http://localhost:4200/admin/posting-accounts`. A "Payroll" section shows five selects (Salaries Expense, Payroll Tax Expense, Cash, Withholdings Payable, Payroll Taxes Payable), each a dropdown of postable accounts, per-module Save present.

- [ ] **Step 5: Restore**

Clear any payroll override (`PUT .../payroll {"slots":{}}`), and if Step 2 enabled payroll, restore the original `EnabledModules`. Confirm the final state matches what JordanSoft started with. Leave JordanSoft on config fallback with no residual overrides.

---

## Self-Review

**1. Spec coverage:**
- Atomic `SetModuleAsync` (targeted `$set`) + tests → Task 1. ✓
- Payroll registry rows → Task 2. ✓
- `StoreBackedPayrollAccountsProvider` (store→config→throw, all 5 slots) + DI swap + delete Configured → Task 2. ✓
- Payroll endpoint behavior (5 slots + PUT validation) → Task 2. ✓
- Multi-slot omit-unset FE guard test (closes slice-1 deferral) → Task 3. ✓
- Smoke (reversible, restore EnabledModules) → Task 4. ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows complete code. Two spots defer to codebase confirmation (the Payroll test-project namespace; whether `base`/`accounts` are module-scoped in the FE spec) — each with a concrete fallback.

**3. Type consistency:** Slot keys are identical across the registry rows, the provider's `Resolve(...)` calls, the config keys `Payroll:Accounts:<slot>`, and the endpoint/FE tests. `StoreBackedPayrollAccountsProvider` maps the five `PayrollPostingAccounts` `required` fields exactly. `IPostingAccountsSource.GetAsync(clientId, "payroll")` matches the store's module key. The atomic `SetModuleAsync` keeps its signature, so endpoints/tests are unaffected.
