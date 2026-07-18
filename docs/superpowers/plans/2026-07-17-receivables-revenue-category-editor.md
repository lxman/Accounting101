# Receivables Revenue-by-Category Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Per-client editing of the receivables `RevenueAccountsByCategory` map (invoice-line category → revenue account), stored in the control DB and editable from the existing `/admin/posting-accounts` screen, with process-config fallback.

**Architecture:** A parallel module-keyed `CategoryMaps` field on the existing `PostingAccountsDoc` (control DB), two new endpoints on the existing posting-accounts route group (`GET`/`PUT …/{moduleKey}/revenue-categories`, gated by `admin.postingAccounts`), a new nullable port method on `IPostingAccountsSource` (default-interface-implemented so existing fakes don't break), and a dynamic add/remove-row sub-section inside the Receivables group of the posting-accounts screen. A stored map wins **wholesale** over config, even when empty.

**Tech Stack:** ASP.NET Core minimal APIs (.NET), MongoDB driver, xUnit + `ApiFixture` (EphemeralMongo), Angular (zoneless, signals, native `<select>`), Karma/Jasmine specs.

**Spec:** `docs/superpowers/specs/2026-07-17-receivables-revenue-category-editor-design.md`
**Branch:** `feat/receivables-revenue-categories` (create from `master` before Task 1)

## Global Constraints

- Module key for this feature is exactly `receivables`; config fallback section is exactly `Receivables:Accounts:RevenueByCategory`.
- Stored-map presence semantics: a client "has" a stored map when `CategoryMaps` contains the module key — **an empty map counts and wins over config**.
- Category-name validation (server 422, mirrored client-side): reject null/empty/whitespace keys, keys containing `.`, and keys starting with `$`. Case-sensitive names — `Consulting` and `consulting` are distinct and both valid.
- Account Guids are advisory — no chart-existence check anywhere.
- All endpoint work stays on the `/clients/{clientId:guid}/posting-accounts` group, gated by `Capabilities.AdminPostingAccounts` via `AdminAuthorization.MayAsync` (existing pattern in `PostingAccountEndpoints.cs`).
- The 7 fixed receivables slots and their rendering are untouched; `StoreBackedPaymentAccountsProvider` is untouched.
- Every commit message ends with the trailer: `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- Run backend tests with `dotnet test Accounting101.slnx` from the repo root (Windows; PowerShell is the default shell).

---

### Task 1: Store — `CategoryMaps` field, atomic setter, source port method

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/PostingAccountStore.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/PostingAccountsSource.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PostingAccountStoreTests.cs`

**Interfaces:**
- Consumes: existing `PostingAccountsDoc`, `PostingAccountStore.SetModuleAsync`/`GetAsync`, `StorePostingAccountsSource`, `ApiFixture.PostingAccounts()`.
- Produces (later tasks rely on these exact signatures):
  - `PostingAccountsDoc.CategoryMaps` — `Dictionary<string, Dictionary<string, Guid>>`, defaults to empty.
  - `Task PostingAccountStore.SetCategoryMapAsync(Guid clientId, string moduleKey, IReadOnlyDictionary<string, Guid> map, CancellationToken cancellationToken = default)`
  - `Task<IReadOnlyDictionary<string, Guid>?> IPostingAccountsSource.GetCategoryMapAsync(Guid clientId, string moduleKey, CancellationToken ct = default)` — **default interface implementation returning `null`** (`null` = no stored map; empty dictionary = stored-and-empty). `StorePostingAccountsSource` overrides it.

- [ ] **Step 1: Write the failing tests**

Append to `PostingAccountStoreTests.cs` (inside the existing class):

```csharp
[Fact]
public async Task Set_category_map_round_trips_and_upserts_a_fresh_client()
{
    PostingAccountStore store = fixture.PostingAccounts();
    Guid clientId = Guid.NewGuid();
    Guid consulting = Guid.NewGuid();
    await store.SetCategoryMapAsync(clientId, "receivables", new Dictionary<string, Guid> { ["Consulting"] = consulting });

    PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
    Assert.Equal(clientId, doc.ClientId);   // upsert seeded ClientId from the filter
    Assert.Equal(consulting, doc.CategoryMaps["receivables"]["Consulting"]);
}

[Fact]
public async Task Set_category_map_does_not_clobber_slot_accounts_or_other_modules_maps()
{
    PostingAccountStore store = fixture.PostingAccounts();
    Guid clientId = Guid.NewGuid();
    Guid cash = Guid.NewGuid();
    Guid other = Guid.NewGuid();
    await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = cash });
    await store.SetCategoryMapAsync(clientId, "othermodule", new Dictionary<string, Guid> { ["X"] = other });
    await store.SetCategoryMapAsync(clientId, "receivables", new Dictionary<string, Guid> { ["Consulting"] = Guid.NewGuid() });

    PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
    Assert.Equal(cash, doc.Accounts["cash"]["Cash"]);                  // slots untouched
    Assert.Equal(other, doc.CategoryMaps["othermodule"]["X"]);         // other module's map untouched
    Assert.True(doc.CategoryMaps.ContainsKey("receivables"));
}

[Fact]
public async Task Set_category_map_full_replaces_the_modules_map()
{
    PostingAccountStore store = fixture.PostingAccounts();
    Guid clientId = Guid.NewGuid();
    await store.SetCategoryMapAsync(clientId, "receivables", new Dictionary<string, Guid> { ["Old"] = Guid.NewGuid() });
    Guid kept = Guid.NewGuid();
    await store.SetCategoryMapAsync(clientId, "receivables", new Dictionary<string, Guid> { ["New"] = kept });

    PostingAccountsDoc doc = (await store.GetAsync(clientId))!;
    Assert.Equal(kept, Assert.Single(doc.CategoryMaps["receivables"]).Value);
    Assert.False(doc.CategoryMaps["receivables"].ContainsKey("Old"));
}

[Fact]
public async Task Source_returns_stored_category_map_including_empty_or_null_when_unset()
{
    PostingAccountStore store = fixture.PostingAccounts();
    StorePostingAccountsSource source = new(store);
    Guid clientId = Guid.NewGuid();

    Assert.Null(await source.GetCategoryMapAsync(clientId, "receivables"));   // no doc at all

    await store.SetModuleAsync(clientId, "cash", new Dictionary<string, Guid> { ["Cash"] = Guid.NewGuid() });
    Assert.Null(await source.GetCategoryMapAsync(clientId, "receivables"));   // doc exists, no map for module

    await store.SetCategoryMapAsync(clientId, "receivables", new Dictionary<string, Guid>());
    IReadOnlyDictionary<string, Guid>? empty = await source.GetCategoryMapAsync(clientId, "receivables");
    Assert.NotNull(empty);                                                    // stored-empty is present, not null
    Assert.Empty(empty);

    Guid consulting = Guid.NewGuid();
    await store.SetCategoryMapAsync(clientId, "receivables", new Dictionary<string, Guid> { ["Consulting"] = consulting });
    Assert.Equal(consulting, (await source.GetCategoryMapAsync(clientId, "receivables"))!["Consulting"]);
}
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountStoreTests" 2>&1 | Select-Object -Last 20`
Expected: compile errors — `PostingAccountsDoc` has no `CategoryMaps`, no `SetCategoryMapAsync`, no `GetCategoryMapAsync`.

- [ ] **Step 3: Implement the store field + setter**

In `PostingAccountStore.cs`, add to `PostingAccountsDoc` (below `Accounts`):

```csharp
    /// <summary>Per-module dynamic category maps ({moduleKey → {category → account id}}). Parallel to
    /// <see cref="Accounts"/>; only receivables uses it today (invoice revenue-by-category). A stored
    /// entry — even an empty one — is the complete per-client truth and wins over process config.</summary>
    public Dictionary<string, Dictionary<string, Guid>> CategoryMaps { get; set; } = new();
```

Add to `PostingAccountStore` (below `SetModuleAsync`):

```csharp
    /// <summary>Upsert the client's category map for one module, replacing it wholesale (other modules
    /// and the slot accounts untouched).</summary>
    public async Task SetCategoryMapAsync(
        Guid clientId, string moduleKey, IReadOnlyDictionary<string, Guid> map, CancellationToken cancellationToken = default)
    {
        // Same targeted-update shape as SetModuleAsync: writes only CategoryMaps.<moduleKey>, so
        // concurrent writes to slots or other modules' maps cannot clobber it. Upsert seeds ClientId
        // from the filter on insert.
        await _accounts.UpdateOneAsync(
            d => d.ClientId == clientId,
            Builders<PostingAccountsDoc>.Update.Set($"CategoryMaps.{moduleKey}", new Dictionary<string, Guid>(map)),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }
```

- [ ] **Step 4: Implement the port method**

In `PostingAccountsSource.cs`, add to `IPostingAccountsSource` (below `GetAsync`):

```csharp
    /// <summary>The client's stored category map for a module (category → account id), or null when the
    /// client has none stored. An EMPTY map is a real stored value (the admin cleared the categories)
    /// and must be returned as empty, not null. Default implementation returns null so existing test
    /// fakes and any source without category support need no change.</summary>
    Task<IReadOnlyDictionary<string, Guid>?> GetCategoryMapAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>?>(null);
```

Add to `StorePostingAccountsSource`:

```csharp
    public async Task<IReadOnlyDictionary<string, Guid>?> GetCategoryMapAsync(Guid clientId, string moduleKey, CancellationToken ct = default)
    {
        PostingAccountsDoc? doc = await store.GetAsync(clientId, ct);
        return doc is not null && doc.CategoryMaps.TryGetValue(moduleKey, out Dictionary<string, Guid>? map)
            ? map
            : null;
    }
```

- [ ] **Step 5: Run the tests and make sure they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountStoreTests" 2>&1 | Select-Object -Last 20`
Expected: PASS (all, including the pre-existing store tests).

- [ ] **Step 6: Commit**

```powershell
git add Backend/Accounting101.Ledger.Api/Control/PostingAccountStore.cs Backend/Accounting101.Ledger.Api/Control/PostingAccountsSource.cs Backend/Accounting101.Ledger.Api.Tests/PostingAccountStoreTests.cs
git commit -m @'
feat(posting-accounts): store per-client category maps alongside slot accounts

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 2: API — revenue-categories GET/PUT + capability registry + contracts

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Control/PostingAccountCategoryMaps.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/PostingAccountEndpoints.cs`
- Modify: `Backend/Accounting101.Ledger.Contracts/PostingAccountContracts.cs`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs`

**Interfaces:**
- Consumes (from Task 1): `PostingAccountStore.SetCategoryMapAsync(Guid, string, IReadOnlyDictionary<string, Guid>, CancellationToken)`, `PostingAccountsDoc.CategoryMaps`.
- Produces:
  - `PostingAccountCategoryMaps.ConfigSectionFor(string moduleKey)` → `string?` — non-null only for `"receivables"` → `"Receivables:Accounts:RevenueByCategory"`.
  - Contracts: `RevenueCategoriesResponse(string ModuleKey, IReadOnlyDictionary<string, Guid> Categories, string Source)` (`Source` is `"stored"` or `"config"`), `SetRevenueCategoriesRequest(IReadOnlyDictionary<string, Guid> Categories)`.
  - Routes: `GET`/`PUT /clients/{clientId:guid}/posting-accounts/{moduleKey}/revenue-categories` (Task 4's UI calls these).
  - `ApiFixture.ConfigRevenueCategoryAccount` — a fixed Guid the fixture seeds into the host config section under key `FixtureCategory`.

- [ ] **Step 1: Seed the fixture config section**

In `ApiFixture.cs`, add a public field to the class:

```csharp
    /// <summary>Seeded into the host config as Receivables:Accounts:RevenueByCategory:FixtureCategory —
    /// lets endpoint tests observe the config-fallback path of the revenue-categories GET.</summary>
    public static readonly Guid ConfigRevenueCategoryAccount = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
```

and chain one more `UseSetting` onto the builder in `InitializeAsync`:

```csharp
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Mongo:ConnectionString", connectionString)
             .UseSetting("Mongo:ControlDatabase", ControlDatabase)
             .UseSetting("Mongo:PlatformDatabase", PlatformDatabase)
             .UseSetting("Tenancy:Platform:Enabled", "true")
             .UseSetting("Receivables:Accounts:RevenueByCategory:FixtureCategory", ConfigRevenueCategoryAccount.ToString()));
