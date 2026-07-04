# Fixed Assets FA-1 — Asset Register Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `fixedassets` module with an asset-register lifecycle (create/edit/deactivate/get/list) backed by the engine's reference document store — master data for the depreciation runs and disposals that later slices post.

**Architecture:** A new module in three projects (domain `Accounting101.FixedAssets`, web `Accounting101.FixedAssets.Api`, tests), following the Payroll/Receivables split. Assets are `.Reference` documents (mutable, auditable, deactivatable). No GL posting, no ledger client in FA-1. The module installs via `AddModule`; capability enforcement flows through the existing `ScopedDocumentStore` → `ModuleAccess` chokepoint.

**Tech Stack:** .NET (C#), ASP.NET Core minimal APIs, MongoDB via the engine document store, xUnit + EphemeralMongo (`SharedMongo`), `WebApplicationFactory<Program>`.

## Global Constraints

- Assets are a **`.Reference`** collection named `assets`. `AccumulatedDepreciation` and `Status` are server-owned (create stamps `0`/`Active`; update preserves them; FA-1 never sets `Disposed`).
- `DepreciationMethod` enum is multi-member from the start (`StraightLine = 0`, `DecliningBalance = 1`); FA-1 stores + validates the method but computes no depreciation.
- Validation (create + update) → **422**: `Description` non-blank; `AcquisitionCost > 0`; `UsefulLifeMonths > 0`; `0 <= SalvageValue <= AcquisitionCost`; `DecliningBalanceFactor` present and `> 0` iff `Method == DecliningBalance` (else must be null).
- **No GL posting / no `HttpLedgerClient` / no posting-accounts provider** in FA-1.
- `RolePresets` ALREADY grants `FixedAssetsRead`/`FixedAssetsWrite` — do NOT modify it. The only engine wiring is the `CapabilityForModule` arm.
- Money is `decimal`; USD only (single-currency by decision).
- Stage explicit paths only (never `git add -A`); leave pre-existing uncommitted noise (`*.Tests.csproj`, `environment.ts`, `.slnx`) — EXCEPT this plan legitimately edits `Accounting101.slnx` to add the new projects, so that one file IS staged in Task 1. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

## File Structure

**Create — domain (`Modules/FixedAssets/Accounting101.FixedAssets/`):** `Accounting101.FixedAssets.csproj`, `DepreciationMethod.cs`, `AssetStatus.cs`, `AssetBody.cs`, `AssetDocument.cs`, `Asset.cs`, `AssetView.cs`, `AssetValidation.cs`, `FixedAssetsPorts.cs` (`IAssetStore`, `DeactivateResult`), `DocumentAssetStore.cs`, `FixedAssetsService.cs`.

**Create — web (`Modules/FixedAssets/Accounting101.FixedAssets.Api/`):** `Accounting101.FixedAssets.Api.csproj`, `FixedAssetsRequests.cs`, `FixedAssetsEndpoints.cs`, `FixedAssetsServiceExtensions.cs`.

**Create — tests (`Modules/FixedAssets/Accounting101.FixedAssets.Tests/`):** `Accounting101.FixedAssets.Tests.csproj`, `AssetValidationTests.cs`, `AssetDocumentStoreFixture.cs`, `AssetDocumentStoreTests.cs`, `FixedAssetsHostFixture.cs`, `FixedAssetsEndpointsTests.cs`.

**Modify:** `Accounting101.slnx` (add 3 projects); `Accounting101.Host/Accounting101.Host.csproj` (ref the Api project); `Accounting101.Host/Program.cs` (`AddFixedAssets` + `MapFixedAssetsEndpoints`); `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs` (`CapabilityForModule` arm).

---

## Task 1: Domain project — asset model + validation

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/Accounting101.FixedAssets.csproj`
- Create: `.../DepreciationMethod.cs`, `.../AssetStatus.cs`, `.../AssetBody.cs`, `.../AssetDocument.cs`, `.../Asset.cs`, `.../AssetView.cs`, `.../AssetValidation.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Accounting101.FixedAssets.Tests.csproj`
- Create: `.../Accounting101.FixedAssets.Tests/AssetValidationTests.cs`
- Modify: `Accounting101.slnx`

**Interfaces:**
- Produces: `Asset`, `AssetBody(string Description, decimal AcquisitionCost, DateOnly InServiceDate, int UsefulLifeMonths, decimal SalvageValue, DepreciationMethod Method, decimal? DecliningBalanceFactor)`, `AssetDocument(...same + AssetStatus Status, decimal AccumulatedDepreciation)`, `AssetView(Asset Asset)`, `DepreciationMethod { StraightLine, DecliningBalance }`, `AssetStatus { Active, Disposed }`, `AssetValidation.Validate(AssetBody) → string?`.

- [ ] **Step 1: Create the domain csproj.** `Modules/FixedAssets/Accounting101.FixedAssets/Accounting101.FixedAssets.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- The fixed-assets module (domain): an upstream consumer of the ledger engine. Depends only on the
       contract shape (IDocumentStore, PagedResponse), never on engine internals. FA-1 does not post. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\Backend\Accounting101.Ledger.Contracts\Accounting101.Ledger.Contracts.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the domain types.** Create each file:

`DepreciationMethod.cs`:
```csharp
namespace Accounting101.FixedAssets;

/// <summary>How an asset depreciates. Multi-member from the start so FA-2 can add a pluggable strategy
/// without a data migration; FA-1 stores and validates the choice but computes nothing. 0-default so a
/// legacy document reads as straight-line.</summary>
public enum DepreciationMethod
{
    StraightLine = 0,
    DecliningBalance = 1,
}
```

`AssetStatus.cs`:
```csharp
namespace Accounting101.FixedAssets;

/// <summary>Register lifecycle of an asset. New assets are Active; FA-3 disposal sets Disposed. FA-1 never
/// sets Disposed. 0-default so a legacy document reads as Active.</summary>
public enum AssetStatus
{
    Active = 0,
    Disposed = 1,
}
```

`AssetBody.cs`:
```csharp
namespace Accounting101.FixedAssets;

/// <summary>The editable parameters of an asset (create/update input). Status and AccumulatedDepreciation
/// are server-owned and are NOT part of the body.</summary>
public sealed record AssetBody(
    string Description,
    decimal AcquisitionCost,
    DateOnly InServiceDate,
    int UsefulLifeMonths,
    decimal SalvageValue,
    DepreciationMethod Method,
    decimal? DecliningBalanceFactor);
```

`AssetDocument.cs`:
```csharp
namespace Accounting101.FixedAssets;

/// <summary>The stored shape of an asset — the opaque reference-document body. The asset id is the
/// document id, so it is not repeated here. Status and AccumulatedDepreciation are server-owned.</summary>
public sealed record AssetDocument(
    string Description,
    decimal AcquisitionCost,
    DateOnly InServiceDate,
    int UsefulLifeMonths,
    decimal SalvageValue,
    DepreciationMethod Method,
    decimal? DecliningBalanceFactor,
    AssetStatus Status,
    decimal AccumulatedDepreciation);
```

`Asset.cs`:
```csharp
namespace Accounting101.FixedAssets;

/// <summary>A fixed asset in the register. Its Id is the reference-document id.</summary>
public sealed record Asset
{
    public required Guid Id { get; init; }
    public required string Description { get; init; }
    public required decimal AcquisitionCost { get; init; }
    public required DateOnly InServiceDate { get; init; }
    public required int UsefulLifeMonths { get; init; }
    public required decimal SalvageValue { get; init; }
    public required DepreciationMethod Method { get; init; }
    public decimal? DecliningBalanceFactor { get; init; }
    public required AssetStatus Status { get; init; }
    public required decimal AccumulatedDepreciation { get; init; }
}
```

`AssetView.cs`:
```csharp
namespace Accounting101.FixedAssets;

/// <summary>Read model for an asset — the register record plus its net book value (cost − accumulated
/// depreciation), a convenience for callers.</summary>
public sealed record AssetView(Asset Asset)
{
    public decimal NetBookValue => Asset.AcquisitionCost - Asset.AccumulatedDepreciation;
}
```

`AssetValidation.cs`:
```csharp
namespace Accounting101.FixedAssets;

/// <summary>Pure validation of an asset body. Returns null when valid, else a human-readable reason
/// (surfaced as 422 at the endpoint).</summary>
public static class AssetValidation
{
    public static string? Validate(AssetBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(body.Description))
            return "Description is required.";
        if (body.AcquisitionCost <= 0)
            return "AcquisitionCost must be greater than zero.";
        if (body.UsefulLifeMonths <= 0)
            return "UsefulLifeMonths must be greater than zero.";
        if (body.SalvageValue < 0)
            return "SalvageValue must not be negative.";
        if (body.SalvageValue > body.AcquisitionCost)
            return "SalvageValue must not exceed AcquisitionCost.";
        if (body.Method == DepreciationMethod.DecliningBalance)
        {
            if (body.DecliningBalanceFactor is not { } factor || factor <= 0)
                return "DecliningBalanceFactor must be greater than zero for the declining-balance method.";
        }
        else if (body.DecliningBalanceFactor is not null)
        {
            return "DecliningBalanceFactor is only valid for the declining-balance method.";
        }
        return null;
    }
}
```

- [ ] **Step 3: Create the test csproj.** `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Accounting101.FixedAssets.Tests.csproj` — mirrors `Accounting101.Payroll.Tests.csproj` but references the FixedAssets projects. (The `Api` and `Host` references are added in Task 3; Task 1 references only the domain project, `Ledger.Api`, and `TestSupport`, which cover Tasks 1–2.)

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="10.0.1">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="EphemeralMongo" Version="3.2.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.7.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="4.0.0-pre.4">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.9" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Accounting101.FixedAssets\Accounting101.FixedAssets.csproj" />
    <ProjectReference Include="..\..\..\Backend\Accounting101.Ledger.Api\Accounting101.Ledger.Api.csproj" />
    <ProjectReference Include="..\..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Write the failing validation tests.** `AssetValidationTests.cs`:

```csharp
using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Tests;

public sealed class AssetValidationTests
{
    private static AssetBody Valid() => new(
        "Delivery van", 30000m, new DateOnly(2026, 1, 1), 60, 3000m, DepreciationMethod.StraightLine, null);

    [Fact]
    public void A_well_formed_straight_line_asset_is_valid() => Assert.Null(AssetValidation.Validate(Valid()));

    [Fact]
    public void A_well_formed_declining_balance_asset_is_valid()
    {
        AssetBody body = Valid() with { Method = DepreciationMethod.DecliningBalance, DecliningBalanceFactor = 2.0m };
        Assert.Null(AssetValidation.Validate(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_description_is_rejected(string description) =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { Description = description }));

    [Fact]
    public void Non_positive_cost_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { AcquisitionCost = 0m }));

    [Fact]
    public void Non_positive_life_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { UsefulLifeMonths = 0 }));

    [Fact]
    public void Negative_salvage_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { SalvageValue = -1m }));

    [Fact]
    public void Salvage_above_cost_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { SalvageValue = 40000m }));

    [Fact]
    public void Declining_balance_without_a_positive_factor_is_rejected()
    {
        Assert.NotNull(AssetValidation.Validate(Valid() with { Method = DepreciationMethod.DecliningBalance, DecliningBalanceFactor = null }));
        Assert.NotNull(AssetValidation.Validate(Valid() with { Method = DepreciationMethod.DecliningBalance, DecliningBalanceFactor = 0m }));
    }

    [Fact]
    public void A_factor_on_a_straight_line_asset_is_rejected() =>
        Assert.NotNull(AssetValidation.Validate(Valid() with { DecliningBalanceFactor = 2.0m }));
}
```

- [ ] **Step 5: Add the projects to the solution.** In `Accounting101.slnx`, add a new folder block after the `/Modules/Payroll/` folder (before `<Folder Name="/Modules/" />`):

```xml
  <Folder Name="/Modules/FixedAssets/">
    <Project Path="Modules/FixedAssets/Accounting101.FixedAssets/Accounting101.FixedAssets.csproj" />
    <Project Path="Modules/FixedAssets/Accounting101.FixedAssets.Api/Accounting101.FixedAssets.Api.csproj" />
    <Project Path="Modules/FixedAssets/Accounting101.FixedAssets.Tests/Accounting101.FixedAssets.Tests.csproj" />
  </Folder>
```

(The `.Api` project file does not exist until Task 3; referencing it in the solution is harmless — `dotnet` warns but still builds the projects that exist. If the build errors on the missing project, add only the domain + tests entries now and the `.Api` entry in Task 3.)

- [ ] **Step 6: Run the validation tests.**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: PASS (11 cases). If the missing `.Api` project entry breaks the solution build, apply the fallback in Step 5.

- [ ] **Step 7: Commit.**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/ Modules/FixedAssets/Accounting101.FixedAssets.Tests/ Accounting101.slnx
git commit -m "feat(fixedassets): FA-1 domain model + asset validation"
```

---

## Task 2: Asset store — reference-document persistence

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPorts.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/DocumentAssetStore.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetDocumentStoreFixture.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetDocumentStoreTests.cs`

**Interfaces:**
- Consumes: `Asset`, `AssetBody`, `AssetDocument`, `AssetStatus` (Task 1); `IDocumentStore`, `DocumentResult<T>`, `DocumentLifecycle`, `PagedResponse<T>` (Contracts); `ScopedDocumentStore`, `ModuleManifestBuilder`, `ModuleIdentity`, `ClientDatabaseResolver`, `ModuleAccess`, `FixedCurrentActor` (test fixture, from `Accounting101.Ledger.Api`).
- Produces: `IAssetStore` (Create/Update/Deactivate/Get/GetByClientPaged); `DeactivateResult { NotFound, AlreadyInactive, Deactivated }`; `DocumentAssetStore`.

- [ ] **Step 1: Write the failing store tests.** First the fixture — `AssetDocumentStoreFixture.cs` (mirrors the Receivables/Payables `DocumentStoreFixture`: a real `ScopedDocumentStore` bound to the `fixedassets` identity + a `.Reference("assets")` manifest):

```csharp
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Documents;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.FixedAssets.Tests;

/// <summary>A disposable EphemeralMongo instance wired exactly as the host wires the fixed-assets module:
/// a registered client + member + the "fixedassets" module, and a real ScopedDocumentStore bound to that
/// identity with a .Reference("assets") manifest. Store tests exercise the actual reference policy.</summary>
public sealed class AssetDocumentStoreFixture : IAsyncLifetime
{
    public Guid ClientId { get; private set; }
    public Guid UserId { get; private set; }
    public IDocumentStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);

        ControlStore control = new(mongo.GetDatabase("control_" + Guid.NewGuid().ToString("N")));
        ClientId = Guid.NewGuid();
        UserId = Guid.NewGuid();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = ClientId, Name = "Acme", DatabaseName = "client_" + ClientId.ToString("N"),
            EnabledModules = ["fixedassets"],
        });
        await control.AddMembershipAsync(UserId, ClientId, LedgerRole.Controller);
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "fixedassets", Name = "Fixed Assets", Enabled = true });

        ModuleManifest manifest = new ModuleManifestBuilder().Reference("assets").Build();

        Store = new ScopedDocumentStore(
            new ModuleIdentity("fixedassets"),
            manifest,
            new ClientDatabaseResolver(mongo, control),
            new FixedActor(UserId),
            new ModuleAccess(control));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>An ICurrentActor that always returns a fixed member principal.</summary>
internal sealed class FixedActor(Guid userId) : ICurrentActor
{
    public Actor Get() => new() { UserId = userId, Name = "Tester" };
}
```

Then the tests — `AssetDocumentStoreTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class AssetDocumentStoreTests(AssetDocumentStoreFixture fixture) : IClassFixture<AssetDocumentStoreFixture>
{
    private DocumentAssetStore Store() => new(fixture.Store);

    private static AssetBody Body(string description = "Van") =>
        new(description, 30000m, new DateOnly(2026, 1, 1), 60, 3000m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Create_stamps_active_status_and_zero_accumulated_depreciation()
    {
        Asset asset = await Store().CreateAsync(fixture.ClientId, Body());
        Assert.NotEqual(Guid.Empty, asset.Id);
        Assert.Equal(AssetStatus.Active, asset.Status);
        Assert.Equal(0m, asset.AccumulatedDepreciation);
        Assert.Equal("Van", asset.Description);
    }

    [Fact]
    public async Task Get_returns_a_created_asset()
    {
        Asset created = await Store().CreateAsync(fixture.ClientId, Body("Forklift"));
        Asset? got = await Store().GetAsync(fixture.ClientId, created.Id);
        Assert.NotNull(got);
        Assert.Equal("Forklift", got!.Description);
    }

    [Fact]
    public async Task Update_changes_editable_params_and_preserves_server_owned_fields()
    {
        Asset created = await Store().CreateAsync(fixture.ClientId, Body("Old"));
        Asset? updated = await Store().UpdateAsync(fixture.ClientId, created.Id, Body("New") with { UsefulLifeMonths = 36 });
        Assert.NotNull(updated);
        Assert.Equal("New", updated!.Description);
        Assert.Equal(36, updated.UsefulLifeMonths);
        Assert.Equal(AssetStatus.Active, updated.Status);
        Assert.Equal(0m, updated.AccumulatedDepreciation);
    }

    [Fact]
    public async Task Update_of_a_missing_asset_returns_null() =>
        Assert.Null(await Store().UpdateAsync(fixture.ClientId, Guid.NewGuid(), Body()));

    [Fact]
    public async Task Deactivate_removes_the_asset_from_the_default_list_but_include_inactive_shows_it()
    {
        Asset created = await Store().CreateAsync(fixture.ClientId, Body("Retire me"));

        DeactivateResult result = await Store().DeactivateAsync(fixture.ClientId, created.Id);
        Assert.Equal(DeactivateResult.Deactivated, result);

        PagedResponse<Asset> active = await Store().GetByClientPagedAsync(fixture.ClientId, 0, 200, true, includeInactive: false, default);
        Assert.DoesNotContain(active.Items, a => a.Id == created.Id);

        PagedResponse<Asset> all = await Store().GetByClientPagedAsync(fixture.ClientId, 0, 200, true, includeInactive: true, default);
        Assert.Contains(all.Items, a => a.Id == created.Id);
    }

    [Fact]
    public async Task Deactivate_is_not_found_then_conflict_on_repeat()
    {
        Assert.Equal(DeactivateResult.NotFound, await Store().DeactivateAsync(fixture.ClientId, Guid.NewGuid()));

        Asset created = await Store().CreateAsync(fixture.ClientId, Body("Once"));
        Assert.Equal(DeactivateResult.Deactivated, await Store().DeactivateAsync(fixture.ClientId, created.Id));
        Assert.Equal(DeactivateResult.AlreadyInactive, await Store().DeactivateAsync(fixture.ClientId, created.Id));
    }
}
```

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FullyQualifiedName~AssetDocumentStoreTests`
Expected: FAIL — `DocumentAssetStore`, `IAssetStore`, `DeactivateResult` do not exist (compile error).

- [ ] **Step 3: Create the port + result enum.** `FixedAssetsPorts.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>The module's asset register store — reference documents backed by the engine's document store.
/// Create/update/deactivate lifecycle; the module owns no database connection.</summary>
public interface IAssetStore
{
    Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default);
    Task<Asset?> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default);
    Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default);
    Task<PagedResponse<Asset>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default);
}

