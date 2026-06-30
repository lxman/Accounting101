# Payables Module UI — Foundation Slice (P-A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the Payables module in the Angular UI — a shell with **Bills** and **Vendors** tabs covering the vendor + bill lifecycle (create vendor, draft bill, enter, void) — plus the one backend addition it needs (`GET /vendors`).

**Architecture:** Mirror the proven Receivables AR-1 foundation. Backend: add a vendor list method to `DocumentVendorStore` and a `GET /clients/{clientId}/vendors` endpoint. Frontend: `core/payables` (models + a root-singleton service) and `features/payables` (shell, vendor-list, bill-list, bill-editor, bill-detail) plus a shared `<app-vendor-select>`. The UI calls `{apiBaseUrl}/clients/{clientId}/…` directly.

**Tech Stack:** ASP.NET Core minimal APIs + xUnit (backend); Angular 22 standalone components, signals, Signal Forms, Spartan/helm UI, Vitest + `@angular/common/http/testing` (frontend).

## Global Constraints

- Commit trailer on EVERY commit, exactly:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- Bill editor is **create-only** — no draft edit/discard (backend has no `PUT`/`DELETE /bills/{id}`).
- Bills are **vendor-scoped**: `GET /bills` requires a `vendorId` query param.
- Bill lines have **no qty/unitPrice/tax** — each line is `{ description, amount, expenseAccountId }`; bill total = sum of line amounts.
- Expense-account dropdown is sourced from `AccountsService`, filtered to `type === 'Expense' && postable && active`.
- All Angular components: `ChangeDetectionStrategy.OnPush`, standalone, `provideZonelessChangeDetection()` in tests.
- Every helm `hlmSelect` needs `*hlmSelectPortal` on `<hlm-select-content>` and `[itemToString]` when the value differs from the label (per `accounting101-spartan-select-gotchas`).
- Record-list rows navigate on **whole-row click** (`cursor-pointer`, `tabindex`, `(click)` + `(keydown.enter)`), never an id-cell `<a>` (per `accounting101-ui-whole-row-click`).
- New backend `using`s must compile clean; keep mirror of receivables naming (`PascalCase` C#, `camelCase` wire keys).
- Branch for this slice off `master`; ff-merge + push + delete branch on "merge and push".

---

### Task 1: Backend — vendor list (store method + `GET /vendors`)

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/VendorPorts.cs`
- Modify: `Modules/Payables/Accounting101.Payables/DocumentVendorStore.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs:14-24` (add route + handler)
- Test: `Modules/Payables/Accounting101.Payables.Tests/DocumentVendorStoreTests.cs` (add a list test)
- Test (create): `Modules/Payables/Accounting101.Payables.Tests/VendorListEndpointTests.cs`

**Interfaces:**
- Produces: `IVendorStore.ListAsync(Guid clientId, CancellationToken ct = default) : Task<IReadOnlyList<Vendor>>` — vendors for the client ordered by `Name` ascending (`StringComparer.OrdinalIgnoreCase`).
- Produces: `GET /clients/{clientId}/vendors` → `200 IReadOnlyList<Vendor>` (each `Vendor { Id, Name, Email }`); client-isolated.

- [ ] **Step 1: Add the failing store test**

In `DocumentVendorStoreTests.cs`, add inside the class:

```csharp
    [Fact]
    public async Task Lists_vendors_ordered_by_name_ascending()
    {
        IVendorStore store = new DocumentVendorStore(fixture.Store);
        await store.SaveAsync(fixture.ClientId, new Vendor { Id = Guid.NewGuid(), Name = "Zeta Supplies", Email = null });
        await store.SaveAsync(fixture.ClientId, new Vendor { Id = Guid.NewGuid(), Name = "Acme Parts", Email = "a@x.com" });

        IReadOnlyList<Vendor> vendors = await store.ListAsync(fixture.ClientId);

        Assert.Equal(2, vendors.Count);
        Assert.Equal("Acme Parts", vendors[0].Name);
        Assert.Equal("Zeta Supplies", vendors[1].Name);
    }
```

- [ ] **Step 2: Run it to verify it fails to compile**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~DocumentVendorStoreTests"`
Expected: BUILD FAILS — `IVendorStore` has no `ListAsync`.

- [ ] **Step 3: Add `ListAsync` to the port**

In `VendorPorts.cs`, add to the `IVendorStore` interface (after `GetAsync`):

```csharp
    Task<IReadOnlyList<Vendor>> ListAsync(Guid clientId, CancellationToken ct = default);
```

- [ ] **Step 4: Implement `ListAsync` in the store**

In `DocumentVendorStore.cs`, add (after `GetAsync`), mirroring `DocumentCustomerStore.ListAsync`:

```csharp
    public async Task<IReadOnlyList<Vendor>> ListAsync(Guid clientId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<VendorBody>> results = await documents.QueryAsync<VendorBody>(
            clientId, Collection, new Dictionary<string, string>(), cancellationToken: ct);
        return results
            .Select(r => new Vendor { Id = r.Id, Name = r.Body.Name, Email = r.Body.Email })
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
```

- [ ] **Step 5: Run the store test to verify it passes**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~DocumentVendorStoreTests"`
Expected: PASS (both tests).

- [ ] **Step 6: Add the failing endpoint test**

Create `VendorListEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;

namespace Accounting101.Payables.Tests;

/// <summary>Proves GET /clients/{clientId}/vendors returns the client's vendors ordered by Name,
/// and that a different client sees an empty array (isolation).</summary>
public sealed class VendorListEndpointTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task List_returns_vendors_ordered_by_name_ascending()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        (await http.PostAsJsonAsync($"/clients/{clientId}/vendors", new CreateVendorRequest("Zeta Supplies", null)))
            .EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync($"/clients/{clientId}/vendors", new CreateVendorRequest("Acme Parts", "a@x.com")))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await http.GetAsync($"/clients/{clientId}/vendors");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Vendor[] vendors = (await response.Content.ReadFromJsonAsync<Vendor[]>())!;
        Assert.Equal(2, vendors.Length);
        Assert.Equal("Acme Parts", vendors[0].Name);
        Assert.Equal("Zeta Supplies", vendors[1].Name);
    }

    [Fact]
    public async Task List_for_a_different_client_returns_empty_array()
    {
        (Guid clientAId, HttpClient httpA) = await fixture.SeedClientAsync();
        (Guid clientBId, HttpClient httpB) = await fixture.SeedClientAsync();

        (await httpA.PostAsJsonAsync($"/clients/{clientAId}/vendors", new CreateVendorRequest("Isolated Co", null)))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await httpB.GetAsync($"/clients/{clientBId}/vendors");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Vendor[] vendors = (await response.Content.ReadFromJsonAsync<Vendor[]>())!;
        Assert.Empty(vendors);
    }
}
```

- [ ] **Step 7: Run it to verify it fails**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorListEndpointTests"`
Expected: FAIL — `GET /vendors` returns 404/405 (route not mapped).

- [ ] **Step 8: Map the route + handler**

In `PayablesEndpoints.cs`, add the route registration (after the `clients.MapPost("/vendors", CreateVendor);` line):

```csharp
        clients.MapGet("/vendors", ListVendors);
```

And add the handler (place it next to `CreateVendor`):

```csharp
    private static async Task<IResult> ListVendors(
        Guid clientId, IVendorStore store, CancellationToken cancellationToken) =>
        Results.Ok(await store.ListAsync(clientId, cancellationToken));
```

- [ ] **Step 9: Run the endpoint test to verify it passes**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorListEndpointTests"`
Expected: PASS (both tests).

- [ ] **Step 10: Run the full payables backend suite (no regressions)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: all green.

- [ ] **Step 11: Commit**

```bash
git add Modules/Payables
git commit -m "feat(payables): GET /vendors list endpoint + IVendorStore.ListAsync

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: UI core — payables models

**Files:**
- Create: `UI/Angular/src/app/core/payables/payables.ts`
- Test: `UI/Angular/src/app/core/payables/payables.spec.ts`

**Interfaces:**
- Produces types: `Vendor`, `BillStatus`, `SettlementStatus`, `SettlementFilter`, `BillLine`, `Bill`, `BillView`, `DraftBillRequest`, `BillListQuery`.
- Produces: `billTotal(lines: readonly Pick<BillLine,'amount'>[]) : number` — sum of line amounts.

- [ ] **Step 1: Write the failing test**

Create `payables.spec.ts`:

```typescript
import { billTotal } from './payables';

describe('billTotal', () => {
  it('sums line amounts', () => {
    expect(billTotal([{ amount: 100 }, { amount: 49.5 }, { amount: 0 }])).toBe(149.5);
  });
  it('is 0 for no lines', () => {
    expect(billTotal([])).toBe(0);
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx vitest run src/app/core/payables/payables.spec.ts`
Expected: FAIL — cannot resolve `./payables`.

- [ ] **Step 3: Create the models file**

Create `payables.ts`:

```typescript
export type BillStatus = 'Draft' | 'Entered' | 'Void';
export type SettlementStatus = 'Open' | 'PartiallyPaid' | 'Paid';
export type SettlementFilter = 'open' | 'paid';

export interface Vendor { id: string; name: string; email: string | null; }

export interface BillLine { description: string; amount: number; expenseAccountId: string; }

export interface Bill {
  id: string; vendorId: string; number: string | null;
  billDate: string; dueDate: string | null;
  vendorReference: string | null; memo: string | null;
  status: BillStatus; lines: BillLine[];
}

export interface BillView { bill: Bill; openBalance: number; settlementStatus: SettlementStatus; }

export interface DraftBillRequest {
  vendorId: string; billDate: string; dueDate: string | null;
  vendorReference: string | null; memo: string | null; lines: BillLine[];
}

export interface VoidBillRequest { reason: string | null; }

export interface BillListQuery {
  vendorId: string; settlement?: SettlementFilter; skip: number; limit: number; order?: 'asc' | 'desc';
}

/** Bill total — sum of line amounts (bills carry no tax). */
export const billTotal = (lines: readonly Pick<BillLine, 'amount'>[]): number =>
  lines.reduce((s, l) => s + l.amount, 0);
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx vitest run src/app/core/payables/payables.spec.ts`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.ts UI/Angular/src/app/core/payables/payables.spec.ts
git commit -m "feat(ui): payables core models + billTotal helper

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: UI core — PayablesService

**Files:**
- Create: `UI/Angular/src/app/core/payables/payables.service.ts`
- Test: `UI/Angular/src/app/core/payables/payables.service.spec.ts`

**Interfaces:**
- Consumes: models from Task 2; `ClientContextService` (`../client/client-context.service`); `environment` (`../api/environment`); `PagedResponse` (`../api/paged-response`); `extractProblem` (`../api/problem-details`).
- Produces (root singleton `PayablesService`):
  - `vendors: Signal<Vendor[]>`, `loadError: Signal<string|null>`, `selectedVendorId: Signal<string>`
  - `load(): void`, `create(name, email?): Observable<Vendor>`, `vendorName(id): string`, `setSelectedVendor(id): void`
  - `listBills(q: BillListQuery): Observable<PagedResponse<BillView>>`, `getBill(id): Observable<BillView>`
  - `draftBill(req: DraftBillRequest): Observable<Bill>`, `enter(id): Observable<Bill>`, `void(id, reason?): Observable<Bill>`

- [ ] **Step 1: Write the failing test**

Create `payables.service.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PayablesService } from './payables.service';
import { ClientContextService } from '../client/client-context.service';

describe('PayablesService', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    return { svc: TestBed.inject(PayablesService), ctrl: TestBed.inject(HttpTestingController) };
  }

  it('loads vendors into the signal', () => {
    const { svc, ctrl } = setup();
    svc.load();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors')
      .flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    expect(svc.vendors().length).toBe(1);
    expect(svc.vendorName('v1')).toBe('Acme Parts');
    ctrl.verify();
  });

  it('lists bills for a vendor with settlement filter', () => {
    const { svc, ctrl } = setup();
    svc.listBills({ vendorId: 'v1', settlement: 'open', skip: 0, limit: 50 }).subscribe();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills');
    expect(req.request.params.get('vendorId')).toBe('v1');
    expect(req.request.params.get('settlement')).toBe('open');
    req.flush({ items: [], total: 0, skip: 0, limit: 50 });
    ctrl.verify();
  });

  it('posts a draft bill', () => {
    const { svc, ctrl } = setup();
    const body = { vendorId: 'v1', billDate: '2026-06-30', dueDate: null, vendorReference: null, memo: null,
      lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] };
    svc.draftBill(body).subscribe();
    const req = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills');
    expect(req.request.body).toEqual(body);
    req.flush({ id: 'b1', vendorId: 'v1', number: null, billDate: '2026-06-30', dueDate: null,
      vendorReference: null, memo: null, status: 'Draft', lines: body.lines });
    ctrl.verify();
  });

  it('enters and voids a bill', () => {
    const { svc, ctrl } = setup();
    svc.enter('b1').subscribe();
    ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/b1/enter').flush({});
    svc.void('b1', 'oops').subscribe();
    const v = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/b1/void');
    expect(v.request.body).toEqual({ reason: 'oops' });
    v.flush({});
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx vitest run src/app/core/payables/payables.service.spec.ts`
Expected: FAIL — cannot resolve `./payables.service`.

- [ ] **Step 3: Create the service**

Create `payables.service.ts` (mirrors `ReceivablesService` vendor/bill subset):

```typescript
import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, tap } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import { extractProblem } from '../api/problem-details';
import { Vendor, Bill, BillView, DraftBillRequest, BillListQuery } from './payables';

@Injectable({ providedIn: 'root' })
export class PayablesService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);
  private readonly _vendors = signal<Vendor[]>([]);
  readonly vendors = this._vendors.asReadonly();
  private readonly byId = computed(() => new Map(this._vendors().map(v => [v.id, v])));
  private readonly _loadError = signal<string | null>(null);
  readonly loadError = this._loadError.asReadonly();

  // Selected vendor survives navigation (root singleton) and reload (per-client localStorage).
  private readonly _selectedVendorId = signal<string>('');
  readonly selectedVendorId = this._selectedVendorId.asReadonly();

  constructor() {
    effect(() => {
      const cid = this.client.clientId();
      this._selectedVendorId.set(cid ? (localStorage.getItem(this.vendorKey(cid)) ?? '') : '');
    });
  }

  private vendorKey(clientId: string): string { return `a101.pay.vendor.${clientId}`; }

  setSelectedVendor(id: string): void {
    this._selectedVendorId.set(id);
    const cid = this.client.clientId(); if (!cid) return;
    if (id) localStorage.setItem(this.vendorKey(cid), id);
    else localStorage.removeItem(this.vendorKey(cid));
  }

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  load(): void {
    const id = this.client.clientId(); if (!id) return;
    this.http.get<Vendor[]>(this.base('/vendors')).subscribe({
      next: vs => {
        this._vendors.set(vs);
        this._loadError.set(null);
        const sel = this._selectedVendorId();
        if (sel && !vs.some(v => v.id === sel)) this.setSelectedVendor('');
      },
      error: e => this._loadError.set(extractProblem(e).detail),
    });
  }

  create(name: string, email?: string | null): Observable<Vendor> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<Vendor>(this.base('/vendors'), { name, email: email ?? null })
      .pipe(tap(v => this._vendors.update(list => [...list, v])));
  }

  vendorName(id: string): string { return this.byId().get(id)?.name ?? id; }

  listBills(q: BillListQuery): Observable<PagedResponse<BillView>> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    let params = new HttpParams().set('vendorId', q.vendorId).set('skip', q.skip).set('limit', q.limit);
    if (q.settlement) params = params.set('settlement', q.settlement);
    if (q.order) params = params.set('order', q.order);
    return this.http.get<PagedResponse<BillView>>(this.base('/bills'), { params });
  }

  getBill(id: string): Observable<BillView> { return this.http.get<BillView>(this.base(`/bills/${id}`)); }

  draftBill(req: DraftBillRequest): Observable<Bill> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<Bill>(this.base('/bills'), req);
  }

  enter(id: string): Observable<Bill> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.post<Bill>(this.base(`/bills/${id}/enter`), {});
  }

  void(id: string, reason?: string | null): Observable<Bill> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.post<Bill>(this.base(`/bills/${id}/void`), { reason: reason ?? null });
  }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx vitest run src/app/core/payables/payables.service.spec.ts`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.service.ts UI/Angular/src/app/core/payables/payables.service.spec.ts
git commit -m "feat(ui): PayablesService (vendors + bill lifecycle)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: shared — `<app-vendor-select>`

**Files:**
- Create: `UI/Angular/src/app/shared/vendor-select.ts`
- Test: `UI/Angular/src/app/shared/vendor-select.spec.ts`

**Interfaces:**
- Consumes: `PayablesService` (`vendors`, `selectedVendorId`, `setSelectedVendor`, `vendorName`).
- Produces: `VendorSelect` component, selector `app-vendor-select`.

- [ ] **Step 1: Write the failing test**

Create `vendor-select.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorSelect } from './vendor-select';
import { PayablesService } from '../core/payables/payables.service';
import { ClientContextService } from '../core/client/client-context.service';

