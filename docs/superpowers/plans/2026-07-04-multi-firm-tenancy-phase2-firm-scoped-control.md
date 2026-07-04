# Multi-Firm Tenancy — Phase 2: Firm-scoped control plane Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every request firm-scoped — a `firmId` claim (defaulting to a configured single firm) resolves the firm's control DB on the firm's cluster, so `ControlStore`/`AdminAuditStore` and the whole client-resolution chain operate within one firm, with cross-firm data access structurally impossible.

**Architecture:** A scoped `IFirmContext` reads the `firm` claim (or the configured `Tenancy:DefaultFirmId`). A `FirmResolutionMiddleware` (after auth) resolves that firm via `PlatformStore` + `IMongoClientFactory` and stashes the firm + its control DB into a scoped `FirmScope`. `ControlStore`, `AdminAuditStore`, `ClientDatabaseResolver`, `ClientLedgerFactory`, `ModuleAccess`, and `LedgerGateway` all become **scoped**, reading the firm's control DB from `FirmScope`; the once-per-process index cache moves to a singleton `IIndexGuard`. `ClientDatabaseResolver` now goes firm→cluster→client, so a `clientId` not in the caller's firm registry is simply not found. A startup `DefaultFirmSeeder` registers the configured single firm against today's control DB, so on-site and the existing test suite work unchanged.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs + middleware, MongoDB C# driver, xUnit, EphemeralMongo (`Accounting101.TestSupport.SharedMongo`).

## Global Constraints

