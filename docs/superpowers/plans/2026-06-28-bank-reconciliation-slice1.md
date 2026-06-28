# Bank Reconciliation — Slice 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fill the empty `Accounting101.Banking.Reconciliation` stub into a working read-only bank-reconciliation core: record a bank statement, start a reconciliation, clear/unclear ledger cash entries, and read a worksheet that computes the cleared-method difference + balanced verdict.

**Architecture:** A new module mirroring the Cash module's one-step pattern, but read-only on the ledger (reads entries-by-account + as-of cash balance; posts nothing). Three projects: domain (records + math + seams), `.Api` (endpoints + read ledger client + registration), `.Tests`.

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs, xUnit, the engine's `IDocumentStore` + `ModuleManifest`, EphemeralMongo for E2E.

## Global Constraints

- New code only under `Modules/Banking/Reconciliation/`. Module key is `"reconciliation"`.
- **Slice 1 performs NO GL mutation** — the ledger seam is read-only (`GetEntriesTouchingAccountAsync`, `GetCashBalanceAsync`); no `PostAsync`/credential.
- Cleared-method math (exact): per entry `CashEffect` = Σ over its lines on the cash account of (`Debit` → `+Amount`, `Credit` → `−Amount`); `clearedTotal` = Σ CashEffect of cleared entries; `reconciledDifference = ClosingBalance − (OpeningBalance + clearedTotal)`; `balanced ⇔ reconciledDifference == 0m`.
- Worksheet entries = ledger entries touching the cash account with `Status == "Active"` AND `Posting == "Posted"` AND `EffectiveDate <= StatementDate`.
- Error mapping mirrors Cash: `ArgumentException` → 422; `InvalidOperationException` → 409; missing `cashAccountId` list filter → 400.
- Statement validation: lines non-empty AND `OpeningBalance + Σ(Lines.Amount) == ClosingBalance`, else `ArgumentException`.
- `complete` flips to Completed only when balanced, else `InvalidOperationException`.
- Money is `decimal`. Commit trailer, verbatim, on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Confirmed contracts (mirror these)

- `IDocumentStore` (Ledger.Contracts): evidentiary `CreateAsync<T>` + `FinalizeAsync` (returns the gapless `long` sequence); plain `PutAsync<T>(clientId, collection, id, body, tags)` (create+overwrite, caller-supplied id) + `DeleteAsync`; universal `GetAsync<T>` → `DocumentResult<T>(Guid Id, DocumentLifecycle State, long? Sequence, T Body)` and `QueryAsync<T>(clientId, collection, tagFilter)`; `NextNumberAsync(clientId, counterName)` → `long`.
- `ModuleManifestBuilder`: `.Evidentiary(collection)`, `.Plain(collection)`.
- `services.AddModule(new ModuleIdentity("reconciliation"), "Reconciliation", manifest => {...})` then `sp.GetRequiredKeyedService<IDocumentStore>("reconciliation")`.
- Engine reads: `GET /clients/{c}/entries?account={accountId}` → `List<EntryResponse>`; `GET /clients/{c}/trial-balance?asOf=yyyy-MM-dd` → `TrialBalanceResponse(DateOnly? AsOf, IReadOnlyList<AccountBalanceResponse> Accounts)`, `AccountBalanceResponse(Guid AccountId, decimal Balance)` (signed, debit-positive).
- `EntryResponse` (Ledger.Contracts): `Id`, `EffectiveDate`, `Status`, `Posting`, `IReadOnlyList<EntryLineResponse> Lines`, `Reference`, `SourceType` (among others); `EntryLineResponse(Guid AccountId, string Direction, decimal Amount, IReadOnlyDictionary<string,Guid> Dimensions, string? LineMemo)`.
- Host wiring: `Accounting101.Host/Program.cs` — module services registered around line 16 (`AddCash`), endpoints mapped around line 62 (`MapCashEndpoints`). Add `AddReconciliation` + `MapReconciliationEndpoints` alongside.
- Solution: `Accounting101.slnx` — add the 3 new projects with `dotnet sln Accounting101.slnx add <path>`.

---

### Task 1: Domain project — records, seams, and the cleared-method math (TDD on the math)

**Files:**
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/Accounting101.Banking.Reconciliation.csproj`
- Create: `…/Reconciliation/BankStatement.cs`, `Reconciliation.cs`, `Bodies.cs`, `Views.cs`, `Ports.cs`, `ReconciliationMath.cs`
- Delete: `…/Reconciliation/ReconciliationModule.cs` (replace the empty stub)
- Create (tests): a new test project is built in Task 5; the math unit test for THIS task goes in a temporary local test — instead, put the math tests in Task 5's project. **To keep Task 1 self-testing, add the math test here in a minimal test project.** (See Step 1.)

**Interfaces:**
- Produces: the domain records, `IBankStatementStore`, `IReconciliationStore`, `IReconciliationLedgerReader`, and `ReconciliationMath` (consumed by Tasks 2-5).

- [ ] **Step 1: Add the csproj reference + write the failing math test**

Replace `Accounting101.Banking.Reconciliation.csproj` contents:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- The reconciliation module (slice 1): bank statements + the cleared-method reconciliation core.
       Read-only on the ledger — reads entries-by-account + as-of cash balance; posts nothing. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\..\Backend\Accounting101.Ledger.Contracts\Accounting101.Ledger.Contracts.csproj" />
  </ItemGroup>

</Project>
```

The math test lives in the Task 5 test project, but Task 1's deliverable must be independently testable. Create the math test project now: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.6.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Accounting101.Banking.Reconciliation\Accounting101.Banking.Reconciliation.csproj" />
  </ItemGroup>
