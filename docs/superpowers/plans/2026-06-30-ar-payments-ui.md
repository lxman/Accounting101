# AR Payments / Cash-Receipt UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user record a customer payment from the UI, allocate it across open invoices (excess → customer credit), settle invoices to PartiallyPaid/Paid, and void a payment to correct mistakes.

**Architecture:** One new backend read endpoint (`GET /clients/{id}/payments?customerId=`) exposes the existing `GetPaymentsByCustomerAsync`. A new Angular `PaymentEditor` screen (customer-level, oldest-first auto-allocation) is reached from the invoice list and invoice detail. The invoice detail gains an applied-payments list with void. Settlement is document-driven (updates immediately); the posted cash entry still needs approval via the existing Approvals flow to hit the statements.

**Tech Stack:** ASP.NET Core minimal APIs + xUnit (backend); Angular 22 (zoneless, signals, OnPush standalone components), Spartan helm UI, Vitest (`ng test`).

**Spec:** `docs/superpowers/specs/2026-06-30-ar-payments-ui-design.md`

## Global Constraints

- Angular components: standalone, `ChangeDetectionStrategy.OnPush`, signals (no NgModules, no Zone).
- Reuse existing pieces: `currency-input` for money inputs, `extractProblem` for error relay, the persisted customer selection in `ReceivablesService`, existing badges.
- Run frontend tests: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false [--include='**/<file>.spec.ts']`.
- Run backend tests: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`.
- Money is 2-decimal; round half-away-from-zero for non-negative inputs (`Math.round(x*100)/100`).
- Commit trailer on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit files only — never `environment.ts` or `.claude/`.

## File Structure

- `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` — add public `GetPaymentsByCustomerAsync` passthrough.
- `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` — add `ListPayments` handler + `MapGet("/payments")`.
- `Modules/Receivables/Accounting101.Receivables.Tests/GetPaymentsEndpointTests.cs` — new endpoint test.
- `UI/Angular/src/app/core/receivables/receivables.ts` — `Payment`, `PaymentAllocation`, `RecordPaymentRequest` types; `AllocRow` + `autoAllocate`.
- `UI/Angular/src/app/core/receivables/receivables.service.ts` — `listPayments`/`recordPayment`/`voidPayment`/`creditBalance`.
- `UI/Angular/src/app/core/receivables/receivables.service.spec.ts` — service-method + `autoAllocate` tests.
- `UI/Angular/src/app/features/receivables/payment-editor.ts` (+ `.spec.ts`) — new screen.
- `UI/Angular/src/app/app.routes.ts` — `receivables/payments/new` route.
- `UI/Angular/src/app/features/receivables/invoice-list.ts` (+ `.spec.ts`) — Record-payment button.
- `UI/Angular/src/app/features/receivables/invoice-detail.ts` (+ `.spec.ts`) — Record link + applied-payments list + void.

---

### Task 1: Backend — `GET /payments?customerId=` endpoint

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/GetPaymentsEndpointTests.cs`

**Interfaces:**
- Consumes: existing `IPaymentStore.GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken)`, `ReceivablesHostFixture.SeedSodClientAsync()` returning `(Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver)`.
- Produces: route `GET /clients/{clientId}/payments?customerId=<guid>` → `200 IReadOnlyList<Payment>` or `400` when `customerId` is missing/empty.

- [ ] **Step 1: Write the failing test**

Create `Modules/Receivables/Accounting101.Receivables.Tests/GetPaymentsEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;   // AccountRequest
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the read endpoint that powers the UI's applied-payments list: it returns a customer's
/// payments (with allocations) and rejects a missing customerId.</summary>
public sealed class GetPaymentsEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", "Customer");
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();

    private static async Task<Guid> IssueInvoiceAsync(HttpClient clerk, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draftRequest = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        return issued.Id;
    }

    [Fact]
    public async Task GET_payments_returns_customer_payments_with_allocations()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;
        Guid invoiceId = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);

        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 31), 60m, "check",
                [new Allocation(invoiceId, 60m)])))
            .Content.ReadFromJsonAsync<Payment>())!;

        Payment[] list = (await clerk.GetFromJsonAsync<Payment[]>(
            $"/clients/{clientId}/payments?customerId={customer.Id}"))!;

        Assert.Single(list);
        Assert.Equal(payment.Id, list[0].Id);
        Assert.Equal(60m, list[0].Amount);
        Assert.Equal(invoiceId, list[0].Allocations.Single().TargetId);
    }

    [Fact]
    public async Task GET_payments_without_customerId_is_400()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/payments");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter FullyQualifiedName~GetPaymentsEndpointTests`
