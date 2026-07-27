# Bank Statement Duplicate Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Warn an operator, on both screens that create a bank statement, when the same cash account already has a statement bearing the same statement date.

**Architecture:** One service method on `ReconciliationService` reading through the existing `IBankStatementStore.GetByAccountAsync` (complete by construction — no paging, no cap), exposed as `GET /clients/{clientId}/bank-statements/duplicates`, consumed by one Angular service method and one shared presentational component used by `StatementEditor` and `StatementImport`. The warning is advisory and never blocks recording.

**Tech Stack:** .NET 10 minimal APIs, xUnit, EphemeralMongo-backed `ReconciliationHostFixture`; Angular 20 standalone components with signals, Vitest + jsdom, Spartan/Helm UI primitives.

**Spec:** [`docs/superpowers/specs/2026-07-26-bank-statement-duplicates-design.md`](../specs/2026-07-26-bank-statement-duplicates-design.md)

## Global Constraints

- **The warning never blocks.** No submit button is ever disabled because of a duplicate hit, on either screen.
- **No module logic moves into `Accounting101.Ledger.Api` or any shared shipped assembly.** Engine code ships to every deployment; module code is gated by `installed ∩ entitled`.
- **`UI/Angular/src/app/core/api/environment.ts` carries a long-lived local dev edit that must stay uncommitted.** Always `git add` explicit file lists in this repo — never `git add -A` or `git add .`.
- **No new authorization gate.** The route group is already `.RequireAuthorization()` and reads reach documents through `ScopedDocumentStore`, where `ModuleAccess.AuthorizeAsync` enforces entitlement and capability.
- **Backend tests:** `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests`
- **Angular tests:** from `UI/Angular`, `npx ng test --include=<path> --watch=false`
- **13 unhandled NG04002 console errors are pre-existing** in the Angular suite (inventory/banking/fixed-assets specs). They are not failures and not from this work. Do not chase them.

---

## File Structure

| File | Responsibility |
|---|---|
| `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/ReconciliationService.cs` | Gains `FindDuplicateStatementsAsync` |
| `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs` | Gains the route registration and handler |
| `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs` | `InMemoryBankStatementStore` gains `Seed` |
| `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/BankStatementDuplicateTests.cs` | **New** — service-level tests |
| `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/BankStatementDuplicateEndpointTests.cs` | **New** — HTTP-level tests |
| `UI/Angular/src/app/core/banking/banking.service.ts` | Gains `statementDuplicates` |
| `UI/Angular/src/app/features/banking/statement-duplicate-warning.ts` | **New** — the shared warning block |
| `UI/Angular/src/app/features/banking/statement-duplicate-warning.spec.ts` | **New** — its spec |
| `UI/Angular/src/app/features/banking/statement-editor.ts` | Scans on account/date change, renders the warning |
| `UI/Angular/src/app/features/banking/statement-editor.spec.ts` | Gains warning tests |
| `UI/Angular/src/app/features/banking/statement-import.ts` | Scans per preview, re-scans on date edit, renders the warning |
| `UI/Angular/src/app/features/banking/statement-import.spec.ts` | Gains warning tests; **existing upload test must be updated** |

---

## Task 1: The service lookup

**Files:**
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/ReconciliationService.cs` (add after `ListStatementsAsync`)
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs` (`InMemoryBankStatementStore`)
- Test: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/BankStatementDuplicateTests.cs` (create)

**Interfaces:**
- Consumes: existing `IBankStatementStore.GetByAccountAsync(Guid clientId, Guid cashAccountId, CancellationToken ct)`; existing `BankStatement` record with `Id`, `Number`, `CashAccountId`, `StatementDate`, `Status`; existing `BankStatementStatus { Posted, Void }`.
- Produces: `ReconciliationService.FindDuplicateStatementsAsync(Guid clientId, Guid cashAccountId, DateOnly statementDate, CancellationToken ct = default) → Task<IReadOnlyList<BankStatement>>`, used by Task 2. `InMemoryBankStatementStore.Seed(Guid clientId, Guid cashAccountId, DateOnly statementDate, BankStatementStatus status) → BankStatement`, used only within this task.

### Background the implementer needs

`InMemoryBankStatementStore.RecordAsync` always produces a `Posted` statement — `BankStatement.Status` defaults to `Posted` and nothing sets it otherwise. So a Void test **cannot be written against the fake as it stands**, and it cannot be written against the real store either: nothing in the codebase voids a bank statement (no `VoidAsync` on `IBankStatementStore`, no caller, and the collection is registered `manifest.Evidentiary("bank-statements")`). That is why this task adds a `Seed` method — it is the only way to get a Void test that can actually fail.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/BankStatementDuplicateTests.cs`:

