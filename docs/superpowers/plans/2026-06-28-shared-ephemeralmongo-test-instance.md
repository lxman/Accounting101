# Shared EphemeralMongo Test Instance — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Share a single EphemeralMongo `mongod` per test process across all 8 fixtures so the integration suite stops flaking on concurrent replica-set elections.

**Architecture:** A new `Accounting101.TestSupport` library exposes a lazy static `SharedMongo` that starts exactly one `mongod` per process. Every fixture awaits it instead of starting its own and no longer disposes it; existing GUID database/collection isolation is unchanged. The replica set stays (transactions need it).

**Tech Stack:** .NET 10, xUnit 2.9.3, EphemeralMongo 3.2.0, MongoDB.Driver.

## Global Constraints

- New project `Accounting101.TestSupport` at the repo root; `TargetFramework net10.0`; references `EphemeralMongo` Version `3.2.0` only (SharedMongo touches no MongoDB.Driver types).
- `MongoRunnerOptions` stay exactly as today: `Version = MongoVersion.V8`, `UseSingleNodeReplicaSet = true`, `AdditionalArguments = ["--quiet"]`, `StandardErrorLogger = Console.Error.WriteLine`. The replica set must NOT be removed (transactions require it).
- No fixture may dispose the shared runner. No test logic, assertion, or product code changes — this is pure test-infra.
- Do NOT add `maxParallelThreads` / `CollectionBehavior` throttling — the shared instance is the fix.
- All GUID database/collection naming in fixtures stays exactly as-is (`control_<guid>`, `client_<guid>`, `journal_<guid>`, `ledger_proto_test`).
- Solution file is `Accounting101.slnx`; add the new project with `dotnet sln Accounting101.slnx add <path>`.
- Commit trailer, verbatim, on every commit:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## The 8 fixtures and their 6 test projects

| Fixture | Project | Shape |
|---|---|---|
| `ApiFixture` | `Backend/Accounting101.Ledger.Api.Tests` | plain `IAsyncLifetime` + own `WebApplicationFactory` field `_factory` |
| `MongoFixture` | `Backend/Accounting101.Ledger.Mongo.Tests` | plain `IAsyncLifetime`, fixed DB `ledger_proto_test` + GUID collections |
| `ReceivablesHostFixture` | `Modules/Receivables/Accounting101.Receivables.Tests` | extends `WebApplicationFactory<Program>`, has `_runner` + `_connectionString` |
| `DocumentStoreFixture` | `Modules/Receivables/Accounting101.Receivables.Tests` | plain `IAsyncLifetime`, local `mongo` var |
| `PayablesHostFixture` | `Modules/Payables/Accounting101.Payables.Tests` | extends `WebApplicationFactory<Program>`, has `_runner` + `_connectionString` |
| `PayablesDocumentStoreFixture` | `Modules/Payables/Accounting101.Payables.Tests` | plain `IAsyncLifetime`, local `mongo` var |
| `CashHostFixture` | `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests` | extends `WebApplicationFactory<Program>`, has `_runner` + `_connectionString` |
| `PayrollHostFixture` | `Modules/Payroll/Accounting101.Payroll.Tests` | extends `WebApplicationFactory<Program>`, has `_runner` + `_connectionString` |

(The spec said "five" projects; it is six — `Ledger.Mongo.Tests` is separate from `Ledger.Api.Tests`.)

ProjectReference relative path to add, per project (TestSupport lives at repo root):
- `Backend/Accounting101.Ledger.Api.Tests` → `..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj`
- `Backend/Accounting101.Ledger.Mongo.Tests` → `..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj`
- `Modules/Receivables/Accounting101.Receivables.Tests` → `..\..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj`
- `Modules/Payables/Accounting101.Payables.Tests` → `..\..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj`
- `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests` → `..\..\..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj`
- `Modules/Payroll/Accounting101.Payroll.Tests` → `..\..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj`

---

### Task 1: Create `Accounting101.TestSupport` with `SharedMongo`

**Files:**
- Create: `Accounting101.TestSupport/Accounting101.TestSupport.csproj`
- Create: `Accounting101.TestSupport/SharedMongo.cs`
- Modify: `Accounting101.slnx` (via `dotnet sln add`)

**Interfaces:**
- Produces (consumed by Tasks 2-7): `Accounting101.TestSupport.SharedMongo.InstanceAsync()` returning `Task<EphemeralMongo.IMongoRunner>` — the process-wide shared runner; callers read `.ConnectionString`.

