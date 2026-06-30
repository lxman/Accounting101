# Payables Vendor Credits UI — Slice P-C Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Credits tab to the Payables module UI — a vendor's available credit balance + a read-only list of credit applications, and an apply-credit editor that allocates available vendor credit across open bills — plus the one backend read endpoint.

**Architecture:** Mirror the P-B payments slice. Backend: add `GET /vendor-credit-applications?vendorId` (calls the existing `IBillPaymentStore.GetCreditApplicationsByVendorAsync`). Frontend: additive core models + service methods, two new components, a fourth shell tab + routes. Reuses `PaymentAllocation`/`AllocRow`/`autoAllocate` from P-B.

**Tech Stack:** ASP.NET Core minimal APIs + xUnit (backend); Angular 22 standalone components, signals, Spartan/helm UI, Vitest via `ng test` (frontend).

## Global Constraints

- Commit trailer on EVERY commit, exactly:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- UI test runner is **`ng test`** (`@angular/build:unit-test`), NOT raw vitest. Scoped run: `npx ng test --include="<spec path>" --watch=false`. NEVER create a `vitest.config.ts`.
- Stage ONLY the files each task changes — never `git add -A`/`commit -am`. The working tree has an unrelated `UI/Angular/src/app/core/api/environment.ts` (devClientId) change that must NOT be committed.
- Vendor credit applications are **vendor-scoped**: `GET /vendor-credit-applications` requires a `vendorId` query param (400 otherwise).
- The credits list is **read-only** — credit applications have **no void** endpoint; render no void button.
- The apply-credit editor has **no cash-amount field** — the allocatable pool is the vendor's available credit balance. `valid` = allocated > 0 && every allocation in `[0, openBalance]` && allocated ≤ available credit.
- Backend enforces: applying > available credit → reject; allocation ≤ bill open balance; allocations > 0. The UI mirrors these as the `valid` gate but the server is the backstop.
- All Angular components: `ChangeDetectionStrategy.OnPush`, standalone; `takeUntilDestroyed(destroyRef)` on every inline `.subscribe()`; `provideZonelessChangeDetection()` in specs.
- camelCase wire keys (no digit-PascalCase fields here).
- Branch for this slice off `master`; ff-merge + push + delete branch on "merge and push".

---

### Task 1: Backend — `GET /vendor-credit-applications`

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs` (add route + handler)
- Test (create): `Modules/Payables/Accounting101.Payables.Tests/VendorCreditApplicationListEndpointTests.cs`

**Interfaces:**
- Produces: `GET /clients/{clientId}/vendor-credit-applications?vendorId={guid}` → `200 IReadOnlyList<VendorCreditApplication>`; `400` if `vendorId` missing/empty; client-isolated.
- Consumes: existing `IBillPaymentStore.GetCreditApplicationsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken)`.

- [ ] **Step 1: Write the failing endpoint test**

Create `VendorCreditApplicationListEndpointTests.cs`. It records a real credit application (overpay a bill → vendor credit → apply it to a second bill), then GETs and asserts, mirroring `PayablesE2eTests`'s flow:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>Proves GET /clients/{clientId}/vendor-credit-applications?vendorId returns the vendor's
/// recorded credit applications, 400s without vendorId, and is client-isolated.</summary>
public sealed class VendorCreditApplicationListEndpointTests(PayablesHostFixture fixture)
    : IClassFixture<PayablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,        "2000", "Accounts Payable", "Liability", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,           "1000", "Cash",             "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId,  "1300", "Vendor Credits",   "Asset",     "Vendor");
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId,    "5200", "Rent Expense",     "Expense",   null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();

    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Lists_a_vendors_credit_applications()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // Bill 1 ($100), enter+approve, then overpay $150 (allocating $100) → $50 vendor credit.
        Bill bill1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", new DraftBillRequest(
            vendor.Id, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 100m, fixture.RentExpenseAccountId)]))).Content.ReadFromJsonAsync<Bill>())!;
        Bill entered1 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill1.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered1.Id);
        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor.Id, new DateOnly(2026, 3, 2), 150m, "check",
                [new Allocation(bill1.Id, 100m)]))).Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // Bill 2 ($40), enter+approve, then apply $40 of the vendor credit to it.
        Bill bill2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", new DraftBillRequest(
            vendor.Id, new DateOnly(2026, 4, 1), null, null, null,
            [new BillLineBody("Rent", 40m, fixture.RentExpenseAccountId)]))).Content.ReadFromJsonAsync<Bill>())!;
        Bill entered2 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill2.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered2.Id);
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
            new VendorCreditApplicationRequest(vendor.Id, new DateOnly(2026, 4, 2), [new Allocation(bill2.Id, 40m)])))
            .EnsureSuccessStatusCode();

        VendorCreditApplication[] apps = (await clerk.GetFromJsonAsync<VendorCreditApplication[]>(
            $"/clients/{clientId}/vendor-credit-applications?vendorId={vendor.Id}"))!;
        Assert.Single(apps);
        Assert.Equal(40m, apps[0].Allocations.Sum(a => a.Amount));
        Assert.Equal(vendor.Id, apps[0].VendorId);
    }

    [Fact]
    public async Task Requires_vendorId()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage response = await clerk.GetAsync($"/clients/{clientId}/vendor-credit-applications");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Is_client_isolated()
    {
        (Guid clientAId, _, HttpClient clerkA, _) = await fixture.SeedSodClientAsync();
        (Guid clientBId, _, HttpClient clerkB, _) = await fixture.SeedSodClientAsync();
        Vendor vendorB = (await (await clerkB.PostAsJsonAsync($"/clients/{clientBId}/vendors",
            new CreateVendorRequest("Other", null))).Content.ReadFromJsonAsync<Vendor>())!;

        VendorCreditApplication[] apps = (await clerkA.GetFromJsonAsync<VendorCreditApplication[]>(
            $"/clients/{clientAId}/vendor-credit-applications?vendorId={vendorB.Id}"))!;
        Assert.Empty(apps);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorCreditApplicationListEndpointTests"`
