# Chart-Health Dashboard Widget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface the six advisory chart-readiness endpoints as a dashboard widget whose flagged gaps deep-link into Chart of Accounts to fix them, with the account editor extended to set required dimensions.

**Architecture:** A frontend Angular slice — a `ChartHealthService` that fans out `GET /clients/{id}/{key}/chart-readiness` across all six module hosts, a dashboard widget rendering per-module readiness with per-gap fix deep-links, and a required-dimensions extension to the account editor (with query-param prefill). One essential backend fix makes `AccountReadinessStatus` serialize as a string.

**Tech Stack:** Angular (standalone, OnPush, signals, signal-forms), Spartan/Helm UI, Tailwind, Vitest + `HttpTestingController`; C# .NET (ModuleKit) for the one backend enum fix.

## Global Constraints

- Angular components: standalone, `ChangeDetectionStrategy.OnPush`, signal-based state. Follow existing `core/<module>/…service.ts` + `features/<area>/…` conventions.
- Services inject `HttpClient` + `ClientContextService`; build URLs off `environment.apiBaseUrl/clients/{clientId}`; guard on `clientId()` and return `EMPTY` when absent.
- Tests: Vitest (`vi.spyOn`), `provideZonelessChangeDetection()`, `provideHttpClient()` + `provideHttpClientTesting()`, `HttpTestingController` with `ctrl.verify()`.
- No new npm libraries. Dimensions input is a comma-separated text field (chips are out of scope).
- Enum-as-string is a **per-type** `[JsonConverter(typeof(JsonStringEnumConverter))]` attribute — there is no global converter.
- Wire contract (camelCase): `ChartReadinessReport { moduleKey, ready, accounts[] }`; `AccountReadinessResult { accountId, label, expectedType, requiredDimensions, status, actualType, actualRequiredDimensions, detail }`; `status ∈ {Ok, Missing, Inactive, WrongType, MissingDimensions}`.
- Six module keys / labels: `receivables`→Receivables, `payables`→Payables, `payroll`→Payroll, `cash`→Cash, `fixedassets`→Fixed Assets, `inventory`→Inventory.
- Account upsert is **PUT-by-id**; a client-chosen id is honored (this is what makes the `Missing` prefill resolve the module's expected account id).

---

### Task 1: Backend — `AccountReadinessStatus` serializes as a string

**Files:**
- Modify: `Modules/Shared/Accounting101.ModuleKit/AccountRequirement.cs`
- Test: `Modules/Shared/Accounting101.ModuleKit.Tests/AccountReadinessStatusSerializationTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: readiness endpoints now emit `status` as a JSON string (e.g. `"Missing"`), which Task 2's TS union depends on.

**Context:** Every domain enum in this repo carries the converter attribute per-type (e.g. `Modules/FixedAssets/Accounting101.FixedAssets/AssetStatus.cs`). `AccountReadinessStatus` currently has none, so it serializes as a number (0–4). E2E tests deserialize back into the C# enum, so numbers round-trip green — a latent wire-format bug (`[[accounting101-ui-mock-casing-trap]]`).

- [ ] **Step 1: Locate the ModuleKit test project.** Confirm it exists and how it is named.

Run: `ls Modules/Shared/Accounting101.ModuleKit.Tests/*.csproj`
Expected: one `.csproj` path prints. (If the directory name differs, use the actual ModuleKit test project path for the test file and all `dotnet test` commands below.)

- [ ] **Step 2: Write the failing serialization test.**

Create `Modules/Shared/Accounting101.ModuleKit.Tests/AccountReadinessStatusSerializationTests.cs`:

```csharp
using System.Text.Json;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.ModuleKit.Tests;

public class AccountReadinessStatusSerializationTests
{
    [Fact]
    public void Status_serializes_as_a_string_not_a_number()
    {
        var report = new ChartReadinessReport(
            "payroll",
            false,
            [new AccountReadinessResult(
                Guid.Empty, "Withholdings Payable", "Liability", [],
                AccountReadinessStatus.Missing, null, null, "add a Liability account")]);

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"status\":\"Missing\"", json);
        Assert.DoesNotContain("\"status\":1", json);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails.**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests --filter AccountReadinessStatusSerializationTests`
Expected: FAIL — the JSON contains `"status":1`, so `Assert.Contains("\"status\":\"Missing\"")` fails.

- [ ] **Step 4: Add the converter attribute.**

In `Modules/Shared/Accounting101.ModuleKit/AccountRequirement.cs`, add the `using` and annotate the enum:

```csharp
using System.Text.Json.Serialization;

namespace Accounting101.ModuleKit;

/// <summary>The status of one required account against a client's chart.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccountReadinessStatus { Ok, Missing, Inactive, WrongType, MissingDimensions }
```

Leave the rest of the file unchanged.

- [ ] **Step 5: Run the test to verify it passes.**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests --filter AccountReadinessStatusSerializationTests`
Expected: PASS.

- [ ] **Step 6: Run the full ModuleKit test project to confirm no regressions.**

Run: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests`
Expected: all pass (the 20 existing + 1 new).

- [ ] **Step 7: Commit.**

```bash
git add Modules/Shared/Accounting101.ModuleKit/AccountRequirement.cs Modules/Shared/Accounting101.ModuleKit.Tests/AccountReadinessStatusSerializationTests.cs
git commit -m "fix(chart-readiness): serialize AccountReadinessStatus as a string"
```

---

### Task 2: Data layer — chart-health model + service

**Files:**
- Create: `UI/Angular/src/app/core/chart-health/chart-health.ts`
- Create: `UI/Angular/src/app/core/chart-health/chart-health.service.ts`
- Test: `UI/Angular/src/app/core/chart-health/chart-health.service.spec.ts` (create)

**Interfaces:**
- Consumes: Task 1's string `status`; `environment.apiBaseUrl`; `ClientContextService.clientId()`.
- Produces:
  - `CHART_HEALTH_MODULES: { key: string; label: string }[]` (six entries, fixed order).
  - `AccountReadinessStatus`, `AccountReadinessResult`, `ChartReadinessReport`, `ModuleHealth` types.
  - `ChartHealthService.readiness(): Observable<ModuleHealth[]>` — six reports in `CHART_HEALTH_MODULES` order; a failed host yields `{ report: null, errored: true }`.

- [ ] **Step 1: Write the model file.**

Create `UI/Angular/src/app/core/chart-health/chart-health.ts`:

```typescript
export type AccountReadinessStatus = 'Ok' | 'Missing' | 'Inactive' | 'WrongType' | 'MissingDimensions';

export interface AccountReadinessResult {
  accountId: string;
  label: string;
  expectedType: string | null;
  requiredDimensions: string[];
  status: AccountReadinessStatus;
  actualType: string | null;
  actualRequiredDimensions: string[] | null;
  detail: string;
}

export interface ChartReadinessReport {
  moduleKey: string;
  ready: boolean;
  accounts: AccountReadinessResult[];
}

/** One module's readiness for the widget; `report` is null when the host call failed. */
export interface ModuleHealth {
  key: string;
  label: string;
  report: ChartReadinessReport | null;
  errored: boolean;
}

/** The six modules the widget checks, in display order. */
export const CHART_HEALTH_MODULES: { key: string; label: string }[] = [
  { key: 'receivables', label: 'Receivables' },
  { key: 'payables', label: 'Payables' },
  { key: 'payroll', label: 'Payroll' },
  { key: 'cash', label: 'Cash' },
  { key: 'fixedassets', label: 'Fixed Assets' },
  { key: 'inventory', label: 'Inventory' },
];
```

- [ ] **Step 2: Write the failing service spec.**

Create `UI/Angular/src/app/core/chart-health/chart-health.service.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChartHealthService } from './chart-health.service';
import { ClientContextService } from '../client/client-context.service';
import { ModuleHealth } from './chart-health';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return { svc: TestBed.inject(ChartHealthService), ctrl: TestBed.inject(HttpTestingController) };
}

