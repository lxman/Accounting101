# Period Close Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the General Ledger ▸ Period Close screen (`/periods`): show current close state, run a guided monthly close (with a month-picker fallback), resolve pending-entry blockers, and run the year-end close — no reopen.

**Architecture:** One new `gl.read` read endpoint `GET /clients/{id}/periods/status` surfaces the current closed-through date + fiscal-year-end month (backed by a one-line `LedgerService` pass-through). The Angular screen loads it, drives the existing `POST /periods/close` and `/periods/close-year` endpoints, handles their three 409 shapes (blockers / fiscal-year-end steer / already-closed), and reuses the entry-detail screen for blocker resolution. Reopen is intentionally omitted; corrections flow through an adjusting entry in the open period.

**Tech Stack:** .NET 10 (minimal APIs, xUnit + EphemeralMongo via `ApiFixture`); Angular 22 (standalone, OnPush, zoneless, signals), Tailwind v4, Spartan Helm; Vitest + TestBed.

## Global Constraints

- **Backend:** namespaces follow folder structure. ADDITIVE — the existing `/periods/close`, `/periods/close-year`, `/periods/reopen` routes/handlers are NOT touched. `gl.read` via `gateway.ResolveAsync(user, clientId, Permission.Read, ct)` (the gate every GL read endpoint uses). **Rider auto-converts explicit types to `var`** — stage the explicit file list per task and check `git diff --cached --stat` for stray churn before each commit.
- **Frontend:** standalone, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. FE test runner is **Vitest** (`vi.fn`/`vi.spyOn` global). Conditional Tailwind classes with special chars (`hover:bg-muted/50`) use `[class]="cond ? '…' : ''"`, never `[class.hover:bg-muted/50]`. Capability gating via the existing `*appCan` structural directive.
- **Wire shapes** identical backend record ↔ FE interface (host `JsonNamingPolicy.CamelCase`): `PeriodStatusResponse{ closedThrough, fiscalYearEndMonth }`; existing `PendingEntryRef{ entryId, reference, effectiveDate, type }`; the close request bodies are `{ asOf }` and `{ fiscalYearEnd }`.
- **Verify prod build:** the container build runs `ng build` (production, budgets enforced). Gate FE tasks on `npx ng build` (NOT `--configuration development`, which skips budgets).
- The `built` array in `app.routes.ts` gets EXACTLY `'/periods'` added.
- The `PeriodClose` screen is a normal content page (not a list) — it does NOT use the full-height list frame.
- `environment.ts` stays modified/uncommitted (never commit).
- Branch `feat/period-close-screen` (already created; the spec is committed on it). Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: Backend — period status read endpoint

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/EntryResponses.cs` (add `PeriodStatusResponse`)
- Modify: `Backend/Accounting101.Ledger.Mongo/LedgerService.cs` (add `GetClosedThroughAsync` pass-through)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (route + handler)
- Test (create): `Backend/Accounting101.Ledger.Api.Tests/PeriodStatusTests.cs`

**Interfaces:**
- Consumes: `ctx.Ledger.Service.GetClosedThroughAsync(clientId, ct) → Task<DateOnly?>` (new), `control.GetClientAsync(clientId, ct) → ClientRegistration?`, `FiscalYear.MonthOf(client) → int` (1–12, defaults 12), `gateway.ResolveAsync(user, clientId, Permission.Read, ct)`. `LedgerService._checkpoints` (`MongoCheckpointStore`) already has `GetClosedThroughAsync(Guid, CancellationToken)`.
- Produces: `PeriodStatusResponse(DateOnly? ClosedThrough, int FiscalYearEndMonth)`; route `GET /clients/{id}/periods/status`.

- [ ] **Step 1: Write the failing tests**

Create `Backend/Accounting101.Ledger.Api.Tests/PeriodStatusTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The period-status read endpoint: closed-through is null before any close and the closed date
/// after one; the fiscal-year-end month is reported; the endpoint requires membership (gl.read).</summary>
public sealed class PeriodStatusTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Reports_null_closed_through_and_the_fiscal_month_before_any_close()
    {
        SeededClient c = await fixture.SeedClientAsync();

        PeriodStatusResponse resp = (await c.Http.GetFromJsonAsync<PeriodStatusResponse>(
            $"/clients/{c.ClientId}/periods/status"))!;

        Assert.Null(resp.ClosedThrough);
        Assert.Equal(12, resp.FiscalYearEndMonth); // seed leaves FiscalYearEndMonth unset → FiscalYear.MonthOf → 12
    }

    [Fact]
    public async Task Reports_the_closed_through_date_after_a_close()
    {
        SeededClient c = await fixture.SeedClientAsync();
        DateOnly asOf = new(2026, 3, 31);
        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/periods/close", new ClosePeriodRequest(asOf)))
            .EnsureSuccessStatusCode();

        PeriodStatusResponse resp = (await c.Http.GetFromJsonAsync<PeriodStatusResponse>(
            $"/clients/{c.ClientId}/periods/status"))!;

        Assert.Equal(asOf, resp.ClosedThrough);
    }

    [Fact]
    public async Task Requires_membership()
    {
        SeededClient c = await fixture.SeedClientAsync();                       // Controller: member, has gl.read
        HttpClient stranger = fixture.ClientFor(Guid.NewGuid(), "Stranger", ("role", "Controller")); // not a member

        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/clients/{c.ClientId}/periods/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await c.Http.GetAsync($"/clients/{c.ClientId}/periods/status")).StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PeriodStatusTests"`