Expected: FAIL — `GET /vendor-credit-applications` returns 404/405 (route not mapped).

- [ ] **Step 3: Map the route + handler**

In `PayablesEndpoints.cs`, add the route registration after `clients.MapPost("/vendor-credit-applications", ApplyCredit);`:

```csharp
        clients.MapGet("/vendor-credit-applications", ListCreditApplications);
```

Add the handler (place it next to `ApplyCredit`), mirroring `ListBillPayments`:

```csharp
    private static async Task<IResult> ListCreditApplications(
        Guid clientId, Guid? vendorId, IBillPaymentStore store, CancellationToken cancellationToken)
    {
        if (vendorId is null || vendorId == Guid.Empty)
            return Results.Problem("vendorId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<VendorCreditApplication> apps = await store.GetCreditApplicationsByVendorAsync(clientId, vendorId.Value, cancellationToken);
        return Results.Ok(apps);
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorCreditApplicationListEndpointTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full payables suite (no regressions)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: all green (was 64; now 67).

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables
git commit -m "feat(payables): GET /vendor-credit-applications list endpoint

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: UI core — vendor-credit models + service methods

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.ts` (append types)
- Modify: `UI/Angular/src/app/core/payables/payables.service.ts` (append methods)
- Test: `UI/Angular/src/app/core/payables/payables.service.spec.ts` (append a case)

**Interfaces:**
- Produces types: `VendorCreditApplication { id, vendorId, date, allocations: PaymentAllocation[], voided }`, `ApplyVendorCreditRequest { vendorId, date, allocations: PaymentAllocation[] }`.
- Produces (on `PayablesService`):
  - `listVendorCreditApplications(vendorId: string): Observable<VendorCreditApplication[]>` → `GET /vendor-credit-applications?vendorId`
  - `applyVendorCredit(req: ApplyVendorCreditRequest): Observable<VendorCreditApplication>` → `POST /vendor-credit-applications`

- [ ] **Step 1: Append the failing service test**

In `payables.service.spec.ts`, add a new case (reuse the existing `setup()` → `{ svc, ctrl }`, client 'C1'):

```typescript
  it('lists and applies vendor credit applications', () => {
    const { svc, ctrl } = setup();

    svc.listVendorCreditApplications('v1').subscribe();
    const list = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/vendor-credit-applications');
    expect(list.request.params.get('vendorId')).toBe('v1');
    list.flush([]);

    svc.applyVendorCredit({ vendorId: 'v1', date: '2026-06-30',
      allocations: [{ targetId: 'b1', amount: 50 }] }).subscribe();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/vendor-credit-applications');
    expect(post.request.body).toEqual({ vendorId: 'v1', date: '2026-06-30', allocations: [{ targetId: 'b1', amount: 50 }] });
    post.flush({ id: 'ca1', vendorId: 'v1', date: '2026-06-30', allocations: [{ targetId: 'b1', amount: 50 }], voided: false });

    ctrl.verify();
  });
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/core/payables/payables.service.spec.ts" --watch=false`
Expected: FAIL — the two methods are not defined.

