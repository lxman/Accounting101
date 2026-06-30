# Payables Payments UI — Slice P-B Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add vendor payments to the Payables module UI — a Payments tab (list), a vendor-level BillPaymentEditor (record cash against open bills, oldest-first auto-allocate, overpay → vendor credit), and an Applied-payments section on BillDetail (void a payment) — plus the one backend read endpoint.

**Architecture:** Faithful mirror of the shipped Receivables AR-payments slice. Backend: add `GET /bill-payments?vendorId` (calls the existing `IBillPaymentStore.GetPaymentsByVendorAsync`). Frontend: additive to `core/payables` (models + service methods), two new components, one modified component (BillDetail), and a third shell tab + routes.

**Tech Stack:** ASP.NET Core minimal APIs + xUnit (backend); Angular 22 standalone components, signals, Spartan/helm UI, Vitest via `ng test` (frontend).

## Global Constraints

- Commit trailer on EVERY commit, exactly:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- UI test runner is **`ng test`** (`@angular/build:unit-test`), NOT raw vitest. Scoped run: `npx ng test --include="<spec path>" --watch=false`. NEVER create a `vitest.config.ts`.
- Bill payments are **vendor-scoped**: `GET /bill-payments` requires a `vendorId` query param (400 otherwise).
- Overpayment (allocations sum < amount) → the remainder becomes **vendor credit** server-side; the UI surfaces the number but does not manage credits (deferred).
- Recording a payment posts the cash entry as **PendingApproval** (needs an approver before it affects the statements); the bill's open balance updates immediately. The editor's helper copy reflects this (mirror of AR).
- Payment **void** is exercised on **BillDetail** (applied-payments section), NOT in the payment list — the list shows Active/Voided status only (mirror of AR `PaymentList`).
- All Angular components: `ChangeDetectionStrategy.OnPush`, standalone; `takeUntilDestroyed(destroyRef)` on every inline `.subscribe()`; `provideZonelessChangeDetection()` in specs.
- camelCase wire keys (no digit-PascalCase fields here).
- Branch for this slice off `master`; ff-merge + push + delete branch on "merge and push".

---