/// <summary>Outcome of a deactivate: the asset was not found, was already inactive, or was deactivated now.</summary>
public enum DeactivateResult
{
    NotFound,
    AlreadyInactive,
    Deactivated,
}
```

- [ ] **Step 4: Create the store.** `DocumentAssetStore.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

/// <summary>Persists assets through the engine's document store as reference data (mutable, audited,
/// deactivatable). Server-owned fields — Status and AccumulatedDepreciation — are stamped by the store:
/// Active/0 on create, preserved on update. The module speaks only IDocumentStore.</summary>
public sealed class DocumentAssetStore(IDocumentStore documents) : IAssetStore
{
    private const string Collection = "assets";
    private static readonly Dictionary<string, string> NoTags = new();

    public async Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = Guid.NewGuid();
        AssetDocument doc = ToDocument(body, AssetStatus.Active, 0m);
        await documents.PutAsync(clientId, Collection, id, doc, NoTags, ct);
        return Map(id, doc);
    }

    public async Task<Asset?> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return null;
        // Only the editable params change; Status + AccumulatedDepreciation are preserved (FA-2/FA-3 own them).
        AssetDocument doc = ToDocument(body, existing.Body.Status, existing.Body.AccumulatedDepreciation);
        await documents.PutAsync(clientId, Collection, assetId, doc, NoTags, ct);
        return Map(assetId, doc);
    }

    public async Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? existing = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        if (existing is null) return DeactivateResult.NotFound;
        if (existing.State == DocumentLifecycle.Inactive) return DeactivateResult.AlreadyInactive;
        await documents.DeactivateAsync(clientId, Collection, assetId, ct);
        return DeactivateResult.Deactivated;
    }

    public async Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default)
    {
        DocumentResult<AssetDocument>? result = await documents.GetAsync<AssetDocument>(clientId, Collection, assetId, ct);
        return result is null ? null : Map(result.Id, result.Body);
    }

    public async Task<PagedResponse<Asset>> GetByClientPagedAsync(
        Guid clientId, int skip, int limit, bool descending, bool includeInactive, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<AssetDocument>> page =
            await documents.QueryAsync<AssetDocument>(clientId, Collection, NoTags, skip, limit, descending, includeInactive, ct);
        long total = await documents.CountAsync(clientId, Collection, NoTags, includeInactive, ct);
        return new PagedResponse<Asset>(page.Select(r => Map(r.Id, r.Body)).ToList(), total, skip, limit);
    }

    private static AssetDocument ToDocument(AssetBody body, AssetStatus status, decimal accumulated) =>
        new(body.Description, body.AcquisitionCost, body.InServiceDate, body.UsefulLifeMonths,
            body.SalvageValue, body.Method, body.DecliningBalanceFactor, status, accumulated);

    private static Asset Map(Guid id, AssetDocument d) => new()
    {
        Id = id, Description = d.Description, AcquisitionCost = d.AcquisitionCost, InServiceDate = d.InServiceDate,
        UsefulLifeMonths = d.UsefulLifeMonths, SalvageValue = d.SalvageValue, Method = d.Method,
        DecliningBalanceFactor = d.DecliningBalanceFactor, Status = d.Status, AccumulatedDepreciation = d.AccumulatedDepreciation,
    };
}
```

- [ ] **Step 5: Run to verify pass.**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FullyQualifiedName~AssetDocumentStoreTests`
Expected: PASS (6/6). (`AssetValidationTests` still pass too.)