- [ ] **Step 3: Append the models**

Append to `payables.ts`:

```typescript
export interface VendorCreditApplication {
  id: string; vendorId: string; date: string; allocations: PaymentAllocation[]; voided: boolean;
}

export interface ApplyVendorCreditRequest {
  vendorId: string; date: string; allocations: PaymentAllocation[];
}
```

- [ ] **Step 4: Append the service methods**

Add `VendorCreditApplication, ApplyVendorCreditRequest` to the existing `./payables` import in `payables.service.ts`, then add these methods to the class (after `vendorCreditBalance(...)`):

```typescript
  listVendorCreditApplications(vendorId: string): Observable<VendorCreditApplication[]> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<VendorCreditApplication[]>(this.base('/vendor-credit-applications'),
      { params: new HttpParams().set('vendorId', vendorId) });
  }

  applyVendorCredit(req: ApplyVendorCreditRequest): Observable<VendorCreditApplication> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<VendorCreditApplication>(this.base('/vendor-credit-applications'), req);
  }
```

- [ ] **Step 5: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/core/payables/payables.service.spec.ts" --watch=false`
Expected: PASS (existing cases + the new one).

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.ts UI/Angular/src/app/core/payables/payables.service.ts UI/Angular/src/app/core/payables/payables.service.spec.ts
git commit -m "feat(ui): vendor-credit models + service (list/apply)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: feature — VendorCreditList tab

**Files:**
- Create: `UI/Angular/src/app/features/payables/vendor-credit-list.ts`
- Test: `UI/Angular/src/app/features/payables/vendor-credit-list.spec.ts`

**Interfaces:**
- Consumes: `PayablesService` (`vendors`, `load`, `selectedVendorId`, `listVendorCreditApplications`, `vendorCreditBalance`), `VendorCreditApplication`, `VendorSelect`, `money`/`displayDate`, `extractProblem`.
- Produces: `VendorCreditList` component, selector `app-vendor-credit-list`. "Apply credit" → `/payables/credits/new?vendor=<id>`, disabled when no vendor OR available credit is 0.

- [ ] **Step 1: Write the failing test**

Create `vendor-credit-list.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorCreditList } from './vendor-credit-list';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';

describe('VendorCreditList', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  it('prompts to select a vendor when none is selected', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(VendorCreditList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Select a vendor');
    ctrl.verify();
  });

  it('shows the credit balance and lists applications for the selected vendor', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const f = TestBed.createComponent(VendorCreditList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    svc.setSelectedVendor('v1');
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/credit-balance').flush({ vendorId: 'v1', creditBalance: 50 });
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/vendor-credit-applications' && r.params.get('vendorId') === 'v1');
    req.flush([{ id: 'ca1', vendorId: 'v1', date: '2026-04-02', allocations: [{ targetId: 'b2', amount: 40 }], voided: false }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Available credit');
    expect(f.nativeElement.textContent).toContain('40');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/vendor-credit-list.spec.ts" --watch=false`
Expected: FAIL — cannot resolve `./vendor-credit-list`.

- [ ] **Step 3: Create the component**

Create `vendor-credit-list.ts`:

