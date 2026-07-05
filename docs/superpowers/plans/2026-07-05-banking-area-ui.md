# Banking Area UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Angular front-end for the existing Cash and Bank Reconciliation backend modules as one unified "Banking" area at `/cash`.

**Architecture:** A tabbed shell at `/cash` (tabs Cash · Statements · Reconcile), one `core/banking` service + models wrapping every Cash + Reconciliation endpoint, and OnPush/signals components mirroring the Fixed Assets (FA-4) UI exactly. Five slices BK-1…BK-5, each an independently shippable, reviewed increment.

**Tech Stack:** Angular 22 (standalone, zoneless, signals, OnPush), Tailwind 4 + Spartan (`@spartan-ng/helm/*`), Vitest (`npm test`), `@angular/forms/signals` for line-array forms.

## Global Constraints

- **Change detection:** every component `ChangeDetectionStrategy.OnPush`. Root already compliant.
- **State:** signals only; lists use the `toObservable(query) → switchMap → toSignal` pattern (see `features/fixed-assets/asset-list.ts`); mutations use `takeUntilDestroyed` + explicit `busy` signal.
- **`busy` is cleared in BOTH `next` and `error`** of every mutate/reload observer (the "stuck busy" trap).
- **Enums are string unions** matching the C# `[JsonStringEnumConverter]` output — never numeric. A serialization-key guard test protects the camelCase round-trip.
- **Method names avoid JS reserved words** — use `voidDisbursement()`, `voidDeposit()`, `voidAdjustment()` (NG5002 forbids `void()`).
- **Lists navigate on the whole row** (`cursor-pointer`, `tabindex="0"`, `(click)` + `(keydown.enter)` → `router.navigate`), never an id-cell anchor.
- **`hlm-select`** with value≠label needs `*hlmSelectPortal` + `[itemToString]`.
- **Money/date formatting** via `core/format/display` (`money`, `displayDate`); server errors via `core/api/problem-details` `extractProblem(e).detail`.
- **Write actions** gated by `*appCan="'<cap>'"` in the template AND `canActivate: [canWrite]` + `data.requiredCapability` on the route. Capability key: **`bankrec.write`** for reconciliation/statements/adjustments and **`cash.write`** for cash vouchers — VERIFY both against the server-enforcement vocabulary in `Backend/.../Control` during Task 1; if the codebase uses a single `banking.write`, use that everywhere and note it in the task's commit.
- **Client id** from `ClientContextService.clientId()`; every service call guards `if (!this.client.clientId()) return EMPTY;`.

## File structure

**Core (`UI/Angular/src/app/core/banking/`)**
- `banking.ts` — all TypeScript models (cash vouchers, statements, worksheet, auto-match, adjustments, request DTOs, list queries) + label helpers.
- `banking.service.ts` — one `@Injectable({providedIn:'root'})` service; all Cash + Reconciliation HTTP calls; unwraps view envelopes.
- `banking.service.spec.ts` — service tests (URL shape, params, unwrap), grown per slice.
- `banking.serialization.spec.ts` — camelCase round-trip guard.

**Feature (`UI/Angular/src/app/features/banking/`)**
- `banking-shell.ts` — tab nav + `<router-outlet>`.
- `cash-list.ts` — combined disbursement + deposit list.
- `cash-voucher-editor.ts` — record a disbursement OR deposit (kind from route data).
- `cash-voucher-detail.ts` — voucher detail + void.
- `statement-list.ts` — per-cash-account statement list + account selector.
- `statement-editor.ts` — manual statement entry (foot check).
- `statement-detail.ts` — statement header/lines + "Start reconciliation".
- `statement-import.ts` — file upload → mapping builder → preview → confirm.
- `reconciliation-list.ts` — reconciliations + start.
- `reconciliation-worksheet.ts` — the worksheet (clear/unclear/auto-match/complete + adjustments panel).
- One `*.spec.ts` per component.

**Routing/nav**
- `app.routes.ts` — add the `/cash` route tree; extend the `built` array.
- `layout/nav.ts` — keep `/cash` + `/cash/reconciliation`; optionally add a Statements affordance.

---

## Slice BK-1 — Core layer + Cash tab

Delivers: `core/banking` models+service, the `BankingShell` with routing/nav wired, and the full Cash voucher UI (list, record disbursement/deposit, detail, void). After BK-1 the Cash tab is fully usable.

### Task 1: Banking models + serialization guard

**Files:**
- Create: `UI/Angular/src/app/core/banking/banking.ts`
- Test: `UI/Angular/src/app/core/banking/banking.serialization.spec.ts`

**Interfaces:**
- Produces: types `CashDisbursement`, `CashDeposit`, `CashVoucherView`, `CashLine`, `BankStatement`, `BankStatementLine`, `ReconciliationRef`, `WorksheetEntry`, `ReconciliationWorksheet`, `AutoMatchProposal`, `AutoMatch`, `UnmatchedLine`, `MatchableEntry`, `BankAdjustment`; requests `RecordCashVoucherRequest`, `RecordBankStatementRequest`, `RecordAdjustmentRequest`, `StatementImportForm`, `CsvMapping`, `ColumnRef`; unions `CashStatus`, `AdjustmentKind`, `ReconciliationStatus`, `BankStatementStatus`; query `BankingListQuery`; helpers `adjustmentKindLabel`.

- [ ] **Step 1: Write the failing guard test**

```typescript
// banking.serialization.spec.ts
import { CashDisbursement, BankAdjustment, ReconciliationWorksheet } from './banking';

describe('banking model serialization keys', () => {
  it('round-trips a cash disbursement JSON payload by camelCase key', () => {
    const json = { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a1', amount: 100 }],
      date: '2026-03-01', reference: null, memo: null, status: 'Posted' };
    const v = json as unknown as CashDisbursement;
    expect(v.number).toBe('CD-00001');
    expect(v.lines[0].accountId).toBe('a1');
    expect(v.status).toBe('Posted');
  });

  it('round-trips a worksheet with cleared entries and verdict', () => {
    const json = { reconciliation: { id: 'r1', number: 'REC-00001', cashAccountId: 'c1',
        bankStatementId: 'b1', statementDate: '2026-03-31', status: 'InProgress', clearedEntryIds: [] },
      statement: { id: 'b1', number: 'BST-00001', cashAccountId: 'c1', statementDate: '2026-03-31',
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' },
      entries: [{ entryId: 'e1', date: '2026-03-05', reference: 'r', sourceType: 'Cash', cashEffect: 100, cleared: true }],
      bookBalance: 100, clearedTotal: 100, reconciledDifference: 0, balanced: true };
    const w = json as unknown as ReconciliationWorksheet;
    expect(w.entries[0].cashEffect).toBe(100);
    expect(w.balanced).toBe(true);
  });

  it('round-trips a bank adjustment kind', () => {
    const json = { id: 'j1', number: 'ADJ-00001', reconciliationId: 'r1', cashAccountId: 'c1',
      offsetAccountId: 'o1', kind: 'Charge', amount: 12.5, date: '2026-03-31', memo: null, status: 'Posted' };
    const a = json as unknown as BankAdjustment;
    expect(a.kind).toBe('Charge');
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npm test -- banking.serialization`
Expected: FAIL — cannot find module `./banking`.

- [ ] **Step 3: Write the models**

```typescript
// banking.ts
export type CashStatus = 'Posted' | 'Void';
export type AdjustmentKind = 'Charge' | 'Credit';
export type ReconciliationStatus = 'InProgress' | 'Completed';
export type BankStatementStatus = 'Posted' | 'Void';

export const adjustmentKindLabel = (k: AdjustmentKind): string =>
  k === 'Charge' ? 'Bank charge' : 'Bank interest';

// ── Cash vouchers ────────────────────────────────────────────────────────────
export interface CashLine { accountId: string; amount: number; }
export interface CashDisbursement {
  id: string; number: string | null; lines: CashLine[];
  date: string; reference: string | null; memo: string | null; status: CashStatus;
}
export type CashDeposit = CashDisbursement;                 // identical shape, different endpoint
export interface CashDisbursementView { disbursement: CashDisbursement; }
export interface CashDepositView { deposit: CashDeposit; }
export type CashKind = 'disbursement' | 'deposit';
/** A row in the combined cash list — normalized across both kinds. */
export interface CashVoucherRow {
  id: string; kind: CashKind; number: string | null; date: string;
  amount: number; memo: string | null; status: CashStatus;
}

export interface RecordCashVoucherRequest {
  lines: CashLine[]; date: string; reference?: string | null; memo?: string | null;
}

// ── Bank statements ──────────────────────────────────────────────────────────
export interface BankStatementLine { date: string; amount: number; description: string; externalRef: string | null; }
export interface BankStatement {
  id: string; number: string | null; cashAccountId: string; statementDate: string;
  openingBalance: number; closingBalance: number; lines: BankStatementLine[]; status: BankStatementStatus;
}
export interface RecordBankStatementRequest {
  cashAccountId: string; statementDate: string; openingBalance: number; closingBalance: number;
  lines: BankStatementLine[];
}

// ── Import (parse-to-preview) ────────────────────────────────────────────────
export type InterchangeFormat = 'Csv' | 'Ofx';
export interface ColumnRef { index?: number | null; header?: string | null; }
export interface CsvMapping {
  date: ColumnRef; amount?: ColumnRef | null; debit?: ColumnRef | null; credit?: ColumnRef | null;
  description: ColumnRef; reference?: ColumnRef | null; dateFormat?: string | null;
  hasHeader: boolean; delimiter?: string | null;
  status?: ColumnRef | null; excludeStatuses?: string[] | null;
}
export interface StatementPreview {
  lines: BankStatementLine[]; detectedOpeningBalance: number | null; detectedClosingBalance: number | null;
  statementDate: string | null; accountHint: string | null;
}
export interface ImportPreviewResponse { statements: StatementPreview[]; warnings: string[]; }

// ── Reconciliation ───────────────────────────────────────────────────────────
export interface ReconciliationRef {
  id: string; number: string | null; cashAccountId: string; bankStatementId: string;
  statementDate: string; status: ReconciliationStatus; clearedEntryIds: string[];
}
export interface WorksheetEntry {
  entryId: string; date: string; reference: string | null; sourceType: string | null;
  cashEffect: number; cleared: boolean;
}
export interface ReconciliationWorksheet {
  reconciliation: ReconciliationRef; statement: BankStatement; entries: WorksheetEntry[];
  bookBalance: number; clearedTotal: number; reconciledDifference: number; balanced: boolean;
}
export interface MatchableEntry { entryId: string; date: string; cashEffect: number; }
export interface AutoMatch { entryId: string; statementLineIndex: number; }
export interface UnmatchedLine { statementLineIndex: number; date: string; amount: number; description: string; }
export interface AutoMatchProposal {
  matches: AutoMatch[]; unmatchedLines: UnmatchedLine[];
  unmatchedEntries: MatchableEntry[]; matchedEntryIds: string[];
}

// ── Adjustments ──────────────────────────────────────────────────────────────
export interface BankAdjustment {
  id: string; number: string | null; reconciliationId: string; cashAccountId: string;
  offsetAccountId: string; kind: AdjustmentKind; amount: number; date: string;
  memo: string | null; status: CashStatus;
}
export interface RecordAdjustmentRequest {
  offsetAccountId: string; amount: number; kind: AdjustmentKind; date?: string | null; memo?: string | null;
}

export interface BankingListQuery { skip: number; limit: number; order?: 'asc' | 'desc'; }
```

> Note the `AutoMatch` field names: the C# record is `AutoMatch(...)` in `AutoMatcher.cs`. During implementation, open `Modules/Banking/Reconciliation/.../AutoMatcher.cs` and confirm the exact property names on `AutoMatch` (line 8) — adjust `entryId`/`statementLineIndex` here to match the serialized JSON before finishing the task.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npm test -- banking.serialization`
Expected: PASS (3 tests).

- [ ] **Step 5: Verify capability keys, then commit**

Grep the backend for the reconciliation/cash write capability constants:
Run: `cd ../.. && grep -rn "bankrec\|cash.write\|banking.write" Backend/Accounting101.Ledger.Api/Control UI/Angular/src/app/layout/nav.ts`
Record the real key(s) in the commit body; if they differ from `bankrec.write`/`cash.write`, note it so later tasks use the correct string.

```bash
git add UI/Angular/src/app/core/banking/banking.ts UI/Angular/src/app/core/banking/banking.serialization.spec.ts
git commit -m "feat(banking-ui): core banking models + serialization guard"
```

---

### Task 2: Banking service — Cash endpoints

**Files:**
- Create: `UI/Angular/src/app/core/banking/banking.service.ts`
- Test: `UI/Angular/src/app/core/banking/banking.service.spec.ts`

**Interfaces:**
- Consumes: models from Task 1; `ClientContextService.clientId()`; `PagedResponse<T>` from `core/api/paged-response`; `environment.apiBaseUrl`.
- Produces: `BankingService` with (this slice) `listCash(q): Observable<PagedResponse<CashVoucherRow>>`, `getDisbursement(id)`, `getDeposit(id)`, `recordDisbursement(req)`, `recordDeposit(req)`, `voidDisbursement(id, reason?)`, `voidDeposit(id, reason?)`, `entriesForSource(sourceRef)`. Later slices extend the same class.

- [ ] **Step 1: Write the failing service test**