```csharp
namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>
/// The duplicate lookup behind the import and editor screens' warning: statements already on file for a
/// cash account bearing the same statement date.
/// <para>
/// The Void case is tested here and ONLY here, against the in-memory store. The production store cannot
/// produce a voided statement — <c>GetByAccountAsync</c> queries with the document store's default
/// <c>includeVoided:false</c>, which excludes Voided/Superseded/Inactive, and nothing voids a bank
/// statement in the first place. A Void test driven through the real store would pass whether or not the
/// service filters, which is worth nothing.
/// </para>
/// </summary>
public sealed class BankStatementDuplicateTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid OtherCash = Guid.NewGuid();
    private static readonly DateOnly StmtDate = new(2026, 1, 31);

    private static (ReconciliationService Svc, InMemoryBankStatementStore Stmts) Build()
    {
        InMemoryBankStatementStore stmts = new();
        ReconciliationService svc = new(stmts, new InMemoryReconciliationStore(), new FakeLedgerReader([], 0m));
        return (svc, stmts);
    }

    private static BankStatementBody Body(Guid cashAccountId, DateOnly date) =>
        new(cashAccountId, date, 0m, 100m, [new BankStatementLine(date, 100m, "dep", null)]);

    [Fact]
    public async Task A_statement_on_the_same_account_and_date_is_returned()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();
        BankStatement recorded = await svc.RecordStatementAsync(clientId, Body(Cash, StmtDate));

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Equal(recorded.Id, Assert.Single(hits).Id);
    }

    [Fact]
    public async Task A_statement_on_another_cash_account_is_not_a_duplicate()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();
        await svc.RecordStatementAsync(clientId, Body(OtherCash, StmtDate));

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task A_statement_on_another_date_is_not_a_duplicate()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();
        await svc.RecordStatementAsync(clientId, Body(Cash, new DateOnly(2026, 2, 28)));

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Empty(hits);
    }

    /// <summary>Delete the Status filter in FindDuplicateStatementsAsync and this test fails — that is the
    /// whole point of Seed existing.</summary>
    [Fact]
    public async Task A_voided_statement_is_not_a_duplicate()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, InMemoryBankStatementStore stmts) = Build();
        stmts.Seed(clientId, Cash, StmtDate, BankStatementStatus.Void);

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task No_matches_returns_an_empty_list()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.NotNull(hits);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Hits_are_ordered_by_number_descending()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, _) = Build();
        BankStatement first = await svc.RecordStatementAsync(clientId, Body(Cash, StmtDate));
        BankStatement second = await svc.RecordStatementAsync(clientId, Body(Cash, StmtDate));

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Equal([second.Id, first.Id], hits.Select(s => s.Id));
    }

    /// <summary>A cap probe, not a business scenario. GET /bank-statements clamps limit to 200, so a
    /// caller assembling its own scan over that list would silently miss hits past the cap. This lookup
    /// reads the account's statements directly and must return all of them.</summary>
    [Fact]
    public async Task Every_match_is_returned_past_the_list_endpoints_page_cap()
    {
        Guid clientId = Guid.NewGuid();
        (ReconciliationService svc, InMemoryBankStatementStore stmts) = Build();
        for (int i = 0; i < 250; i++) stmts.Seed(clientId, Cash, StmtDate, BankStatementStatus.Posted);

        IReadOnlyList<BankStatement> hits = await svc.FindDuplicateStatementsAsync(clientId, Cash, StmtDate);

        Assert.Equal(250, hits.Count);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests --filter BankStatementDuplicateTests`
Expected: compile failure — `FindDuplicateStatementsAsync` and `Seed` do not exist.

- [ ] **Step 3: Add `Seed` to the fake**

In `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs`, inside `InMemoryBankStatementStore`, add after `RecordAsync`:

```csharp
    /// <summary>Place a statement directly, in a chosen status. <see cref="RecordAsync"/> can only produce
    /// Posted, and no production path produces Void at all, so a test covering the Void filter has to seed
    /// one here — otherwise that filter would be covered only by a test that cannot fail.</summary>
    public BankStatement Seed(Guid clientId, Guid cashAccountId, DateOnly statementDate, BankStatementStatus status)
    {
        BankStatement s = new()
        {
            Id = Guid.NewGuid(), Number = $"BST-{Interlocked.Increment(ref _seq):D5}",
            CashAccountId = cashAccountId, StatementDate = statementDate,
            OpeningBalance = 0m, ClosingBalance = 0m, Lines = [], Status = status,
        };
        _store[(clientId, s.Id)] = s;
        return s;
    }
```

- [ ] **Step 4: Add the service method**

In `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/ReconciliationService.cs`, immediately after `ListStatementsAsync`:

```csharp
    /// <summary>
    /// Statements already on file for this cash account bearing the same statement date — the lookup behind
    /// the import and editor screens' duplicate warning. Advisory only: nothing in this module consults it,
    /// because re-issued and corrected statements are legitimate and only a human can tell.
    ///
    /// Complete by construction: reads the account's statements directly, with no paging and no cap. The
    /// paged <see cref="ListStatementsAsync"/> is a different read — a caller assembling its own scan over
    /// that list would be clamped to 200 and could report "none found" without knowing it.
    ///
    /// The Status filter is belt-and-braces, not the load-bearing exclusion. Two facts already keep voided
    /// statements out: <see cref="IBankStatementStore.GetByAccountAsync"/> queries the document store with
    /// its default includeVoided:false, which excludes Voided/Superseded/Inactive; and nothing voids a bank
    /// statement in the first place — the store exposes no void method and the collection is registered
    /// Evidentiary. The filter keeps this read correct if either fact ever changes.
    /// </summary>
    public async Task<IReadOnlyList<BankStatement>> FindDuplicateStatementsAsync(
        Guid clientId, Guid cashAccountId, DateOnly statementDate, CancellationToken ct = default)
    {
        IReadOnlyList<BankStatement> all = await statements.GetByAccountAsync(clientId, cashAccountId, ct);

        return
        [
            .. all
                .Where(s => s.Status != BankStatementStatus.Void && s.StatementDate == statementDate)
                .OrderByDescending(s => s.Number)
        ];
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests --filter BankStatementDuplicateTests`
Expected: PASS, 7 tests.

- [ ] **Step 6: Confirm the Void test can fail**

Temporarily delete `s.Status != BankStatementStatus.Void &&` from the `Where` clause and re-run. Expected: `A_voided_statement_is_not_a_duplicate` FAILS. Restore the clause and re-run — all 7 pass. Do not commit the temporary deletion.

- [ ] **Step 7: Run the module suite for regressions**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests`
Expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/ReconciliationService.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/BankStatementDuplicateTests.cs
git commit -m "feat(reconciliation): find bank statements duplicating an account and date"
```

---

## Task 2: The endpoint

**Files:**
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs`
- Test: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/BankStatementDuplicateEndpointTests.cs` (create)

**Interfaces:**
- Consumes: `ReconciliationService.FindDuplicateStatementsAsync` from Task 1.
- Produces: `GET /clients/{clientId:guid}/bank-statements/duplicates?cashAccountId={guid}&statementDate={yyyy-MM-dd}` returning a bare JSON array of `BankStatement`. Consumed by Task 3.

### Background the implementer needs

`statementDate` binds as `string?` and is parsed in the handler, **not** as `DateOnly?`. A malformed value bound directly would produce the framework's own empty-bodied 400, and every other 400 in this module returns a `problem+json` body — which the tests assert.

There is no route conflict with `/bank-statements/{id:guid}`: that route is guid-constrained, so `duplicates` cannot match it.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/BankStatementDuplicateEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Reconciliation.Api;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>The duplicate lookup over HTTP. Both query parameters are required, and the result is scoped
/// to one cash account and one date.</summary>
public sealed class BankStatementDuplicateEndpointTests(ReconciliationHostFixture fixture)
    : IClassFixture<ReconciliationHostFixture>
{
    private static readonly DateOnly StmtDate = new(2026, 1, 31);

    private RecordBankStatementRequest ValidStatementRequest() =>
        new(fixture.CashAccountId, StmtDate, 0m, 60m,
            [
                new BankStatementLineRequest(new DateOnly(2026, 1, 20), 100m, "deposit", null),
                new BankStatementLineRequest(new DateOnly(2026, 1, 20), -40m, "payment", null),
            ]);

    [Fact]
    public async Task Without_a_cashAccountId_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?statementDate=2026-01-31");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Without_a_statementDate_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?cashAccountId={fixture.CashAccountId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task With_an_unparseable_statementDate_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?cashAccountId={fixture.CashAccountId}&statementDate=not-a-date");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task With_no_matching_statements_returns_an_empty_array()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?cashAccountId={Guid.NewGuid()}&statementDate=2026-01-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<BankStatement>? matches = await response.Content.ReadFromJsonAsync<List<BankStatement>>();
        Assert.NotNull(matches);
        Assert.Empty(matches);
    }

    /// <summary>The assertion the endpoint exists for: nothing else in this file records a statement, so a
    /// transposed argument or a mis-bound query key would leave every other test here green while every
    /// import silently reported "no duplicates found".</summary>
    [Fact]
    public async Task With_a_matching_statement_returns_it()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);
        BankStatement recorded = (await (await http.PostAsJsonAsync(
            $"/clients/{clientId}/bank-statements", ValidStatementRequest()))
            .Content.ReadFromJsonAsync<BankStatement>())!;

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?cashAccountId={fixture.CashAccountId}&statementDate=2026-01-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<BankStatement>? matches = await response.Content.ReadFromJsonAsync<List<BankStatement>>();
        Assert.NotNull(matches);
        BankStatement match = Assert.Single(matches);
        Assert.Equal(recorded.Id, match.Id);
        Assert.Equal(fixture.CashAccountId, match.CashAccountId);
        Assert.Equal(StmtDate, match.StatementDate);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests --filter BankStatementDuplicateEndpointTests`
Expected: FAIL — the 400 tests return 404 (route not mapped), and `With_a_matching_statement_returns_it` fails deserializing a 404 body.

- [ ] **Step 3: Register the route**

In `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs`, in `MapReconciliationEndpoints`, add immediately after the `clients.MapGet("/bank-statements", ListStatements);` line:

```csharp
        clients.MapGet("/bank-statements/duplicates", FindDuplicateStatements);