```typescript
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { PayablesService } from '../../core/payables/payables.service';
import { VendorCreditApplication } from '../../core/payables/payables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { VendorSelect } from '../../shared/vendor-select';

@Component({
  selector: 'app-vendor-credit-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, VendorSelect],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Credits</h1>
        <app-vendor-select />
        @if (vendorId()) {
          <span class="text-sm text-muted-foreground">Available credit: <span class="tabular-nums font-semibold text-foreground">{{ fmtMoney(balance()) }}</span></span>
        }
        <a hlmBtn size="sm" class="ms-auto"
           routerLink="/payables/credits/new"
           [queryParams]="{ vendor: vendorId() }"
           [class.pointer-events-none]="!vendorId() || balance() <= 0"
           [class.opacity-50]="!vendorId() || balance() <= 0">
          Apply credit
        </a>
      </div>

      @if (svc.vendors().length === 0) {
        <p class="text-muted-foreground text-sm">No vendors yet — <a routerLink="/payables/vendors" class="underline">add one first</a>.</p>
      } @else if (!vendorId()) {
        <p class="text-muted-foreground text-sm">Select a vendor to view credits.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (applications().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No credit applications recorded.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr><th hlmTh>Date</th><th hlmTh>Applied</th><th hlmTh>Bills</th><th hlmTh>Status</th></tr>
              </thead>
              <tbody hlmTBody>
                @for (c of applications(); track c.id) {
                  <tr hlmTr [class.opacity-50]="c.voided">
                    <td hlmTd>{{ fmtDate(c.date) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(applied(c)) }}</td>
                    <td hlmTd class="tabular-nums">{{ c.allocations.length }}</td>
                    <td hlmTd>{{ c.voided ? 'Voided' : 'Active' }}</td>
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
export class VendorCreditList {
  readonly svc = inject(PayablesService);
  readonly vendorId = this.svc.selectedVendorId;
  readonly listError = signal<string | null>(null);

  readonly balance = toSignal(
    toObservable(this.vendorId).pipe(
      switchMap(vid => vid ? this.svc.vendorCreditBalance(vid).pipe(catchError(() => of(0))) : of(0)),
    ),
    { initialValue: 0 },
  );

  readonly applications = toSignal(
    toObservable(this.vendorId).pipe(
      switchMap(vid => {
        if (!vid) return of([] as VendorCreditApplication[]);
        this.listError.set(null);
        return this.svc.listVendorCreditApplications(vid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as VendorCreditApplication[]); }),
        );
      }),
    ),
    { initialValue: [] as VendorCreditApplication[] },
  );

  constructor() { this.svc.load(); }

  applied(c: VendorCreditApplication): number { return c.allocations.reduce((s, a) => s + a.amount, 0); }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/vendor-credit-list.spec.ts" --watch=false`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/vendor-credit-list.ts UI/Angular/src/app/features/payables/vendor-credit-list.spec.ts
git commit -m "feat(ui): payables VendorCreditList tab (balance + applications)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: feature — VendorCreditApplyEditor

**Files:**
- Create: `UI/Angular/src/app/features/payables/vendor-credit-apply-editor.ts`
- Test: `UI/Angular/src/app/features/payables/vendor-credit-apply-editor.spec.ts`

**Interfaces:**
- Consumes: `PayablesService` (`load`, `vendorName`, `listBills`, `applyVendorCredit`, `vendorCreditBalance`), `AllocRow`/`autoAllocate` (`../../core/payables/payables`), `CurrencyInput`, `money`/`displayDate`, `extractProblem`, `ActivatedRoute`/`Router`.
- Produces: `VendorCreditApplyEditor` component, selector `app-vendor-credit-apply-editor`. Reads `?vendor=` (required → redirect `/payables` if absent). Save → `applyVendorCredit` → navigate `/payables/credits`.

- [ ] **Step 1: Write the failing test**

Create `vendor-credit-apply-editor.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorCreditApplyEditor } from './vendor-credit-apply-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { vi } from 'vitest';

describe('VendorCreditApplyEditor', () => {
  function setup(vendor = 'v1') {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: (k: string) => k === 'vendor' ? vendor : null } } } },
      ],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  function flushInit(ctrl: HttpTestingController) {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/credit-balance').flush({ vendorId: 'v1', creditBalance: 50 });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills').flush({
      items: [{ bill: { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-05-01', dueDate: null,
        vendorReference: null, memo: null, status: 'Entered',
        lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] }, openBalance: 100, settlementStatus: 'Open' }],
      total: 1, skip: 0, limit: 200 });
  }

  it('auto-allocates the available credit oldest-first on load', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(VendorCreditApplyEditor);
    f.detectChanges();
    flushInit(ctrl);
    f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.available()).toBe(50);
    expect(cmp.rows()[0].allocation).toBe(50); // 50 credit, bill open 100 → 50 applied
    expect(cmp.allocated()).toBe(50);
    expect(cmp.valid()).toBe(true);
    ctrl.verify();
  });

  it('applies the credit and navigates to the credits list', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(VendorCreditApplyEditor);
    f.detectChanges();
    flushInit(ctrl);
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.save();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/vendor-credit-applications');
    expect(post.request.body).toEqual({ vendorId: 'v1', date: cmp.date(), allocations: [{ targetId: 'b1', amount: 50 }] });
    post.flush({ id: 'ca9', vendorId: 'v1', date: cmp.date(), allocations: [{ targetId: 'b1', amount: 50 }], voided: false });
    expect(nav).toHaveBeenCalledWith(['/payables/credits']);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/vendor-credit-apply-editor.spec.ts" --watch=false`