- [ ] **Step 6: Commit.**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsPorts.cs Modules/FixedAssets/Accounting101.FixedAssets/DocumentAssetStore.cs Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetDocumentStoreFixture.cs Modules/FixedAssets/Accounting101.FixedAssets.Tests/AssetDocumentStoreTests.cs
git commit -m "feat(fixedassets): reference-document asset store"
```

---

## Task 3: Service + HTTP surface + module wiring

**Files:**
- Create: `Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsService.cs`
- Create: `Modules/FixedAssets/Accounting101.FixedAssets.Api/Accounting101.FixedAssets.Api.csproj`
- Create: `.../FixedAssetsRequests.cs`, `.../FixedAssetsEndpoints.cs`, `.../FixedAssetsServiceExtensions.cs`
- Modify: `Accounting101.Host/Accounting101.Host.csproj`, `Accounting101.Host/Program.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs`
- Modify: `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Accounting101.FixedAssets.Tests.csproj` (add Api + Host refs)
- Create: `.../FixedAssetsHostFixture.cs`, `.../FixedAssetsEndpointsTests.cs`

**Interfaces:**
- Consumes: `IAssetStore`, `DeactivateResult`, `AssetValidation`, `Asset`, `AssetBody`, `AssetView` (Tasks 1–2); `AddModule`, `ModuleIdentity`, `IDocumentStore`, `ModuleAccessLevel`, `Capabilities.CapabilityForModule` (engine).
- Produces: `FixedAssetsService`; `SaveAssetRequest`; `MapFixedAssetsEndpoints`; `AddFixedAssets`.

- [ ] **Step 1: Create the service.** `FixedAssetsService.cs`:

```csharp
namespace Accounting101.FixedAssets;