### Task 1: Backend — `GET /bill-payments`

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs` (add route + handler)
- Test (create): `Modules/Payables/Accounting101.Payables.Tests/BillPaymentListEndpointTests.cs`

**Interfaces:**
- Produces: `GET /clients/{clientId}/bill-payments?vendorId={guid}` → `200 IReadOnlyList<BillPayment>` (the vendor's payments); `400` if `vendorId` missing/empty; client-isolated.
- Consumes: existing `IBillPaymentStore.GetPaymentsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken)`.

- [ ] **Step 1: Write the failing endpoint test**

Create `BillPaymentListEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>Proves GET /clients/{clientId}/bill-payments?vendorId returns the vendor's recorded
/// payments, 400s without vendorId, and is client-isolated.</summary>
public sealed class BillPaymentListEndpointTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task Lists_a_vendors_recorded_payments()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,          "1000", "Cash",           "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId, "1300", "Vendor Credits", "Asset", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,       "2000", "Accounts Payable","Liability", "Vendor");

        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // A pure prepayment (no allocations) → full amount becomes vendor credit; no bill needed.
        RecordBillPaymentRequest req = new(vendor.Id, new DateOnly(2026, 3, 1), 500m, "check", []);
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req)).EnsureSuccessStatusCode();

        BillPayment[] payments = (await clerk.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientId}/bill-payments?vendorId={vendor.Id}"))!;
        Assert.Single(payments);
        Assert.Equal(500m, payments[0].Amount);
        Assert.Equal(vendor.Id, payments[0].VendorId);
    }

    [Fact]
    public async Task Requires_vendorId()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage response = await clerk.GetAsync($"/clients/{clientId}/bill-payments");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Is_client_isolated()
    {
        (Guid clientAId, _, HttpClient clerkA, _) = await fixture.SeedSodClientAsync();
        (Guid clientBId, _, HttpClient clerkB, _) = await fixture.SeedSodClientAsync();
        Vendor vendorB = (await (await clerkB.PostAsJsonAsync($"/clients/{clientBId}/vendors",
            new CreateVendorRequest("Other", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // Client A asks for client B's vendor id → empty (A has no such payments).
        BillPayment[] payments = (await clerkA.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientAId}/bill-payments?vendorId={vendorB.Id}"))!;
        Assert.Empty(payments);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~BillPaymentListEndpointTests"`
Expected: FAIL — `GET /bill-payments` returns 404/405 (route not mapped).

- [ ] **Step 3: Map the route + handler**

In `PayablesEndpoints.cs`, add the route registration after `clients.MapPost("/bill-payments", RecordPayment);`:

```csharp
        clients.MapGet("/bill-payments", ListBillPayments);
```

Add the handler (place it next to `RecordPayment`), mirroring the receivables `ListPayments` but injecting the store directly:

```csharp
    private static async Task<IResult> ListBillPayments(
        Guid clientId, Guid? vendorId, IBillPaymentStore store, CancellationToken cancellationToken)
    {
        if (vendorId is null || vendorId == Guid.Empty)
            return Results.Problem("vendorId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<BillPayment> payments = await store.GetPaymentsByVendorAsync(clientId, vendorId.Value, cancellationToken);
        return Results.Ok(payments);
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~BillPaymentListEndpointTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full payables suite (no regressions)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: all green (was 61; now 64).

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables
git commit -m "feat(payables): GET /bill-payments list endpoint

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: UI core — payment models + `autoAllocate`

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.ts` (append)
- Test: `UI/Angular/src/app/core/payables/payables.spec.ts` (append cases)

**Interfaces:**
- Produces: `PaymentAllocation`, `BillPayment`, `RecordBillPaymentRequest`, `AllocRow`, and `autoAllocate(amount, rows): AllocRow[]`.

- [ ] **Step 1: Append the failing test cases**

In `payables.spec.ts`, add an import and a new describe block:

```typescript
import { billTotal, autoAllocate, AllocRow } from './payables';

describe('autoAllocate', () => {
  const rows = (): AllocRow[] => [
    { billId: 'b1', number: 'B-1', billDate: '2026-01-01', openBalance: 100, allocation: 0 },
    { billId: 'b2', number: 'B-2', billDate: '2026-02-01', openBalance: 50, allocation: 0 },
  ];
  it('fills oldest-first, capped at each open balance', () => {
    const out = autoAllocate(120, rows());
    expect(out.map(r => r.allocation)).toEqual([100, 20]);
  });
  it('leaves a remainder unallocated when amount exceeds total open', () => {
    const out = autoAllocate(200, rows());
    expect(out.map(r => r.allocation)).toEqual([100, 50]); // 50 remainder → vendor credit
  });
  it('allocates nothing for a zero amount', () => {
    expect(autoAllocate(0, rows()).map(r => r.allocation)).toEqual([0, 0]);
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/core/payables/payables.spec.ts" --watch=false`
Expected: FAIL — `autoAllocate`/`AllocRow` not exported.

- [ ] **Step 3: Append the models + helper**

Append to `payables.ts`:

```typescript
export interface PaymentAllocation { targetId: string; amount: number; }

export interface BillPayment {
  id: string; vendorId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[]; voided: boolean;
}

export interface RecordBillPaymentRequest {
  vendorId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[];
}

/** One open bill the user can apply cash to in the payment editor. */
export interface AllocRow {
  billId: string; number: string | null; billDate: string; openBalance: number; allocation: number;
}

/** Distribute `amount` across rows in their given (oldest-first) order, each capped at its open
 *  balance. Returns new rows; any remainder beyond the rows' open balances is left unallocated
 *  (→ vendor credit). Mirror of the receivables helper. */
export function autoAllocate(amount: number, rows: readonly AllocRow[]): AllocRow[] {
  let remaining = Math.max(0, amount);
  return rows.map(r => {
    const take = Math.min(r.openBalance, remaining);
    remaining = Math.round((remaining - take) * 100) / 100;
    return { ...r, allocation: Math.round(take * 100) / 100 };
  });
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/core/payables/payables.spec.ts" --watch=false`
Expected: PASS (the existing billTotal cases + 3 new autoAllocate cases).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.ts UI/Angular/src/app/core/payables/payables.spec.ts
git commit -m "feat(ui): payables payment models + autoAllocate

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: UI core — PayablesService payment methods

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.service.ts` (append methods)
- Test: `UI/Angular/src/app/core/payables/payables.service.spec.ts` (append cases)

**Interfaces:**
- Consumes: `BillPayment`, `RecordBillPaymentRequest` from `./payables`.
- Produces (on `PayablesService`):
  - `listBillPayments(vendorId: string): Observable<BillPayment[]>` → `GET /bill-payments?vendorId`
  - `recordBillPayment(req: RecordBillPaymentRequest): Observable<BillPayment>` → `POST /bill-payments`
  - `voidBillPayment(id: string, reason?: string | null): Observable<BillPayment>` → `POST /bill-payments/{id}/void`
  - `vendorCreditBalance(vendorId: string): Observable<number>` → `GET /vendors/{id}/credit-balance`

- [ ] **Step 1: Append the failing test cases**

In `payables.service.spec.ts`, add a new describe block (reuse the file's existing `setup()` helper that selects client 'C1' and returns `{ svc, ctrl }`):

```typescript
  it('lists, records, and voids bill payments; reads vendor credit balance', () => {
    const { svc, ctrl } = setup();

    svc.listBillPayments('v1').subscribe();
    const list = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments');
    expect(list.request.params.get('vendorId')).toBe('v1');
    list.flush([]);

    svc.recordBillPayment({ vendorId: 'v1', date: '2026-06-30', amount: 100, method: 'check',
      allocations: [{ targetId: 'b1', amount: 80 }] }).subscribe();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bill-payments');
    expect(post.request.body).toEqual({ vendorId: 'v1', date: '2026-06-30', amount: 100, method: 'check',
      allocations: [{ targetId: 'b1', amount: 80 }] });
    post.flush({ id: 'p1', vendorId: 'v1', date: '2026-06-30', amount: 100, method: 'check',
      allocations: [{ targetId: 'b1', amount: 80 }], voided: false });

    svc.voidBillPayment('p1', 'oops').subscribe();
    const v = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bill-payments/p1/void');
    expect(v.request.body).toEqual({ reason: 'oops' });
    v.flush({});

    svc.vendorCreditBalance('v1').subscribe(bal => expect(bal).toBe(25));
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/credit-balance').flush({ vendorId: 'v1', creditBalance: 25 });

    ctrl.verify();
  });
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/core/payables/payables.service.spec.ts" --watch=false`
Expected: FAIL — the four methods are not defined.

- [ ] **Step 3: Append the service methods**

Add the import for `BillPayment, RecordBillPaymentRequest` to the existing `./payables` import in `payables.service.ts`, then add these methods to the class (after `void(...)`):

```typescript
  listBillPayments(vendorId: string): Observable<BillPayment[]> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<BillPayment[]>(this.base('/bill-payments'), { params: new HttpParams().set('vendorId', vendorId) });
  }

  recordBillPayment(req: RecordBillPaymentRequest): Observable<BillPayment> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<BillPayment>(this.base('/bill-payments'), req);
  }

  voidBillPayment(id: string, reason?: string | null): Observable<BillPayment> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.post<BillPayment>(this.base(`/bill-payments/${id}/void`), { reason: reason ?? null });
  }

  vendorCreditBalance(vendorId: string): Observable<number> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<{ creditBalance: number }>(this.base(`/vendors/${vendorId}/credit-balance`))
      .pipe(map(r => r.creditBalance));
  }
```

If `map` is not already imported from `rxjs` in this file, add it to the rxjs import (the file already imports `tap`; change to `import { EMPTY, Observable, map, tap } from 'rxjs';`).

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/core/payables/payables.service.spec.ts" --watch=false`
Expected: PASS (existing cases + the new one).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.service.ts UI/Angular/src/app/core/payables/payables.service.spec.ts
git commit -m "feat(ui): PayablesService payment methods (list/record/void/credit-balance)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: feature — BillPaymentList tab

**Files:**
- Create: `UI/Angular/src/app/features/payables/bill-payment-list.ts`
- Test: `UI/Angular/src/app/features/payables/bill-payment-list.spec.ts`

**Interfaces:**
- Consumes: `PayablesService` (`vendors`, `load`, `selectedVendorId`, `listBillPayments`), `BillPayment`, `VendorSelect`, `money`/`displayDate`, `extractProblem`.
- Produces: `BillPaymentList` component, selector `app-bill-payment-list`. "Record payment" → `/payables/payments/new?vendor=<id>`.

- [ ] **Step 1: Write the failing test**

Create `bill-payment-list.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillPaymentList } from './bill-payment-list';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';

describe('BillPaymentList', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  it('prompts to select a vendor when none is selected', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillPaymentList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Select a vendor');
    ctrl.verify();
  });

  it('lists the selected vendor\'s payments', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const f = TestBed.createComponent(BillPaymentList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    svc.setSelectedVendor('v1');
    f.detectChanges();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments' && r.params.get('vendorId') === 'v1');
    req.flush([{ id: 'p1', vendorId: 'v1', date: '2026-06-01', amount: 100, method: 'check',
      allocations: [{ targetId: 'b1', amount: 80 }], voided: false }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('check');
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/bill-payment-list.spec.ts" --watch=false`
Expected: FAIL — cannot resolve `./bill-payment-list`.

- [ ] **Step 3: Create the component** (mirror of `payment-list.ts`)

Create `bill-payment-list.ts`:

```typescript
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { PayablesService } from '../../core/payables/payables.service';
import { BillPayment } from '../../core/payables/payables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { VendorSelect } from '../../shared/vendor-select';

@Component({
  selector: 'app-bill-payment-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, VendorSelect],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Payments</h1>
        <app-vendor-select />
        <a hlmBtn size="sm" class="ms-auto"
           routerLink="/payables/payments/new"
           [queryParams]="{ vendor: vendorId() }"
           [class.pointer-events-none]="!vendorId()"
           [class.opacity-50]="!vendorId()">
          Record payment
        </a>
      </div>

      @if (svc.vendors().length === 0) {
        <p class="text-muted-foreground text-sm">No vendors yet — <a routerLink="/payables/vendors" class="underline">add one first</a>.</p>
      } @else if (!vendorId()) {
        <p class="text-muted-foreground text-sm">Select a vendor to view payments.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (payments().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No payments recorded.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>Date</th><th hlmTh>Amount</th><th hlmTh>Method</th>
                  <th hlmTh>Allocated</th><th hlmTh>Unapplied</th><th hlmTh>Status</th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (p of payments(); track p.id) {
                  <tr hlmTr [class.opacity-50]="p.voided">
                    <td hlmTd>{{ fmtDate(p.date) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(p.amount) }}</td>
                    <td hlmTd>{{ p.method ?? '—' }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(allocated(p)) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(p.amount - allocated(p)) }}</td>
                    <td hlmTd>{{ p.voided ? 'Voided' : 'Active' }}</td>
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
export class BillPaymentList {
  readonly svc = inject(PayablesService);
  readonly vendorId = this.svc.selectedVendorId;
  readonly listError = signal<string | null>(null);

  readonly payments = toSignal(
    toObservable(this.vendorId).pipe(
      switchMap(vid => {
        if (!vid) return of([] as BillPayment[]);
        this.listError.set(null);
        return this.svc.listBillPayments(vid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as BillPayment[]); }),
        );
      }),
    ),
    { initialValue: [] as BillPayment[] },
  );

  constructor() { this.svc.load(); }

  allocated(p: BillPayment): number { return p.allocations.reduce((s, a) => s + a.amount, 0); }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/bill-payment-list.spec.ts" --watch=false`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/bill-payment-list.ts UI/Angular/src/app/features/payables/bill-payment-list.spec.ts
git commit -m "feat(ui): payables BillPaymentList tab (vendor-scoped)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: feature — BillPaymentEditor

**Files:**
- Create: `UI/Angular/src/app/features/payables/bill-payment-editor.ts`
- Test: `UI/Angular/src/app/features/payables/bill-payment-editor.spec.ts`

**Interfaces:**
- Consumes: `PayablesService` (`load`, `vendorName`, `listBills`, `recordBillPayment`, `vendorCreditBalance`), `AllocRow`/`autoAllocate` (`../../core/payables/payables`), `CurrencyInput`, `money`/`displayDate`, `extractProblem`, `ActivatedRoute`/`Router`.
- Produces: `BillPaymentEditor` component, selector `app-bill-payment-editor`. Reads `?vendor=` (required; redirects to `/payables` if absent) and optional `?bill=` (focus). Save → `recordBillPayment` → navigate `/payables/payments`.

- [ ] **Step 1: Write the failing test**

Create `bill-payment-editor.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillPaymentEditor } from './bill-payment-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { vi } from 'vitest';

describe('BillPaymentEditor', () => {
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
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills').flush({
      items: [{ bill: { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-05-01', dueDate: null,
        vendorReference: null, memo: null, status: 'Entered',
        lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] }, openBalance: 100, settlementStatus: 'Open' }],
      total: 1, skip: 0, limit: 200 });
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/credit-balance').flush({ vendorId: 'v1', creditBalance: 0 });
  }

  it('auto-allocates oldest-first when the amount changes', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillPaymentEditor);
    f.detectChanges();
    flushInit(ctrl);
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.onAmount(60);
    expect(cmp.rows()[0].allocation).toBe(60);
    expect(cmp.unallocated()).toBe(0);
    ctrl.verify();
  });

  it('records a payment with allocations and navigates to the payments list', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillPaymentEditor);
    f.detectChanges();
    flushInit(ctrl);
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.onAmount(100);
    cmp.save();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bill-payments');
    expect(post.request.body).toEqual({ vendorId: 'v1', date: cmp.date(), amount: 100, method: null,
      allocations: [{ targetId: 'b1', amount: 100 }] });
    post.flush({ id: 'p9', vendorId: 'v1', date: cmp.date(), amount: 100, method: null,
      allocations: [{ targetId: 'b1', amount: 100 }], voided: false });
    expect(nav).toHaveBeenCalledWith(['/payables/payments']);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/bill-payment-editor.spec.ts" --watch=false`
Expected: FAIL — cannot resolve `./bill-payment-editor`.

- [ ] **Step 3: Create the component** (mirror of `payment-editor.ts`)

Create `bill-payment-editor.ts`:

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
  selector: 'app-bill-payment-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <h1 class="text-2xl font-bold">Record payment</h1>
      <p class="text-sm text-muted-foreground">{{ svc.vendorName(vendorId!) }}</p>
      @if (creditBalance() > 0) {
        <p class="text-sm text-muted-foreground">Existing vendor credit: {{ money(creditBalance()) }}</p>
      }

      <div class="grid grid-cols-3 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Amount paid</label>
          <app-currency-input ariaLabel="Amount paid" [value]="amount()" (valueChange)="onAmount($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Method</label>
          <input hlmInput type="text" placeholder="check, ACH…" [value]="method()" (input)="method.set($any($event.target).value)" />
        </div>
      </div>

      @if (rows().length === 0) {
        <p class="text-sm text-muted-foreground">No open bills for this vendor — the full amount becomes vendor credit.</p>
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
        <div class="flex justify-between" [class.text-destructive]="allocated() > amount()">
          <span>Unallocated → vendor credit</span><span>{{ money(unallocated()) }}</span>
        </div>
      </div>

      <p class="text-xs text-muted-foreground">
        Recording a payment posts a cash entry that needs approval before it affects the statements.
        The bill's open balance updates immediately.
      </p>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Record payment</button>
        <a hlmBtn variant="outline" routerLink="/payables/payments">Cancel</a>
      </div>
    </div>
  `,
})
export class BillPaymentEditor {
  readonly svc = inject(PayablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly vendorId = this.route.snapshot.queryParamMap.get('vendor');
  private readonly focusBill = this.route.snapshot.queryParamMap.get('bill');

  readonly amount = signal(0);
  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly method = signal('');
  readonly rows = signal<AllocRow[]>([]);
  readonly creditBalance = signal(0);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly allocated = computed(() => Math.round(this.rows().reduce((s, r) => s + r.allocation, 0) * 100) / 100);
  readonly unallocated = computed(() => Math.max(0, Math.round((this.amount() - this.allocated()) * 100) / 100));
  readonly valid = computed(() =>
    this.amount() > 0 &&
    this.rows().every(r => r.allocation >= 0 && r.allocation <= r.openBalance) &&
    this.allocated() <= this.amount());

  constructor() {
    if (!this.vendorId) { void this.router.navigate(['/payables']); return; }
    this.svc.load();
    this.svc.listBills({ vendorId: this.vendorId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(page => {
        let rows: AllocRow[] = page.items.map(v => ({
          billId: v.bill.id, number: v.bill.number, billDate: v.bill.billDate,
          openBalance: v.openBalance, allocation: 0,
        }));
        if (this.focusBill) {
          rows = [...rows.filter(r => r.billId === this.focusBill), ...rows.filter(r => r.billId !== this.focusBill)];
        }
        const initialAmount = this.focusBill
          ? (rows.find(r => r.billId === this.focusBill)?.openBalance ?? 0) : 0;
        this.amount.set(initialAmount);
        this.rows.set(autoAllocate(initialAmount, rows));
      });
    this.svc.vendorCreditBalance(this.vendorId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(b => this.creditBalance.set(b));
  }

  onAmount(v: number): void { this.amount.set(v); this.rows.update(rs => autoAllocate(v, rs)); }
  onRow(i: number, v: number): void { this.rows.update(rs => rs.map((r, idx) => idx === i ? { ...r, allocation: v } : r)); }

  save(): void {
    if (!this.valid() || !this.vendorId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordBillPayment({
      vendorId: this.vendorId, date: this.date(), amount: this.amount(),
      method: this.method().trim() || null,
      allocations: this.rows().filter(r => r.allocation > 0).map(r => ({ targetId: r.billId, amount: r.allocation })),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/payables/payments']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/bill-payment-editor.spec.ts" --watch=false`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/bill-payment-editor.ts UI/Angular/src/app/features/payables/bill-payment-editor.spec.ts
git commit -m "feat(ui): payables BillPaymentEditor (vendor-level, auto-allocate, overpay→credit)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: feature — Applied payments on BillDetail

**Files:**
- Modify: `UI/Angular/src/app/features/payables/bill-detail.ts`
- Test: `UI/Angular/src/app/features/payables/bill-detail.spec.ts` (append a case)

**Interfaces:**
- Consumes: `PayablesService.listBillPayments`, `PayablesService.voidBillPayment`, `BillPayment`.
- Produces: an "Applied payments" section on the Entered bill; `voidPayment(p)` voids via the service and reloads bill + payments.

- [ ] **Step 1: Append the failing test**

In `bill-detail.spec.ts`, append a case. It reuses the existing `setup`/`flushLoads` helpers.

**IMPORTANT — update the existing tests first.** After this task, *any time the loaded/reloaded bill has status `Entered`*, the component additionally fires `GET /bill-payments?vendorId`. Both existing P-A tests hit an Entered bill and will leave that request unmatched at `ctrl.verify()` unless you flush it:
- **"renders a draft bill and enters it"** — the bill starts Draft (no payments fetch on first load), but after `enter()` the reload returns an **Entered** bill → add a payments flush right after that reload's bill flush:
  ```typescript
  ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments').flush([]);
  ```
- **"voids an entered bill with a reason"** — the bill loads as Entered (payments fetched on first load) and reloads as Entered after the void (payments fetched again) → add the same payments flush in **two** places: right after the initial `flushLoads(ctrl, 'Entered')`, and right after the post-void bill-reload flush:
  ```typescript
  ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments').flush([]);
  ```

Then add this new test:

```typescript
  it('shows applied payments on an entered bill and voids one', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillDetail);
    f.detectChanges();
    flushLoads(ctrl, 'Entered');
    // Entered bills load the vendor's payments for the applied-payments section.
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments' && r.params.get('vendorId') === 'v1')
      .flush([{ id: 'p1', vendorId: 'v1', date: '2026-06-10', amount: 1200, method: 'check',
        allocations: [{ targetId: 'b1', amount: 1200 }], voided: false }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Applied payments');

    f.componentInstance.voidPayment({ id: 'p1', vendorId: 'v1', date: '2026-06-10', amount: 1200, method: 'check',
      allocations: [{ targetId: 'b1', amount: 1200 }], voided: false } as any);
    const v = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bill-payments/p1/void');
    v.flush({});
    // reload: bill + payments fetched again
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/b1').flush({ bill: { id: 'b1', vendorId: 'v1', number: 'B-1',
      billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9', memo: null, status: 'Entered',
      lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] }, openBalance: 1200, settlementStatus: 'Open' });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments').flush([]);
    ctrl.verify();
  });
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/bill-detail.spec.ts" --watch=false`
Expected: FAIL — `voidPayment` not defined / "Applied payments" not rendered / unmatched payments request.

- [ ] **Step 3: Modify the component**

In `bill-detail.ts`:

(a) Add imports — extend the payables model import and add `HlmButton` is already imported; add the model type:

```typescript
import { BillView, BillPayment, billTotal } from '../../core/payables/payables';
```

(b) Add state + computed (after the `voidReason` signal):

```typescript
  readonly payments = signal<BillPayment[]>([]);
  readonly applied = computed(() => this.payments()
    .map(p => ({ payment: p, here: p.allocations.filter(a => a.targetId === this.id).reduce((s, a) => s + a.amount, 0) }))
    .filter(x => x.here > 0));
```

(c) In `reload()`, after `this.view.set(v);`, load payments for an entered bill:

```typescript
      next: (v) => {
        this.view.set(v);
        if (v.bill.status === 'Entered') this.loadPayments(v.bill.vendorId);
        if (clearBusy) this.busy.set(false);
      },
```

(d) Add the loader + void handler (after `reload`):

```typescript
  private loadPayments(vendorId: string): void {
    this.svc.listBillPayments(vendorId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (ps) => this.payments.set(ps),
      error: () => this.payments.set([]),
    });
  }

  voidPayment(p: BillPayment): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidBillPayment(p.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.reload(true); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
```

(e) In the template, inside the `@case ('Entered')` block, after the void-reason row, add the applied-payments section:

```html
            @if (applied().length > 0) {
              <div class="flex flex-col gap-1">
                <h2 class="text-sm font-semibold text-muted-foreground">Applied payments</h2>
                <table class="text-sm w-full max-w-md">
                  <tbody>
                    @for (a of applied(); track a.payment.id) {
                      <tr [class.opacity-50]="a.payment.voided">
                        <td class="py-1">{{ formatDate(a.payment.date) }}</td>
                        <td class="tabular-nums">{{ money(a.here) }}</td>
                        <td class="text-muted-foreground">{{ a.payment.method ?? '—' }}</td>
                        <td class="text-right">
                          @if (!a.payment.voided) {
                            <button hlmBtn type="button" variant="ghost" size="sm"
                                    (click)="voidPayment(a.payment)" [disabled]="busy()">Void</button>
                          } @else {
                            <span class="text-xs text-muted-foreground">Voided</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
```

(f) Add `computed` to the `@angular/core` import if not present (it is already imported in this file).

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/bill-detail.spec.ts" --watch=false`
Expected: PASS (the existing 2 tests, updated, + the new applied-payments test).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/bill-detail.ts UI/Angular/src/app/features/payables/bill-detail.spec.ts
git commit -m "feat(ui): applied-payments section on BillDetail (void a payment)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 7: feature — Payments tab + routes

**Files:**
- Modify: `UI/Angular/src/app/features/payables/payables-shell.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`
- Test: `UI/Angular/src/app/features/payables/payables-shell.spec.ts` (append a case)

**Interfaces:**
- Consumes: `BillPaymentList`, `BillPaymentEditor`.
- Produces: a Payments tab in the shell (order Bills | Payments | Vendors); `/payables/payments` → BillPaymentList, `/payables/payments/new` → BillPaymentEditor.

- [ ] **Step 1: Append the failing shell test**

In `payables-shell.spec.ts`, extend the existing render assertion (or add a case) to require the Payments tab:

```typescript
  it('renders Bills, Payments and Vendors tabs', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([])] });
    const f = TestBed.createComponent(PayablesShell);
    f.detectChanges();
    const tabs = f.nativeElement.textContent;
    expect(tabs).toContain('Bills');
    expect(tabs).toContain('Payments');
    expect(tabs).toContain('Vendors');
  });
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/payables-shell.spec.ts" --watch=false`
Expected: FAIL — no "Payments" tab.

- [ ] **Step 3: Add the Payments tab to the shell**

In `payables-shell.ts`, insert the Payments tab anchor **between** the Bills and Vendors anchors (order Bills | Payments | Vendors):

```html
        <a routerLink="payments"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-payments">Payments</a>
```

- [ ] **Step 4: Wire the routes**

In `app.routes.ts`, add imports near the other payables imports:

```typescript
import { BillPaymentList } from './features/payables/bill-payment-list';
import { BillPaymentEditor } from './features/payables/bill-payment-editor';
```

In the `payables` children array, add the two payment routes (after the `bills/...` routes, before `vendors`):

```typescript
    { path: 'payments', component: BillPaymentList },
    { path: 'payments/new', component: BillPaymentEditor },
```

- [ ] **Step 5: Run the shell test + full suite + type-check**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/payables-shell.spec.ts" --watch=false`
Expected: PASS.

Then the whole gate:

Run: `cd UI/Angular && npx tsc -p tsconfig.app.json --noEmit && npx ng test --watch=false`
Expected: tsc clean; ALL specs pass (existing + new payables payment specs). Report totals.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/payables/payables-shell.ts UI/Angular/src/app/features/payables/payables-shell.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): Payables Payments tab + routes

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review notes

- **Spec coverage:** backend `GET /bill-payments` (T1); core models + autoAllocate (T2); service list/record/void/credit-balance (T3); Payments tab list (T4); BillPaymentEditor with auto-allocate + overpay→credit (T5); applied-payments + void on BillDetail (T6); shell tab + routes (T7). Deferred items (vendor credits mgmt, 360, draft edit) correctly absent.
- **Mirror correction vs spec:** the spec text said "each non-voided payment has Void" in the list; the faithful AR mirror puts void on BillDetail, not the list (AR `PaymentList` shows status only). Plan follows the AR mirror per the user's "mirror receivables" directive — payment list is read-only, void lives on BillDetail (T6).
- **Type consistency:** `BillPayment { id, vendorId, date, amount, method, allocations, voided }`, `PaymentAllocation { targetId, amount }`, `AllocRow { billId, number, billDate, openBalance, allocation }`, `RecordBillPaymentRequest` fields match the backend `RecordBillPaymentRequest(VendorId, Date, Amount, Method, Allocations)` → camelCase wire keys. `autoAllocate` signature identical in T2 (definition) and T5 (use).
- **Posting behavior confirmed:** bill payments post the cash entry as PendingApproval (verified against `PayablesE2eTests`), so the editor's "needs approval" copy is accurate — not a guess.
- **No digit-PascalCase fields** on the payment wire, so the camelCase trap doesn't apply.