Expected: FAIL — cannot resolve `./vendor-credit-apply-editor`.

- [ ] **Step 3: Create the component** (adapted from `bill-payment-editor.ts`; no amount field, pool = available credit)

Create `vendor-credit-apply-editor.ts`:

```typescript
import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayablesService } from '../../core/payables/payables.service';
import { AllocRow, autoAllocate } from '../../core/payables/payables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

@Component({
  selector: 'app-vendor-credit-apply-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <h1 class="text-2xl font-bold">Apply vendor credit</h1>
      <p class="text-sm text-muted-foreground">{{ svc.vendorName(vendorId!) }}</p>
      <p class="text-sm text-muted-foreground">Available credit: <span class="tabular-nums font-semibold text-foreground">{{ money(available()) }}</span></p>

      <div class="grid grid-cols-2 gap-4 max-w-sm">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
      </div>

      @if (rows().length === 0) {
        <p class="text-sm text-muted-foreground">No open bills for this vendor — nothing to apply credit to.</p>
      } @else {
        <table class="w-full text-sm">
          <thead>
            <tr class="text-left text-muted-foreground">
              <th class="py-1">Bill</th><th>Bill date</th>
              <th class="text-right pr-5">Open</th><th class="text-right pr-5">Apply</th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.billId; let i = $index) {
              <tr>
                <td class="py-1">{{ r.number ?? '—' }}</td>
                <td>{{ formatDate(r.billDate) }}</td>
                <td class="text-right tabular-nums pr-5">{{ money(r.openBalance) }}</td>
                <td class="pr-2">
                  <div class="flex justify-end">
                    <app-currency-input class="w-32" [ariaLabel]="'Apply to ' + (r.number ?? r.billId)"
                         [value]="r.allocation" (valueChange)="onRow(i, $event)" />
                  </div>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }

      <div class="text-right text-sm tabular-nums flex flex-col gap-1 w-72 ms-auto">
        <div class="flex justify-between"><span>Allocated</span><span>{{ money(allocated()) }}</span></div>
        <div class="flex justify-between" [class.text-destructive]="allocated() > available()">
          <span>Remaining credit</span><span>{{ money(remaining()) }}</span>
        </div>
      </div>

      <p class="text-xs text-muted-foreground">
        Applying credit posts an entry that needs approval before it affects the statements.
        The bill's open balance updates immediately.
      </p>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Apply credit</button>
        <a hlmBtn variant="outline" routerLink="/payables/credits">Cancel</a>
      </div>
    </div>
  `,
})
export class VendorCreditApplyEditor {
  readonly svc = inject(PayablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly vendorId = this.route.snapshot.queryParamMap.get('vendor');

  readonly available = signal(0);
  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly rows = signal<AllocRow[]>([]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly allocated = computed(() => Math.round(this.rows().reduce((s, r) => s + r.allocation, 0) * 100) / 100);
  readonly remaining = computed(() => Math.round((this.available() - this.allocated()) * 100) / 100);
  readonly valid = computed(() =>
    this.allocated() > 0 &&
    this.rows().every(r => r.allocation >= 0 && r.allocation <= r.openBalance) &&
    this.allocated() <= this.available());

  constructor() {
    if (!this.vendorId) { void this.router.navigate(['/payables']); return; }
    this.svc.load();
    this.svc.vendorCreditBalance(this.vendorId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(b => {
      this.available.set(b);
      this.rows.update(rs => autoAllocate(b, rs));
    });
    this.svc.listBills({ vendorId: this.vendorId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(page => {
        const rows: AllocRow[] = page.items.map(v => ({
          billId: v.bill.id, number: v.bill.number, billDate: v.bill.billDate,
          openBalance: v.openBalance, allocation: 0,
        }));
        this.rows.set(autoAllocate(this.available(), rows));
      });
  }

  onRow(i: number, v: number): void { this.rows.update(rs => rs.map((r, idx) => idx === i ? { ...r, allocation: v } : r)); }

  save(): void {
    if (!this.valid() || !this.vendorId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.applyVendorCredit({
      vendorId: this.vendorId, date: this.date(),
      allocations: this.rows().filter(r => r.allocation > 0).map(r => ({ targetId: r.billId, amount: r.allocation })),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/payables/credits']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

Note: the two init subscriptions (credit balance, open bills) can arrive in either order; each calls `autoAllocate(available, rows)` against the current signals, so the final state is correct regardless of arrival order (the balance handler re-allocates existing rows; the bills handler allocates with the current balance).

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/vendor-credit-apply-editor.spec.ts" --watch=false`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/vendor-credit-apply-editor.ts UI/Angular/src/app/features/payables/vendor-credit-apply-editor.spec.ts
git commit -m "feat(ui): payables VendorCreditApplyEditor (allocate available credit to bills)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: feature — Credits tab + routes

**Files:**
- Modify: `UI/Angular/src/app/features/payables/payables-shell.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`
- Test: `UI/Angular/src/app/features/payables/payables-shell.spec.ts` (update the tab assertion)

**Interfaces:**
- Consumes: `VendorCreditList`, `VendorCreditApplyEditor`.
- Produces: a Credits tab in the shell (order Bills | Payments | Vendors | Credits); `/payables/credits` → VendorCreditList, `/payables/credits/new` → VendorCreditApplyEditor.

- [ ] **Step 1: Update the failing shell test**

In `payables-shell.spec.ts`, update the render assertion to require the Credits tab too:

```typescript
  it('renders Bills, Payments, Vendors and Credits tabs', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([])] });
    const f = TestBed.createComponent(PayablesShell);
    f.detectChanges();
    const tabs = f.nativeElement.textContent;
    expect(tabs).toContain('Bills');
    expect(tabs).toContain('Payments');
    expect(tabs).toContain('Vendors');
    expect(tabs).toContain('Credits');
  });
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/payables-shell.spec.ts" --watch=false`
Expected: FAIL — no "Credits" tab.

- [ ] **Step 3: Add the Credits tab to the shell**

In `payables-shell.ts`, add the Credits tab anchor AFTER the Vendors anchor (order Bills | Payments | Vendors | Credits):

```html
        <a routerLink="credits"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-credits">Credits</a>
```

- [ ] **Step 4: Wire the routes**

In `app.routes.ts`, add imports near the other payables imports:

```typescript
import { VendorCreditList } from './features/payables/vendor-credit-list';
import { VendorCreditApplyEditor } from './features/payables/vendor-credit-apply-editor';
```

In the `payables` children array, add the two credit routes (after `vendors`):

```typescript
    { path: 'credits', component: VendorCreditList },
    { path: 'credits/new', component: VendorCreditApplyEditor },
```

- [ ] **Step 5: Run the shell test + full suite + type-check**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/payables-shell.spec.ts" --watch=false`
Expected: PASS.

Then the whole gate:

Run: `cd UI/Angular && npx tsc -p tsconfig.app.json --noEmit && npx ng test --watch=false`
Expected: tsc clean; ALL specs pass (existing + new vendor-credit specs). Report totals.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/payables/payables-shell.ts UI/Angular/src/app/features/payables/payables-shell.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): Payables Credits tab + routes

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review notes

- **Spec coverage:** backend `GET /vendor-credit-applications` (T1); core models + service list/apply (T2); Credits tab with balance + applications (T3); apply-credit editor, no amount field, pool = available credit (T4); shell Credits tab + routes (T5). Deferred items (vendor 360, draft edit) correctly absent.
- **Models-have-no-logic:** the two new types carry no runtime behavior, so they're folded into the service task (T2) rather than a contentless test task; the service spec exercises them via the wire shapes.
- **Type consistency:** `VendorCreditApplication { id, vendorId, date, allocations: PaymentAllocation[], voided }` and `ApplyVendorCreditRequest { vendorId, date, allocations }` match the backend `VendorCreditApplicationRequest(VendorId, Date, Allocations)` and the `VendorCreditApplication` domain type (camelCase wire keys). `AllocRow`/`autoAllocate`/`PaymentAllocation` reused from P-B with identical signatures. Editor `valid` gate = allocated>0 && per-row ≤ openBalance && allocated ≤ available.
- **Read-only list:** no void button (backend has no void for credit applications) — consistent with the constraint and AR's treatment of credit-application.
- **No digit-PascalCase fields** on the wire, so the camelCase trap doesn't apply.