/// <summary>The asset-register lifecycle: validate then create / update, deactivate, get. Validation
/// failures throw ArgumentException (→ 422 at the endpoint). No ledger dependency — FA-1 does not post.</summary>
public sealed class FixedAssetsService(IAssetStore store)
{
    public Task<Asset> CreateAsync(Guid clientId, AssetBody body, CancellationToken ct = default)
    {
        if (AssetValidation.Validate(body) is { } error) throw new ArgumentException(error);
        return store.CreateAsync(clientId, body, ct);
    }

    public Task<Asset?> UpdateAsync(Guid clientId, Guid assetId, AssetBody body, CancellationToken ct = default)
    {
        if (AssetValidation.Validate(body) is { } error) throw new ArgumentException(error);
        return store.UpdateAsync(clientId, assetId, body, ct);
    }

    public Task<DeactivateResult> DeactivateAsync(Guid clientId, Guid assetId, CancellationToken ct = default) =>
        store.DeactivateAsync(clientId, assetId, ct);

    public Task<Asset?> GetAsync(Guid clientId, Guid assetId, CancellationToken ct = default) =>
        store.GetAsync(clientId, assetId, ct);
}
```

- [ ] **Step 2: Create the Api csproj.** `Accounting101.FixedAssets.Api/Accounting101.FixedAssets.Api.csproj` (mirrors `Accounting101.Payroll.Api.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- The fixed-assets module's web tier: endpoints + DI wiring. FA-1 does not post, so there is no
       loopback ledger client here. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Microsoft.AspNetCore.Builder" />
    <Using Include="Microsoft.AspNetCore.Http" />
    <Using Include="Microsoft.AspNetCore.Routing" />
    <Using Include="Microsoft.Extensions.Configuration" />
    <Using Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Accounting101.FixedAssets\Accounting101.FixedAssets.csproj" />
    <ProjectReference Include="..\..\..\Backend\Accounting101.Ledger.Api\Accounting101.Ledger.Api.csproj" />
    <ProjectReference Include="..\..\..\Backend\Accounting101.Ledger.Contracts\Accounting101.Ledger.Contracts.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create the request DTO.** `FixedAssetsRequests.cs`:

```csharp
using Accounting101.FixedAssets;

namespace Accounting101.FixedAssets.Api;

/// <summary>Create or update an asset. Status and AccumulatedDepreciation are server-owned and never sent.</summary>
public sealed record SaveAssetRequest(
    string Description,
    decimal AcquisitionCost,
    DateOnly InServiceDate,
    int UsefulLifeMonths,
    decimal SalvageValue,
    DepreciationMethod Method,
    decimal? DecliningBalanceFactor)
{
    public AssetBody ToBody() =>
        new(Description, AcquisitionCost, InServiceDate, UsefulLifeMonths, SalvageValue, Method, DecliningBalanceFactor);
}
```

- [ ] **Step 4: Create the endpoints.** `FixedAssetsEndpoints.cs` (mirrors `PayrollEndpoints`):

```csharp
using Accounting101.FixedAssets;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Api;

/// <summary>The fixed-assets HTTP surface: the asset-register lifecycle under /clients/{clientId}.
/// Responses are AssetView; only the request body is a DTO.</summary>
public static class FixedAssetsEndpoints
{
    public static void MapFixedAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/assets", CreateAsset);
        clients.MapPut("/assets/{assetId:guid}", UpdateAsset);
        clients.MapPost("/assets/{assetId:guid}/deactivate", DeactivateAsset);
        clients.MapGet("/assets/{assetId:guid}", GetAsset);
        clients.MapGet("/assets", ListAssets);
    }

    private static async Task<IResult> CreateAsset(
        Guid clientId, SaveAssetRequest request, FixedAssetsService service, CancellationToken cancellationToken)
    {
        try
        {
            Asset asset = await service.CreateAsync(clientId, request.ToBody(), cancellationToken);
            return Results.Created($"/clients/{clientId}/assets/{asset.Id}", new AssetView(asset));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> UpdateAsset(
        Guid clientId, Guid assetId, SaveAssetRequest request, FixedAssetsService service, CancellationToken cancellationToken)
    {
        try
        {
            Asset? asset = await service.UpdateAsync(clientId, assetId, request.ToBody(), cancellationToken);
            return asset is null ? Results.NotFound() : Results.Ok(new AssetView(asset));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> DeactivateAsset(
        Guid clientId, Guid assetId, FixedAssetsService service, CancellationToken cancellationToken)
    {
        DeactivateResult result = await service.DeactivateAsync(clientId, assetId, cancellationToken);
        return result switch
        {
            DeactivateResult.Deactivated => Results.NoContent(),
            DeactivateResult.NotFound => Results.NotFound(),
            DeactivateResult.AlreadyInactive => Results.Problem(
                "Asset is already inactive.", statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem("Unexpected deactivate result.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> GetAsset(
        Guid clientId, Guid assetId, FixedAssetsService service, CancellationToken cancellationToken)
    {
        Asset? asset = await service.GetAsync(clientId, assetId, cancellationToken);
        return asset is null ? Results.NotFound() : Results.Ok(new AssetView(asset));
    }

    private static async Task<IResult> ListAssets(
        Guid clientId, int? skip, int? limit, string? order, bool? includeInactive,
        IAssetStore store, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

        PagedResponse<Asset> page = await store.GetByClientPagedAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeInactive ?? false, cancellationToken);

        return Results.Ok(new PagedResponse<AssetView>(
            page.Items.Select(a => new AssetView(a)).ToList(), page.Total, page.Skip, page.Limit));
    }

    private static bool TryOrder(string? order, out bool descending)
    {
        descending = true;
        if (string.IsNullOrEmpty(order)) return true;
        if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) { descending = true; return true; }
        if (string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)) { descending = false; return true; }
        return false;
    }
}
```