```typescript
// banking.service.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BankingService } from './banking.service';
import { ClientContextService } from '../client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return { svc: TestBed.inject(BankingService), ctrl: TestBed.inject(HttpTestingController) };
}

describe('BankingService — cash', () => {
  it('recordDisbursement posts and unwraps the disbursement view', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.recordDisbursement({ lines: [{ accountId: 'a1', amount: 50 }], date: '2026-03-01' }).subscribe(d => (got = d));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements');
    expect(req.request.method).toBe('POST');
    req.flush({ disbursement: { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a1', amount: 50 }],
      date: '2026-03-01', reference: null, memo: null, status: 'Posted' } });
    expect(got.id).toBe('v1');
    ctrl.verify();
  });

  it('listCash normalizes disbursements and deposits into signed rows', () => {
    const { svc, ctrl } = setup();
    let rows: { kind: string; amount: number }[] = [];
    svc.listCash({ skip: 0, limit: 50 }).subscribe(p => (rows = p.items));
    const d = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/cash-disbursements');
    d.flush({ items: [{ disbursement: { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a', amount: 50 }],
      date: '2026-03-01', reference: null, memo: null, status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    const p = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/cash-deposits');
    p.flush({ items: [{ deposit: { id: 'w1', number: 'CR-00001', lines: [{ accountId: 'b', amount: 30 }],
      date: '2026-03-02', reference: null, memo: null, status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    expect(rows.find(r => r.kind === 'disbursement')!.amount).toBe(50);
    expect(rows.find(r => r.kind === 'deposit')!.amount).toBe(30);
    ctrl.verify();
  });

  it('voidDeposit posts a reason', () => {
    const { svc, ctrl } = setup();
    svc.voidDeposit('w1', 'error').subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-deposits/w1/void');
    expect(req.request.body).toEqual({ reason: 'error' });
    req.flush({ deposit: { id: 'w1', number: 'CR-00001', lines: [], date: '2026-03-02',
      reference: null, memo: null, status: 'Void' } });
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: FAIL — cannot find module `./banking.service`.

- [ ] **Step 3: Write the service (cash section)**

```typescript
// banking.service.ts
import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, forkJoin, map } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import { EntryResponse } from '../entries/entry';
import {
  CashDisbursement, CashDeposit, CashDisbursementView, CashDepositView, CashVoucherRow,
  RecordCashVoucherRequest, BankingListQuery,
} from './banking';

@Injectable({ providedIn: 'root' })
export class BankingService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }
  private listParams(q: BankingListQuery): HttpParams {
    let p = new HttpParams().set('skip', q.skip).set('limit', q.limit);
    if (q.order) p = p.set('order', q.order);
    return p;
  }
  private sum(lines: { amount: number }[]): number { return lines.reduce((s, l) => s + l.amount, 0); }

  // ── Cash vouchers ──────────────────────────────────────────────────────────
  recordDisbursement(req: RecordCashVoucherRequest): Observable<CashDisbursement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<CashDisbursementView>(this.base('/cash-disbursements'), req).pipe(map(v => v.disbursement));
  }
  recordDeposit(req: RecordCashVoucherRequest): Observable<CashDeposit> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<CashDepositView>(this.base('/cash-deposits'), req).pipe(map(v => v.deposit));
  }
  getDisbursement(id: string): Observable<CashDisbursement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<CashDisbursementView>(this.base(`/cash-disbursements/${id}`)).pipe(map(v => v.disbursement));
  }
  getDeposit(id: string): Observable<CashDeposit> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<CashDepositView>(this.base(`/cash-deposits/${id}`)).pipe(map(v => v.deposit));
  }
  voidDisbursement(id: string, reason?: string | null): Observable<CashDisbursement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<CashDisbursementView>(this.base(`/cash-disbursements/${id}/void`), { reason: reason ?? null })
      .pipe(map(v => v.disbursement));
  }
  voidDeposit(id: string, reason?: string | null): Observable<CashDeposit> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<CashDepositView>(this.base(`/cash-deposits/${id}/void`), { reason: reason ?? null })
      .pipe(map(v => v.deposit));
  }

  /** Combined cash list: fetch both kinds, normalize to signed rows (disbursement −, deposit +), sort by date desc. */
  listCash(q: BankingListQuery): Observable<PagedResponse<CashVoucherRow>> {
    if (!this.client.clientId()) return EMPTY;
    const params = this.listParams(q);
    return forkJoin({
      disb: this.http.get<PagedResponse<CashDisbursementView>>(this.base('/cash-disbursements'), { params }),
      dep: this.http.get<PagedResponse<CashDepositView>>(this.base('/cash-deposits'), { params }),
    }).pipe(map(({ disb, dep }) => {
      const rows: CashVoucherRow[] = [
        ...disb.items.map(v => ({ id: v.disbursement.id, kind: 'disbursement' as const, number: v.disbursement.number,
          date: v.disbursement.date, amount: this.sum(v.disbursement.lines), memo: v.disbursement.memo, status: v.disbursement.status })),
        ...dep.items.map(v => ({ id: v.deposit.id, kind: 'deposit' as const, number: v.deposit.number,
          date: v.deposit.date, amount: this.sum(v.deposit.lines), memo: v.deposit.memo, status: v.deposit.status })),
      ].sort((a, b) => (a.date < b.date ? 1 : a.date > b.date ? -1 : (a.number ?? '') < (b.number ?? '') ? 1 : -1));
      return { items: rows, total: disb.total + dep.total, skip: q.skip, limit: q.limit };
    }));
  }

  /** Posted journal entry(ies) for a banking document — powers the "posted journal entry" link. */
  entriesForSource(sourceRef: string): Observable<EntryResponse[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<EntryResponse[]>(this.base('/entries'), { params: new HttpParams().set('sourceRef', sourceRef) });
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/banking/banking.service.ts UI/Angular/src/app/core/banking/banking.service.spec.ts
git commit -m "feat(banking-ui): banking service — cash disbursement/deposit endpoints"
```

---

### Task 3: Banking shell + routing + nav

**Files:**
- Create: `UI/Angular/src/app/features/banking/banking-shell.ts`
- Create: `UI/Angular/src/app/features/banking/banking-shell.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add `/cash` tree + extend `built`)
- Modify: `UI/Angular/src/app/layout/nav.ts` (only if a Statements affordance is wanted; the `/cash` + `/cash/reconciliation` entries already exist)

**Interfaces:**
- Consumes: `CashList`, `CashVoucherEditor`, `CashVoucherDetail` (Tasks 4–6) — route them with lazy component refs added as those tasks land; in this task, wire the routes for the components that exist and stub the rest by pointing at `CashList` until Tasks 4–6 replace them. (Order the slice so Task 4–6 components exist before final route wiring, OR land Task 3's route file edits in Task 6.)

> **Sequencing note:** implement Tasks 4, 5, 6 first, then do this task's `app.routes.ts` wiring last within BK-1 so every referenced component exists. The shell component itself (below) has no such dependency and can land here.

- [ ] **Step 1: Write the shell spec**

```typescript
// banking-shell.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { BankingShell } from './banking-shell';

describe('BankingShell', () => {
  it('renders three tab links', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    });
    const fixture = TestBed.createComponent(BankingShell);
    fixture.detectChanges();
    const tabs = fixture.nativeElement.querySelectorAll('nav a');
    expect(tabs.length).toBe(3);
    expect([...tabs].map((a: HTMLAnchorElement) => a.getAttribute('data-testid')))
      .toEqual(['tab-cash', 'tab-statements', 'tab-reconcile']);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npm test -- banking-shell`
Expected: FAIL — cannot find module `./banking-shell`.

- [ ] **Step 3: Write the shell**

```typescript
// banking-shell.ts
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-banking-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="flex flex-col gap-0">
      <nav class="flex gap-1 border-b border-border px-4 pt-4">
        <a routerLink="cash" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-cash">Cash</a>
        <a routerLink="statements" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-statements">Statements</a>
        <a routerLink="reconciliation" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-reconcile">Reconcile</a>
      </nav>
      <router-outlet />
    </div>
  `,
})
export class BankingShell {}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npm test -- banking-shell`
Expected: PASS.

- [ ] **Step 5: Wire routes (do this after Tasks 4–6 exist)**

In `app.routes.ts`, add imports and a `/cash` route tree after the `fixed-assets` block:

```typescript
import { BankingShell } from './features/banking/banking-shell';
import { CashList } from './features/banking/cash-list';
import { CashVoucherEditor } from './features/banking/cash-voucher-editor';
import { CashVoucherDetail } from './features/banking/cash-voucher-detail';
// (statement + reconciliation components imported as BK-2..BK-5 land)
```

```typescript
  { path: 'cash', component: BankingShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'cash' },
    { path: 'cash', component: CashList },
    { path: 'cash/disbursements/new', component: CashVoucherEditor, canActivate: [canWrite],
      data: { requiredCapability: 'cash.write', fallback: '/cash/cash', kind: 'disbursement' } },
    { path: 'cash/deposits/new', component: CashVoucherEditor, canActivate: [canWrite],
      data: { requiredCapability: 'cash.write', fallback: '/cash/cash', kind: 'deposit' } },
    { path: 'cash/:id', component: CashVoucherDetail },
    // BK-2..BK-5 add: statements, statements/new, statements/import, statements/:id,
    //                 reconciliation, reconciliation/:id
  ] },
```

Extend the `built` array with `'/cash'`:

```typescript
const built = ['/dashboard', '/journal', '/trial-balance', '/statements', '/accounts', '/receivables', '/payables', '/payroll', '/fixed-assets', '/cash', '/admin/users', '/admin/access/sets', '/admin/access/sets/new'];
```

> `/cash/reconciliation` (nav child) resolves to the `reconciliation` child route once BK-4 lands; until then it falls through to the placeholder, which is acceptable mid-epic.

- [ ] **Step 6: Verify build + commit**

Run: `cd UI/Angular && npm run build`
Expected: build clean.

```bash
git add UI/Angular/src/app/features/banking/banking-shell.ts UI/Angular/src/app/features/banking/banking-shell.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(banking-ui): banking shell + /cash route tree"
```

---

### Task 4: Cash list

**Files:**
- Create: `UI/Angular/src/app/features/banking/cash-list.ts`
- Test: `UI/Angular/src/app/features/banking/cash-list.spec.ts`

**Interfaces:**
- Consumes: `BankingService.listCash`, `CashVoucherRow`, `ClientContextService`, `money`/`displayDate`, `CanDirective`.

- [ ] **Step 1: Write the failing test**

```typescript
// cash-list.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CashList } from './cash-list';
import { ClientContextService } from '../../core/client/client-context.service';