</Project>
```
(If the xunit.runner.visualstudio version above is unavailable, copy the exact `PackageReference` versions from `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Accounting101.Banking.Cash.Tests.csproj`.)

Create `…Reconciliation.Tests/ReconciliationMathTests.cs`:
```csharp
using Accounting101.Banking.Reconciliation;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class ReconciliationMathTests
{
    private static readonly Guid Cash = Guid.NewGuid();

    private static EntryResponse Entry(Guid id, string direction, decimal amount, string posting = "Posted", string status = "Active") =>
        new(id, 0, new DateOnly(2026, 1, 31), "Standard", status, posting, 1, null, null, null, null,
            [new EntryLineResponse(Cash, direction, amount, new Dictionary<string, Guid>(), null),
             new EntryLineResponse(Guid.NewGuid(), direction == "Debit" ? "Credit" : "Debit", amount, new Dictionary<string, Guid>(), null)]);

    [Fact]
    public void Cash_effect_is_positive_for_a_debit_to_cash_and_negative_for_a_credit()
    {
        Assert.Equal(100m, ReconciliationMath.CashEffect(Entry(Guid.NewGuid(), "Debit", 100m), Cash));
        Assert.Equal(-60m, ReconciliationMath.CashEffect(Entry(Guid.NewGuid(), "Credit", 60m), Cash));
    }

    [Fact]
    public void Cleared_total_sums_only_the_cleared_entries_cash_effects()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        EntryResponse[] entries = [Entry(a, "Debit", 100m), Entry(b, "Credit", 60m), Entry(c, "Debit", 25m)];
        decimal total = ReconciliationMath.ClearedTotal(entries, new HashSet<Guid> { a, b }, Cash);
        Assert.Equal(40m, total); // +100 − 60
    }

    [Fact]
    public void Reconciled_difference_is_closing_minus_opening_plus_cleared_and_balanced_at_zero()
    {
        // opening 0, cleared +40 → expected closing 40 balances.
        Assert.Equal(0m, ReconciliationMath.ReconciledDifference(0m, 40m, 40m));
        Assert.True(ReconciliationMath.IsBalanced(ReconciliationMath.ReconciledDifference(0m, 40m, 40m)));
        // a $5 bank-only fee not in the cleared total → difference −5, not balanced.
        Assert.Equal(-5m, ReconciliationMath.ReconciledDifference(0m, 35m, 40m));
        Assert.False(ReconciliationMath.IsBalanced(-5m));
    }
}
```

- [ ] **Step 2: Run, verify it FAILS (no `ReconciliationMath` yet)**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~ReconciliationMathTests" --nologo`
Expected: FAIL to compile (`ReconciliationMath` / records not defined).

- [ ] **Step 3: Delete the stub + create the domain files**

Delete `ReconciliationModule.cs`. Create:

`BankStatement.cs`:
```csharp
namespace Accounting101.Banking.Reconciliation;

/// <summary>A bank statement: immutable evidence of what the bank reported for a cash account over a period.</summary>
public sealed record BankStatement
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }               // "BST-{seq:D5}", assigned at finalize
    public required Guid CashAccountId { get; init; }
    public required DateOnly StatementDate { get; init; }
    public required decimal OpeningBalance { get; init; }
    public required decimal ClosingBalance { get; init; }
    public required IReadOnlyList<BankStatementLine> Lines { get; init; }
    public BankStatementStatus Status { get; init; } = BankStatementStatus.Posted;
}

/// <summary>One line on a bank statement. Amount is signed from the bank's perspective: + money into the
/// account (a deposit clearing), − money out (a payment clearing).</summary>
public sealed record BankStatementLine(DateOnly Date, decimal Amount, string Description, string? ExternalRef);

public enum BankStatementStatus { Posted, Void }
```

`Reconciliation.cs`:
```csharp
namespace Accounting101.Banking.Reconciliation;

/// <summary>A reconciliation of one bank statement: the working record of which ledger cash entries have
/// been cleared (matched as appearing on the bank). Editable while InProgress; flips to Completed when balanced.</summary>
public sealed record Reconciliation
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }               // "REC-{n:D5}"
    public required Guid CashAccountId { get; init; }
    public required Guid BankStatementId { get; init; }
    public required DateOnly StatementDate { get; init; }
    public ReconciliationStatus Status { get; init; } = ReconciliationStatus.InProgress;
    public required IReadOnlyList<Guid> ClearedEntryIds { get; init; }
}

public enum ReconciliationStatus { InProgress, Completed }
```

`Bodies.cs`:
```csharp
namespace Accounting101.Banking.Reconciliation;

/// <summary>Stored body of a bank statement (Number/Status/Id are derived, never sent).</summary>
public sealed record BankStatementBody(
    Guid CashAccountId, DateOnly StatementDate, decimal OpeningBalance, decimal ClosingBalance,
    IReadOnlyList<BankStatementLine> Lines);
```

`Ports.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Bank statements as evidentiary documents (one-step record; immutable).</summary>
public interface IBankStatementStore
{
    Task<BankStatement> RecordAsync(Guid clientId, BankStatementBody body, CancellationToken ct = default);
    Task<BankStatement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<BankStatement>> GetByAccountAsync(Guid clientId, Guid cashAccountId, CancellationToken ct = default);
}

/// <summary>Reconciliations as editable (plain) documents — the cleared set changes until completion.</summary>
public interface IReconciliationStore
{
    Task<Reconciliation> CreateAsync(Guid clientId, Guid cashAccountId, Guid bankStatementId, DateOnly statementDate, CancellationToken ct = default);
    Task SaveAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct = default);
    Task<Reconciliation?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default);
}

/// <summary>The module's READ-ONLY window onto the engine: entries touching a cash account, and that
/// account's as-of balance. Slice 1 never posts.</summary>
public interface IReconciliationLedgerReader
{
    Task<IReadOnlyList<EntryResponse>> GetEntriesTouchingAccountAsync(Guid clientId, Guid accountId, CancellationToken ct = default);
    Task<decimal> GetCashBalanceAsync(Guid clientId, Guid accountId, DateOnly asOf, CancellationToken ct = default);
}
```

`Views.cs`:
```csharp
namespace Accounting101.Banking.Reconciliation;

/// <summary>One ledger entry on the reconciliation worksheet.</summary>
public sealed record WorksheetEntry(Guid EntryId, DateOnly Date, string? Reference, string? SourceType, decimal CashEffect, bool Cleared);

/// <summary>The reconciliation worksheet: the statement, the cash-account entries through the statement date
/// with cleared flags, and the cleared-method totals + verdict.</summary>
public sealed record ReconciliationWorksheet(
    Reconciliation Reconciliation,
    BankStatement Statement,
    IReadOnlyList<WorksheetEntry> Entries,
    decimal BookBalance,
    decimal ClearedTotal,
    decimal ReconciledDifference,
    bool Balanced);
```