Expected: BUILD FAILURE — `PeriodStatusResponse` does not exist.

- [ ] **Step 3: Add the response record**

In `EntryResponses.cs`, after `CloseYearResponse` (~line 56), append:
```csharp
/// <summary>Current period state: the date the ledger is closed through (null if never closed) and the
/// client's fiscal-year-end month (1–12), so the UI can identify the year-end close date.</summary>
public sealed record PeriodStatusResponse(DateOnly? ClosedThrough, int FiscalYearEndMonth);
```

- [ ] **Step 4: Expose closed-through on the service**

In `LedgerService.cs`, next to the other public read methods (e.g. near `EnsureOpenForPostAsync`), add:
```csharp
/// <summary>The date the client's ledger is frozen through, or null if no period has been closed.</summary>
public Task<DateOnly?> GetClosedThroughAsync(Guid clientId, CancellationToken cancellationToken = default)
    => _checkpoints.GetClosedThroughAsync(clientId, cancellationToken);
```

- [ ] **Step 5: Register the route**

In `LedgerEndpoints.cs`, next to the existing `clients.MapPost("/periods/close", ClosePeriod);` (~line 38), add:
```csharp
        clients.MapGet("/periods/status", GetPeriodStatus);
```

- [ ] **Step 6: Add the handler**

In `LedgerEndpoints.cs`, add next to `ClosePeriod` (its namespace already has `Permission`, `FiscalYear`, `ControlStore`, `ClientRegistration`, `LedgerContext` in scope):
```csharp
    private static async Task<IResult> GetPeriodStatus(
        Guid clientId, LedgerGateway gateway, ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        DateOnly? closedThrough = await ctx.Ledger.Service.GetClosedThroughAsync(clientId, cancellationToken);
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        int fiscalMonth = client is null ? 12 : FiscalYear.MonthOf(client);
        return Results.Ok(new PeriodStatusResponse(closedThrough, fiscalMonth));
    }
```

- [ ] **Step 7: Run to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PeriodStatusTests"`
Expected: PASS (3 tests). Sanity: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~PeriodCloseApiTests"` still green (existing close endpoints untouched).

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/EntryResponses.cs Backend/Accounting101.Ledger.Mongo/LedgerService.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/PeriodStatusTests.cs
git commit -m "feat(ledger): period status read endpoint (closed-through + fiscal month)"
```

---

### Task 2: Frontend — service + period-close screen (status + monthly close)

**Files:**
- Create: `UI/Angular/src/app/core/periods/periods.ts` (interfaces)
- Create: `UI/Angular/src/app/core/periods/periods.service.ts`
- Create: `UI/Angular/src/app/features/periods/period-close.ts`
- Create: `UI/Angular/src/app/features/periods/period-close.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (route + `built` array)

