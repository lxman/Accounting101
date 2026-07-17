# Posting Accounts — Slice 1 (Cash) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Per-client posting-accounts store, wired end-to-end for the Cash module: an account chosen on a new `/admin/posting-accounts` screen becomes the account the Cash module posts to for that client, with a config fallback so nothing changes until it's set.

**Architecture:** New control-DB `PostingAccountStore` (module→slot→accountId per client) + a host `IPostingAccountsSource` port; the Cash provider reads the store, falling back to config. Generic slot registry + GET/PUT endpoints gated by `admin.postingAccounts`. A data-driven UI renders whatever slots the GET returns, so the other five modules are pure fan-out later.

**Tech Stack:** ASP.NET Core minimal APIs + Mongo + xUnit (backend); Angular (zoneless, standalone, OnPush signals) + Vitest/TestBed (frontend).

**Spec:** `docs/superpowers/specs/2026-07-17-posting-accounts-slice1-cash-design.md`

## Global Constraints

- Store lives in the **control DB** (registered like `ControlStore`: scoped, from `FirmScope.RequireControlDatabase()` at `LedgerEngineExtensions.cs:36`). Mongo collection name `posting_accounts`. Call `LedgerMongoBootstrap.RegisterOnce()` in the store's static ctor (as `ControlStore` does).
- The `IPostingAccountsSource` port lives in the **host** (`Accounting101.Ledger.Api.Control`); the Cash `.Api` project already references the host, so the Cash provider can inject it.
- **Config fallback:** the store-backed Cash provider returns the stored `Cash` account if set, else the existing `Cash:Accounts:Cash` config value; if neither, throw the same `InvalidOperationException("Cash posting account 'Cash:Accounts:Cash' is not configured.")` the current provider throws. **No behavior change until an account is stored.**
- Endpoints gated in-handler by `AdminAuthorization.MayAsync(..., Capabilities.AdminPostingAccounts, ...)` → 403; 404 when the client is missing; 422 for unknown moduleKey/slotKey.
- GET returns slots only for modules in the client's `EnabledModules`. Slice 1 registry contains only the Cash slot: `("cash", "Cash", "Cash / bank account", "Asset", [])`.
- Wire format camelCase; `Guid?` current account serializes as a string or null.
- Route guard: `canWrite` + `requiredCapability: 'admin.postingAccounts'`, `fallback: '/admin/users'`; add `/admin/posting-accounts` to the `built` array.
- Test runner (FE): `npx ng test --include=<path> --watch=false`; prod build `npx ng build --configuration production` (< 2MB). Backend: `dotnet test Backend/Accounting101.Ledger.Api.Tests`.
- TDD: failing test first; commit after each green task. Do NOT push. Do NOT stage `UI/Angular/src/app/core/api/environment.ts`.

---

### Task 1: Backend — store + source + slot registry + DI

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Control/PostingAccountStore.cs` (`PostingAccountsDoc` + `PostingAccountStore`)
- Create: `Backend/Accounting101.Ledger.Api/Control/PostingAccountsSource.cs` (`IPostingAccountsSource` + `StorePostingAccountsSource`)
- Create: `Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs` (`PostingAccountSlot` + registry)
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs` (register store + source, near line 36)
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs` (add `PostingAccounts()` helper near `Control()` at line 47)
- Test: `Backend/Accounting101.Ledger.Api.Tests/PostingAccountStoreTests.cs`

**Interfaces:**
- Produces: `PostingAccountStore` (`GetAsync`, `SetModuleAsync`), `IPostingAccountsSource.GetAsync(clientId, moduleKey)`, `PostingAccountSlots.All`/`ForModule`/`ModuleKeys`, `PostingAccountsDoc`. Consumed by Tasks 2 (endpoints) and 3 (Cash provider).

- [ ] **Step 1: Write the failing tests**

Create `PostingAccountStoreTests.cs` (mirrors `CapabilitySetStoreTests` — uses `ApiFixture`):

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PostingAccountStoreTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Set_then_get_round_trips_a_modules_slots()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        Guid clientId = Guid.NewGuid();
        Guid cash = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = cash });

        PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
        Assert.Equal(cash, doc.Accounts["cash"]["Cash"]);
    }

    [Fact]
    public async Task Setting_one_module_does_not_clobber_another()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        Guid clientId = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = Guid.NewGuid() });
        Guid inv = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "inventory", new Dictionary<string, Guid> { ["InventoryAsset"] = inv });

        PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
        Assert.True(doc.Accounts.ContainsKey("cash"));
        Assert.Equal(inv, doc.Accounts["inventory"]["InventoryAsset"]);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unset_client()
    {
        Assert.Null(await fixture.PostingAccounts().GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Source_returns_stored_map_or_empty_when_unset()
    {
        PostingAccountStore store = fixture.PostingAccounts();
        StorePostingAccountsSource source = new(store);
        Guid clientId = Guid.NewGuid();
        Assert.Empty(await source.GetAsync(clientId, "cash"));

        Guid cash = Guid.NewGuid();
        await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = cash });
        IReadOnlyDictionary<string, Guid> got = await source.GetAsync(clientId, "cash");
        Assert.Equal(cash, got["Cash"]);
    }

    [Fact]
    public void Registry_contains_the_cash_slot()
    {
        PostingAccountSlot slot = Assert.Single(PostingAccountSlots.ForModule("cash"));
        Assert.Equal("Cash", slot.SlotKey);
        Assert.Equal("Asset", slot.ExpectedType);
        Assert.Contains("cash", PostingAccountSlots.ModuleKeys);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountStoreTests"`