`ReconciliationMath.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>The cleared-method reconciliation math — pure functions over ledger entries and a statement.</summary>
public static class ReconciliationMath
{
    /// <summary>The signed book cash effect of one entry on the cash account: Debit +Amount, Credit −Amount,
    /// summed over the entry's lines that touch the cash account.</summary>
    public static decimal CashEffect(EntryResponse entry, Guid cashAccountId) =>
        entry.Lines.Where(l => l.AccountId == cashAccountId)
            .Sum(l => string.Equals(l.Direction, "Debit", StringComparison.OrdinalIgnoreCase) ? l.Amount : -l.Amount);

    /// <summary>Σ cash effect of the entries whose id is in <paramref name="clearedIds"/>.</summary>
    public static decimal ClearedTotal(IEnumerable<EntryResponse> entries, IReadOnlySet<Guid> clearedIds, Guid cashAccountId) =>
        entries.Where(e => clearedIds.Contains(e.Id)).Sum(e => CashEffect(e, cashAccountId));

    public static decimal ReconciledDifference(decimal openingBalance, decimal closingBalance, decimal clearedTotal) =>
        closingBalance - (openingBalance + clearedTotal);

    public static bool IsBalanced(decimal reconciledDifference) => reconciledDifference == 0m;
}
```

- [ ] **Step 4: Run, verify the math tests PASS**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~ReconciliationMathTests" --nologo`
Expected: 3/3 PASS.

- [ ] **Step 5: Add the two new projects to the solution + commit**

```bash
dotnet sln Accounting101.slnx add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/Accounting101.Banking.Reconciliation.csproj
dotnet sln Accounting101.slnx add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj
git add Modules/Banking/Reconciliation/ Accounting101.slnx
git commit -m "$(cat <<'EOF'
feat(reconciliation): domain records + cleared-method math (slice 1)

Replaces the empty Reconciliation stub with the domain (BankStatement,
Reconciliation, bodies, views), the store + read-ledger seams, and the pure
cleared-method math (CashEffect / ClearedTotal / ReconciledDifference),
unit-tested.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Document stores

**Files:**
- Create: `…/Reconciliation/DocumentBankStatementStore.cs`, `…/Reconciliation/DocumentReconciliationStore.cs`

**Interfaces:**
- Consumes: `IBankStatementStore`, `IReconciliationStore`, the domain records, `IDocumentStore` (Task 1 / Ledger.Contracts).

- [ ] **Step 1: Create `DocumentBankStatementStore`**

`DocumentBankStatementStore.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Persists bank statements through the engine's document store as evidentiary data: created
/// mutable then immediately finalized into an append-only numbered document. Number/status derived from
/// the envelope, never stored.</summary>
public sealed class DocumentBankStatementStore(IDocumentStore documents) : IBankStatementStore
{
    private const string Collection = "bank-statements";

    public async Task<BankStatement> RecordAsync(Guid clientId, BankStatementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<BankStatementBody>? result = await documents.GetAsync<BankStatementBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public async Task<BankStatement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        DocumentResult<BankStatementBody>? result = await documents.GetAsync<BankStatementBody>(clientId, Collection, id, ct);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<BankStatement>> GetByAccountAsync(Guid clientId, Guid cashAccountId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<BankStatementBody>> results =
            await documents.QueryAsync<BankStatementBody>(clientId, Collection, Tags(), ct);
        return results.Where(r => r.Body.CashAccountId == cashAccountId).Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags() => new();

    private static BankStatement Map(DocumentResult<BankStatementBody> r) => new()
    {
        Id = r.Id,
        Number = r.Sequence is { } seq ? $"BST-{seq:D5}" : null,
        CashAccountId = r.Body.CashAccountId,
        StatementDate = r.Body.StatementDate,
        OpeningBalance = r.Body.OpeningBalance,
        ClosingBalance = r.Body.ClosingBalance,
        Lines = r.Body.Lines,
        Status = r.State is DocumentLifecycle.Voided or DocumentLifecycle.Superseded ? BankStatementStatus.Void : BankStatementStatus.Posted,
    };
}
```

- [ ] **Step 2: Create `DocumentReconciliationStore`**

The reconciliation is a Plain document (editable). Its full `Reconciliation` record is the stored body, keyed by `Reconciliation.Id`; the number comes from a module counter at creation.

`DocumentReconciliationStore.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Persists reconciliations as plain (editable) documents: created with an empty cleared set and a
/// counter-assigned number, overwritten as the cleared set changes, read back by id.</summary>
public sealed class DocumentReconciliationStore(IDocumentStore documents) : IReconciliationStore
{
    private const string Collection = "reconciliations";

    public async Task<Reconciliation> CreateAsync(
        Guid clientId, Guid cashAccountId, Guid bankStatementId, DateOnly statementDate, CancellationToken ct = default)
    {
        long n = await documents.NextNumberAsync(clientId, "reconciliation", ct);
        Reconciliation reconciliation = new()
        {
            Id = Guid.NewGuid(),
            Number = $"REC-{n:D5}",
            CashAccountId = cashAccountId,
            BankStatementId = bankStatementId,
            StatementDate = statementDate,
            Status = ReconciliationStatus.InProgress,
            ClearedEntryIds = [],
        };
        await documents.PutAsync(clientId, Collection, reconciliation.Id, reconciliation, Tags(), ct);
        return reconciliation;
    }

    public Task SaveAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        return documents.PutAsync(clientId, Collection, reconciliation.Id, reconciliation, Tags(), ct);
    }

    public async Task<Reconciliation?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        DocumentResult<Reconciliation>? result = await documents.GetAsync<Reconciliation>(clientId, Collection, id, ct);
        return result?.Body;
    }