**Interfaces:**
- Consumes: Task 1's `PeriodStatusResponse` wire shape; `ClientContextService`, `environment`, `displayDate`, `extractProblem`, `CanDirective`.
- Produces: `PeriodsService.status()/close(asOf)/closeYear(fiscalYearEnd)`; `PeriodClose` component with public methods used by Task 3 (`load()`, `date()`, `monthName()`, `lastDay(year,month)`, `nextPeriod()`, `nextEnd()`, `nextLabel()`); signals `status`, `loading`, `loadError`, `actionError`, `busy`; route `/periods`.

- [ ] **Step 1: Write the failing spec**

Create `UI/Angular/src/app/features/periods/period-close.spec.ts`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { PeriodClose } from './period-close';
import { PeriodsService } from '../../core/periods/periods.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { PeriodStatus } from '../../core/periods/periods';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000009';

async function boot(status: PeriodStatus, caps: string[] = ['gl.read', 'gl.close'], stubOverrides: Partial<Record<string, unknown>> = {}) {
  const stub = {
    status: vi.fn().mockReturnValue(of(status)),
    close: vi.fn().mockReturnValue(of({ asOf: '2026-06-30', openingBalances: [] })),
    closeYear: vi.fn().mockReturnValue(of({ closingEntry: null })),
    ...stubOverrides,
  };
  await TestBed.configureTestingModule({
    imports: [PeriodClose],
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideCapabilities(...caps), { provide: PeriodsService, useValue: stub }],
  }).compileComponents();
  TestBed.inject(ClientContextService).select(clientId);
  const f = TestBed.createComponent(PeriodClose);
  f.detectChanges(); await f.whenStable(); f.detectChanges();
  return { f, stub };
}