Expected: FAIL — types/`fixture.PostingAccounts()` do not exist (compile error).

- [ ] **Step 3: Create the store**

Create `Control/PostingAccountStore.cs`:

```csharp
using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Control;

/// <summary>Per-client posting-account configuration: which chart account each module posts to for a
/// given slot. Lives in the control DB (admin config, like the client registration). The map is
/// module key → slot key → account id.</summary>
public sealed class PostingAccountsDoc
{
    public Guid ClientId { get; set; }
    public Dictionary<string, Dictionary<string, Guid>> Accounts { get; set; } = new();
}

public sealed class PostingAccountStore
{
    private readonly IMongoCollection<PostingAccountsDoc> _accounts;

    static PostingAccountStore() => LedgerMongoBootstrap.RegisterOnce();

    public PostingAccountStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _accounts = database.GetCollection<PostingAccountsDoc>("posting_accounts");
    }

    public async Task<PostingAccountsDoc?> GetAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        await _accounts.Find(d => d.ClientId == clientId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Upsert the client's posting accounts, replacing the given module's slot map (other
    /// modules untouched).</summary>
    public async Task SetModuleAsync(
        Guid clientId, string moduleKey, IReadOnlyDictionary<string, Guid> slots, CancellationToken cancellationToken = default)
    {
        PostingAccountsDoc doc = await GetAsync(clientId, cancellationToken) ?? new PostingAccountsDoc { ClientId = clientId };
        doc.Accounts[moduleKey] = new Dictionary<string, Guid>(slots);
        await _accounts.ReplaceOneAsync(
            d => d.ClientId == clientId, doc, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }
}
```

- [ ] **Step 4: Create the source port**

Create `Control/PostingAccountsSource.cs`:

```csharp
namespace Accounting101.Ledger.Api.Control;

/// <summary>Read-only per-client posting-account lookup for module providers: the account ids a module
/// posts to for a client, by slot. Empty when the client has configured none (the provider falls back
/// to process config).</summary>
public interface IPostingAccountsSource
{
    Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default);
}

public sealed class StorePostingAccountsSource(PostingAccountStore store) : IPostingAccountsSource
{
    public async Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default)
    {
        PostingAccountsDoc? doc = await store.GetAsync(clientId, ct);
        return doc is not null && doc.Accounts.TryGetValue(moduleKey, out Dictionary<string, Guid>? slots)
            ? slots
            : new Dictionary<string, Guid>();
    }
}
```

- [ ] **Step 5: Create the slot registry**

Create `Control/PostingAccountSlots.cs`:

```csharp
namespace Accounting101.Ledger.Api.Control;

/// <summary>One posting-account slot a module needs, with the metadata the admin screen renders. The
/// expected type and required dimensions are advisory (chart-readiness), not enforced at save time.</summary>
public sealed record PostingAccountSlot(
    string ModuleKey, string SlotKey, string Label, string ExpectedType, IReadOnlyList<string> RequiredDimensions);

/// <summary>The declared posting-account slots, per module. Slice 1: Cash only; other modules fan out
/// here (sourced from each module's *ChartRequirements).</summary>
public static class PostingAccountSlots
{
    public static readonly IReadOnlyList<PostingAccountSlot> All =
    [
        new("cash", "Cash", "Cash / bank account", "Asset", []),
    ];

    public static IReadOnlyList<PostingAccountSlot> ForModule(string moduleKey) =>
        All.Where(s => s.ModuleKey == moduleKey).ToList();

    public static IReadOnlySet<string> ModuleKeys => All.Select(s => s.ModuleKey).ToHashSet();
}
```

- [ ] **Step 6: Register store + source in the host**