- [ ] **Step 1: Create the project file**

`Accounting101.TestSupport/Accounting101.TestSupport.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="EphemeralMongo" Version="3.2.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create `SharedMongo`**

`Accounting101.TestSupport/SharedMongo.cs`:
```csharp
using EphemeralMongo;

namespace Accounting101.TestSupport;

/// <summary>
/// One shared EphemeralMongo replica set per test process. The <see cref="Lazy{T}"/> (default thread-safe
/// mode) guarantees exactly one <c>mongod</c> even when many test classes request it in parallel — which is
/// what eliminates the replica-set-election storm. Per-test isolation comes from GUID databases/collections
/// in the fixtures, so sharing one server is safe. Disposed once at process exit; never by a fixture.
/// </summary>
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

    /// <summary>The process-wide shared runner; fixtures consume its <c>ConnectionString</c>.</summary>
    public static Task<IMongoRunner> InstanceAsync() => Runner.Value;
}
```

- [ ] **Step 3: Add the project to the solution**

Run: `dotnet sln Accounting101.slnx add Accounting101.TestSupport/Accounting101.TestSupport.csproj`
Expected: "Project ... added to the solution."

- [ ] **Step 4: Build the new project**

Run: `dotnet build Accounting101.TestSupport/Accounting101.TestSupport.csproj --nologo`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add Accounting101.TestSupport/ Accounting101.slnx
git commit -m "$(cat <<'EOF'
test(support): add Accounting101.TestSupport with shared EphemeralMongo

SharedMongo lazily starts one mongod per test process (Lazy guarantees a
single replica-set election under parallel access) and disposes it at
process exit. Fixtures will consume InstanceAsync() instead of each
starting their own mongod.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Convert Receivables.Tests (2 fixtures) — the flake proof

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesHostFixture.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/DocumentStoreFixture.cs`

**Interfaces:**
- Consumes: `Accounting101.TestSupport.SharedMongo.InstanceAsync()` (Task 1).

- [ ] **Step 1: Add the ProjectReference**

In `Accounting101.Receivables.Tests.csproj`, inside the existing `<ItemGroup>` that holds `<ProjectReference>` entries, add:
```xml
    <ProjectReference Include="..\..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj" />
```

- [ ] **Step 2: Convert `ReceivablesHostFixture`**

Add `using Accounting101.TestSupport;` to the using block. Remove the field `private IMongoRunner _runner = null!;`. In `InitializeAsync`, replace:
```csharp
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };
        _runner = await MongoRunner.RunAsync(options);
        _connectionString = _runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
```
with:
```csharp
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        _connectionString = runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
```
In `DisposeAsync`, remove the `_runner?.Dispose();` line so it reads:
```csharp
    async Task IAsyncLifetime.DisposeAsync()
    {
        await ((IAsyncDisposable)this).DisposeAsync();
    }
```
(Keep `using EphemeralMongo;` — `IMongoRunner` is still referenced. Keep the `_connectionString` field.)

- [ ] **Step 3: Convert `DocumentStoreFixture`**

Add `using Accounting101.TestSupport;`. Remove the field `private IMongoRunner _runner = null!;`. In `InitializeAsync`, replace:
```csharp
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };
        _runner = await MongoRunner.RunAsync(options);
        IMongoClient mongo = new MongoClient(_runner.ConnectionString);
```
with:
```csharp
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);
```
Replace the whole `DisposeAsync` body so it no longer disposes the runner:
```csharp
    public Task DisposeAsync() => Task.CompletedTask;
```
(Keep `using EphemeralMongo;` — `IMongoRunner` is referenced.)

- [ ] **Step 4: Build**

Run: `dotnet build Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --nologo`
Expected: Build succeeded, 0 warnings (no unused-using warning), 0 errors.

- [ ] **Step 5: Run the full Receivables suite 3× — confirm green and no replica-set timeout**

Run (three times):
`dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --nologo`
Expected each run: `Passed! - Failed: 0` for the full project (118 tests). Previously this project intermittently failed 2–11 tests with replica-set election timeouts; all three runs must be clean. If any run shows a timeout, STOP and report it (do not retry past 3).

- [ ] **Step 6: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Tests/
git commit -m "$(cat <<'EOF'
test(receivables): consume shared EphemeralMongo instance

