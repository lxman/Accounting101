# Shared EphemeralMongo Test Instance — Design

**Date:** 2026-06-28
**Status:** Approved (design)

## Problem

The integration test suite is flaky. Eight fixtures across five test projects each
start their own EphemeralMongo `mongod` as a single-node replica set:

- `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`
- `Backend/Accounting101.Ledger.Mongo.Tests/MongoFixture.cs`
- `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesHostFixture.cs`
- `Modules/Receivables/Accounting101.Receivables.Tests/DocumentStoreFixture.cs`
- `Modules/Payables/Accounting101.Payables.Tests/PayablesHostFixture.cs`
- `Modules/Payables/Accounting101.Payables.Tests/PayablesDocumentStoreFixture.cs`
- `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashHostFixture.cs`
- `Modules/Payroll/Accounting101.Payroll.Tests/PayrollHostFixture.cs`

xUnit's `IClassFixture<T>` creates **one fixture instance per test class**, so a
single project instantiates a fresh `mongod` for every E2E test class — a dozen
or more — and xUnit runs them in parallel. Each `mongod` must elect a single-node
replica set on startup; under that concurrent-spin-up load the elections time out,
producing intermittent failures (e.g. `Receivables.Tests` showed 2–11 transient
failures in `DocumentInvoiceStoreTests` and other classes that pass when re-run in
isolation).

Two constraints shape the fix:

1. **The replica set must stay.** The engine uses MongoDB transactions
   (`StartSession`/`WithTransaction` in `LedgerService`, `MongoJournalStore`,
   `MongoAuditLog`, etc.), which require a replica set. Dropping
   `UseSingleNodeReplicaSet` is not an option.
2. **Isolation is already by-database/collection, not by-instance.** Host and
   document fixtures use GUID-suffixed databases (`control_<guid>`,
   `client_<guid>`); `MongoFixture` uses a fixed database `ledger_proto_test` but
   isolates each test via a GUID-suffixed collection (`journal_<guid>`). Nothing
   relies on having a private server.

So the fix is "share one replica set," not "avoid the replica set."

## Goal

Eliminate the replica-set-election storm by sharing a single `mongod` per test
process, consumed by all eight fixtures, with the existing GUID-based isolation
unchanged. Transactions keep working; test parallelism is preserved; no product
or test *behavior* changes — only where Mongo comes from.

## Scope

**In scope:** the eight fixtures above; a new shared test-support project; the
five test `.csproj` files; the solution file.

**Out of scope:** product code; test logic/assertions; the benchmark harness
(which deliberately uses a real/native Mongo); reducing xUnit parallelism (the
shared instance makes that unnecessary).

## Decisions

- **Ownership/lifecycle:** a **lazy static singleton**. A
  `static readonly Lazy<Task<IMongoRunner>>` starts exactly one `mongod` on first
  access; the default thread-safe `Lazy` mode guarantees a single `RunAsync` even
  when parallel test classes request it simultaneously — this is the mechanism
  that collapses the storm. No new NuGet dependency. The runner is disposed once
  via an `AppDomain.CurrentDomain.ProcessExit` hook; fixtures never dispose it.
- **Placement:** a new shared library **`Accounting101.TestSupport`** referenced
  by all five test projects — one implementation, no drift.
- **Replica set stays** (`UseSingleNodeReplicaSet = true`), options otherwise
  identical to today (`MongoVersion.V8`, `--quiet`, stderr logger).

## Architecture

```
Accounting101.TestSupport (new net10.0 lib; refs EphemeralMongo + MongoDB.Driver)
  └── SharedMongo  — static; one IMongoRunner per test process, lazily started
        ▲ ProjectReference
   ┌────┴───────────────────────────────────────────────────────────┐
   ApiFixture  MongoFixture  *HostFixture (×4)  *DocumentStoreFixture (×2)
   each: await SharedMongo.InstanceAsync() → use runner.ConnectionString
         GUID databases / collections unchanged
         DisposeAsync: dispose the host (if any), NEVER the runner
```