    private static Dictionary<string, string> Tags() => new();
}
```

- [ ] **Step 3: Build, then commit**

Run: `dotnet build Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/Accounting101.Banking.Reconciliation.csproj --nologo`
Expected: Build succeeded.

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/DocumentBankStatementStore.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/DocumentReconciliationStore.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): document stores for statements + reconciliations

BankStatement as evidentiary (create+finalize, BST- number); Reconciliation
as a plain editable document keyed by id with a REC- counter number.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `ReconciliationService` + service unit tests

**Files:**
- Create: `…/Reconciliation/ReconciliationService.cs`
- Create (test): `…Reconciliation.Tests/ReconciliationServiceTests.cs`, `…Reconciliation.Tests/Fakes.cs`

**Interfaces:**
- Consumes: the stores + reader + math (Tasks 1-2).
- Produces: `ReconciliationService` (consumed by Task 4 endpoints).

- [ ] **Step 1: Write the service**

`ReconciliationService.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>The reconciliation lifecycle: record a bank statement, start a reconciliation against it,
/// clear/unclear ledger cash entries, and read the worksheet. Read-only on the ledger — no posting.</summary>
public sealed class ReconciliationService(
    IBankStatementStore statements, IReconciliationStore reconciliations, IReconciliationLedgerReader ledger)
{
    public async Task<BankStatement> RecordStatementAsync(Guid clientId, BankStatementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Lines.Count == 0)
            throw new ArgumentException("A bank statement needs at least one line.");
        decimal expectedClosing = body.OpeningBalance + body.Lines.Sum(l => l.Amount);
        if (expectedClosing != body.ClosingBalance)
            throw new ArgumentException(
                $"Statement does not foot: opening {body.OpeningBalance:C} + lines {body.Lines.Sum(l => l.Amount):C} = {expectedClosing:C}, but closing is {body.ClosingBalance:C}.");
        return await statements.RecordAsync(clientId, body, ct);
    }

    public Task<BankStatement?> GetStatementAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        statements.GetAsync(clientId, id, ct);

    public Task<IReadOnlyList<BankStatement>> ListStatementsAsync(Guid clientId, Guid cashAccountId, CancellationToken ct = default) =>
        statements.GetByAccountAsync(clientId, cashAccountId, ct);

    public async Task<Reconciliation> StartReconciliationAsync(Guid clientId, Guid bankStatementId, CancellationToken ct = default)
    {
        BankStatement statement = await statements.GetAsync(clientId, bankStatementId, ct)
            ?? throw new ArgumentException($"Bank statement {bankStatementId} does not exist.");
        return await reconciliations.CreateAsync(clientId, statement.CashAccountId, statement.Id, statement.StatementDate, ct);
    }

    public async Task<ReconciliationWorksheet?> GetWorksheetAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default)
    {
        Reconciliation? reconciliation = await reconciliations.GetAsync(clientId, reconciliationId, ct);
        if (reconciliation is null) return null;
        return await BuildWorksheetAsync(clientId, reconciliation, ct);
    }

    public async Task<ReconciliationWorksheet> ClearAsync(Guid clientId, Guid reconciliationId, IReadOnlyList<Guid> entryIds, CancellationToken ct = default)
    {
        Reconciliation reconciliation = await RequireOpenAsync(clientId, reconciliationId, ct);
        IReadOnlyList<EntryResponse> eligible = await EligibleEntriesAsync(clientId, reconciliation, ct);
        var eligibleIds = eligible.Select(e => e.Id).ToHashSet();
        foreach (Guid id in entryIds)
            if (!eligibleIds.Contains(id))
                throw new ArgumentException($"Entry {id} is not a posted entry on this cash account dated on or before the statement date.");

        var cleared = reconciliation.ClearedEntryIds.Concat(entryIds).Distinct().ToList();
        Reconciliation updated = reconciliation with { ClearedEntryIds = cleared };
        await reconciliations.SaveAsync(clientId, updated, ct);
        return BuildWorksheet(updated, await statements.GetAsync(clientId, updated.BankStatementId, ct)!, eligible, await ledger.GetCashBalanceAsync(clientId, updated.CashAccountId, updated.StatementDate, ct));
    }

    public async Task<ReconciliationWorksheet> UnclearAsync(Guid clientId, Guid reconciliationId, IReadOnlyList<Guid> entryIds, CancellationToken ct = default)
    {
        Reconciliation reconciliation = await RequireOpenAsync(clientId, reconciliationId, ct);
        var remove = entryIds.ToHashSet();
        var cleared = reconciliation.ClearedEntryIds.Where(id => !remove.Contains(id)).ToList();
        Reconciliation updated = reconciliation with { ClearedEntryIds = cleared };
        await reconciliations.SaveAsync(clientId, updated, ct);
        return await BuildWorksheetAsync(clientId, updated, ct);
    }

    public async Task<Reconciliation> CompleteAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default)
    {
        Reconciliation reconciliation = await RequireOpenAsync(clientId, reconciliationId, ct);
        ReconciliationWorksheet worksheet = await BuildWorksheetAsync(clientId, reconciliation, ct);
        if (!worksheet.Balanced)
            throw new InvalidOperationException(
                $"Cannot complete: the reconciliation is not balanced (difference {worksheet.ReconciledDifference:C}). Clear the remaining items or record the bank-only adjustments first.");
        Reconciliation completed = reconciliation with { Status = ReconciliationStatus.Completed };
        await reconciliations.SaveAsync(clientId, completed, ct);
        return completed;
    }

    private async Task<Reconciliation> RequireOpenAsync(Guid clientId, Guid reconciliationId, CancellationToken ct)
    {
        Reconciliation reconciliation = await reconciliations.GetAsync(clientId, reconciliationId, ct)
            ?? throw new InvalidOperationException($"Reconciliation {reconciliationId} not found.");
        if (reconciliation.Status == ReconciliationStatus.Completed)
            throw new InvalidOperationException($"Reconciliation {reconciliationId} is already completed.");
        return reconciliation;
    }

    /// <summary>Active, posted entries touching the cash account, dated on or before the statement date.</summary>
    private async Task<IReadOnlyList<EntryResponse>> EligibleEntriesAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct) =>
        (await ledger.GetEntriesTouchingAccountAsync(clientId, reconciliation.CashAccountId, ct))
            .Where(e => e.Status == "Active" && e.Posting == "Posted" && e.EffectiveDate <= reconciliation.StatementDate)
            .ToList();

    private async Task<ReconciliationWorksheet> BuildWorksheetAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct)
    {
        BankStatement statement = (await statements.GetAsync(clientId, reconciliation.BankStatementId, ct))!;
        IReadOnlyList<EntryResponse> eligible = await EligibleEntriesAsync(clientId, reconciliation, ct);
        decimal bookBalance = await ledger.GetCashBalanceAsync(clientId, reconciliation.CashAccountId, reconciliation.StatementDate, ct);
        return BuildWorksheet(reconciliation, statement, eligible, bookBalance);
    }

    private static ReconciliationWorksheet BuildWorksheet(
        Reconciliation reconciliation, BankStatement statement, IReadOnlyList<EntryResponse> eligible, decimal bookBalance)
    {
        var clearedIds = reconciliation.ClearedEntryIds.ToHashSet();
        List<WorksheetEntry> entries = eligible
            .Select(e => new WorksheetEntry(e.Id, e.EffectiveDate, e.Reference, e.SourceType,
                ReconciliationMath.CashEffect(e, reconciliation.CashAccountId), clearedIds.Contains(e.Id)))
            .ToList();
        decimal clearedTotal = ReconciliationMath.ClearedTotal(eligible, clearedIds, reconciliation.CashAccountId);
        decimal difference = ReconciliationMath.ReconciledDifference(statement.OpeningBalance, statement.ClosingBalance, clearedTotal);
        return new ReconciliationWorksheet(reconciliation, statement, entries, bookBalance, clearedTotal, difference, ReconciliationMath.IsBalanced(difference));
    }
}
```

- [ ] **Step 2: Write the fakes + service tests**

`Fakes.cs`:
```csharp
using System.Collections.Concurrent;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