```

- [ ] **Step 2: Write the failing endpoint tests**

Append to `PostingAccountEndpointTests.cs`:

```csharp
    [Fact]
    public async Task Revenue_categories_get_falls_back_to_config_until_a_map_is_stored()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);

        RevenueCategoriesResponse before = (await http.GetFromJsonAsync<RevenueCategoriesResponse>(
            $"/clients/{clientId}/posting-accounts/receivables/revenue-categories"))!;
        Assert.Equal("config", before.Source);
        Assert.Equal(ApiFixture.ConfigRevenueCategoryAccount, Assert.Single(before.Categories).Value);
        Assert.Equal("FixtureCategory", Assert.Single(before.Categories).Key);

        Guid consulting = Guid.NewGuid();
        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/receivables/revenue-categories",
            new SetRevenueCategoriesRequest(new Dictionary<string, Guid> { ["Consulting"] = consulting }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        RevenueCategoriesResponse echoed = (await put.Content.ReadFromJsonAsync<RevenueCategoriesResponse>())!;
        Assert.Equal("stored", echoed.Source);

        RevenueCategoriesResponse after = (await http.GetFromJsonAsync<RevenueCategoriesResponse>(
            $"/clients/{clientId}/posting-accounts/receivables/revenue-categories"))!;
        Assert.Equal("stored", after.Source);
        Assert.Equal(consulting, after.Categories["Consulting"]);
        Assert.DoesNotContain("FixtureCategory", after.Categories.Keys);   // wholesale, not merged
    }

    [Fact]
    public async Task Revenue_categories_stored_empty_map_wins_over_config()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);

        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/receivables/revenue-categories",
            new SetRevenueCategoriesRequest(new Dictionary<string, Guid>()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        RevenueCategoriesResponse got = (await http.GetFromJsonAsync<RevenueCategoriesResponse>(
            $"/clients/{clientId}/posting-accounts/receivables/revenue-categories"))!;
        Assert.Equal("stored", got.Source);
        Assert.Empty(got.Categories);   // the fixture config category is suppressed
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Prof.Services")]
    [InlineData("$bad")]
    public async Task Revenue_categories_put_rejects_invalid_category_names(string badName)
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);
        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/receivables/revenue-categories",
            new SetRevenueCategoriesRequest(new Dictionary<string, Guid> { [badName] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Fact]
    public async Task Revenue_categories_reject_modules_without_category_map_support()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.AdminPostingAccounts, Capabilities.GlRead);

        HttpResponseMessage get = await http.GetAsync($"/clients/{clientId}/posting-accounts/cash/revenue-categories");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, get.StatusCode);

        HttpResponseMessage put = await http.PutAsJsonAsync(
            $"/clients/{clientId}/posting-accounts/cash/revenue-categories",
            new SetRevenueCategoriesRequest(new Dictionary<string, Guid> { ["Consulting"] = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    [Fact]
    public async Task Member_without_cap_is_forbidden_for_revenue_categories()
    {
        (Guid clientId, HttpClient http) = await MemberWithCashEnabledAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/posting-accounts/receivables/revenue-categories");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
```

- [ ] **Step 3: Run the new tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountEndpointTests" 2>&1 | Select-Object -Last 20`
Expected: compile errors — `RevenueCategoriesResponse` / `SetRevenueCategoriesRequest` don't exist.

- [ ] **Step 4: Implement contracts, registry, endpoints**

Append to `PostingAccountContracts.cs`:

```csharp
/// <summary>A module's revenue-category map for a client (category → account id) and where it came
/// from: "stored" (per-client, wins wholesale — even empty) or "config" (deployment default).</summary>
public sealed record RevenueCategoriesResponse(string ModuleKey, IReadOnlyDictionary<string, Guid> Categories, string Source);

/// <summary>Full-replace a module's per-client revenue-category map.</summary>
public sealed record SetRevenueCategoriesRequest(IReadOnlyDictionary<string, Guid> Categories);
```

Create `Backend/Accounting101.Ledger.Api/Control/PostingAccountCategoryMaps.cs`:

```csharp
namespace Accounting101.Ledger.Api.Control;

/// <summary>Which modules support a per-client revenue-category map, and where the deployment-default
/// map lives in process config. Only receivables today; fan out by adding a row.</summary>
public static class PostingAccountCategoryMaps
{
    private static readonly IReadOnlyDictionary<string, string> ConfigSections = new Dictionary<string, string>
    {
        ["receivables"] = "Receivables:Accounts:RevenueByCategory",
    };

    /// <summary>The module's config-fallback section, or null when the module has no category map.</summary>
    public static string? ConfigSectionFor(string moduleKey) =>
        ConfigSections.TryGetValue(moduleKey, out string? section) ? section : null;
}
```

In `PostingAccountEndpoints.cs`, register the routes in `MapPostingAccountEndpoints` (after the existing two):

```csharp
        g.MapGet("/{moduleKey}/revenue-categories", GetRevenueCategories);
        g.MapPut("/{moduleKey}/revenue-categories", SetRevenueCategories);
```

and add the handlers (same gating/404 shape as the existing pair):

```csharp
    private static async Task<IResult> GetRevenueCategories(
        Guid clientId, string moduleKey, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        PostingAccountStore store, IConfiguration configuration, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminPostingAccounts, actorFactory, control, ct))
            return Results.Forbid();
        if (await control.GetClientAsync(clientId, ct) is null) return Results.NotFound();

        string? section = PostingAccountCategoryMaps.ConfigSectionFor(moduleKey);
        if (section is null)
            return Results.Problem($"Module '{moduleKey}' does not support a revenue-category map.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        PostingAccountsDoc? doc = await store.GetAsync(clientId, ct);
        if (doc is not null && doc.CategoryMaps.TryGetValue(moduleKey, out Dictionary<string, Guid>? stored))
            return Results.Ok(new RevenueCategoriesResponse(moduleKey, stored, "stored"));

        // Config fallback, strict like the module provider: absent section → empty; malformed id → loud.
        Dictionary<string, Guid> fromConfig = configuration.GetSection(section).GetChildren().ToDictionary(
            child => child.Key,
            child => Guid.TryParse(child.Value, out Guid id)
                ? id
                : throw new InvalidOperationException(
                    $"Revenue category '{child.Key}' has a malformed account id '{child.Value}'."));
        return Results.Ok(new RevenueCategoriesResponse(moduleKey, fromConfig, "config"));
    }

    private static async Task<IResult> SetRevenueCategories(
        Guid clientId, string moduleKey, SetRevenueCategoriesRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, PostingAccountStore store, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminPostingAccounts, actorFactory, control, ct))
            return Results.Forbid();
        if (await control.GetClientAsync(clientId, ct) is null) return Results.NotFound();

        if (PostingAccountCategoryMaps.ConfigSectionFor(moduleKey) is null)
            return Results.Problem($"Module '{moduleKey}' does not support a revenue-category map.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        IReadOnlyDictionary<string, Guid> categories = request.Categories ?? new Dictionary<string, Guid>();
        // Names become BSON element names under CategoryMaps.<moduleKey>; dot/dollar keys are unsafe there.
        if (categories.Keys.FirstOrDefault(k =>
                string.IsNullOrWhiteSpace(k) || k.Contains('.') || k.StartsWith('$')) is { } bad)
            return Results.Problem(
                $"Invalid revenue category name '{bad}': names must be non-blank and must not contain '.' or start with '$'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        await store.SetCategoryMapAsync(clientId, moduleKey, categories, ct);
        return Results.Ok(new RevenueCategoriesResponse(moduleKey, categories, "stored"));
    }
```

Also update the class doc-comment on `PostingAccountEndpoints` to mention the revenue-categories pair, e.g. append: `Modules with a dynamic category map (receivables) additionally expose GET/PUT {moduleKey}/revenue-categories.`

- [ ] **Step 5: Run the endpoint tests and make sure they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PostingAccountEndpointTests" 2>&1 | Select-Object -Last 20`
Expected: PASS (new + all pre-existing cases).

- [ ] **Step 6: Run the whole Ledger.Api test project (fixture change touches everything)**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests 2>&1 | Select-Object -Last 10`
Expected: PASS, no failures.

- [ ] **Step 7: Commit**

```powershell
git add Backend/Accounting101.Ledger.Api/Control/PostingAccountCategoryMaps.cs Backend/Accounting101.Ledger.Api/Endpoints/PostingAccountEndpoints.cs Backend/Accounting101.Ledger.Contracts/PostingAccountContracts.cs Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs Backend/Accounting101.Ledger.Api.Tests/PostingAccountEndpointTests.cs
git commit -m @'
feat(posting-accounts): GET/PUT per-client revenue-category map with config fallback

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 3: Provider — stored category map wins wholesale in `StoreBackedInvoiceAccountsProvider`

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/StoreBackedInvoiceAccountsProvider.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/StoreBackedInvoiceAccountsProviderTests.cs`

**Interfaces:**
- Consumes (from Task 1): `IPostingAccountsSource.GetCategoryMapAsync(Guid clientId, string moduleKey, CancellationToken ct = default)` → `Task<IReadOnlyDictionary<string, Guid>?>` (null = no stored map; the interface has a default implementation returning null, so fakes that don't implement it still compile).
- Produces: unchanged `IInvoiceAccountsProvider` surface — only the sourcing of `InvoicePostingAccounts.RevenueAccountsByCategory` changes.

- [ ] **Step 1: Extend the file-local fake and write the failing tests**

In `StoreBackedInvoiceAccountsProviderTests.cs`, replace the existing `FakeSource` with:

```csharp
file sealed class FakeSource(Dictionary<string, Guid> map, Dictionary<string, Guid>? categories = null) : IPostingAccountsSource
{
    public Task<IReadOnlyDictionary<string, Guid>> GetAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>>(map);

    public Task<IReadOnlyDictionary<string, Guid>?> GetCategoryMapAsync(Guid clientId, string moduleKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, Guid>?>(categories);
}
```

(All existing tests construct `new FakeSource(new(...))` with one argument, so they compile unchanged and keep `categories = null` → config fallback; the existing `Reads_revenue_category_map_from_config_even_with_stored_fixed_slots` test MUST stay green as-is.)

Append the new tests:

```csharp
    [Fact]
    public async Task Stored_category_map_wins_wholesale_over_config()
    {
        Guid stored = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:RevenueByCategory:License"] = Guid.NewGuid().ToString();   // config has a DIFFERENT key
        var provider = new StoreBackedInvoiceAccountsProvider(
            new FakeSource(new(), new Dictionary<string, Guid> { ["Consulting"] = stored }), Config(cfg));

        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(stored, got.RevenueAccountsByCategory["Consulting"]);
        Assert.Single(got.RevenueAccountsByCategory);   // "License" from config is NOT merged in
    }

    [Fact]
    public async Task Stored_empty_category_map_suppresses_config_categories()
    {
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:RevenueByCategory:License"] = Guid.NewGuid().ToString();
        var provider = new StoreBackedInvoiceAccountsProvider(new FakeSource(new(), new Dictionary<string, Guid>()), Config(cfg));

        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Empty(got.RevenueAccountsByCategory);
    }

    [Fact]
    public async Task Fixed_slot_resolution_is_unaffected_by_a_stored_category_map()
    {
        Guid revenue = Guid.NewGuid();
        Dictionary<string, string?> cfg = Keys.ToDictionary(k => $"Receivables:Accounts:{k}", k => (string?)Guid.NewGuid().ToString());
        cfg["Receivables:Accounts:Revenue"] = revenue.ToString();
        var provider = new StoreBackedInvoiceAccountsProvider(
            new FakeSource(new(), new Dictionary<string, Guid> { ["Consulting"] = Guid.NewGuid() }), Config(cfg));

        InvoicePostingAccounts got = await provider.GetAsync(Guid.NewGuid());
        Assert.Equal(revenue, got.DefaultRevenueAccountId);   // still resolved store→config, independent of the map
    }
```

- [ ] **Step 2: Run the receivables tests to verify the new ones fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~StoreBackedInvoiceAccountsProviderTests" 2>&1 | Select-Object -Last 20`
Expected: the three new tests FAIL (`Stored_category_map_wins_wholesale_over_config` and `Stored_empty_category_map_suppresses_config_categories` assert against config-derived values); everything pre-existing PASSES.

- [ ] **Step 3: Implement the provider change**

In `StoreBackedInvoiceAccountsProvider.cs`, replace `GetAsync` with:

```csharp
    public async Task<InvoicePostingAccounts> GetAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, Guid> stored = await source.GetAsync(clientId, "receivables", cancellationToken);
        IReadOnlyDictionary<string, Guid>? categoryMap = await source.GetCategoryMapAsync(clientId, "receivables", cancellationToken);
        return new InvoicePostingAccounts
        {
            ReceivableAccountId       = Resolve(stored, "Receivable"),
            DefaultRevenueAccountId   = Resolve(stored, "Revenue"),
            SalesTaxPayableAccountId  = Resolve(stored, "SalesTaxPayable"),
            RevenueAccountsByCategory = categoryMap ?? ReadCategoryMap("Receivables:Accounts:RevenueByCategory"),
        };
    }
```

and replace the class doc-comment (it currently says the map is NOT per-client-configurable) with:

```csharp
/// <summary>Resolves the invoice posting accounts per client: each fixed account is the one configured on
/// the posting-accounts admin screen if set, else the process config value (<c>Receivables:Accounts:*</c>)
/// — so behavior is unchanged until a per-client account is chosen. The dynamic
/// <c>RevenueAccountsByCategory</c> map is the client's stored map when one exists (wholesale — a stored
/// EMPTY map deliberately suppresses the config categories), else the config section
/// (<c>Receivables:Accounts:RevenueByCategory</c>).</summary>
```

- [ ] **Step 4: Run the full receivables test project and make sure it passes**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests 2>&1 | Select-Object -Last 10`
Expected: PASS — including the untouched `StoreBackedPaymentAccountsProviderTests` (its `FakeSource` relies on the interface default).

- [ ] **Step 5: Run the full solution (interface change touches every module's fakes)**

Run: `dotnet test Accounting101.slnx 2>&1 | Select-Object -Last 15`
Expected: PASS, no failures across all projects.

- [ ] **Step 6: Commit**

```powershell
git add Modules/Receivables/Accounting101.Receivables.Api/StoreBackedInvoiceAccountsProvider.cs Modules/Receivables/Accounting101.Receivables.Tests/StoreBackedInvoiceAccountsProviderTests.cs
git commit -m @'
feat(receivables): per-client revenue-category map wins wholesale over config

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 4: UI — "Revenue by category" sub-section on the posting-accounts screen

**Files:**
- Modify: `UI/Angular/src/app/core/posting-accounts/posting-accounts.ts`
- Modify: `UI/Angular/src/app/core/posting-accounts/posting-accounts.service.ts`
- Modify: `UI/Angular/src/app/features/admin/posting-accounts.ts`
- Test: `UI/Angular/src/app/features/admin/posting-accounts.spec.ts`

**Interfaces:**
- Consumes (from Task 2, wire): `GET/PUT {apiBaseUrl}/clients/{id}/posting-accounts/receivables/revenue-categories`; GET/PUT-echo body `{ moduleKey: string, categories: Record<string, string>, source: 'stored' | 'config' }`; PUT request body `{ categories: Record<string, string> }`.
- Produces: nothing downstream — this is the leaf task.

- [ ] **Step 1: Add wire types and service methods**

Append to `UI/Angular/src/app/core/posting-accounts/posting-accounts.ts`:

```typescript
export interface RevenueCategories {
  moduleKey: string;
  categories: Record<string, string>;
  source: 'stored' | 'config';
}
```

In `posting-accounts.service.ts`, add the import of `RevenueCategories` to the existing import from `./posting-accounts` and append two methods to the service:

```typescript
  revenueCategories(): Observable<RevenueCategories> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<RevenueCategories>(`${this.clientBase()}/posting-accounts/receivables/revenue-categories`);
  }

  setRevenueCategories(categories: Record<string, string>): Observable<unknown> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put(`${this.clientBase()}/posting-accounts/receivables/revenue-categories`, { categories });
  }
```

- [ ] **Step 2: Write the failing screen specs**

Append to `UI/Angular/src/app/features/admin/posting-accounts.spec.ts` (inside the existing `describe`, reusing its `seed`, `base`, `accounts`, `http` helpers):

```typescript
  const rxSlot = { moduleKey: 'receivables', slotKey: 'Revenue', label: 'Revenue', expectedType: 'Revenue', requiredDimensions: [], currentAccountId: null };

  function bootReceivables(categories: Record<string, string>, source: 'stored' | 'config') {
    seed('admin.postingAccounts'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(PostingAccountsScreen);
    f.detectChanges();
    http.expectOne(`${base}/posting-accounts`).flush({ slots: [rxSlot] });
    http.expectOne(`${base}/accounts`).flush(accounts);
    f.detectChanges();
    http.expectOne(`${base}/posting-accounts/receivables/revenue-categories`)
      .flush({ moduleKey: 'receivables', categories, source });
    f.detectChanges();
    return f;
  }

  it('does not request revenue categories when receivables is not among the slots', () => {
    boot(['admin.postingAccounts']);   // cash-only boot from the existing helper
    http.expectNone(`${base}/posting-accounts/receivables/revenue-categories`);
  });

  it('renders category rows from the GET and notes the config source', () => {
    const f = bootReceivables({ Consulting: 'a2' }, 'config');
    const el = f.nativeElement as HTMLElement;
    const name = el.querySelector('[data-testid="category-name-0"]') as HTMLInputElement;
    expect(name.value).toBe('Consulting');
    const select = el.querySelector('[data-testid="category-account-0"]') as HTMLSelectElement;
    expect(select.value).toBe('a2');
    expect(el.textContent).toContain('deployment defaults');
  });

  it('saves the rows as a full-replace PUT and flips the source note off', () => {
    const f = bootReceivables({ Consulting: 'a2' }, 'config');
    const c = f.componentInstance as PostingAccountsScreen;
    c.addCategory();
    c.setCategoryName(1, 'License');
    c.setCategoryAccount(1, 'a1');
    c.saveCategories();
    const req = http.expectOne(`${base}/posting-accounts/receivables/revenue-categories`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ categories: { Consulting: 'a2', License: 'a1' } });
    req.flush({ moduleKey: 'receivables', categories: { Consulting: 'a2', License: 'a1' }, source: 'stored' });
    f.detectChanges();
    expect(c.categoriesSaved()).toBeTrue();
    expect((f.nativeElement as HTMLElement).textContent).not.toContain('deployment defaults');
  });

  it('deleting a row removes it from the next save', () => {
    const f = bootReceivables({ Consulting: 'a2', License: 'a1' }, 'stored');
    const c = f.componentInstance as PostingAccountsScreen;
    c.removeCategory(0);
    c.saveCategories();
    const req = http.expectOne(`${base}/posting-accounts/receivables/revenue-categories`);
    expect(req.request.body).toEqual({ categories: { License: 'a1' } });
    req.flush({ moduleKey: 'receivables', categories: { License: 'a1' }, source: 'stored' });
  });

  it('blocks save on duplicate names (case-sensitive: differing case is allowed)', () => {
    const f = bootReceivables({ Consulting: 'a2' }, 'stored');
    const c = f.componentInstance as PostingAccountsScreen;
    c.addCategory();
    c.setCategoryName(1, 'Consulting');
    c.setCategoryAccount(1, 'a1');
    expect(c.categoryValidation()).toContain('unique');
    c.saveCategories();
    http.expectNone(`${base}/posting-accounts/receivables/revenue-categories`);

    c.setCategoryName(1, 'consulting');   // different case — distinct, valid
    expect(c.categoryValidation()).toBeNull();
  });

  it('blocks save on blank, dotted, or dollar-prefixed names and unset accounts', () => {
    const f = bootReceivables({}, 'stored');
    const c = f.componentInstance as PostingAccountsScreen;
    c.addCategory();
    expect(c.categoryValidation()).not.toBeNull();          // blank name
    c.setCategoryName(0, 'Prof.Services');
    c.setCategoryAccount(0, 'a1');
    expect(c.categoryValidation()).toContain('.');
    c.setCategoryName(0, '$bad');
    expect(c.categoryValidation()).not.toBeNull();
    c.setCategoryName(0, 'Services');
    c.setCategoryAccount(0, '');
    expect(c.categoryValidation()).toContain('account');    // unset account
    c.setCategoryAccount(0, 'a1');
    expect(c.categoryValidation()).toBeNull();
  });
```

- [ ] **Step 3: Run the specs to verify the new ones fail**

Run (from `UI/Angular`): `npx ng test --watch=false 2>&1 | Select-Object -Last 25`
Expected: new specs FAIL (missing methods/signals like `addCategory`, `setCategoryName`, `categoryValidation`; unexpected-request errors); all pre-existing specs still PASS — the cash/payroll boots must NOT see a revenue-categories request.

- [ ] **Step 4: Implement the screen sub-section**

In `UI/Angular/src/app/features/admin/posting-accounts.ts`:

Add a row model next to `ModuleGroup`:

```typescript
interface CategoryRow { name: string; accountId: string; }
```

Add state and validation to the class:

```typescript
  // "Revenue by category" (receivables only) — the one intentionally non-slot-driven part of the screen.
  readonly categoryRows = signal<CategoryRow[]>([]);
  readonly categorySource = signal<'stored' | 'config' | null>(null);
  readonly categoriesLoaded = signal(false);
  readonly categoriesSaved = signal(false);
  readonly categoryError = signal<string | null>(null);
  readonly categoryValidation = computed<string | null>(() => {
    const rows = this.categoryRows();
    const names = rows.map((r) => r.name);
    if (names.some((n) => n.trim() === '')) return 'Category names cannot be blank.';
    if (names.some((n) => n.includes('.') || n.startsWith('$'))) return 'Category names cannot contain "." or start with "$".';
    if (new Set(names).size !== names.length) return 'Category names must be unique.';
    if (rows.some((r) => !r.accountId)) return 'Choose an account for every category.';
    return null;
  });
```

In the constructor's slots subscription `next` handler, after `this.slotsLoaded.set(true);`, load the categories only when a receivables group will render:

```typescript
        if (p.slots.some((s) => s.moduleKey === 'receivables')) {
          this.service.revenueCategories().subscribe({
            next: (rc) => {
              this.categoryRows.set(Object.entries(rc.categories).map(([name, accountId]) => ({ name, accountId })));
              this.categorySource.set(rc.source);
              this.categoriesLoaded.set(true);
            },
            error: () => this.categoryError.set('Could not load revenue categories.'),
          });
        }
```

Add the row methods:

```typescript
  addCategory(): void {
    this.categoryRows.update((rows) => [...rows, { name: '', accountId: '' }]);
    this.categoriesSaved.set(false);
  }

  removeCategory(index: number): void {
    this.categoryRows.update((rows) => rows.filter((_, i) => i !== index));
    this.categoriesSaved.set(false);
  }

  setCategoryName(index: number, name: string): void {
    this.categoryRows.update((rows) => rows.map((r, i) => (i === index ? { ...r, name } : r)));
    this.categoriesSaved.set(false);
  }

  setCategoryAccount(index: number, accountId: string): void {
    this.categoryRows.update((rows) => rows.map((r, i) => (i === index ? { ...r, accountId } : r)));
    this.categoriesSaved.set(false);
  }

  onCategoryName(index: number, event: Event): void {
    this.setCategoryName(index, (event.target as HTMLInputElement).value);
  }

  onCategoryAccount(index: number, event: Event): void {
    this.setCategoryAccount(index, (event.target as HTMLSelectElement).value);
  }

  saveCategories(): void {
    if (this.categoryValidation() !== null) return;
    this.categoryError.set(null);
    const categories: Record<string, string> = {};
    for (const r of this.categoryRows()) categories[r.name] = r.accountId;
    this.service.setRevenueCategories(categories).subscribe({
      next: () => { this.categoriesSaved.set(true); this.categorySource.set('stored'); },
      error: (e) => this.categoryError.set(e?.error?.detail ?? 'Save failed.'),
    });
  }
```

In the template, inside the module `<section>`'s `<div class="space-y-3">`, immediately BEFORE the existing `@if (savedModule() === g.moduleKey)` line, add the sub-section (renders only in the receivables group; the 7 fixed slots above are untouched):

```html
              @if (g.moduleKey === 'receivables') {
                <div class="mt-4 border-t border-border pt-3" data-testid="revenue-categories">
                  <h3 class="text-sm font-medium">Revenue by category</h3>
                  <p class="text-xs text-muted-foreground mb-2">Invoice lines whose revenue category matches a row credit that account;
                     unmatched lines credit the Revenue account above.</p>
                  @if (categoryError()) { <p class="text-red-600 text-sm mb-2">{{ categoryError() }}</p> }
                  @if (categoriesLoaded()) {
                    @if (categorySource() === 'config') {
                      <p class="text-xs text-muted-foreground mb-2">Showing deployment defaults — saving stores a map for this client.</p>
                    }
                    @for (row of categoryRows(); track $index) {
                      <div class="flex items-center gap-2 mb-2">
                        <input type="text" placeholder="Category"
                               class="w-48 rounded border border-border bg-background px-3 py-2 text-sm"
                               [value]="row.name" (input)="onCategoryName($index, $event)"
                               [attr.data-testid]="'category-name-' + $index" />
                        <select class="w-96 rounded border border-border bg-background px-3 py-2 text-sm"
                                (change)="onCategoryAccount($index, $event)"
                                [attr.data-testid]="'category-account-' + $index">
                          <option value="" [selected]="row.accountId === ''">— choose account —</option>
                          @for (a of postableAccounts(); track a.id) {
                            <option [value]="a.id" [selected]="a.id === row.accountId">{{ a.number }} · {{ a.name }} ({{ a.type }})</option>
                          }
                        </select>
                        <button type="button" class="text-sm text-muted-foreground hover:text-red-600"
                                (click)="removeCategory($index)" [attr.data-testid]="'category-delete-' + $index"
                                aria-label="Remove category">✕</button>
                      </div>
                    }
                    <button type="button" hlmBtn variant="outline" (click)="addCategory()" data-testid="category-add">Add category</button>
                    @if (categoryValidation(); as msg) { <p class="text-red-600 text-sm mt-1">{{ msg }}</p> }
                    @if (categoriesSaved()) { <p class="text-green-600 text-sm mt-1">Categories saved.</p> }
                    <div class="mt-2">
                      <button *appCan="'admin.postingAccounts'" hlmBtn [disabled]="categoryValidation() !== null"
                              (click)="saveCategories()" data-testid="category-save">Save revenue categories</button>
                    </div>
                  } @else if (!categoryError()) {
                    <p class="text-sm text-muted-foreground">Loading…</p>
                  }
                </div>
              }
```

(If `hlmBtn variant="outline"` fails to compile in this codebase's Spartan version, drop the `variant` attribute — plain `hlmBtn` like the existing Save button is fine.)

- [ ] **Step 5: Run all specs and make sure they pass**

Run (from `UI/Angular`): `npx ng test --watch=false 2>&1 | Select-Object -Last 15`
Expected: ALL specs pass (new + pre-existing across the app).

- [ ] **Step 6: Gate on the PROD build (bundle budgets)**

Run (from `UI/Angular`): `npx ng build 2>&1 | Select-Object -Last 15`
Expected: build succeeds with no budget errors.

- [ ] **Step 7: Commit**

```powershell
git add UI/Angular/src/app/core/posting-accounts/posting-accounts.ts UI/Angular/src/app/core/posting-accounts/posting-accounts.service.ts UI/Angular/src/app/features/admin/posting-accounts.ts UI/Angular/src/app/features/admin/posting-accounts.spec.ts
git commit -m @'
feat(posting-accounts): revenue-by-category editor in the Receivables section

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

## After the tasks (supervisor, not subagents)

1. Final review of the whole branch diff (opus-level review per the established workflow).
2. Live JordanSoft smoke per the spec's Verification section — including the `$unset CategoryMaps.receivables` restore step and exact module-set restore. Zero footprint.
3. Merge to `master` and push.