```

- [ ] **Step 4: Add the handler and its using**

Add `using System.Globalization;` to the top of the same file, alongside the existing `using System.Text.Json;`.

Add the handler immediately after the `ListStatements` method:

```csharp
    /// <summary>Statements this cash account already has bearing the given statement date. Advisory: the
    /// caller decides what to do about them. Returns the domain type per this class's convention.
    /// <para>
    /// statementDate binds as a string and is parsed here rather than bound as DateOnly?. A malformed value
    /// bound directly yields the framework's own empty-bodied 400, and every other 400 in this module
    /// returns a problem+json body.
    /// </para></summary>
    private static async Task<IResult> FindDuplicateStatements(
        Guid clientId, Guid? cashAccountId, string? statementDate,
        ReconciliationService service, CancellationToken ct)
    {
        if (cashAccountId is null || cashAccountId == Guid.Empty)
            return Results.Problem("cashAccountId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);

        if (!DateOnly.TryParse(statementDate, CultureInfo.InvariantCulture, out DateOnly parsed))
            return Results.Problem("statementDate query parameter is required and must be a date (yyyy-MM-dd).",
                statusCode: StatusCodes.Status400BadRequest);

        return Results.Ok(await service.FindDuplicateStatementsAsync(clientId, cashAccountId.Value, parsed, ct));
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests --filter BankStatementDuplicateEndpointTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Run the module suite for regressions**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/BankStatementDuplicateEndpointTests.cs
git commit -m "feat(reconciliation): expose the statement duplicate lookup over HTTP"
```

---

## Task 3: The Angular client method and the shared warning

**Files:**
- Modify: `UI/Angular/src/app/core/banking/banking.service.ts`
- Create: `UI/Angular/src/app/features/banking/statement-duplicate-warning.ts`
- Test: `UI/Angular/src/app/features/banking/statement-duplicate-warning.spec.ts` (create)

**Interfaces:**
- Consumes: the endpoint from Task 2; the existing `BankStatement` interface in `core/banking/banking.ts` (`{ id, number: string | null, cashAccountId, statementDate, openingBalance, closingBalance, lines, status }`).
- Produces: `BankingService.statementDuplicates(cashAccountId: string, statementDate: string): Observable<BankStatement[]>` and the standalone component `StatementDuplicateWarning` with a required input `duplicates: BankStatement[]`, both consumed by Tasks 4 and 5.

- [ ] **Step 1: Write the failing spec**

Create `UI/Angular/src/app/features/banking/statement-duplicate-warning.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { StatementDuplicateWarning } from './statement-duplicate-warning';
import { BankStatement } from '../../core/banking/banking';

function hit(over: Partial<BankStatement> = {}): BankStatement {
  return {
    id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
    openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted', ...over,
  };
}

function boot(duplicates: BankStatement[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([])],
  });
  const fixture = TestBed.createComponent(StatementDuplicateWarning);
  fixture.componentRef.setInput('duplicates', duplicates);
  fixture.detectChanges();
  return fixture;
}