describe('PeriodClose', () => {
  it('shows the closed-through date and fiscal year-end', async () => {
    const { f } = await boot({ closedThrough: '2026-05-31', fiscalYearEndMonth: 12 });
    const text = (f.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Closed through');
    expect(text).toContain('December');
    expect(text).toContain('Closed periods are final');
  });

  it('shows an empty state when nothing has been closed', async () => {
    const { f } = await boot({ closedThrough: null, fiscalYearEndMonth: 12 });
    expect((f.nativeElement as HTMLElement).textContent).toContain('No periods have been closed yet');
  });

  it('closes the next month after the closed-through date and refreshes', async () => {
    const { f, stub } = await boot({ closedThrough: '2026-05-31', fiscalYearEndMonth: 12 });
    const el = f.nativeElement as HTMLElement;
    expect(el.textContent).toContain('June 2026');
    const btn = [...el.querySelectorAll('button')].find(b => b.textContent!.includes('Close June 2026'))!;
    btn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable();
    expect(stub.close).toHaveBeenCalledWith('2026-06-30');
    expect(stub.status).toHaveBeenCalledTimes(2); // initial load + refresh after close
  });

  it('hides the close controls without gl.close', async () => {
    const { f } = await boot({ closedThrough: '2026-05-31', fiscalYearEndMonth: 12 }, ['gl.read']);
    const el = f.nativeElement as HTMLElement;
    expect([...el.querySelectorAll('button')].some(b => b.textContent!.includes('Close June 2026'))).toBe(false);
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run (from `UI/Angular`): `npx ng test --include='**/period-close.spec.ts' --watch=false` → FAIL (cannot resolve `./period-close`).

- [ ] **Step 3: Add the interfaces**

Create `core/periods/periods.ts`:
```ts
export interface PeriodStatus { closedThrough: string | null; fiscalYearEndMonth: number; }
export interface PendingEntryRef { entryId: string; reference: string | null; effectiveDate: string; type: string; }
export interface CloseResponse { asOf: string; openingBalances: { accountId: string; balance: number; number: string | null; name: string | null; }[]; }
export interface CloseYearResponse { closingEntry: { id: string } | null; }
```

- [ ] **Step 4: Add the service**

Create `core/periods/periods.service.ts`:
```ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { PeriodStatus, CloseResponse, CloseYearResponse } from './periods';

@Injectable({ providedIn: 'root' })
export class PeriodsService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path: string): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  status(): Observable<PeriodStatus> {
    return this.http.get<PeriodStatus>(this.base('/periods/status'));
  }

  close(asOf: string): Observable<CloseResponse> {
    return this.http.post<CloseResponse>(this.base('/periods/close'), { asOf });
  }

  closeYear(fiscalYearEnd: string): Observable<CloseYearResponse> {
    return this.http.post<CloseYearResponse>(this.base('/periods/close-year'), { fiscalYearEnd });
  }
}
```

- [ ] **Step 5: Create the screen (status + monthly close)**

Create `features/periods/period-close.ts`:
```ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { PeriodsService } from '../../core/periods/periods.service';
import { PeriodStatus } from '../../core/periods/periods';
import { displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December'];

@Component({
  selector: 'app-period-close',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Period Close</h1>

      @if (loading()) { <p class="text-muted-foreground text-sm">Loading…</p> }
      @if (loadError()) { <p class="text-destructive text-sm">{{ loadError() }}</p> }

      @if (status(); as s) {
        <div class="rounded-lg border border-border p-4 flex flex-col gap-1">
          @if (s.closedThrough) {
            <p class="text-sm">Closed through <span class="font-semibold">{{ date(s.closedThrough) }}</span></p>
          } @else {
            <p class="text-sm text-muted-foreground">No periods have been closed yet.</p>
          }
          <p class="text-xs text-muted-foreground">Fiscal year ends in {{ monthName(s.fiscalYearEndMonth) }}.</p>
        </div>

        <p class="text-xs text-muted-foreground">
          Closed periods are final. To correct a closed period, post an adjusting entry in the current period.
        </p>

        <div class="rounded-lg border border-border p-4 flex flex-col gap-3">
          <p class="text-sm">Next period to close: <span class="font-semibold">{{ nextLabel() }}</span></p>
          @if (actionError()) { <p class="text-destructive text-sm">{{ actionError() }}</p> }

          <button type="button" *appCan="'gl.close'"
                  class="self-start text-sm px-3 py-1.5 rounded-lg bg-primary text-primary-foreground disabled:opacity-50"
                  [disabled]="busy()" (click)="closeNext()">
            Close {{ nextLabel() }}
          </button>

          <div class="flex items-center gap-2 text-sm" *appCan="'gl.close'">
            <span class="text-muted-foreground">Other period:</span>
            <select class="rounded-md border border-border bg-background px-2 py-1"
                    (change)="pickMonth.set(+$any($event.target).value)">
              @for (m of months; track m.value) { <option [value]="m.value" [selected]="m.value === pickMonth()">{{ m.label }}</option> }
            </select>
            <select class="rounded-md border border-border bg-background px-2 py-1"
                    (change)="pickYear.set(+$any($event.target).value)">
              @for (y of years(); track y) { <option [value]="y" [selected]="y === pickYear()">{{ y }}</option> }
            </select>
            <button type="button" class="text-sm px-3 py-1.5 rounded-lg border border-border disabled:opacity-50"
                    [disabled]="busy()" (click)="closePicked()">Close</button>
          </div>
        </div>
      }
    </div>
  `,
})
export class PeriodClose {
  protected readonly svc = inject(PeriodsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly status = signal<PeriodStatus | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly busy = signal(false);

  readonly months = MONTHS.map((label, i) => ({ value: i + 1, label }));
  readonly pickMonth = signal(1);
  readonly pickYear = signal(2026);
  readonly years = computed(() => { const y = this.pickYear(); return [y - 2, y - 1, y, y + 1]; });

  constructor() { this.load(); }

  load(): void {
    this.loading.set(true);
    this.actionError.set(null);
    this.svc.status().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (s) => {
        this.status.set(s);
        const np = this.nextPeriod();
        this.pickMonth.set(np.month);
        this.pickYear.set(np.year);
        this.loading.set(false);
      },
      error: (e) => { this.loadError.set(extractProblem(e).detail); this.loading.set(false); },
    });
  }

  date(d: string): string { return displayDate(d); }
  monthName(m: number): string { return MONTHS[m - 1] ?? String(m); }

  lastDay(year: number, month: number): string {
    const d = new Date(Date.UTC(year, month, 0)); // day 0 of the next month = last day of `month`
    return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`;
  }

  nextPeriod(): { year: number; month: number } {
    const ct = this.status()?.closedThrough;
    if (ct) {
      const [y, m] = ct.split('-').map(Number);
      return m >= 12 ? { year: y + 1, month: 1 } : { year: y, month: m + 1 };
    }
    const now = new Date();
    return now.getUTCMonth() === 0
      ? { year: now.getUTCFullYear() - 1, month: 12 }
      : { year: now.getUTCFullYear(), month: now.getUTCMonth() }; // 0-based current = 1-based previous
  }

  nextEnd(): string { const np = this.nextPeriod(); return this.lastDay(np.year, np.month); }
  nextLabel(): string { const np = this.nextPeriod(); return `${this.monthName(np.month)} ${np.year}`; }

  closeNext(): void { this.runClose(this.nextEnd()); }
  closePicked(): void { this.runClose(this.lastDay(this.pickYear(), this.pickMonth())); }

  protected runClose(asOf: string): void {
    this.actionError.set(null);
    this.busy.set(true);
    this.svc.close(asOf).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.load(); },
      error: (e) => { this.actionError.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
```

- [ ] **Step 6: Add the route**

In `app.routes.ts`:
- Import: `import { PeriodClose } from './features/periods/period-close';`
- Add the route next to the other GL routes (e.g. after the `trial-balance` route): `{ path: 'periods', component: PeriodClose },`
- Add `'/periods'` to the `built` array.

- [ ] **Step 7: Run the spec + compile gate**

Run (from `UI/Angular`): `npx ng test --include='**/period-close.spec.ts' --watch=false` → 4 specs PASS.
Run: `npx ng build` → `Application bundle generation complete` (production; budgets enforced).

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/periods/periods.ts UI/Angular/src/app/core/periods/periods.service.ts UI/Angular/src/app/features/periods/period-close.ts UI/Angular/src/app/features/periods/period-close.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): period close screen — status + guided monthly close"
```

---

### Task 3: Frontend — blocker resolution, fiscal-year-end steer, year-end close

**Files:**
- Modify: `UI/Angular/src/app/features/periods/period-close.ts`
- Modify (extend): `UI/Angular/src/app/features/periods/period-close.spec.ts`

**Interfaces:**
- Consumes: Task 2's `PeriodClose` (`runClose`, `nextPeriod`, `nextEnd`, `nextLabel`, `load`, `status`, `busy`, `actionError`), `PeriodsService.closeYear`, `PendingEntryRef`; `CapabilityService`, `RouterLink`.
- Produces: nothing downstream.

**Note:** a close/close-year 409 carries its extensions at the top of the error body (`err.error.blockers` / `.useEndpoint` / `.fiscalYearEnd`) — `extractProblem` only returns `detail`, so those are read directly. The guided section shows the **year-end** affordance instead of the monthly button when the next period's month is the fiscal-year-end month. A blocked close lists each pending entry linking to `/journal/{entryId}` (gl.read-gated) with a "Retry close".

- [ ] **Step 1: Write the failing tests**

Add to `period-close.spec.ts`:
- Extend the imports from `'../../core/periods/periods'` to include `PendingEntryRef` (add it): `import { PeriodStatus, PendingEntryRef } from '../../core/periods/periods';`
- Add a helper for a 409 error and these tests inside `describe('PeriodClose', …)`:
```ts
  function conflict(extensions: Record<string, unknown>) {
    return { error: { detail: 'Conflict.', ...extensions }, status: 409 };
  }

  it('lists blockers as journal links and retries the same close', async () => {
    const blockers: PendingEntryRef[] = [
      { entryId: 'e1', reference: 'ACCR-05', effectiveDate: '2026-06-30', type: 'Journal' },
      { entryId: 'e2', reference: 'BANK-05', effectiveDate: '2026-06-28', type: 'Journal' },
    ];
    const close = vi.fn()
      .mockReturnValueOnce(throwError(() => conflict({ blockers })))
      .mockReturnValueOnce(of({ asOf: '2026-06-30', openingBalances: [] }));
    const { f, stub } = await boot({ closedThrough: '2026-05-31', fiscalYearEndMonth: 12 }, ['gl.read', 'gl.close'], { close });
    const el = f.nativeElement as HTMLElement;

    [...el.querySelectorAll('button')].find(b => b.textContent!.includes('Close June 2026'))!
      .dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable(); f.detectChanges();

    expect(el.textContent).toContain('ACCR-05');
    const link = [...el.querySelectorAll('a')].find(a => a.getAttribute('href')?.includes('/journal/e1'));
    expect(link).toBeTruthy();

    [...el.querySelectorAll('button')].find(b => b.textContent!.includes('Retry close'))!
      .dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable();
    expect(close).toHaveBeenNthCalledWith(1, '2026-06-30');
    expect(close).toHaveBeenNthCalledWith(2, '2026-06-30'); // retry re-issues the same asOf
    expect(stub.status).toHaveBeenCalledTimes(2); // initial + refresh after the successful retry
  });

  it('shows the year-end affordance when the next period is the fiscal year-end', async () => {
    const { f, stub } = await boot({ closedThrough: '2026-11-30', fiscalYearEndMonth: 12 });
    const el = f.nativeElement as HTMLElement;
    expect([...el.querySelectorAll('button')].some(b => b.textContent!.includes('Close December 2026'))).toBe(false);
    const yeBtn = [...el.querySelectorAll('button')].find(b => b.textContent!.includes('Run year-end close'))!;
    expect(yeBtn).toBeTruthy();
    yeBtn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable();
    expect(stub.closeYear).toHaveBeenCalledWith('2026-12-31');
  });
```
- Add to the top-of-file imports: `import { throwError } from 'rxjs';` (merge with the existing `import { of } from 'rxjs';` → `import { of, throwError } from 'rxjs';`).

- [ ] **Step 2: Run to verify they fail**

Run (from `UI/Angular`): `npx ng test --include='**/period-close.spec.ts' --watch=false` → the two new specs FAIL (no blocker list, no year-end button).

- [ ] **Step 3a: Extend the imports + component class**

In `period-close.ts`:
- Extend the Angular/router/rxjs imports and add capability + router + `PendingEntryRef`:
```ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { PeriodsService } from '../../core/periods/periods.service';
import { PeriodStatus, PendingEntryRef } from '../../core/periods/periods';
import { displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';
import { CapabilityService } from '../../core/capabilities/capability.service';
```
- Add `RouterLink` to the `imports` array: `imports: [CanDirective, RouterLink],`
- Add these fields + computed to the class (after `busy`):
```ts
  private readonly caps = inject(CapabilityService);
  readonly canDrill = computed(() => this.caps.has('gl.read'));
  readonly blockers = signal<PendingEntryRef[] | null>(null);
  private lastAsOf: string | null = null;

  readonly isYearEndNext = computed(() => {
    const s = this.status();
    return s ? this.nextPeriod().month === s.fiscalYearEndMonth : false;
  });
```

- [ ] **Step 3b: Replace `runClose` to parse the 409 extensions, and add year-end + retry**

Replace the `runClose` method with:
```ts
  protected runClose(asOf: string): void {
    this.actionError.set(null);
    this.blockers.set(null);
    this.lastAsOf = asOf;
    this.busy.set(true);
    this.svc.close(asOf).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.load(); },
      error: (e) => this.handleCloseError(e),
    });
  }

  retryClose(): void { if (this.lastAsOf) this.runClose(this.lastAsOf); }

  runYearEnd(fiscalYearEnd: string): void {
    this.actionError.set(null);
    this.blockers.set(null);
    this.busy.set(true);
    this.svc.closeYear(fiscalYearEnd).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.load(); },
      error: (e) => this.handleCloseError(e),
    });
  }

  closeYearNext(): void { this.runYearEnd(this.nextEnd()); }

  private handleCloseError(e: unknown): void {
    this.busy.set(false);
    const body = (e instanceof HttpErrorResponse ? e.error : null) as
      { blockers?: PendingEntryRef[]; useEndpoint?: string; fiscalYearEnd?: string } | null;
    if (Array.isArray(body?.blockers) && body.blockers.length > 0) {
      this.blockers.set(body.blockers);
      this.actionError.set(null);
      return;
    }
    if (typeof body?.fiscalYearEnd === 'string') {
      // Fiscal-year-end steer: run the year-end close for that date instead.
      this.runYearEnd(body.fiscalYearEnd);
      return;
    }
    this.actionError.set(extractProblem(e).detail);
  }