Expected: FAIL — `GET /payments` returns 404/405 (route not mapped), so the GET deserialization throws / status assertions fail.

- [ ] **Step 3: Add the service passthrough**

In `PaymentService.cs`, add this public method (next to `GetCustomerCreditBalanceAsync`):

```csharp
/// <summary>All payments recorded for a customer (including voided), newest-or-stored order. Read-only;
/// powers the UI's applied-payments view.</summary>
public Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
    payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
```

- [ ] **Step 4: Add the handler + route**

In `ReceivablesEndpoints.cs`, add the route in `MapReceivablesEndpoints` immediately after the `MapPost("/payments", RecordPayment)` line:

```csharp
clients.MapGet("/payments", ListPayments);
```

And add the handler next to `RecordPayment`:

```csharp
private static async Task<IResult> ListPayments(
    Guid clientId, Guid? customerId, PaymentService service, CancellationToken cancellationToken)
{
    if (customerId is null || customerId == Guid.Empty)
        return Results.Problem("customerId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
    IReadOnlyList<Payment> payments = await service.GetPaymentsByCustomerAsync(clientId, customerId.Value, cancellationToken);
    return Results.Ok(payments);
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter FullyQualifiedName~GetPaymentsEndpointTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables/PaymentService.cs Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs Modules/Receivables/Accounting101.Receivables.Tests/GetPaymentsEndpointTests.cs
git commit -m "feat(receivables): GET /payments?customerId= read endpoint

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: UI model types + `autoAllocate` pure function

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.ts`
- Test: `UI/Angular/src/app/core/receivables/receivables.service.spec.ts` (add an `autoAllocate` describe block)

**Interfaces:**
- Produces: types `PaymentAllocation { targetId: string; amount: number }`, `Payment { id; customerId; date; method: string|null; amount; allocations: PaymentAllocation[]; voided: boolean }`, `RecordPaymentRequest { customerId; date; amount; method: string|null; allocations: PaymentAllocation[] }`; `AllocRow { invoiceId: string; number: string|null; issueDate: string; openBalance: number; allocation: number }`; `autoAllocate(amount: number, rows: readonly AllocRow[]): AllocRow[]`.

- [ ] **Step 1: Write the failing tests**

In `receivables.service.spec.ts`, add this describe block after the existing `describe('pure math', …)` block. Add `autoAllocate, AllocRow` to the existing import from `'./receivables'`:

```ts
describe('autoAllocate', () => {
  const row = (invoiceId: string, openBalance: number): AllocRow =>
    ({ invoiceId, number: invoiceId, issueDate: '2026-06-01', openBalance, allocation: 0 });

  it('fills oldest-first, capping each row at its open balance', () => {
    const out = autoAllocate(300, [row('a', 105), row('b', 150), row('c', 200)]);
    expect(out.map(r => r.allocation)).toEqual([105, 150, 45]);
  });

  it('partial first row when amount is less than the first open balance', () => {
    const out = autoAllocate(60, [row('a', 105), row('b', 150)]);
    expect(out.map(r => r.allocation)).toEqual([60, 0]);
  });

  it('excess over total open balances stays unallocated (rows capped)', () => {
    const out = autoAllocate(500, [row('a', 105), row('b', 150)]);
    expect(out.map(r => r.allocation)).toEqual([105, 150]);
    expect(out.reduce((s, r) => s + r.allocation, 0)).toBe(255);
  });

  it('zero amount allocates nothing', () => {
    const out = autoAllocate(0, [row('a', 105)]);
    expect(out.map(r => r.allocation)).toEqual([0]);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/receivables.service.spec.ts'`
