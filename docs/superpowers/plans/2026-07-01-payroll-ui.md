# Payroll Module UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Angular UI for the Payroll module — list, record, and view/void payroll runs and tax remittances — replacing the `/payroll` placeholder.

**Architecture:** Mirror the AR/AP module-UI structure (shell + tabs + list/record/detail) but with the simpler record-then-void lifecycle (no drafts, no allocation). A root-singleton `PayrollService` wraps the eight endpoints plus an `entriesForSource` lookup for the posted-journal-entry link. Two document types (runs, remittances) get parallel list/editor/detail trios.

**Tech Stack:** Angular 22, standalone + OnPush, signals, Spartan NG helm components, `CurrencyInput`, vitest via `ng test`.

## Global Constraints

- USD-only; camelCase JSON on the wire.
- Follow established module-UI conventions: shell tabs like `PayablesShell` (`routerLinkActive`, `[routerLinkActiveOptions]="{ exact: false }"`, `data-testid`), whole-row-click-to-detail (`cursor-pointer hover:bg-muted/50`, `tabindex="0"`, `(click)`+`(keydown.enter)` → `router.navigate`), void-on-detail (not on the list), `PagedResponse` envelope, `extractProblem(e).detail` on error paths, `takeUntilDestroyed(this.destroyRef)` on every inline subscribe, root-singleton core service with `${environment.apiBaseUrl}/clients/${client.clientId()}` base.
- Money via `money` and `displayDate` from `../../core/format/display`; currency entry via `<app-currency-input>`.
- No backend changes.
- Do NOT stage `UI/Angular/src/app/core/api/environment.ts`, any `*.csproj`, or `Accounting101.slnx` (unrelated local/IDE churn) — stage explicit paths only, never `git add -A`.

## Prerequisite (controller-handled, NOT a subagent task)

Running the app live needs a `Payroll__Accounts__*` block in `.localdev/start.ps1` (gitignored) + seeded Demo Co accounts (SalariesExpense, PayrollTaxExpense, Cash [1000, exists], WithholdingsPayable, PayrollTaxesPayable), with numbers that don't collide with existing seeds. The controller handles this before the live smoke test. The vitest specs use `HttpTestingController` (mocked) and do not need it.

---

### Task 1: Core (types + service) + shell

**Files:**
- Create: `UI/Angular/src/app/core/payroll/payroll.ts`
- Create: `UI/Angular/src/app/core/payroll/payroll.service.ts`
- Create: `UI/Angular/src/app/core/payroll/payroll.service.spec.ts`
- Create: `UI/Angular/src/app/features/payroll/payroll-shell.ts`

**Interfaces:**
- Produces (types): `PayrollRun`, `TaxRemittance`, `PayrollRunStatus`, `TaxRemittanceStatus`, `PayrollRunView`, `TaxRemittanceView`, `RecordPayrollRunRequest`, `RecordTaxRemittanceRequest`, `PayrollListQuery`, and helpers `netPay(r)`, `remittanceTotal(r)`.
- Produces (service `PayrollService`): `listRuns(q): Observable<PagedResponse<PayrollRun>>`, `getRun(id): Observable<PayrollRun>`, `recordRun(req): Observable<PayrollRun>`, `voidRun(id, reason?): Observable<PayrollRun>`, `listRemittances(q): Observable<PagedResponse<TaxRemittance>>`, `getRemittance(id): Observable<TaxRemittance>`, `recordRemittance(req): Observable<TaxRemittance>`, `voidRemittance(id, reason?): Observable<TaxRemittance>`, `entriesForSource(sourceRef): Observable<EntryResponse[]>`.
- Consumes: `PagedResponse<T>` from `../api/paged-response`; `EntryResponse` from `../entries/entry`; `ClientContextService` from `../client/client-context.service`; `environment` from `../api/environment`.

- [ ] **Step 1: Create the types file**

Create `UI/Angular/src/app/core/payroll/payroll.ts`:

```typescript
export type PayrollRunStatus = 'Posted' | 'Void';
export type TaxRemittanceStatus = 'Posted' | 'Void';

export interface PayrollRun {
  id: string;
  number: string | null;
  gross: number;
  employeeFica: number;
  employerFica: number;
  deductions: number;
  incomeTaxWithheld: number;
  payDate: string;
  memo: string | null;
  status: PayrollRunStatus;
}

export interface TaxRemittance {
  id: string;
  number: string | null;
  withholdingsAmount: number;
  taxesAmount: number;
  payDate: string;
  memo: string | null;
  status: TaxRemittanceStatus;
}

export interface PayrollRunView { run: PayrollRun; }
export interface TaxRemittanceView { remittance: TaxRemittance; }

export interface RecordPayrollRunRequest {
  gross: number;
  employeeFica: number;
  employerFica: number;
  deductions: number;
  incomeTaxWithheld: number;
  payDate: string;
  memo: string | null;
}

export interface RecordTaxRemittanceRequest {
  withholdingsAmount: number;
  taxesAmount: number;
  payDate: string;
  memo: string | null;
}

export interface PayrollListQuery {
  skip: number;
  limit: number;
  order?: 'asc' | 'desc';
  includeVoided?: boolean;
}

/** Net pay a run disburses = gross − employee FICA − income tax withheld − deductions. Not stored. */
export const netPay = (r: Pick<PayrollRun, 'gross' | 'employeeFica' | 'incomeTaxWithheld' | 'deductions'>): number =>
  Math.round((r.gross - r.employeeFica - r.incomeTaxWithheld - r.deductions) * 100) / 100;

/** Total cash a remittance pays = withholdings + taxes. Not stored. */
export const remittanceTotal = (r: Pick<TaxRemittance, 'withholdingsAmount' | 'taxesAmount'>): number =>
  Math.round((r.withholdingsAmount + r.taxesAmount) * 100) / 100;
```