```

- [ ] **Step 3c: Replace the "next period" button block + add the blocker panel**

In the template, replace the single monthly `<button … *appCan="'gl.close'" … (click)="closeNext()">Close {{ nextLabel() }}</button>` with the year-end-aware pair:
```html
          @if (isYearEndNext()) {
            <button type="button" *appCan="'gl.close'"
                    class="self-start text-sm px-3 py-1.5 rounded-lg bg-primary text-primary-foreground disabled:opacity-50"
                    [disabled]="busy()" (click)="closeYearNext()">
              Run year-end close ({{ date(nextEnd()) }})
            </button>
          } @else {
            <button type="button" *appCan="'gl.close'"
                    class="self-start text-sm px-3 py-1.5 rounded-lg bg-primary text-primary-foreground disabled:opacity-50"
                    [disabled]="busy()" (click)="closeNext()">
              Close {{ nextLabel() }}
            </button>
          }

          @if (blockers(); as bs) {
            <div class="rounded-md border border-destructive p-3 flex flex-col gap-2">
              <p class="text-sm text-destructive">
                Can't close — {{ bs.length }} {{ bs.length === 1 ? 'entry is' : 'entries are' }} still awaiting approval:
              </p>
              <ul class="flex flex-col gap-1 text-sm">
                @for (b of bs; track b.entryId) {
                  <li class="flex items-center gap-2">
                    <span class="tabular-nums text-muted-foreground">{{ date(b.effectiveDate) }}</span>
                    @if (canDrill()) {
                      <a [routerLink]="['/journal', b.entryId]" class="underline">{{ b.reference ?? b.type }}</a>
                    } @else {
                      <span>{{ b.reference ?? b.type }}</span>
                    }
                    <span class="text-muted-foreground">{{ b.type }}</span>
                  </li>
                }
              </ul>
              <button type="button" *appCan="'gl.close'"
                      class="self-start text-sm px-3 py-1.5 rounded-lg border border-border disabled:opacity-50"
                      [disabled]="busy()" (click)="retryClose()">Retry close</button>
            </div>
          }