- **Target framework:** .NET 10; namespaces follow folder structure.
- **Scoped-chain decision (approved):** `ControlStore`/`AdminAuditStore`/`ClientDatabaseResolver`/`ClientLedgerFactory`/`ModuleAccess`/`LedgerGateway` become **scoped**; the index cache moves to a **singleton** `IIndexGuard`. Endpoints already inject `ControlStore`/`AdminAuditStore`/`LedgerGateway` as handler parameters, so scoping is transparent to them — do NOT change endpoint signatures.
- **Default-firm decision (approved):** a request with no `firm` claim resolves to `Tenancy:DefaultFirmId` (config; falls back to the well-known `TenancyDefaults.DefaultFirmId`). A startup `DefaultFirmSeeder` registers that firm with `ControlDatabase = Mongo:ControlDatabase` and `ClusterKey = Mongo:ClusterKey`.
- **Isolation guarantee:** `ClientDatabaseResolver` resolves a client DB only through the caller firm's own control DB; a `clientId` from another firm returns null → 404. This must hold and be tested end-to-end.
- **Serialization / bootstrap:** any Mongo-persisted type triggers `LedgerMongoBootstrap.RegisterOnce()` before I/O (via a store's static ctor, as existing stores do). Persisted enums use `[BsonRepresentation(BsonType.String)]` (Phase 1 convention).
- **Green boundary:** Tasks 1–4 add new, independently-tested components WITHOUT rewiring — the suite stays green. **Task 5 is the atomic cutover** (rewire DI + pipeline + fixtures) — the suite is red mid-task and returns to green at its end. Task 6 adds the cross-firm enforcement proof.
- **Scope validation is ON in the test host** (WebApplicationFactory defaults to the Development environment → `ValidateScopes = true`), so an invalid scoped-from-singleton capture throws at host build. Every singleton/hosted service that injected a now-scoped service MUST be reworked in the cutover: `CapabilitySetSeeder` and `ModuleRegistrar` (both hosted, injected `ControlStore`) and the keyed `IDocumentStore` registration (its factory resolves the now-scoped `IClientDatabaseResolver` + `ModuleAccess`).
- **Commits:** stage **explicit file paths only** — never `git add -A`. Do NOT stage `UI/Angular/src/app/core/api/environment.ts` or IDE `.csproj`/`.slnx` churn. Every commit message ends with:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Backend runner:** `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`. The cutover (Task 5) also affects module suites — run the whole solution there: `dotnet test Accounting101.slnx`.

---

## File Structure

**New files (all under `Backend/Accounting101.Ledger.Api/`):**
- `Platform/FirmClaims.cs` — the `firm` claim-type constant.
- `Platform/TenancyDefaults.cs` — well-known default firm id + `ResolveDefaultFirmId(IConfiguration)`.
- `Platform/DefaultFirmSeeder.cs` — startup hosted service registering the configured single firm.
- `Platform/IFirmContext.cs` + `Platform/HttpContextFirmContext.cs` — scoped current-firm resolver.
- `Platform/FirmScope.cs` — scoped per-request holder (resolved firm + its control DB).
- `Platform/FirmResolutionMiddleware.cs` — resolves the firm into `FirmScope`; `UseFirmResolution` extension.
- `Platform/IIndexGuard.cs` + `Platform/IndexGuard.cs` — singleton once-per-process client index cache.
- `Tenancy/FirmScopedClientDatabaseResolver.cs` — firm→cluster→client resolver.

**Modified files:**
- `Hosting/LedgerEngineExtensions.cs` — the DI rewiring (Task 5).
- `Hosting/CapabilitySetSeeder.cs` — resolve the default firm's control DB instead of an injected `ControlStore` (Task 5).
- `Hosting/ModuleRegistrar.cs` — same rework: resolve the default firm's control DB instead of injected `ControlStore` (Task 5).
- `Hosting/ModuleHostingExtensions.cs` — the keyed `IDocumentStore` registration becomes `AddKeyedScoped` (Task 5).
- `Tenancy/ClientLedgerFactory.cs` — take `IIndexGuard` instead of a private dictionary (Task 5).
- `Accounting101.Host/Program.cs` — insert `app.UseFirmResolution()` after auth (Task 5).
- `Backend/Accounting101.Ledger.Api.Tests/ModuleHostingTests.cs` — update the direct `ModuleRegistrar` construction to the reworked constructor (Task 5).
- Host fixtures (Task 5) — GUID-isolate `Mongo:PlatformDatabase`:
  `Modules/Receivables/.../ReceivablesHostFixture.cs`, `Modules/Payables/.../PayablesHostFixture.cs`,
  `Modules/Payroll/.../PayrollHostFixture.cs`, `Modules/Banking/Cash/.../CashHostFixture.cs`,
  `Modules/Banking/Reconciliation/.../ReconciliationHostFixture.cs`,
  `Modules/Receivables/.../DocumentStoreFixture.cs`, `Modules/Payables/.../PayablesDocumentStoreFixture.cs`.
- `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs` — second-firm + firm-claim helpers (Task 6).

**New test files (under `Backend/Accounting101.Ledger.Api.Tests/`):**
- `DefaultFirmSeederTests.cs`, `FirmContextTests.cs`, `FirmResolutionMiddlewareTests.cs`,
  `FirmScopedClientDatabaseResolverTests.cs`, `CrossFirmIsolationTests.cs`.

---

## Task 1: Firm claim, tenancy defaults, and the default-firm seeder

Create the firm-claim constant, the default-firm-id resolution helper, and a startup hosted service that registers the configured single firm against today's control DB. Not wired into `AddLedgerEngine` yet (Task 5 registers it) — tested in isolation.

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Platform/FirmClaims.cs`
- Create: `Backend/Accounting101.Ledger.Api/Platform/TenancyDefaults.cs`
- Create: `Backend/Accounting101.Ledger.Api/Platform/DefaultFirmSeeder.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/DefaultFirmSeederTests.cs`

**Interfaces:**
- Consumes: `PlatformStore.GetFirmAsync`/`RegisterFirmAsync` (Phase 1), `FirmRegistration`, config keys `Mongo:ControlDatabase`/`Mongo:ClusterKey`/`Tenancy:DefaultFirmId`.
- Produces: `FirmClaims.FirmId` (`"firm"`); `TenancyDefaults.DefaultFirmId` (Guid) + `TenancyDefaults.ResolveDefaultFirmId(IConfiguration)`; `DefaultFirmSeeder : IHostedService`.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/DefaultFirmSeederTests.cs`:

```csharp
using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class DefaultFirmSeederTests
{
    private static IConfiguration Config(string controlDb, string platformDb) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:ControlDatabase"] = controlDb,
            ["Mongo:PlatformDatabase"] = platformDb,
            ["Mongo:ClusterKey"] = "default",
        }).Build();

    [Fact]
    public async Task Seeds_the_default_firm_pointing_at_the_configured_control_db()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string platformDb = "platform_" + Guid.NewGuid().ToString("N");
        string controlDb = "control_" + Guid.NewGuid().ToString("N");
        PlatformStore platform = new(new MongoClient(runner.ConnectionString).GetDatabase(platformDb));

        DefaultFirmSeeder seeder = new(platform, Config(controlDb, platformDb));
        await seeder.StartAsync(CancellationToken.None);

        FirmRegistration firm = (await platform.GetFirmAsync(TenancyDefaults.DefaultFirmId))!;
        Assert.Equal(controlDb, firm.ControlDatabase);
        Assert.Equal("default", firm.ClusterKey);
        Assert.Equal(FirmStatus.Active, firm.Status);
    }

    [Fact]
    public async Task Is_idempotent_and_does_not_clobber_an_existing_default_firm()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string platformDb = "platform_" + Guid.NewGuid().ToString("N");
        PlatformStore platform = new(new MongoClient(runner.ConnectionString).GetDatabase(platformDb));

        // A pre-existing default firm with a hand-set control DB the seeder must not overwrite.
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = TenancyDefaults.DefaultFirmId, Name = "Default Firm",
            ControlDatabase = "hand_set_control", ClusterKey = "default",
        });

        await new DefaultFirmSeeder(platform, Config("some_other_control", platformDb))
            .StartAsync(CancellationToken.None);

        FirmRegistration firm = (await platform.GetFirmAsync(TenancyDefaults.DefaultFirmId))!;
        Assert.Equal("hand_set_control", firm.ControlDatabase);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter DefaultFirmSeederTests`
Expected: FAIL — compile error, the `Platform` types do not exist.

- [ ] **Step 3: Create `FirmClaims` and `TenancyDefaults`**

Create `Backend/Accounting101.Ledger.Api/Platform/FirmClaims.cs`:

```csharp
namespace Accounting101.Ledger.Api.Platform;

/// <summary>Claim types carried by an authenticated principal for tenancy.</summary>
public static class FirmClaims
{
    /// <summary>The firm the request acts within (a GUID). Absent on legacy/single-firm tokens, which
    /// fall back to the configured default firm.</summary>
    public const string FirmId = "firm";
}
```

Create `Backend/Accounting101.Ledger.Api/Platform/TenancyDefaults.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Single-firm defaults. On-site and any request without a <see cref="FirmClaims.FirmId"/> claim resolve
/// to <see cref="DefaultFirmId"/> (overridable via <c>Tenancy:DefaultFirmId</c>). The constant is a stable
/// well-known id so a deployment's single firm has the same handle across restarts.
/// </summary>
public static class TenancyDefaults
{
    public static readonly Guid DefaultFirmId = new("f1f10000-0000-0000-0000-000000000001");

    public static Guid ResolveDefaultFirmId(IConfiguration configuration) =>
        Guid.TryParse(configuration["Tenancy:DefaultFirmId"], out Guid id) ? id : DefaultFirmId;
}
```

- [ ] **Step 4: Create `DefaultFirmSeeder`**

Create `Backend/Accounting101.Ledger.Api/Platform/DefaultFirmSeeder.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Registers the deployment's single (default) firm on startup, pointing at the configured control DB and
/// home cluster, idempotently. This is what makes today's one-control-DB deployment "the default firm's
/// control DB", so requests with no firm claim resolve to it. Leaves an existing default firm untouched
/// (an operator may have re-pointed it) and tolerates the concurrent-cold-start duplicate-key race.
/// </summary>
public sealed class DefaultFirmSeeder(PlatformStore platform, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Guid firmId = TenancyDefaults.ResolveDefaultFirmId(configuration);
        if (await platform.GetFirmAsync(firmId, cancellationToken) is not null)
            return;

        string controlDatabase = configuration["Mongo:ControlDatabase"] ?? "ledger_control";
        string clusterKey = configuration["Mongo:ClusterKey"] ?? "default";
        try
        {
            await platform.RegisterFirmAsync(new FirmRegistration
            {
                Id = firmId,
                Name = "Default Firm",
                ControlDatabase = controlDatabase,
                ClusterKey = clusterKey,
            }, cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another instance seeded the default firm concurrently on a fresh platform_control — success.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter DefaultFirmSeederTests`
Expected: PASS (both tests).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Platform/FirmClaims.cs \
        Backend/Accounting101.Ledger.Api/Platform/TenancyDefaults.cs \
        Backend/Accounting101.Ledger.Api/Platform/DefaultFirmSeeder.cs \
        Backend/Accounting101.Ledger.Api.Tests/DefaultFirmSeederTests.cs
git commit -m "feat(tenancy): firm claim, tenancy defaults, default-firm seeder

Adds the 'firm' claim constant, DefaultFirmId resolution, and a startup
seeder that registers the configured single firm against today's control DB.
Not yet wired into AddLedgerEngine.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Scoped current-firm context

The scoped resolver that turns the request's `firm` claim (or the configured default) into a firm id. Mirrors `HttpContextCurrentActor`. Not registered yet.

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Platform/IFirmContext.cs`
- Create: `Backend/Accounting101.Ledger.Api/Platform/HttpContextFirmContext.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/FirmContextTests.cs`

**Interfaces:**
- Consumes: `IHttpContextAccessor`, `IConfiguration`, `FirmClaims.FirmId`, `TenancyDefaults.ResolveDefaultFirmId`.
- Produces: `interface IFirmContext { Guid FirmId { get; } }`; `HttpContextFirmContext(IHttpContextAccessor, IConfiguration)`.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/FirmContextTests.cs`:

```csharp
using System.Security.Claims;
using Accounting101.Ledger.Api.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Ledger.Api.Tests;

public sealed class FirmContextTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IHttpContextAccessor AccessorWith(params Claim[] claims)
    {
        DefaultHttpContext ctx = new() { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    [Fact]
    public void Uses_the_firm_claim_when_present()
    {
        Guid firmId = Guid.NewGuid();
        HttpContextFirmContext ctx = new(AccessorWith(new Claim(FirmClaims.FirmId, firmId.ToString())), EmptyConfig());
        Assert.Equal(firmId, ctx.FirmId);
    }

    [Fact]
    public void Falls_back_to_the_well_known_default_when_no_claim()
    {
        HttpContextFirmContext ctx = new(AccessorWith(), EmptyConfig());
        Assert.Equal(TenancyDefaults.DefaultFirmId, ctx.FirmId);
    }

    [Fact]
    public void Falls_back_to_the_configured_default_when_no_claim()
    {
        Guid configured = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tenancy:DefaultFirmId"] = configured.ToString(),
        }).Build();
        HttpContextFirmContext ctx = new(AccessorWith(), config);
        Assert.Equal(configured, ctx.FirmId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FirmContextTests`
Expected: FAIL — `IFirmContext`/`HttpContextFirmContext` do not exist.

- [ ] **Step 3: Create the interface**

Create `Backend/Accounting101.Ledger.Api/Platform/IFirmContext.cs`:

```csharp
namespace Accounting101.Ledger.Api.Platform;

/// <summary>Resolves the firm the current request acts within, from the authenticated principal's
/// <see cref="FirmClaims.FirmId"/> claim, or the configured default when the claim is absent.</summary>
public interface IFirmContext
{
    Guid FirmId { get; }
}
```

- [ ] **Step 4: Create the implementation**

Create `Backend/Accounting101.Ledger.Api/Platform/HttpContextFirmContext.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Reads the <see cref="FirmClaims.FirmId"/> claim off the current request's principal; when it is absent
/// or unparsable (a single-firm/on-site token), falls back to the configured default firm. IdP-agnostic —
/// a production JWT issuer just needs to emit the "firm" claim.
/// </summary>
public sealed class HttpContextFirmContext(IHttpContextAccessor accessor, IConfiguration configuration) : IFirmContext
{
    public Guid FirmId
    {
        get
        {
            string? claim = accessor.HttpContext?.User.FindFirstValue(FirmClaims.FirmId);
            return Guid.TryParse(claim, out Guid id) ? id : TenancyDefaults.ResolveDefaultFirmId(configuration);
        }
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FirmContextTests`
Expected: PASS (all three).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Platform/IFirmContext.cs \
        Backend/Accounting101.Ledger.Api/Platform/HttpContextFirmContext.cs \
        Backend/Accounting101.Ledger.Api.Tests/FirmContextTests.cs
git commit -m "feat(tenancy): scoped IFirmContext from the firm claim (default fallback)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Firm scope holder + resolution middleware

The per-request holder (resolved firm + its control DB) and the middleware that populates it: resolve the firm via `PlatformStore` + `IMongoClientFactory`, or 403 for an unknown/suspended firm. Not added to the pipeline yet (Task 5).

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Platform/FirmScope.cs`
- Create: `Backend/Accounting101.Ledger.Api/Platform/FirmResolutionMiddleware.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/FirmResolutionMiddlewareTests.cs`

**Interfaces:**
- Consumes: `IFirmContext` (Task 2), `PlatformStore` + `IMongoClientFactory` + `FirmRegistration` + `FirmStatus` (Phase 1).
- Produces:
  - `FirmScope` — scoped mutable holder: `FirmRegistration? Firm`, `IMongoDatabase? ControlDatabase`, `FirmRegistration RequireFirm()`, `IMongoDatabase RequireControlDatabase()`.
  - `FirmResolutionMiddleware(RequestDelegate next)` with `InvokeAsync(HttpContext, IFirmContext, PlatformStore, IMongoClientFactory, FirmScope)`.
  - `FirmResolutionMiddlewareExtensions.UseFirmResolution(IApplicationBuilder)`.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/FirmResolutionMiddlewareTests.cs`:

```csharp
using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Http;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class FirmResolutionMiddlewareTests
{
    private sealed class FixedFirm(Guid firmId) : IFirmContext { public Guid FirmId => firmId; }

    private static async Task<(PlatformStore Platform, IMongoClientFactory Factory, IMongoClient Home)> BackendAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient home = new MongoClient(runner.ConnectionString);
        PlatformStore platform = new(home.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
        await platform.RegisterClusterAsync(new ClusterRegistration { Key = "default", ConnectionString = runner.ConnectionString });
        return (platform, new MongoClientFactory(home, "default", platform), home);
    }

    [Fact]
    public async Task Populates_firm_scope_for_an_active_firm()
    {
        (PlatformStore platform, IMongoClientFactory factory, _) = await BackendAsync();
        Guid firmId = Guid.NewGuid();
        string controlDb = "firm_" + firmId.ToString("N") + "_control";
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = firmId, Name = "Firm A", ControlDatabase = controlDb, ClusterKey = "default",
        });

        FirmScope scope = new();
        bool nextRan = false;
        FirmResolutionMiddleware middleware = new(_ => { nextRan = true; return Task.CompletedTask; });
        DefaultHttpContext ctx = new();

        await middleware.InvokeAsync(ctx, new FixedFirm(firmId), platform, factory, scope);

        Assert.True(nextRan);
        Assert.Equal(firmId, scope.RequireFirm().Id);
        Assert.Equal(controlDb, scope.RequireControlDatabase().DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public async Task Rejects_an_unknown_firm_with_403_and_does_not_call_next()
    {
        (PlatformStore platform, IMongoClientFactory factory, _) = await BackendAsync();
        FirmScope scope = new();
        bool nextRan = false;
        FirmResolutionMiddleware middleware = new(_ => { nextRan = true; return Task.CompletedTask; });
        DefaultHttpContext ctx = new();
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx, new FixedFirm(Guid.NewGuid()), platform, factory, scope);

        Assert.False(nextRan);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Null(scope.Firm);
    }

    [Fact]
    public async Task Rejects_a_suspended_firm_with_403()
    {
        (PlatformStore platform, IMongoClientFactory factory, _) = await BackendAsync();
        Guid firmId = Guid.NewGuid();
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = firmId, Name = "Suspended", ControlDatabase = "x_control", ClusterKey = "default",
            Status = FirmStatus.Suspended,
        });

        FirmScope scope = new();
        bool nextRan = false;
        FirmResolutionMiddleware middleware = new(_ => { nextRan = true; return Task.CompletedTask; });
        DefaultHttpContext ctx = new();
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx, new FixedFirm(firmId), platform, factory, scope);

        Assert.False(nextRan);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FirmResolutionMiddlewareTests`
Expected: FAIL — `FirmScope`/`FirmResolutionMiddleware` do not exist.

- [ ] **Step 3: Create `FirmScope`**

Create `Backend/Accounting101.Ledger.Api/Platform/FirmScope.cs`:

```csharp
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Per-request holder for the resolved firm and its control database, populated by
/// <see cref="FirmResolutionMiddleware"/> and read by the scoped control-plane services. The Require*
/// accessors fail loudly if a service resolves it before the middleware ran (a wiring bug, not a runtime
/// input error).
/// </summary>
public sealed class FirmScope
{
    public FirmRegistration? Firm { get; set; }
    public IMongoDatabase? ControlDatabase { get; set; }

    public FirmRegistration RequireFirm() =>
        Firm ?? throw new InvalidOperationException(
            "Firm not resolved for this request; FirmResolutionMiddleware did not run.");

    public IMongoDatabase RequireControlDatabase() =>
        ControlDatabase ?? throw new InvalidOperationException(
            "Firm control database not resolved for this request; FirmResolutionMiddleware did not run.");
}
```

- [ ] **Step 4: Create the middleware + extension**

Create `Backend/Accounting101.Ledger.Api/Platform/FirmResolutionMiddleware.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Resolves the current request's firm (from <see cref="IFirmContext"/>) into the scoped
/// <see cref="FirmScope"/>: looks the firm up in the platform registry, gets the client for the firm's
/// cluster, and records the firm + its control database. An unknown or suspended firm is refused with 403
/// before any endpoint runs. Runs after authentication so the firm claim is available.
/// </summary>
public sealed class FirmResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, IFirmContext firmContext, PlatformStore platform,
        IMongoClientFactory factory, FirmScope scope)
    {
        Guid firmId = firmContext.FirmId;
        FirmRegistration? firm = await platform.GetFirmAsync(firmId, context.RequestAborted);
        if (firm is null || firm.Status != FirmStatus.Active)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = "Unknown or suspended firm.",
            }, context.RequestAborted);
            return;
        }

        IMongoClient client = await factory.GetAsync(firm.ClusterKey, context.RequestAborted);
        scope.Firm = firm;
        scope.ControlDatabase = client.GetDatabase(firm.ControlDatabase);
        await next(context);
    }
}

/// <summary>Pipeline registration for <see cref="FirmResolutionMiddleware"/>.</summary>
public static class FirmResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseFirmResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<FirmResolutionMiddleware>();
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FirmResolutionMiddlewareTests`
Expected: PASS (all three).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Platform/FirmScope.cs \
        Backend/Accounting101.Ledger.Api/Platform/FirmResolutionMiddleware.cs \
        Backend/Accounting101.Ledger.Api.Tests/FirmResolutionMiddlewareTests.cs
git commit -m "feat(tenancy): FirmScope holder + firm-resolution middleware (403 on unknown/suspended)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Index guard + firm-scoped client database resolver

Extract the once-per-process index cache into a singleton `IIndexGuard`, and add a firm→cluster→client resolver that reaches a client DB only through the caller firm's control DB (the isolation boundary). Both new; wiring is Task 5.

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Platform/IIndexGuard.cs`
- Create: `Backend/Accounting101.Ledger.Api/Platform/IndexGuard.cs`
- Create: `Backend/Accounting101.Ledger.Api/Tenancy/FirmScopedClientDatabaseResolver.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/FirmScopedClientDatabaseResolverTests.cs`

**Interfaces:**
- Consumes: `FirmScope` (Task 3), `ControlStore.GetClientAsync` + `ClientRegistration.DatabaseName` (existing), `IMongoClientFactory` (Phase 1), `IClientDatabaseResolver` (existing interface).
- Produces:
  - `interface IIndexGuard { bool TryClaim(Guid clientId); void Release(Guid clientId); }`; `IndexGuard` (singleton, `ConcurrentDictionary`-backed).
  - `FirmScopedClientDatabaseResolver(FirmScope scope, ControlStore control, IMongoClientFactory factory) : IClientDatabaseResolver` — `ResolveAsync(clientId)` returns the client DB on the firm's cluster, or null if the client is not in the firm's registry.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/FirmScopedClientDatabaseResolverTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class FirmScopedClientDatabaseResolverTests
{
    private static async Task<(FirmScope Scope, ControlStore Control, IMongoClientFactory Factory)> FirmAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient home = new MongoClient(runner.ConnectionString);
        PlatformStore platform = new(home.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
        await platform.RegisterClusterAsync(new ClusterRegistration { Key = "default", ConnectionString = runner.ConnectionString });
        MongoClientFactory factory = new(home, "default", platform);

        Guid firmId = Guid.NewGuid();
        string controlDb = "firm_" + firmId.ToString("N") + "_control";
        FirmRegistration firm = new() { Id = firmId, Name = "F", ControlDatabase = controlDb, ClusterKey = "default" };
        FirmScope scope = new() { Firm = firm, ControlDatabase = home.GetDatabase(controlDb) };
        ControlStore control = new(home.GetDatabase(controlDb));
        return (scope, control, factory);
    }

    [Fact]
    public async Task Resolves_a_client_registered_in_this_firm()
    {
        (FirmScope scope, ControlStore control, IMongoClientFactory factory) = await FirmAsync();
        Guid clientId = Guid.NewGuid();
        string clientDb = "firm_client_" + clientId.ToString("N");
        await control.RegisterClientAsync(new ClientRegistration { Id = clientId, Name = "C", DatabaseName = clientDb });

        FirmScopedClientDatabaseResolver resolver = new(scope, control, factory);
        IMongoDatabase? db = await resolver.ResolveAsync(clientId);

        Assert.NotNull(db);
        Assert.Equal(clientDb, db!.DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public async Task Refuses_a_client_not_registered_in_this_firm()
    {
        (FirmScope scope, ControlStore control, IMongoClientFactory factory) = await FirmAsync();

        FirmScopedClientDatabaseResolver resolver = new(scope, control, factory);
        // A clientId that belongs to no client in this firm's registry (e.g. another firm's client).
        Assert.Null(await resolver.ResolveAsync(Guid.NewGuid()));
    }

    [Fact]
    public void Index_guard_claims_once_per_client()
    {
        IndexGuard guard = new();
        Guid clientId = Guid.NewGuid();
        Assert.True(guard.TryClaim(clientId));
        Assert.False(guard.TryClaim(clientId));
        guard.Release(clientId);
        Assert.True(guard.TryClaim(clientId));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FirmScopedClientDatabaseResolverTests`
Expected: FAIL — the new types do not exist.

- [ ] **Step 3: Create `IIndexGuard` + `IndexGuard`**

Create `Backend/Accounting101.Ledger.Api/Platform/IIndexGuard.cs`:

```csharp
namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Process-wide "have this client's indexes been ensured yet?" latch. Extracted from
/// <c>ClientLedgerFactory</c> so it survives when the factory becomes request-scoped — the once-per-process
/// guarantee must outlive a single request.
/// </summary>
public interface IIndexGuard
{
    /// <summary>Claims the client for indexing; true exactly once until <see cref="Release"/>.</summary>
    bool TryClaim(Guid clientId);

    /// <summary>Re-arms the client (call after a failed index attempt so a later request retries).</summary>
    void Release(Guid clientId);
}
```

Create `Backend/Accounting101.Ledger.Api/Platform/IndexGuard.cs`:

```csharp
using System.Collections.Concurrent;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>Singleton <see cref="IIndexGuard"/> backed by a concurrent set of already-indexed client ids.</summary>
public sealed class IndexGuard : IIndexGuard
{
    private readonly ConcurrentDictionary<Guid, bool> _indexed = new();

    public bool TryClaim(Guid clientId) => _indexed.TryAdd(clientId, true);

    public void Release(Guid clientId) => _indexed.TryRemove(clientId, out _);
}
```

- [ ] **Step 4: Create `FirmScopedClientDatabaseResolver`**

Create `Backend/Accounting101.Ledger.Api/Tenancy/FirmScopedClientDatabaseResolver.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tenancy;

/// <summary>
/// Resolves a client's ledger database within the current request's firm: the client must be registered
/// in the firm's own control DB (the scoped <see cref="ControlStore"/>), and its ledger lives on the firm's
/// cluster. A clientId not in this firm's registry returns null — which is the structural isolation
/// boundary: one firm can never name another firm's client, because it has no registry entry to resolve
/// it through.
/// </summary>
public sealed class FirmScopedClientDatabaseResolver(
    FirmScope scope, ControlStore control, IMongoClientFactory factory) : IClientDatabaseResolver
{
    public async Task<IMongoDatabase?> ResolveAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        ClientRegistration? registration = await control.GetClientAsync(clientId, cancellationToken);
        if (registration is null)
            return null;

        IMongoClient client = await factory.GetAsync(scope.RequireFirm().ClusterKey, cancellationToken);
        return client.GetDatabase(registration.DatabaseName);
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter FirmScopedClientDatabaseResolverTests`
Expected: PASS (all three).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Platform/IIndexGuard.cs \
        Backend/Accounting101.Ledger.Api/Platform/IndexGuard.cs \
        Backend/Accounting101.Ledger.Api/Tenancy/FirmScopedClientDatabaseResolver.cs \
        Backend/Accounting101.Ledger.Api.Tests/FirmScopedClientDatabaseResolverTests.cs
git commit -m "feat(tenancy): singleton IndexGuard + firm-scoped client DB resolver

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Cutover — scope the control plane to the firm

**This is the atomic cutover.** The build/suite is red partway through and returns to green at the end. Rewire DI so the control-plane services are firm-scoped, insert the middleware, rework the two seeders, extract the index cache, and isolate the platform DB in every host fixture. No endpoint signatures change.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Tenancy/ClientLedgerFactory.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`
- Modify: `Accounting101.Host/Program.cs`
- Modify (uniform edit): the seven host fixtures listed in **File Structure** above.

**Interfaces:**
- Consumes: everything from Tasks 1–4 plus Phase 1 (`PlatformStore`, `IMongoClientFactory`).
- Produces: firm-scoped `ControlStore`/`AdminAuditStore`/`ClientDatabaseResolver`/`ClientLedgerFactory`/`ModuleAccess`/`LedgerGateway`; `app.UseFirmResolution()` in the pipeline; `CapabilitySetSeeder` targeting the default firm's control DB; `DefaultFirmSeeder` registered before it.

- [ ] **Step 1: Extract the index cache in `ClientLedgerFactory`**

In `Backend/Accounting101.Ledger.Api/Tenancy/ClientLedgerFactory.cs`, replace the private dictionary with the injected `IIndexGuard`. Change the constructor from `ClientLedgerFactory(IClientDatabaseResolver resolver)` to also take the guard, delete the `private readonly ConcurrentDictionary<Guid, bool> _indexed = new();` field, and update the index-ensuring block:

```csharp
using Accounting101.Ledger.Api.Platform;
// ... existing usings ...

public sealed class ClientLedgerFactory(IClientDatabaseResolver resolver, IIndexGuard indexGuard)
{
    public async Task<ClientLedger?> CreateAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        IMongoDatabase? database = await resolver.ResolveAsync(clientId, cancellationToken);
        if (database is null)
            return null;

        MongoJournalStore journal = new(database);
        MongoBalanceProjection projection = new(database, journal);
        MongoCheckpointStore checkpoints = new(database);
        MongoAuditLog audit = new(database);
        MongoAccountStore accounts = new(database);
        MongoSequenceStore sequences = new(database);
        LedgerService service = new(database.Client, journal, projection, checkpoints, audit, sequences);
        ChartService chart = new(database.Client, accounts, audit);
        FinancialStatementService statements = new(journal, accounts);

        // Ensure indexes once per client per process — claim via the singleton guard so the guarantee
        // survives this factory being request-scoped. Release on failure so a later request retries.
        if (indexGuard.TryClaim(clientId))
        {
            try
            {
                await journal.EnsureIndexesAsync(cancellationToken);
                await audit.EnsureIndexesAsync(cancellationToken);
            }
            catch
            {
                indexGuard.Release(clientId);
                throw;
            }
        }

        return new ClientLedger(service, journal, audit, projection, checkpoints, accounts, chart, statements);
    }
}
```

Remove the now-unused `using System.Collections.Concurrent;` if the compiler flags it.

- [ ] **Step 2: Rework `CapabilitySetSeeder` to target the default firm's control DB**

`CapabilitySetSeeder` is a startup hosted service; it can no longer inject the now-scoped `ControlStore`. Replace its body in `Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>Seeds the built-in capability sets and backfills legacy role grants into the DEFAULT firm's
/// control DB on startup (idempotent). Runs after <see cref="Platform.DefaultFirmSeeder"/>, which
/// guarantees the default firm exists.</summary>
public sealed class CapabilitySetSeeder(
    PlatformStore platform, IMongoClientFactory factory, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Guid firmId = TenancyDefaults.ResolveDefaultFirmId(configuration);
        FirmRegistration? firm = await platform.GetFirmAsync(firmId, cancellationToken);
        if (firm is null)
            return; // DefaultFirmSeeder runs first; nothing to seed if the default firm is absent.

        IMongoClient client = await factory.GetAsync(firm.ClusterKey, cancellationToken);
        ControlStore control = new(client.GetDatabase(firm.ControlDatabase));
        await control.SeedBuiltinCapabilitySetsAsync(cancellationToken);
        await control.BackfillGrantedSetIdsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 2b: Rework `ModuleRegistrar` to target the default firm's control DB**

`ModuleRegistrar` is a startup hosted service that upserts module registrations; like `CapabilitySetSeeder` it can no longer inject the scoped `ControlStore`. Replace `Backend/Accounting101.Ledger.Api/Hosting/ModuleRegistrar.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// On startup, upserts the control-DB registration for each installed module into the DEFAULT firm's
/// control DB. Idempotent. (Per-firm module registration is a Phase 3 provisioning concern; today every
/// deployment has a single default firm.) Runs after <see cref="Platform.DefaultFirmSeeder"/>.
/// </summary>
public sealed class ModuleRegistrar(
    IEnumerable<ModuleRegistration> modules, PlatformStore platform,
    IMongoClientFactory factory, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Guid firmId = TenancyDefaults.ResolveDefaultFirmId(configuration);
        FirmRegistration? firm = await platform.GetFirmAsync(firmId, cancellationToken);
        if (firm is null)
            return;

        IMongoClient client = await factory.GetAsync(firm.ClusterKey, cancellationToken);
        ControlStore control = new(client.GetDatabase(firm.ControlDatabase));
        foreach (ModuleRegistration module in modules)
            await control.RegisterModuleAsync(module, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`ModuleRegistrar` is registered via `TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ModuleRegistrar>())` in `ModuleHostingExtensions.AddModule`, which runs (per module) after `AddLedgerEngine`, so it starts after `DefaultFirmSeeder` — the default firm exists by then. No registration change needed here.

- [ ] **Step 2c: Make the keyed document store scoped**

In `Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs`, the keyed `IDocumentStore` factory resolves `IClientDatabaseResolver` and `ModuleAccess`, both now scoped, so the registration can no longer be a singleton. Change `AddKeyedSingleton` to `AddKeyedScoped` (the factory body is unchanged):

```csharp
        services.AddKeyedScoped<IDocumentStore>(identity.Key, (sp, _) => new ScopedDocumentStore(
            identity,
            manifest,
            sp.GetRequiredService<IClientDatabaseResolver>(),
            sp.GetRequiredService<ICurrentActor>(),
            sp.GetRequiredService<ModuleAccess>()));