describe('StatementDuplicateWarning', () => {
  it('renders nothing when there are no duplicates', () => {
    const fixture = boot([]);
    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('lists each duplicate with a link to its statement', () => {
    const fixture = boot([hit(), hit({ id: 'b2', number: 'BST-00002' })]);
    const links = fixture.nativeElement.querySelectorAll('a');
    expect(links.length).toBe(2);
    expect(links[0].getAttribute('href')).toBe('/cash/statements/b1');
    expect(links[0].textContent).toContain('BST-00001');
    expect(fixture.nativeElement.textContent).toContain('2026-03-31');
  });

  it('falls back to a label when a statement has no number', () => {
    const fixture = boot([hit({ number: null })]);
    expect(fixture.nativeElement.querySelector('a').textContent).toContain('Statement');
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run from `UI/Angular`: `npx ng test --include=src/app/features/banking/statement-duplicate-warning.spec.ts --watch=false`
Expected: FAIL — cannot resolve `./statement-duplicate-warning`.

- [ ] **Step 3: Create the component**

Create `UI/Angular/src/app/features/banking/statement-duplicate-warning.ts`:

```ts
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BankStatement } from '../../core/banking/banking';
import { money as fmtMoney } from '../../core/format/display';

/**
 * The advisory duplicate block shared by the statement editor and the statement import screen: statements
 * already on file for the same cash account and statement date. It never blocks — re-issued and corrected
 * statements are legitimate and only a human can tell.
 */
@Component({
  selector: 'app-statement-duplicate-warning',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    @if (duplicates().length) {
      <div class="border border-amber-400 rounded p-3 text-sm flex flex-col gap-1">
        <p class="font-semibold">Already on file for this account and date</p>
        <p class="text-muted-foreground">Recording this statement is still allowed.</p>
        <ul class="list-disc ps-5">
          @for (s of duplicates(); track s.id) {
            <li>
              <a [routerLink]="['/cash/statements', s.id]" class="text-primary hover:underline">{{ s.number ?? 'Statement' }}</a>
              — {{ s.statementDate }}, closing {{ money(s.closingBalance) }}
            </li>
          }
        </ul>
      </div>
    }
  `,
})
export class StatementDuplicateWarning {
  readonly duplicates = input.required<BankStatement[]>();
  money(n: number): string { return fmtMoney(n); }
}
```

- [ ] **Step 4: Add the service method**

In `UI/Angular/src/app/core/banking/banking.service.ts`, add immediately after `recordStatement`:

```ts
  /** Statements already on file for this cash account and statement date. Advisory — never blocks. */
  statementDuplicates(cashAccountId: string, statementDate: string): Observable<BankStatement[]> {
    if (!this.client.clientId()) return EMPTY;
    const params = new HttpParams().set('cashAccountId', cashAccountId).set('statementDate', statementDate);
    return this.http.get<BankStatement[]>(this.base('/bank-statements/duplicates'), { params });
  }
```

- [ ] **Step 5: Run the spec to verify it passes**

Run from `UI/Angular`: `npx ng test --include=src/app/features/banking/statement-duplicate-warning.spec.ts --watch=false`
Expected: PASS, 3 tests.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/banking/banking.service.ts \
        UI/Angular/src/app/features/banking/statement-duplicate-warning.ts \
        UI/Angular/src/app/features/banking/statement-duplicate-warning.spec.ts
git commit -m "feat(banking-ui): statement duplicate lookup client and shared warning block"
```

---

## Task 4: Wire the warning into the manual editor

**Files:**
- Modify: `UI/Angular/src/app/features/banking/statement-editor.ts`
- Test: `UI/Angular/src/app/features/banking/statement-editor.spec.ts`

**Interfaces:**
- Consumes: `BankingService.statementDuplicates` and `StatementDuplicateWarning` from Task 3.
- Produces: nothing consumed by later tasks. Task 5 applies the same pattern independently, with a per-preview index.

### Background the implementer needs

This screen holds a single statement, so a monotonically increasing `generation` counter incremented **on every scan** is both the staleness guard and the key guard — the last scan started is the only one allowed to write. Task 5's screen needs two separate guards because it has N previews; do not assume the shapes match.

The scan must never touch `busy`, which belongs to `save()`. A guarded callback that skipped `busy.set(false)` is what deadlocked the e-invoicing import screen's button.

- [ ] **Step 1: Write the failing tests**

Append to `UI/Angular/src/app/features/banking/statement-editor.spec.ts`, inside the existing `describe('StatementEditor')` block. The file already has a `boot()` helper returning `{ fixture, ctrl }` and flushing the `/accounts` load — use it as-is.

The pre-existing tests in this file set `cmp.cashAccountId.set('CA1')` directly rather than through the new setter, so they never trigger a scan and their `ctrl.verify()` stays clean. Leave them alone.

```ts
  it('scans for duplicates once the account and date are both set, and renders the hits', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.setCashAccount('CA1');
    const req = ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates'));
    expect(req.request.params.get('cashAccountId')).toBe('CA1');
    expect(req.request.params.get('statementDate')).toBe(cmp.statementDate());
    req.flush([{ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: cmp.statementDate(),
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.duplicates().length).toBe(1);
    expect(fixture.nativeElement.textContent).toContain('BST-00001');
    ctrl.verify();
  });

  it('never disables Record statement because of a duplicate', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.setCashAccount('CA1');
    cmp.patch(0, { date: '2026-03-05', description: 'dep', amount: 100 });
    cmp.closingBalance.set(100);
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates')).flush([
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: cmp.statementDate(),
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.duplicates().length).toBe(1);
    expect(cmp.canSave()).toBe(true);
    ctrl.verify();
  });

  it('ignores a response superseded by a later scan', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.setCashAccount('CA1');
    const first = ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates'));
    cmp.setStatementDate('2026-04-30');
    const second = ctrl.expectOne(r => r.params.get('statementDate') === '2026-04-30');
    second.flush([]);
    first.flush([{ id: 'stale', number: 'BST-09999', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.duplicates()).toEqual([]);
    ctrl.verify();
  });

  it('leaves no stale hit on screen when a re-scan fails', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.setCashAccount('CA1');
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates')).flush([
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: cmp.statementDate(),
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.duplicates().length).toBe(1);
    cmp.setStatementDate('2026-04-30');
    ctrl.expectOne(r => r.params.get('statementDate') === '2026-04-30')
        .flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(cmp.duplicates()).toEqual([]);
    ctrl.verify();
  });
```

- [ ] **Step 2: Run the spec to verify it fails**

Run from `UI/Angular`: `npx ng test --include=src/app/features/banking/statement-editor.spec.ts --watch=false`
Expected: FAIL — `setCashAccount`, `setStatementDate` and `duplicates` do not exist.

- [ ] **Step 3: Add the scan to the component class**

In `UI/Angular/src/app/features/banking/statement-editor.ts`:

Add to the imports from `core/banking/banking` on line 9 so it reads:

```ts
import { BankStatement, BankStatementLine, RecordBankStatementRequest } from '../../core/banking/banking';
```

Add the warning component import:

```ts
import { StatementDuplicateWarning } from './statement-duplicate-warning';
```

Add it to the component's `imports` array:

```ts
  imports: [RouterLink, CanDirective, StatementDuplicateWarning, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
```

Add the signal beside `message`:

```ts
  readonly duplicates = signal<BankStatement[]>([]);
  private generation = 0;
```

Add the setters and the scan after `accountItemToString`:

```ts
  setCashAccount(id: string): void { this.cashAccountId.set(id); this.scan(); }
  setStatementDate(date: string): void { this.statementDate.set(date); this.scan(); }

  /**
   * Advisory duplicate scan. `generation` is incremented per scan and captured, so only the most recently
   * started scan may write — the single-statement equivalent of the import screen's generation + key pair.
   * Deliberately never touches `busy`: that belongs to save(), and a guarded callback skipping
   * busy.set(false) is what deadlocked the e-invoicing import button.
   */
  private scan(): void {
    this.duplicates.set([]);
    const account = this.cashAccountId();
    const date = this.statementDate();
    const gen = ++this.generation;
    if (!account || !date) return;
    this.svc.statementDuplicates(account, date).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (hits) => { if (gen === this.generation) this.duplicates.set(hits); },
      error: () => { if (gen === this.generation) this.duplicates.set([]); },
    });
  }
```

- [ ] **Step 4: Route the template through the setters and render the warning**

In the same file, change the cash-account select's handler (line 30) from `(valueChange)="cashAccountId.set($any($event))"` to:

```html
            (valueChange)="setCashAccount($any($event))"
```

Change the statement-date input's handler (line 39) from `(change)="statementDate.set($any($event.target).value)"` to:

```html
          <input hlmInput type="date" [value]="statementDate()" (change)="setStatementDate($any($event.target).value)" />
```

Add the warning immediately after the closing `</div>` of the `grid grid-cols-2` block and before the `<table`:

```html
      <app-statement-duplicate-warning [duplicates]="duplicates()" />
```

- [ ] **Step 5: Run the spec to verify it passes**

Run from `UI/Angular`: `npx ng test --include=src/app/features/banking/statement-editor.spec.ts --watch=false`
Expected: PASS — the four new tests plus every pre-existing test in the file.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/banking/statement-editor.ts \
        UI/Angular/src/app/features/banking/statement-editor.spec.ts
git commit -m "feat(banking-ui): warn on duplicate statements in the manual editor"
```

---

## Task 5: Wire the warning into the import screen

**Files:**
- Modify: `UI/Angular/src/app/features/banking/statement-import.ts`
- Test: `UI/Angular/src/app/features/banking/statement-import.spec.ts`

**Interfaces:**
- Consumes: `BankingService.statementDuplicates` and `StatementDuplicateWarning` from Task 3.
- Produces: nothing consumed by later tasks.

### Background the implementer needs

This screen holds N previews sharing one cash account, each with its own editable date, so it needs **two** guards, not one:

- `generation`, incremented **only** in `reset()` — the whole preview set is gone, so no in-flight scan may write to it.
- a per-scan `key` of the date being scanned, compared against that preview's current date on arrival — the operator can edit preview 2's date twice while the first response is in flight.

They guard different things and both are needed. Do not collapse them.

**Two pre-existing tests in this spec file will break, and both must be fixed as part of this task:**

1. `uploads a CSV with a built mapping and moves to preview` — `upload()` now issues a duplicate scan per preview, and the test ends with `ctrl.verify()`. It must flush the scan.
2. `confirms a preview by posting a bank statement` — it calls `cmp.previews.set([{ lines, openingBalance, closingBalance, statementDate }])` with **no `duplicates` field**. Once `duplicates` is a required member of `EditablePreview` that is a TypeScript compile error, so add `duplicates: []` to that object literal.

Both are expected consequences of the model change, not regressions.

- [ ] **Step 1: Write the failing tests**

Append to `UI/Angular/src/app/features/banking/statement-import.spec.ts`, inside the existing `describe` block:

```ts
  it('scans each preview for duplicates on entering the preview stage', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.file.set(new File(['x'], 'bank.csv', { type: 'text/csv' }));
    cmp.setColumn('date', 0); cmp.setColumn('amount', 1); cmp.setColumn('description', 2);
    cmp.upload();
    ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements/import').flush({
      statements: [
        { lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
          detectedOpeningBalance: 0, detectedClosingBalance: 100, statementDate: '2026-03-31', accountHint: null },
        { lines: [{ date: '2026-04-05', amount: 50, description: 'dep', externalRef: null }],
          detectedOpeningBalance: 0, detectedClosingBalance: 50, statementDate: '2026-04-30', accountHint: null },
      ], warnings: [],
    });
    fixture.detectChanges();

    const scans = ctrl.match(r => r.url.endsWith('/bank-statements/duplicates'));
    expect(scans.length).toBe(2);
    expect(scans.map(s => s.request.params.get('statementDate'))).toEqual(['2026-03-31', '2026-04-30']);
    scans[0].flush([{ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    scans[1].flush([]);
    fixture.detectChanges();

    expect(cmp.previews()[0].duplicates.length).toBe(1);
    expect(cmp.previews()[1].duplicates).toEqual([]);
    expect(fixture.nativeElement.textContent).toContain('BST-00001');
    ctrl.verify();
  });

  it('re-scans only the edited preview when its date changes', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [], openingBalance: 0, closingBalance: 0, statementDate: '2026-03-31', duplicates: [] }]);
    cmp.stage.set('preview');
    cmp.setStatementDate(0, '2026-04-30');
    const req = ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates'));
    expect(req.request.params.get('statementDate')).toBe('2026-04-30');
    req.flush([]);
    expect(cmp.previews()[0].statementDate).toBe('2026-04-30');
    ctrl.verify();
  });

  it('ignores a scan response for a date the preview has moved off', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [], openingBalance: 0, closingBalance: 0, statementDate: '2026-03-31', duplicates: [] }]);
    cmp.stage.set('preview');
    cmp.setStatementDate(0, '2026-04-30');
    const first = ctrl.expectOne(r => r.params.get('statementDate') === '2026-04-30');
    cmp.setStatementDate(0, '2026-05-31');
    ctrl.expectOne(r => r.params.get('statementDate') === '2026-05-31').flush([]);
    first.flush([{ id: 'stale', number: 'BST-09999', cashAccountId: 'CA1', statementDate: '2026-04-30',
      openingBalance: 0, closingBalance: 0, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.previews()[0].duplicates).toEqual([]);
    ctrl.verify();
  });

  it('never disables Record statement because of a duplicate', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
      openingBalance: 0, closingBalance: 100, statementDate: '2026-03-31', duplicates: [] }]);
    cmp.stage.set('preview');
    cmp.setStatementDate(0, '2026-03-31');
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates')).flush([
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.previews()[0].duplicates.length).toBe(1);
    expect(cmp.foots(cmp.previews()[0])).toBe(true);
    ctrl.verify();
  });

  it('scans a defaulted date exactly like a detected one', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    const today = new Date().toISOString().slice(0, 10);
    cmp.cashAccountId.set('CA1');
    cmp.file.set(new File(['x'], 'bank.csv', { type: 'text/csv' }));
    cmp.setColumn('date', 0); cmp.setColumn('amount', 1); cmp.setColumn('description', 2);
    cmp.upload();
    ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements/import').flush({
      statements: [{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
        detectedOpeningBalance: 0, detectedClosingBalance: 100, statementDate: null, accountHint: null }],
      warnings: [],
    });
    fixture.detectChanges();

    const scan = ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates'));
    expect(scan.request.params.get('statementDate')).toBe(today);
    scan.flush([]);
    ctrl.verify();
  });

  // A stale ERROR must not wipe out a newer scan that already succeeded. Note what this test does NOT
  // do: flush a failing scan and assert duplicates is empty. That assertion holds whether or not the
  // error handler is guarded, because scan() clears duplicates synchronously before any response
  // arrives — it is a test that cannot fail. The guard only earns its place in the ordering below.
  it('keeps a newer scan\'s hits when an older scan errors late', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [], openingBalance: 0, closingBalance: 0, statementDate: '2026-03-31', duplicates: [] }]);
    cmp.stage.set('preview');

    cmp.setStatementDate(0, '2026-04-30');
    const older = ctrl.expectOne(r => r.params.get('statementDate') === '2026-04-30');   // left in flight

    cmp.setStatementDate(0, '2026-05-31');
    ctrl.expectOne(r => r.params.get('statementDate') === '2026-05-31').flush([
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-05-31',
        openingBalance: 0, closingBalance: 0, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.previews()[0].duplicates.length).toBe(1);

    older.flush('boom', { status: 500, statusText: 'Server Error' });                    // arrives late
    fixture.detectChanges();
    expect(cmp.previews()[0].duplicates.length).toBe(1);   // the newer scan's hit survives
    expect(cmp.message()).toBeNull();                      // a failed scan is not a failed import
    ctrl.verify();
  });