- [ ] **Step 2: Write the failing service spec**

Create `UI/Angular/src/app/core/payroll/payroll.service.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PayrollService } from './payroll.service';
import { ClientContextService } from '../client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return { svc: TestBed.inject(PayrollService), ctrl: TestBed.inject(HttpTestingController) };
}

describe('PayrollService', () => {
  it('lists runs, unwrapping the PayrollRunView envelope', () => {
    const { svc, ctrl } = setup();
    let items: unknown;
    svc.listRuns({ skip: 0, limit: 50, includeVoided: true }).subscribe(p => (items = p.items));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/payroll-runs'
      && r.params.get('skip') === '0' && r.params.get('limit') === '50' && r.params.get('includeVoided') === 'true');
    req.flush({ items: [{ run: { id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 76.5, employerFica: 76.5,
      deductions: 50, incomeTaxWithheld: 120, payDate: '2026-06-30', memo: null, status: 'Posted' } }],
      total: 1, skip: 0, limit: 50 });
    expect(items).toEqual([{ id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 76.5, employerFica: 76.5,
      deductions: 50, incomeTaxWithheld: 120, payDate: '2026-06-30', memo: null, status: 'Posted' }]);
    ctrl.verify();
  });

  it('records a run (posts the request body verbatim)', () => {
    const { svc, ctrl } = setup();
    const body = { gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50,
      incomeTaxWithheld: 120, payDate: '2026-06-30', memo: 'June' };
    svc.recordRun(body).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payroll-runs');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    req.flush({ id: 'r9', number: 'PR-9', ...body, status: 'Posted' });
    ctrl.verify();
  });

  it('voids a run with a reason', () => {
    const { svc, ctrl } = setup();
    svc.voidRun('r1', 'mistake').subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payroll-runs/r1/void');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ reason: 'mistake' });
    req.flush({ id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 0, employerFica: 0, deductions: 0,
      incomeTaxWithheld: 0, payDate: '2026-06-30', memo: null, status: 'Void' });
    ctrl.verify();
  });

  it('gets a remittance, unwrapping the view', () => {
    const { svc, ctrl } = setup();
    let r: unknown;
    svc.getRemittance('m1').subscribe(x => (r = x));
    ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances/m1')
      .flush({ remittance: { id: 'm1', number: 'TR-1', withholdingsAmount: 170, taxesAmount: 153,
        payDate: '2026-06-30', memo: null, status: 'Posted' } });
    expect(r).toEqual({ id: 'm1', number: 'TR-1', withholdingsAmount: 170, taxesAmount: 153,
      payDate: '2026-06-30', memo: null, status: 'Posted' });
    ctrl.verify();
  });

  it('fetches the posted entries for a source ref', () => {
    const { svc, ctrl } = setup();
    let entries: unknown;
    svc.entriesForSource('r1').subscribe(e => (entries = e));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries'
      && r.params.get('sourceRef') === 'r1');
    req.flush([{ id: 'e1', sequenceNumber: 5, effectiveDate: '2026-06-30', type: 'Standard', status: 'Open',
      posting: 'PendingApproval', lineCount: 5, supersedes: null, supersededBy: null, reversalOf: null,
      reversedBy: null, lines: [], sourceRef: 'r1', sourceType: 'PayrollRun', reference: null, memo: null, viaModule: 'payroll' }]);
    expect((entries as { id: string }[])[0].id).toBe('e1');
    ctrl.verify();
  });
});
```

- [ ] **Step 3: Run the spec to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `PayrollService` does not exist yet (module resolution / compile error in the spec).

- [ ] **Step 4: Create the service**

Create `UI/Angular/src/app/core/payroll/payroll.service.ts`:

```typescript
import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, map } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import { EntryResponse } from '../entries/entry';
import {
  PayrollRun, TaxRemittance, PayrollRunView, TaxRemittanceView,
  RecordPayrollRunRequest, RecordTaxRemittanceRequest, PayrollListQuery,
} from './payroll';

@Injectable({ providedIn: 'root' })
export class PayrollService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  private listParams(q: PayrollListQuery): HttpParams {
    let p = new HttpParams().set('skip', q.skip).set('limit', q.limit);
    if (q.order) p = p.set('order', q.order);
    if (q.includeVoided) p = p.set('includeVoided', true);
    return p;
  }

  listRuns(q: PayrollListQuery): Observable<PagedResponse<PayrollRun>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<PayrollRunView>>(this.base('/payroll-runs'), { params: this.listParams(q) })
      .pipe(map(pg => ({ ...pg, items: pg.items.map(v => v.run) })));
  }

  getRun(id: string): Observable<PayrollRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PayrollRunView>(this.base(`/payroll-runs/${id}`)).pipe(map(v => v.run));
  }

  recordRun(req: RecordPayrollRunRequest): Observable<PayrollRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<PayrollRun>(this.base('/payroll-runs'), req);
  }

  voidRun(id: string, reason?: string | null): Observable<PayrollRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<PayrollRun>(this.base(`/payroll-runs/${id}/void`), { reason: reason ?? null });
  }

  listRemittances(q: PayrollListQuery): Observable<PagedResponse<TaxRemittance>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<TaxRemittanceView>>(this.base('/tax-remittances'), { params: this.listParams(q) })
      .pipe(map(pg => ({ ...pg, items: pg.items.map(v => v.remittance) })));
  }

  getRemittance(id: string): Observable<TaxRemittance> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<TaxRemittanceView>(this.base(`/tax-remittances/${id}`)).pipe(map(v => v.remittance));
  }

  recordRemittance(req: RecordTaxRemittanceRequest): Observable<TaxRemittance> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<TaxRemittance>(this.base('/tax-remittances'), req);
  }

  voidRemittance(id: string, reason?: string | null): Observable<TaxRemittance> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<TaxRemittance>(this.base(`/tax-remittances/${id}/void`), { reason: reason ?? null });
  }

  /** Posted journal entry(ies) for a payroll document — powers the "posted journal entry" link. */
  entriesForSource(sourceRef: string): Observable<EntryResponse[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<EntryResponse[]>(this.base('/entries'), { params: new HttpParams().set('sourceRef', sourceRef) });
  }
}
```

- [ ] **Step 5: Create the shell**

Create `UI/Angular/src/app/features/payroll/payroll-shell.ts`:

```typescript
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-payroll-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="flex flex-col gap-0">
      <nav class="flex gap-1 border-b border-border px-4 pt-4">
        <a routerLink="runs"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-runs">Runs</a>
        <a routerLink="remittances"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-remittances">Remittances</a>
      </nav>
      <router-outlet />
    </div>
  `,
})
export class PayrollShell {}
```

- [ ] **Step 6: Run the spec to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — the five `PayrollService` tests pass and the suite is green (shell has no spec; it is exercised via routes in Task 3).

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/payroll/payroll.ts \
        UI/Angular/src/app/core/payroll/payroll.service.ts \
        UI/Angular/src/app/core/payroll/payroll.service.spec.ts \
        UI/Angular/src/app/features/payroll/payroll-shell.ts
git commit -m "feat(payroll): core types + service + shell

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Payroll runs (list + editor + detail)

**Files:**
- Create: `UI/Angular/src/app/features/payroll/run-list.ts`
- Create: `UI/Angular/src/app/features/payroll/run-list.spec.ts`
- Create: `UI/Angular/src/app/features/payroll/run-editor.ts`
- Create: `UI/Angular/src/app/features/payroll/run-editor.spec.ts`
- Create: `UI/Angular/src/app/features/payroll/run-detail.ts`
- Create: `UI/Angular/src/app/features/payroll/run-detail.spec.ts`

**Interfaces:**
- Consumes: `PayrollService` (Task 1) — `listRuns`, `recordRun`, `getRun`, `voidRun`, `entriesForSource`; types `PayrollRun`, `RecordPayrollRunRequest`, `netPay` (Task 1); `CurrencyInput` from `../../shared/currency-input`; `money`, `displayDate` from `../../core/format/display`; `extractProblem` from `../../core/api/problem-details`.
- Produces: components `RunList`, `RunEditor`, `RunDetail` (used by routes in Task 3).

- [ ] **Step 1: Write the failing run-list spec**

Create `UI/Angular/src/app/features/payroll/run-list.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunList } from './run-list';
import { ClientContextService } from '../../core/client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('RunList', () => {
  it('renders payroll runs with computed net pay', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/payroll-runs')
      .flush({ items: [{ run: { id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 76.5, employerFica: 76.5,
        deductions: 50, incomeTaxWithheld: 120, payDate: '2026-06-30', memo: null, status: 'Posted' } }],
        total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    // net pay = 1000 - 76.5 - 120 - 50 = 753.50
    expect(f.nativeElement.textContent).toContain('PR-1');
    expect(f.nativeElement.textContent).toContain('753.50');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `RunList` does not exist.

- [ ] **Step 3: Create the run list**

Create `UI/Angular/src/app/features/payroll/run-list.ts`:

```typescript
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { PayrollService } from '../../core/payroll/payroll.service';
import { PayrollRun, netPay } from '../../core/payroll/payroll';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';

@Component({
  selector: 'app-run-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Payroll runs</h1>
        <a hlmBtn size="sm" routerLink="/payroll/runs/new" class="ms-auto">Record payroll run</a>
        <label class="flex items-center gap-2 text-sm text-muted-foreground">
          <input type="checkbox" [checked]="includeVoided()" (change)="toggleVoided($any($event.target).checked)" />
          Show voided
        </label>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (runs().length === 0) {
        <p class="text-muted-foreground text-sm">No payroll runs yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr>
                <th hlmTh>#</th><th hlmTh>Pay date</th>
                <th hlmTh class="text-right">Gross</th><th hlmTh class="text-right">Net pay</th><th hlmTh>Status</th>
              </tr>
            </thead>
            <tbody hlmTBody>
              @for (run of runs(); track run.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="open(run.id)" (keydown.enter)="open(run.id)">
                  <td hlmTd>{{ run.number ?? '—' }}</td>
                  <td hlmTd>{{ formatDate(run.payDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(run.gross) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(net(run)) }}</td>
                  <td hlmTd>{{ run.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <div class="flex items-center justify-between text-sm text-muted-foreground">
          <span>Page {{ currentPage() }} of {{ pageCount() }}</span>
          <div class="flex gap-2">
            <button hlmBtn variant="outline" size="sm" [disabled]="skip() === 0" (click)="prev()">Previous</button>
            <button hlmBtn variant="outline" size="sm" [disabled]="currentPage() >= pageCount()" (click)="next()">Next</button>
          </div>
        </div>
      }
    </div>
  `,
})
export class RunList {
  private readonly svc = inject(PayrollService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly includeVoided = signal(false);
  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({
    id: this.client.clientId(), includeVoided: this.includeVoided(), skip: this.skip(), limit: this.limit(),
  }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, includeVoided, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listRuns({ skip, limit, includeVoided }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading runs'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<PayrollRun> | null },
  );

  readonly runs = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  net(r: PayrollRun): number { return netPay(r); }
  open(id: string): void { void this.router.navigate(['/payroll/runs', id]); }
  toggleVoided(v: boolean): void { this.includeVoided.set(v); this.skip.set(0); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Run to verify the list test passes**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS for the RunList test (whole suite green).

- [ ] **Step 5: Write the failing run-editor spec**

Create `UI/Angular/src/app/features/payroll/run-editor.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
import { RunEditor } from './run-editor';
import { ClientContextService } from '../../core/client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('RunEditor', () => {
  it('warns and disables Save when net pay is negative', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.gross.set(100); cmp.employeeFica.set(50); cmp.incomeTaxWithheld.set(40); cmp.deductions.set(30); // net = -20
    f.detectChanges();
    expect(cmp.net()).toBe(-20);
    expect(cmp.canSave()).toBe(false);
    expect(f.nativeElement.textContent).toContain('Net pay is negative');
    ctrl.verify();
  });

  it('posts the run and navigates to its detail', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(RunEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.gross.set(1000); cmp.employeeFica.set(76.5); cmp.employerFica.set(76.5);
    cmp.deductions.set(50); cmp.incomeTaxWithheld.set(120); cmp.payDate.set('2026-06-30'); cmp.memo.set('June');
    f.detectChanges();
    expect(cmp.net()).toBe(753.5);
    expect(cmp.canSave()).toBe(true);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payroll-runs');
    expect(req.request.body).toEqual({ gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50,
      incomeTaxWithheld: 120, payDate: '2026-06-30', memo: 'June' });
    req.flush({ id: 'r9', number: 'PR-9', gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50,
      incomeTaxWithheld: 120, payDate: '2026-06-30', memo: 'June', status: 'Posted' });
    expect(nav).toHaveBeenCalledWith(['/payroll/runs', 'r9']);
    ctrl.verify();
  });
});
```

- [ ] **Step 6: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `RunEditor` does not exist.

- [ ] **Step 7: Create the run editor**

Create `UI/Angular/src/app/features/payroll/run-editor.ts`:

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayrollService } from '../../core/payroll/payroll.service';
import { netPay } from '../../core/payroll/payroll';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

@Component({
  selector: 'app-run-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Record payroll run</h1>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Gross</label>
          <app-currency-input ariaLabel="Gross" [value]="gross()" (valueChange)="gross.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Pay date</label>
          <input hlmInput type="date" [value]="payDate()" (change)="payDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Employee FICA</label>
          <app-currency-input ariaLabel="Employee FICA" [value]="employeeFica()" (valueChange)="employeeFica.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Employer FICA</label>
          <app-currency-input ariaLabel="Employer FICA" [value]="employerFica()" (valueChange)="employerFica.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Income tax withheld</label>
          <app-currency-input ariaLabel="Income tax withheld" [value]="incomeTaxWithheld()" (valueChange)="incomeTaxWithheld.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Deductions</label>
          <app-currency-input ariaLabel="Deductions" [value]="deductions()" (valueChange)="deductions.set($event)" />
        </div>
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      <div class="text-right text-sm tabular-nums flex justify-between w-64 ms-auto font-semibold border-t border-border pt-1">
        <span>Net pay</span><span [class.text-destructive]="net() < 0">{{ money(net()) }}</span>
      </div>

      @if (netPayWarning()) { <p class="text-destructive text-sm">{{ netPayWarning() }}</p> }
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Record payroll run</button>
        <a hlmBtn variant="outline" routerLink="/payroll/runs">Cancel</a>
      </div>
    </div>
  `,
})
export class RunEditor {
  private readonly svc = inject(PayrollService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly gross = signal(0);
  readonly employeeFica = signal(0);
  readonly employerFica = signal(0);
  readonly deductions = signal(0);
  readonly incomeTaxWithheld = signal(0);
  readonly payDate = signal(new Date().toISOString().slice(0, 10));
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly net = computed(() => netPay({
    gross: this.gross(), employeeFica: this.employeeFica(),
    incomeTaxWithheld: this.incomeTaxWithheld(), deductions: this.deductions(),
  }));

  readonly netPayWarning = computed<string | null>(() =>
    this.net() < 0
      ? `Net pay is negative (${fmtMoney(this.net())}) — gross must cover FICA, withholding, and deductions.`
      : null);

  readonly canSave = computed(() => this.gross() > 0 && this.net() >= 0 && !!this.payDate());

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordRun({
      gross: this.gross(), employeeFica: this.employeeFica(), employerFica: this.employerFica(),
      deductions: this.deductions(), incomeTaxWithheld: this.incomeTaxWithheld(),
      payDate: this.payDate(), memo: this.memo(),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (run) => { this.busy.set(false); void this.router.navigate(['/payroll/runs', run.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
}
```

- [ ] **Step 8: Run to verify the editor tests pass**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS for the two RunEditor tests.

- [ ] **Step 9: Write the failing run-detail spec**

Create `UI/Angular/src/app/features/payroll/run-detail.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunDetail } from './run-detail';
import { ClientContextService } from '../../core/client/client-context.service';

function setup(id = 'r1') {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } },
    ],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

function flushLoad(ctrl: HttpTestingController, status: string, id = 'r1') {
  ctrl.expectOne(`http://localhost:5000/clients/C1/payroll-runs/${id}`).flush({ run: { id, number: 'PR-1',
    gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50, incomeTaxWithheld: 120,
    payDate: '2026-06-30', memo: null, status } });
  ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries' && r.params.get('sourceRef') === id)
    .flush([{ id: 'e1', sequenceNumber: 5, effectiveDate: '2026-06-30', type: 'Standard', status: 'Open',
      posting: 'PendingApproval', lineCount: 5, supersedes: null, supersededBy: null, reversalOf: null,
      reversedBy: null, lines: [], sourceRef: id, sourceType: 'PayrollRun', reference: null, memo: null, viaModule: 'payroll' }]);
}

describe('RunDetail', () => {
  it('renders the run, net pay, and a link to the posted entry', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunDetail); f.detectChanges();
    flushLoad(ctrl, 'Posted');
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('PR-1');
    expect(f.nativeElement.textContent).toContain('753.50');       // net pay
    const link = f.nativeElement.querySelector('a[href="/journal/e1"]');
    expect(link).toBeTruthy();
    ctrl.verify();
  });

  it('voids a posted run with a reason', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunDetail); f.detectChanges();
    flushLoad(ctrl, 'Posted');
    f.detectChanges();
    f.componentInstance.reason.set('entered twice');
    f.componentInstance.void();
    const del = ctrl.expectOne('http://localhost:5000/clients/C1/payroll-runs/r1/void');
    expect(del.request.body).toEqual({ reason: 'entered twice' });
    del.flush({ id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50,
      incomeTaxWithheld: 120, payDate: '2026-06-30', memo: null, status: 'Void' });
    flushLoad(ctrl, 'Void');       // reload after void
    f.detectChanges();
    ctrl.verify();
  });
});
```

- [ ] **Step 10: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `RunDetail` does not exist.

- [ ] **Step 11: Create the run detail**

Create `UI/Angular/src/app/features/payroll/run-detail.ts`:

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayrollService } from '../../core/payroll/payroll.service';
import { PayrollRun, netPay } from '../../core/payroll/payroll';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';

@Component({
  selector: 'app-run-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/payroll/runs" class="text-sm text-muted-foreground hover:text-foreground">← Runs</a>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (run(); as r) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Payroll run {{ r.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="r.status === 'Void'">{{ r.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Pay date</td><td class="text-right">{{ formatDate(r.payDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Gross</td><td class="text-right tabular-nums">{{ money(r.gross) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Employee FICA</td><td class="text-right tabular-nums">{{ money(r.employeeFica) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Employer FICA</td><td class="text-right tabular-nums">{{ money(r.employerFica) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Income tax withheld</td><td class="text-right tabular-nums">{{ money(r.incomeTaxWithheld) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Deductions</td><td class="text-right tabular-nums">{{ money(r.deductions) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Net pay</td><td class="text-right tabular-nums">{{ money(net(r)) }}</td></tr>
            @if (r.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ r.memo }}</td></tr> }
          </tbody>
        </table>

        @if (postedEntryId(); as eid) {
          <a [routerLink]="['/journal', eid]" class="text-sm text-primary hover:underline">Posted journal entry →</a>
        }

        @if (r.status === 'Posted') {
          <div class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)"
                   [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button hlmBtn type="button" variant="outline" (click)="void()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class RunDetail {
  private readonly svc = inject(PayrollService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly run = signal<PayrollRun | null>(null);
  readonly postedEntryId = signal<string | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getRun(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (r) => { this.run.set(r); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
    this.svc.entriesForSource(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (entries) => this.postedEntryId.set(entries[0]?.id ?? null),
      error: () => this.postedEntryId.set(null),
    });
  }

  void(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidRun(this.id, this.reason()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  net(r: PayrollRun): number { return netPay(r); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 12: Run to verify the detail tests pass**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — all RunList/RunEditor/RunDetail tests green, whole suite green, output pristine.

- [ ] **Step 13: Commit**

```bash
git add UI/Angular/src/app/features/payroll/run-list.ts \
        UI/Angular/src/app/features/payroll/run-list.spec.ts \
        UI/Angular/src/app/features/payroll/run-editor.ts \
        UI/Angular/src/app/features/payroll/run-editor.spec.ts \
        UI/Angular/src/app/features/payroll/run-detail.ts \
        UI/Angular/src/app/features/payroll/run-detail.spec.ts