describe('VendorSelect', () => {
  it('renders vendor options from the service', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    const svc = TestBed.inject(PayablesService);
    const ctrl = TestBed.inject(HttpTestingController);
    svc.load();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    const f = TestBed.createComponent(VendorSelect);
    f.detectChanges();
    expect(f.componentInstance.svc.vendors()[0].name).toBe('Acme Parts');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx vitest run src/app/shared/vendor-select.spec.ts`
Expected: FAIL — cannot resolve `./vendor-select`.

- [ ] **Step 3: Create the component**

Create `vendor-select.ts` (mirror of `customer-select.ts`):

```typescript
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { PayablesService } from '../core/payables/payables.service';

/** The vendor picker shared by the Payables list tabs. Bound to the service's persisted
 *  per-client selection, so choosing a vendor on one tab carries to the others. */
@Component({
  selector: 'app-vendor-select',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmSelectImports],
  template: `
    <div hlmSelect [value]="svc.selectedVendorId()" [itemToString]="toName"
         (valueChange)="svc.setSelectedVendor($any($event) ?? '')">
      <hlm-select-trigger class="w-64">
        <hlm-select-value placeholder="Select a vendor" />
      </hlm-select-trigger>
      <hlm-select-content *hlmSelectPortal>
        @for (v of svc.vendors(); track v.id) {
          <hlm-select-item [value]="v.id">{{ v.name }}</hlm-select-item>
        }
      </hlm-select-content>
    </div>
  `,
})
export class VendorSelect {
  readonly svc = inject(PayablesService);
  readonly toName = (id: string): string => this.svc.vendorName(id);
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx vitest run src/app/shared/vendor-select.spec.ts`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/shared/vendor-select.ts UI/Angular/src/app/shared/vendor-select.spec.ts
git commit -m "feat(ui): shared <app-vendor-select>

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: feature — VendorList tab

**Files:**
- Create: `UI/Angular/src/app/features/payables/vendor-list.ts`
- Test: `UI/Angular/src/app/features/payables/vendor-list.spec.ts`

**Interfaces:**
- Consumes: `PayablesService`, `extractProblem`, Angular `Router`.
- Produces: `VendorList` component, selector `app-vendor-list`. Whole-row click → `setSelectedVendor(id)` then navigate `['/payables/bills']`.

- [ ] **Step 1: Write the failing test**

Create `vendor-list.spec.ts` (mirror of `customer-list.spec.ts`):

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorList } from './vendor-list';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { vi } from 'vitest';

describe('VendorList', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  it('lists vendors and creates one inline', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(VendorList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Acme Parts');

    const cmp = f.componentInstance;
    cmp.newName.set('Beta Supply');
    cmp.newEmail.set('b@x.com');
    cmp.add();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/vendors');
    expect(post.request.body).toEqual({ name: 'Beta Supply', email: 'b@x.com' });
    post.flush({ id: 'v2', name: 'Beta Supply', email: 'b@x.com' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Beta Supply');
    expect(cmp.newName()).toBe('');
    ctrl.verify();
  });

  it('clicking a vendor row selects it and navigates to bills', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(VendorList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    const row = f.nativeElement.querySelector('[data-testid="vendor-row"]') as HTMLElement;
    row.click();
    expect(svc.selectedVendorId()).toBe('v1');
    expect(nav).toHaveBeenCalledWith(['/payables/bills']);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/vendor-list.spec.ts`
Expected: FAIL — cannot resolve `./vendor-list`.

- [ ] **Step 3: Create the component**

Create `vendor-list.ts` (mirror of `customer-list.ts`; row click selects + routes to bills):

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayablesService } from '../../core/payables/payables.service';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-vendor-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmInputImports, HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Vendors</h1>
      <div class="flex items-end gap-2">
        <div class="flex flex-col gap-1 flex-1"><label class="text-xs text-muted-foreground">Name</label>
          <input hlmInput [value]="newName()" (input)="newName.set($any($event.target).value)" /></div>
        <div class="flex flex-col gap-1 flex-1"><label class="text-xs text-muted-foreground">Email (optional)</label>
          <input hlmInput [value]="newEmail()" (input)="newEmail.set($any($event.target).value)" /></div>
        <button hlmBtn type="button" (click)="add()" [disabled]="!newName().trim() || busy()">Add</button>
      </div>
      @if (svc.loadError()) { <p class="text-destructive text-sm">{{ svc.loadError() }}</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (svc.vendors().length === 0 && !svc.loadError()) { <p class="text-sm text-muted-foreground italic">No vendors yet.</p> }
      @for (v of svc.vendors(); track v.id) {
        <div data-testid="vendor-row"
             class="flex items-center gap-3 py-1 border-b border-border/50 text-sm cursor-pointer hover:bg-muted/50"
             role="button" tabindex="0"
             (click)="open(v.id)" (keydown.enter)="open(v.id)">
          <span>{{ v.name }}</span><span class="text-muted-foreground">{{ v.email }}</span>
        </div>
      }
    </div>`,
})
export class VendorList {
  readonly svc = inject(PayablesService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  readonly newName = signal('');
  readonly newEmail = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  constructor() { this.svc.load(); }

  add(): void {
    const name = this.newName().trim();
    if (!name) return;
    this.busy.set(true);
    this.error.set(null);
    this.svc.create(name, this.newEmail().trim() || null).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.newName.set(''); this.newEmail.set(''); this.busy.set(false); },
      error: (e) => { this.error.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  open(id: string): void { this.svc.setSelectedVendor(id); void this.router.navigate(['/payables/bills']); }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/vendor-list.spec.ts`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/vendor-list.ts UI/Angular/src/app/features/payables/vendor-list.spec.ts
git commit -m "feat(ui): payables VendorList tab (list + create, row→bills)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: feature — BillList tab

**Files:**
- Create: `UI/Angular/src/app/features/payables/bill-list.ts`
- Test: `UI/Angular/src/app/features/payables/bill-list.spec.ts`

**Interfaces:**
- Consumes: `PayablesService`, `VendorSelect`, `SettlementBadge` (`../../shared/settlement-badge`), `billTotal`, `money`/`displayDate`, `PagedResponse`, `extractProblem`, `Router`.
- Produces: `BillList` component, selector `app-bill-list`. Whole-row click → `/payables/bills/:id`. New-bill button → `/payables/bills/new`.

- [ ] **Step 1: Write the failing test**

Create `bill-list.spec.ts` (mirror of the invoice-list test pattern; selection comes from the service):

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillList } from './bill-list';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { vi } from 'vitest';

describe('BillList', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  function flushVendorsThenSelect(ctrl: HttpTestingController, svc: PayablesService) {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    svc.setSelectedVendor('v1');
  }

  it('prompts to select a vendor when none is selected', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Select a vendor');
    ctrl.verify();
  });

  it('lists bills for the selected vendor', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const f = TestBed.createComponent(BillList);
    f.detectChanges();
    flushVendorsThenSelect(ctrl, svc);
    f.detectChanges();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills' && r.params.get('vendorId') === 'v1');
    req.flush({ items: [{ bill: { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-01', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Entered',
      lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] }, openBalance: 100, settlementStatus: 'Open' }],
      total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('B-1');
    expect(f.nativeElement.textContent).toContain('INV-9');
    ctrl.verify();
  });

  it('clicking a bill row navigates to its detail', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillList);
    f.detectChanges();
    flushVendorsThenSelect(ctrl, svc);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills').flush({ items: [{ bill: { id: 'b1',
      vendorId: 'v1', number: 'B-1', billDate: '2026-06-01', dueDate: null, vendorReference: null, memo: null,
      status: 'Entered', lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] },
      openBalance: 100, settlementStatus: 'Open' }], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    (f.nativeElement.querySelector('tbody tr') as HTMLElement).click();
    expect(nav).toHaveBeenCalledWith(['/payables/bills', 'b1']);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/bill-list.spec.ts`
Expected: FAIL — cannot resolve `./bill-list`.

- [ ] **Step 3: Create the component**

Create `bill-list.ts` (mirror of `invoice-list.ts`; vendor-scoped, `billTotal`, no status badge — inline status text, reuse `SettlementBadge`):

```typescript
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmPaginationImports } from '@spartan-ng/helm/pagination';
import { PayablesService } from '../../core/payables/payables.service';
import { BillView, SettlementFilter, billTotal } from '../../core/payables/payables';
import { PagedResponse } from '../../core/api/paged-response';
import { money, displayDate } from '../../core/format/display';
import { VendorSelect } from '../../shared/vendor-select';
import { SettlementBadge } from '../../shared/settlement-badge';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-bill-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, VendorSelect, SettlementBadge, ...HlmSelectImports, ...HlmTableImports, ...HlmPaginationImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Bills</h1>
        <app-vendor-select />
        <div hlmSelect [value]="settlement()" (valueChange)="onSettlementChange($event)" [itemToString]="settlementToLabel">
          <hlm-select-trigger class="w-36"><hlm-select-value placeholder="All" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            <hlm-select-item value="">All</hlm-select-item>
            <hlm-select-item value="open">Open</hlm-select-item>
            <hlm-select-item value="paid">Paid</hlm-select-item>
          </hlm-select-content>
        </div>
        <a hlmBtn size="sm" class="ms-auto" routerLink="/payables/bills/new"
           [class.pointer-events-none]="!vendorId()" [class.opacity-50]="!vendorId()">New bill</a>
      </div>

      @if (svc.vendors().length === 0) {
        <p class="text-muted-foreground text-sm">No vendors yet — <a routerLink="/payables/vendors" class="underline">add one first</a>.</p>
      } @else if (!vendorId()) {
        <p class="text-muted-foreground text-sm">Select a vendor to view bills.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (bills().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No bills found.</p>
        } @else {
          <div hlmTableContainer><table hlmTable>
            <thead hlmTHead><tr hlmTr>
              <th hlmTh>Number</th><th hlmTh>Bill date</th><th hlmTh>Due</th>
              <th hlmTh>Vendor ref</th><th hlmTh>Total</th><th hlmTh>Open</th><th hlmTh>Status</th>
            </tr></thead>
            <tbody hlmTBody>
              @for (v of bills(); track v.bill.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="openBill(v.bill.id)" (keydown.enter)="openBill(v.bill.id)">
                  <td hlmTd>{{ v.bill.number ?? '—' }}</td>
                  <td hlmTd>{{ fmtDate(v.bill.billDate) }}</td>
                  <td hlmTd>{{ v.bill.dueDate ? fmtDate(v.bill.dueDate) : '—' }}</td>
                  <td hlmTd>{{ v.bill.vendorReference ?? '—' }}</td>
                  <td hlmTd>{{ fmtMoney(calcTotal(v)) }}</td>
                  <td hlmTd>{{ fmtMoney(v.openBalance) }}</td>
                  <td hlmTd class="flex gap-1 flex-wrap items-center">
                    <span class="text-xs text-muted-foreground">{{ v.bill.status }}</span>
                    <app-settlement-badge [status]="v.settlementStatus" />
                  </td>
                </tr>
              }
            </tbody>
          </table></div>

          <div class="flex items-center justify-between text-sm text-muted-foreground">
            <span class="whitespace-nowrap">Page {{ currentPage() }} of {{ pageCount() }}</span>
            <nav hlmPagination aria-label="Bills pagination"><ul hlmPaginationContent>
              <li hlmPaginationItem><hlm-pagination-previous
                [class]="skip() === 0 ? 'pointer-events-none opacity-50' : ''" (click)="prevPage()" /></li>
              <li hlmPaginationItem><hlm-pagination-next
                [class]="currentPage() >= pageCount() ? 'pointer-events-none opacity-50' : ''" (click)="nextPage()" /></li>
            </ul></nav>
          </div>
        }
      }
    </div>
  `,
})
export class BillList {
  readonly svc = inject(PayablesService);
  private readonly router = inject(Router);

  readonly vendorId = this.svc.selectedVendorId;
  readonly settlement = signal<SettlementFilter | ''>('');
  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly listError = signal<string | null>(null);

  private readonly query = computed(() => ({
    vendorId: this.vendorId(), settlement: this.settlement() || undefined, skip: this.skip(), limit: this.limit(),
  }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      switchMap(q => {
        if (!q.vendorId) return of(null);
        this.listError.set(null);
        return this.svc.listBills({
          vendorId: q.vendorId, settlement: q.settlement as SettlementFilter | undefined, skip: q.skip, limit: q.limit,
        }).pipe(catchError(e => { this.listError.set(extractProblem(e).detail); return of(null); }));
      }),
    ),
    { initialValue: null as PagedResponse<BillView> | null },
  );

  readonly bills = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => {
    const p = this.page(); if (!p || p.total === 0) return 1; return Math.ceil(p.total / p.limit);
  });
  readonly currentPage = computed(() => {
    const p = this.page(); if (!p) return 1; return Math.floor(p.skip / p.limit) + 1;
  });

  readonly settlementToLabel = (v: string): string => v === 'open' ? 'Open' : v === 'paid' ? 'Paid' : 'All';

  constructor() {
    this.svc.load();
    effect(() => { this.vendorId(); this.skip.set(0); });
  }

  onSettlementChange(value: unknown): void {
    this.settlement.set((value as string ?? '') as SettlementFilter | '');
    this.skip.set(0);
  }

  openBill(id: string): void { void this.router.navigate(['/payables/bills', id]); }

  prevPage(): void { const s = this.skip(), l = this.limit(); if (s > 0) this.skip.set(Math.max(0, s - l)); }
  nextPage(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }

  calcTotal(v: BillView): number { return billTotal(v.bill.lines); }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/bill-list.spec.ts`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/bill-list.ts UI/Angular/src/app/features/payables/bill-list.spec.ts