```

**Prove this test can fail before committing.** Temporarily change the `error:` branch of `scan()` to clear unconditionally (drop both guard lines from `settle`'s error path, or make the handler `error: () => this.patchPreview(i, { duplicates: [] })`) and re-run the spec. `keeps a newer scan's hits when an older scan errors late` MUST fail — the late error wipes the newer hit. Restore the guarded version, re-run, all pass. Report the mutation evidence; a guard test nobody has watched fail is worth nothing.

Then update the **existing** `uploads a CSV with a built mapping and moves to preview` test: after its `fixture.detectChanges()` and before `ctrl.verify()`, add:

```ts
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates')).flush([]);
```

- [ ] **Step 2: Run the spec to verify it fails**

Run from `UI/Angular`: `npx ng test --include=src/app/features/banking/statement-import.spec.ts --watch=false`
Expected: FAIL — `setStatementDate` does not exist and `duplicates` is not on `EditablePreview`.

- [ ] **Step 3: Extend the preview model and the component class**

In `UI/Angular/src/app/features/banking/statement-import.ts`:

Change the `EditablePreview` interface on line 19 to:

```ts
interface EditablePreview { lines: BankStatementLine[]; openingBalance: number; closingBalance: number; statementDate: string; duplicates: BankStatement[]; createdId?: string; error?: string; }
```