git commit -m "feat(payroll): payroll run list, editor (net-pay + warning), detail (void + posted-entry link)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Tax remittances (list + editor + detail) + routes wiring

**Files:**
- Create: `UI/Angular/src/app/features/payroll/remittance-list.ts`
- Create: `UI/Angular/src/app/features/payroll/remittance-list.spec.ts`
- Create: `UI/Angular/src/app/features/payroll/remittance-editor.ts`
- Create: `UI/Angular/src/app/features/payroll/remittance-editor.spec.ts`
- Create: `UI/Angular/src/app/features/payroll/remittance-detail.ts`
- Create: `UI/Angular/src/app/features/payroll/remittance-detail.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `PayrollService` (Task 1) — `listRemittances`, `recordRemittance`, `getRemittance`, `voidRemittance`, `entriesForSource`; types `TaxRemittance`, `remittanceTotal` (Task 1); `PayrollShell`, `RunList`, `RunEditor`, `RunDetail` (Tasks 1-2) for routes.
- Produces: components `RemittanceList`, `RemittanceEditor`, `RemittanceDetail`; the navigable `/payroll` route tree.

- [ ] **Step 1: Write the failing remittance-list spec**

Create `UI/Angular/src/app/features/payroll/remittance-list.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RemittanceList } from './remittance-list';
import { ClientContextService } from '../../core/client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('RemittanceList', () => {
  it('renders remittances with computed total', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RemittanceList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/tax-remittances')
      .flush({ items: [{ remittance: { id: 'm1', number: 'TR-1', withholdingsAmount: 170, taxesAmount: 153,
        payDate: '2026-06-30', memo: null, status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('TR-1');
    expect(f.nativeElement.textContent).toContain('323.00');   // 170 + 153
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `RemittanceList` does not exist.

- [ ] **Step 3: Create the remittance list**

Create `UI/Angular/src/app/features/payroll/remittance-list.ts`:

```typescript
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { PayrollService } from '../../core/payroll/payroll.service';
import { TaxRemittance, remittanceTotal } from '../../core/payroll/payroll';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';

@Component({
  selector: 'app-remittance-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Tax remittances</h1>
        <a hlmBtn size="sm" routerLink="/payroll/remittances/new" class="ms-auto">Record remittance</a>
        <label class="flex items-center gap-2 text-sm text-muted-foreground">
          <input type="checkbox" [checked]="includeVoided()" (change)="toggleVoided($any($event.target).checked)" />
          Show voided
        </label>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (remittances().length === 0) {
        <p class="text-muted-foreground text-sm">No tax remittances yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr>
                <th hlmTh>#</th><th hlmTh>Pay date</th>
                <th hlmTh class="text-right">Withholdings</th><th hlmTh class="text-right">Taxes</th>
                <th hlmTh class="text-right">Total</th><th hlmTh>Status</th>
              </tr>
            </thead>
            <tbody hlmTBody>
              @for (m of remittances(); track m.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="open(m.id)" (keydown.enter)="open(m.id)">
                  <td hlmTd>{{ m.number ?? '—' }}</td>
                  <td hlmTd>{{ formatDate(m.payDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(m.withholdingsAmount) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(m.taxesAmount) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(total(m)) }}</td>
                  <td hlmTd>{{ m.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <div class="flex items-center justify-between text-sm text-muted-foreground">
          <span>Page {{ currentPage() }} of {{ pageCount() }}</span>
          <div class="flex gap-2">
            <button hlmBtn variant="outline" size="sm" [disabled]="skip() === 0" (click)="prev()">Previous</button>
            <button hlmBtn variant="outline" size="sm" [disabled]="currentPage() >= pageCount()" (click)="next()">Next</button>
          </div>
        </div>
      }
    </div>
  `,
})
export class RemittanceList {
  private readonly svc = inject(PayrollService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly includeVoided = signal(false);
  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({
    id: this.client.clientId(), includeVoided: this.includeVoided(), skip: this.skip(), limit: this.limit(),
  }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, includeVoided, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listRemittances({ skip, limit, includeVoided }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading remittances'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<TaxRemittance> | null },
  );

  readonly remittances = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  total(m: TaxRemittance): number { return remittanceTotal(m); }
  open(id: string): void { void this.router.navigate(['/payroll/remittances', id]); }
  toggleVoided(v: boolean): void { this.includeVoided.set(v); this.skip.set(0); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Run to verify the list test passes**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS for the RemittanceList test.

- [ ] **Step 5: Write the failing remittance-editor spec**

Create `UI/Angular/src/app/features/payroll/remittance-editor.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
import { RemittanceEditor } from './remittance-editor';
import { ClientContextService } from '../../core/client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('RemittanceEditor', () => {
  it('shows the total and posts the remittance, navigating to detail', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(RemittanceEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.withholdingsAmount.set(170); cmp.taxesAmount.set(153); cmp.payDate.set('2026-06-30'); cmp.memo.set('Q2');
    f.detectChanges();
    expect(cmp.total()).toBe(323);
    expect(cmp.canSave()).toBe(true);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances');
    expect(req.request.body).toEqual({ withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: 'Q2' });
    req.flush({ id: 'm9', number: 'TR-9', withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: 'Q2', status: 'Posted' });
    expect(nav).toHaveBeenCalledWith(['/payroll/remittances', 'm9']);
    ctrl.verify();
  });
});
```

- [ ] **Step 6: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `RemittanceEditor` does not exist.

- [ ] **Step 7: Create the remittance editor**

Create `UI/Angular/src/app/features/payroll/remittance-editor.ts`:

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayrollService } from '../../core/payroll/payroll.service';
import { remittanceTotal } from '../../core/payroll/payroll';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

@Component({
  selector: 'app-remittance-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Record tax remittance</h1>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Withholdings amount</label>
          <app-currency-input ariaLabel="Withholdings amount" [value]="withholdingsAmount()" (valueChange)="withholdingsAmount.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Taxes amount</label>
          <app-currency-input ariaLabel="Taxes amount" [value]="taxesAmount()" (valueChange)="taxesAmount.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Pay date</label>
          <input hlmInput type="date" [value]="payDate()" (change)="payDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      <div class="text-right text-sm tabular-nums flex justify-between w-64 ms-auto font-semibold border-t border-border pt-1">
        <span>Total</span><span>{{ money(total()) }}</span>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Record remittance</button>
        <a hlmBtn variant="outline" routerLink="/payroll/remittances">Cancel</a>
      </div>
    </div>
  `,
})
export class RemittanceEditor {
  private readonly svc = inject(PayrollService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly withholdingsAmount = signal(0);
  readonly taxesAmount = signal(0);
  readonly payDate = signal(new Date().toISOString().slice(0, 10));
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly total = computed(() => remittanceTotal({ withholdingsAmount: this.withholdingsAmount(), taxesAmount: this.taxesAmount() }));
  readonly canSave = computed(() => this.total() > 0 && !!this.payDate());

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordRemittance({
      withholdingsAmount: this.withholdingsAmount(), taxesAmount: this.taxesAmount(),
      payDate: this.payDate(), memo: this.memo(),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (m) => { this.busy.set(false); void this.router.navigate(['/payroll/remittances', m.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
}
```

- [ ] **Step 8: Run to verify the editor test passes**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS for the RemittanceEditor test.

- [ ] **Step 9: Write the failing remittance-detail spec**

Create `UI/Angular/src/app/features/payroll/remittance-detail.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RemittanceDetail } from './remittance-detail';
import { ClientContextService } from '../../core/client/client-context.service';

function setup(id = 'm1') {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } },
    ],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

function flushLoad(ctrl: HttpTestingController, status: string, id = 'm1') {
  ctrl.expectOne(`http://localhost:5000/clients/C1/tax-remittances/${id}`).flush({ remittance: { id, number: 'TR-1',
    withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: null, status } });
  ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries' && r.params.get('sourceRef') === id)
    .flush([{ id: 'e2', sequenceNumber: 6, effectiveDate: '2026-06-30', type: 'Standard', status: 'Open',
      posting: 'PendingApproval', lineCount: 3, supersedes: null, supersededBy: null, reversalOf: null,
      reversedBy: null, lines: [], sourceRef: id, sourceType: 'TaxRemittance', reference: null, memo: null, viaModule: 'payroll' }]);
}

describe('RemittanceDetail', () => {
  it('renders the remittance, total, and posted-entry link', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RemittanceDetail); f.detectChanges();
    flushLoad(ctrl, 'Posted');
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('TR-1');
    expect(f.nativeElement.textContent).toContain('323.00');
    expect(f.nativeElement.querySelector('a[href="/journal/e2"]')).toBeTruthy();
    ctrl.verify();
  });

  it('voids a posted remittance', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RemittanceDetail); f.detectChanges();
    flushLoad(ctrl, 'Posted');
    f.detectChanges();
    f.componentInstance.void();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances/m1/void');
    expect(req.request.body).toEqual({ reason: null });
    req.flush({ id: 'm1', number: 'TR-1', withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: null, status: 'Void' });
    flushLoad(ctrl, 'Void');
    f.detectChanges();
    ctrl.verify();
  });
});
```

- [ ] **Step 10: Run to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `RemittanceDetail` does not exist.

- [ ] **Step 11: Create the remittance detail**

Create `UI/Angular/src/app/features/payroll/remittance-detail.ts`:

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayrollService } from '../../core/payroll/payroll.service';
import { TaxRemittance, remittanceTotal } from '../../core/payroll/payroll';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';

@Component({
  selector: 'app-remittance-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/payroll/remittances" class="text-sm text-muted-foreground hover:text-foreground">← Remittances</a>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (remittance(); as m) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Tax remittance {{ m.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="m.status === 'Void'">{{ m.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Pay date</td><td class="text-right">{{ formatDate(m.payDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Withholdings</td><td class="text-right tabular-nums">{{ money(m.withholdingsAmount) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Taxes</td><td class="text-right tabular-nums">{{ money(m.taxesAmount) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Total</td><td class="text-right tabular-nums">{{ money(total(m)) }}</td></tr>
            @if (m.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ m.memo }}</td></tr> }
          </tbody>
        </table>

        @if (postedEntryId(); as eid) {
          <a [routerLink]="['/journal', eid]" class="text-sm text-primary hover:underline">Posted journal entry →</a>
        }

        @if (m.status === 'Posted') {
          <div class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)"
                   [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button hlmBtn type="button" variant="outline" (click)="void()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class RemittanceDetail {
  private readonly svc = inject(PayrollService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly remittance = signal<TaxRemittance | null>(null);
  readonly postedEntryId = signal<string | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getRemittance(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (m) => { this.remittance.set(m); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
    this.svc.entriesForSource(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (entries) => this.postedEntryId.set(entries[0]?.id ?? null),
      error: () => this.postedEntryId.set(null),
    });
  }

  void(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidRemittance(this.id, this.reason()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  total(m: TaxRemittance): number { return remittanceTotal(m); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 12: Wire the routes**

Modify `UI/Angular/src/app/app.routes.ts`. Add these imports after the existing payables imports (after line 35):

```typescript
import { PayrollShell } from './features/payroll/payroll-shell';
import { RunList } from './features/payroll/run-list';
import { RunEditor } from './features/payroll/run-editor';
import { RunDetail } from './features/payroll/run-detail';
import { RemittanceList } from './features/payroll/remittance-list';
import { RemittanceEditor } from './features/payroll/remittance-editor';
import { RemittanceDetail } from './features/payroll/remittance-detail';
```

Insert the payroll route block immediately after the `payables` block closes (after line 92, before the `// remaining nav targets` comment):

```typescript
  { path: 'payroll', component: PayrollShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'runs' },
    { path: 'runs', component: RunList },
    { path: 'runs/new', component: RunEditor },
    { path: 'runs/:id', component: RunDetail },
    { path: 'remittances', component: RemittanceList },
    { path: 'remittances/new', component: RemittanceEditor },
    { path: 'remittances/:id', component: RemittanceDetail },
  ] },
```

In the placeholder filter line, add `'/payroll'` to the exclusion array so `/payroll` no longer falls through to `Placeholder`:

```typescript
  ...NAV.filter(n => ![ '/dashboard', '/trial-balance', '/statements', '/accounts', '/receivables', '/payables', '/payroll' ].includes(n.path) && !n.path.startsWith('/journal')).map(n => ({ path: n.path.slice(1), component: Placeholder })),
```

- [ ] **Step 13: Run the full suite to verify everything passes**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — all payroll specs green, whole suite green, output pristine. (Route wiring compiles against the now-existing components.)

- [ ] **Step 14: Commit**

```bash
git add UI/Angular/src/app/features/payroll/remittance-list.ts \
        UI/Angular/src/app/features/payroll/remittance-list.spec.ts \
        UI/Angular/src/app/features/payroll/remittance-editor.ts \
        UI/Angular/src/app/features/payroll/remittance-editor.spec.ts \
        UI/Angular/src/app/features/payroll/remittance-detail.ts \
        UI/Angular/src/app/features/payroll/remittance-detail.spec.ts \
        UI/Angular/src/app/app.routes.ts
git commit -m "feat(payroll): tax remittance list/editor/detail + wire /payroll routes

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- Shell + tabs (Runs | Remittances) → Task 1 (shell) + Task 3 (routes). ✓
- Core service (8 endpoints + entriesForSource) → Task 1. ✓
- Two list screens (paged, whole-row, includeVoided) → Task 2 (RunList) + Task 3 (RemittanceList). ✓
- Two record forms (run net-pay + negative warning; remittance total) → Task 2 (RunEditor) + Task 3 (RemittanceEditor). ✓
- Two detail screens (fields + derived + void-while-Posted + posted-entry link) → Task 2 (RunDetail) + Task 3 (RemittanceDetail). ✓
- Routes replacing the placeholder → Task 3. ✓
- Dev-stack account block/seeding → Prerequisite section (controller-handled, not a subagent task). ✓

**Placeholder scan:** none — every code and test step is complete.

**Type consistency:** `PayrollService` method names and signatures identical across Task 1 (definition) and Tasks 2-3 (consumption). `netPay`/`remittanceTotal` helpers used consistently. `EntryResponse` shape (from `../entries/entry`) matches the flushed test fixtures. Route paths (`/payroll/runs`, `/payroll/remittances`, `/journal/{id}`) consistent between components and the route block. `includeVoided` param name matches the backend (`includeVoided`).