```

- [ ] **Step 4: Run the spec + compile gate**

Run (from `UI/Angular`): `npx ng test --include='**/period-close.spec.ts' --watch=false` → all specs PASS (4 from Task 2 + 2 new).
Run: `npx ng build` → `Application bundle generation complete`.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/periods/period-close.ts UI/Angular/src/app/features/periods/period-close.spec.ts
git commit -m "feat(ui): period close — blocker resolution, year-end steer, year-end close"
```

---

## Self-Review

**Spec coverage:**
- Backend `GET /periods/status` (gl.read) + `PeriodStatusResponse` + `LedgerService.GetClosedThroughAsync` → Task 1. ✓
- Existing close/year/reopen endpoints untouched (only a new GET + a new public read method); sanity-runs `PeriodCloseApiTests` → Task 1. ✓
- `PeriodsService` (status/close/closeYear) + interfaces → Task 2. ✓
- Screen: status card, principle note, guided monthly close, month-picker, success refresh, `gl.close` gating, route/`built` → Task 2. ✓
- Blocker 409 → linked entries + retry; fiscal-year-end steer (proactive via `isYearEndNext`, reactive via `fiscalYearEnd`); year-end close → Task 3. ✓
- No reopen anywhere; no `auth_time`/step-up change. ✓
- Testing: backend null/after-close/membership; FE status/empty/close-next/gating (Task 2) + blocker-links-retry/year-end-steer (Task 3). ✓

**Placeholder scan:** every step contains complete code; no TBD.

**Type/name consistency:** `PeriodStatusResponse`/`PeriodStatus` fields (`closedThrough`, `fiscalYearEndMonth`) identical backend record ↔ FE interface ↔ wire (host camelCase). `PendingEntryRef` fields (`entryId/reference/effectiveDate/type`) reused from the existing backend contract. `runClose`/`runYearEnd`/`handleCloseError`/`retryClose`/`closeYearNext`/`nextPeriod`/`nextEnd`/`nextLabel`/`lastDay` names consistent across Task 2 and Task 3. Task 3's template edits are supersets of Task 2's block (monthly button becomes the year-end-aware pair; the blocker panel is additive), so Task 2's render/gating assertions stay valid. `close(asOf)` sends `{ asOf }`, `closeYear(fye)` sends `{ fiscalYearEnd }` — matching `ClosePeriodRequest`/`CloseYearRequest`.

## Execution Handoff

Two execution options:
1. **Subagent-Driven (recommended)** — fresh implementer per task, per-task review, final whole-branch review.
2. **Inline Execution** — execute in this session with checkpoints.
