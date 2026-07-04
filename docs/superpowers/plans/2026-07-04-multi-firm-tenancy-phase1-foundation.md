# Multi-Firm Tenancy — Phase 1: Foundation (data model + platform registry + cluster factory) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the additive foundation for multi-firm tenancy — client lifecycle/entitlement fields, a `platform_control` firm+cluster registry, and a cluster-keyed `IMongoClient` factory — without touching the existing per-request control/ledger path, so the current test suite stays green.

**Architecture:** Everything in this phase is *additive*. New model fields on `ClientRegistration` default to backward-compatible values (legacy documents deserialize unchanged). A new `Accounting101.Ledger.Api.Platform` namespace holds the `platform_control` persistence (`PlatformStore`), the firm/cluster records, and a `MongoClientFactory` that resolves one pooled `IMongoClient` per cluster key. A DI extension wires these as singletons plus one idempotent startup seeder for the `"default"` cluster. Nothing in Phase 1 changes how a request resolves a client DB — that re-scoping is Phase 2.

**Tech Stack:** C# / .NET 10, MongoDB C# driver, xUnit, EphemeralMongo (single-node replica set; nothing to install), `Accounting101.TestSupport.SharedMongo` shared runner.

## Global Constraints

- **Target framework:** .NET 10; use the latest NuGets already referenced. Namespaces follow folder structure.
- **Tests use EphemeralMongo** via `Accounting101.TestSupport.SharedMongo.InstanceAsync()`; isolate every test by a GUID database name. Never stand up a second `mongod`.
- **Serialization:** any type persisted to Mongo must have `LedgerMongoBootstrap.RegisterOnce()` invoked before first I/O (call it from a static constructor, matching every existing store). GUIDs are binary subtype 4 (standard), decimals are Decimal128 — already registered by that call.
- **Additive only in Phase 1:** do NOT change the lifetime or signature of `ControlStore`, `AdminAuditStore`, `ClientDatabaseResolver`, `ClientLedgerFactory`, `ModuleAccess`, or any endpoint. The existing ~800-test suite must remain green after every task.
- **Commits:** stage **explicit file paths only** — never `git add -A`/`git add .`. Do NOT stage `UI/Angular/src/app/core/api/environment.ts`, and do NOT stage IDE-driven `.csproj`/`.slnx` churn. Verify the staged set before committing.
- **Every commit message ends with the trailer:**
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Build/test commands:** backend tests run with
  `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`.

---

## File Structure

**New files (all under `Backend/Accounting101.Ledger.Api/`):**
- `Control/ClientStatus.cs` — `ClientStatus` enum (`Active`/`Archived`). Lives beside `ClientRegistration`.
- `Platform/FirmRegistration.cs` — `FirmStatus` enum + `FirmRegistration` record-class (firm → control DB + cluster key + status).
- `Platform/ClusterRegistration.cs` — `ClusterRegistration` (cluster key → connection string).
- `Platform/PlatformStore.cs` — persistence over `platform_control` (`firms`, `clusters`).
- `Platform/IMongoClientFactory.cs` — the cluster-key → `IMongoClient` seam.
- `Platform/MongoClientFactory.cs` — pooled-per-cluster implementation.
- `Platform/PlatformClusterSeeder.cs` — `IHostedService` that idempotently registers the home (`"default"`) cluster on startup.
- `Hosting/PlatformRegistryExtensions.cs` — `AddPlatformRegistry(IServiceCollection, IConfiguration)` DI wiring.

**Modified files:**
- `Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs` — add `Status`, `EnabledModules`, `CreatedUtc`, `ArchivedUtc`.
- `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs` — one line calling `AddPlatformRegistry`.

**New test files (under `Backend/Accounting101.Ledger.Api.Tests/`):**
- `ClientRegistrationTenancyFieldsTests.cs`
- `PlatformStoreTests.cs`
- `MongoClientFactoryTests.cs`
- `PlatformRegistryWiringTests.cs`

---

## Task 1: Client tenancy fields (Status, EnabledModules, timestamps)