Add `BankStatement` to the imports from `core/banking/banking` so the block reads:

```ts
import {
  InterchangeFormat, CsvMapping, ColumnRef, StatementPreview, BankStatement, BankStatementLine, RecordBankStatementRequest,
} from '../../core/banking/banking';
```

Add the warning component import:

```ts
import { StatementDuplicateWarning } from './statement-duplicate-warning';
```

Add it to the component's `imports` array:

```ts
  imports: [RouterLink, CanDirective, StatementDuplicateWarning, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
```

Add the guard counter beside the other fields:

```ts
  private generation = 0;
```

Add the date setter and the scan after `patchPreview`:

```ts
  setStatementDate(i: number, date: string): void { this.patchPreview(i, { statementDate: date }); this.scan(i); }

  /**
   * Advisory duplicate scan for one preview. Two guards, because this screen holds N previews sharing one
   * cash account: `generation` (incremented ONLY in reset(), so an in-flight scan cannot write to a preview
   * set that has been thrown away) and `key` (the date being scanned, so an earlier response cannot land on
   * a preview whose date has since moved). They guard different things — do not collapse them.
   * Deliberately never touches `busy`, which belongs to upload()/confirm().
   */
  private scan(i: number): void {
    this.patchPreview(i, { duplicates: [] });
    const account = this.cashAccountId();
    const key = this.previews()[i]?.statementDate;
    if (!account || !key) return;
    const gen = this.generation;
    const settle = (hits: BankStatement[]): void => {
      if (gen !== this.generation) return;
      if (this.previews()[i]?.statementDate !== key) return;
      this.patchPreview(i, { duplicates: hits });
    };
    this.svc.statementDuplicates(account, key).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (hits) => settle(hits),
      error: () => settle([]),
    });
  }
```