```

Note: `ScopedDocumentStore` keeps its internal `_indexed` collection-index cache, which is now per-request rather than per-process — the ensure-indexes calls it guards are idempotent, so this is correctness-neutral; folding that cache into a shared guard is a deferred optimization (out of scope for Phase 2).

- [ ] **Step 3: Rewire `AddLedgerEngine`**

In `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`, change the control-plane registrations. Add `using Accounting101.Ledger.Api.Platform;`. Replace the two singleton store registrations, the resolver/factory/gateway/moduleaccess registrations, and the seeder line as follows.

Replace:
```csharp
        services.AddSingleton(sp =>
            new ControlStore(sp.GetRequiredService<IMongoClient>().GetDatabase(controlDatabase)));
        services.AddSingleton(sp =>
            new AdminAuditStore(sp.GetRequiredService<IMongoClient>().GetDatabase(controlDatabase)));
```
with:
```csharp
        // Firm-scoped control plane: the firm for this request is resolved by FirmResolutionMiddleware into
        // FirmScope; the control stores read that firm's control DB. Scoped, one per request.
        services.AddScoped<FirmScope>();
        services.AddScoped<IFirmContext, HttpContextFirmContext>();
        services.AddScoped(sp => new ControlStore(sp.GetRequiredService<FirmScope>().RequireControlDatabase()));
        services.AddScoped(sp => new AdminAuditStore(sp.GetRequiredService<FirmScope>().RequireControlDatabase()));