git commit -m "feat(ui): payables BillList tab (vendor-scoped, row→detail)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 7: feature — BillEditor (create, per-line expense account)

**Files:**
- Create: `UI/Angular/src/app/features/payables/bill-editor.ts`
- Test: `UI/Angular/src/app/features/payables/bill-editor.spec.ts`

**Interfaces:**
- Consumes: `PayablesService` (`vendors`, `load`, `vendorName`, `selectedVendorId`, `draftBill`), `AccountsService` (`../../core/accounts/accounts.service` — `accounts`, `load`), `AccountResponse` (`../../core/accounts/account`), `billTotal`, `money`, `extractProblem`, `CurrencyInput` (`../../shared/currency-input`), Signal Forms, `Router`.
- Produces: `BillEditor` component, selector `app-bill-editor`. On save → `draftBill` → navigate `['/payables/bills', saved.id]`.

- [ ] **Step 1: Write the failing test**

Create `bill-editor.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillEditor } from './bill-editor';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { vi } from 'vitest';

describe('BillEditor', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    const ctrl = TestBed.inject(HttpTestingController);
    return ctrl;
  }

  function flushRefData(ctrl: HttpTestingController) {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush([
      { id: 'a1', number: '6100', name: 'Rent Expense', type: 'Expense', parentId: null, postable: true,
        requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true,
        normalSide: 'Debit', isTemporary: true },
      { id: 'c1', number: '1000', name: 'Cash', type: 'Asset', parentId: null, postable: true,
        requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true,
        normalSide: 'Debit', isTemporary: false },
    ]);
  }

  it('shows only postable active expense accounts in the line picker', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillEditor);
    f.detectChanges();
    flushRefData(ctrl);
    f.detectChanges();
    expect(f.componentInstance.expenseAccounts().map(a => a.id)).toEqual(['a1']);
    ctrl.verify();
  });

  it('posts a draft bill with a line and navigates to its detail', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillEditor);
    f.detectChanges();
    flushRefData(ctrl);
    f.detectChanges();

    const cmp = f.componentInstance;
    cmp.model.update(v => ({ ...v, vendorId: 'v1', billDate: '2026-06-30',
      lines: [{ lineId: 'L1', description: 'June rent', amount: 1200, expenseAccountId: 'a1' }] }));
    f.detectChanges();
    expect(cmp.canSave()).toBe(true);
    cmp.save();

    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills');
    expect(post.request.body).toEqual({ vendorId: 'v1', billDate: '2026-06-30', dueDate: null,
      vendorReference: null, memo: null, lines: [{ description: 'June rent', amount: 1200, expenseAccountId: 'a1' }] });
    post.flush({ id: 'b9', vendorId: 'v1', number: null, billDate: '2026-06-30', dueDate: null,
      vendorReference: null, memo: null, status: 'Draft', lines: post.request.body.lines });
    expect(nav).toHaveBeenCalledWith(['/payables/bills', 'b9']);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/bill-editor.spec.ts`