ReceivablesHostFixture + DocumentStoreFixture await SharedMongo.InstanceAsync()
instead of each starting a mongod, and no longer dispose the shared runner.
GUID database isolation unchanged. Full suite green across repeated runs.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Convert Payables.Tests (2 fixtures)

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/PayablesHostFixture.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/PayablesDocumentStoreFixture.cs`

**Interfaces:**
- Consumes: `SharedMongo.InstanceAsync()` (Task 1).

- [ ] **Step 1: Add the ProjectReference**

In `Accounting101.Payables.Tests.csproj`, inside the `<ProjectReference>` `<ItemGroup>`, add:
```xml
    <ProjectReference Include="..\..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj" />
```

- [ ] **Step 2: Convert `PayablesHostFixture`**

Add `using Accounting101.TestSupport;`. Remove the field `private IMongoRunner _runner = null!;`. In `InitializeAsync`, replace:
```csharp
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };
        _runner = await MongoRunner.RunAsync(options);
        _connectionString = _runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
```
with:
```csharp
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        _connectionString = runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
```
In `DisposeAsync`, remove `_runner?.Dispose();`:
```csharp
    async Task IAsyncLifetime.DisposeAsync()
    {
        await ((IAsyncDisposable)this).DisposeAsync();
    }
```

- [ ] **Step 3: Convert `PayablesDocumentStoreFixture`**

Add `using Accounting101.TestSupport;`. Remove `private IMongoRunner _runner = null!;`. In `InitializeAsync`, replace:
```csharp
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };
        _runner = await MongoRunner.RunAsync(options);
        IMongoClient mongo = new MongoClient(_runner.ConnectionString);
```
with:
```csharp
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);
```
Replace `DisposeAsync`:
```csharp
    public Task DisposeAsync() => Task.CompletedTask;
```

- [ ] **Step 4: Build**

Run: `dotnet build Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --nologo`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 5: Run the full Payables suite 2×**

Run (twice): `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --nologo`
Expected each: `Passed! - Failed: 0` (47 tests). If any timeout, STOP and report.

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Tests/
git commit -m "$(cat <<'EOF'
test(payables): consume shared EphemeralMongo instance

PayablesHostFixture + PayablesDocumentStoreFixture await SharedMongo
instead of starting their own mongod, and no longer dispose the shared
runner. GUID database isolation unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Convert Ledger.Api.Tests (`ApiFixture`)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
- Modify: `Backend/Accounting101.Ledger.Api.Tests/ApiFixture.cs`

**Interfaces:**
- Consumes: `SharedMongo.InstanceAsync()` (Task 1).

- [ ] **Step 1: Add the ProjectReference**

In `Accounting101.Ledger.Api.Tests.csproj`, inside the `<ProjectReference>` `<ItemGroup>`, add:
```xml
    <ProjectReference Include="..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj" />
```

- [ ] **Step 2: Convert `ApiFixture`**

Add `using Accounting101.TestSupport;`. Remove the field `private IMongoRunner _runner = null!;`. In `InitializeAsync`, replace:
```csharp
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };
        _runner = await MongoRunner.RunAsync(options);
        Mongo = new MongoClient(_runner.ConnectionString);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Mongo:ConnectionString", _runner.ConnectionString)
             .UseSetting("Mongo:ControlDatabase", ControlDatabase));
```
with:
```csharp
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string connectionString = runner.ConnectionString;
        Mongo = new MongoClient(connectionString);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Mongo:ConnectionString", connectionString)
             .UseSetting("Mongo:ControlDatabase", ControlDatabase));
```
In `DisposeAsync`, remove `_runner?.Dispose();`:
```csharp
    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --nologo`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 4: Run the full Ledger.Api suite 2×**

Run (twice): `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --nologo`
Expected each: `Passed! - Failed: 0`. If any timeout, STOP and report. (Pre-existing note: `ChartOfAccountsTests.Normal_side_matches_account_type` has been flagged as possibly pre-existing-failing elsewhere — if it fails, confirm it fails on `master` too via `git stash`/checkout before attributing it here; do not fix unrelated failures.)

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api.Tests/
git commit -m "$(cat <<'EOF'
test(ledger-api): consume shared EphemeralMongo instance

ApiFixture awaits SharedMongo instead of starting its own mongod and no
longer disposes the shared runner; the WebApplicationFactory still binds
the shared connection string + its GUID control database.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Convert Ledger.Mongo.Tests (`MongoFixture`)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo.Tests/Accounting101.Ledger.Mongo.Tests.csproj`
- Modify: `Backend/Accounting101.Ledger.Mongo.Tests/MongoFixture.cs`