```

Replace the seeder line:
```csharp
        services.AddHostedService<CapabilitySetSeeder>();
```
with (order matters — the default firm must exist before capability sets seed into it):
```csharp
        services.AddHostedService<DefaultFirmSeeder>();
        services.AddHostedService<CapabilitySetSeeder>();
```

Replace:
```csharp
        services.AddSingleton<IClientDatabaseResolver, ClientDatabaseResolver>();
        services.AddSingleton<ClientLedgerFactory>();
        services.AddSingleton<LedgerGateway>();
```
with:
```csharp
        services.AddScoped<IClientDatabaseResolver, FirmScopedClientDatabaseResolver>();
        services.AddScoped<ClientLedgerFactory>();
        services.AddSingleton<IIndexGuard, IndexGuard>();
        services.AddScoped<LedgerGateway>();
```

Replace:
```csharp
        services.AddSingleton<ModuleAccess>();
```
with:
```csharp
        services.AddScoped<ModuleAccess>();
```

(`AddPlatformRegistry`, `AddHttpContextAccessor`, `ICurrentActor`, `IActorFactory`, `IModuleAuthenticator`, and the auth/authorization registrations are unchanged. The old `ClientDatabaseResolver` class remains in the tree but is no longer registered; leave it — Phase 3/cleanup can remove it.)

- [ ] **Step 4: Insert the middleware in `Program.cs`**

In `Accounting101.Host/Program.cs`, add `using Accounting101.Ledger.Api.Platform;` at the top, and insert the firm-resolution middleware immediately after `app.UseAuthorization();`:

```csharp
app.UseAuthentication();
app.UseAuthorization();