In `Hosting/LedgerEngineExtensions.cs`, right after the `ControlStore` registration (line 36), add:

```csharp
        services.AddScoped(sp => new PostingAccountStore(sp.GetRequiredService<FirmScope>().RequireControlDatabase()));
        services.AddScoped<IPostingAccountsSource, StorePostingAccountsSource>();
```

(Add a `using Accounting101.Ledger.Api.Control;` if the file doesn't already have it.)

- [ ] **Step 7: Add the fixture helper**

In `ApiFixture.cs`, next to `public ControlStore Control() => new(Mongo.GetDatabase(ControlDatabase));` (line 47), add:

```csharp
    public PostingAccountStore PostingAccounts() => new(Mongo.GetDatabase(ControlDatabase));
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountStoreTests"`
Expected: PASS (5/5).

- [ ] **Step 9: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/PostingAccountStore.cs Backend/Accounting101.Ledger.Api/Control/PostingAccountsSource.cs Backend/Accounting101.Ledger.Api/Control/PostingAccountSlots.cs Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs Backend/Accounting101.Ledger.Api.Tests/PostingAccountStoreTests.cs
git commit -m "feat(posting-accounts): per-client store + source port + slot registry"
```

---

### Task 2: Backend — GET/PUT endpoints + contracts

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/PostingAccountContracts.cs`
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/PostingAccountEndpoints.cs`
- Modify: `Accounting101.Host/Program.cs` (map the group, near line 103)
- Test: `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`

**Interfaces:**
- Consumes: `PostingAccountStore`, `PostingAccountSlots`, `ControlStore.GetClientAsync`/`SetClientModulesAsync`, `AdminAuthorization.MayAsync`, `Capabilities.AdminPostingAccounts` (Task 1 + existing).
- Produces: `GET /clients/{id}/posting-accounts`, `PUT /clients/{id}/posting-accounts/{moduleKey}`, and the contract records.

- [ ] **Step 1: Write the failing tests**

Create `PostingAccountEndpointTests.cs` (mirrors `AdminCapabilityTests` — `MemberWithAsync`, `fixture`, `System.Net.Http.Json`):

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PostingAccountEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid clientId, HttpClient http)> MemberWithCashEnabledAsync(params string[] caps)
    {
        SeededClient c = await fixture.SeedClientAsync("PostAcct");
        await fixture.Control().SetClientModulesAsync(c.ClientId, new[] { "cash" });
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], caps);
        return (c.ClientId, fixture.ClientFor(userId, "Member"));
    }

    [Fact]
    public async Task Get_lists_the_cash_slot_with_null_then_the_saved_value()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);

        PostingAccountsResponse before = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{clientId}/posting-accounts"))!;
        PostingAccountSlotResponse cash = Assert.Single(before.Slots);
        Assert.Equal("cash", cash.ModuleKey);
        Assert.Equal("Cash", cash.SlotKey);
        Assert.Null(cash.CurrentAccountId);

        Guid account = Guid.NewGuid();
        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/cash",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Cash"] = account }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        PostingAccountsResponse after = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{clientId}/posting-accounts"))!;
        Assert.Equal(account, Assert.Single(after.Slots).CurrentAccountId);
    }

    [Fact]
    public async Task Get_omits_slots_for_modules_the_client_has_not_enabled()
    {
        // No modules enabled → no slots.
        SeededClient c = await fixture.SeedClientAsync("PostAcctNoMods");
        Guid userId = Guid.NewGuid();
        await fixture.Control().SetMembershipAsync(userId, c.ClientId, [], new[] { Capabilities.AdminPostingAccounts, Capabilities.GlRead });
        HttpClient http = fixture.ClientFor(userId, "Member");

        PostingAccountsResponse got = (await http.GetFromJsonAsync<PostingAccountsResponse>(
            $"/clients/{c.ClientId}/posting-accounts"))!;
        Assert.Empty(got.Slots);
    }

    [Fact]
    public async Task Put_rejects_an_unknown_module()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);
        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/ghost",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Cash"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Fact]
    public async Task Put_rejects_an_unknown_slot()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);
        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/cash",
            new SetPostingAccountsRequest(new Dictionary<string, Guid> { ["Bogus"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Fact]
    public async Task Member_without_cap_is_forbidden()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/posting-accounts");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountEndpointTests"`
Expected: FAIL — contracts/endpoints do not exist (compile error) / routes 404.

- [ ] **Step 3: Add the contracts**

Create `Contracts/PostingAccountContracts.cs`:

```csharp
namespace Accounting101.Ledger.Contracts;

/// <summary>One posting-account slot for a client, with its current account (null when unset).</summary>
public sealed record PostingAccountSlotResponse(
    string ModuleKey, string SlotKey, string Label, string ExpectedType,
    IReadOnlyList<string> RequiredDimensions, Guid? CurrentAccountId);

/// <summary>All posting-account slots for a client's enabled modules.</summary>
public sealed record PostingAccountsResponse(IReadOnlyList<PostingAccountSlotResponse> Slots);

/// <summary>Set a module's posting accounts: slot key → chart account id.</summary>
public sealed record SetPostingAccountsRequest(IReadOnlyDictionary<string, Guid> Slots);

/// <summary>A module's saved posting accounts, echoed back by the setter.</summary>
public sealed record PostingAccountsModuleResponse(string ModuleKey, IReadOnlyDictionary<string, Guid> Slots);
```

- [ ] **Step 4: Create the endpoints**

Create `Endpoints/PostingAccountEndpoints.cs`:

```csharp
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>Per-client posting-account configuration: which chart account each enabled module posts to
/// per slot. Gated by <c>admin.postingAccounts</c> (a deployment admin overrides). Values are advisory
/// against the chart (readiness), never enforced here.</summary>
public static class PostingAccountEndpoints
{
    public static void MapPostingAccountEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/clients/{clientId:guid}/posting-accounts").RequireAuthorization();
        g.MapGet("", GetPostingAccounts);
        g.MapPut("/{moduleKey}", SetPostingAccounts);
    }

    private static async Task<IResult> GetPostingAccounts(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        PostingAccountStore store, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminPostingAccounts, actorFactory, control, ct))
            return Results.Forbid();

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();

        HashSet<string> enabled = [.. client.EnabledModules];
        PostingAccountsDoc? doc = await store.GetAsync(clientId, ct);

        Guid? Current(string moduleKey, string slotKey) =>
            doc is not null && doc.Accounts.TryGetValue(moduleKey, out Dictionary<string, Guid>? slots)
                && slots.TryGetValue(slotKey, out Guid id) && id != Guid.Empty
                ? id : null;

        List<PostingAccountSlotResponse> slots = PostingAccountSlots.All
            .Where(s => enabled.Contains(s.ModuleKey))
            .Select(s => new PostingAccountSlotResponse(
                s.ModuleKey, s.SlotKey, s.Label, s.ExpectedType, s.RequiredDimensions, Current(s.ModuleKey, s.SlotKey)))
            .ToList();

        return Results.Ok(new PostingAccountsResponse(slots));
    }

    private static async Task<IResult> SetPostingAccounts(
        Guid clientId, string moduleKey, SetPostingAccountsRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, PostingAccountStore store, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminPostingAccounts, actorFactory, control, ct))
            return Results.Forbid();

        if (await control.GetClientAsync(clientId, ct) is null) return Results.NotFound();

        IReadOnlyList<PostingAccountSlot> moduleSlots = PostingAccountSlots.ForModule(moduleKey);
        if (moduleSlots.Count == 0)
            return Results.Problem($"Unknown posting-accounts module '{moduleKey}'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        HashSet<string> validSlots = [.. moduleSlots.Select(s => s.SlotKey)];
        IReadOnlyDictionary<string, Guid> slots = request.Slots ?? new Dictionary<string, Guid>();
        if (slots.Keys.FirstOrDefault(k => !validSlots.Contains(k)) is { } bad)
            return Results.Problem($"Unknown slot '{bad}' for module '{moduleKey}'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        await store.SetModuleAsync(clientId, moduleKey, slots, ct);
        return Results.Ok(new PostingAccountsModuleResponse(moduleKey, slots));
    }
}
```

- [ ] **Step 5: Map the endpoints in the host**

In `Accounting101.Host/Program.cs`, after `app.MapApprovalPolicyEndpoints();` (line 103), add:

```csharp
app.MapPostingAccountEndpoints();
```

(Add a `using Accounting101.Ledger.Api.Endpoints;` if not already present — it is, since `MapAdminEndpoints` is used.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountEndpointTests"`
Expected: PASS (5/5).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/PostingAccountContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/PostingAccountEndpoints.cs Accounting101.Host/Program.cs Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs
git commit -m "feat(posting-accounts): GET/PUT endpoints (admin.postingAccounts)"
```

---

### Task 3: Backend — Cash provider reads the store (config fallback)

**Files:**
- Create: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/StoreBackedCashAccountsProvider.cs`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashServiceExtensions.cs` (swap the registration at line 26)
- Test: add provider unit tests to the existing Cash module test project (find `Modules/Banking/Cash/**/*Tests*`; if none, add to `Backend/Accounting101.Ledger.Api.Tests`).

**Interfaces:**
- Consumes: `IPostingAccountsSource` (Task 1), `ICashAccountsProvider`/`CashPostingAccounts` (existing), `IConfiguration`.
- Produces: per-client cash account with config fallback.

- [ ] **Step 1: Write the failing unit tests**

Locate the Cash test project (e.g. `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests` or similar — match its namespace/conventions). Add `StoreBackedCashAccountsProviderTests`:

```csharp
using Accounting101.Ledger.Api.Control;
using Microsoft.Extensions.Configuration;

// namespace: match the Cash test project

file sealed class FakeSource(Dictionary<string, Guid> map) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);
}