- [ ] **Step 5: Create the service-registration extension.** `FixedAssetsServiceExtensions.cs`:

```csharp
using Accounting101.FixedAssets;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Api;

/// <summary>Installs the fixed-assets module: module identity + a .Reference("assets") manifest, the
/// document-store-backed asset store, and the service. FA-1 does not post, so there is no ledger client.</summary>
public static class FixedAssetsServiceExtensions
{
    public static IServiceCollection AddFixedAssets(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("fixedassets"), "Fixed Assets", manifest =>
        {
            manifest.Reference("assets");
        });

        services.AddScoped<IAssetStore>(sp =>
            new DocumentAssetStore(sp.GetRequiredKeyedService<IDocumentStore>("fixedassets")));
        services.AddScoped<FixedAssetsService>();

        return services;
    }
}
```

- [ ] **Step 6: Wire the module into the host.** In `Accounting101.Host/Accounting101.Host.csproj`, add a `ProjectReference` beside the other module `.Api` references:

```xml
    <ProjectReference Include="..\Modules\FixedAssets\Accounting101.FixedAssets.Api\Accounting101.FixedAssets.Api.csproj" />
```

In `Accounting101.Host/Program.cs`, add the `using` (top, with the other module usings):

```csharp
using Accounting101.FixedAssets.Api;
```

Register the module (after `builder.Services.AddReconciliation(builder.Configuration);`):

```csharp
builder.Services.AddFixedAssets(builder.Configuration);
```

Map the endpoints (after `app.MapReconciliationEndpoints();`):

```csharp
app.MapFixedAssetsEndpoints();
```

- [ ] **Step 7: Wire capability enforcement.** In `Backend/Accounting101.Ledger.Api/Control/Capabilities.cs`, add the arm to `CapabilityForModule` (after the `"reconciliation"` arm, before `_ => null`):

```csharp
        "fixedassets"    => level == ModuleAccessLevel.Write ? FixedAssetsWrite : FixedAssetsRead,
```