// Resolve the request's firm (from the firm claim or the configured default) into FirmScope before any
// endpoint runs; unknown/suspended firms are refused here with 403.
app.UseFirmResolution();
```

- [ ] **Step 5: GUID-isolate `Mongo:PlatformDatabase` in every host fixture**

Each module host fixture boots `Program` and now runs `DefaultFirmSeeder`, which writes the default firm into `platform_control`. Without per-fixture isolation, parallel fixtures share the literal `platform_control` and clobber each other's default-firm→control-DB mapping. Apply the SAME edit `ApiFixture` already has to each of these seven fixtures:

For the five `WebApplicationFactory<Program>` module fixtures (`ReceivablesHostFixture.cs`, `PayablesHostFixture.cs`, `PayrollHostFixture.cs`, `CashHostFixture.cs`, `ReconciliationHostFixture.cs`) and the two document-store fixtures (`Modules/Receivables/.../DocumentStoreFixture.cs`, `Modules/Payables/.../PayablesDocumentStoreFixture.cs`):

1. Add a property next to the existing `ControlDatabase` property:
```csharp
    public string PlatformDatabase { get; } = "platform_" + Guid.NewGuid().ToString("N");
```
2. In the host-building method (`ConfigureWebHost` for the module fixtures, or the `WithWebHostBuilder`/`UseSetting` chain for `WebApplicationFactory`-derived ones), add right after the `Mongo:ControlDatabase` setting:
```csharp
        builder.UseSetting("Mongo:PlatformDatabase", PlatformDatabase);