internal sealed class InMemoryBankStatementStore : IBankStatementStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), BankStatement> _store = new();
    private long _seq;
    public Task<BankStatement> RecordAsync(Guid clientId, BankStatementBody body, CancellationToken ct = default)
    {
        BankStatement s = new()
        {
            Id = Guid.NewGuid(), Number = $"BST-{Interlocked.Increment(ref _seq):D5}",
            CashAccountId = body.CashAccountId, StatementDate = body.StatementDate,
            OpeningBalance = body.OpeningBalance, ClosingBalance = body.ClosingBalance, Lines = body.Lines,
        };
        _store[(clientId, s.Id)] = s;
        return Task.FromResult(s);
    }
    public Task<BankStatement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, id)));
    public Task<IReadOnlyList<BankStatement>> GetByAccountAsync(Guid clientId, Guid cashAccountId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BankStatement>>(_store.Values.Where(s => s.CashAccountId == cashAccountId).ToList());
}

internal sealed class InMemoryReconciliationStore : IReconciliationStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Reconciliation> _store = new();
    private long _seq;
    public Task<Reconciliation> CreateAsync(Guid clientId, Guid cashAccountId, Guid bankStatementId, DateOnly statementDate, CancellationToken ct = default)
    {
        Reconciliation r = new()
        {
            Id = Guid.NewGuid(), Number = $"REC-{Interlocked.Increment(ref _seq):D5}",
            CashAccountId = cashAccountId, BankStatementId = bankStatementId, StatementDate = statementDate,
            Status = ReconciliationStatus.InProgress, ClearedEntryIds = [],
        };
        _store[(clientId, r.Id)] = r;
        return Task.FromResult(r);
    }
    public Task SaveAsync(Guid clientId, Reconciliation reconciliation, CancellationToken ct = default)
    { _store[(clientId, reconciliation.Id)] = reconciliation; return Task.CompletedTask; }
    public Task<Reconciliation?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, id)));
}

internal sealed class FakeLedgerReader(IReadOnlyList<EntryResponse> entries, decimal bookBalance) : IReconciliationLedgerReader
{
    public Task<IReadOnlyList<EntryResponse>> GetEntriesTouchingAccountAsync(Guid clientId, Guid accountId, CancellationToken ct = default) =>
        Task.FromResult(entries);
    public Task<decimal> GetCashBalanceAsync(Guid clientId, Guid accountId, DateOnly asOf, CancellationToken ct = default) =>
        Task.FromResult(bookBalance);
}
```

`ReconciliationServiceTests.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class ReconciliationServiceTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly DateOnly StmtDate = new(2026, 1, 31);

    private static EntryResponse CashEntry(Guid id, string direction, decimal amount) =>
        new(id, 0, new DateOnly(2026, 1, 15), "Standard", "Active", "Posted", 2, null, null, null, null,
            [new EntryLineResponse(Cash, direction, amount, new Dictionary<string, Guid>(), null),
             new EntryLineResponse(Guid.NewGuid(), direction == "Debit" ? "Credit" : "Debit", amount, new Dictionary<string, Guid>(), null)],
            SourceType: "Test", Reference: "R");

    private static (ReconciliationService svc, InMemoryBankStatementStore stmts, InMemoryReconciliationStore recs)
        Build(IReadOnlyList<EntryResponse> entries, decimal bookBalance)
    {
        InMemoryBankStatementStore stmts = new();
        InMemoryReconciliationStore recs = new();
        return (new ReconciliationService(stmts, recs, new FakeLedgerReader(entries, bookBalance)), stmts, recs);
    }

    private static BankStatementBody StatementBody(decimal opening, decimal closing, params (decimal amt, string desc)[] lines) =>
        new(Cash, StmtDate, opening, closing, lines.Select(l => new BankStatementLine(StmtDate, l.amt, l.desc, null)).ToList());

    [Fact]
    public async Task A_statement_that_does_not_foot_is_rejected()
    {
        (ReconciliationService svc, _, _) = Build([], 0m);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RecordStatementAsync(Guid.NewGuid(), StatementBody(0m, 999m, (100m, "dep"))));
    }

    [Fact]
    public async Task Clearing_the_matching_entries_balances_the_reconciliation()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid(), pay = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m), CashEntry(pay, "Credit", 40m)]; // net +60
        (ReconciliationService svc, _, _) = Build(entries, 60m);

        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 60m, (100m, "dep"), (-40m, "pay")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);

        ReconciliationWorksheet before = (await svc.GetWorksheetAsync(clientId, rec.Id))!;
        Assert.False(before.Balanced);                    // nothing cleared yet
        Assert.Equal(60m, before.ReconciledDifference);   // 60 − (0 + 0)

        ReconciliationWorksheet after = await svc.ClearAsync(clientId, rec.Id, [dep, pay]);
        Assert.Equal(60m, after.ClearedTotal);
        Assert.Equal(0m, after.ReconciledDifference);
        Assert.True(after.Balanced);

        Reconciliation done = await svc.CompleteAsync(clientId, rec.Id);
        Assert.Equal(ReconciliationStatus.Completed, done.Status);
    }

    [Fact]
    public async Task A_bank_only_residual_leaves_a_non_zero_difference_and_blocks_complete()
    {
        Guid clientId = Guid.NewGuid();
        Guid dep = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(dep, "Debit", 100m)];
        (ReconciliationService svc, _, _) = Build(entries, 100m);

        // Bank closing 95: a $5 bank fee the books don't have. Statement foots (0 + 100 − 5 = 95).
        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 95m, (100m, "dep"), (-5m, "fee")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);
        await svc.ClearAsync(clientId, rec.Id, [dep]);

        ReconciliationWorksheet ws = (await svc.GetWorksheetAsync(clientId, rec.Id))!;
        Assert.Equal(-5m, ws.ReconciledDifference);       // 95 − (0 + 100)
        Assert.False(ws.Balanced);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(clientId, rec.Id));
    }

    [Fact]
    public async Task Clearing_an_entry_not_on_the_cash_account_is_rejected()
    {
        Guid clientId = Guid.NewGuid();
        EntryResponse[] entries = [CashEntry(Guid.NewGuid(), "Debit", 100m)];
        (ReconciliationService svc, _, _) = Build(entries, 100m);
        BankStatement statement = await svc.RecordStatementAsync(clientId, StatementBody(0m, 100m, (100m, "dep")));
        Reconciliation rec = await svc.StartReconciliationAsync(clientId, statement.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.ClearAsync(clientId, rec.Id, [Guid.NewGuid()]));
    }
}
```

- [ ] **Step 3: Run the service + math tests, verify PASS**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --nologo`
Expected: all PASS (3 math + 4 service).