**Interfaces:**
- Consumes: `SharedMongo.InstanceAsync()` (Task 1).

- [ ] **Step 1: Add the ProjectReference**

In `Accounting101.Ledger.Mongo.Tests.csproj`, inside the `<ProjectReference>` `<ItemGroup>`, add:
```xml
    <ProjectReference Include="..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj" />
```

- [ ] **Step 2: Convert `MongoFixture`**

Add `using Accounting101.TestSupport;`. Remove the field `private IMongoRunner _runner = null!;`. In `InitializeAsync`, replace:
```csharp
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,          // enables transactions / change streams
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };

        _runner = await MongoRunner.RunAsync(options);
        Database = new MongoClient(_runner.ConnectionString).GetDatabase("ledger_proto_test");
```
with:
```csharp
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        Database = new MongoClient(runner.ConnectionString).GetDatabase("ledger_proto_test");
```
Replace `DisposeAsync`:
```csharp
    public Task DisposeAsync() => Task.CompletedTask;
```
(The fixed `ledger_proto_test` database name stays — per-test isolation is the GUID collection from `NewStore()`, which is safe under a shared server.)

- [ ] **Step 3: Build**

Run: `dotnet build Backend/Accounting101.Ledger.Mongo.Tests/Accounting101.Ledger.Mongo.Tests.csproj --nologo`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 4: Run the full Ledger.Mongo suite 2×**

Run (twice): `dotnet test Backend/Accounting101.Ledger.Mongo.Tests/Accounting101.Ledger.Mongo.Tests.csproj --nologo`
Expected each: `Passed! - Failed: 0`. If any timeout, STOP and report.

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Mongo.Tests/
git commit -m "$(cat <<'EOF'
test(ledger-mongo): consume shared EphemeralMongo instance

MongoFixture awaits SharedMongo instead of starting its own mongod and no
longer disposes the shared runner; per-test isolation stays the GUID
collection from NewStore().

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Convert Banking.Cash.Tests (`CashHostFixture`)

**Files:**
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Accounting101.Banking.Cash.Tests.csproj`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashHostFixture.cs`

**Interfaces:**
- Consumes: `SharedMongo.InstanceAsync()` (Task 1).

- [ ] **Step 1: Add the ProjectReference**

In `Accounting101.Banking.Cash.Tests.csproj`, inside the `<ProjectReference>` `<ItemGroup>`, add:
```xml
    <ProjectReference Include="..\..\..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj" />
```

- [ ] **Step 2: Convert `CashHostFixture`**

Add `using Accounting101.TestSupport;`. Remove the field `private IMongoRunner _runner = null!;`. In `InitializeAsync`, replace:
```csharp
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };
        _runner = await MongoRunner.RunAsync(options);
        _connectionString = _runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
```
with:
```csharp
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        _connectionString = runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
```
In `DisposeAsync`, remove `_runner?.Dispose();`:
```csharp
    async Task IAsyncLifetime.DisposeAsync()
    {
        await ((IAsyncDisposable)this).DisposeAsync();
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Accounting101.Banking.Cash.Tests.csproj --nologo`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 4: Run the full Banking.Cash suite 2×**

Run (twice): `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Accounting101.Banking.Cash.Tests.csproj --nologo`
Expected each: `Passed! - Failed: 0`. If any timeout, STOP and report.

- [ ] **Step 5: Commit**

```bash
git add Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/
git commit -m "$(cat <<'EOF'
test(banking-cash): consume shared EphemeralMongo instance

CashHostFixture awaits SharedMongo instead of starting its own mongod and
no longer disposes the shared runner. GUID database isolation unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Convert Payroll.Tests (`PayrollHostFixture`) + solution-wide validation

**Files:**
- Modify: `Modules/Payroll/Accounting101.Payroll.Tests/Accounting101.Payroll.Tests.csproj`
- Modify: `Modules/Payroll/Accounting101.Payroll.Tests/PayrollHostFixture.cs`

**Interfaces:**
- Consumes: `SharedMongo.InstanceAsync()` (Task 1).

- [ ] **Step 1: Add the ProjectReference**

In `Accounting101.Payroll.Tests.csproj`, inside the `<ProjectReference>` `<ItemGroup>`, add:
```xml
    <ProjectReference Include="..\..\..\Accounting101.TestSupport\Accounting101.TestSupport.csproj" />