```
(Use the same `builder`/`b` variable name that the surrounding `UseSetting("Mongo:ControlDatabase", …)` line uses in that file.)

- [ ] **Step 5b: Update the `ModuleRegistrar` construction test**

`ModuleHostingTests.The_registrar_upserts_contributed_registrations_on_startup` constructs `ModuleRegistrar` directly with a `ControlStore` — the reworked constructor no longer takes one. Replace that test method in `Backend/Accounting101.Ledger.Api.Tests/ModuleHostingTests.cs` (add `using Accounting101.Ledger.Api.Platform;` and `using Microsoft.Extensions.Configuration;`):

```csharp
    [Fact]
    public async Task The_registrar_upserts_contributed_registrations_on_startup()
    {
        // Seed the default firm pointing at the fixture's control DB, then run the registrar against it.
        PlatformStore platform = new(fixture.Mongo.GetDatabase(fixture.PlatformDatabase));
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = TenancyDefaults.DefaultFirmId, Name = "Default Firm",
            ControlDatabase = fixture.ControlDatabase, ClusterKey = "default",
        });
        // A factory whose home key "default" is pre-seeded to the fixture's client, so GetAsync("default")
        // needs no cluster-registry row.
        MongoClientFactory factory = new(fixture.Mongo, "default", platform);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        ModuleRegistrar registrar = new(
            [new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true }],
            platform, factory, config);
        await registrar.StartAsync(CancellationToken.None);

        ControlStore control = fixture.Control();
        ModuleRegistration? registered = await control.GetModuleAsync("invoicing");
        Assert.NotNull(registered);
        Assert.True(registered!.Enabled);
    }