Change `reset()` to bump the generation first:

```ts
  reset(): void { this.generation++; this.stage.set('upload'); this.previews.set([]); this.warnings.set([]); this.file.set(null); this.message.set(null); }
```

In `upload()`, set `duplicates: []` when mapping the previews and kick off a scan per preview. The `next` callback becomes:

```ts
        next: (res) => {
          this.previews.set(res.statements.map((s: StatementPreview) => ({
            lines: s.lines,
            openingBalance: s.detectedOpeningBalance ?? 0,
            closingBalance: s.detectedClosingBalance ?? 0,
            statementDate: s.statementDate ?? new Date().toISOString().slice(0, 10),
            duplicates: [],
          })));
          this.warnings.set(res.warnings);
          this.stage.set('preview'); this.busy.set(false);
          this.previews().forEach((_, i) => this.scan(i));
        },
```

- [ ] **Step 4: Route the template through the setter and render the warning**

In the same file, change the per-preview statement-date input (line 106) from `(change)="patchPreview(i, { statementDate: $any($event.target).value })"` to:

```html
                <input hlmInput type="date" [value]="p.statementDate" (change)="setStatementDate(i, $any($event.target).value)" [disabled]="!!p.createdId" /></div>
```

Add the warning immediately after the closing `</div>` of that preview's `grid grid-cols-3` block and before the `<table`:

```html
            <app-statement-duplicate-warning [duplicates]="p.duplicates" />
```

- [ ] **Step 5: Run the spec to verify it passes**

Run from `UI/Angular`: `npx ng test --include=src/app/features/banking/statement-import.spec.ts --watch=false`
Expected: PASS — the six new tests plus both updated pre-existing tests.

- [ ] **Step 6: Run the full Angular suite**

Run from `UI/Angular`: `npx ng test --watch=false`
Expected: PASS. The 13 pre-existing NG04002 console errors are not failures — ignore them.

- [ ] **Step 7: Run the production build**

Run from `UI/Angular`: `npx ng build`
Expected: succeeds. A pre-existing bundle-budget **warning** over 1.00 MB is expected and is not a failure.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/features/banking/statement-import.ts \
        UI/Angular/src/app/features/banking/statement-import.spec.ts
git commit -m "feat(banking-ui): warn on duplicate statements in the import screen"
```

---

## Verification before completion

- [ ] `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests` — passes
- [ ] Full local solution test run — no regressions against the 1601-test baseline
- [ ] From `UI/Angular`: `npx ng test --watch=false` — passes
- [ ] From `UI/Angular`: `npx ng build` — succeeds
- [ ] `git status` shows `UI/Angular/src/app/core/api/environment.ts` still modified and **uncommitted**
