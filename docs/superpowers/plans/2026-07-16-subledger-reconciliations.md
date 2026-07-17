# Subledger Reconciliations Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the `/audit/reconciliations` screen (the third Assurance ▸ Audit leaf) — for every dimensioned control account in the chart, show whether its subsidiary ledger ties out to the GL control balance, with an expandable per-dimension-value breakdown.

**Architecture:** One new `audit.read`-gated aggregating endpoint `GET /clients/{id}/subledger/reconciliations` that walks the chart (every account with `RequiredDimensions`), reconciles each against each required dimension via the existing folds, and returns a labeled list. The FE loads it once and renders a per-row tie-out table; a variance row expands to lazy-load the breakdown from the existing `/subledger` endpoint. The existing `/subledger` and `/subledger/reconciliation` endpoints are untouched.

**Tech Stack:** .NET 10 (minimal APIs, xUnit + EphemeralMongo via `ApiFixture`); Angular 22 (standalone, OnPush, zoneless, signals), Tailwind v4, Spartan Helm; Vitest + TestBed.

## Global Constraints

- **Backend:** namespaces follow folder structure. The new endpoint is ADDITIVE — the existing `/subledger` and `/subledger/reconciliation` (routes, `gl.read` gating, behavior) are NOT touched. `audit.read` via `gateway.ResolveCapabilityAsync(user, clientId, Capabilities.AuditRead, ct)` (the method the audit endpoints use; no `Permission`-map changes). Discovery is chart-driven (`Account.RequiredDimensions`), no module coupling. **Rider auto-converts explicit types to `var`** — stage the explicit file list per task and check `git diff --cached --stat` for stray churn before each commit.
- The new route `/subledger/reconciliations` (plural) is distinct from the existing `/subledger/reconciliation` (singular) — separate literal segments, no routing conflict.
- **Frontend:** standalone, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. No new route guard (nav-gate `area: 'audit'` → `audit.read`, already in place, + backend 403). FE test runner is **Vitest** (`vi.fn`/`vi.spyOn` global). Conditional Tailwind classes with special chars (`hover:bg-muted/50`) use `[class]="cond ? '…' : ''"`, never `[class.hover:bg-muted/50]`.
- **Wire shapes** identical backend record ↔ FE interface (host `JsonNamingPolicy.CamelCase`): `SubledgerReconciliationsResponse{ asOf, lines }`; `SubledgerReconciliationLine{ account, number, name, dimension, controlBalance, subledgerTotal, variance, tiesOut }`; `SubledgerResponse{ dimension, asOf, lines }`; `SubledgerLineResponse{ accountId, dimensionValue, balance, number, name }`.
- The `built` array in `app.routes.ts` gets EXACTLY `'/audit/reconciliations'` added (so this leaf leaves the Placeholder fallback; the audit-screens leaves stay as they are).
- Only touch files named per task. Do NOT change existing subledger/audit endpoints, the audit-trail/verify screens, or the Bank Reconciliation area.
- `environment.ts` stays modified/uncommitted (never commit).
- Branch `feat/subledger-reconciliations`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: Backend — aggregating reconciliations endpoint

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/SubledgerContracts.cs` (add the two records)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (route + handler)
- Test (create): `Backend/Accounting101.Ledger.Api.Tests/SubledgerReconciliationsTests.cs`

**Interfaces:**
- Consumes: `ctx.Ledger.Accounts.GetChartAsync → ChartOfAccounts` (`.Accounts` = `IReadOnlyCollection<Account>`, `Account.RequiredDimensions`/`.Number`/`.Name`/`.Id`), `ctx.Ledger.Journal.AggregateBalancesAsync(clientId, asOf, ct) → IReadOnlyDictionary<Guid,decimal>`, `ctx.Ledger.Journal.AggregateSubledgerAsync(clientId, dimension, account, asOf, cancellationToken:) → IReadOnlyList<SubledgerBalance>` (`.Balance`), `gateway.ResolveCapabilityAsync`, `Capabilities.AuditRead`.
- Produces: `SubledgerReconciliationsResponse(DateOnly? AsOf, IReadOnlyList<SubledgerReconciliationLine> Lines)`; `SubledgerReconciliationLine(Guid Account, string? Number, string? Name, string Dimension, decimal ControlBalance, decimal SubledgerTotal, decimal Variance, bool TiesOut)`; route `GET /clients/{id}/subledger/reconciliations?asOf=`.

- [ ] **Step 1: Write the failing tests**

Create `Backend/Accounting101.Ledger.Api.Tests/SubledgerReconciliationsTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The chart-driven aggregating reconciliations endpoint: every dimensioned control account tied
/// out per dimension, drift surfaced as a variance, non-control accounts absent, and audit.read gating.</summary>
public sealed class SubledgerReconciliationsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> PutAccountAsync(HttpClient http, Guid clientId, AccountRequest request)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", request)).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task PostAsync(HttpClient http, Guid client, PostEntryRequest entry)
    {
        PostEntryResponse created = (await (await http.PostAsJsonAsync($"/clients/{client}/entries", entry))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    private static PostEntryRequest ArSale(Guid ar, Guid revenue, decimal amount, Dictionary<string, Guid>? dims) =>
        new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(ar, "Debit", amount, Dimensions: dims),
             new PostLineRequest(revenue, "Credit", amount)]);

    [Fact]
    public async Task Lists_a_tying_control_account_per_dimension()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid ar = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" });
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });
        Guid custA = Guid.NewGuid(), custB = Guid.NewGuid();
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 100m, new() { ["Customer"] = custA }));
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 60m, new() { ["Customer"] = custB }));

        SubledgerReconciliationsResponse resp = (await c.Http.GetFromJsonAsync<SubledgerReconciliationsResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliations"))!;

        SubledgerReconciliationLine line = Assert.Single(resp.Lines, l => l.Account == ar);
        Assert.Equal("Customer", line.Dimension);
        Assert.Equal("1200", line.Number);
        Assert.Equal("Accounts Receivable", line.Name);
        Assert.Equal(160m, line.ControlBalance);
        Assert.Equal(160m, line.SubledgerTotal);
        Assert.Equal(0m, line.Variance);
        Assert.True(line.TiesOut);
    }

    [Fact]
    public async Task Surfaces_a_variance_from_untagged_drift()
    {
        SeededClient c = await fixture.SeedClientAsync();
        // AR starts as a PLAIN account so an untagged line can be posted, then gains the Customer dimension.
        Guid ar = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset" });
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 25m, dims: null)); // untagged — allowed while plain

        // Retroactively make AR a control account requiring Customer (no guard blocks this).
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{ar}",
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" }))
            .EnsureSuccessStatusCode();
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 100m, new() { ["Customer"] = Guid.NewGuid() })); // tagged

        SubledgerReconciliationsResponse resp = (await c.Http.GetFromJsonAsync<SubledgerReconciliationsResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliations"))!;

        SubledgerReconciliationLine line = Assert.Single(resp.Lines, l => l.Account == ar);
        Assert.Equal(125m, line.ControlBalance);   // 25 untagged + 100 tagged
        Assert.Equal(100m, line.SubledgerTotal);   // only the tagged line carries Customer
        Assert.Equal(25m, line.Variance);          // the untagged remainder
        Assert.False(line.TiesOut);
    }

    [Fact]
    public async Task A_two_dimension_control_account_yields_a_line_per_dimension()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid ar = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimensions = ["Customer", "Invoice"] });
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 80m,
            new() { ["Customer"] = Guid.NewGuid(), ["Invoice"] = Guid.NewGuid() }));

        SubledgerReconciliationsResponse resp = (await c.Http.GetFromJsonAsync<SubledgerReconciliationsResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliations"))!;

        List<SubledgerReconciliationLine> arLines = resp.Lines.Where(l => l.Account == ar).ToList();
        Assert.Equal(2, arLines.Count);
        Assert.Contains(arLines, l => l.Dimension == "Customer" && l.TiesOut && l.ControlBalance == 80m);
        Assert.Contains(arLines, l => l.Dimension == "Invoice" && l.TiesOut && l.ControlBalance == 80m);
    }

    [Fact]
    public async Task Non_control_accounts_are_absent()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });
        await PostAsync(c.Http, c.ClientId, new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(cash, "Debit", 50m), new PostLineRequest(revenue, "Credit", 50m)]));

        SubledgerReconciliationsResponse resp = (await c.Http.GetFromJsonAsync<SubledgerReconciliationsResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliations"))!;

        Assert.DoesNotContain(resp.Lines, l => l.Account == cash);
        Assert.Empty(resp.Lines); // no dimensioned control accounts at all → empty
    }

    [Fact]
    public async Task Requires_audit_read()
    {
        SeededClient c = await fixture.SeedClientAsync();   // Controller: holds audit.read
        HttpClient arClerk = await fixture.AddMemberAsync(c.ClientId, LedgerRole.ArClerk, "AR Clerk"); // gl.read, no audit.read

        Assert.Equal(HttpStatusCode.Forbidden,
            (await arClerk.GetAsync($"/clients/{c.ClientId}/subledger/reconciliations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await c.Http.GetAsync($"/clients/{c.ClientId}/subledger/reconciliations")).StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~SubledgerReconciliationsTests"`
Expected: BUILD FAILURE — `SubledgerReconciliationsResponse`/`SubledgerReconciliationLine` do not exist.

- [ ] **Step 3: Add the response records**

In `SubledgerContracts.cs`, append:
```csharp
/// <summary>Every dimensioned control account tied out against its GL balance, one line per (account,
/// required dimension). Chart-driven: an account is a control account iff it has RequiredDimensions.</summary>
public sealed record SubledgerReconciliationsResponse(
    DateOnly? AsOf,
    IReadOnlyList<SubledgerReconciliationLine> Lines);

/// <summary>One control account reconciled on one dimension. Variance is the untagged remainder
/// (ControlBalance − SubledgerTotal); TiesOut is Variance == 0.</summary>
public sealed record SubledgerReconciliationLine(
    Guid Account, string? Number, string? Name, string Dimension,
    decimal ControlBalance, decimal SubledgerTotal, decimal Variance, bool TiesOut);
```

- [ ] **Step 4: Register the route**

In `LedgerEndpoints.cs`, after the existing `clients.MapGet("/subledger/reconciliation", GetSubledgerReconciliation);` (line 49):
```csharp
        clients.MapGet("/subledger/reconciliations", GetSubledgerReconciliations);
```

- [ ] **Step 5: Add the handler**

In `LedgerEndpoints.cs`, add next to `GetSubledgerReconciliation` (after its closing brace, ~line 754). Ensure `using Accounting101.Ledger.Api.Control;` is present in the file (it is — `Permission`/`Capabilities` live there):
```csharp
    private static async Task<IResult> GetSubledgerReconciliations(
        Guid clientId, DateOnly? asOf, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveCapabilityAsync(user, clientId, Capabilities.AuditRead, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
        IReadOnlyDictionary<Guid, decimal> balances =
            await ctx.Ledger.Journal.AggregateBalancesAsync(clientId, asOf, cancellationToken);

        List<SubledgerReconciliationLine> lines = [];
        foreach (Account account in chart.Accounts
                     .Where(a => a.RequiredDimensions.Count > 0)
                     .OrderBy(a => a.Number, StringComparer.Ordinal))
        {
            decimal control = balances.GetValueOrDefault(account.Id);
            foreach (string dimension in account.RequiredDimensions)
            {
                IReadOnlyList<SubledgerBalance> subledger = await ctx.Ledger.Journal.AggregateSubledgerAsync(
                    clientId, dimension, account.Id, asOf, cancellationToken: cancellationToken);
                decimal subledgerTotal = subledger.Sum(s => s.Balance);
                decimal variance = control - subledgerTotal;
                lines.Add(new SubledgerReconciliationLine(
                    account.Id, account.Number, account.Name, dimension, control, subledgerTotal, variance, variance == 0m));
            }
        }
        return Results.Ok(new SubledgerReconciliationsResponse(asOf, lines));
    }
```

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~SubledgerReconciliationsTests"`
Expected: PASS (5 tests). Sanity: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~SubledgerTests"` still green (existing endpoints untouched).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/SubledgerContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/SubledgerReconciliationsTests.cs
git commit -m "feat(ledger): chart-driven subledger reconciliations endpoint (audit.read)"
```

---

### Task 2: Frontend — service + summary screen

**Files:**
- Create: `UI/Angular/src/app/core/subledger/subledger.ts` (interfaces)
- Create: `UI/Angular/src/app/core/subledger/subledger.service.ts`
- Create: `UI/Angular/src/app/features/audit/subledger-reconciliations.ts`
- Create: `UI/Angular/src/app/features/audit/subledger-reconciliations.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (route + `built` array)

**Interfaces:**
- Consumes: Task 1's `SubledgerReconciliationsResponse` wire shape; `ClientContextService`, `environment`, `money`.
- Produces: `SubledgerService.reconciliations()` + `.breakdown()` (breakdown consumed in Task 3); `SubledgerReconciliations` component; route `/audit/reconciliations`.

- [ ] **Step 1: Write the failing spec**

Create `subledger-reconciliations.spec.ts`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { SubledgerReconciliations } from './subledger-reconciliations';
import { SubledgerService } from '../../core/subledger/subledger.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { SubledgerReconciliationsResponse } from '../../core/subledger/subledger';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000003';

const resp: SubledgerReconciliationsResponse = {
  asOf: null,
  lines: [
    { account: 'ar', number: '1200', name: 'Accounts Receivable', dimension: 'Customer',
      controlBalance: 160, subledgerTotal: 160, variance: 0, tiesOut: true },
    { account: 'inv', number: '1400', name: 'Inventory', dimension: 'Item',
      controlBalance: 300, subledgerTotal: 250, variance: 50, tiesOut: false },
  ],
};

async function boot(response = resp) {
  const stub = { reconciliations: vi.fn().mockReturnValue(of(response)), breakdown: vi.fn() };
  await TestBed.configureTestingModule({
    imports: [SubledgerReconciliations],
    providers: [provideZonelessChangeDetection(), { provide: SubledgerService, useValue: stub }],
  }).compileComponents();
  TestBed.inject(ClientContextService).select(clientId);
  const f = TestBed.createComponent(SubledgerReconciliations);
  f.detectChanges(); await f.whenStable(); f.detectChanges();
  return { f, stub };
}

describe('SubledgerReconciliations', () => {
  it('renders a row per line with balances and a ties-out / variance badge', async () => {
    const { f } = await boot();
    const el = f.nativeElement as HTMLElement;
    expect(el.querySelectorAll('tbody tr').length).toBe(2);
    expect(el.textContent).toContain('Accounts Receivable');
    expect(el.textContent).toContain('160.00');
    expect(el.textContent).toContain('Ties out');
    expect(el.textContent).toContain('50.00');    // inventory variance
    expect(el.textContent).toContain('Variance');
  });

  it('shows an empty state when there are no dimensioned control accounts', async () => {
    const { f } = await boot({ asOf: null, lines: [] });
    expect((f.nativeElement as HTMLElement).textContent).toContain('No dimensioned control accounts');
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run (from `UI/Angular`): `npx ng test --include='**/subledger-reconciliations.spec.ts' --watch=false` → FAIL (cannot resolve `./subledger-reconciliations`).

- [ ] **Step 3: Add the interfaces**

Create `core/subledger/subledger.ts`:
```ts
export interface SubledgerReconciliationLine {
  account: string; number: string | null; name: string | null; dimension: string;
  controlBalance: number; subledgerTotal: number; variance: number; tiesOut: boolean;
}
export interface SubledgerReconciliationsResponse { asOf: string | null; lines: SubledgerReconciliationLine[]; }

export interface SubledgerLineResponse { accountId: string; dimensionValue: string; balance: number; number: string | null; name: string | null; }
export interface SubledgerResponse { dimension: string; asOf: string | null; lines: SubledgerLineResponse[]; }
```

- [ ] **Step 4: Add the service**

Create `core/subledger/subledger.service.ts`:
```ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { SubledgerReconciliationsResponse, SubledgerResponse } from './subledger';

@Injectable({ providedIn: 'root' })
export class SubledgerService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path: string): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  reconciliations(): Observable<SubledgerReconciliationsResponse> {
    return this.http.get<SubledgerReconciliationsResponse>(this.base('/subledger/reconciliations'));
  }

  breakdown(account: string, dimension: string): Observable<SubledgerResponse> {
    return this.http.get<SubledgerResponse>(this.base(`/subledger?account=${account}&dimension=${dimension}`));
  }
}
```

- [ ] **Step 5: Create the summary component**

Create `features/audit/subledger-reconciliations.ts`:
```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { SubledgerService } from '../../core/subledger/subledger.service';
import { SubledgerReconciliationLine } from '../../core/subledger/subledger';
import { money as fmtMoney } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-subledger-reconciliations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <h1 class="text-2xl font-bold">Subledger Reconciliations</h1>
      <p class="text-sm text-muted-foreground">
        Each dimensioned control account tied out to its general-ledger balance. A variance is the
        remainder posted to the account without the dimension tag.
      </p>

      @if (loading()) { <p class="text-muted-foreground text-sm">Loading…</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (!loading() && !error()) {
        @if (rows().length === 0) {
          <p class="text-muted-foreground text-sm">No dimensioned control accounts to reconcile.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr><th hlmTh>Account</th><th hlmTh>Dimension</th><th hlmTh class="text-right">Control</th><th hlmTh class="text-right">Subledger</th><th hlmTh class="text-right">Variance</th><th hlmTh>Status</th></tr>
              </thead>
              <tbody hlmTBody>
                @for (r of rows(); track key(r)) {
                  <tr hlmTr>
                    <td hlmTd>{{ r.number }} {{ r.name }}</td>
                    <td hlmTd>{{ r.dimension }}</td>
                    <td hlmTd class="text-right tabular-nums">{{ money(r.controlBalance) }}</td>
                    <td hlmTd class="text-right tabular-nums">{{ money(r.subledgerTotal) }}</td>
                    <td hlmTd class="text-right tabular-nums" [class.text-destructive]="!r.tiesOut">{{ money(r.variance) }}</td>
                    <td hlmTd>
                      @if (r.tiesOut) {
                        <span class="text-sm px-2 py-0.5 rounded border border-border text-green-700 dark:text-green-400">Ties out</span>
                      } @else {
                        <span class="text-sm px-2 py-0.5 rounded border border-destructive text-destructive">Variance</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      }
    </div>
  `,
})
export class SubledgerReconciliations {
  private readonly svc = inject(SubledgerService);
  private readonly destroyRef = inject(DestroyRef);

  readonly rows = signal<SubledgerReconciliationLine[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor() {
    this.svc.reconciliations().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (r) => { this.rows.set(r.lines); this.loading.set(false); },
      error: (e) => { this.error.set(extractProblem(e).detail); this.loading.set(false); },
    });
  }

  key(r: SubledgerReconciliationLine): string { return `${r.account}|${r.dimension}`; }
  money(n: number): string { return fmtMoney(n); }
}
```

- [ ] **Step 6: Add the route**

In `app.routes.ts`:
- Import: `import { SubledgerReconciliations } from './features/audit/subledger-reconciliations';`
- Add the route next to the other `audit/*` routes (after `audit/verify`):
```ts
  { path: 'audit/reconciliations', component: SubledgerReconciliations },
```
- Add `'/audit/reconciliations'` to the `built` array (now `..., '/audit/trail', '/audit/verify', '/audit/reconciliations'`).

- [ ] **Step 7: Run the spec + compile gate**

Run (from `UI/Angular`): `npx ng test --include='**/subledger-reconciliations.spec.ts' --watch=false` → 2 specs PASS.
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/subledger/subledger.ts UI/Angular/src/app/core/subledger/subledger.service.ts UI/Angular/src/app/features/audit/subledger-reconciliations.ts UI/Angular/src/app/features/audit/subledger-reconciliations.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): subledger reconciliations summary screen"
```

---

### Task 3: Frontend — expandable breakdown drill

**Files:**
- Modify: `UI/Angular/src/app/features/audit/subledger-reconciliations.ts` (expand + lazy breakdown)
- Modify (extend): `UI/Angular/src/app/features/audit/subledger-reconciliations.spec.ts`

**Interfaces:**
- Consumes: Task 2's `SubledgerService.breakdown(account, dimension)` → `SubledgerResponse`; `SubledgerLineResponse`.
- Produces: nothing downstream.

**Note:** each row becomes a clickable toggle that lazy-loads and caches the per-dimension-value breakdown; the expanded panel lists the tagged values and, when `variance !== 0`, an explicit "Untagged remainder … {variance}" line (the variance is composed of lines lacking the dimension, so it never appears among the tagged values).

- [ ] **Step 1: Write the failing test**

Add to `subledger-reconciliations.spec.ts`:
- Add `SubledgerResponse` to the imports from `'../../core/subledger/subledger'`.
- Add this test inside `describe('SubledgerReconciliations', ...)`:
```ts
  it('expands a variance row to lazy-load its breakdown and shows the untagged remainder', async () => {
    const breakdown: SubledgerResponse = {
      dimension: 'Item', asOf: null,
      lines: [
        { accountId: 'inv', dimensionValue: 'i1', balance: 150, number: 'WIDGET', name: 'Widget' },
        { accountId: 'inv', dimensionValue: 'i2', balance: 100, number: 'GADGET', name: 'Gadget' },
      ],
    };
    const stub = { reconciliations: vi.fn().mockReturnValue(of(resp)), breakdown: vi.fn().mockReturnValue(of(breakdown)) };
    await TestBed.configureTestingModule({
      imports: [SubledgerReconciliations],
      providers: [provideZonelessChangeDetection(), { provide: SubledgerService, useValue: stub }],
    }).compileComponents();
    TestBed.inject(ClientContextService).select(clientId);
    const f = TestBed.createComponent(SubledgerReconciliations);
    f.detectChanges(); await f.whenStable(); f.detectChanges();

    const invRow = [...(f.nativeElement as HTMLElement).querySelectorAll('tbody tr')]
      .find(tr => tr.textContent!.includes('Inventory')) as HTMLElement;
    invRow.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable(); f.detectChanges();
    invRow.dispatchEvent(new MouseEvent('click', { bubbles: true }));   // toggle off then on again below — no re-fetch
    f.detectChanges();
    invRow.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable(); f.detectChanges();

    expect(stub.breakdown).toHaveBeenCalledTimes(1);                    // cached across expands
    expect(stub.breakdown).toHaveBeenCalledWith('inv', 'Item');
    const text = (f.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('WIDGET');
    expect(text).toContain('150.00');
    expect(text).toContain('Untagged remainder');
    expect(text).toContain('50.00');   // the variance
  });
```

- [ ] **Step 2: Run to verify it fails**

Run (from `UI/Angular`): `npx ng test --include='**/subledger-reconciliations.spec.ts' --watch=false` → the new spec FAILS (rows not clickable → `breakdown` never called).

- [ ] **Step 3: Wire the expand + lazy breakdown**

In `subledger-reconciliations.ts`:

**3a.** Extend the imports:
```ts
import { SubledgerReconciliationLine, SubledgerResponse } from '../../core/subledger/subledger';
```

**3b.** Make each summary `<tr>` a clickable toggle and add an expanded panel row. Replace the `@for (r of rows(); track key(r)) { … }` block's `<tr>` with:
```html
                @for (r of rows(); track key(r)) {
                  <tr hlmTr role="button" tabindex="0" class="cursor-pointer hover:bg-muted/50"
                      (click)="toggle(r)" (keydown.enter)="toggle(r)">
                    <td hlmTd>{{ expanded().has(key(r)) ? '▾' : '▸' }} {{ r.number }} {{ r.name }}</td>
                    <td hlmTd>{{ r.dimension }}</td>
                    <td hlmTd class="text-right tabular-nums">{{ money(r.controlBalance) }}</td>
                    <td hlmTd class="text-right tabular-nums">{{ money(r.subledgerTotal) }}</td>
                    <td hlmTd class="text-right tabular-nums" [class.text-destructive]="!r.tiesOut">{{ money(r.variance) }}</td>
                    <td hlmTd>
                      @if (r.tiesOut) {
                        <span class="text-sm px-2 py-0.5 rounded border border-border text-green-700 dark:text-green-400">Ties out</span>
                      } @else {
                        <span class="text-sm px-2 py-0.5 rounded border border-destructive text-destructive">Variance</span>
                      }
                    </td>
                  </tr>
                  @if (expanded().has(key(r))) {
                    <tr hlmTr>
                      <td hlmTd colspan="6" class="bg-muted/30">
                        @if (breakdowns()[key(r)]; as b) {
                          @if (b === 'error') {
                            <p class="text-destructive text-sm">Could not load the breakdown.</p>
                          } @else if (b === 'loading') {
                            <p class="text-muted-foreground text-sm">Loading…</p>
                          } @else {
                            <table class="text-sm w-full max-w-lg">
                              <tbody>
                                @for (l of b.lines; track l.dimensionValue) {
                                  <tr>
                                    <td class="py-0.5">{{ l.number ?? l.name ?? l.dimensionValue }}</td>
                                    <td class="py-0.5 text-right tabular-nums">{{ money(l.balance) }}</td>
                                  </tr>
                                }
                                @if (r.variance !== 0) {
                                  <tr class="border-t border-border text-destructive">
                                    <td class="py-0.5">Untagged remainder (no {{ r.dimension }})</td>
                                    <td class="py-0.5 text-right tabular-nums">{{ money(r.variance) }}</td>
                                  </tr>
                                }
                              </tbody>
                            </table>
                          }
                        }
                      </td>
                    </tr>
                  }
                }
```

**3c.** Add the expand state + lazy loader to the class (after `key`/`money`):
```ts
  readonly expanded = signal<Set<string>>(new Set());
  readonly breakdowns = signal<Record<string, SubledgerResponse | 'loading' | 'error'>>({});

  toggle(r: SubledgerReconciliationLine): void {
    const k = this.key(r);
    const next = new Set(this.expanded());
    if (next.has(k)) { next.delete(k); this.expanded.set(next); return; }
    next.add(k);
    this.expanded.set(next);
    if (this.breakdowns()[k]) return;   // already loaded/loading — do not re-fetch
    this.breakdowns.update((m) => ({ ...m, [k]: 'loading' }));
    this.svc.breakdown(r.account, r.dimension).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (b) => this.breakdowns.update((m) => ({ ...m, [k]: b })),
      error: () => this.breakdowns.update((m) => ({ ...m, [k]: 'error' })),
    });
  }
```

- [ ] **Step 4: Run the spec + compile gate**

Run (from `UI/Angular`): `npx ng test --include='**/subledger-reconciliations.spec.ts' --watch=false` → all specs PASS (2 from Task 2 + 1 new).
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/audit/subledger-reconciliations.ts UI/Angular/src/app/features/audit/subledger-reconciliations.spec.ts
git commit -m "feat(ui): subledger-reconciliations expandable per-value breakdown"
```

---

## Self-Review

**Spec coverage:**
- New aggregating endpoint (chart-driven, `audit.read`-gated, reuses folds) + DTOs → Task 1. ✓
- Existing `/subledger`+`/subledger/reconciliation` untouched → Task 1 changes only add a route+handler+records; sanity-runs `SubledgerTests`. ✓
- Summary screen (per-row control/subledger/variance/ties-out badge, empty state) + service + interfaces + route/`built` → Task 2. ✓
- Expandable breakdown (lazy `/subledger`, cached, untagged-remainder line, inline error) → Task 3. ✓
- Gating: nav `area:'audit'` (existing) + backend 403; endpoint proven `audit.read` via the ArClerk test → Task 1/2. ✓
- Testing: ties-out, variance-from-drift, two-dimension, non-control absence + empty, `audit.read` gating (backend); render+badges+empty (FE Task 2); expand lazy-loads-once + values + remainder (FE Task 3). ✓

**Placeholder scan:** every step contains complete code; no TBD.

**Type/name consistency:** `SubledgerReconciliationLine`/`SubledgerReconciliationsResponse` field names identical backend record ↔ FE interface ↔ wire (host camelCase); `SubledgerResponse`/`SubledgerLineResponse` reused from the existing `/subledger` shape; `reconciliations()`/`breakdown()`/`GetSubledgerReconciliations`/`ResolveCapabilityAsync`/`Capabilities.AuditRead` names consistent; `key(r) = account|dimension` is the single row/expand/cache key throughout; the `built` array gains exactly `'/audit/reconciliations'`; the drift-variance test relies on `ChartService.UpsertAsync` having no guard against adding `RequiredDimensions` post-hoc (verified). Task 3's `<tr>` replacement is a superset of Task 2's `<tr>` (adds click/keydown/caret + the expanded panel), so the render/badge assertions from Task 2 stay valid.

## Execution Handoff

Two execution options:
1. **Subagent-Driven (recommended)** — fresh implementer per task, per-task review, final whole-branch review.
2. **Inline Execution** — execute in this session with checkpoints.