Expected: FAIL — `autoAllocate is not a function` / import error.

- [ ] **Step 3: Add the types and function**

Append to `receivables.ts`:

```ts
export interface PaymentAllocation { targetId: string; amount: number; }
export interface Payment {
  id: string; customerId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[]; voided: boolean;
}
export interface RecordPaymentRequest {
  customerId: string; date: string; amount: number;
  method: string | null; allocations: PaymentAllocation[];
}

/** A payment-allocation editor row: one open invoice the user can apply cash to. */
export interface AllocRow {
  invoiceId: string; number: string | null; issueDate: string; openBalance: number; allocation: number;
}

/** Distribute `amount` across rows in their given (oldest-first) order, each capped at its open balance.
 *  Returns new rows; any remainder beyond the rows' open balances is left unallocated (→ customer credit). */
export function autoAllocate(amount: number, rows: readonly AllocRow[]): AllocRow[] {
  let remaining = Math.max(0, amount);
  return rows.map(r => {
    const take = Math.min(r.openBalance, remaining);
    remaining = Math.round((remaining - take) * 100) / 100;
    return { ...r, allocation: Math.round(take * 100) / 100 };
  });
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/receivables.service.spec.ts'`
Expected: PASS (existing tests + 4 new `autoAllocate` tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/receivables/receivables.ts UI/Angular/src/app/core/receivables/receivables.service.spec.ts
git commit -m "feat(ui): payment types + oldest-first autoAllocate helper

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: UI service methods (`listPayments`/`recordPayment`/`voidPayment`/`creditBalance`)

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.service.ts`
- Test: `UI/Angular/src/app/core/receivables/receivables.service.spec.ts`

**Interfaces:**
- Consumes: `Payment`, `RecordPaymentRequest` (Task 2); existing `base()`, `client.clientId()`.
- Produces: `listPayments(customerId: string): Observable<Payment[]>`, `recordPayment(req: RecordPaymentRequest): Observable<Payment>`, `voidPayment(id: string, reason?: string | null): Observable<Payment>`, `creditBalance(customerId: string): Observable<number>`.

- [ ] **Step 1: Write the failing tests**

In `receivables.service.spec.ts`, inside `describe('ReceivablesService', …)`, add these tests. Add `Payment` to the import from `'./receivables'` and `map`-less (we assert via flush). Ensure `import { HttpParams } from '@angular/common/http';` is not needed in the spec.

```ts
it('listPayments GETs /payments?customerId=', () => {
  const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  let result: Payment[] | undefined;
  svc.listPayments('cu1').subscribe(p => (result = p));
  const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/payments' && r.params.get('customerId') === 'cu1');
  expect(req.request.method).toBe('GET');
  req.flush([{ id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 60, method: 'check', allocations: [{ targetId: 'inv1', amount: 60 }], voided: false }] as Payment[]);
  expect(result!.length).toBe(1);
});

it('recordPayment POSTs the request to /payments', () => {
  const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  svc.recordPayment({ customerId: 'cu1', date: '2026-06-30', amount: 60, method: null, allocations: [{ targetId: 'inv1', amount: 60 }] }).subscribe();
  const req = ctrl.expectOne('http://localhost:5000/clients/C1/payments');
  expect(req.request.method).toBe('POST');
  expect(req.request.body.allocations).toEqual([{ targetId: 'inv1', amount: 60 }]);
  req.flush({ id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 60, method: null, allocations: [{ targetId: 'inv1', amount: 60 }], voided: false });
});

it('voidPayment POSTs the reason to /payments/{id}/void', () => {
  const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  svc.voidPayment('p1', 'oops').subscribe();
  const req = ctrl.expectOne('http://localhost:5000/clients/C1/payments/p1/void');
  expect(req.request.method).toBe('POST');
  expect(req.request.body).toEqual({ reason: 'oops' });
  req.flush({ id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 60, method: null, allocations: [], voided: true });
});

it('creditBalance GETs and unwraps creditBalance', () => {
  const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  let bal: number | undefined;
  svc.creditBalance('cu1').subscribe(b => (bal = b));
  ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/credit-balance')
    .flush({ customerId: 'cu1', creditBalance: 42.5 });
  expect(bal).toBe(42.5);
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/receivables.service.spec.ts'`
Expected: FAIL — methods do not exist.

- [ ] **Step 3: Implement the methods**

In `receivables.service.ts`: add `map` to the rxjs import (`import { EMPTY, Observable, map, tap } from 'rxjs';`) and `Payment, RecordPaymentRequest` to the `./receivables` import. Add these methods to the class (after `void(...)`):

```ts
listPayments(customerId: string): Observable<Payment[]> {
  const id = this.client.clientId(); if (!id) return EMPTY;
  return this.http.get<Payment[]>(this.base('/payments'), { params: new HttpParams().set('customerId', customerId) });
}
recordPayment(req: RecordPaymentRequest): Observable<Payment> {
  const id = this.client.clientId(); if (!id) return EMPTY;
  return this.http.post<Payment>(this.base('/payments'), req);
}
voidPayment(id: string, reason?: string | null): Observable<Payment> {
  const clientId = this.client.clientId(); if (!clientId) return EMPTY;
  return this.http.post<Payment>(this.base(`/payments/${id}/void`), { reason: reason ?? null });
}
creditBalance(customerId: string): Observable<number> {
  const id = this.client.clientId(); if (!id) return EMPTY;
  return this.http.get<{ creditBalance: number }>(this.base(`/customers/${customerId}/credit-balance`))
    .pipe(map(r => r.creditBalance));
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/receivables.service.spec.ts'`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/receivables/receivables.service.ts UI/Angular/src/app/core/receivables/receivables.service.spec.ts
git commit -m "feat(ui): receivables service payment methods (list/record/void/credit)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `PaymentEditor` screen + route

**Files:**
- Create: `UI/Angular/src/app/features/receivables/payment-editor.ts`
- Create: `UI/Angular/src/app/features/receivables/payment-editor.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `ReceivablesService.listInvoices`, `.recordPayment`, `.creditBalance`, `.load`, `.customerName`; `autoAllocate`, `AllocRow` (Task 2); `RecordPaymentRequest` (Task 2); `currency-input`; `extractProblem`.
- Produces: route `receivables/payments/new` → `PaymentEditor`. Public methods used by template/tests: `onAmount(v: number)`, `onRow(i: number, v: number)`, `save()`; signals `amount`, `date`, `method`, `rows`, `creditBalance`, computed `allocated`, `unallocated`, `valid`.

- [ ] **Step 1: Write the failing test**

Create `payment-editor.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PaymentEditor } from './payment-editor';
import { ClientContextService } from '../../core/client/client-context.service';

function routeStub(params: Record<string, string | null>) {
  return { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: (k: string) => params[k] ?? null } } } };
}