function report(moduleKey: string, ready: boolean) {
  return { moduleKey, ready, accounts: [] };
}

describe('ChartHealthService', () => {
  it('fans out to all six module chart-readiness endpoints in order', () => {
    const { svc, ctrl } = setup();
    let out: ModuleHealth[] = [];
    svc.readiness().subscribe(m => (out = m));

    for (const key of ['receivables', 'payables', 'payroll', 'cash', 'fixedassets', 'inventory']) {
      ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`).flush(report(key, true));
    }

    expect(out.map(m => m.key)).toEqual(['receivables', 'payables', 'payroll', 'cash', 'fixedassets', 'inventory']);
    expect(out.every(m => m.report?.ready && !m.errored)).toBe(true);
    ctrl.verify();
  });

  it('marks a module errored when its host call fails, keeping the others', () => {
    const { svc, ctrl } = setup();
    let out: ModuleHealth[] = [];
    svc.readiness().subscribe(m => (out = m));

    ctrl.expectOne('http://localhost:5000/clients/C1/receivables/chart-readiness').flush(report('receivables', true));
    ctrl.expectOne('http://localhost:5000/clients/C1/payables/chart-readiness').flush(report('payables', false));
    ctrl.expectOne('http://localhost:5000/clients/C1/payroll/chart-readiness')
      .flush('boom', { status: 400, statusText: 'Bad Request' });
    ctrl.expectOne('http://localhost:5000/clients/C1/cash/chart-readiness').flush(report('cash', true));
    ctrl.expectOne('http://localhost:5000/clients/C1/fixedassets/chart-readiness').flush(report('fixedassets', true));
    ctrl.expectOne('http://localhost:5000/clients/C1/inventory/chart-readiness').flush(report('inventory', true));

    const payroll = out.find(m => m.key === 'payroll')!;
    expect(payroll.errored).toBe(true);
    expect(payroll.report).toBeNull();
    expect(out.filter(m => !m.errored).length).toBe(5);
    ctrl.verify();
  });
});
```

- [ ] **Step 3: Run the spec to verify it fails.**

Run: `cd UI/Angular && npx vitest run src/app/core/chart-health/chart-health.service.spec.ts`
Expected: FAIL — `ChartHealthService` does not exist yet.

- [ ] **Step 4: Write the service.**

Create `UI/Angular/src/app/core/chart-health/chart-health.service.ts`:

```typescript
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable, catchError, forkJoin, map, of } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { CHART_HEALTH_MODULES, ChartReadinessReport, ModuleHealth } from './chart-health';

@Injectable({ providedIn: 'root' })
export class ChartHealthService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  /** Reads all six modules' chart readiness. A failing host becomes an errored entry, never a thrown stream. */
  readiness(): Observable<ModuleHealth[]> {
    const id = this.client.clientId();
    if (!id) return EMPTY;
    return forkJoin(
      CHART_HEALTH_MODULES.map(m =>
        this.http.get<ChartReadinessReport>(`${environment.apiBaseUrl}/clients/${id}/${m.key}/chart-readiness`).pipe(
          map((report): ModuleHealth => ({ key: m.key, label: m.label, report, errored: false })),
          catchError(() => of<ModuleHealth>({ key: m.key, label: m.label, report: null, errored: true })),
        ),
      ),
    );
  }
}
```

- [ ] **Step 5: Run the spec to verify it passes.**

Run: `cd UI/Angular && npx vitest run src/app/core/chart-health/chart-health.service.spec.ts`
Expected: PASS (both tests).

- [ ] **Step 6: Commit.**

```bash
git add UI/Angular/src/app/core/chart-health/
git commit -m "feat(chart-health): readiness service fanning out to six module hosts"
```

---

### Task 3: Account editor — required-dimensions field + query-param prefill

**Files:**
- Modify: `UI/Angular/src/app/core/accounts/account.ts`
- Modify: `UI/Angular/src/app/core/accounts/accounts.service.ts:26-37`
- Modify: `UI/Angular/src/app/features/accounts/account-editor.ts`
- Test: `UI/Angular/src/app/features/accounts/account-editor.spec.ts` (modify)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `/accounts/new` accepts query params `id`, `type`, `name`, `dims` (comma-separated) and prefills them; the account editor round-trips `requiredDimensions`; the PUT body carries `requiredDimensions: string[]`. Task 4's deep-links target this.

- [ ] **Step 1: Add `requiredDimensions` to the account TS interfaces.**

In `UI/Angular/src/app/core/accounts/account.ts`, add the field to both interfaces:

```typescript
export type AccountType = 'Asset' | 'Liability' | 'Equity' | 'Revenue' | 'Expense';
export interface AccountResponse {
  id: string; number: string; name: string; type: AccountType; parentId: string | null;
  postable: boolean; requiredDimension: string | null; requiredDimensions: string[]; cashFlowActivity: string | null;
  isRetainedEarnings: boolean; active: boolean; normalSide: 'Debit' | 'Credit'; isTemporary: boolean;
}

export interface AccountUpsert {
  id: string; number: string; name: string; type: AccountType;
  parentId: string | null; postable: boolean; requiredDimension: string | null; requiredDimensions: string[];
  cashFlowActivity: string | null; isRetainedEarnings: boolean; active: boolean;
}
```

- [ ] **Step 2: Send `requiredDimensions` in the upsert body.**

In `UI/Angular/src/app/core/accounts/accounts.service.ts`, update the `body` in `upsert` (lines 29-31) to include the plural field:

```typescript
    const body = { number: a.number, name: a.name, type: a.type, parentId: a.parentId,
      postable: a.postable, requiredDimension: a.requiredDimension, requiredDimensions: a.requiredDimensions,
      cashFlowActivity: a.cashFlowActivity, isRetainedEarnings: a.isRetainedEarnings, active: a.active };
```

- [ ] **Step 3: Update the editor spec's `route()` helper and seed to cover query params + dimensions, then add the failing tests.**

In `UI/Angular/src/app/features/accounts/account-editor.spec.ts`:

Replace the `route` helper with one that also stubs `queryParamMap`:

```typescript
function route(id: string | null, query: Record<string, string> = {}) {
  return { provide: ActivatedRoute, useValue: { snapshot: {
    paramMap: { get: () => id },
    queryParamMap: { get: (k: string) => query[k] ?? null },
  } } };
}
```

Update `seedAccounts` so a seeded account carries `requiredDimensions` (add it to the factory `a`):

```typescript
function seedAccounts(svc: AccountsService) {
  const a = (id: string, number: string, type: AccountResponse['type']): AccountResponse =>
    ({ id, number, name: 'n' + number, type, parentId: null, postable: true, requiredDimension: null, requiredDimensions: [], cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false });
  (svc as unknown as { _accounts: { set(v: AccountResponse[]): void } })._accounts.set([a('cash', '1000', 'Asset'), a('rev', '4000', 'Revenue')]);
}
```

Append these tests inside the `describe('AccountEditor', …)` block:

```typescript
  it('prefills id, type, name and dims from query params on a new account', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('gl.manageAccounts'),
      route(null, { id: 'expected-guid', type: 'Liability', name: 'Withholdings Payable', dims: 'Employee, PayPeriod' })] });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.accountForm.name().value()).toBe('Withholdings Payable');
    expect(cmp.accountForm.type().value()).toBe('Liability');
    expect(cmp.dimsText()).toBe('Employee, PayPeriod');

    cmp.accountForm.number().value.set('2100');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    f.detectChanges(); cmp.save();
    const put = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/expected-guid'); // prefilled id, not a random one
    expect(put.request.body.requiredDimensions).toEqual(['Employee', 'PayPeriod']);
    expect(put.request.body.type).toBe('Liability');
    put.flush({ id: 'expected-guid', number: '2100', name: 'Withholdings Payable', type: 'Liability', parentId: null, postable: true, requiredDimension: null, requiredDimensions: ['Employee', 'PayPeriod'], cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Credit', isTemporary: false });
    expect(nav).toHaveBeenCalledWith(['/accounts']);
  });

  it('setDims parses a comma-separated string into a trimmed, non-empty array', () => {
    setup(null); const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.setDims('Customer,  Invoice ,,');
    expect(cmp.accountForm.requiredDimensions().value()).toEqual(['Customer', 'Invoice']);
    expect(cmp.dimsText()).toBe('Customer, Invoice');
  });

  it('edit preserves an existing account\'s dimensions through save', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('gl.manageAccounts'), route('ar')] });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const svc = TestBed.inject(AccountsService);
    (svc as unknown as { _accounts: { set(v: AccountResponse[]): void } })._accounts.set([
      { id: 'ar', number: '1100', name: 'A/R', type: 'Asset', parentId: null, postable: true, requiredDimension: null, requiredDimensions: ['Customer', 'Invoice'], cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false },
    ]);
    const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.dimsText()).toBe('Customer, Invoice');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    cmp.save();
    const put = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/ar');
    expect(put.request.body.requiredDimensions).toEqual(['Customer', 'Invoice']);
    put.flush({ id: 'ar', number: '1100', name: 'A/R', type: 'Asset', parentId: null, postable: true, requiredDimension: null, requiredDimensions: ['Customer', 'Invoice'], cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false });
    expect(nav).toHaveBeenCalledWith(['/accounts']);
  });
```

- [ ] **Step 4: Run the editor spec to verify the new tests fail.**

Run: `cd UI/Angular && npx vitest run src/app/features/accounts/account-editor.spec.ts`
Expected: FAIL — `dimsText`/`setDims` don't exist, prefill isn't read, `requiredDimensions` isn't in the body.

- [ ] **Step 5: Extend the editor component.**

In `UI/Angular/src/app/features/accounts/account-editor.ts`:

(a) Add `requiredDimensions` to `EditorValue`:

```typescript
interface EditorValue {
  number: string; name: string; type: AccountType; parentId: string | null;
  cashFlowActivity: string; postable: boolean; isRetainedEarnings: boolean; active: boolean;
  requiredDimensions: string[];
}
```

(b) Add a prefill id field right after `editId`, and add the dims computed/setter. Replace the `editId` line and add below it:

```typescript
  readonly editId = this.route.snapshot.paramMap.get('id'); // null on /accounts/new
  readonly #prefillId = this.route.snapshot.queryParamMap.get('id'); // only used when creating
```

Add these members (near `parentLabel`/`setParent`):

```typescript
  readonly dimsText = computed(() => this.model().requiredDimensions.join(', '));
  setDims(text: string): void {
    this.accountForm.requiredDimensions().value.set(text.split(',').map(s => s.trim()).filter(Boolean));
  }
```

(c) Replace `initialValue()` to read prefill query params (type/name/dims) when creating:

```typescript
  private initialValue(): EditorValue {
    const q = this.route.snapshot.queryParamMap;
    const creating = !this.editId;
    const qType = q.get('type') as AccountType | null;
    const qDims = q.get('dims');
    return {
      number: '',
      name: creating ? (q.get('name') ?? '') : '',
      type: creating && qType && TYPES.includes(qType) ? qType : 'Asset',
      parentId: null, cashFlowActivity: '', postable: true, isRetainedEarnings: false, active: true,
      requiredDimensions: creating && qDims ? qDims.split(',').map(s => s.trim()).filter(Boolean) : [],
    };
  }
```

(d) Update `fromAccount()` to carry dims:

```typescript
  private fromAccount(a: AccountResponse): EditorValue {
    return { number: a.number, name: a.name, type: a.type, parentId: a.parentId, cashFlowActivity: a.cashFlowActivity ?? '', postable: a.postable, isRetainedEarnings: a.isRetainedEarnings, active: a.active, requiredDimensions: a.requiredDimensions ?? [] };
  }
```

(e) In `save()`, use the prefilled id when creating and send `requiredDimensions`:

```typescript
    this.accounts.upsert({
      id: this.editId ?? this.#prefillId ?? this.accounts.newId(), number: v.number, name: v.name, type: v.type, parentId: v.parentId,
      postable: v.postable, requiredDimension: null, requiredDimensions: v.requiredDimensions, cashFlowActivity: v.cashFlowActivity || null,
      isRetainedEarnings: v.isRetainedEarnings, active: v.active,
    }).subscribe({
```

(f) Register `requiredDimensions` in the form and add the input to the template. In the `form(...)` call no change is needed (it derives fields from the model), but add the input block after the Cash-flow activity block and before the checkboxes:

```html
      <div class="flex flex-col gap-1">
        <label hlmLabel>Required dimensions (comma-separated)</label>
        <input hlmInput type="text" [value]="dimsText()" (change)="setDims($any($event.target).value)"
               placeholder="e.g. Customer, Invoice" />
        <p class="text-muted-foreground text-xs">Control accounts require these dimension axes on every posting line.</p>
      </div>
```

- [ ] **Step 6: Run the editor spec to verify it passes.**

Run: `cd UI/Angular && npx vitest run src/app/features/accounts/account-editor.spec.ts`
Expected: PASS (existing + 3 new tests).

- [ ] **Step 7: Commit.**

```bash
git add UI/Angular/src/app/core/accounts/account.ts UI/Angular/src/app/core/accounts/accounts.service.ts UI/Angular/src/app/features/accounts/account-editor.ts UI/Angular/src/app/features/accounts/account-editor.spec.ts
git commit -m "feat(accounts): required-dimensions field + new-account query-param prefill"
```

---

### Task 4: Dashboard widget + placement

**Files:**
- Create: `UI/Angular/src/app/features/dashboard/chart-health-widget.ts`
- Modify: `UI/Angular/src/app/features/dashboard/dashboard.ts`
- Test: `UI/Angular/src/app/features/dashboard/chart-health-widget.spec.ts` (create)

**Interfaces:**
- Consumes: `ChartHealthService.readiness()` (Task 2); the account-editor deep-link contract (Task 3): `Missing` → `/accounts/new` with `{ id, type, name, dims }`; other statuses → `/accounts/{accountId}/edit`.
- Produces: nothing downstream.

- [ ] **Step 1: Write the failing widget spec.**

Create `UI/Angular/src/app/features/dashboard/chart-health-widget.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChartHealthWidget } from './chart-health-widget';
import { ClientContextService } from '../../core/client/client-context.service';

const KEYS = ['receivables', 'payables', 'payroll', 'cash', 'fixedassets', 'inventory'];

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  return { ctrl };
}

function flushAll(ctrl: HttpTestingController, overrides: Record<string, unknown> = {}) {
  for (const key of KEYS) {
    const body = overrides[key] ?? { moduleKey: key, ready: true, accounts: [] };
    ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`).flush(body);
  }
}

describe('ChartHealthWidget', () => {
  it('shows the ready count out of six', () => {
    const { ctrl } = setup();
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    flushAll(ctrl, { payroll: { moduleKey: 'payroll', ready: false, accounts: [
      { accountId: 'wh', label: 'Withholdings Payable', expectedType: 'Liability', requiredDimensions: [], status: 'Missing', actualType: null, actualRequiredDimensions: null, detail: 'add a Liability account' } ] } });
    f.detectChanges();
    expect(f.componentInstance.readyCount()).toBe(5);
    const text = (f.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('5 / 6');
    ctrl.verify();
  });

  it('builds a prefilled new-account link for a Missing gap', () => {
    const { ctrl } = setup();
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    flushAll(ctrl, { payroll: { moduleKey: 'payroll', ready: false, accounts: [
      { accountId: 'wh-guid', label: 'Withholdings Payable', expectedType: 'Liability', requiredDimensions: ['Employee'], status: 'Missing', actualType: null, actualRequiredDimensions: null, detail: 'add a Liability account' } ] } });
    f.detectChanges();
    const gap = { accountId: 'wh-guid', label: 'Withholdings Payable', expectedType: 'Liability', requiredDimensions: ['Employee'], status: 'Missing' as const, actualType: null, actualRequiredDimensions: null, detail: '' };
    expect(f.componentInstance.gapLink(gap)).toEqual(['/accounts', 'new']);
    expect(f.componentInstance.gapQuery(gap)).toEqual({ id: 'wh-guid', type: 'Liability', name: 'Withholdings Payable', dims: 'Employee' });
    ctrl.verify();
  });

  it('builds an edit link for a non-Missing gap', () => {
    const { ctrl } = setup();
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    flushAll(ctrl);
    f.detectChanges();
    const gap = { accountId: 'ar', label: 'A/R', expectedType: 'Asset', requiredDimensions: ['Customer'], status: 'MissingDimensions' as const, actualType: 'Asset', actualRequiredDimensions: [], detail: '' };
    expect(f.componentInstance.gapLink(gap)).toEqual(['/accounts', 'ar', 'edit']);
    expect(f.componentInstance.gapQuery(gap)).toBeUndefined();
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run the widget spec to verify it fails.**

Run: `cd UI/Angular && npx vitest run src/app/features/dashboard/chart-health-widget.spec.ts`
Expected: FAIL — `ChartHealthWidget` does not exist.

- [ ] **Step 3: Write the widget component.**

Create `UI/Angular/src/app/features/dashboard/chart-health-widget.ts`:

```typescript
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ChartHealthService } from '../../core/chart-health/chart-health.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { AccountReadinessResult, CHART_HEALTH_MODULES, ModuleHealth } from '../../core/chart-health/chart-health';

@Component({
  selector: 'app-chart-health-widget',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <section class="rounded-lg border p-4 flex flex-col gap-3 max-w-xl">
      <header class="flex items-center justify-between">
        <h2 class="font-semibold">Chart Health</h2>
        <span class="text-sm text-muted-foreground">{{ readyCount() }} / {{ total }} ready</span>
      </header>

      @if (loading()) {
        <p class="text-sm text-muted-foreground">Checking…</p>
      } @else {
        <ul class="flex flex-col divide-y">
          @for (m of modules(); track m.key) {
            <li class="py-2">
              <div class="flex items-center justify-between">
                <span>{{ m.label }}</span>
                @if (m.errored) {
                  <span class="text-sm text-muted-foreground">couldn't check</span>
                } @else if (m.report?.ready) {
                  <span class="text-sm text-green-600" aria-label="ready">✓ ready</span>
                } @else {
                  <button type="button" class="text-sm text-destructive underline" (click)="toggle(m.key)">
                    {{ gapCount(m) }} gap{{ gapCount(m) === 1 ? '' : 's' }} {{ expanded().has(m.key) ? '▾' : '›' }}
                  </button>
                }
              </div>
              @if (expanded().has(m.key) && m.report) {
                <ul class="mt-2 flex flex-col gap-1 pl-3 text-sm">
                  @for (g of gaps(m); track g.accountId) {
                    <li class="flex flex-col">
                      <span class="text-muted-foreground">{{ g.label }} — {{ g.status }}</span>
                      <span>{{ g.detail }}</span>
                      <a class="text-primary underline w-fit" [routerLink]="gapLink(g)" [queryParams]="gapQuery(g)">Fix ›</a>
                    </li>
                  }
                </ul>
              }
            </li>
          }
        </ul>
      }
    </section>
  `,
})
export class ChartHealthWidget {
  private readonly health = inject(ChartHealthService);
  private readonly client = inject(ClientContextService);

  readonly total = CHART_HEALTH_MODULES.length;
  readonly modules = signal<ModuleHealth[]>([]);
  readonly loading = signal(false);
  readonly expanded = signal<Set<string>>(new Set());

  readonly readyCount = computed(() => this.modules().filter(m => m.report?.ready).length);

  constructor() {
    effect(() => {
      const id = this.client.clientId();
      if (!id) { this.modules.set([]); return; }
      this.loading.set(true);
      this.health.readiness().subscribe(m => { this.modules.set(m); this.loading.set(false); });
    });
  }

  gapCount(m: ModuleHealth): number { return this.gaps(m).length; }
  gaps(m: ModuleHealth): AccountReadinessResult[] { return (m.report?.accounts ?? []).filter(a => a.status !== 'Ok'); }

  toggle(key: string): void {
    this.expanded.update(set => {
      const next = new Set(set);
      next.has(key) ? next.delete(key) : next.add(key);
      return next;
    });
  }

  gapLink(g: AccountReadinessResult): (string)[] {
    return g.status === 'Missing' ? ['/accounts', 'new'] : ['/accounts', g.accountId, 'edit'];
  }

  gapQuery(g: AccountReadinessResult): Record<string, string> | undefined {
    if (g.status !== 'Missing') return undefined;
    return { id: g.accountId, type: g.expectedType ?? '', name: g.label, dims: g.requiredDimensions.join(',') };
  }
}
```

- [ ] **Step 4: Run the widget spec to verify it passes.**

Run: `cd UI/Angular && npx vitest run src/app/features/dashboard/chart-health-widget.spec.ts`
Expected: PASS (three tests).

- [ ] **Step 5: Place the widget on the dashboard.**

Replace `UI/Angular/src/app/features/dashboard/dashboard.ts` with:

```typescript
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ChartHealthWidget } from './chart-health-widget';

@Component({
  selector: 'app-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChartHealthWidget],
  template: `<h1 class="text-2xl font-bold mb-2">Dashboard</h1>
    <p class="text-muted-foreground mb-4">Welcome to Accounting 101.</p>
    <app-chart-health-widget />`,
})
export class Dashboard {}
```

- [ ] **Step 6: Run the dashboard spec + typecheck to confirm placement compiles.**

Run: `cd UI/Angular && npx vitest run src/app/features/dashboard/ && npx tsc -p tsconfig.app.json --noEmit`
Expected: PASS; no type errors.

- [ ] **Step 7: Commit.**

```bash
git add UI/Angular/src/app/features/dashboard/
git commit -m "feat(dashboard): chart-health widget with per-gap fix deep-links"
```

---

### Task 5: Dev-stack smoke test (mandatory casing-trap gate)

**Files:** none (verification only).

**Context:** Per `[[accounting101-ui-mock-casing-trap]]`, self-consistent unit specs go green even when real cross-host serialization is wrong. This task exercises the live dev stack — the only layer that sees the module hosts' actual JSON. **This gate is non-optional before merge.**

- [ ] **Step 1: Start the dev stack.** Use the gitignored `.localdev/start.ps1` (starts the ledger + module hosts with seeded `*__Accounts__*` GUIDs) and `ng serve` for the Angular app. Confirm the API is reachable at `http://localhost:5000` and the app at its dev URL.

- [ ] **Step 2: Load the dashboard for the seeded demo client** (`environment.devClientId`). Confirm the Chart Health widget renders and shows an `X / 6 ready` summary.

- [ ] **Step 3: Verify the status is TEXT, not a number.** In the browser devtools Network tab, inspect a `…/chart-readiness` response and confirm each `accounts[].status` is a string (`"Ok"`, `"Missing"`, …), never a bare number. In the widget, any expanded gap shows a word status (e.g. `Withholdings Payable — Missing`), never `— 1`. If a number appears, Task 1 did not take effect for that host — stop and fix before proceeding.

- [ ] **Step 4: Drive a `Missing` fix round-trip.** Expand a module with a `Missing` gap, click **Fix ›**, and confirm the New-Account form opens with Type and Name prefilled (and dimensions if the gap required them). Save it, return to the dashboard, and confirm that module now reports **✓ ready** (proving the prefilled expected id matched the module's posting account).

- [ ] **Step 5: Record the result.** Note in the task/PR summary: which client, which module(s) exercised, that `status` serialized as strings, and that the fix round-trip flipped a module to ready. Shut the dev stack down cleanly.

---

## Notes for the whole-branch review

- Confirm no engine (`Backend/Accounting101.Ledger.*` beyond ModuleKit) files changed.
- Confirm `git status` shows `UI/Angular/src/app/core/api/environment.ts` remains uncommitted (per repo convention).
- Full suites before merge: `dotnet test Modules/Shared/Accounting101.ModuleKit.Tests` and `cd UI/Angular && npx vitest run`.