Expected: FAIL — cannot resolve `./bill-editor`.

- [ ] **Step 3: Create the component**

Create `bill-editor.ts` (header + line grid; each line picks an expense account; total = `billTotal`):

```typescript
import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { form, applyEach, required, FormField } from '@angular/forms/signals';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { PayablesService } from '../../core/payables/payables.service';
import { DraftBillRequest, billTotal } from '../../core/payables/payables';
import { AccountsService } from '../../core/accounts/accounts.service';
import { AccountResponse } from '../../core/accounts/account';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

interface LineModel { lineId: string; description: string; amount: number; expenseAccountId: string | null; }
interface BillFormValue {
  vendorId: string; billDate: string; dueDate: string | null;
  vendorReference: string | null; memo: string | null; lines: LineModel[];
}

const emptyLine = (): LineModel => ({ lineId: crypto.randomUUID(), description: '', amount: 0, expenseAccountId: null });

@Component({
  selector: 'app-bill-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, FormField, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-4xl">
      <h1 class="text-2xl font-bold">New bill</h1>

      <div class="flex flex-col gap-1">
        <label hlmLabel>Vendor</label>
        <div hlmSelect [value]="form.vendorId().value()" [itemToString]="vendorLabel"
             (valueChange)="form.vendorId().value.set($any($event))">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a vendor" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            @for (v of svc.vendors(); track v.id) { <hlm-select-item [value]="v.id">{{ v.name }}</hlm-select-item> }
          </hlm-select-content>
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1"><label hlmLabel>Bill date</label>
          <input hlmInput type="date" [formField]="form.billDate" /></div>
        <div class="flex flex-col gap-1"><label hlmLabel>Due date</label>
          <input hlmInput type="date" [value]="form.dueDate().value() ?? ''"
                 (change)="form.dueDate().value.set($any($event.target).value || null)" /></div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1"><label hlmLabel>Vendor reference</label>
          <input hlmInput type="text" [value]="form.vendorReference().value() ?? ''"
                 (input)="form.vendorReference().value.set($any($event.target).value || null)" /></div>
        <div class="flex flex-col gap-1"><label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="form.memo().value() ?? ''"
                 (input)="form.memo().value.set($any($event.target).value || null)" /></div>
      </div>

      <table class="w-full text-sm">
        <thead><tr class="text-left text-muted-foreground">
          <th class="py-1">Description</th><th class="pr-2">Expense account</th>
          <th class="text-right pr-5">Amount</th><th></th>
        </tr></thead>
        <tbody>
          @for (line of model().lines; track line.lineId; let i = $index) {
            <tr>
              <td class="py-1 pr-2"><input hlmInput type="text" [formField]="form.lines[i].description" /></td>
              <td class="pr-2">
                <div hlmSelect [value]="form.lines[i].expenseAccountId().value() ?? ''" [itemToString]="accountLabel"
                     (valueChange)="form.lines[i].expenseAccountId().value.set($any($event) || null)">
                  <hlm-select-trigger class="w-56"><hlm-select-value placeholder="Select account" /></hlm-select-trigger>
                  <hlm-select-content *hlmSelectPortal>
                    @for (a of expenseAccounts(); track a.id) {
                      <hlm-select-item [value]="a.id">{{ a.number }} · {{ a.name }}</hlm-select-item>
                    }
                  </hlm-select-content>
                </div>
              </td>
              <td class="pr-2"><div class="flex justify-end">
                <app-currency-input class="w-32" ariaLabel="Amount"
                     [value]="form.lines[i].amount().value()"
                     (valueChange)="form.lines[i].amount().value.set($event)" /></div></td>
              <td><button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine(i)">✕</button></td>
            </tr>
          }
        </tbody>
      </table>

      <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>

      <div class="text-right text-sm tabular-nums flex flex-col gap-1 w-56 ms-auto">
        <div class="flex justify-between font-semibold border-t border-border pt-1">
          <span>Total</span><span>{{ money(total()) }}</span></div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Save</button>
        <a hlmBtn variant="outline" routerLink="/payables">Cancel</a>
      </div>
    </div>
  `,
})
export class BillEditor {
  readonly svc = inject(PayablesService);
  readonly accountsSvc = inject(AccountsService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly model = signal<BillFormValue>({
    vendorId: this.svc.selectedVendorId() ?? '',
    billDate: new Date().toISOString().slice(0, 10),
    dueDate: null, vendorReference: null, memo: null, lines: [],
  });

  readonly form = form(this.model, (p) => {
    required(p.vendorId);
    required(p.billDate);
    applyEach(p.lines, (l) => { required(l.description); required(l.expenseAccountId); });
  });

  readonly expenseAccounts = computed<AccountResponse[]>(() =>
    this.accountsSvc.accounts().filter(a => a.type === 'Expense' && a.postable && a.active));

  readonly total = computed(() => billTotal(this.model().lines.map(l => ({ amount: l.amount }))));
  readonly canSave = computed(() =>
    this.form().valid() &&
    this.model().lines.length > 0 &&
    this.model().lines.every(l => l.amount > 0 && !!l.expenseAccountId) &&
    this.total() > 0);

  readonly vendorLabel = (id: string): string => this.svc.vendorName(id);
  readonly accountLabel = (id: string): string => {
    const a = this.accountsSvc.accounts().find(x => x.id === id);
    return a ? `${a.number} · ${a.name}` : id;
  };

  constructor() {
    this.svc.load();
    this.accountsSvc.load();
  }

  addLine(): void { this.model.update(v => ({ ...v, lines: [...v.lines, emptyLine()] })); }
  removeLine(i: number): void { this.model.update(v => ({ ...v, lines: v.lines.filter((_, idx) => idx !== i) })); }
  money(n: number): string { return fmtMoney(n); }

  private toRequest(): DraftBillRequest {
    const v = this.model();
    return {
      vendorId: v.vendorId, billDate: v.billDate, dueDate: v.dueDate,
      vendorReference: v.vendorReference, memo: v.memo,
      lines: v.lines.map(l => ({ description: l.description, amount: l.amount, expenseAccountId: l.expenseAccountId! })),
    };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true);
    this.message.set(null);
    this.svc.draftBill(this.toRequest()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => { this.busy.set(false); void this.router.navigate(['/payables/bills', saved.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/bill-editor.spec.ts`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/bill-editor.ts UI/Angular/src/app/features/payables/bill-editor.spec.ts
git commit -m "feat(ui): payables BillEditor (create, per-line expense account)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 8: feature — BillDetail (enter / void)

**Files:**
- Create: `UI/Angular/src/app/features/payables/bill-detail.ts`
- Test: `UI/Angular/src/app/features/payables/bill-detail.spec.ts`

**Interfaces:**
- Consumes: `PayablesService` (`getBill`, `enter`, `void`, `vendorName`, `load`), `AccountsService` (resolve account names), `billTotal`, `money`/`displayDate`, `SettlementBadge`, `extractProblem`, `ActivatedRoute`/`Router`.
- Produces: `BillDetail` component, selector `app-bill-detail`. Reads `:id`; Enter when `Draft`; Void (reason) when `Entered`.

- [ ] **Step 1: Write the failing test**

Create `bill-detail.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillDetail } from './bill-detail';
import { ClientContextService } from '../../core/client/client-context.service';

describe('BillDetail', () => {
  function setup(id = 'b1') {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } },
      ],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  function flushLoads(ctrl: HttpTestingController, status: string) {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush([
      { id: 'a1', number: '6100', name: 'Rent Expense', type: 'Expense', parentId: null, postable: true,
        requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true,
        normalSide: 'Debit', isTemporary: true }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/b1').flush({ bill: { id: 'b1', vendorId: 'v1',
      number: status === 'Draft' ? null : 'B-1', billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9',
      memo: null, status, lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] },
      openBalance: 1200, settlementStatus: 'Open' });
  }

  it('renders a draft bill and enters it', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillDetail);
    f.detectChanges();
    flushLoads(ctrl, 'Draft');
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Rent Expense');
    f.componentInstance.enter();
    const req = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/b1/enter');
    req.flush({ id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-30', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] });
    // reload after enter
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/b1').flush({ bill: { id: 'b1', vendorId: 'v1', number: 'B-1',
      billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9', memo: null, status: 'Entered',
      lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] }, openBalance: 1200, settlementStatus: 'Open' });
    ctrl.verify();
  });

  it('voids an entered bill with a reason', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillDetail);
    f.detectChanges();
    flushLoads(ctrl, 'Entered');
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.voidReason.set('duplicate');
    cmp.voidBill();
    const req = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/b1/void');
    expect(req.request.body).toEqual({ reason: 'duplicate' });
    req.flush({ id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-30', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Void', lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] });
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/b1').flush({ bill: { id: 'b1', vendorId: 'v1', number: 'B-1',
      billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9', memo: null, status: 'Void',
      lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] }, openBalance: 0, settlementStatus: 'Open' });
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/bill-detail.spec.ts`
Expected: FAIL — cannot resolve `./bill-detail`.

- [ ] **Step 3: Create the component**

Create `bill-detail.ts` (mirror of `invoice-detail.ts`, simplified — enter/void, resolve account names):

```typescript
import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { PayablesService } from '../../core/payables/payables.service';
import { BillView, billTotal } from '../../core/payables/payables';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { SettlementBadge } from '../../shared/settlement-badge';