- [ ] **Step 4: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/ReconciliationService.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/ReconciliationServiceTests.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): ReconciliationService + service unit tests

Record/start/worksheet/clear/unclear/complete over the cleared-method math.
Statement must foot; clear only posted cash-account entries on/before the
statement date; complete only when balanced. Read-only on the ledger.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Api project — endpoints, read ledger client, registration, host wiring

**Files:**
- Create: `…/Accounting101.Banking.Reconciliation.Api/Accounting101.Banking.Reconciliation.Api.csproj`, `ReconciliationRequests.cs`, `ReconciliationEndpoints.cs`, `HttpReconciliationLedgerReader.cs`, `ReconciliationServiceExtensions.cs`
- Modify: `Accounting101.Host/Program.cs`, `Accounting101.Host/Accounting101.Host.csproj`, `Accounting101.slnx`

**Interfaces:**
- Consumes: `ReconciliationService`, the stores, `IReconciliationLedgerReader` (Tasks 1-3).
- Produces: `AddReconciliation` + `MapReconciliationEndpoints` (host wiring).

- [ ] **Step 1: Create the Api csproj**

`Accounting101.Banking.Reconciliation.Api.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- The reconciliation module's web tier: endpoints + the read-only loopback ledger client + DI wiring. -->
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
    <ProjectReference Include="..\Accounting101.Banking.Reconciliation\Accounting101.Banking.Reconciliation.csproj" />
    <ProjectReference Include="..\..\..\..\Backend\Accounting101.Ledger.Api\Accounting101.Ledger.Api.csproj" />
    <ProjectReference Include="..\..\..\..\Backend\Accounting101.Ledger.Contracts\Accounting101.Ledger.Contracts.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the request DTOs**

`ReconciliationRequests.cs`:
```csharp
using Accounting101.Banking.Reconciliation;

namespace Accounting101.Banking.Reconciliation.Api;

public sealed record RecordBankStatementRequest(
    Guid CashAccountId, DateOnly StatementDate, decimal OpeningBalance, decimal ClosingBalance,
    IReadOnlyList<BankStatementLineRequest> Lines);

public sealed record BankStatementLineRequest(DateOnly Date, decimal Amount, string Description, string? ExternalRef);

public sealed record StartReconciliationRequest(Guid BankStatementId);