function openInvoice(id: string, number: string, open: number) {
  return {
    invoice: { id, customerId: 'cu1', number, issueDate: '2026-06-01', dueDate: null, status: 'Issued', taxRate: 0, memo: null, lines: [] },
    openBalance: open, settlementStatus: 'Open' as const,
  };
}

function setup(params: Record<string, string | null>) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), routeStub(params)],
  });
  TestBed.inject(ClientContextService).select('C1');
  const ctrl = TestBed.inject(HttpTestingController);
  return ctrl;
}

describe('PaymentEditor', () => {
  it('redirects to /receivables when no customer query param', () => {
    const ctrl = setup({ customer: null });
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    TestBed.createComponent(PaymentEditor).detectChanges();
    expect(nav).toHaveBeenCalledWith(['/receivables']);
    ctrl.verify();
  });

  it('loads open invoices, auto-allocates the entered amount oldest-first, and posts the payment', () => {
    const ctrl = setup({ customer: 'cu1' });
    const f = TestBed.createComponent(PaymentEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme', email: null }]);
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices') && r.params.get('settlement') === 'open')
      .flush({ items: [openInvoice('inv1', '1001', 105), openInvoice('inv2', '1002', 150)], total: 2, skip: 0, limit: 200 });
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/credit-balance').flush({ customerId: 'cu1', creditBalance: 0 });
    f.detectChanges();
    const cmp = f.componentInstance as PaymentEditor;
    cmp.onAmount(200); f.detectChanges();
    expect(cmp.rows().map(r => r.allocation)).toEqual([105, 95]);
    expect(cmp.unallocated()).toBe(0);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payments');
    expect(req.request.body.amount).toBe(200);
    expect(req.request.body.allocations).toEqual([{ targetId: 'inv1', amount: 105 }, { targetId: 'inv2', amount: 95 }]);
    req.flush({ id: 'p1', customerId: 'cu1', date: cmp.date(), amount: 200, method: null, allocations: req.request.body.allocations, voided: false });
    expect(nav).toHaveBeenCalledWith(['/receivables']);
    ctrl.verify();
  });

  it('prefills the amount from the focused invoice and reports overpayment as credit', () => {
    const ctrl = setup({ customer: 'cu1', invoice: 'inv2' });
    const f = TestBed.createComponent(PaymentEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme', email: null }]);
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices')).flush(
      { items: [openInvoice('inv1', '1001', 105), openInvoice('inv2', '1002', 150)], total: 2, skip: 0, limit: 200 });
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/credit-balance').flush({ customerId: 'cu1', creditBalance: 0 });
    f.detectChanges();
    const cmp = f.componentInstance as PaymentEditor;
    // focused invoice sorted first, amount prefilled to its open balance
    expect(cmp.rows()[0].invoiceId).toBe('inv2');
    expect(cmp.amount()).toBe(150);
    // open balances total 255 (150 + 105); pay 305 → all allocated, 50 left as credit
    cmp.onAmount(305); f.detectChanges();
    expect(cmp.rows().map(r => r.allocation)).toEqual([150, 105]);
    expect(cmp.unallocated()).toBe(50);                // → customer credit
    expect(cmp.valid()).toBe(true);
    ctrl.verify();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/payment-editor.spec.ts'`
Expected: FAIL — `PaymentEditor` does not exist.

- [ ] **Step 3: Create the component**

Create `payment-editor.ts`:

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { AllocRow, autoAllocate } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

@Component({
  selector: 'app-payment-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Invoices</a>
      <h1 class="text-2xl font-bold">Record payment</h1>
      <p class="text-sm text-muted-foreground">{{ svc.customerName(customerId!) }}</p>

      <div class="grid grid-cols-3 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Amount received</label>
          <app-currency-input ariaLabel="Amount received" [value]="amount()" (valueChange)="onAmount($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Method</label>
          <input hlmInput type="text" placeholder="check, card…" [value]="method()" (input)="method.set($any($event.target).value)" />
        </div>
      </div>

      @if (rows().length === 0) {
        <p class="text-sm text-muted-foreground">No open invoices for this customer — the full amount becomes customer credit.</p>
      } @else {
        <table class="w-full text-sm">
          <thead>
            <tr class="text-left text-muted-foreground">
              <th class="py-1">Invoice</th><th>Issued</th>
              <th class="text-right pr-5">Open</th><th class="text-right pr-5">Apply</th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.invoiceId; let i = $index) {
              <tr>
                <td class="py-1">{{ r.number ?? '—' }}</td>
                <td>{{ formatDate(r.issueDate) }}</td>
                <td class="text-right tabular-nums pr-5">{{ money(r.openBalance) }}</td>
                <td class="pr-2">
                  <div class="flex justify-end">
                    <app-currency-input class="w-32" [ariaLabel]="'Apply to ' + (r.number ?? r.invoiceId)"
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
          <span>Unallocated → customer credit</span><span>{{ money(unallocated()) }}</span>
        </div>
      </div>

      <p class="text-xs text-muted-foreground">
        Recording a payment posts a cash entry that needs approval before it affects the statements.
        The invoice's open balance updates immediately.
      </p>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Record payment</button>
        <a hlmBtn variant="outline" routerLink="/receivables">Cancel</a>
      </div>
    </div>
  `,
})
export class PaymentEditor {
  readonly svc = inject(ReceivablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly customerId = this.route.snapshot.queryParamMap.get('customer');
  private readonly focusInvoice = this.route.snapshot.queryParamMap.get('invoice');

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
    if (!this.customerId) { void this.router.navigate(['/receivables']); return; }
    this.svc.load();
    this.svc.listInvoices({ customerId: this.customerId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })
      .subscribe(page => {
        let rows: AllocRow[] = page.items.map(v => ({
          invoiceId: v.invoice.id, number: v.invoice.number, issueDate: v.invoice.issueDate,
          openBalance: v.openBalance, allocation: 0,
        }));
        if (this.focusInvoice) {
          rows = [...rows.filter(r => r.invoiceId === this.focusInvoice), ...rows.filter(r => r.invoiceId !== this.focusInvoice)];
        }
        const initialAmount = this.focusInvoice
          ? (rows.find(r => r.invoiceId === this.focusInvoice)?.openBalance ?? 0) : 0;
        this.amount.set(initialAmount);
        this.rows.set(autoAllocate(initialAmount, rows));
      });
    this.svc.creditBalance(this.customerId).subscribe(b => this.creditBalance.set(b));
  }

  onAmount(v: number): void { this.amount.set(v); this.rows.update(rs => autoAllocate(v, rs)); }
  onRow(i: number, v: number): void { this.rows.update(rs => rs.map((r, idx) => idx === i ? { ...r, allocation: v } : r)); }

  save(): void {
    if (!this.valid() || !this.customerId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordPayment({
      customerId: this.customerId, date: this.date(), amount: this.amount(),
      method: this.method().trim() || null,
      allocations: this.rows().filter(r => r.allocation > 0).map(r => ({ targetId: r.invoiceId, amount: r.allocation })),
    }).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/receivables']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 4: Register the route**

In `app.routes.ts`, add the import after the other receivables imports:

```ts
import { PaymentEditor } from './features/receivables/payment-editor';
```

And add the route inside the `receivables` children array, after the `invoices/new` line:

```ts
    { path: 'payments/new', component: PaymentEditor },
```

- [ ] **Step 5: Run to verify it passes**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/payment-editor.spec.ts'`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/payment-editor.ts UI/Angular/src/app/features/receivables/payment-editor.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): payment editor screen (customer-level, oldest-first allocation)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Invoice list — Record-payment button

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/invoice-list.ts`
- Test: `UI/Angular/src/app/features/receivables/invoice-list.spec.ts`

**Interfaces:**
- Consumes: existing `customerId()` signal on `InvoiceList`.
- Produces: a `RouterLink` to `/receivables/payments/new` with `queryParams { customer }`, disabled-styled when no customer.

- [ ] **Step 1: Write the failing test**

Add to `invoice-list.spec.ts` (inside the `describe('InvoiceList', …)`):

```ts
it('Record payment link targets the payment editor for the selected customer', () => {
  const ctrl = TestBed.inject(HttpTestingController);
  const f = TestBed.createComponent(InvoiceList); f.detectChanges();
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  f.detectChanges();
  f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices') && r.params.get('customerId') === 'cu1')
    .flush({ items: [], total: 0, skip: 0, limit: 50 });
  f.detectChanges();
  const link = [...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Record payment') as HTMLAnchorElement;
  expect(link).toBeTruthy();
  expect(link.getAttribute('href')).toContain('/receivables/payments/new');
  expect(link.getAttribute('href')).toContain('customer=cu1');
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/invoice-list.spec.ts'`
Expected: FAIL — no "Record payment" link found.

- [ ] **Step 3: Add the button**

In `invoice-list.ts`, replace the existing single "New invoice" anchor (the `<a hlmBtn size="sm" class="ms-auto" routerLink="/receivables/invoices/new" …>New invoice</a>` block) with a wrapper holding both buttons:

```html
        <div class="ms-auto flex items-center gap-2">
          <a hlmBtn size="sm" variant="outline"
             routerLink="/receivables/payments/new"
             [queryParams]="{ customer: customerId() }"
             [class.pointer-events-none]="!customerId()"
             [class.opacity-50]="!customerId()">
            Record payment
          </a>
          <a hlmBtn size="sm"
             routerLink="/receivables/invoices/new"
             [queryParams]="{ customer: customerId() }"
             [class.pointer-events-none]="!customerId()"
             [class.opacity-50]="!customerId()">
            New invoice
          </a>
        </div>
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/invoice-list.spec.ts'`
Expected: PASS (existing 6 + 1 new).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/receivables/invoice-list.ts UI/Angular/src/app/features/receivables/invoice-list.spec.ts
git commit -m "feat(ui): Record-payment button on the invoice list

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Invoice detail — Record link + applied-payments list + void

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/invoice-detail.ts`
- Test: `UI/Angular/src/app/features/receivables/invoice-detail.spec.ts`

**Interfaces:**
- Consumes: `ReceivablesService.listPayments`, `.voidPayment`; `Payment` type; existing `reload()`, `view`, `id`, `busy`, `message`.
- Produces: a Record-payment link (Issued only) and an applied-payments section with per-payment Void.

**Note on existing tests:** the detail now also calls `listPayments(customerId)` after the invoice view loads **for Issued invoices**. Existing tests that flush an *Issued* invoice view must additionally flush a `GET /payments?customerId=…`. The Draft-view path is unchanged.

- [ ] **Step 1: Write the failing test + fix existing Issued-view tests**

In `invoice-detail.spec.ts`:

First, after each place an **Issued** view is flushed via `ctrl.expectOne('…/invoices/inv1').flush(view('Issued','1001'))` (the `issued: void POSTs the reason` test) and after the issue-reload in the first test (`…/invoices/inv2`), add a payments flush. Concretely:

- In `'draft: Issue POSTs, then re-points…'`: after `ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv2').flush(view('Issued', '1001'));` add:
  ```ts
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/payments') && r.params.get('customerId') === 'cu1').flush([]);
  ```
- In `'issued: void POSTs the reason'`: after the initial `ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush(view('Issued', '1001'));` add the same payments flush; and after the void-reload flush at the end add it again (the reload re-fetches payments):
  ```ts
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/payments')).flush([]);
  ```

Then add the new test:

```ts
it('lists payments applied to this invoice and voids one, reloading after', () => {
  setup();
  const f = TestBed.createComponent(InvoiceDetail); f.detectChanges();
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush(view('Issued', '1001'));
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/payments') && r.params.get('customerId') === 'cu1')
    .flush([{ id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 110, method: 'check', allocations: [{ targetId: 'inv1', amount: 110 }], voided: false }]);
  f.detectChanges();
  expect(f.nativeElement.textContent).toContain('110.00');
  const cmp = f.componentInstance as InvoiceDetail;
  cmp.voidPayment({ id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 110, method: 'check', allocations: [{ targetId: 'inv1', amount: 110 }], voided: false });
  ctrl.expectOne('http://localhost:5000/clients/C1/payments/p1/void').flush({ id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 110, method: 'check', allocations: [{ targetId: 'inv1', amount: 110 }], voided: true });
  // reload: invoice view + payments
  ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush(view('Issued', '1001'));
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/payments')).flush([]);
  f.detectChanges();
  expect(cmp.busy()).toBe(false);
});
```

The payment object literals passed to `voidPayment` are structurally typed, so no new import is strictly required. If your editor/linter wants the explicit type, add a new import line near the top of the spec: `import { Payment } from '../../core/receivables/receivables';` (this spec currently has no receivables-model import).

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/invoice-detail.spec.ts'`
Expected: FAIL — `voidPayment` not a function / no payments list; possibly extra-request errors guiding the change.

- [ ] **Step 3: Implement the detail changes**

In `invoice-detail.ts`:

1. Add imports: `import { InvoiceView, invoiceTotals, lineAmount, Payment } from '../../core/receivables/receivables';` (extend the existing import to include `Payment`).

2. Add a payments signal and a computed for this invoice's applied payments, after the `voidReason` signal:

```ts
  readonly payments = signal<Payment[]>([]);
  readonly applied = computed(() => this.payments()
    .map(p => ({ payment: p, here: p.allocations.filter(a => a.targetId === this.id).reduce((s, a) => s + a.amount, 0) }))
    .filter(x => x.here > 0));
```

3. Change `reload()` to also load payments for Issued invoices:

```ts
  reload(clearBusy = false): void {
    this.svc.getInvoice(this.id).subscribe({
      next: (v) => {
        this.view.set(v);
        if (v.invoice.status === 'Issued') this.loadPayments(v.invoice.customerId);
        if (clearBusy) this.busy.set(false);
      },
      error: (e) => { this.message.set(extractProblem(e).detail); if (clearBusy) this.busy.set(false); },
    });
  }

  private loadPayments(customerId: string): void {
    this.svc.listPayments(customerId).subscribe({
      next: (ps) => this.payments.set(ps),
      error: () => this.payments.set([]),
    });
  }

  voidPayment(p: Payment): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidPayment(p.id).subscribe({
      next: () => { this.reload(true); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
```

4. In the template `@case ('Issued')` block, add the Record-payment link before the void controls:

```html
          @case ('Issued') {
            <div class="flex items-center gap-2">
              <a hlmBtn variant="outline" routerLink="/receivables/payments/new"
                 [queryParams]="{ customer: v.invoice.customerId, invoice: id }">Record payment</a>
              <input hlmInput type="text" aria-label="Void reason" placeholder="Void reason"
                     [value]="voidReason()" (input)="voidReason.set($any($event.target).value)" />
              <button hlmBtn type="button" variant="outline" (click)="voidInvoice()" [disabled]="busy()">Void</button>
            </div>

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
          }
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/invoice-detail.spec.ts'`
Expected: PASS (existing tests updated + 1 new).

- [ ] **Step 5: Run the full UI suite + backend suite**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false`
Expected: PASS (all spec files).
Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/invoice-detail.ts UI/Angular/src/app/features/receivables/invoice-detail.spec.ts
git commit -m "feat(ui): record-payment link + applied-payments list with void on invoice detail

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Manual verification (after all tasks, against the running dev stack)

1. `pwsh .localdev/start.ps1` (host has the Receivables account config already).
2. Receivables → select a customer with an Issued invoice → **Record payment** → enter an amount → confirm oldest-first auto-allocation and the "Unallocated → credit" readout → Record.
3. Invoice list shows the invoice as PartiallyPaid/Paid; open the invoice → **Applied payments** lists the payment.
4. Switch "Acting as" → Dev Approver → Approvals → approve the cash entry → trial balance shows Cash ↑ / A/R ↓.
5. Back on the invoice → **Void** the payment → invoice reopens (open balance restored).

## Self-review notes

- Spec coverage: endpoint (T1), model+autoAllocate (T2), service methods (T3), screen+route (T4), list button (T5), detail link+applied+void (T6), tests in every task, manual verification covers the approval + statements flow.
- Settlement-is-document-driven and the approval caveat are surfaced in the screen copy (T4) and manual steps.
- Deferred items (multi-currency, payment editing, payments-history nav, credit/refund/write-off screens) are out of scope per the spec.