@Component({
  selector: 'app-bill-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, SettlementBadge, ...HlmTableImports, HlmButton, ...HlmInputImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/payables" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Bills</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">{{ v.bill.number ?? 'Draft' }}</h1>
          <span class="text-xs text-muted-foreground">{{ v.bill.status }}</span>
          <app-settlement-badge [status]="v.settlementStatus" />
        </div>
        <div class="text-sm text-muted-foreground">
          {{ svc.vendorName(v.bill.vendorId) }} · Bill date {{ formatDate(v.bill.billDate) }}
          @if (v.bill.dueDate) { · Due {{ formatDate(v.bill.dueDate) }} }
          @if (v.bill.vendorReference) { · Ref {{ v.bill.vendorReference }} }
        </div>

        <div hlmTableContainer><table hlmTable>
          <thead hlmTHead><tr hlmTr>
            <th hlmTh>Description</th><th hlmTh>Account</th><th hlmTh class="text-right">Amount</th>
          </tr></thead>
          <tbody hlmTBody>
            @for (l of v.bill.lines; track $index) {
              <tr hlmTr>
                <td hlmTd>{{ l.description }}</td>
                <td hlmTd>{{ accountName(l.expenseAccountId) }}</td>
                <td hlmTd class="text-right tabular-nums">{{ money(l.amount) }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr hlmTr class="font-semibold border-double border-t-4 border-border">
              <td hlmTd colspan="2" class="text-right">Total</td>
              <td hlmTd class="text-right tabular-nums">{{ money(total()) }}</td>
            </tr>
            <tr hlmTr>
              <td hlmTd colspan="2" class="text-right text-muted-foreground">Open balance</td>
              <td hlmTd class="text-right tabular-nums">{{ money(v.openBalance) }}</td>
            </tr>
          </tfoot>
        </table></div>

        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

        @switch (v.bill.status) {
          @case ('Draft') {
            <div class="flex items-center gap-2">
              <button hlmBtn type="button" (click)="enter()" [disabled]="busy()">Enter</button>
            </div>
          }
          @case ('Entered') {
            <div class="flex items-center gap-2">
              <input hlmInput type="text" aria-label="Void reason" placeholder="Void reason"
                     [value]="voidReason()" (input)="voidReason.set($any($event.target).value)" />
              <button hlmBtn type="button" variant="outline" (click)="voidBill()" [disabled]="busy()">Void</button>
            </div>
          }
        }
      } @else {
        <p class="text-muted-foreground text-sm">Loading…</p>
      }
    </div>
  `,
})
export class BillDetail {
  readonly svc = inject(PayablesService);
  readonly accountsSvc = inject(AccountsService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<BillView | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly voidReason = signal('');

  readonly total = computed(() => this.view() ? billTotal(this.view()!.bill.lines) : 0);

  constructor() {
    this.svc.load();
    this.accountsSvc.load();
    this.reload();
  }

  reload(clearBusy = false): void {
    this.svc.getBill(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => { this.view.set(v); if (clearBusy) this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); if (clearBusy) this.busy.set(false); },
    });
  }

  accountName(id: string): string {
    const a = this.accountsSvc.accounts().find(x => x.id === id);
    return a ? `${a.number} · ${a.name}` : id;
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }

  enter(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.enter(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.reload(true); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  voidBill(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.void(this.id, this.voidReason() || null).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.reload(true); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/bill-detail.spec.ts`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/bill-detail.ts UI/Angular/src/app/features/payables/bill-detail.spec.ts
git commit -m "feat(ui): payables BillDetail (enter / void)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 9: feature — PayablesShell + routes wiring

**Files:**
- Create: `UI/Angular/src/app/features/payables/payables-shell.ts`
- Test: `UI/Angular/src/app/features/payables/payables-shell.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add imports + route block; exclude `/payables` from the placeholder filter)

**Interfaces:**
- Consumes: `RouterLink`, `RouterLinkActive`, `RouterOutlet`.
- Produces: `PayablesShell` component, selector `app-payables-shell`. Route tree under `/payables` (default → `bills`).

- [ ] **Step 1: Write the failing shell test**

Create `payables-shell.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { PayablesShell } from './payables-shell';

describe('PayablesShell', () => {
  it('renders Bills and Vendors tabs', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    });
    const f = TestBed.createComponent(PayablesShell);
    f.detectChanges();
    const tabs = f.nativeElement.textContent;
    expect(tabs).toContain('Bills');
    expect(tabs).toContain('Vendors');
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/payables-shell.spec.ts`
Expected: FAIL — cannot resolve `./payables-shell`.

- [ ] **Step 3: Create the shell**

Create `payables-shell.ts` (mirror of `receivables-shell.ts`, two tabs):

```typescript
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-payables-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="flex flex-col gap-0">
      <nav class="flex gap-1 border-b border-border px-4 pt-4">
        <a routerLink="bills"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-bills">Bills</a>
        <a routerLink="vendors"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-vendors">Vendors</a>
      </nav>
      <router-outlet />
    </div>
  `,
})
export class PayablesShell {}
```

- [ ] **Step 4: Run the shell test to verify it passes**

Run: `cd UI/Angular && npx vitest run src/app/features/payables/payables-shell.spec.ts`
Expected: PASS.

- [ ] **Step 5: Wire the routes**

In `app.routes.ts`, add imports (after the receivables import block, before `import { NAV }`):

```typescript
import { PayablesShell } from './features/payables/payables-shell';
import { VendorList } from './features/payables/vendor-list';
import { BillList } from './features/payables/bill-list';
import { BillEditor } from './features/payables/bill-editor';
import { BillDetail } from './features/payables/bill-detail';
```

Add the route block (immediately after the `receivables` block, before the placeholder spread):

```typescript
  { path: 'payables', component: PayablesShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'bills' },
    { path: 'bills', component: BillList },
    { path: 'bills/new', component: BillEditor },
    { path: 'bills/:id', component: BillDetail },
    { path: 'vendors', component: VendorList },
  ] },
```

Exclude `/payables` from the placeholder filter — change the `.includes([...])` array on the placeholder-spread line to add `'/payables'`:

```typescript
  ...NAV.filter(n => ![ '/dashboard', '/trial-balance', '/statements', '/accounts', '/receivables', '/payables' ].includes(n.path) && !n.path.startsWith('/journal')).map(n => ({ path: n.path.slice(1), component: Placeholder })),
```

- [ ] **Step 6: Verify the app compiles + full UI suite is green**

Run: `cd UI/Angular && npx tsc -p tsconfig.app.json --noEmit && npx vitest run`
Expected: type-check clean; ALL specs pass (existing + new payables specs).

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/features/payables/payables-shell.ts UI/Angular/src/app/features/payables/payables-shell.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): PayablesShell + /payables routes (Bills | Vendors)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review notes

- **Spec coverage:** backend `GET /vendors` + `ListAsync` (T1); core models (T2); service vendors+bills (T3); `<app-vendor-select>` (T4); Vendors tab (T5); Bills tab vendor-scoped (T6); bill editor with expense-account dropdown (T7); bill detail enter/void (T8); shell + routes + nav-unblock (T9). Deferred items (payments/credits/360/draft-edit/dev-stack env) correctly absent. All spec sections map to a task.
- **Type consistency:** `BillView { bill, openBalance, settlementStatus }`, `Bill.lines: BillLine{description,amount,expenseAccountId}`, `billTotal(Pick<BillLine,'amount'>[])`, `DraftBillRequest` field names match the backend `DraftBillRequest`/`BillBody` (`vendorId, billDate, dueDate, vendorReference, memo, lines`) and the C# `BillLineBody(Description, Amount, ExpenseAccountId)` → camelCase wire keys. `SettlementStatus` union reused structurally by `SettlementBadge`.
- **Wire-key check (digit-PascalCase trap):** none of the payables fields are digit-PascalCase, so the `d1To30` casing hazard does not apply here; standard camelCase holds.