public sealed record ClearRequest(IReadOnlyList<Guid> EntryIds);
```

- [ ] **Step 3: Create the read-only ledger client**

`HttpReconciliationLedgerReader.cs`:
```csharp
using System.Net.Http.Json;
using Accounting101.Banking.Reconciliation;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>Read-only loopback onto the engine: forwards the caller's bearer token to the entries-by-account
/// and trial-balance reads. No module credential — slice 1 never posts.</summary>
public sealed class HttpReconciliationLedgerReader(HttpClient http, IHttpContextAccessor context) : IReconciliationLedgerReader
{
    public async Task<IReadOnlyList<EntryResponse>> GetEntriesTouchingAccountAsync(Guid clientId, Guid accountId, CancellationToken ct = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?account={accountId}");
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(ct))!;
    }

    public async Task<decimal> GetCashBalanceAsync(Guid clientId, Guid accountId, DateOnly asOf, CancellationToken ct = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/trial-balance?asOf={asOf:yyyy-MM-dd}");
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        TrialBalanceResponse tb = (await response.Content.ReadFromJsonAsync<TrialBalanceResponse>(ct))!;
        return tb.Accounts.SingleOrDefault(a => a.AccountId == accountId)?.Balance ?? 0m;
    }

    private HttpRequestMessage Forwarded(HttpMethod method, string uri)
    {
        HttpRequestMessage request = new(method, uri);
        string? authorization = context.HttpContext?.Request.Headers.Authorization;
        if (!string.IsNullOrEmpty(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }
}
```

- [ ] **Step 4: Create the registration**

`ReconciliationServiceExtensions.cs`:
```csharp
using Accounting101.Banking.Reconciliation;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>Installs the reconciliation module: module identity + collection manifest (evidentiary
/// bank-statements, plain reconciliations), the document-store-backed stores and service, and the
/// read-only loopback ledger client. Slice 1 posts nothing, so no accounts provider or module credential
/// is used.</summary>
public static class ReconciliationServiceExtensions
{
    public static IServiceCollection AddReconciliation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("reconciliation"), "Reconciliation", manifest =>
        {
            manifest.Evidentiary("bank-statements");
            manifest.Plain("reconciliations");
        });

        services.AddScoped<IBankStatementStore>(sp => new DocumentBankStatementStore(sp.GetRequiredKeyedService<IDocumentStore>("reconciliation")));
        services.AddScoped<IReconciliationStore>(sp => new DocumentReconciliationStore(sp.GetRequiredKeyedService<IDocumentStore>("reconciliation")));
        services.AddScoped<ReconciliationService>();

        // Read-only loopback client; explicit name avoids the cross-module ILedgerClient short-name collision.
        services.AddHttpClient("ReconciliationLedgerClient", client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<IReconciliationLedgerReader, HttpReconciliationLedgerReader>();

        return services;
    }
}
```

- [ ] **Step 5: Create the endpoints**

`ReconciliationEndpoints.cs`:
```csharp
using Accounting101.Banking.Reconciliation;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>The reconciliation HTTP surface under /clients/{clientId}: bank statements (record/read) and
/// reconciliations (start/worksheet/clear/unclear/complete). Read-only on the ledger.</summary>
public static class ReconciliationEndpoints
{
    public static void MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/bank-statements", RecordStatement);
        clients.MapGet("/bank-statements/{id:guid}", GetStatement);
        clients.MapGet("/bank-statements", ListStatements);

        clients.MapPost("/reconciliations", StartReconciliation);
        clients.MapGet("/reconciliations/{id:guid}", GetWorksheet);
        clients.MapPost("/reconciliations/{id:guid}/clear", Clear);
        clients.MapPost("/reconciliations/{id:guid}/unclear", Unclear);
        clients.MapPost("/reconciliations/{id:guid}/complete", Complete);
    }

    private static async Task<IResult> RecordStatement(
        Guid clientId, RecordBankStatementRequest request, ReconciliationService service, CancellationToken ct)
    {
        try
        {
            BankStatement statement = await service.RecordStatementAsync(clientId,
                new BankStatementBody(request.CashAccountId, request.StatementDate, request.OpeningBalance, request.ClosingBalance,
                    request.Lines.Select(l => new BankStatementLine(l.Date, l.Amount, l.Description, l.ExternalRef)).ToList()),
                ct);
            return Results.Created($"/clients/{clientId}/bank-statements/{statement.Id}", statement);
        }
        catch (ArgumentException ex) // empty lines, or statement does not foot
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetStatement(Guid clientId, Guid id, ReconciliationService service, CancellationToken ct)
    {
        BankStatement? statement = await service.GetStatementAsync(clientId, id, ct);
        return statement is null ? Results.NotFound() : Results.Ok(statement);
    }

    private static async Task<IResult> ListStatements(
        Guid clientId, Guid? cashAccountId, ReconciliationService service, CancellationToken ct)
    {
        if (cashAccountId is null || cashAccountId == Guid.Empty)
            return Results.Problem("cashAccountId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        return Results.Ok(await service.ListStatementsAsync(clientId, cashAccountId.Value, ct));
    }

    private static async Task<IResult> StartReconciliation(
        Guid clientId, StartReconciliationRequest request, ReconciliationService service, CancellationToken ct)
    {
        try
        {
            Reconciliation reconciliation = await service.StartReconciliationAsync(clientId, request.BankStatementId, ct);
            return Results.Created($"/clients/{clientId}/reconciliations/{reconciliation.Id}", reconciliation);
        }
        catch (ArgumentException ex) // unknown statement
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetWorksheet(Guid clientId, Guid id, ReconciliationService service, CancellationToken ct)
    {
        ReconciliationWorksheet? worksheet = await service.GetWorksheetAsync(clientId, id, ct);
        return worksheet is null ? Results.NotFound() : Results.Ok(worksheet);
    }

    private static Task<IResult> Clear(Guid clientId, Guid id, ClearRequest request, ReconciliationService service, CancellationToken ct) =>
        MutateAsync(() => service.ClearAsync(clientId, id, request.EntryIds, ct));

    private static Task<IResult> Unclear(Guid clientId, Guid id, ClearRequest request, ReconciliationService service, CancellationToken ct) =>
        MutateAsync(() => service.UnclearAsync(clientId, id, request.EntryIds, ct));

    private static async Task<IResult> MutateAsync(Func<Task<ReconciliationWorksheet>> op)
    {
        try { return Results.Ok(await op()); }
        catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity); }
        catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }

    private static async Task<IResult> Complete(Guid clientId, Guid id, ReconciliationService service, CancellationToken ct)
    {
        try { return Results.Ok(await service.CompleteAsync(clientId, id, ct)); }
        catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }
}
```

- [ ] **Step 6: Wire into the host + solution**

In `Accounting101.Host/Program.cs`, add after the `AddCash` line (~line 16):
```csharp
builder.Services.AddReconciliation(builder.Configuration);
```
and after the `MapCashEndpoints()` line (~line 62):
```csharp
app.MapReconciliationEndpoints();
```
(Add `using Accounting101.Banking.Reconciliation.Api;` if the host does not use a global-usings/implicit pattern that already covers it — check the top of Program.cs and match how `AddCash`/`MapCashEndpoints` are referenced.)

In `Accounting101.Host/Accounting101.Host.csproj`, add a ProjectReference next to the Cash.Api one:
```xml
    <ProjectReference Include="..\Modules\Banking\Reconciliation\Accounting101.Banking.Reconciliation.Api\Accounting101.Banking.Reconciliation.Api.csproj" />
```
(Confirm the relative path matches the existing `..\Modules\Banking\Cash\Accounting101.Banking.Cash.Api\...` reference's depth.)

Add the Api project to the solution:
```bash
dotnet sln Accounting101.slnx add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/Accounting101.Banking.Reconciliation.Api.csproj
```

- [ ] **Step 7: Build the host, verify it composes**

Run: `dotnet build Accounting101.Host/Accounting101.Host.csproj --nologo`
Expected: Build succeeded (the module registers; no DI errors at build time).

- [ ] **Step 8: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ \
        Accounting101.Host/Program.cs Accounting101.Host/Accounting101.Host.csproj Accounting101.slnx
git commit -m "$(cat <<'EOF'
feat(reconciliation): Api endpoints + read-only ledger client + host wiring

Bank-statement + reconciliation endpoints (record/start/worksheet/clear/
unclear/complete), a bearer-forwarding read-only ledger client (entries by
account + trial-balance), AddReconciliation registration, and host wiring.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: E2E host fixture + tests

**Files:**
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj` (add the host/E2E references)
- Create: `…Reconciliation.Tests/ReconciliationHostFixture.cs`, `…Reconciliation.Tests/ReconciliationE2eTests.cs`

**Interfaces:**
- Consumes: the full module + host (Tasks 1-4); the Cash module (to create real cash entries to reconcile).

- [ ] **Step 1: Extend the test csproj for E2E**

Add to `Accounting101.Banking.Reconciliation.Tests.csproj` the package + project references that `CashHostFixture` needs (copy the exact set from `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Accounting101.Banking.Cash.Tests.csproj`): `Microsoft.AspNetCore.Mvc.Testing`, `EphemeralMongo`, the shared-Mongo `Accounting101.TestSupport` project, the `Accounting101.Host` project, the engine `Accounting101.Ledger.Api`/`.Contracts` projects, the Cash module (`Accounting101.Banking.Cash` + `.Api`), and this module's `.Api`. Match versions to the Cash test csproj exactly.

- [ ] **Step 2: Create the host fixture**

`ReconciliationHostFixture.cs` — mirror `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashHostFixture.cs` exactly (it already boots the full host with all modules, including the now-registered Reconciliation module via the shared `Program`), but additionally repoint the `"ReconciliationLedgerClient"` named loopback client at the test server alongside `"CashLedgerClient"`. Expose the configured `CashAccountId` and a deposit line account id (e.g. `MembersCapitalAccountId`) and a disbursement line account (`InterestExpenseAccountId`) — the same ids `CashHostFixture` already exposes. Reuse its `SeedSodClientAsync()` and `SharedMongo` usage verbatim. (Concretely: copy `CashHostFixture.cs`, rename the class to `ReconciliationHostFixture`, change the namespace to `Accounting101.Banking.Reconciliation.Tests`, and add this line inside the `ConfigureTestServices` block where the other named clients are repointed:
```csharp
services.AddHttpClient("ReconciliationLedgerClient", c => c.BaseAddress = new Uri("http://localhost"))
        .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
```
)

- [ ] **Step 3: Create the E2E test**

`ReconciliationE2eTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Cash.Api;
using Accounting101.Banking.Reconciliation;
using Accounting101.Banking.Reconciliation.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>End-to-end through the real host: post real cash entries via the Cash module, record a matching
/// bank statement, then reconcile — clearing the entries balances the worksheet and lets complete succeed;
/// a bank-only residual leaves a non-zero difference and blocks complete.</summary>
public sealed class ReconciliationE2eTests(ReconciliationHostFixture fixture) : IClassFixture<ReconciliationHostFixture>
{
    private static async Task SetUpChartAsync(HttpClient controller, Guid clientId, ReconciliationHostFixture f)
    {
        await PutAccountAsync(controller, clientId, f.CashAccountId, "1000", "Cash", "Asset");
        await PutAccountAsync(controller, clientId, f.MembersCapitalAccountId, "3000", "Members Capital", "Equity");
        await PutAccountAsync(controller, clientId, f.InterestExpenseAccountId, "5000", "Interest Expense", "Expense");
    }

    private static Task PutAccountAsync(HttpClient http, Guid clientId, Guid id, string number, string name, string type) =>
        http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", new AccountRequest { Number = number, Name = name, Type = type })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>($"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Clearing_the_posted_cash_entries_balances_the_reconciliation_and_completes()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly date = new(2026, 1, 20);
        DateOnly stmtDate = new(2026, 1, 31);

        // A real deposit (Dr Cash 100) and disbursement (Cr Cash 40), both approved → posted.
        CashDeposit dep = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits",
                new RecordCashDepositRequest([new CashLineRequest(fixture.MembersCapitalAccountId, 100m)], date, "DEP", null)))
            .Content.ReadFromJsonAsync<CashDeposit>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dep.Id);
        CashDisbursement dis = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements",
                new RecordCashDisbursementRequest([new CashLineRequest(fixture.InterestExpenseAccountId, 40m)], date, "DIS", null)))
            .Content.ReadFromJsonAsync<CashDisbursement>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dis.Id);

        // Statement: opening 0, +100 deposit, −40 payment, closing 60.
        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, 60m,
                    [new BankStatementLineRequest(date, 100m, "deposit", null), new BankStatementLineRequest(date, -40m, "payment", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;

        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        ReconciliationWorksheet before = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Assert.Equal(2, before.Entries.Count);
        Assert.False(before.Balanced);

        // complete is refused while unbalanced.
        Assert.Equal(HttpStatusCode.Conflict, (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/complete", null)).StatusCode);

        // Clear both entries → balanced.
        Guid[] entryIds = before.Entries.Select(e => e.EntryId).ToArray();
        ReconciliationWorksheet after = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/clear",
                new ClearRequest(entryIds))).Content.ReadFromJsonAsync<ReconciliationWorksheet>())!;
        Assert.Equal(60m, after.ClearedTotal);
        Assert.Equal(0m, after.ReconciledDifference);
        Assert.True(after.Balanced);

        // complete now succeeds.
        Reconciliation done = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/complete", null))
            .Content.ReadFromJsonAsync<Reconciliation>())!;
        Assert.Equal(ReconciliationStatus.Completed, done.Status);
    }

    [Fact]
    public async Task A_statement_that_does_not_foot_is_rejected_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
            new RecordBankStatementRequest(fixture.CashAccountId, new DateOnly(2026, 1, 31), 0m, 999m,
                [new BankStatementLineRequest(new DateOnly(2026, 1, 20), 100m, "deposit", null)]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
```

> Implementer note: the SoD client's Clerk/Controller/Approver roles — recording a bank statement and clearing entries are reads/own-document writes (no GL post), so the Clerk can do them. If a role check refuses any reconciliation endpoint for the Clerk, that is a finding (the module authorizes its own document ops; the engine only gates GL posts/approvals) — STOP and report rather than switching roles silently.

- [ ] **Step 4: Run the full Reconciliation test project**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --nologo`
Expected: all PASS (3 math + 4 service + 2 E2E).

- [ ] **Step 5: Build the whole solution — confirm no regressions**

Run: `dotnet build Accounting101.slnx --nologo`
Expected: Build succeeded across all projects (the new module + host compose cleanly).

- [ ] **Step 6: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/
git commit -m "$(cat <<'EOF'
test(reconciliation): E2E host fixture + reconciliation flow

Posts real cash entries via the Cash module, records a matching statement,
and reconciles end-to-end: clearing balances the worksheet and completes;
an unbalanced reconciliation refuses complete; a non-footing statement is 422.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- New domain + math (cleared method) → Task 1. ✓
- Document stores (evidentiary statement, plain reconciliation) → Task 2. ✓
- Service (record/start/worksheet/clear/unclear/complete; foots; clear-eligibility; complete-when-balanced) → Task 3. ✓
- Api (endpoints, read-only ledger client, registration, host wiring) → Task 4. ✓
- E2E + host fixture; service + math unit tests → Tasks 1/3/5. ✓
- Read-only on the GL (no PostAsync/credential anywhere) → reader interface + HttpReconciliationLedgerReader. ✓
- Error mapping (422 ArgumentException, 409 InvalidOperationException, 400 missing filter) → endpoints. ✓

**2. Placeholder scan:** No TBD/TODO; full code for every file; commands explicit. The two "copy the exact versions/fixture from the Cash test project" steps (csproj package versions in Task 1/5, the host fixture in Task 5) reference a concrete existing file to mirror rather than guessing version strings — they are instructions to copy a named artifact, not placeholders.

**3. Type consistency:** `ReconciliationMath` (CashEffect/ClearedTotal/ReconciledDifference/IsBalanced), the store interfaces, `IReconciliationLedgerReader`, and the records are defined in Task 1 and consumed unchanged in Tasks 2-5. `EntryResponse`/`EntryLineResponse`/`TrialBalanceResponse`/`AccountBalanceResponse`/`IDocumentStore`/`DocumentResult`/`ModuleIdentity`/`AddModule` match the confirmed-contracts section. Cash request DTOs (`RecordCashDepositRequest`/`RecordCashDisbursementRequest`/`CashLineRequest`) used in the E2E match the Cash module. ✓