(RolePresets already grants `FixedAssetsRead`/`FixedAssetsWrite` — no change there.)

- [ ] **Step 8: Add Api + Host references to the test project.** In `Modules/FixedAssets/Accounting101.FixedAssets.Tests/Accounting101.FixedAssets.Tests.csproj`, add to the `ProjectReference` group:

```xml
    <ProjectReference Include="..\Accounting101.FixedAssets.Api\Accounting101.FixedAssets.Api.csproj" />
    <ProjectReference Include="..\..\..\Accounting101.Host\Accounting101.Host.csproj" />
```

- [ ] **Step 9: Write the failing HTTP tests.** First the host fixture — `FixedAssetsHostFixture.cs` (mirrors `ReceivablesHostFixture` but without a ledger-loopback repoint, since FA-1 does not post):

```csharp
using System.Net.Http.Headers;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace Accounting101.FixedAssets.Tests;

/// <summary>Boots the real composition-root host (engine + all modules incl. fixed assets) against a
/// disposable EphemeralMongo. FA-1 does not post, so no loopback ledger client is repointed.</summary>
public sealed class FixedAssetsHostFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _connectionString = "";

    public IMongoClient Mongo { get; private set; } = null!;
    public string ControlDatabase { get; } = "control_" + Guid.NewGuid().ToString("N");
    public string PlatformDatabase { get; } = "platform_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        _connectionString = runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
    }

    async Task IAsyncLifetime.DisposeAsync() => await ((IAsyncDisposable)this).DisposeAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mongo:ConnectionString", _connectionString);
        builder.UseSetting("Mongo:ControlDatabase", ControlDatabase);
        builder.UseSetting("Mongo:PlatformDatabase", PlatformDatabase);
    }

    public ControlStore Control() => new(Mongo.GetDatabase(ControlDatabase));

    public HttpClient ClientFor(Guid userId, string name, LedgerRole role)
    {
        HttpClient http = CreateClient();
        string token = DevToken.Encode(new DevTokenPayload(userId, name, [new DevClaim("role", role.ToString())]));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(DevTokenDefaults.Scheme, token);
        return http;
    }

    /// <summary>Register a client (entitled to fixedassets by default) + a member with the given role,
    /// returning the client id and an HttpClient authed as that member.</summary>
    public async Task<(Guid ClientId, HttpClient Http)> SeedClientAsync(
        LedgerRole role = LedgerRole.Controller, IReadOnlyList<string>? enabledModules = null)
    {
        Guid clientId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        ControlStore control = Control();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Acme", DatabaseName = "client_" + clientId.ToString("N"),
            EnabledModules = enabledModules ?? ["fixedassets"],
        });
        await control.AddMembershipAsync(userId, clientId, role);
        return (clientId, ClientFor(userId, $"Acme {role}", role));
    }
}
```

Then the tests — `FixedAssetsEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.FixedAssets.Api;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Tests;

public sealed class FixedAssetsEndpointsTests(FixedAssetsHostFixture fixture) : IClassFixture<FixedAssetsHostFixture>
{
    private static SaveAssetRequest Van() => new(
        "Delivery van", 30000m, new DateOnly(2026, 1, 1), 60, 3000m, DepreciationMethod.StraightLine, null);

    [Fact]
    public async Task Create_list_get_update_deactivate_lifecycle()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage created = await http.PostAsJsonAsync($"/clients/{clientId}/assets", Van());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssetView view = (await created.Content.ReadFromJsonAsync<AssetView>())!;
        Guid assetId = view.Asset.Id;
        Assert.Equal(AssetStatus.Active, view.Asset.Status);
        Assert.Equal(27000m, view.NetBookValue); // 30000 − 0

        PagedResponse<AssetView> list = (await http.GetFromJsonAsync<PagedResponse<AssetView>>($"/clients/{clientId}/assets"))!;
        Assert.Contains(list.Items, a => a.Asset.Id == assetId);

        AssetView got = (await http.GetFromJsonAsync<AssetView>($"/clients/{clientId}/assets/{assetId}"))!;
        Assert.Equal("Delivery van", got.Asset.Description);

        HttpResponseMessage updated = await http.PutAsJsonAsync(
            $"/clients/{clientId}/assets/{assetId}", Van() with { Description = "Delivery van (renamed)", UsefulLifeMonths = 48 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Delivery van (renamed)", (await updated.Content.ReadFromJsonAsync<AssetView>())!.Asset.Description);

        HttpResponseMessage deactivated = await http.PostAsync($"/clients/{clientId}/assets/{assetId}/deactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        PagedResponse<AssetView> afterList = (await http.GetFromJsonAsync<PagedResponse<AssetView>>($"/clients/{clientId}/assets"))!;
        Assert.DoesNotContain(afterList.Items, a => a.Asset.Id == assetId);
        PagedResponse<AssetView> withInactive = (await http.GetFromJsonAsync<PagedResponse<AssetView>>($"/clients/{clientId}/assets?includeInactive=true"))!;
        Assert.Contains(withInactive.Items, a => a.Asset.Id == assetId);
    }

    [Fact]
    public async Task Invalid_asset_is_422()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        HttpResponseMessage response = await http.PostAsJsonAsync(
            $"/clients/{clientId}/assets", Van() with { AcquisitionCost = 0m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_missing_asset_is_404_and_repeat_is_409()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsync($"/clients/{clientId}/assets/{Guid.NewGuid()}/deactivate", null)).StatusCode);

        AssetView created = (await (await http.PostAsJsonAsync($"/clients/{clientId}/assets", Van())).Content.ReadFromJsonAsync<AssetView>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/clients/{clientId}/assets/{created.Asset.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/clients/{clientId}/assets/{created.Asset.Id}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task A_member_without_fixedassets_write_cannot_create_but_can_read()
    {
        // Auditor holds fixedassets.read but NOT fixedassets.write.
        (Guid clientId, HttpClient auditor) = await fixture.SeedClientAsync(role: LedgerRole.Auditor);

        HttpResponseMessage create = await auditor.PostAsJsonAsync($"/clients/{clientId}/assets", Van());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        HttpResponseMessage list = await auditor.GetAsync($"/clients/{clientId}/assets");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task A_client_not_entitled_to_fixedassets_is_403()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(enabledModules: []);
        HttpResponseMessage response = await http.PostAsJsonAsync($"/clients/{clientId}/assets", Van());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 10: Run to verify failure, then implement is already in place — run to verify pass.**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests --filter FullyQualifiedName~FixedAssetsEndpointsTests`