Add the lifecycle + entitlement fields the billing meter will read. Legacy client documents (written before these fields existed) must deserialize with safe defaults: `Status = Active`, `EnabledModules = []`, `ArchivedUtc = null`.

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Control/ClientStatus.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ClientRegistrationTenancyFieldsTests.cs`

**Interfaces:**
- Consumes: `ControlStore.RegisterClientAsync(ClientRegistration, ...)`, `ControlStore.GetClientAsync(Guid, ...)` (existing, unchanged).
- Produces: `enum ClientStatus { Active = 0, Archived = 1 }`; new `ClientRegistration` members `ClientStatus Status`, `IReadOnlyList<string> EnabledModules`, `DateTime CreatedUtc`, `DateTime? ArchivedUtc`. Later phases read `EnabledModules` (entitlement gate) and `Status`/timestamps (usage meter).

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/ClientRegistrationTenancyFieldsTests.cs`:

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ClientRegistrationTenancyFieldsTests
{
    private static async Task<IMongoDatabase> FreshDbAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        return new MongoClient(runner.ConnectionString)
            .GetDatabase("ctl_tenancy_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task New_tenancy_fields_round_trip()
    {
        ControlStore control = new(await FreshDbAsync());
        Guid id = Guid.NewGuid();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = id,
            Name = "Acme",
            DatabaseName = "client_x",
            Status = ClientStatus.Archived,
            EnabledModules = ["payroll", "payables"],
            CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ArchivedUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        ClientRegistration reg = (await control.GetClientAsync(id))!;
        Assert.Equal(ClientStatus.Archived, reg.Status);
        Assert.Equal(new[] { "payroll", "payables" }, reg.EnabledModules);
        Assert.Equal(2026, reg.CreatedUtc.Year);
        Assert.NotNull(reg.ArchivedUtc);
    }

    [Fact]
    public async Task Legacy_document_without_the_fields_defaults_to_Active_and_empty()
    {
        IMongoDatabase db = await FreshDbAsync();
        Guid id = Guid.NewGuid();

        // A pre-existing registration written before these fields existed: only the original members.
        await db.GetCollection<BsonDocument>("clients").InsertOneAsync(new BsonDocument
        {
            { "_id", new BsonBinaryData(id, GuidRepresentation.Standard) },
            { "Name", "Legacy Co" },
            { "DatabaseName", "client_legacy" },
            { "RequireSegregationOfDuties", false },
            { "FiscalYearEndMonth", 12 },
        });

        ControlStore control = new(db);
        ClientRegistration reg = (await control.GetClientAsync(id))!;
        Assert.Equal(ClientStatus.Active, reg.Status);
        Assert.Empty(reg.EnabledModules);
        Assert.Null(reg.ArchivedUtc);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter ClientRegistrationTenancyFieldsTests`
Expected: FAIL — compile error, `ClientStatus` and the new members do not exist yet.

- [ ] **Step 3: Create the `ClientStatus` enum**

Create `Backend/Accounting101.Ledger.Api/Control/ClientStatus.cs`:

```csharp
namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Lifecycle of a client's books. <see cref="Active"/> is the billable/usable state; <see cref="Archived"/>
/// stops the per-client meter while the ledger DB is retained intact (accounting data must survive for
/// years, so closing a client is never a delete). <see cref="Active"/> is 0 so legacy documents written
/// before this field existed deserialize to Active.
/// </summary>
public enum ClientStatus
{
    Active = 0,
    Archived = 1,
}
```

- [ ] **Step 4: Add the fields to `ClientRegistration`**

In `Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs`, add these members inside the class, after the existing `FiscalYearEndMonth` property:

```csharp
    /// <summary>Lifecycle state. Defaults to <see cref="ClientStatus.Active"/>; a missing field on a legacy
    /// document also deserializes to Active. Archiving stops the meter but keeps the ledger DB.</summary>
    public ClientStatus Status { get; set; } = ClientStatus.Active;

    /// <summary>The module keys this client is entitled to (e.g. "receivables", "payables", "payroll").
    /// Doubles as the billing meter (per-module fee) and the access gate (Phase 3 checks it at the module
    /// authorization chokepoint). Empty by default; a missing field on a legacy document deserializes to
    /// empty.</summary>
    public IReadOnlyList<string> EnabledModules { get; set; } = [];

    /// <summary>When the client was provisioned (UTC). Legacy documents deserialize to default(DateTime).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When the client was archived (UTC), or null while active.</summary>
    public DateTime? ArchivedUtc { get; set; }
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter ClientRegistrationTenancyFieldsTests`
Expected: PASS (both tests).

- [ ] **Step 6: Run the full backend suite to confirm nothing regressed**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS (no regressions — the fields are additive with defaults).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ClientStatus.cs \
        Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs \
        Backend/Accounting101.Ledger.Api.Tests/ClientRegistrationTenancyFieldsTests.cs
git commit -m "feat(tenancy): client lifecycle + module-entitlement fields

Adds Status (Active/Archived), EnabledModules, CreatedUtc, ArchivedUtc to
ClientRegistration with backward-compatible defaults for legacy documents.
Additive only; no behavior change to the request path.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Platform registry store (`platform_control`)

Create the new tier's persistence: firm records (firm → control DB + cluster + status) and cluster records (cluster key → connection string). This is the one-per-SaaS-install registry above the firm control DBs.

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Platform/FirmRegistration.cs`
- Create: `Backend/Accounting101.Ledger.Api/Platform/ClusterRegistration.cs`
- Create: `Backend/Accounting101.Ledger.Api/Platform/PlatformStore.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformStoreTests.cs`

**Interfaces:**
- Consumes: `IMongoDatabase` (the `platform_control` DB), `LedgerMongoBootstrap.RegisterOnce()`.
- Produces:
  - `enum FirmStatus { Active = 0, Suspended = 1 }`
  - `class FirmRegistration { Guid Id; string Name; string ControlDatabase; string ClusterKey = "default"; FirmStatus Status = Active; DateTime CreatedUtc; }`
  - `class ClusterRegistration { string Key; string ConnectionString; }`
  - `PlatformStore(IMongoDatabase)` with:
    `Task<FirmRegistration?> GetFirmAsync(Guid, CancellationToken=default)`,
    `Task RegisterFirmAsync(FirmRegistration, CancellationToken=default)`,
    `Task<IReadOnlyList<FirmRegistration>> ListFirmsAsync(CancellationToken=default)`,
    `Task SetFirmStatusAsync(Guid, FirmStatus, CancellationToken=default)`,
    `Task<ClusterRegistration?> GetClusterAsync(string, CancellationToken=default)`,
    `Task RegisterClusterAsync(ClusterRegistration, CancellationToken=default)`,
    `Task<IReadOnlyList<ClusterRegistration>> ListClustersAsync(CancellationToken=default)`.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/PlatformStoreTests.cs`:

```csharp
using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformStoreTests
{
    private static async Task<PlatformStore> FreshStoreAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        return new PlatformStore(new MongoClient(runner.ConnectionString)
            .GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Firm_round_trips_lists_and_status_updates()
    {
        PlatformStore platform = await FreshStoreAsync();
        Guid firmId = Guid.NewGuid();
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = firmId,
            Name = "Ledger Pros",
            ControlDatabase = "firm_x_control",
            ClusterKey = "default",
            CreatedUtc = new DateTime(2026, 7, 4, 0, 0, 0, DateTimeKind.Utc),
        });

        FirmRegistration firm = (await platform.GetFirmAsync(firmId))!;
        Assert.Equal("Ledger Pros", firm.Name);
        Assert.Equal("firm_x_control", firm.ControlDatabase);
        Assert.Equal(FirmStatus.Active, firm.Status);

        await platform.SetFirmStatusAsync(firmId, FirmStatus.Suspended);
        Assert.Equal(FirmStatus.Suspended, (await platform.GetFirmAsync(firmId))!.Status);

        Assert.Contains(await platform.ListFirmsAsync(), f => f.Id == firmId);
    }

    [Fact]
    public async Task Cluster_round_trips()
    {
        PlatformStore platform = await FreshStoreAsync();
        await platform.RegisterClusterAsync(new ClusterRegistration
        {
            Key = "cluster-2",
            ConnectionString = "mongodb://c2.example",
        });

        Assert.Equal("mongodb://c2.example", (await platform.GetClusterAsync("cluster-2"))!.ConnectionString);
        Assert.Contains(await platform.ListClustersAsync(), c => c.Key == "cluster-2");
        Assert.Null(await platform.GetClusterAsync("missing"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformStoreTests`
Expected: FAIL — compile error, the `Platform` types do not exist.

- [ ] **Step 3: Create the firm + cluster records**

Create `Backend/Accounting101.Ledger.Api/Platform/FirmRegistration.cs`:

```csharp
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>Billing/access lifecycle of a firm. <see cref="Active"/> is 0 so legacy documents default to it.</summary>
public enum FirmStatus
{
    Active = 0,
    Suspended = 1,
}

/// <summary>
/// One firm (an accounting practice) registered in the platform control database. Maps the firm id to
/// the firm's own control database and to the cluster that holds all of the firm's databases. A firm is
/// the unit of cluster placement — its control DB and every client DB share its <see cref="ClusterKey"/>,
/// which is what makes a firm a self-contained, relocatable set of databases.
/// </summary>
public sealed class FirmRegistration
{
    [BsonId]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The MongoDB database name holding this firm's control data (client registry, memberships, …).</summary>
    public string ControlDatabase { get; set; } = string.Empty;

    /// <summary>The cluster this firm lives on. Defaults to the home cluster; a missing field on a legacy
    /// document also deserializes to "default".</summary>
    public string ClusterKey { get; set; } = "default";

    public FirmStatus Status { get; set; } = FirmStatus.Active;

    /// <summary>When the firm was provisioned (UTC).</summary>
    public DateTime CreatedUtc { get; set; }
}
```

Create `Backend/Accounting101.Ledger.Api/Platform/ClusterRegistration.cs`:

```csharp
using MongoDB.Bson.Serialization.Attributes;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// A physical MongoDB cluster the platform can place firms on, keyed by a stable short name (the
/// <see cref="Key"/>, e.g. "default", "cluster-2"). The connection string is resolved through this
/// registry so adding a second Atlas cluster is a data change, not a code change.
/// </summary>
public sealed class ClusterRegistration
{
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public string ConnectionString { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Create `PlatformStore`**

Create `Backend/Accounting101.Ledger.Api/Platform/PlatformStore.cs`:

```csharp
using Accounting101.Ledger.Mongo.Serialization;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Persistence for the platform control database (one per SaaS install): the firm registry
/// (firm id → control DB + cluster) and the cluster registry (cluster key → connection string). This is
/// the tier above every firm's control DB. On an on-site deployment it holds exactly one firm.
/// </summary>
public sealed class PlatformStore
{
    private readonly IMongoCollection<FirmRegistration> _firms;
    private readonly IMongoCollection<ClusterRegistration> _clusters;

    static PlatformStore() => LedgerMongoBootstrap.RegisterOnce();

    public PlatformStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _firms = database.GetCollection<FirmRegistration>("firms");
        _clusters = database.GetCollection<ClusterRegistration>("clusters");
    }

    /// <summary>The firm's registration, or null if no such firm exists.</summary>
    public async Task<FirmRegistration?> GetFirmAsync(Guid firmId, CancellationToken cancellationToken = default) =>
        await _firms.Find(f => f.Id == firmId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Register (or update) a firm — idempotent upsert keyed by firm id.</summary>
    public Task RegisterFirmAsync(FirmRegistration firm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firm);
        return _firms.ReplaceOneAsync(
            f => f.Id == firm.Id, firm, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>All firms registered on the platform.</summary>
    public async Task<IReadOnlyList<FirmRegistration>> ListFirmsAsync(CancellationToken cancellationToken = default) =>
        await _firms.Find(FilterDefinition<FirmRegistration>.Empty).ToListAsync(cancellationToken);

    /// <summary>Set a firm's lifecycle status (e.g. suspend on non-payment).</summary>
    public Task SetFirmStatusAsync(Guid firmId, FirmStatus status, CancellationToken cancellationToken = default) =>
        _firms.UpdateOneAsync(
            f => f.Id == firmId,
            Builders<FirmRegistration>.Update.Set(f => f.Status, status),
            cancellationToken: cancellationToken);

    /// <summary>The cluster's registration, or null if no such cluster key is registered.</summary>
    public async Task<ClusterRegistration?> GetClusterAsync(string key, CancellationToken cancellationToken = default) =>
        await _clusters.Find(c => c.Key == key).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Register (or update) a cluster — idempotent upsert keyed by cluster key.</summary>
    public Task RegisterClusterAsync(ClusterRegistration cluster, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return _clusters.ReplaceOneAsync(
            c => c.Key == cluster.Key, cluster, new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>All registered clusters.</summary>
    public async Task<IReadOnlyList<ClusterRegistration>> ListClustersAsync(CancellationToken cancellationToken = default) =>
        await _clusters.Find(FilterDefinition<ClusterRegistration>.Empty).ToListAsync(cancellationToken);
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformStoreTests`
Expected: PASS (both tests).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Platform/FirmRegistration.cs \
        Backend/Accounting101.Ledger.Api/Platform/ClusterRegistration.cs \
        Backend/Accounting101.Ledger.Api/Platform/PlatformStore.cs \
        Backend/Accounting101.Ledger.Api.Tests/PlatformStoreTests.cs
git commit -m "feat(tenancy): platform_control firm + cluster registry

Adds the platform tier above firm control DBs: FirmRegistration (firm ->
control DB + clusterKey + status), ClusterRegistration (clusterKey ->
connection string), and PlatformStore CRUD. Not yet wired into the request
path.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Cluster-keyed Mongo client factory

Add the seam that maps a cluster key to a pooled `IMongoClient`. The home cluster reuses the process client; any other registered cluster gets one lazily-built, cached client; an unknown key fails closed.

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Platform/IMongoClientFactory.cs`
- Create: `Backend/Accounting101.Ledger.Api/Platform/MongoClientFactory.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/MongoClientFactoryTests.cs`

**Interfaces:**
- Consumes: `PlatformStore.GetClusterAsync(string, CancellationToken)` (Task 2), the process `IMongoClient`.
- Produces:
  - `interface IMongoClientFactory { Task<IMongoClient> GetAsync(string clusterKey, CancellationToken ct = default); }`
  - `MongoClientFactory(IMongoClient homeClient, string homeClusterKey, PlatformStore platform)` — pre-seeds `homeClusterKey → homeClient`; resolves other keys through `PlatformStore`; throws `InvalidOperationException` for an unregistered key.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/MongoClientFactoryTests.cs`:

```csharp
using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MongoClientFactoryTests
{
    [Fact]
    public async Task Home_key_returns_process_client_registered_key_is_cached_unknown_throws()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string conn = runner.ConnectionString;
        IMongoClient home = new MongoClient(conn);

        PlatformStore platform = new(home.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
        await platform.RegisterClusterAsync(new ClusterRegistration { Key = "cluster-2", ConnectionString = conn });

        MongoClientFactory factory = new(home, "default", platform);

        // Home key returns the exact process client (no second pool to the same server).
        Assert.Same(home, await factory.GetAsync("default"));

        // A registered non-home cluster gets its own client, cached across calls.
        IMongoClient a = await factory.GetAsync("cluster-2");
        IMongoClient b = await factory.GetAsync("cluster-2");
        Assert.Same(a, b);
        Assert.NotSame(home, a);

        // An unregistered key fails closed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetAsync("nope"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter MongoClientFactoryTests`
Expected: FAIL — compile error, `IMongoClientFactory` / `MongoClientFactory` do not exist.

- [ ] **Step 3: Create the interface**

Create `Backend/Accounting101.Ledger.Api/Platform/IMongoClientFactory.cs`:

```csharp
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Resolves a cluster key to the pooled <see cref="IMongoClient"/> for that cluster. This is the seam
/// that lets firms spread across multiple Atlas clusters later without the engine knowing clusters exist:
/// today every firm resolves to the home cluster; adding cluster #2 is a registry row, not a code change.
/// </summary>
public interface IMongoClientFactory
{
    Task<IMongoClient> GetAsync(string clusterKey, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Create the implementation**

Create `Backend/Accounting101.Ledger.Api/Platform/MongoClientFactory.cs`:

```csharp
using System.Collections.Concurrent;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Pools one <see cref="IMongoClient"/> per cluster key. The home cluster reuses the process client
/// (pre-seeded in the constructor) so we never open a second pool to the same server; every other key is
/// resolved through <see cref="PlatformStore"/> and its client is built once and cached. An unregistered
/// key throws — the factory refuses to invent a connection.
/// </summary>
public sealed class MongoClientFactory : IMongoClientFactory
{
    private readonly PlatformStore _platform;
    private readonly ConcurrentDictionary<string, IMongoClient> _clients = new();

    public MongoClientFactory(IMongoClient homeClient, string homeClusterKey, PlatformStore platform)
    {
        ArgumentNullException.ThrowIfNull(homeClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeClusterKey);
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _clients[homeClusterKey] = homeClient;
    }

    public async Task<IMongoClient> GetAsync(string clusterKey, CancellationToken cancellationToken = default)
    {
        if (_clients.TryGetValue(clusterKey, out IMongoClient? cached))
            return cached;

        ClusterRegistration? cluster = await _platform.GetClusterAsync(clusterKey, cancellationToken);
        if (cluster is null)
            throw new InvalidOperationException($"No cluster registered for key '{clusterKey}'.");

        // GetOrAdd collapses a concurrent double-build to a single cached client; any loser is discarded
        // unused (a MongoClient with no operations holds no connections).
        return _clients.GetOrAdd(clusterKey, new MongoClient(cluster.ConnectionString));
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter MongoClientFactoryTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Platform/IMongoClientFactory.cs \
        Backend/Accounting101.Ledger.Api/Platform/MongoClientFactory.cs \
        Backend/Accounting101.Ledger.Api.Tests/MongoClientFactoryTests.cs
git commit -m "feat(tenancy): cluster-keyed IMongoClient factory

Adds IMongoClientFactory + MongoClientFactory: home cluster reuses the
process client, other registered clusters are pooled and cached, unknown
keys fail closed. The seam for spreading firms across clusters later.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: DI wiring + default-cluster startup seeder

Register the platform services as singletons and add one idempotent hosted service that records the home (`"default"`) cluster in the registry on startup, then call the extension from `AddLedgerEngine`. This is the integration capstone: after boot, the default cluster resolves and the factory returns the process client for it.

**Files:**
- Create: `Backend/Accounting101.Ledger.Api/Platform/PlatformClusterSeeder.cs`
- Create: `Backend/Accounting101.Ledger.Api/Hosting/PlatformRegistryExtensions.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/PlatformRegistryWiringTests.cs`

**Interfaces:**
- Consumes: `PlatformStore` (Task 2), `IMongoClientFactory`/`MongoClientFactory` (Task 3), the `IMongoClient` singleton registered by `AddLedgerEngine`, `IConfiguration` keys `Mongo:ConnectionString`, `Mongo:PlatformDatabase` (default `"platform_control"`), `Mongo:ClusterKey` (default `"default"`).
- Produces: `IServiceCollection AddPlatformRegistry(this IServiceCollection, IConfiguration)`; a `PlatformClusterSeeder : IHostedService` that upserts the home cluster row. After `AddLedgerEngine`, `PlatformStore`, `IMongoClientFactory`, and the seeder are resolvable.

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Api.Tests/PlatformRegistryWiringTests.cs`:

```csharp
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformRegistryWiringTests
{
    [Fact]
    public async Task AddPlatformRegistry_seeds_default_cluster_and_factory_resolves_home_client()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string conn = runner.ConnectionString;
        string platformDb = "platform_" + Guid.NewGuid().ToString("N");

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:ConnectionString"] = conn,
            ["Mongo:PlatformDatabase"] = platformDb,
        }).Build();

        ServiceCollection services = new();
        services.AddSingleton<IMongoClient>(new MongoClient(conn));
        services.AddPlatformRegistry(config);
        await using ServiceProvider sp = services.BuildServiceProvider();

        // Run the hosted seeder(s), as the host would on startup.
        foreach (IHostedService hosted in sp.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        PlatformStore platform = sp.GetRequiredService<PlatformStore>();
        ClusterRegistration? def = await platform.GetClusterAsync("default");
        Assert.NotNull(def);
        Assert.Equal(conn, def!.ConnectionString);

        IMongoClientFactory factory = sp.GetRequiredService<IMongoClientFactory>();
        IMongoClient home = sp.GetRequiredService<IMongoClient>();
        Assert.Same(home, await factory.GetAsync("default"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformRegistryWiringTests`
Expected: FAIL — compile error, `AddPlatformRegistry` and `PlatformClusterSeeder` do not exist.

- [ ] **Step 3: Create the startup seeder**

Create `Backend/Accounting101.Ledger.Api/Platform/PlatformClusterSeeder.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Records the home cluster (the process connection string, under the home cluster key) in the platform
/// registry on startup, idempotently. This makes the home cluster discoverable through the same registry
/// as any future cluster, so tooling and the resolver treat "default" like every other key.
/// </summary>
public sealed class PlatformClusterSeeder(PlatformStore platform, IConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        string key = configuration["Mongo:ClusterKey"] ?? "default";
        string connectionString = configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27017";
        return platform.RegisterClusterAsync(
            new ClusterRegistration { Key = key, ConnectionString = connectionString }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 4: Create the DI extension**

Create `Backend/Accounting101.Ledger.Api/Hosting/PlatformRegistryExtensions.cs`:

```csharp
using Accounting101.Ledger.Api.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Hosting;

/// <summary>
/// Registers the platform-registry tier: the <see cref="PlatformStore"/> over the platform control DB,
/// the cluster-keyed <see cref="IMongoClientFactory"/>, and the startup seeder for the home cluster.
/// Additive — it does not alter how a request resolves a client database (that is a later phase).
/// </summary>
public static class PlatformRegistryExtensions
{
    public static IServiceCollection AddPlatformRegistry(this IServiceCollection services, IConfiguration configuration)
    {
        string platformDatabase = configuration["Mongo:PlatformDatabase"] ?? "platform_control";
        string homeClusterKey = configuration["Mongo:ClusterKey"] ?? "default";

        services.AddSingleton(sp =>
            new PlatformStore(sp.GetRequiredService<IMongoClient>().GetDatabase(platformDatabase)));

        services.AddSingleton<IMongoClientFactory>(sp =>
            new MongoClientFactory(
                sp.GetRequiredService<IMongoClient>(),
                homeClusterKey,
                sp.GetRequiredService<PlatformStore>()));

        services.AddHostedService<PlatformClusterSeeder>();

        return services;
    }
}
```

- [ ] **Step 5: Wire it into `AddLedgerEngine`**

In `Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs`, immediately after the `AdminAuditStore` singleton registration (the line
`services.AddSingleton(sp => new AdminAuditStore(sp.GetRequiredService<IMongoClient>().GetDatabase(controlDatabase)));`),
add:

```csharp
        // Platform registry tier (firms + clusters). Additive: does not change client resolution yet.
        services.AddPlatformRegistry(configuration);
```

- [ ] **Step 6: Run the wiring test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter PlatformRegistryWiringTests`
Expected: PASS.

- [ ] **Step 7: Run the full backend suite (wiring now runs on every app boot)**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: PASS — `AddLedgerEngine` now also registers the platform singletons + seeder; existing tests boot through `ApiFixture` and must be unaffected (the seeder only upserts a `clusters` doc into a `platform_control` DB in the shared Mongo).

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Platform/PlatformClusterSeeder.cs \
        Backend/Accounting101.Ledger.Api/Hosting/PlatformRegistryExtensions.cs \
        Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs \
        Backend/Accounting101.Ledger.Api.Tests/PlatformRegistryWiringTests.cs
git commit -m "feat(tenancy): wire platform registry into AddLedgerEngine

Adds AddPlatformRegistry (PlatformStore + IMongoClientFactory singletons +
PlatformClusterSeeder) and calls it from AddLedgerEngine. Startup idempotently
records the home 'default' cluster; the factory resolves it to the process
client. Additive; request-path resolution unchanged.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Phase 1 Done — Definition of Done

- `ClientRegistration` carries `Status`, `EnabledModules`, `CreatedUtc`, `ArchivedUtc` with legacy-safe defaults.
- `platform_control` has a working `PlatformStore` (firms + clusters CRUD).
- `IMongoClientFactory` resolves cluster keys to pooled clients (home reused, others cached, unknown fails closed).
- `AddLedgerEngine` registers all of it and seeds the `"default"` cluster on startup.
- Full backend suite green; nothing in the request path changed.

**Not in Phase 1 (later phases):** the `firmId` claim + auth, converting `ControlStore`/`AdminAuditStore` from singletons to firm-resolved, extending `IClientDatabaseResolver` to go firm→cluster→client, firm provisioning endpoints + per-firm capability-set seeding, module-entitlement enforcement at the `ModuleAccess` chokepoint, the usage/metering read, and the on-site one-firm collapse. Those are Phases 2–4 per the design spec (`docs/superpowers/specs/2026-07-04-multi-firm-atlas-tenancy-design.md`).