public sealed class StoreBackedCashAccountsProviderTests
{
    private static IConfiguration Config(string? cash) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cash:Accounts:Cash"] = cash }).Build();

    [Fact]
    public async Task Prefers_the_stored_account_over_config()
    {
        Guid stored = Guid.NewGuid();
        var provider = new StoreBackedCashAccountsProvider(new FakeSource(new() { ["Cash"] = stored }), Config(Guid.NewGuid().ToString()));
        CashPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(stored, got.CashAccountId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_unset()
    {
        Guid configured = Guid.NewGuid();
        var provider = new StoreBackedCashAccountsProvider(new FakeSource(new()), Config(configured.ToString()));
        CashPostingAccounts got = await provider.GetAccountsAsync(Guid.NewGuid());
        Assert.Equal(configured, got.CashAccountId);
    }

    [Fact]
    public async Task Throws_when_neither_store_nor_config_supplies_it()
    {
        var provider = new StoreBackedCashAccountsProvider(new FakeSource(new()), Config(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccountsAsync(Guid.NewGuid()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test <the Cash test project> --filter "FullyQualifiedName~StoreBackedCashAccountsProviderTests"`
Expected: FAIL — `StoreBackedCashAccountsProvider` does not exist.

- [ ] **Step 3: Implement the provider**

Create `StoreBackedCashAccountsProvider.cs`:

```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Banking.Cash.Api;

/// <summary>Resolves the cash posting account per client: the account configured on the posting-accounts
/// admin screen if set, else the process config value (<c>Cash:Accounts:Cash</c>) — so behavior is
/// unchanged until a per-client account is chosen.</summary>
public sealed class StoreBackedCashAccountsProvider(IPostingAccountsSource source, IConfiguration configuration)
    : ICashAccountsProvider
{
    public async Task<CashPostingAccounts> GetAccountsAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "cash", ct);
        Guid cash = stored.TryGetValue("Cash", out Guid id) && id != Guid.Empty
            ? id
            : ConfiguredFallback("Cash:Accounts:Cash");
        return new CashPostingAccounts { CashAccountId = cash };
    }

    private Guid ConfiguredFallback(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Cash posting account '{key}' is not configured.");
}
```

- [ ] **Step 4: Swap the registration**

In `CashServiceExtensions.cs`, replace line 26:

```csharp
        services.AddSingleton<ICashAccountsProvider, ConfiguredCashAccountsProvider>();
```

with (scoped — it now depends on the scoped `IPostingAccountsSource`):

```csharp
        services.AddScoped<ICashAccountsProvider, StoreBackedCashAccountsProvider>();
```

Then grep for other references to `ConfiguredCashAccountsProvider`:
Run: `grep -rn "ConfiguredCashAccountsProvider" Backend/ Modules/`
If the only hit is now the (unused) class file, delete `ConfiguredCashAccountsProvider.cs`. If anything else references it, leave it.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test <the Cash test project> --filter "FullyQualifiedName~StoreBackedCashAccountsProviderTests"`
Expected: PASS (3/3). Also build the solution to confirm the DI swap compiles: `dotnet build Accounting101.slnx`.

- [ ] **Step 6: Commit**

```bash
git add Modules/Banking/Cash/Accounting101.Banking.Cash.Api/
git commit -m "feat(cash): resolve cash posting account per client with config fallback"
```

---

### Task 4: Frontend — Posting accounts screen

**Files:**
- Create: `UI/Angular/src/app/core/posting-accounts/posting-accounts.ts` (model)
- Create: `UI/Angular/src/app/core/posting-accounts/posting-accounts.service.ts`
- Create: `UI/Angular/src/app/features/admin/posting-accounts.ts` (+ spec)
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `GET /clients/{id}/posting-accounts` → `{ slots: [...] }`, `PUT /clients/{id}/posting-accounts/{moduleKey}` (Task 2); `GET /accounts` for the chart (existing `AccountsService` or direct HTTP); `ClientContextService`, `CanDirective`, `HlmButton`, `provideCapabilities`.
- Produces: navigable `/admin/posting-accounts` screen.

- [ ] **Step 1: Write the failing spec**

Create `UI/Angular/src/app/features/admin/posting-accounts.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PostingAccountsScreen } from './posting-accounts';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function seed(...caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities(...caps)],
  });
  TestBed.inject(ClientContextService).select('c1');
}

const base = `${environment.apiBaseUrl}/clients/c1`;
const cashSlot = { moduleKey: 'cash', slotKey: 'Cash', label: 'Cash / bank account', expectedType: 'Asset', requiredDimensions: [], currentAccountId: null };
const accounts = [
  { id: 'a1', number: '1000', name: 'Business Checking', type: 'Asset', postable: true },
  { id: 'a2', number: '4000', name: 'Sales', type: 'Revenue', postable: true },
];

describe('PostingAccountsScreen', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  function boot(caps: string[]) {
    seed(...caps); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(PostingAccountsScreen);
    f.detectChanges();
    http.expectOne(`${base}/posting-accounts`).flush({ slots: [cashSlot] });
    http.expectOne(`${base}/accounts`).flush(accounts);
    f.detectChanges();
    return f;
  }

  it('renders the Cash slot and PUTs the chosen account', () => {
    const f = boot(['admin.postingAccounts']);
    const c = f.componentInstance as PostingAccountsScreen;
    expect(c.slots().length).toBe(1);

    c.selectAccount('cash', 'Cash', 'a1');
    c.save('cash');
    const req = http.expectOne(`${base}/posting-accounts/cash`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ slots: { Cash: 'a1' } });
    req.flush({ moduleKey: 'cash', slots: { Cash: 'a1' } });
    expect(c.savedModule()).toBe('cash');
  });

  it('hides Save without admin.postingAccounts', () => {
    const f = boot(['gl.read']);
    expect((f.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run: `cd UI/Angular && npx ng test --include=src/app/features/admin/posting-accounts.spec.ts --watch=false`
Expected: FAIL — cannot resolve `./posting-accounts`.

- [ ] **Step 3: Create the model**

Create `core/posting-accounts/posting-accounts.ts`:

```ts
export interface PostingAccountSlot {
  moduleKey: string;
  slotKey: string;
  label: string;
  expectedType: string;
  requiredDimensions: string[];
  currentAccountId: string | null;
}

export interface PostingAccounts {
  slots: PostingAccountSlot[];
}

export interface ChartAccount {
  id: string;
  number: string;
  name: string;
  type: string;
  postable: boolean;
}
```

- [ ] **Step 4: Create the service**

Create `core/posting-accounts/posting-accounts.service.ts`:

```ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PostingAccounts, ChartAccount } from './posting-accounts';

@Injectable({ providedIn: 'root' })
export class PostingAccountsService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private clientBase(): string {
    return `${environment.apiBaseUrl}/clients/${this.client.clientId()}`;
  }

  get(): Observable<PostingAccounts> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PostingAccounts>(`${this.clientBase()}/posting-accounts`);
  }

  accounts(): Observable<ChartAccount[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<ChartAccount[]>(`${this.clientBase()}/accounts`);
  }

  setModule(moduleKey: string, slots: Record<string, string>): Observable<unknown> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put(`${this.clientBase()}/posting-accounts/${moduleKey}`, { slots });
  }
}
```

Note: confirm `GET /accounts` returns a bare array of accounts with `{ id, number, name, type, postable }`. If the existing accounts endpoint/DTO uses different field names, adjust `ChartAccount` and the template accordingly (check `UI/Angular/src/app/core/accounts/*` for the existing account model and reuse it instead of redefining if convenient).

- [ ] **Step 5: Create the component**

Create `features/admin/posting-accounts.ts`:

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { CanDirective } from '../../core/capabilities/can.directive';
import { PostingAccountsService } from '../../core/posting-accounts/posting-accounts.service';
import { PostingAccountSlot, ChartAccount } from '../../core/posting-accounts/posting-accounts';

const MODULE_LABELS: Record<string, string> = {
  cash: 'Cash & Banking', receivables: 'Receivables', payables: 'Payables',
  payroll: 'Payroll', fixedassets: 'Fixed Assets', inventory: 'Inventory',
};

interface ModuleGroup { moduleKey: string; label: string; slots: PostingAccountSlot[]; }

@Component({
  selector: 'app-posting-accounts',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective],
  template: `
    <h1 class="text-xl font-semibold mb-4">Posting accounts</h1>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }
    <p class="text-sm text-muted-foreground mb-4">The chart accounts each module posts to for this client.
       Leaving a slot unset uses the deployment default.</p>

    @if (slots().length === 0) {
      <p class="text-sm text-muted-foreground">No modules with posting accounts are enabled for this client.</p>
    }

    @for (g of groups(); track g.moduleKey) {
      <section class="mb-6">
        <h2 class="font-medium mb-2">{{ g.label }}</h2>
        <div class="space-y-3">
          @for (s of g.slots; track s.slotKey) {
            <label class="block">
              <span class="text-sm font-medium">{{ s.label }}</span>
              <span class="ms-2 text-xs text-muted-foreground">expects {{ s.expectedType }}</span>
              <select class="mt-1 block w-96 rounded border border-border bg-background px-3 py-2 text-sm"
                      [value]="chosen()[key(g.moduleKey, s.slotKey)] ?? ''"
                      (change)="onSelect(g.moduleKey, s.slotKey, $event)"
                      [attr.data-testid]="'slot-' + g.moduleKey + '-' + s.slotKey">
                <option value="">— deployment default —</option>
                @for (a of postableAccounts(); track a.id) {
                  <option [value]="a.id">{{ a.number }} · {{ a.name }} ({{ a.type }})</option>
                }
              </select>
            </label>
          }
          @if (savedModule() === g.moduleKey) { <p class="text-green-600 text-sm">Saved.</p> }
          <button *appCan="'admin.postingAccounts'" hlmBtn (click)="save(g.moduleKey)">Save {{ g.label }}</button>
        </div>
      </section>
    }
  `,
})
export class PostingAccountsScreen {
  private readonly service = inject(PostingAccountsService);

  readonly slots = signal<PostingAccountSlot[]>([]);
  readonly postableAccounts = signal<ChartAccount[]>([]);
  readonly error = signal<string | null>(null);
  readonly savedModule = signal<string | null>(null);
  // slot composite key -> chosen account id ('' = default)
  private readonly chosenMap = signal<Record<string, string>>({});
  readonly chosen = this.chosenMap.asReadonly();

  readonly groups = computed<ModuleGroup[]>(() => {
    const byModule = new Map<string, PostingAccountSlot[]>();
    for (const s of this.slots()) {
      const list = byModule.get(s.moduleKey) ?? [];
      list.push(s);
      byModule.set(s.moduleKey, list);
    }
    return [...byModule.entries()].map(([moduleKey, slots]) => ({
      moduleKey, label: MODULE_LABELS[moduleKey] ?? moduleKey, slots,
    }));
  });

  constructor() {
    this.service.get().subscribe({
      next: (p) => {
        this.slots.set(p.slots);
        this.chosenMap.set(Object.fromEntries(
          p.slots.map((s) => [this.key(s.moduleKey, s.slotKey), s.currentAccountId ?? ''])));
      },
      error: () => this.error.set('Could not load posting accounts.'),
    });
    this.service.accounts().subscribe({
      next: (a) => this.postableAccounts.set(a.filter((x) => x.postable)),
      error: () => this.error.set('Could not load the chart of accounts.'),
    });
  }

  key(moduleKey: string, slotKey: string): string { return `${moduleKey}:${slotKey}`; }

  onSelect(moduleKey: string, slotKey: string, event: Event): void {
    this.selectAccount(moduleKey, slotKey, (event.target as HTMLSelectElement).value);
  }

  selectAccount(moduleKey: string, slotKey: string, accountId: string): void {
    this.chosenMap.update((m) => ({ ...m, [this.key(moduleKey, slotKey)]: accountId }));
    this.savedModule.set(null);
  }

  save(moduleKey: string): void {
    this.error.set(null);
    const slots: Record<string, string> = {};
    for (const s of this.slots().filter((x) => x.moduleKey === moduleKey)) {
      const chosen = this.chosenMap()[this.key(moduleKey, s.slotKey)];
      if (chosen) slots[s.slotKey] = chosen;   // omit unset slots (deployment default)
    }
    this.service.setModule(moduleKey, slots).subscribe({
      next: () => this.savedModule.set(moduleKey),
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }
}
```

- [ ] **Step 6: Wire the route**

In `app.routes.ts`, add the import near the other admin screen imports:

```ts
import { PostingAccountsScreen } from './features/admin/posting-accounts';
```

Add the route after the `admin/fiscal` route:

```ts
  { path: 'admin/posting-accounts', component: PostingAccountsScreen, canActivate: [canWrite], data: { requiredCapability: 'admin.postingAccounts', fallback: '/admin/users' } },
```

Add `'/admin/posting-accounts'` to the `built` array.

- [ ] **Step 7: Run the spec + build**

Run: `cd UI/Angular && npx ng test --include=src/app/features/admin/posting-accounts.spec.ts --watch=false`
Expected: PASS. Then:
Run: `cd UI/Angular && npx ng build --configuration production`
Expected: build succeeds within budgets.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/posting-accounts/ UI/Angular/src/app/features/admin/posting-accounts.ts UI/Angular/src/app/features/admin/posting-accounts.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): posting accounts screen (Cash slot)"
```

---

### Task 5: Dev-stack SMOKE (JordanSoft)

**Files:** none (verification only).

- [ ] **Step 1: Deploy the branch**

Run `C:\Users\jorda\OneDrive\Documents\JordanSoft\deploy\update.ps1`. Build the Owner DevToken as in prior smokes; client `761f80b1-f0b5-4927-b8de-dedf84477e59`.

- [ ] **Step 2: Confirm cash is enabled + the GET lists the slot**

```bash
curl -s -H "Authorization: DevToken <token>" http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/posting-accounts
```
Expected: `{"slots":[{"moduleKey":"cash","slotKey":"Cash",...,"currentAccountId":null}]}`. If `slots` is empty, cash is not in the client's `EnabledModules` — enable it via `PUT /admin/clients/{id}/modules` (add "cash") first, then re-check. Note whether cash was already enabled (restore that set at the end).

- [ ] **Step 3: Set the cash account via the API and confirm the GET reflects it**

Use JordanSoft's real Business Checking account id (the configured fallback is `10000000-0000-4000-8000-000000000001`; use the same id to avoid changing posting behavior):

```bash
curl -s -X PUT -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"slots":{"Cash":"10000000-0000-4000-8000-000000000001"}}' \
  http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/posting-accounts/cash
curl -s -H "Authorization: DevToken <token>" http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/posting-accounts
```
Expected: PUT 200; GET now shows `"currentAccountId":"10000000-0000-4000-8000-000000000001"`. Because it equals the config fallback, Cash posting behavior is unchanged.

- [ ] **Step 4: Confirm the screen (browser)**

Open `http://localhost:4200/admin/posting-accounts`. The Cash & Banking section shows a "Cash / bank account" select with Business Checking selected. Save shows "Saved." (Save button present — Owner holds the cap).

- [ ] **Step 5: Restore**

Clear the per-client cash account so the client returns to config fallback (and restore the original `EnabledModules` if Step 2 changed it):

```bash
curl -s -X PUT -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"slots":{}}' http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/posting-accounts/cash
curl -s -H "Authorization: DevToken <token>" http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/posting-accounts
```
Expected: GET shows `"currentAccountId":null` again. JordanSoft is back on config fallback with no residual per-client override.

---

## Self-Review

**1. Spec coverage:**
- Per-client store (control DB) + doc → Task 1. ✓
- `IPostingAccountsSource` port (host) → Task 1. ✓
- Slot registry seeded with Cash → Task 1. ✓
- GET (enabled-module slots + current) / PUT (validate module+slot, persist) gated by `admin.postingAccounts` → Task 2. ✓
- Cash provider store-then-config fallback + throw-when-neither → Task 3. ✓
- Data-driven UI (module groups, per-slot chart dropdown, per-module save, cap-gated) + route + `built` → Task 4. ✓
- Backend tests (store round-trip, source, provider fallback trio, endpoint GET/PUT/422/403) → Tasks 1–3. ✓
- FE tests (renders slot + PUT body, Save hidden without cap) → Task 4. ✓
- Dev-stack smoke (reversible: set to the config account, then clear) → Task 5. ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows complete code. Two spots ask the implementer to confirm an existing shape against the codebase (the `GET /accounts` DTO field names in Task 4, and the Cash test-project location in Task 3) — each with a concrete fallback.

**3. Type consistency:** Backend `PostingAccountSlotResponse(ModuleKey, SlotKey, Label, ExpectedType, RequiredDimensions, CurrentAccountId)` ↔ FE `PostingAccountSlot { moduleKey, slotKey, label, expectedType, requiredDimensions, currentAccountId }` match by camelCase JSON. `SetPostingAccountsRequest { Slots: {slotKey→Guid} }` ↔ FE PUT body `{ slots: { Cash: 'a1' } }`. `IPostingAccountsSource.GetAsync(clientId, moduleKey)` used identically in `StorePostingAccountsSource` and `StoreBackedCashAccountsProvider`. Store `SetModuleAsync(clientId, moduleKey, slots)` / `GetAsync(clientId)` names consistent across store, source, endpoints, tests. `PostingAccountSlots.All/ForModule/ModuleKeys` consistent across registry, endpoints, tests.