```

- [ ] **Step 2: Convert `PayrollHostFixture`**

Add `using Accounting101.TestSupport;`. Remove the field `private IMongoRunner _runner = null!;`. In `InitializeAsync`, replace:
```csharp
        MongoRunnerOptions options = new()
        {
            Version = MongoVersion.V8,
            UseSingleNodeReplicaSet = true,
            AdditionalArguments = ["--quiet"],
            StandardErrorLogger = Console.Error.WriteLine,
        };
        _runner = await MongoRunner.RunAsync(options);
        _connectionString = _runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
```
with:
```csharp
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        _connectionString = runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
```
In `DisposeAsync`, remove `_runner?.Dispose();`:
```csharp
    async Task IAsyncLifetime.DisposeAsync()
    {
        await ((IAsyncDisposable)this).DisposeAsync();
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build Modules/Payroll/Accounting101.Payroll.Tests/Accounting101.Payroll.Tests.csproj --nologo`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 4: Run the full Payroll suite 2×**

Run (twice): `dotnet test Modules/Payroll/Accounting101.Payroll.Tests/Accounting101.Payroll.Tests.csproj --nologo`
Expected each: `Passed! - Failed: 0`. If any timeout, STOP and report.

- [ ] **Step 5: Confirm no fixture still starts or disposes its own runner**

Run: `grep -rn "MongoRunner.RunAsync\|_runner" Backend Modules --include=*.cs`
Expected: NO matches in any `*.Tests` fixture (the only remaining `MongoRunner`/runner references should be none in test fixtures; `Accounting101.TestSupport/SharedMongo.cs` and `Backend/Accounting101.Ledger.Benchmark/Program.cs` are out of this grep's scope or expected). If any test fixture still references `_runner` or `MongoRunner.RunAsync`, a conversion was missed — fix it before continuing.

- [ ] **Step 6: Solution-wide validation — all six affected suites in one run**

Run: `dotnet test Accounting101.slnx --nologo`
Expected: all projects `Passed! - Failed: 0`. This is the real proof: under a full-solution run every integration project starts exactly one `mongod` (one per process), so the election storm that caused the flake is gone. If a known-pre-existing unrelated failure appears (e.g. `ChartOfAccountsTests.Normal_side_matches_account_type`), confirm it also fails on `master` before attributing it here; do not fix unrelated failures in this branch.

- [ ] **Step 7: Commit**

```bash
git add Modules/Payroll/Accounting101.Payroll.Tests/
git commit -m "$(cat <<'EOF'
test(payroll): consume shared EphemeralMongo instance

PayrollHostFixture awaits SharedMongo instead of starting its own mongod
and no longer disposes the shared runner. Completes the conversion: every
integration project now starts exactly one mongod per process, ending the
replica-set-election flake. GUID database isolation unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- New `Accounting101.TestSupport` project + `SharedMongo` lazy static + ProcessExit disposal → Task 1. ✓
- All 8 fixtures consume `SharedMongo.InstanceAsync()` and stop disposing the runner → Tasks 2-7 (Receivables ×2, Payables ×2, ApiFixture, MongoFixture, Cash, Payroll). ✓
- GUID isolation unchanged; replica set kept; no throttling; no behavior change → Global Constraints + each task keeps naming/options. ✓
- Risk gate (server-global ops) → already verified during planning: only `DropDatabaseAsync` is in the excluded Benchmark project; Task 7 Step 5 re-greps for stray runner usage. ✓
- Validation: affected suites run repeatedly + a solution-wide run → Task 2 (3×, the flake proof), Tasks 3-7 (2× each), Task 7 Step 6 (solution-wide). ✓

**2. Placeholder scan:** No TBD/TODO; every edit shows exact before/after code; all commands explicit with expected output. ✓

**3. Type consistency:** `SharedMongo.InstanceAsync()` → `Task<IMongoRunner>` defined in Task 1 and consumed identically in every fixture (`IMongoRunner runner = await SharedMongo.InstanceAsync();`). All fixtures keep `using EphemeralMongo;` for the `IMongoRunner` type. Relative ProjectReference depths match each project's location (verified against existing `..\..\..\Backend\...`-style refs). ✓