```

- [ ] **Step 6: Build, then run the full backend suite**

Run: `dotnet build Accounting101.slnx`
Expected: build succeeds (no leftover references to the removed `_indexed` field, the old `ControlStore` singleton registration, or the old `ModuleRegistrar(modules, control)` constructor).

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS — existing tokens carry no firm claim → default firm → the fixture's control DB (same as before). The suite returns to green.

- [ ] **Step 7: Run the module suites (the cutover touched shared boot + fixtures)**

Run: `dotnet test Accounting101.slnx`
Expected: PASS across all projects. If a module fixture fails to resolve a client, confirm its `Mongo:PlatformDatabase` isolation edit (Step 5) landed and that `DefaultFirmSeeder` mapped the default firm to that fixture's `Mongo:ControlDatabase`.

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Tenancy/ClientLedgerFactory.cs \
        Backend/Accounting101.Ledger.Api/Hosting/CapabilitySetSeeder.cs \
        Backend/Accounting101.Ledger.Api/Hosting/ModuleRegistrar.cs \
        Backend/Accounting101.Ledger.Api/Hosting/ModuleHostingExtensions.cs \
        Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs \
        Backend/Accounting101.Ledger.Api.Tests/ModuleHostingTests.cs \
        Accounting101.Host/Program.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesHostFixture.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/DocumentStoreFixture.cs \
        Modules/Payables/Accounting101.Payables.Tests/PayablesHostFixture.cs \
        Modules/Payables/Accounting101.Payables.Tests/PayablesDocumentStoreFixture.cs \
        Modules/Payroll/Accounting101.Payroll.Tests/PayrollHostFixture.cs \
        Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashHostFixture.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/ReconciliationHostFixture.cs
git commit -m "feat(tenancy): firm-scope the control plane (cutover)

ControlStore/AdminAuditStore/ClientDatabaseResolver/ClientLedgerFactory/
ModuleAccess/LedgerGateway become scoped, reading the request firm's control
DB from FirmScope (populated by FirmResolutionMiddleware after auth). Index
cache extracted to a singleton IndexGuard. DefaultFirmSeeder registers the
configured single firm; CapabilitySetSeeder targets it. Host fixtures
GUID-isolate Mongo:PlatformDatabase. No firm claim -> default firm, so
existing behavior is unchanged.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Cross-firm isolation enforcement (end-to-end)

Prove the guarantee: a token scoped to firm A cannot reach firm B's client, even with B's real clientId. Add `ApiFixture` helpers to seed a second firm and mint firm-scoped tokens, then assert isolation through the real HTTP pipeline.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/CrossFirmIsolationTests.cs`