### `SharedMongo`

```csharp
using EphemeralMongo;

namespace Accounting101.TestSupport;

/// <summary>One shared EphemeralMongo replica set per test process. The Lazy guarantees a single
/// mongod even under parallel first-access from many test classes — which is what stops the
/// replica-set-election storm. Per-test isolation comes from GUID databases/collections in the
/// fixtures, so sharing the server is safe. Disposed once at process exit; never by a fixture.</summary>
public static class SharedMongo
{
    private static readonly Lazy<Task<IMongoRunner>> Runner = new(StartAsync);

    private static async Task<IMongoRunner> StartAsync()
    {
        IMongoRunner runner = await MongoRunner.RunAsync(new MongoRunnerOptions
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => runner.Dispose();
        return runner;
    }

    /// <summary>The process-wide shared runner; its <c>ConnectionString</c> is what fixtures consume.</summary>
    public static Task<IMongoRunner> InstanceAsync() => Runner.Value;
}
```

### Fixture change (uniform across all eight)

Each fixture's `InitializeAsync` replaces its private runner startup:

```csharp
// before
_runner = await MongoRunner.RunAsync(options);
Mongo = new MongoClient(_runner.ConnectionString);

// after
IMongoRunner runner = await SharedMongo.InstanceAsync();
Mongo = new MongoClient(runner.ConnectionString);
```

and `DisposeAsync` stops disposing the runner:

```csharp
// before
public Task DisposeAsync() { _factory?.Dispose(); _runner?.Dispose(); return Task.CompletedTask; }
// after
public Task DisposeAsync() { _factory?.Dispose(); return Task.CompletedTask; }   // runner is shared; never disposed here
```

The `_runner` field and the local `MongoRunnerOptions` are removed from each
fixture (the options now live once in `SharedMongo`). All GUID database/collection
naming, control-store seeding, manifest wiring, and `WebApplicationFactory`
configuration stay exactly as they are. `MongoFixture` keeps its
`ledger_proto_test` database name (collection-level GUID isolation is preserved
under sharing).

## Data flow

```
test class N starts → fixture.InitializeAsync → SharedMongo.InstanceAsync()
   first caller in the process: starts the one mongod (Lazy)
   every later caller: gets the same runner instantly (no new election)
→ fixture builds MongoClient(runner.ConnectionString) + its own GUID control/client DBs
→ tests run isolated by GUID DB/collection
→ fixture.DisposeAsync disposes only its host; runner lives on
→ process exit → ProcessExit hook disposes the one runner
```

Each test **project** is its own process, so a full solution run starts ~5
`mongod` (one per integration project) instead of dozens.

## Implementation notes / risk to verify

- **Server-global operations:** confirm no fixture or test performs a
  server-global op that would behave differently under a shared server —
  `ListDatabaseNames`, an unscoped `DropDatabase`, or assertions that the server
  contains only one database. The GUID-database design implies it is clean; the
  implementation must grep the test projects (`ListDatabaseNames`,
  `DropDatabase`, `ListDatabases`) and confirm before relying on it. If a real
  global op exists, scope it to the fixture's own GUID databases.
- **EphemeralMongo cleanup:** the `ProcessExit` hook disposes the runner; even
  without it, EphemeralMongo terminates its child `mongod` when the test host
  process exits. The hook makes teardown explicit.
- **No parallelism throttling:** do not add `maxParallelThreads` or
  `CollectionBehavior` limits — the shared instance is the fix; throttling would
  only mask it and slow the suite.

## Success criteria

- `Accounting101.TestSupport` builds and is referenced by all five test projects.
- All eight fixtures consume `SharedMongo.InstanceAsync()` and none dispose the
  runner.
- Each affected full suite passes, run **2–3× consecutively**, with no
  replica-set-election timeout — in particular the previously-flaky
  `Accounting101.Receivables.Tests` full run.
- No product code and no test assertions changed.
