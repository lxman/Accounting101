# Multi-Firm Tenancy — Phase 3a: Operator tier + firm provisioning + clusters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the platform-operator control plane — a `platform=true`-gated `/platform/*` surface to provision firms (creating a GUID-named control DB seeded with capability sets), suspend/reactivate them, and register/list clusters.

**Architecture:** A new `PlatformAdmin` authorization policy (`RequireClaim("platform","true")`) gates a new `PlatformEndpoints` group mapped at `/platform`. Handlers operate on the existing singleton `PlatformStore` (the `platform_control` registry) and `IMongoClientFactory` — they do NOT use `FirmScope`. Everything is additive; the existing suite stays green.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB C# driver, xUnit, EphemeralMongo (`Accounting101.TestSupport.SharedMongo`), `WebApplicationFactory<Program>` via `ApiFixture`.

## Global Constraints

- **Target framework:** .NET 10; namespaces follow folder structure.
- **Operator gate:** every `/platform/*` route requires the `PlatformEndpoints.Policy` (`"PlatformAdmin"` → `RequireClaim("platform","true")`). A firm-admin (`admin=true`) token is NOT a platform operator and must get 403; an unauthenticated request gets 401.
- **GUID DB naming:** a provisioned firm's control DB is `"firm_" + firmId.ToString("N") + "_control"`.
- **Provisioning seeds capability sets, creates NO membership** (memberships are per-client; none exist at firm creation). Seed via `ControlStore.SeedBuiltinCapabilitySetsAsync` against the new firm's control DB.
- **Cluster list redacts connection strings** — never return a raw connection string in any response.
- **Additive only:** no existing endpoint, service lifetime, or the firm-scoped request path changes. The full backend suite stays green after every task.
- **Tests:** EphemeralMongo via `SharedMongo`; `ApiFixture` boots the real pipeline (auth + firm resolution) and GUID-isolates `Mongo:PlatformDatabase`. A platform-operator client is `fixture.ClientFor(userId, "Operator", ("platform","true"))`.
- **Commits:** stage explicit file paths only — never `git add -A`. Do NOT stage `UI/Angular/src/app/core/api/environment.ts` or IDE `.csproj`/`.slnx` churn. Every commit message ends with:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Backend runner:** `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`.

---

## File Structure

**New files:**
- `Backend/Accounting101.Ledger.Contracts/PlatformContracts.cs` — wire DTOs (`ProvisionFirmRequest`, `FirmResponse`, `SetFirmStatusRequest`, `RegisterClusterRequest`, `ClusterResponse`).
- `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs` — the `PlatformAdmin`-gated `/platform/*` group + handlers.
- `Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs` — firm gate + list + provision + status tests.
- `Backend/Accounting101.Ledger.Api.Tests/PlatformClustersTests.cs` — cluster register/list tests.

**Modified files:**
- `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs` — register the `PlatformAdmin` policy (Task 1).
- `Accounting101.Host/Program.cs` — `app.MapPlatformEndpoints()` (Task 1).

---

## Task 1: Platform contracts, PlatformAdmin policy, and `GET /platform/firms`

Establish the operator surface: the contracts, the policy, the endpoint group, and the simplest handler (list firms). Prove the gate (operator 200, firm-admin 403, anonymous 401).

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/PlatformContracts.cs`
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`
- Modify: `Accounting101.Host/Program.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs`