describe('CashList', () => {
  it('renders a row per voucher with signed amounts', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(CashList);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/cash-disbursements')).flush(
      { items: [{ disbursement: { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a', amount: 50 }],
        date: '2026-03-01', reference: null, memo: 'rent', status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    ctrl.expectOne(r => r.url.endsWith('/cash-deposits')).flush({ items: [], total: 0, skip: 0, limit: 50 });
    fixture.detectChanges();
    const rows = fixture.nativeElement.querySelectorAll('tbody tr');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('CD-00001');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npm test -- cash-list`
Expected: FAIL — cannot find module `./cash-list`.

- [ ] **Step 3: Write the component**

```typescript
// cash-list.ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { BankingService } from '../../core/banking/banking.service';
import { CashVoucherRow } from '../../core/banking/banking';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-cash-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Cash vouchers</h1>
        <div class="ms-auto flex gap-2">
          <a *appCan="'cash.write'" hlmBtn size="sm" variant="outline" routerLink="/cash/cash/deposits/new">New deposit</a>
          <a *appCan="'cash.write'" hlmBtn size="sm" routerLink="/cash/cash/disbursements/new">New disbursement</a>
        </div>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (rows().length === 0) {
        <p class="text-muted-foreground text-sm">No cash vouchers yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr><th hlmTh>Number</th><th hlmTh>Date</th><th hlmTh>Type</th>
                <th hlmTh class="text-right">Amount</th><th hlmTh>Memo</th><th hlmTh>Status</th></tr>
            </thead>
            <tbody hlmTBody>
              @for (r of rows(); track r.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="open(r.id)" (keydown.enter)="open(r.id)">
                  <td hlmTd>{{ r.number ?? '—' }}</td>
                  <td hlmTd>{{ date(r.date) }}</td>
                  <td hlmTd>{{ r.kind === 'deposit' ? 'Deposit' : 'Disbursement' }}</td>
                  <td hlmTd class="text-right tabular-nums" [class.text-destructive]="r.kind === 'disbursement'">
                    {{ r.kind === 'disbursement' ? '(' + money(r.amount) + ')' : money(r.amount) }}</td>
                  <td hlmTd>{{ r.memo ?? '' }}</td>
                  <td hlmTd [class.text-destructive]="r.status === 'Void'">{{ r.status }}</td>
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
export class CashList {
  private readonly svc = inject(BankingService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({ id: this.client.clientId(), skip: this.skip(), limit: this.limit() }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listCash({ skip, limit }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading cash vouchers'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<CashVoucherRow> | null },
  );

  readonly rows = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  open(id: string): void { void this.router.navigate(['/cash/cash', id]); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npm test -- cash-list`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/banking/cash-list.ts UI/Angular/src/app/features/banking/cash-list.spec.ts
git commit -m "feat(banking-ui): combined cash voucher list"
```

---

### Task 5: Cash voucher editor (disbursement + deposit)

**Files:**
- Create: `UI/Angular/src/app/features/banking/cash-voucher-editor.ts`
- Test: `UI/Angular/src/app/features/banking/cash-voucher-editor.spec.ts`

**Interfaces:**
- Consumes: `BankingService.recordDisbursement`/`recordDeposit`, `AccountsService` (`accounts()`, `load()`, `label(id)`, `postable`), `extractProblem`, route `data.kind` (`'disbursement' | 'deposit'`).
- Produces: a voucher editor whose "kind" comes from route data; the clerk enters N non-cash lines `{account, amount}`; the balancing cash line is server-derived (shown as an informational total, not entered).

- [ ] **Step 1: Write the failing test**

```typescript
// cash-voucher-editor.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CashVoucherEditor } from './cash-voucher-editor';
import { ClientContextService } from '../../core/client/client-context.service';

function make(kind: 'disbursement' | 'deposit') {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { data: { kind } } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(CashVoucherEditor);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
    [{ id: 'a1', number: '6200', name: 'Rent', type: 'Expense', postable: true }]);   // bare array — accounts endpoint is not paged
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('CashVoucherEditor', () => {
  it('posts a disbursement to the disbursements endpoint', () => {
    const { fixture, ctrl } = make('disbursement');
    const cmp = fixture.componentInstance;
    cmp.setAccount(0, 'a1'); cmp.setAmount(0, 500);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements');
    expect(req.request.body.lines).toEqual([{ accountId: 'a1', amount: 500 }]);
    req.flush({ disbursement: { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a1', amount: 500 }],
      date: cmp.date(), reference: null, memo: null, status: 'Posted' } });
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npm test -- cash-voucher-editor`
Expected: FAIL — cannot find module.

- [ ] **Step 3: Write the component**

```typescript
// cash-voucher-editor.ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { CashKind, CashLine, RecordCashVoucherRequest } from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

interface LineModel { lineId: string; accountId: string; amount: number | null; }
const emptyLine = (): LineModel => ({ lineId: crypto.randomUUID(), accountId: '', amount: null });

@Component({
  selector: 'app-cash-voucher-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <h1 class="text-2xl font-bold">{{ isDeposit() ? 'Record cash deposit' : 'Record cash disbursement' }}</h1>
      <p class="text-sm text-muted-foreground">
        Enter the non-cash lines. The balancing Cash {{ isDeposit() ? 'debit' : 'credit' }} is posted automatically.
      </p>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Reference</label>
          <input hlmInput type="text" [value]="reference() ?? ''" (input)="reference.set($any($event.target).value || null)" />
        </div>
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      <table class="w-full text-sm">
        <thead><tr class="text-left text-muted-foreground"><th class="py-1">Account</th><th class="text-right">Amount</th><th></th></tr></thead>
        <tbody>
          @for (line of lines(); track line.lineId; let i = $index) {
            <tr>
              <td class="py-1 pr-2">
                <div hlmSelect [value]="line.accountId" [itemToString]="accountItemToString" (valueChange)="setAccount(i, $any($event))" class="w-full">
                  <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select account" /></hlm-select-trigger>
                  <hlm-select-content *hlmSelectPortal>
                    @for (a of postableAccounts(); track a.id) { <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item> }
                  </hlm-select-content>
                </div>
              </td>
              <td class="pr-2"><input hlmInput type="number" class="text-right tabular-nums" [value]="line.amount ?? ''"
                    (input)="setAmount(i, $any($event.target).value === '' ? null : +$any($event.target).value)" /></td>
              <td><button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine(i)" [disabled]="lines().length <= 1">✕</button></td>
            </tr>
          }
        </tbody>
        <tfoot>
          <tr class="font-semibold border-t border-border">
            <td class="py-1 text-right pr-2">Cash {{ isDeposit() ? 'debit' : 'credit' }} (auto)</td>
            <td class="text-right tabular-nums">{{ money(total()) }}</td><td></td>
          </tr>
        </tfoot>
      </table>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-3">
        <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>
        <div class="flex items-center gap-2 ms-auto">
          <button *appCan="'cash.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Record</button>
          <a hlmBtn variant="outline" routerLink="/cash/cash">Cancel</a>
        </div>
      </div>
    </div>
  `,
})
export class CashVoucherEditor {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly kind = (this.route.snapshot.data['kind'] as CashKind) ?? 'disbursement';
  readonly isDeposit = computed(() => this.kind === 'deposit');

  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly reference = signal<string | null>(null);
  readonly memo = signal<string | null>(null);
  readonly lines = signal<LineModel[]>([emptyLine()]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly postableAccounts = computed(() => this.accounts.accounts().filter(a => a.postable));
  readonly total = computed(() => this.lines().reduce((s, l) => s + (l.amount ?? 0), 0));
  readonly canSave = computed(() =>
    this.lines().length > 0 && this.lines().every(l => l.accountId && (l.amount ?? 0) > 0));

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  readonly accountItemToString = (id: string): string => this.accounts.label(id);
  setAccount(i: number, id: string): void { this.lines.update(v => v.map((l, idx) => idx === i ? { ...l, accountId: id } : l)); }
  setAmount(i: number, amount: number | null): void { this.lines.update(v => v.map((l, idx) => idx === i ? { ...l, amount } : l)); }
  addLine(): void { this.lines.update(v => [...v, emptyLine()]); }
  removeLine(i: number): void { this.lines.update(v => v.filter((_, idx) => idx !== i)); }
  money(n: number): string { return fmtMoney(n); }

  private toRequest(): RecordCashVoucherRequest {
    const lines: CashLine[] = this.lines().map(l => ({ accountId: l.accountId, amount: l.amount! }));
    return { lines, date: this.date(), reference: this.reference(), memo: this.memo() };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    const call = this.isDeposit() ? this.svc.recordDeposit(this.toRequest()) : this.svc.recordDisbursement(this.toRequest());
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => { this.busy.set(false); void this.router.navigate(['/cash/cash', v.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npm test -- cash-voucher-editor`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/banking/cash-voucher-editor.ts UI/Angular/src/app/features/banking/cash-voucher-editor.spec.ts
git commit -m "feat(banking-ui): cash voucher editor (disbursement/deposit)"
```

---

### Task 6: Cash voucher detail + void

**Files:**
- Create: `UI/Angular/src/app/features/banking/cash-voucher-detail.ts`
- Test: `UI/Angular/src/app/features/banking/cash-voucher-detail.spec.ts`

**Interfaces:**
- Consumes: `BankingService.getDisbursement`/`getDeposit`/`voidDisbursement`/`voidDeposit`/`entriesForSource`, `CashDisbursement`, `AccountsService.label`, `money`/`displayDate`, `extractProblem`.
- Route: `/cash/cash/:id`. Kind is not in the URL, so the detail resolves it by trying disbursement first, then deposit (a 404 falls through to the other).

- [ ] **Step 1: Write the failing test**

```typescript
// cash-voucher-detail.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CashVoucherDetail } from './cash-voucher-detail';
import { ClientContextService } from '../../core/client/client-context.service';

describe('CashVoucherDetail', () => {
  it('loads a disbursement and shows its number', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'v1']]) } } }],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(CashVoucherDetail);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/cash-disbursements/v1')).flush(
      { disbursement: { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a', amount: 50 }],
        date: '2026-03-01', reference: null, memo: null, status: 'Posted' } });
    ctrl.expectOne(r => r.url.endsWith('/entries')).flush([]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('CD-00001');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npm test -- cash-voucher-detail`
Expected: FAIL — cannot find module.

- [ ] **Step 3: Write the component**

```typescript
// cash-voucher-detail.ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, of } from 'rxjs';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { BankingService } from '../../core/banking/banking.service';
import { CashDisbursement, CashKind } from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-cash-voucher-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/cash/cash" class="text-sm text-muted-foreground hover:text-foreground">← Cash vouchers</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (voucher(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">{{ kind() === 'deposit' ? 'Deposit' : 'Disbursement' }} {{ v.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="v.status === 'Void'">{{ v.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Date</td><td class="text-right">{{ date(v.date) }}</td></tr>
            @if (v.reference) { <tr><td class="py-1 text-muted-foreground">Reference</td><td class="text-right">{{ v.reference }}</td></tr> }
            @if (v.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ v.memo }}</td></tr> }
          </tbody>
        </table>

        <table class="w-full text-sm">
          <thead><tr class="text-left text-muted-foreground"><th class="py-1">Account</th><th class="text-right">Amount</th></tr></thead>
          <tbody>
            @for (l of v.lines; track l.accountId) {
              <tr><td class="py-1">{{ label(l.accountId) }}</td><td class="text-right tabular-nums">{{ money(l.amount) }}</td></tr>
            }
            <tr class="font-semibold border-t border-border">
              <td class="py-1 text-right">Cash {{ kind() === 'deposit' ? 'debit' : 'credit' }}</td>
              <td class="text-right tabular-nums">{{ money(total(v)) }}</td></tr>
          </tbody>
        </table>

        @if (postedEntryId(); as eid) { <a [routerLink]="['/journal', eid]" class="text-sm text-primary hover:underline">Posted journal entry →</a> }

        @if (v.status === 'Posted') {
          <div *appCan="'cash.write'" class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)" [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button hlmBtn type="button" variant="outline" (click)="voidVoucher()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class CashVoucherDetail {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly voucher = signal<CashDisbursement | null>(null);
  readonly kind = signal<CashKind>('disbursement');
  readonly postedEntryId = signal<string | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); this.reload(); }

  reload(): void {
    // Try disbursement first; on 404 fall through to deposit.
    this.svc.getDisbursement(this.id).pipe(
      catchError(() => { this.kind.set('deposit'); return this.svc.getDeposit(this.id).pipe(catchError((e) => { this.message.set(extractProblem(e).detail); return of(null); })); }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe((v) => { if (v) this.voucher.set(v); this.busy.set(false); });

    this.svc.entriesForSource(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (entries) => this.postedEntryId.set(entries[0]?.id ?? null),
      error: () => this.postedEntryId.set(null),
    });
  }

  voidVoucher(): void {
    this.busy.set(true); this.message.set(null);
    const call = this.kind() === 'deposit' ? this.svc.voidDeposit(this.id, this.reason()) : this.svc.voidDisbursement(this.id, this.reason());
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  total(v: CashDisbursement): number { return v.lines.reduce((s, l) => s + l.amount, 0); }
  label(id: string): string { return this.accounts.label(id); }
  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npm test -- cash-voucher-detail`
Expected: PASS.

- [ ] **Step 5: Now complete Task 3's route wiring** (add `/cash` tree + `built` entry) since all three cash components now exist. Run the build.

Run: `cd UI/Angular && npm run build`
Expected: build clean.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/banking/cash-voucher-detail.ts UI/Angular/src/app/features/banking/cash-voucher-detail.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(banking-ui): cash voucher detail + void; wire /cash routes"
```

**BK-1 done:** Cash tab fully usable. Add `Cash__Accounts__*` env vars to `.localdev/start.ps1` (see §8 of the spec) before smoke-testing.

---

## Slice BK-2 — Statements (manual entry)

Delivers: the Statements tab with a per-cash-account statement list, a manual-entry form with a live foot check, and a statement detail that launches a reconciliation.

### Task 7: Banking service — statement endpoints

**Files:**
- Modify: `UI/Angular/src/app/core/banking/banking.service.ts` (add statement methods)
- Modify: `UI/Angular/src/app/core/banking/banking.service.spec.ts` (add statement tests)

**Interfaces:**
- Produces: `listStatements(cashAccountId, q): Observable<PagedResponse<BankStatement>>`, `getStatement(id): Observable<BankStatement>`, `recordStatement(req): Observable<BankStatement>`. `BankStatement` is returned bare (no view envelope — see `RecordStatement` returning `Results.Created(..., statement)`).

- [ ] **Step 1: Add failing tests**

```typescript
// append to banking.service.spec.ts
describe('BankingService — statements', () => {
  it('listStatements requires cashAccountId and passes it as a query param', () => {
    const { svc, ctrl } = setup();
    let items: unknown[] = [];
    svc.listStatements('CA1', { skip: 0, limit: 50 }).subscribe(p => (items = p.items));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bank-statements');
    expect(req.request.params.get('cashAccountId')).toBe('CA1');
    req.flush({ items: [{ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }], total: 1, skip: 0, limit: 50 });
    expect((items[0] as { number: string }).number).toBe('BST-00001');
    ctrl.verify();
  });

  it('recordStatement posts the full request and returns the bare statement', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.recordStatement({ cashAccountId: 'CA1', statementDate: '2026-03-31', openingBalance: 0, closingBalance: 100,
      lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }] }).subscribe(s => (got = s));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.closingBalance).toBe(100);
    req.flush({ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' });
    expect(got.id).toBe('b1');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: FAIL — `listStatements`/`recordStatement` not a function.

- [ ] **Step 3: Add the methods**

Add imports to `banking.service.ts`:

```typescript
import {
  // ...existing...
  BankStatement, RecordBankStatementRequest,
} from './banking';
```

Add a Statements section to the class:

```typescript
  // ── Bank statements ──────────────────────────────────────────────────────────
  listStatements(cashAccountId: string, q: BankingListQuery): Observable<PagedResponse<BankStatement>> {
    if (!this.client.clientId()) return EMPTY;
    const params = this.listParams(q).set('cashAccountId', cashAccountId);
    return this.http.get<PagedResponse<BankStatement>>(this.base('/bank-statements'), { params });
  }
  getStatement(id: string): Observable<BankStatement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<BankStatement>(this.base(`/bank-statements/${id}`));
  }
  recordStatement(req: RecordBankStatementRequest): Observable<BankStatement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<BankStatement>(this.base('/bank-statements'), req);
  }
```

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: PASS (all cash + statement tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/banking/banking.service.ts UI/Angular/src/app/core/banking/banking.service.spec.ts
git commit -m "feat(banking-ui): banking service — bank-statement endpoints"
```

---

### Task 8: Statement list + cash-account selector

**Files:**
- Create: `UI/Angular/src/app/features/banking/statement-list.ts`
- Test: `UI/Angular/src/app/features/banking/statement-list.spec.ts`

**Interfaces:**
- Consumes: `BankingService.listStatements`, `AccountsService` (to populate the cash-account selector — filter to bank/cash accounts by `type === 'Asset'`), `BankStatement`, `money`/`displayDate`.
- Behavior: the list is per-account. A cash-account `hlm-select` sits at the top; until one is picked, show a prompt. When picked, load statements for it.

- [ ] **Step 1: Write the failing test**

```typescript
// statement-list.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StatementList } from './statement-list';
import { ClientContextService } from '../../core/client/client-context.service';

describe('StatementList', () => {
  it('loads statements once a cash account is selected', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(StatementList);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
      [{ id: 'CA1', number: '1000', name: 'Cash', type: 'Asset', postable: true }]);   // bare array — accounts endpoint is not paged
    fixture.componentInstance.cashAccountId.set('CA1');
    fixture.detectChanges();
    const req = ctrl.expectOne(r => r.url.endsWith('/bank-statements'));
    expect(req.request.params.get('cashAccountId')).toBe('CA1');
    req.flush({ items: [{ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }], total: 1, skip: 0, limit: 50 });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('BST-00001');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- statement-list`
Expected: FAIL — cannot find module.

- [ ] **Step 3: Write the component**

```typescript
// statement-list.ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { BankStatement } from '../../core/banking/banking';
import { PagedResponse } from '../../core/api/paged-response';
import { AccountsService } from '../../core/accounts/accounts.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-statement-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmSelectImports, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Bank statements</h1>
        <div class="ms-auto flex gap-2">
          <a *appCan="'bankrec.write'" hlmBtn size="sm" variant="outline" routerLink="/cash/statements/import">Import</a>
          <a *appCan="'bankrec.write'" hlmBtn size="sm" routerLink="/cash/statements/new">New statement</a>
        </div>
      </div>

      <div class="flex items-center gap-2 max-w-md">
        <label class="text-sm text-muted-foreground">Cash account</label>
        <div hlmSelect [value]="cashAccountId() ?? ''" [itemToString]="accountItemToString" (valueChange)="cashAccountId.set($any($event))" class="w-full">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a cash account" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            @for (a of cashAccounts(); track a.id) { <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item> }
          </hlm-select-content>
        </div>
      </div>

      @if (!cashAccountId()) {
        <p class="text-muted-foreground text-sm">Select a cash account to see its statements.</p>
      } @else if (error()) {
        <p class="text-destructive text-sm">{{ error() }}</p>
      } @else if (statements().length === 0) {
        <p class="text-muted-foreground text-sm">No statements for this account yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr><th hlmTh>Number</th><th hlmTh>Statement date</th>
                <th hlmTh class="text-right">Opening</th><th hlmTh class="text-right">Closing</th><th hlmTh>Status</th></tr>
            </thead>
            <tbody hlmTBody>
              @for (s of statements(); track s.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0" (click)="open(s.id)" (keydown.enter)="open(s.id)">
                  <td hlmTd>{{ s.number ?? '—' }}</td>
                  <td hlmTd>{{ date(s.statementDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(s.openingBalance) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(s.closingBalance) }}</td>
                  <td hlmTd [class.text-destructive]="s.status === 'Void'">{{ s.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
})
export class StatementList {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly cashAccountId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly cashAccounts = computed(() => this.accounts.accounts().filter(a => a.type === 'Asset' && a.postable));

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  private readonly query = computed(() => ({ id: this.client.clientId(), account: this.cashAccountId() }));
  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, account }) => {
        if (!id || !account) return of(null);
        return this.svc.listStatements(account, { skip: 0, limit: 50 }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading statements'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<BankStatement> | null },
  );

  readonly statements = computed(() => this.page()?.items ?? []);
  readonly accountItemToString = (id: string): string => this.accounts.label(id);
  open(id: string): void { void this.router.navigate(['/cash/statements', id]); }
  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- statement-list`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/banking/statement-list.ts UI/Angular/src/app/features/banking/statement-list.spec.ts
git commit -m "feat(banking-ui): bank statement list + cash-account selector"
```

---

### Task 9: Statement manual editor (foot check)

**Files:**
- Create: `UI/Angular/src/app/features/banking/statement-editor.ts`
- Test: `UI/Angular/src/app/features/banking/statement-editor.spec.ts`

**Interfaces:**
- Consumes: `BankingService.recordStatement`, `AccountsService`, `RecordBankStatementRequest`, `BankStatementLine`, `extractProblem`, `money`.
- Behavior: cash account, statement date, opening/closing balance, N lines `{date, amount(signed), description, externalRef?}`. A **live foot check** computes `opening + Σamounts` and compares to `closing`; the Record button is disabled until it foots. Server `422` (non-footing) still surfaced.

- [ ] **Step 1: Write the failing test**

```typescript
// statement-editor.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StatementEditor } from './statement-editor';
import { ClientContextService } from '../../core/client/client-context.service';

function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(StatementEditor);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
    [{ id: 'CA1', number: '1000', name: 'Cash', type: 'Asset', postable: true }]);   // bare array — accounts endpoint is not paged
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('StatementEditor', () => {
  it('blocks save until the statement foots, then posts', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1'); cmp.openingBalance.set(0); cmp.closingBalance.set(100);
    cmp.setLine(0, { date: '2026-03-05', amount: 60, description: 'dep', externalRef: null });
    expect(cmp.foots()).toBe(false);           // 0 + 60 ≠ 100
    cmp.addLine();
    cmp.setLine(1, { date: '2026-03-06', amount: 40, description: 'dep2', externalRef: null });
    expect(cmp.foots()).toBe(true);            // 0 + 100 = 100
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements');
    expect(req.request.body.lines.length).toBe(2);
    req.flush({ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: cmp.statementDate(),
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' });
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- statement-editor`
Expected: FAIL — cannot find module.

- [ ] **Step 3: Write the component**

```typescript
// statement-editor.ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { BankStatementLine, RecordBankStatementRequest } from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

interface LineModel extends BankStatementLine { lineId: string; }
const emptyLine = (): LineModel => ({ lineId: crypto.randomUUID(), date: new Date().toISOString().slice(0, 10), amount: 0, description: '', externalRef: null });

@Component({
  selector: 'app-statement-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <h1 class="text-2xl font-bold">New bank statement</h1>
      <p class="text-sm text-muted-foreground">Line amounts are signed from the bank's view: + money in, − money out. The statement must foot before you can record it.</p>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Cash account</label>
          <div hlmSelect [value]="cashAccountId() ?? ''" [itemToString]="accountItemToString" (valueChange)="cashAccountId.set($any($event))" class="w-full">
            <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a cash account" /></hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal>
              @for (a of cashAccounts(); track a.id) { <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item> }
            </hlm-select-content>
          </div>
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Statement date</label>
          <input hlmInput type="date" [value]="statementDate()" (change)="statementDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Opening balance</label>
          <input hlmInput type="number" class="text-right tabular-nums" [value]="openingBalance()" (input)="openingBalance.set(+$any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Closing balance</label>
          <input hlmInput type="number" class="text-right tabular-nums" [value]="closingBalance()" (input)="closingBalance.set(+$any($event.target).value)" />
        </div>
      </div>

      <table class="w-full text-sm">
        <thead><tr class="text-left text-muted-foreground"><th class="py-1">Date</th><th>Description</th><th class="text-right">Amount</th><th>Ref</th><th></th></tr></thead>
        <tbody>
          @for (l of lines(); track l.lineId; let i = $index) {
            <tr>
              <td class="py-1 pr-2"><input hlmInput type="date" [value]="l.date" (change)="patch(i, { date: $any($event.target).value })" /></td>
              <td class="pr-2"><input hlmInput type="text" [value]="l.description" (input)="patch(i, { description: $any($event.target).value })" /></td>
              <td class="pr-2"><input hlmInput type="number" class="text-right tabular-nums" [value]="l.amount" (input)="patch(i, { amount: +$any($event.target).value })" /></td>
              <td class="pr-2"><input hlmInput type="text" [value]="l.externalRef ?? ''" (input)="patch(i, { externalRef: $any($event.target).value || null })" /></td>
              <td><button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine(i)" [disabled]="lines().length <= 1">✕</button></td>
            </tr>
          }
        </tbody>
        <tfoot>
          <tr class="border-t border-border">
            <td class="py-1 text-right pr-2" colspan="2">Opening + lines</td>
            <td class="text-right tabular-nums">{{ money(computedClosing()) }}</td>
            <td colspan="2" [class.text-destructive]="!foots()" [class.text-emerald-600]="foots()">{{ foots() ? 'Foots' : 'Off by ' + money(difference()) }}</td>
          </tr>
        </tfoot>
      </table>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-3">
        <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>
        <div class="flex items-center gap-2 ms-auto">
          <button *appCan="'bankrec.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Record statement</button>
          <a hlmBtn variant="outline" routerLink="/cash/statements">Cancel</a>
        </div>
      </div>
    </div>
  `,
})
export class StatementEditor {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly cashAccountId = signal<string | null>(null);
  readonly statementDate = signal(new Date().toISOString().slice(0, 10));
  readonly openingBalance = signal(0);
  readonly closingBalance = signal(0);
  readonly lines = signal<LineModel[]>([emptyLine()]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly cashAccounts = computed(() => this.accounts.accounts().filter(a => a.type === 'Asset' && a.postable));
  readonly lineSum = computed(() => this.lines().reduce((s, l) => s + (l.amount ?? 0), 0));
  readonly computedClosing = computed(() => this.openingBalance() + this.lineSum());
  readonly difference = computed(() => Math.round((this.computedClosing() - this.closingBalance()) * 100) / 100);
  readonly foots = computed(() => this.difference() === 0);
  readonly canSave = computed(() =>
    !!this.cashAccountId() && this.foots() && this.lines().every(l => l.date && l.description));

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  readonly accountItemToString = (id: string): string => this.accounts.label(id);
  patch(i: number, part: Partial<BankStatementLine>): void { this.lines.update(v => v.map((l, idx) => idx === i ? { ...l, ...part } : l)); }
  setLine(i: number, line: BankStatementLine): void { this.lines.update(v => v.map((l, idx) => idx === i ? { ...l, ...line } : l)); }
  addLine(): void { this.lines.update(v => [...v, emptyLine()]); }
  removeLine(i: number): void { this.lines.update(v => v.filter((_, idx) => idx !== i)); }
  money(n: number): string { return fmtMoney(n); }

  private toRequest(): RecordBankStatementRequest {
    return { cashAccountId: this.cashAccountId()!, statementDate: this.statementDate(),
      openingBalance: this.openingBalance(), closingBalance: this.closingBalance(),
      lines: this.lines().map(({ lineId, ...l }) => l) };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordStatement(this.toRequest()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (s) => { this.busy.set(false); void this.router.navigate(['/cash/statements', s.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- statement-editor`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/banking/statement-editor.ts UI/Angular/src/app/features/banking/statement-editor.spec.ts
git commit -m "feat(banking-ui): manual bank statement editor with live foot check"
```

---

### Task 10: Statement detail + start reconciliation

**Files:**
- Create: `UI/Angular/src/app/features/banking/statement-detail.ts`
- Test: `UI/Angular/src/app/features/banking/statement-detail.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add `statements`, `statements/new`, `statements/:id` child routes; `statements/import` added in BK-3)

**Interfaces:**
- Consumes: `BankingService.getStatement`, `BankingService.startReconciliation` (added in BK-4 — for BK-2 the "Start reconciliation" button is present but calls a method that BK-4 adds; to keep BK-2 self-contained, wire the button to navigate to `/cash/reconciliation` with the statement id as a query param, and let BK-4's ReconciliationList consume it). `BankStatement`, `money`/`displayDate`.

> To keep BK-2 shippable without BK-4, the "Start reconciliation" action navigates to `/cash/reconciliation?statement=<id>`; BK-4's list reads that param and offers the start. Until BK-4 lands, `/cash/reconciliation` is the placeholder — acceptable mid-epic.

- [ ] **Step 1: Write the failing test**

```typescript
// statement-detail.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StatementDetail } from './statement-detail';
import { ClientContextService } from '../../core/client/client-context.service';

describe('StatementDetail', () => {
  it('loads a statement and lists its lines', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'b1']]) } } }],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(StatementDetail);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/b1')).flush(
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31', openingBalance: 0,
        closingBalance: 100, lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: 'X1' }], status: 'Posted' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('BST-00001');
    expect(fixture.nativeElement.textContent).toContain('dep');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- statement-detail`
Expected: FAIL — cannot find module.

- [ ] **Step 3: Write the component**

```typescript
// statement-detail.ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { BankingService } from '../../core/banking/banking.service';
import { BankStatement } from '../../core/banking/banking';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-statement-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/cash/statements" class="text-sm text-muted-foreground hover:text-foreground">← Statements</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (statement(); as s) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Statement {{ s.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="s.status === 'Void'">{{ s.status }}</span>
          @if (s.status === 'Posted') {
            <button *appCan="'bankrec.write'" hlmBtn size="sm" class="ms-auto" (click)="startReconciliation(s.id)">Start reconciliation</button>
          }
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Statement date</td><td class="text-right">{{ date(s.statementDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Opening balance</td><td class="text-right tabular-nums">{{ money(s.openingBalance) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Closing balance</td><td class="text-right tabular-nums">{{ money(s.closingBalance) }}</td></tr>
          </tbody>
        </table>

        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>Date</th><th hlmTh>Description</th><th hlmTh class="text-right">Amount</th><th hlmTh>Ref</th></tr></thead>
            <tbody hlmTBody>
              @for (l of s.lines; track $index) {
                <tr hlmTr><td hlmTd>{{ date(l.date) }}</td><td hlmTd>{{ l.description }}</td>
                  <td hlmTd class="text-right tabular-nums" [class.text-destructive]="l.amount < 0">{{ money(l.amount) }}</td>
                  <td hlmTd>{{ l.externalRef ?? '' }}</td></tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
})
export class StatementDetail {
  private readonly svc = inject(BankingService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly statement = signal<BankStatement | null>(null);
  readonly message = signal<string | null>(null);

  constructor() {
    this.svc.getStatement(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (s) => this.statement.set(s),
      error: (e) => this.message.set(extractProblem(e).detail),
    });
  }

  // BK-2: navigate to the reconcile tab with the statement id; BK-4 turns this into a real start.
  startReconciliation(statementId: string): void {
    void this.router.navigate(['/cash/reconciliation'], { queryParams: { statement: statementId } });
  }

  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- statement-detail`
Expected: PASS.

- [ ] **Step 5: Wire statement routes**

In `app.routes.ts`, add to the `/cash` children (after the cash routes):

```typescript
    { path: 'statements', component: StatementList },
    { path: 'statements/new', component: StatementEditor, canActivate: [canWrite],
      data: { requiredCapability: 'bankrec.write', fallback: '/cash/statements' } },
    { path: 'statements/:id', component: StatementDetail },
```

Add the imports:

```typescript
import { StatementList } from './features/banking/statement-list';
import { StatementEditor } from './features/banking/statement-editor';
import { StatementDetail } from './features/banking/statement-detail';
```

- [ ] **Step 6: Build + commit**

Run: `cd UI/Angular && npm run build`
Expected: build clean.

```bash
git add UI/Angular/src/app/features/banking/statement-detail.ts UI/Angular/src/app/features/banking/statement-detail.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(banking-ui): bank statement detail + statement routes"
```

**BK-2 done:** manual statements list/create/detail usable.

---

## Slice BK-3 — Statements (file import)

Delivers: import CSV/OFX bank exports through the parse-to-preview flow — upload, pick format, (for CSV) build the column mapping, review the preview, fill any gaps, and confirm each statement to `POST /bank-statements`.

### Task 11: Banking service — import endpoint

**Files:**
- Modify: `UI/Angular/src/app/core/banking/banking.service.ts`
- Modify: `UI/Angular/src/app/core/banking/banking.service.spec.ts`

**Interfaces:**
- Produces: `importStatements(file: File, format: InterchangeFormat, mapping: CsvMapping | null): Observable<ImportPreviewResponse>`. Sends `multipart/form-data` with fields `file`, `format`, and (CSV only) `mapping` as a JSON string. The endpoint has `DisableAntiforgery`.

- [ ] **Step 1: Add the failing test**

```typescript
// append to banking.service.spec.ts
describe('BankingService — import', () => {
  it('posts multipart with file, format and mapping JSON', () => {
    const { svc, ctrl } = setup();
    const file = new File(['date,amount,desc\n2026-03-05,100,dep'], 'bank.csv', { type: 'text/csv' });
    const mapping = { date: { index: 0 }, amount: { index: 1 }, description: { index: 2 }, hasHeader: true } as unknown as import('./banking').CsvMapping;
    let res: { statements?: unknown[] } = {};
    svc.importStatements(file, 'Csv', mapping).subscribe(r => (res = r));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements/import');
    expect(req.request.method).toBe('POST');
    const body = req.request.body as FormData;
    expect(body.get('format')).toBe('Csv');
    expect(body.get('file')).toBeInstanceOf(File);
    expect(JSON.parse(body.get('mapping') as string).hasHeader).toBe(true);
    req.flush({ statements: [{ lines: [], detectedOpeningBalance: null, detectedClosingBalance: null, statementDate: null, accountHint: null }], warnings: [] });
    expect(res.statements!.length).toBe(1);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: FAIL — `importStatements` not a function.

- [ ] **Step 3: Add the method**

Add imports to `banking.service.ts`:

```typescript
import {
  // ...existing...
  InterchangeFormat, CsvMapping, ImportPreviewResponse,
} from './banking';
```

Add to the class:

```typescript
  // ── Import (parse-to-preview) ────────────────────────────────────────────────
  importStatements(file: File, format: InterchangeFormat, mapping: CsvMapping | null): Observable<ImportPreviewResponse> {
    if (!this.client.clientId()) return EMPTY;
    const body = new FormData();
    body.append('file', file, file.name);
    body.append('format', format);
    if (mapping) body.append('mapping', JSON.stringify(mapping));
    return this.http.post<ImportPreviewResponse>(this.base('/bank-statements/import'), body);
  }
```

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/banking/banking.service.ts UI/Angular/src/app/core/banking/banking.service.spec.ts
git commit -m "feat(banking-ui): banking service — statement import (multipart)"
```

---

### Task 12: Statement import screen (upload → mapping → preview → confirm)

**Files:**
- Create: `UI/Angular/src/app/features/banking/statement-import.ts`
- Test: `UI/Angular/src/app/features/banking/statement-import.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add `statements/import`)

**Interfaces:**
- Consumes: `BankingService.importStatements` + `recordStatement`, `AccountsService`, `CsvMapping`, `ColumnRef`, `StatementPreview`, `ImportPreviewResponse`, `RecordBankStatementRequest`, `money`, `extractProblem`.
- Behavior: three visual stages driven by a `stage` signal — **(1) Upload**: cash-account select, file picker, format select (CSV/OFX); for CSV a **mapping builder** (has-header toggle, and for each field Date/Amount/Debit/Credit/Description/Reference a column index input, plus optional dateFormat + delimiter). **(2) Preview**: render each returned `StatementPreview` as a card with its lines and detected balances; the user fills missing opening/closing/date and confirms. **(3) Confirm**: each card's "Record" POSTs to `/bank-statements` and links to the created statement.

> The `CsvMapping` shape is authoritative — it comes from `Accounting101.Interchange/CsvMapping.cs`: `Date`, `Amount?`, `Debit?`, `Credit?`, `Description`, `Reference?`, `DateFormat?`, `HasHeader`, `Delimiter?`, `Status?`, `ExcludeStatuses?`. Amount is EITHER a single signed column OR a Debit+Credit pair. This screen exposes single-signed-amount + optional debit/credit columns; Status/ExcludeStatuses are out of scope for the first pass (defaulted to null). `ColumnRef` is `{index?, header?}` — this screen uses index-based columns.

- [ ] **Step 1: Write the failing test**

```typescript
// statement-import.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StatementImport } from './statement-import';
import { ClientContextService } from '../../core/client/client-context.service';

function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(StatementImport);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
    [{ id: 'CA1', number: '1000', name: 'Cash', type: 'Asset', postable: true }]);   // bare array — accounts endpoint is not paged
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('StatementImport', () => {
  it('uploads a CSV with a built mapping and moves to preview', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.file.set(new File(['x'], 'bank.csv', { type: 'text/csv' }));
    cmp.format.set('Csv');
    cmp.setColumn('date', 0); cmp.setColumn('amount', 1); cmp.setColumn('description', 2);
    cmp.upload();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements/import');
    const mapping = JSON.parse((req.request.body as FormData).get('mapping') as string);
    expect(mapping.date.index).toBe(0);
    req.flush({ statements: [{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
      detectedOpeningBalance: 0, detectedClosingBalance: 100, statementDate: '2026-03-31', accountHint: null }], warnings: [] });
    fixture.detectChanges();
    expect(cmp.stage()).toBe('preview');
    expect(cmp.previews().length).toBe(1);
    ctrl.verify();
  });

  it('confirms a preview by posting a bank statement', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
      openingBalance: 0, closingBalance: 100, statementDate: '2026-03-31' }]);
    cmp.stage.set('preview');
    cmp.confirm(0);
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements');
    expect(req.request.body.closingBalance).toBe(100);
    req.flush({ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' });
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- statement-import`
Expected: FAIL — cannot find module.

- [ ] **Step 3: Write the component**

```typescript
// statement-import.ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import {
  InterchangeFormat, CsvMapping, ColumnRef, StatementPreview, BankStatementLine, RecordBankStatementRequest,
} from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

type MappableField = 'date' | 'amount' | 'debit' | 'credit' | 'description' | 'reference';
/** An editable preview: server lines + the user-supplied balances/date that the preview may not carry. */
interface EditablePreview { lines: BankStatementLine[]; openingBalance: number; closingBalance: number; statementDate: string; createdId?: string; error?: string; }
type Stage = 'upload' | 'preview';

@Component({
  selector: 'app-statement-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/cash/statements" class="text-sm text-muted-foreground hover:text-foreground">← Statements</a>
      <h1 class="text-2xl font-bold">Import bank statement</h1>

      @if (stage() === 'upload') {
        <div class="grid grid-cols-2 gap-4">
          <div class="flex flex-col gap-1">
            <label hlmLabel>Cash account</label>
            <div hlmSelect [value]="cashAccountId() ?? ''" [itemToString]="accountItemToString" (valueChange)="cashAccountId.set($any($event))" class="w-full">
              <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a cash account" /></hlm-select-trigger>
              <hlm-select-content *hlmSelectPortal>
                @for (a of cashAccounts(); track a.id) { <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item> }
              </hlm-select-content>
            </div>
          </div>
          <div class="flex flex-col gap-1">
            <label hlmLabel>Format</label>
            <div hlmSelect [value]="format()" (valueChange)="format.set($any($event))" class="w-full">
              <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
              <hlm-select-content *hlmSelectPortal>
                <hlm-select-item value="Csv">CSV</hlm-select-item>
                <hlm-select-item value="Ofx">OFX</hlm-select-item>
              </hlm-select-content>
            </div>
          </div>
          <div class="flex flex-col gap-1 col-span-2">
            <label hlmLabel>File</label>
            <input hlmInput type="file" (change)="onFile($event)" />
          </div>
        </div>

        @if (format() === 'Csv') {
          <div class="flex flex-col gap-3 border border-border rounded p-3">
            <div class="flex items-center gap-2">
              <input type="checkbox" [checked]="hasHeader()" (change)="hasHeader.set($any($event.target).checked)" id="hdr" />
              <label for="hdr" class="text-sm">File has a header row</label>
            </div>
            <p class="text-xs text-muted-foreground">Enter the zero-based column index for each field. Use Amount for a single signed column, or Debit + Credit for a two-column layout.</p>
            <div class="grid grid-cols-3 gap-3">
              @for (f of fields; track f.key) {
                <div class="flex flex-col gap-1">
                  <label hlmLabel>{{ f.label }}{{ f.required ? ' *' : '' }}</label>
                  <input hlmInput type="number" min="0" [value]="columnIndex(f.key) ?? ''"
                         (input)="setColumn(f.key, $any($event.target).value === '' ? null : +$any($event.target).value)" />
                </div>
              }
              <div class="flex flex-col gap-1">
                <label hlmLabel>Date format (optional)</label>
                <input hlmInput type="text" placeholder="yyyy-MM-dd" [value]="dateFormat() ?? ''" (input)="dateFormat.set($any($event.target).value || null)" />
              </div>
              <div class="flex flex-col gap-1">
                <label hlmLabel>Delimiter (optional)</label>
                <input hlmInput type="text" maxlength="1" placeholder="," [value]="delimiter() ?? ''" (input)="delimiter.set($any($event.target).value || null)" />
              </div>
            </div>
          </div>
        }

        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
        <div class="flex items-center gap-2">
          <button *appCan="'bankrec.write'" hlmBtn type="button" (click)="upload()" [disabled]="!canUpload() || busy()">Upload &amp; preview</button>
        </div>
      }

      @if (stage() === 'preview') {
        @if (warnings().length) {
          <div class="border border-amber-400 rounded p-3 text-sm">
            <p class="font-semibold">Warnings</p>
            <ul class="list-disc ps-5">@for (w of warnings(); track $index) { <li>{{ w }}</li> }</ul>
          </div>
        }
        @for (p of previews(); track $index; let i = $index) {
          <div class="border border-border rounded p-3 flex flex-col gap-3">
            <div class="flex items-center gap-3">
              <h2 class="font-semibold">Statement {{ i + 1 }} — {{ p.lines.length }} lines</h2>
              @if (p.createdId) { <a [routerLink]="['/cash/statements', p.createdId]" class="text-primary hover:underline text-sm ms-auto">Recorded →</a> }
            </div>
            <div class="grid grid-cols-3 gap-3">
              <div class="flex flex-col gap-1"><label hlmLabel>Statement date</label>
                <input hlmInput type="date" [value]="p.statementDate" (change)="patchPreview(i, { statementDate: $any($event.target).value })" [disabled]="!!p.createdId" /></div>
              <div class="flex flex-col gap-1"><label hlmLabel>Opening</label>
                <input hlmInput type="number" class="text-right tabular-nums" [value]="p.openingBalance" (input)="patchPreview(i, { openingBalance: +$any($event.target).value })" [disabled]="!!p.createdId" /></div>
              <div class="flex flex-col gap-1"><label hlmLabel>Closing</label>
                <input hlmInput type="number" class="text-right tabular-nums" [value]="p.closingBalance" (input)="patchPreview(i, { closingBalance: +$any($event.target).value })" [disabled]="!!p.createdId" /></div>
            </div>
            <table class="w-full text-sm">
              <thead><tr class="text-left text-muted-foreground"><th class="py-1">Date</th><th>Description</th><th class="text-right">Amount</th></tr></thead>
              <tbody>@for (l of p.lines; track $index) {
                <tr><td class="py-1">{{ l.date }}</td><td>{{ l.description }}</td><td class="text-right tabular-nums">{{ money(l.amount) }}</td></tr> }</tbody>
            </table>
            <p class="text-xs" [class.text-destructive]="!foots(p)" [class.text-emerald-600]="foots(p)">{{ foots(p) ? 'Foots' : 'Does not foot — adjust balances' }}</p>
            @if (p.error) { <p class="text-destructive text-sm">{{ p.error }}</p> }
            @if (!p.createdId) {
              <button *appCan="'bankrec.write'" hlmBtn size="sm" type="button" (click)="confirm(i)" [disabled]="!foots(p) || busy()">Record statement</button>
            }
          </div>
        }
        <button hlmBtn variant="outline" type="button" (click)="reset()">Import another</button>
      }
    </div>
  `,
})
export class StatementImport {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly fields: { key: MappableField; label: string; required: boolean }[] = [
    { key: 'date', label: 'Date', required: true }, { key: 'amount', label: 'Amount', required: false },
    { key: 'debit', label: 'Debit', required: false }, { key: 'credit', label: 'Credit', required: false },
    { key: 'description', label: 'Description', required: true }, { key: 'reference', label: 'Reference', required: false },
  ];

  readonly stage = signal<Stage>('upload');
  readonly cashAccountId = signal<string | null>(null);
  readonly file = signal<File | null>(null);
  readonly format = signal<InterchangeFormat>('Csv');
  readonly hasHeader = signal(true);
  readonly dateFormat = signal<string | null>(null);
  readonly delimiter = signal<string | null>(null);
  private readonly columns = signal<Partial<Record<MappableField, number | null>>>({});
  readonly previews = signal<EditablePreview[]>([]);
  readonly warnings = signal<string[]>([]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly cashAccounts = computed(() => this.accounts.accounts().filter(a => a.type === 'Asset' && a.postable));
  readonly canUpload = computed(() => {
    if (!this.cashAccountId() || !this.file()) return false;
    if (this.format() === 'Ofx') return true;
    const c = this.columns();
    const hasAmount = c.amount != null || (c.debit != null && c.credit != null);
    return c.date != null && c.description != null && hasAmount;
  });

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  readonly accountItemToString = (id: string): string => this.accounts.label(id);
  onFile(e: Event): void { this.file.set(($any(e.target) as HTMLInputElement).files?.[0] ?? null); }
  columnIndex(f: MappableField): number | null { return this.columns()[f] ?? null; }
  setColumn(f: MappableField, index: number | null): void { this.columns.update(c => ({ ...c, [f]: index })); }
  money(n: number): string { return fmtMoney(n); }
  foots(p: EditablePreview): boolean {
    const sum = p.lines.reduce((s, l) => s + l.amount, 0);
    return Math.round((p.openingBalance + sum - p.closingBalance) * 100) === 0;
  }
  patchPreview(i: number, part: Partial<EditablePreview>): void { this.previews.update(v => v.map((p, idx) => idx === i ? { ...p, ...part } : p)); }
  reset(): void { this.stage.set('upload'); this.previews.set([]); this.warnings.set([]); this.file.set(null); this.message.set(null); }

  private buildMapping(): CsvMapping | null {
    if (this.format() !== 'Csv') return null;
    const c = this.columns();
    const ref = (n?: number | null): ColumnRef | null => (n == null ? null : { index: n });
    return {
      date: ref(c.date)!, amount: ref(c.amount), debit: ref(c.debit), credit: ref(c.credit),
      description: ref(c.description)!, reference: ref(c.reference),
      dateFormat: this.dateFormat(), hasHeader: this.hasHeader(),
      delimiter: this.delimiter(), status: null, excludeStatuses: null,
    };
  }

  upload(): void {
    if (!this.canUpload()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.importStatements(this.file()!, this.format(), this.buildMapping())
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (res) => {
          this.previews.set(res.statements.map((s: StatementPreview) => ({
            lines: s.lines,
            openingBalance: s.detectedOpeningBalance ?? 0,
            closingBalance: s.detectedClosingBalance ?? 0,
            statementDate: s.statementDate ?? new Date().toISOString().slice(0, 10),
          })));
          this.warnings.set(res.warnings);
          this.stage.set('preview'); this.busy.set(false);
        },
        error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
      });
  }

  confirm(i: number): void {
    const p = this.previews()[i];
    if (!this.foots(p)) return;
    this.busy.set(true); this.patchPreview(i, { error: undefined });
    const req: RecordBankStatementRequest = {
      cashAccountId: this.cashAccountId()!, statementDate: p.statementDate,
      openingBalance: p.openingBalance, closingBalance: p.closingBalance, lines: p.lines,
    };
    this.svc.recordStatement(req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (s) => { this.patchPreview(i, { createdId: s.id }); this.busy.set(false); },
      error: (e) => { this.patchPreview(i, { error: extractProblem(e).detail }); this.busy.set(false); },
    });
  }
}
```

> `$any(e.target)` in `onFile` requires the `$any` cast helper; in a `.ts` method use a plain cast: `(e.target as HTMLInputElement)`. Adjust the template-vs-class casts accordingly (the template uses `$any(...)`, the class method uses `as`).

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- statement-import`
Expected: PASS (2 tests).

- [ ] **Step 5: Wire the import route**

In `app.routes.ts`, add to the `/cash` children (before `statements/:id`):

```typescript
    { path: 'statements/import', component: StatementImport, canActivate: [canWrite],
      data: { requiredCapability: 'bankrec.write', fallback: '/cash/statements' } },
```

Add the import:

```typescript
import { StatementImport } from './features/banking/statement-import';
```

> Route order: `statements/import` and `statements/new` must precede `statements/:id` so they are not captured as an `:id`. Place them accordingly.

- [ ] **Step 6: Build + commit**

Run: `cd UI/Angular && npm run build`
Expected: build clean.

```bash
git add UI/Angular/src/app/features/banking/statement-import.ts UI/Angular/src/app/features/banking/statement-import.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(banking-ui): statement import — upload, CSV mapping builder, preview, confirm"
```

**BK-3 done:** file import produces recorded statements.

---

## Slice BK-4 — Reconciliation worksheet

Delivers: the Reconcile tab — a reconciliation list that starts a reconciliation from a posted statement, and the worksheet (clear/unclear, auto-match proposal→apply, running summary, complete).

### Task 13: Banking service — reconciliation endpoints

**Files:**
- Modify: `UI/Angular/src/app/core/banking/banking.service.ts`
- Modify: `UI/Angular/src/app/core/banking/banking.service.spec.ts`

**Interfaces:**
- Produces: `startReconciliation(bankStatementId): Observable<ReconciliationRef>`, `getWorksheet(id): Observable<ReconciliationWorksheet>`, `clear(id, entryIds): Observable<ReconciliationWorksheet>`, `unclear(id, entryIds): Observable<ReconciliationWorksheet>`, `autoMatchProposal(id): Observable<AutoMatchProposal>`, `autoMatchApply(id): Observable<ReconciliationWorksheet>`, `completeReconciliation(id): Observable<ReconciliationWorksheet>`. There is no list endpoint for reconciliations on the backend — the list is built from statements the user starts (see Task 14 note).

- [ ] **Step 1: Add failing tests**

```typescript
// append to banking.service.spec.ts
describe('BankingService — reconciliation', () => {
  const worksheet = { reconciliation: { id: 'r1', number: 'REC-00001', cashAccountId: 'CA1', bankStatementId: 'b1',
      statementDate: '2026-03-31', status: 'InProgress', clearedEntryIds: [] },
    statement: { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' },
    entries: [{ entryId: 'e1', date: '2026-03-05', reference: null, sourceType: 'Cash', cashEffect: 100, cleared: false }],
    bookBalance: 100, clearedTotal: 0, reconciledDifference: 100, balanced: false };

  it('startReconciliation posts the statement id', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.startReconciliation('b1').subscribe(r => (got = r));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations');
    expect(req.request.body).toEqual({ bankStatementId: 'b1' });
    req.flush(worksheet.reconciliation);
    expect(got.id).toBe('r1');
    ctrl.verify();
  });

  it('clear posts entry ids and returns the worksheet', () => {
    const { svc, ctrl } = setup();
    let w: { balanced?: boolean } = {};
    svc.clear('r1', ['e1']).subscribe(x => (w = x));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations/r1/clear');
    expect(req.request.body).toEqual({ entryIds: ['e1'] });
    req.flush({ ...worksheet, clearedTotal: 100, reconciledDifference: 0, balanced: true });
    expect(w.balanced).toBe(true);
    ctrl.verify();
  });

  it('autoMatchApply hits the apply=true query', () => {
    const { svc, ctrl } = setup();
    svc.autoMatchApply('r1').subscribe();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/reconciliations/r1/auto-match');
    expect(req.request.params.get('apply')).toBe('true');
    req.flush(worksheet);
    ctrl.verify();
  });

  it('completeReconciliation posts to /complete', () => {
    const { svc, ctrl } = setup();
    svc.completeReconciliation('r1').subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations/r1/complete');
    expect(req.request.method).toBe('POST');
    req.flush({ ...worksheet, reconciliation: { ...worksheet.reconciliation, status: 'Completed' } });
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: FAIL — methods not functions.

- [ ] **Step 3: Add the methods**

Add imports to `banking.service.ts`:

```typescript
import {
  // ...existing...
  ReconciliationRef, ReconciliationWorksheet, AutoMatchProposal,
} from './banking';
```

Add to the class:

```typescript
  // ── Reconciliation ───────────────────────────────────────────────────────────
  startReconciliation(bankStatementId: string): Observable<ReconciliationRef> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationRef>(this.base('/reconciliations'), { bankStatementId });
  }
  getWorksheet(id: string): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<ReconciliationWorksheet>(this.base(`/reconciliations/${id}`));
  }
  clear(id: string, entryIds: string[]): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationWorksheet>(this.base(`/reconciliations/${id}/clear`), { entryIds });
  }
  unclear(id: string, entryIds: string[]): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationWorksheet>(this.base(`/reconciliations/${id}/unclear`), { entryIds });
  }
  autoMatchProposal(id: string): Observable<AutoMatchProposal> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<AutoMatchProposal>(this.base(`/reconciliations/${id}/auto-match`), {}, { params: new HttpParams().set('apply', false) });
  }
  autoMatchApply(id: string): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationWorksheet>(this.base(`/reconciliations/${id}/auto-match`), {}, { params: new HttpParams().set('apply', true) });
  }
  completeReconciliation(id: string): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationWorksheet>(this.base(`/reconciliations/${id}/complete`), {});
  }
```

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/banking/banking.service.ts UI/Angular/src/app/core/banking/banking.service.spec.ts
git commit -m "feat(banking-ui): banking service — reconciliation worksheet endpoints"
```

---

### Task 14: Reconciliation list / start

**Files:**
- Create: `UI/Angular/src/app/features/banking/reconciliation-list.ts`
- Test: `UI/Angular/src/app/features/banking/reconciliation-list.spec.ts`

**Interfaces:**
- Consumes: `BankingService.listStatements` + `startReconciliation`, `AccountsService`, `ActivatedRoute` (reads `?statement=<id>` from BK-2's detail hand-off), `BankStatement`, `money`/`displayDate`.
- Behavior: there is no backend "list reconciliations" endpoint. The Reconcile tab is a **launcher**: pick a cash account → show its posted statements → "Reconcile" a statement calls `startReconciliation` and navigates to the worksheet. If arriving with `?statement=<id>`, offer to start it directly. (A future backend list endpoint could replace this; note the limitation in the commit.)

- [ ] **Step 1: Write the failing test**

```typescript
// reconciliation-list.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReconciliationList } from './reconciliation-list';
import { ClientContextService } from '../../core/client/client-context.service';

describe('ReconciliationList', () => {
  it('starts a reconciliation from a chosen statement', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map() } } }],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(ReconciliationList);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
      [{ id: 'CA1', number: '1000', name: 'Cash', type: 'Asset', postable: true }]);   // bare array — accounts endpoint is not paged
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    fixture.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/bank-statements')).flush({ items: [
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }], total: 1, skip: 0, limit: 50 });
    fixture.detectChanges();
    cmp.start('b1');
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations');
    expect(req.request.body).toEqual({ bankStatementId: 'b1' });
    req.flush({ id: 'r1', number: 'REC-00001', cashAccountId: 'CA1', bankStatementId: 'b1',
      statementDate: '2026-03-31', status: 'InProgress', clearedEntryIds: [] });
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- reconciliation-list`
Expected: FAIL — cannot find module.

- [ ] **Step 3: Write the component**

```typescript
// reconciliation-list.ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { BankStatement } from '../../core/banking/banking';
import { PagedResponse } from '../../core/api/paged-response';
import { AccountsService } from '../../core/accounts/accounts.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-reconciliation-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective, ...HlmSelectImports, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <h1 class="text-2xl font-bold">Reconcile</h1>
      <p class="text-sm text-muted-foreground">Pick a cash account, then start a reconciliation from one of its posted statements.</p>

      <div class="flex items-center gap-2 max-w-md">
        <label class="text-sm text-muted-foreground">Cash account</label>
        <div hlmSelect [value]="cashAccountId() ?? ''" [itemToString]="accountItemToString" (valueChange)="cashAccountId.set($any($event))" class="w-full">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a cash account" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            @for (a of cashAccounts(); track a.id) { <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item> }
          </hlm-select-content>
        </div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (!cashAccountId()) {
        <p class="text-muted-foreground text-sm">Select a cash account to see its statements.</p>
      } @else if (statements().length === 0) {
        <p class="text-muted-foreground text-sm">No posted statements for this account.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>Statement</th><th hlmTh>Date</th>
              <th hlmTh class="text-right">Closing</th><th hlmTh></th></tr></thead>
            <tbody hlmTBody>
              @for (s of statements(); track s.id) {
                <tr hlmTr>
                  <td hlmTd>{{ s.number ?? '—' }}</td>
                  <td hlmTd>{{ date(s.statementDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(s.closingBalance) }}</td>
                  <td hlmTd class="text-right">
                    <button *appCan="'bankrec.write'" hlmBtn size="sm" (click)="start(s.id)" [disabled]="busy()">Reconcile</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
})
export class ReconciliationList {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly client = inject(ClientContextService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly cashAccountId = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly cashAccounts = computed(() => this.accounts.accounts().filter(a => a.type === 'Asset' && a.postable));

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  private readonly query = computed(() => ({ id: this.client.clientId(), account: this.cashAccountId() }));
  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.message.set(null)),
      switchMap(({ id, account }) => {
        if (!id || !account) return of(null);
        return this.svc.listStatements(account, { skip: 0, limit: 50 }).pipe(catchError(() => of(null)));
      }),
    ),
    { initialValue: null as PagedResponse<BankStatement> | null },
  );

  readonly statements = computed(() => (this.page()?.items ?? []).filter(s => s.status === 'Posted'));
  readonly accountItemToString = (id: string): string => this.accounts.label(id);

  start(statementId: string): void {
    this.busy.set(true); this.message.set(null);
    this.svc.startReconciliation(statementId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (r) => { this.busy.set(false); void this.router.navigate(['/cash/reconciliation', r.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- reconciliation-list`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/banking/reconciliation-list.ts UI/Angular/src/app/features/banking/reconciliation-list.spec.ts
git commit -m "feat(banking-ui): reconciliation launcher (start from a statement)"
```

---

### Task 15: Reconciliation worksheet (clear/unclear/auto-match/complete)

**Files:**
- Create: `UI/Angular/src/app/features/banking/reconciliation-worksheet.ts`
- Test: `UI/Angular/src/app/features/banking/reconciliation-worksheet.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add `reconciliation`, `reconciliation/:id`)

**Interfaces:**
- Consumes: `BankingService.getWorksheet`/`clear`/`unclear`/`autoMatchProposal`/`autoMatchApply`/`completeReconciliation`, `ReconciliationWorksheet`, `WorksheetEntry`, `AutoMatchProposal`, `money`/`displayDate`, `extractProblem`.
- Behavior: load the worksheet by id. Entry grid with a per-row cleared checkbox → `clear([id])`/`unclear([id])`. A summary panel shows book balance, cleared total, reconciled difference, and a Balanced badge. **Auto-match** is two steps: "Auto-match" fetches the read-only proposal and shows it (matched pairs, unmatched lines, unmatched entries); an **Apply** button then calls `autoMatchApply`. **Complete** is enabled only when `balanced`; a `409` (e.g. not balanced) is surfaced. Adjustments (BK-5) mount below the summary.

- [ ] **Step 1: Write the failing test**

```typescript
// reconciliation-worksheet.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReconciliationWorksheet } from './reconciliation-worksheet';
import { ClientContextService } from '../../core/client/client-context.service';

const worksheet = (over: Record<string, unknown> = {}) => ({
  reconciliation: { id: 'r1', number: 'REC-00001', cashAccountId: 'CA1', bankStatementId: 'b1',
    statementDate: '2026-03-31', status: 'InProgress', clearedEntryIds: [] },
  statement: { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
    openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' },
  entries: [{ entryId: 'e1', date: '2026-03-05', reference: null, sourceType: 'Cash', cashEffect: 100, cleared: false }],
  bookBalance: 100, clearedTotal: 0, reconciledDifference: 100, balanced: false, ...over });

function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'r1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(ReconciliationWorksheet);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/reconciliations/r1')).flush(worksheet());
  ctrl.expectOne(r => r.url.endsWith('/reconciliations/r1/adjustments')).flush({ items: [], total: 0, skip: 0, limit: 50 });
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('ReconciliationWorksheet', () => {
  it('clears an entry and reflects the balanced verdict', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.toggle({ entryId: 'e1', date: '2026-03-05', reference: null, sourceType: 'Cash', cashEffect: 100, cleared: false });
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations/r1/clear');
    expect(req.request.body).toEqual({ entryIds: ['e1'] });
    req.flush(worksheet({ clearedTotal: 100, reconciledDifference: 0, balanced: true,
      entries: [{ entryId: 'e1', date: '2026-03-05', reference: null, sourceType: 'Cash', cashEffect: 100, cleared: true }] }));
    fixture.detectChanges();
    expect(cmp.worksheet()!.balanced).toBe(true);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- reconciliation-worksheet`
Expected: FAIL — cannot find module.

- [ ] **Step 3: Write the component**

```typescript
// reconciliation-worksheet.ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { BankingService } from '../../core/banking/banking.service';
import { ReconciliationWorksheet as Worksheet, WorksheetEntry, AutoMatchProposal } from '../../core/banking/banking';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';
import { AdjustmentsPanel } from './adjustments-panel';   // added in BK-5; see note

@Component({
  selector: 'app-reconciliation-worksheet',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, AdjustmentsPanel, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-4xl">
      <a routerLink="/cash/reconciliation" class="text-sm text-muted-foreground hover:text-foreground">← Reconcile</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (worksheet(); as w) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Reconciliation {{ w.reconciliation.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded">{{ w.reconciliation.status }}</span>
          <a [routerLink]="['/cash/statements', w.statement.id]" class="text-sm text-primary hover:underline ms-auto">Statement {{ w.statement.number }} →</a>
        </div>

        <div class="grid grid-cols-4 gap-3 text-sm">
          <div class="border border-border rounded p-2"><div class="text-muted-foreground">Book balance</div><div class="tabular-nums text-lg">{{ money(w.bookBalance) }}</div></div>
          <div class="border border-border rounded p-2"><div class="text-muted-foreground">Cleared total</div><div class="tabular-nums text-lg">{{ money(w.clearedTotal) }}</div></div>
          <div class="border border-border rounded p-2"><div class="text-muted-foreground">Statement closing</div><div class="tabular-nums text-lg">{{ money(w.statement.closingBalance) }}</div></div>
          <div class="border border-border rounded p-2" [class.border-emerald-500]="w.balanced" [class.border-destructive]="!w.balanced">
            <div class="text-muted-foreground">Difference</div>
            <div class="tabular-nums text-lg" [class.text-emerald-600]="w.balanced" [class.text-destructive]="!w.balanced">{{ money(w.reconciledDifference) }}</div>
          </div>
        </div>

        @if (w.reconciliation.status === 'InProgress') {
          <div class="flex items-center gap-2">
            <button *appCan="'bankrec.write'" hlmBtn size="sm" variant="outline" (click)="loadProposal()" [disabled]="busy()">Auto-match</button>
            <button *appCan="'bankrec.write'" hlmBtn size="sm" (click)="complete()" [disabled]="!w.balanced || busy()">Complete</button>
          </div>
        }

        @if (proposal(); as p) {
          <div class="border border-border rounded p-3 flex flex-col gap-2 text-sm">
            <div class="flex items-center gap-3">
              <span class="font-semibold">Auto-match proposal</span>
              <span class="text-muted-foreground">{{ p.matchedEntryIds.length }} entr(ies) match · {{ p.unmatchedLines.length }} statement line(s) unmatched</span>
              <div class="ms-auto flex gap-2">
                <button *appCan="'bankrec.write'" hlmBtn size="sm" (click)="applyProposal()" [disabled]="p.matchedEntryIds.length === 0 || busy()">Apply</button>
                <button hlmBtn size="sm" variant="ghost" (click)="proposal.set(null)">Dismiss</button>
              </div>
            </div>
          </div>
        }

        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>Cleared</th><th hlmTh>Date</th><th hlmTh>Reference</th>
              <th hlmTh>Source</th><th hlmTh class="text-right">Cash effect</th></tr></thead>
            <tbody hlmTBody>
              @for (e of w.entries; track e.entryId) {
                <tr hlmTr>
                  <td hlmTd><input type="checkbox" [checked]="e.cleared" [disabled]="w.reconciliation.status !== 'InProgress' || busy()" (change)="toggle(e)" /></td>
                  <td hlmTd>{{ date(e.date) }}</td>
                  <td hlmTd>{{ e.reference ?? '—' }}</td>
                  <td hlmTd>{{ e.sourceType ?? '—' }}</td>
                  <td hlmTd class="text-right tabular-nums" [class.text-destructive]="e.cashEffect < 0">{{ money(e.cashEffect) }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <app-adjustments-panel [reconciliationId]="w.reconciliation.id" [locked]="w.reconciliation.status !== 'InProgress'" (changed)="reload()" />
      }
    </div>
  `,
})
export class ReconciliationWorksheet {
  private readonly svc = inject(BankingService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly worksheet = signal<Worksheet | null>(null);
  readonly proposal = signal<AutoMatchProposal | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getWorksheet(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (w) => { this.worksheet.set(w); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  toggle(e: WorksheetEntry): void {
    this.busy.set(true); this.message.set(null);
    const call = e.cleared ? this.svc.unclear(this.id, [e.entryId]) : this.svc.clear(this.id, [e.entryId]);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (w) => { this.worksheet.set(w); this.busy.set(false); },
      error: (err) => { this.message.set(extractProblem(err).detail); this.busy.set(false); },
    });
  }

  loadProposal(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.autoMatchProposal(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (p) => { this.proposal.set(p); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  applyProposal(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.autoMatchApply(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (w) => { this.worksheet.set(w); this.proposal.set(null); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  complete(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.completeReconciliation(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (w) => { this.worksheet.set(w); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
```

> **BK-4/BK-5 ordering:** `ReconciliationWorksheet` imports `AdjustmentsPanel` (BK-5, Task 17). To keep BK-4 self-contained and green, land Task 15 with the adjustments line **temporarily removed** (drop the `AdjustmentsPanel` import, the `imports` entry, and the `<app-adjustments-panel …/>` element), then re-add all three in BK-5 Task 17. The spec test above does not exercise the panel; the boot helper flushes the `/adjustments` GET only because Task 17 adds that call — in BK-4-only form, remove that `expectOne('/adjustments')` line from the test too, and restore it in Task 17. Whichever order the executor prefers, the worksheet's own behavior (clear/auto-match/complete) is fully tested here.

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- reconciliation-worksheet`
Expected: PASS.

- [ ] **Step 5: Wire reconciliation routes**

In `app.routes.ts`, add to the `/cash` children:

```typescript
    { path: 'reconciliation', component: ReconciliationList },
    { path: 'reconciliation/:id', component: ReconciliationWorksheet },
```

Add imports:

```typescript
import { ReconciliationList } from './features/banking/reconciliation-list';
import { ReconciliationWorksheet } from './features/banking/reconciliation-worksheet';
```

- [ ] **Step 6: Build + commit**

Run: `cd UI/Angular && npm run build`
Expected: build clean.

```bash
git add UI/Angular/src/app/features/banking/reconciliation-worksheet.ts UI/Angular/src/app/features/banking/reconciliation-worksheet.spec.ts UI/Angular/src/app/features/banking/reconciliation-list.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(banking-ui): reconciliation worksheet — clear/unclear/auto-match/complete + routes"
```

**BK-4 done:** the full reconciliation loop works (minus adjustments).

---

## Slice BK-5 — Adjustments

Delivers: bank-only adjustments (Charge = fee, Credit = interest) recorded within a reconciliation, listed, and voidable — mounted as a panel in the worksheet.

### Task 16: Banking service — adjustment endpoints

**Files:**
- Modify: `UI/Angular/src/app/core/banking/banking.service.ts`
- Modify: `UI/Angular/src/app/core/banking/banking.service.spec.ts`

**Interfaces:**
- Produces: `listAdjustments(reconciliationId, q): Observable<PagedResponse<BankAdjustment>>`, `recordAdjustment(reconciliationId, req): Observable<BankAdjustment>`, `voidAdjustment(reconciliationId, adjId, reason?): Observable<BankAdjustment>`. Returns bare `BankAdjustment` (no view envelope).

- [ ] **Step 1: Add failing tests**

```typescript
// append to banking.service.spec.ts
describe('BankingService — adjustments', () => {
  it('recordAdjustment posts to the reconciliation and returns the adjustment', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.recordAdjustment('r1', { offsetAccountId: 'o1', amount: 12.5, kind: 'Charge', date: null, memo: 'fee' }).subscribe(a => (got = a));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations/r1/adjustments');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.kind).toBe('Charge');
    req.flush({ id: 'j1', number: 'ADJ-00001', reconciliationId: 'r1', cashAccountId: 'CA1', offsetAccountId: 'o1',
      kind: 'Charge', amount: 12.5, date: '2026-03-31', memo: 'fee', status: 'Posted' });
    expect(got.id).toBe('j1');
    ctrl.verify();
  });

  it('voidAdjustment posts a reason to the adjustment void route', () => {
    const { svc, ctrl } = setup();
    svc.voidAdjustment('r1', 'j1', 'oops').subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations/r1/adjustments/j1/void');
    expect(req.request.body).toEqual({ reason: 'oops' });
    req.flush({ id: 'j1', number: 'ADJ-00001', reconciliationId: 'r1', cashAccountId: 'CA1', offsetAccountId: 'o1',
      kind: 'Charge', amount: 12.5, date: '2026-03-31', memo: 'fee', status: 'Void' });
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: FAIL — methods not functions.

- [ ] **Step 3: Add the methods**

Add imports to `banking.service.ts`:

```typescript
import {
  // ...existing...
  BankAdjustment, RecordAdjustmentRequest,
} from './banking';
```

Add to the class:

```typescript
  // ── Adjustments ──────────────────────────────────────────────────────────────
  listAdjustments(reconciliationId: string, q: BankingListQuery): Observable<PagedResponse<BankAdjustment>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<BankAdjustment>>(this.base(`/reconciliations/${reconciliationId}/adjustments`), { params: this.listParams(q) });
  }
  recordAdjustment(reconciliationId: string, req: RecordAdjustmentRequest): Observable<BankAdjustment> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<BankAdjustment>(this.base(`/reconciliations/${reconciliationId}/adjustments`), req);
  }
  voidAdjustment(reconciliationId: string, adjId: string, reason?: string | null): Observable<BankAdjustment> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<BankAdjustment>(this.base(`/reconciliations/${reconciliationId}/adjustments/${adjId}/void`), { reason: reason ?? null });
  }
```

- [ ] **Step 4: Run to verify pass**

Run: `cd UI/Angular && npm test -- banking.service`
Expected: PASS (all banking service suites).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/banking/banking.service.ts UI/Angular/src/app/core/banking/banking.service.spec.ts
git commit -m "feat(banking-ui): banking service — adjustment endpoints"
```

---

### Task 17: Adjustments panel (record/list/void) + mount in worksheet

**Files:**
- Create: `UI/Angular/src/app/features/banking/adjustments-panel.ts`
- Test: `UI/Angular/src/app/features/banking/adjustments-panel.spec.ts`
- Modify: `UI/Angular/src/app/features/banking/reconciliation-worksheet.ts` (re-add the `AdjustmentsPanel` import, `imports` entry, and `<app-adjustments-panel>` element removed/deferred in BK-4 Task 15)
- Modify: `UI/Angular/src/app/features/banking/reconciliation-worksheet.spec.ts` (restore the `/adjustments` GET flush in the boot helper)

**Interfaces:**
- Consumes: `BankingService.listAdjustments`/`recordAdjustment`/`voidAdjustment`, `AccountsService`, `BankAdjustment`, `RecordAdjustmentRequest`, `AdjustmentKind`, `adjustmentKindLabel`, `money`/`displayDate`, `extractProblem`.
- Produces: `AdjustmentsPanel` with inputs `reconciliationId: string`, `locked: boolean`, and output `changed` (emitted after a successful record/void so the worksheet reloads its balance).

- [ ] **Step 1: Write the failing test**

```typescript
// adjustments-panel.spec.ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AdjustmentsPanel } from './adjustments-panel';
import { ClientContextService } from '../../core/client/client-context.service';

function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(AdjustmentsPanel);
  fixture.componentRef.setInput('reconciliationId', 'r1');
  fixture.componentRef.setInput('locked', false);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
    [{ id: 'o1', number: '6900', name: 'Bank fees', type: 'Expense', postable: true }]);   // bare array — accounts endpoint is not paged
  ctrl.expectOne(r => r.url.endsWith('/reconciliations/r1/adjustments')).flush({ items: [], total: 0, skip: 0, limit: 50 });
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('AdjustmentsPanel', () => {
  it('records a charge and emits changed', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    let emitted = false; cmp.changed.subscribe(() => (emitted = true));
    cmp.offsetAccountId.set('o1'); cmp.amount.set(12.5); cmp.kind.set('Charge');
    cmp.record();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations/r1/adjustments');
    expect(req.request.body.amount).toBe(12.5);
    req.flush({ id: 'j1', number: 'ADJ-00001', reconciliationId: 'r1', cashAccountId: 'CA1', offsetAccountId: 'o1',
      kind: 'Charge', amount: 12.5, date: '2026-03-31', memo: null, status: 'Posted' });
    // list reloads
    ctrl.expectOne(r => r.url.endsWith('/reconciliations/r1/adjustments')).flush({ items: [
      { id: 'j1', number: 'ADJ-00001', reconciliationId: 'r1', cashAccountId: 'CA1', offsetAccountId: 'o1',
        kind: 'Charge', amount: 12.5, date: '2026-03-31', memo: null, status: 'Posted' }], total: 1, skip: 0, limit: 50 });
    expect(emitted).toBe(true);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `cd UI/Angular && npm test -- adjustments-panel`
Expected: FAIL — cannot find module.

- [ ] **Step 3: Write the component**

```typescript
// adjustments-panel.ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal, effect } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { BankAdjustment, AdjustmentKind, RecordAdjustmentRequest, adjustmentKindLabel } from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-adjustments-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CanDirective, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-3 border-t border-border pt-4">
      <h2 class="font-semibold">Bank adjustments</h2>

      @if (adjustments().length === 0) {
        <p class="text-sm text-muted-foreground">No adjustments recorded.</p>
      } @else {
        <table class="w-full text-sm">
          <thead><tr class="text-left text-muted-foreground"><th class="py-1">Number</th><th>Type</th>
            <th>Account</th><th class="text-right">Amount</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (a of adjustments(); track a.id) {
              <tr>
                <td class="py-1">{{ a.number ?? '—' }}</td>
                <td>{{ kindLabel(a.kind) }}</td>
                <td>{{ label(a.offsetAccountId) }}</td>
                <td class="text-right tabular-nums">{{ money(a.amount) }}</td>
                <td [class.text-destructive]="a.status === 'Void'">{{ a.status }}</td>
                <td class="text-right">
                  @if (a.status === 'Posted' && !locked()) {
                    <button *appCan="'bankrec.write'" hlmBtn size="sm" variant="ghost" (click)="voidAdjustment(a.id)" [disabled]="busy()">Void</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      }

      @if (!locked()) {
        <div *appCan="'bankrec.write'" class="grid grid-cols-4 gap-3 items-end border border-border rounded p-3">
          <div class="flex flex-col gap-1">
            <label hlmLabel>Type</label>
            <div hlmSelect [value]="kind()" (valueChange)="kind.set($any($event))" class="w-full">
              <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
              <hlm-select-content *hlmSelectPortal>
                <hlm-select-item value="Charge">Bank charge</hlm-select-item>
                <hlm-select-item value="Credit">Bank interest</hlm-select-item>
              </hlm-select-content>
            </div>
          </div>
          <div class="flex flex-col gap-1">
            <label hlmLabel>Offset account</label>
            <div hlmSelect [value]="offsetAccountId() ?? ''" [itemToString]="accountItemToString" (valueChange)="offsetAccountId.set($any($event))" class="w-full">
              <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select account" /></hlm-select-trigger>
              <hlm-select-content *hlmSelectPortal>
                @for (ac of postableAccounts(); track ac.id) { <hlm-select-item [value]="ac.id">{{ ac.number }} {{ ac.name }}</hlm-select-item> }
              </hlm-select-content>
            </div>
          </div>
          <div class="flex flex-col gap-1">
            <label hlmLabel>Amount</label>
            <input hlmInput type="number" class="text-right tabular-nums" [value]="amount() ?? ''" (input)="amount.set($any($event.target).value === '' ? null : +$any($event.target).value)" />
          </div>
          <button hlmBtn type="button" (click)="record()" [disabled]="!canRecord() || busy()">Record</button>
        </div>
      }

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
    </div>
  `,
})
export class AdjustmentsPanel {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly reconciliationId = input.required<string>();
  readonly locked = input(false);
  readonly changed = output<void>();

  readonly adjustments = signal<BankAdjustment[]>([]);
  readonly kind = signal<AdjustmentKind>('Charge');
  readonly offsetAccountId = signal<string | null>(null);
  readonly amount = signal<number | null>(null);
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly postableAccounts = computed(() => this.accounts.accounts().filter(a => a.postable));
  readonly canRecord = computed(() => !!this.offsetAccountId() && (this.amount() ?? 0) > 0);

  constructor() {
    if (this.accounts.accounts().length === 0) this.accounts.load();
    // Reload the list whenever the bound reconciliation id changes.
    effect(() => { const id = this.reconciliationId(); if (id) this.reload(id); });
  }

  private reload(id: string): void {
    this.svc.listAdjustments(id, { skip: 0, limit: 50 }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (p) => this.adjustments.set(p.items),
      error: (e) => this.message.set(extractProblem(e).detail),
    });
  }

  readonly accountItemToString = (id: string): string => this.accounts.label(id);

  record(): void {
    if (!this.canRecord()) return;
    this.busy.set(true); this.message.set(null);
    const req: RecordAdjustmentRequest = { offsetAccountId: this.offsetAccountId()!, amount: this.amount()!, kind: this.kind(), date: null, memo: this.memo() };
    this.svc.recordAdjustment(this.reconciliationId(), req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.amount.set(null); this.offsetAccountId.set(null); this.busy.set(false); this.reload(this.reconciliationId()); this.changed.emit(); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  voidAdjustment(adjId: string): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidAdjustment(this.reconciliationId(), adjId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.reload(this.reconciliationId()); this.changed.emit(); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  kindLabel(k: AdjustmentKind): string { return adjustmentKindLabel(k); }
  label(id: string): string { return this.accounts.label(id); }
  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Re-integrate into the worksheet**

Restore in `reconciliation-worksheet.ts` (removed/deferred in BK-4 Task 15): the `import { AdjustmentsPanel } from './adjustments-panel';`, the `AdjustmentsPanel` entry in `imports`, and the element:

```html
<app-adjustments-panel [reconciliationId]="w.reconciliation.id" [locked]="w.reconciliation.status !== 'InProgress'" (changed)="reload()" />
```

Restore the `/adjustments` GET flush in `reconciliation-worksheet.spec.ts`'s boot helper.

- [ ] **Step 5: Run to verify pass**

Run: `cd UI/Angular && npm test -- adjustments-panel reconciliation-worksheet`
Expected: PASS (both).

- [ ] **Step 6: Build + commit**

Run: `cd UI/Angular && npm run build`
Expected: build clean.

```bash
git add UI/Angular/src/app/features/banking/adjustments-panel.ts UI/Angular/src/app/features/banking/adjustments-panel.spec.ts UI/Angular/src/app/features/banking/reconciliation-worksheet.ts UI/Angular/src/app/features/banking/reconciliation-worksheet.spec.ts
git commit -m "feat(banking-ui): bank adjustments panel (record/void) in the worksheet"
```

**BK-5 done:** the Banking area is feature-complete.

---

## Epic wrap-up

After BK-5, before the whole-area review:

- [ ] **Full Angular suite green:** `cd UI/Angular && npm test` — all specs pass.
- [ ] **Build clean:** `cd UI/Angular && npm run build`.
- [ ] **Dev-stack wiring:** add a `Cash__Accounts__*` block to `.localdev/start.ps1` mapped to seeded Demo Co chart GUIDs (mirror the FixedAssets block). Confirm the Cash module's `CashPostingAccounts` keys by reading `Modules/Banking/Cash/Accounting101.Banking.Cash/CashPostingAccounts.cs` and `.../CashServiceExtensions.cs` (env prefix `Cash:Accounts:`). Restart the host. Verify a cash disbursement, a manual statement, a reconciliation, and an adjustment each post without account-config errors.
- [ ] **Smoke test** the Banking tab in the running dev stack (standing convention) before the final merge to master.
- [ ] **Whole-area review** via subagent-driven-development's whole-branch reviewer.

## Self-review (plan author)

**Spec coverage:** every spec §4 slice maps to tasks — BK-1 (Tasks 1–6), BK-2 (7–10), BK-3 (11–12), BK-4 (13–15), BK-5 (16–17). Spec §3.1 routing → Tasks 3/6/10/12/15. §3.2 core → Tasks 1/2 (+ grown per slice). §6 error handling → `extractProblem` + `busy`-both-observers in every editor/detail. §7 testing → serialization guard (Task 1), service specs, per-component specs, mapping-builder + auto-match two-step (Tasks 12/15). §8 dev wiring → wrap-up. §9 watch-outs → auto-match two-step (Task 15), CsvMapping authoritative (Task 12 note), reserved-word names (global constraints), statement-list requires cashAccountId (Task 8).

**Type consistency:** `BankingService` method names are consistent across the tasks that define and consume them (`recordDisbursement`/`recordDeposit`/`voidDisbursement`/`voidDeposit`/`listCash`/`listStatements`/`getStatement`/`recordStatement`/`importStatements`/`startReconciliation`/`getWorksheet`/`clear`/`unclear`/`autoMatchProposal`/`autoMatchApply`/`completeReconciliation`/`listAdjustments`/`recordAdjustment`/`voidAdjustment`). Model names match between `banking.ts` (Task 1) and every consumer.

**Two verify-against-backend gates flagged for the executor:** (1) `AutoMatch` JSON property names — confirm against `AutoMatcher.cs` in Task 1; (2) capability keys `cash.write`/`bankrec.write` — confirm against the control-plane vocabulary in Task 1, adjust everywhere if different.

**Known intentional deferral:** no backend "list reconciliations" endpoint exists — the Reconcile tab is a launcher off statements (Task 14). Noted, not silently worked around.