**Interfaces:**
- Consumes: `ApiFixture` (existing; already sets `Mongo:PlatformDatabase`), `PlatformStore`, `FirmClaims.FirmId`, `ControlStore`, `ClientRegistration`, the existing `ClientFor(userId, name, params claims)` helper.
- Produces: `ApiFixture.SeedFirmAsync(name)` → `(Guid FirmId, string ControlDatabase)`; a way to seed a client + member inside a named firm; firm-scoped `HttpClient`s via the existing `ClientFor(..., ("firm", firmId))`.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/CrossFirmIsolationTests.cs`:

```csharp
using System.Net;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CrossFirmIsolationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task A_firm_A_token_cannot_read_a_firm_B_client()
    {
        // Firm B: its own control DB, one client, one member.
        (Guid firmBId, string firmBControl) = await fixture.SeedFirmAsync("Firm B");
        Guid clientB = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        ControlStore controlB = new(fixture.Mongo.GetDatabase(firmBControl));
        await controlB.RegisterClientAsync(new ClientRegistration
        {
            Id = clientB, Name = "B Books", DatabaseName = "client_" + clientB.ToString("N"),
        });
        await controlB.AddMembershipAsync(userB, clientB, LedgerRole.Controller);

        // A user acting in firm A (the default firm) presents firm B's real clientId.
        HttpClient firmAClient = fixture.ClientFor(Guid.NewGuid(), "A User",
            (FirmClaims.FirmId, TenancyDefaults.DefaultFirmId.ToString()));

        HttpResponseMessage response = await firmAClient.GetAsync($"/clients/{clientB}/accounts");

        // Isolation: the request is resolved entirely within firm A's control DB, which knows nothing of
        // firm B's client. Denial surfaces as 403 (no membership for that id in firm A) — and would be 404
        // if a membership somehow existed but the client did not — but NEVER 200 and never firm B's data.
        // (The resolver's cross-firm refusal itself is unit-tested in FirmScopedClientDatabaseResolverTests.)
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"expected 403/404, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task A_firm_B_token_reaches_its_own_client()
    {
        (Guid firmBId, string firmBControl) = await fixture.SeedFirmAsync("Firm B2");
        Guid clientB = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        ControlStore controlB = new(fixture.Mongo.GetDatabase(firmBControl));
        await controlB.RegisterClientAsync(new ClientRegistration
        {
            Id = clientB, Name = "B2 Books", DatabaseName = "client_" + clientB.ToString("N"),
        });
        await controlB.AddMembershipAsync(userB, clientB, LedgerRole.Controller);

        HttpClient firmBClient = fixture.ClientFor(userB, "B2 User",
            (FirmClaims.FirmId, firmBId.ToString()), ("role", "Controller"));

        HttpResponseMessage response = await firmBClient.GetAsync($"/clients/{clientB}/accounts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

(The account-list route is `GET /clients/{clientId:guid}/accounts`, confirmed in `LedgerEndpoints` — `clients.MapGet("/accounts", ListAccounts)` under the `/clients/{clientId:guid}` group. `ListAccounts` resolves the client via the gateway, so an unresolvable client returns 404.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter CrossFirmIsolationTests`
Expected: FAIL — `ApiFixture.SeedFirmAsync` does not exist.

- [ ] **Step 3: Add the second-firm helper to `ApiFixture`**

In `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`, add `using Accounting101.Ledger.Api.Platform;` and a helper that registers a new firm in the fixture's platform DB with its own control DB:

```csharp
    /// <summary>Register a second firm (its own control DB) in this fixture's platform registry, so a test
    /// can prove cross-firm isolation. Returns the firm id and its control database name.</summary>
    public async Task<(Guid FirmId, string ControlDatabase)> SeedFirmAsync(string name)
    {
        Guid firmId = Guid.NewGuid();
        string controlDatabase = "firm_" + firmId.ToString("N") + "_control";
        PlatformStore platform = new(Mongo.GetDatabase(PlatformDatabase));
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = firmId, Name = name, ControlDatabase = controlDatabase, ClusterKey = "default",
        });
        return (firmId, controlDatabase);
    }
```

(`PlatformDatabase` already exists on `ApiFixture` from Phase 1. The `Mongo` client and `ClientFor(userId, name, params (string,string)[] claims)` helper already exist.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter CrossFirmIsolationTests`
Expected: PASS (both) — the firm-A token gets 404 for firm B's client; the firm-B token gets 200 for its own.

- [ ] **Step 5: Run the full backend suite**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs \
        Backend/Accounting101.Ledger.Api.Tests/CrossFirmIsolationTests.cs
git commit -m "test(tenancy): cross-firm isolation end-to-end

A firm-A token presenting firm B's clientId gets 404 (resolves only through
firm A's registry); a firm-B token reaches its own client. Proves the
structural isolation boundary.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Phase 2 Done — Definition of Done

- Every request resolves a firm (claim or configured default) into `FirmScope` via middleware; unknown/suspended firms are refused 403.
- `ControlStore`, `AdminAuditStore`, `ClientDatabaseResolver`, `ClientLedgerFactory`, `ModuleAccess`, `LedgerGateway` are firm-scoped; the index guarantee is preserved by a singleton `IIndexGuard`.
- `ClientDatabaseResolver` goes firm→cluster→client; a clientId outside the caller's firm is refused (404). Proven end-to-end.
- On-site / existing behavior unchanged: no firm claim → the seeded default firm → today's control DB. Full solution suite green.

**Not in Phase 2 (Phase 3):** firm provisioning endpoints (`POST /platform/firms` + per-firm capability-set seeding), the `platform=true` operator tier + policy, module-entitlement enforcement at the `ModuleAccess` chokepoint (reading client `EnabledModules`), and the usage/metering read. **Phase 4:** the on-site one-firm hardening. Deferred cleanup: remove the now-unregistered `Tenancy/ClientDatabaseResolver.cs`. Open questions from the spec still open: what issues the `platform=true` claim; GUID vs. slug in DB names.
```