Expected: PASS (5/5). (Steps 1–8 provide the implementation; the tests are written last here only because they exercise the full wiring. If a test fails: a 404 on every asset route → `MapFixedAssetsEndpoints` not wired in `Program.cs`; a 403 where 201 is expected on the happy path → the client is not entitled or the `CapabilityForModule` arm is missing; a compile error → the test-csproj Api/Host refs from Step 8 are missing.)

- [ ] **Step 11: Run the whole FixedAssets suite.**

Run: `dotnet test Modules/FixedAssets/Accounting101.FixedAssets.Tests`
Expected: all PASS (validation + store + endpoints).

- [ ] **Step 12: Commit.**

```bash
git add Modules/FixedAssets/Accounting101.FixedAssets/FixedAssetsService.cs Modules/FixedAssets/Accounting101.FixedAssets.Api/ Modules/FixedAssets/Accounting101.FixedAssets.Tests/Accounting101.FixedAssets.Tests.csproj Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsHostFixture.cs Modules/FixedAssets/Accounting101.FixedAssets.Tests/FixedAssetsEndpointsTests.cs Accounting101.Host/Accounting101.Host.csproj Accounting101.Host/Program.cs Backend/Accounting101.Ledger.Api/Control/Capabilities.cs
git commit -m "feat(fixedassets): asset-register HTTP surface + module wiring"
```

---

## Task 4: Whole-solution green + review

- [ ] **Step 1: Build and test the whole solution.**

Run: `dotnet test Accounting101.slnx`
Expected: all PASS. The new `fixedassets` module installs in every host boot; existing suites stay green (the module is unentitled for their clients and its routes are additive). Expect the prior total plus the new FixedAssets suite (~22 tests).

- [ ] **Step 2: Whole-branch review.** Follow `superpowers:requesting-code-review`. A reviewer must confirm: (a) assets are a genuine `.Reference` collection (mutable/deactivatable, not evidentiary); (b) server-owned `Status`/`AccumulatedDepreciation` are preserved across update; (c) the `CapabilityForModule` arm makes `fixedassets.write`/`read` actually enforced at the chokepoint (403 for a read-only member, 403 `NotEntitled` for an unentitled client); (d) no GL posting / no ledger client leaked into FA-1; (e) the module installs without breaking existing suites.

- [ ] **Step 3: Update memory** — record FA-1 shipped (module scaffolding + asset register), the FA-2/FA-3 roadmap (pluggable `IDepreciationMethod` strategy + depreciation runs; disposals), and the note that `RolePresets` already carried the caps.

---

## Self-Review

**Spec coverage:**
- New `fixedassets` module in 3 projects → Tasks 1–3. ✓
- Asset reference-document entity + create/edit/deactivate/get/list → Tasks 2–3. ✓
- Multi-method-ready data model (enum + `DecliningBalanceFactor`), no computation → Task 1 (`DepreciationMethod`, `AssetDocument`), validated in `AssetValidation`. ✓
- Validation rules → Task 1 (`AssetValidation`) + 422 mapping Task 3. ✓
- `CapabilityForModule` arm; RolePresets untouched (already grants) → Task 3 Step 7. ✓
- Entitlement/secrets free from prior phases → exercised by fixtures (`EnabledModules = ["fixedassets"]`, `RegisterModuleAsync`) Tasks 2–3. ✓
- No GL posting / no ledger client → no `HttpLedgerClient` or posting-accounts provider in any task. ✓
- Testing (E2E lifecycle, validation 422, capability 403, entitlement NotEntitled 403, store units) → Tasks 1–3. ✓

**Placeholder scan:** Step 5/Step 10 note fallbacks for known failure modes (missing `.Api` solution entry; route/entitlement/ref failures) — these are concrete diagnostics, not omitted logic. No functional placeholder.

**Type consistency:** `AssetBody`/`AssetDocument`/`Asset`/`AssetView` field lists and `IAssetStore` signatures (`CreateAsync`, `UpdateAsync`, `DeactivateAsync → DeactivateResult`, `GetAsync`, `GetByClientPagedAsync`), `DepreciationMethod`/`AssetStatus` members, `AssetValidation.Validate → string?`, `SaveAssetRequest.ToBody()`, `MapFixedAssetsEndpoints`/`AddFixedAssets` — all used identically across tasks. ✓