**Interfaces:**
- Consumes: `PlatformStore.ListFirmsAsync(CancellationToken=default)` returning `IReadOnlyList<FirmRegistration>`; `FirmRegistration { Guid Id; string Name; string ControlDatabase; string ClusterKey; FirmStatus Status; DateTime CreatedUtc; }`; `TenancyDefaults.DefaultFirmId`; `ApiFixture.ClientFor(Guid, string?, params (string,string)[])`, `ApiFixture.AnonymousClient()`.
- Produces:
  - Contracts: `ProvisionFirmRequest { required string Name; string? ClusterKey; }`, `FirmResponse(Guid Id, string Name, string Status, string ClusterKey, string ControlDatabase, DateTime CreatedUtc)`, `SetFirmStatusRequest(string Status)`, `RegisterClusterRequest(string Key, string ConnectionString)`, `ClusterResponse(string Key, bool HasConnectionString)`.
  - `PlatformEndpoints.Policy` (`"PlatformAdmin"`), `PlatformEndpoints.MapPlatformEndpoints(IEndpointRouteBuilder)`, and the private `ToResponse(FirmRegistration)` helper later tasks reuse.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformFirmsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private HttpClient Operator() => fixture.ClientFor(Guid.NewGuid(), "Operator", ("platform", "true"));

    [Fact]
    public async Task Non_operator_is_forbidden_and_anonymous_is_unauthorized()
    {
        HttpClient firmAdmin = fixture.ClientFor(Guid.NewGuid(), "Firm Admin", ("admin", "true"));
        Assert.Equal(HttpStatusCode.Forbidden, (await firmAdmin.GetAsync("/platform/firms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.AnonymousClient().GetAsync("/platform/firms")).StatusCode);
    }

    [Fact]
    public async Task Operator_lists_firms_including_the_default_firm()
    {
        HttpResponseMessage resp = await Operator().GetAsync("/platform/firms");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        List<FirmResponse> firms = (await resp.Content.ReadFromJsonAsync<List<FirmResponse>>())!;
        Assert.Contains(firms, f => f.Id == TenancyDefaults.DefaultFirmId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformFirmsTests`
Expected: FAIL — `FirmResponse` / `/platform/firms` do not exist (compile error, then 404).

- [ ] **Step 3: Create the platform contracts**

Create `Backend/Accounting101.Ledger.Contracts/PlatformContracts.cs`:

```csharp
namespace Accounting101.Ledger.Contracts;

/// <summary>Provision a new firm. <see cref="ClusterKey"/> defaults to the home cluster when omitted.</summary>
public sealed record ProvisionFirmRequest
{
    public required string Name { get; init; }
    public string? ClusterKey { get; init; }
}

/// <summary>A firm as returned by the platform control plane. Status is "Active" | "Suspended".</summary>
public sealed record FirmResponse(
    Guid Id, string Name, string Status, string ClusterKey, string ControlDatabase, DateTime CreatedUtc);

/// <summary>Set a firm's lifecycle status: "Active" | "Suspended".</summary>
public sealed record SetFirmStatusRequest(string Status);

/// <summary>Register a cluster the platform can place firms on.</summary>
public sealed record RegisterClusterRequest(string Key, string ConnectionString);

/// <summary>A registered cluster. The connection string is never returned — only whether one is set.</summary>
public sealed record ClusterResponse(string Key, bool HasConnectionString);
```

- [ ] **Step 4: Create `PlatformEndpoints` with the group + ListFirms**

Create `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`:

```csharp
using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// The platform-operator control plane: provision and manage firms and clusters in the platform_control
/// registry. Gated by the <see cref="Policy"/> (a trusted <c>platform=true</c> token claim) — one tier
/// above firm admin. These handlers operate on the singleton <see cref="PlatformStore"/> and do not use
/// the request's firm scope.
/// </summary>
public static class PlatformEndpoints
{
    public const string Policy = "PlatformAdmin";

    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder platform = app.MapGroup("/platform").RequireAuthorization(Policy);
        platform.MapGet("/firms", ListFirms);
    }

    private static async Task<IResult> ListFirms(PlatformStore platform, CancellationToken cancellationToken)
    {
        IReadOnlyList<FirmRegistration> firms = await platform.ListFirmsAsync(cancellationToken);
        return Results.Ok(firms.Select(ToResponse).ToList());
    }

    private static FirmResponse ToResponse(FirmRegistration f) =>
        new(f.Id, f.Name, f.Status.ToString(), f.ClusterKey, f.ControlDatabase, f.CreatedUtc);
}
```

- [ ] **Step 5: Register the `PlatformAdmin` policy**

In `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`, in the `AddAuthorization` options block, add the platform policy right after the existing admin policy line (`options.AddPolicy(AdminEndpoints.Policy, policy => policy.RequireClaim("admin", "true"));`):

```csharp
            options.AddPolicy(PlatformEndpoints.Policy, policy => policy.RequireClaim("platform", "true"));
```

(`PlatformEndpoints` is in the same `Accounting101.Ledger.Api` assembly; `AdminEndpoints` is already referenced in this file, so no new `using` is required — both live under `Accounting101.Ledger.Api.Endpoints`, which is already imported for `AdminEndpoints.Policy`.)

- [ ] **Step 6: Map the endpoints in `Program.cs`**

In `Accounting101.Host/Program.cs`, add `app.MapPlatformEndpoints();` in the endpoint-mapping block, right after `app.MapAdminAuditEndpoints();`:

```csharp
app.MapPlatformEndpoints();
```

(The `using Accounting101.Ledger.Api.Endpoints;` needed for this extension is already present — `Program.cs` calls `app.MapAdminEndpoints()` etc. from that namespace.)

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformFirmsTests`
Expected: PASS (both tests).

- [ ] **Step 8: Run the full backend suite (Program.cs + policy touch the shared host)**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS — additive; existing tests unaffected.

- [ ] **Step 9: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/PlatformContracts.cs \
        Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs \
        Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs \
        Accounting101.Host/Program.cs \
        Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs
git commit -m "feat(platform): operator tier + GET /platform/firms

PlatformAdmin policy (platform=true) gating a new /platform group; contracts;
list firms. Firm-admin/anonymous are refused (403/401).

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: `POST /platform/firms` — provision a firm

Create a firm: register it, build its GUID-named control DB, and seed the built-in capability sets so an `admin=true` firm admin can use it immediately. Reject an unknown cluster with 400.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs`

**Interfaces:**
- Consumes: `IMongoClientFactory.GetAsync(string clusterKey, CancellationToken=default)` (throws `InvalidOperationException` for an unregistered key; the home key `"default"` always resolves); `PlatformStore.RegisterFirmAsync(FirmRegistration, CancellationToken=default)`; `ControlStore(IMongoDatabase)` + `ControlStore.SeedBuiltinCapabilitySetsAsync(CancellationToken=default)` + `ControlStore.GetCapabilitySetByNameAsync(string, CancellationToken=default)`.
- Produces: `POST /platform/firms` returning `201 Created` + `FirmResponse`.

- [ ] **Step 1: Write the failing test**

Add to `Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs` (add `using Accounting101.Ledger.Api.Control;` to the file's usings):

```csharp
    [Fact]
    public async Task Provisions_a_firm_and_seeds_its_capability_sets()
    {
        HttpResponseMessage resp = await Operator().PostAsJsonAsync(
            "/platform/firms", new ProvisionFirmRequest { Name = "Ledger Pros" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        FirmResponse firm = (await resp.Content.ReadFromJsonAsync<FirmResponse>())!;
        Assert.Equal("Ledger Pros", firm.Name);
        Assert.Equal("Active", firm.Status);
        Assert.StartsWith("firm_", firm.ControlDatabase);

        // The new firm's control DB has the built-in capability sets — it is usable immediately.
        ControlStore control = new(fixture.Mongo.GetDatabase(firm.ControlDatabase));
        Assert.NotNull(await control.GetCapabilitySetByNameAsync("Admin"));
        Assert.NotNull(await control.GetCapabilitySetByNameAsync("Clerk"));

        // And it is listed.
        List<FirmResponse> firms = (await (await Operator().GetAsync("/platform/firms"))
            .Content.ReadFromJsonAsync<List<FirmResponse>>())!;
        Assert.Contains(firms, f => f.Id == firm.Id);
    }

    [Fact]
    public async Task Provisioning_with_an_unknown_cluster_is_400()
    {
        HttpResponseMessage resp = await Operator().PostAsJsonAsync(
            "/platform/firms", new ProvisionFirmRequest { Name = "X", ClusterKey = "no-such-cluster" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformFirmsTests`
Expected: FAIL — `POST /platform/firms` is unmapped (404), so `Created`/`BadRequest` assertions fail.

- [ ] **Step 3: Add the ProvisionFirm handler**

In `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`, add `using Accounting101.Ledger.Api.Control;` and `using MongoDB.Driver;` to the usings, register the route in `MapPlatformEndpoints` (after the `MapGet("/firms", ...)` line):

```csharp
        platform.MapPost("/firms", ProvisionFirm);
```

and add the handler (after `ListFirms`):

```csharp
    private static async Task<IResult> ProvisionFirm(
        ProvisionFirmRequest request, PlatformStore platform, IMongoClientFactory factory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Firm name is required.", statusCode: StatusCodes.Status400BadRequest);

        string clusterKey = string.IsNullOrWhiteSpace(request.ClusterKey) ? "default" : request.ClusterKey;

        IMongoClient client;
        try
        {
            client = await factory.GetAsync(clusterKey, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem($"Unknown cluster '{clusterKey}'.", statusCode: StatusCodes.Status400BadRequest);
        }

        Guid firmId = Guid.NewGuid();
        string controlDatabase = "firm_" + firmId.ToString("N") + "_control";
        FirmRegistration firm = new()
        {
            Id = firmId,
            Name = request.Name,
            ControlDatabase = controlDatabase,
            ClusterKey = clusterKey,
            Status = FirmStatus.Active,
            CreatedUtc = DateTime.UtcNow,
        };
        await platform.RegisterFirmAsync(firm, cancellationToken);

        // Seed the new firm's control DB with the built-in capability sets so an admin=true firm admin can
        // create clients and members immediately. No membership is created here — memberships are per-client.
        ControlStore control = new(client.GetDatabase(controlDatabase));
        await control.SeedBuiltinCapabilitySetsAsync(cancellationToken);

        return Results.Created($"/platform/firms/{firmId}", ToResponse(firm));
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformFirmsTests`
Expected: PASS (all four tests in the class).

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs
git commit -m "feat(platform): POST /platform/firms provisions a firm + seeds its control DB

Creates a GUID-named control DB, registers the firm, seeds built-in capability
sets (no membership). Unknown cluster -> 400.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: `PATCH /platform/firms/{firmId}/status` — suspend / reactivate

Let an operator suspend or reactivate a firm. Suspension is already enforced at `FirmResolutionMiddleware` (Phase 2), so this proves end-to-end: suspend → the firm's requests are refused with 403.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs`

**Interfaces:**
- Consumes: `PlatformStore.GetFirmAsync(Guid, CancellationToken=default)`, `PlatformStore.SetFirmStatusAsync(Guid, FirmStatus, CancellationToken=default)`; `FirmClaims.FirmId` (`"firm"`); `FirmStatus { Active, Suspended }`.
- Produces: `PATCH /platform/firms/{firmId:guid}/status` returning `200` + updated `FirmResponse`, `404` for an unknown firm, `422` for an unparsable status.

- [ ] **Step 1: Write the failing test**

Add to `Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs`:

```csharp
    [Fact]
    public async Task Suspending_a_firm_blocks_its_requests_at_the_middleware()
    {
        FirmResponse firm = (await (await Operator().PostAsJsonAsync(
            "/platform/firms", new ProvisionFirmRequest { Name = "Doomed" }))
            .Content.ReadFromJsonAsync<FirmResponse>())!;

        HttpResponseMessage patch = await Operator().PatchAsJsonAsync(
            $"/platform/firms/{firm.Id}/status", new SetFirmStatusRequest("Suspended"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Suspended", (await patch.Content.ReadFromJsonAsync<FirmResponse>())!.Status);

        // A request carrying the suspended firm's claim is refused at firm resolution, before any endpoint.
        HttpClient suspended = fixture.ClientFor(Guid.NewGuid(), "Member", (FirmClaims.FirmId, firm.Id.ToString()));
        HttpResponseMessage blocked = await suspended.GetAsync($"/clients/{Guid.NewGuid()}/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [Fact]
    public async Task Set_status_on_an_unknown_firm_is_404()
    {
        HttpResponseMessage resp = await Operator().PatchAsJsonAsync(
            $"/platform/firms/{Guid.NewGuid()}/status", new SetFirmStatusRequest("Suspended"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformFirmsTests`
Expected: FAIL — `PATCH /platform/firms/{id}/status` is unmapped (404 on the PATCH, so the OK assertion fails).

- [ ] **Step 3: Add the SetFirmStatus handler**

In `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`, register the route in `MapPlatformEndpoints` (after the `MapPost("/firms", ...)` line):

```csharp
        platform.MapPatch("/firms/{firmId:guid}/status", SetFirmStatus);
```

and add the handler:

```csharp
    private static async Task<IResult> SetFirmStatus(
        Guid firmId, SetFirmStatusRequest request, PlatformStore platform, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(request.Status, ignoreCase: true, out FirmStatus status))
            return Results.Problem($"Unknown status '{request.Status}'.", statusCode: StatusCodes.Status422UnprocessableEntity);

        if (await platform.GetFirmAsync(firmId, cancellationToken) is null)
            return Results.NotFound();

        await platform.SetFirmStatusAsync(firmId, status, cancellationToken);
        FirmRegistration updated = (await platform.GetFirmAsync(firmId, cancellationToken))!;
        return Results.Ok(ToResponse(updated));
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformFirmsTests`
Expected: PASS (all six tests in the class).

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/PlatformFirmsTests.cs
git commit -m "feat(platform): PATCH /platform/firms/{id}/status (suspend/reactivate)

Suspending a firm blocks its requests at FirmResolutionMiddleware (403);
unknown firm -> 404, unparsable status -> 422.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: `POST /platform/clusters` + `GET /platform/clusters` — cluster management

Register a cluster and list registered clusters, redacting connection strings.

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformClustersTests.cs`

**Interfaces:**
- Consumes: `PlatformStore.RegisterClusterAsync(ClusterRegistration, CancellationToken=default)`, `PlatformStore.ListClustersAsync(CancellationToken=default)`; `ClusterRegistration { string Key; string ConnectionString; }`; the `"default"` cluster is present (seeded at startup by `PlatformClusterSeeder`).
- Produces: `POST /platform/clusters` → `201` + `ClusterResponse`; `GET /platform/clusters` → `200` + `List<ClusterResponse>` (connection strings redacted).

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/PlatformClustersTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformClustersTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private HttpClient Operator() => fixture.ClientFor(Guid.NewGuid(), "Operator", ("platform", "true"));

    [Fact]
    public async Task Registers_and_lists_clusters_without_leaking_connection_strings()
    {
        const string secret = "mongodb://secret-host:27017/very-secret";
        HttpResponseMessage reg = await Operator().PostAsJsonAsync(
            "/platform/clusters", new RegisterClusterRequest("cluster-2", secret));
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        HttpResponseMessage list = await Operator().GetAsync("/platform/clusters");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        string body = await list.Content.ReadAsStringAsync();
        List<ClusterResponse> clusters = JsonSerializer.Deserialize<List<ClusterResponse>>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Contains(clusters, c => c.Key == "cluster-2" && c.HasConnectionString);
        Assert.Contains(clusters, c => c.Key == "default");
        // Redaction: the raw connection string is never present in the response body.
        Assert.DoesNotContain("secret-host", body);
    }

    [Fact]
    public async Task Register_cluster_requires_key_and_connection_string()
    {
        HttpResponseMessage resp = await Operator().PostAsJsonAsync(
            "/platform/clusters", new RegisterClusterRequest("", "mongodb://x"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformClustersTests`
Expected: FAIL — the cluster routes are unmapped (404).

- [ ] **Step 3: Add the cluster handlers**

In `Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs`, register the routes in `MapPlatformEndpoints` (after the firm routes):

```csharp
        platform.MapGet("/clusters", ListClusters);
        platform.MapPost("/clusters", RegisterCluster);
```

and add the handlers:

```csharp
    private static async Task<IResult> ListClusters(PlatformStore platform, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClusterRegistration> clusters = await platform.ListClustersAsync(cancellationToken);
        return Results.Ok(clusters
            .Select(c => new ClusterResponse(c.Key, !string.IsNullOrEmpty(c.ConnectionString)))
            .ToList());
    }

    private static async Task<IResult> RegisterCluster(
        RegisterClusterRequest request, PlatformStore platform, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.ConnectionString))
            return Results.Problem("Cluster key and connection string are required.", statusCode: StatusCodes.Status400BadRequest);

        await platform.RegisterClusterAsync(
            new ClusterRegistration { Key = request.Key, ConnectionString = request.ConnectionString }, cancellationToken);
        return Results.Created($"/platform/clusters/{request.Key}", new ClusterResponse(request.Key, true));
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformClustersTests`
Expected: PASS (both tests).

- [ ] **Step 5: Run the full backend suite**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS — all platform tests plus no regressions.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/PlatformEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/PlatformClustersTests.cs
git commit -m "feat(platform): register + list clusters (connection strings redacted)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Phase 3a Done — Definition of Done

- A `platform=true`-gated `/platform/*` surface exists; firm-admin/anonymous are refused (403/401).
- `POST /platform/firms` provisions a firm with a GUID-named control DB seeded with capability sets (no membership); unknown cluster → 400.
- `PATCH /platform/firms/{id}/status` suspends/reactivates; suspension is enforced end-to-end at the middleware (403).
- `POST`/`GET /platform/clusters` register and list clusters without leaking connection strings.
- Full backend suite green; nothing existing changed.

**Not in 3a (Phase 3b):** the `PUT /admin/clients/{clientId}/modules` entitlement setter, default-closed `EnabledModules` enforcement at the `ModuleAccess` chokepoint (with the module-fixture sweep), and `GET /platform/usage`. Open questions for 3b: validate unknown module keys in the setter (lean: yes); whether provisioned firms need module registrations seeded (deferred until a real second firm is provisioned).
