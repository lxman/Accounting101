# Receivables Refunds Area (Slice C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Refunds tab to the Receivables hub — record and list customer refunds (cash paid back against unapplied credit) — backed by a new `GET /refunds` read endpoint.

**Architecture:** One new backend read endpoint (`GET /clients/{id}/refunds?customerId=`) returning the domain `Refund` list (with memo surfaced onto the record). The UI adds a 5th hub tab with a `RefundList` home (mirrors `CreditList`) and an amount-only `RefundEditor` form (mirrors `PaymentEditor`) that posts to the existing `POST /refunds`. The write path, store, and void endpoint already exist; this slice adds the unified read + the UI.

**Tech Stack:** ASP.NET Core minimal APIs (.NET 10) + Angular 22 (standalone, signals, zoneless) + Spartan-ng helm + Tailwind. Backend tests: xUnit (`dotnet test`). UI tests: `@angular/build:unit-test` (vitest) via `npm test`.

## Global Constraints

- **Currency:** USD-only. Money formatted via existing `money()` from `core/format/display`.
- **Settlement / two-track:** recording a refund reduces the customer's credit balance immediately; the posted GL entry (Dr Customer Credits / Cr Cash) lands `PendingApproval` and only affects statements after approval. The form states this.
- **Authorization:** the read endpoint uses the same Read authorization as the other module GETs — under the existing `RequireAuthorization()` `clients` group, no module credential.
- **Refunds have a real void endpoint** (`POST /refunds/{id}/void` already exists), so `voidRefund(id)` is unconditional — no type-narrowing (unlike Slice B's credit-applications).
- **Memo surfaced:** the `Refund` domain record gains an additive `string? Memo` (init-only, default null) populated from the persisted body — backward-compatible.
- **Reuse** `<app-customer-select>` + the persisted per-client selection (`ReceivablesService.selectedCustomerId`); the chosen customer carries across all five tabs. No new customer-select. No contextual entry points (refunds recorded only from the Refunds tab). No `method` field (refunds carry none).
- **Angular template gotcha:** name the void method `doVoid`, not `void` (Angular template expressions parse `void` as the JS operator).
- **Exact API base in tests:** `http://localhost:5000`.

---

## File Structure

**Backend (`Modules/Receivables/`):**
- `Accounting101.Receivables/Disposition.cs` — add `Memo` to `Refund`.
- `Accounting101.Receivables/DocumentPaymentStore.cs` — populate `Memo` in `MapRefund`.
- `Accounting101.Receivables/PaymentService.cs` — new `GetRefundsByCustomerAsync` passthrough.
- `Accounting101.Receivables.Api/ReceivablesEndpoints.cs` — new `ListRefunds` handler + route.
- `Accounting101.Receivables.Tests/GetRefundsEndpointTests.cs` — new endpoint test.

**UI (`UI/Angular/src/app/`):**
- `core/receivables/receivables.ts` — `Refund`, `RefundRequest`.
- `core/receivables/receivables.service.ts` — `listRefunds`/`recordRefund`/`voidRefund`.
- `core/receivables/receivables.service.spec.ts` — service tests.
- `features/receivables/receivables-shell.ts` (+ `.spec.ts`) — add the Refunds tab.
- `features/receivables/refund-list.ts` (+ `.spec.ts`) — the Refunds home.
- `features/receivables/refund-editor.ts` (+ `.spec.ts`) — the amount-only form.
- `app.routes.ts` — `refunds` + `refunds/new` routes.

---

## Task 1: Backend — `GET /refunds` read endpoint (with memo surfaced)

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/Disposition.cs` (the `Refund` record)
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs` (`MapRefund`)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (route list ~line 24; handler near `ListPayments`)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/GetRefundsEndpointTests.cs`

**Interfaces:**
- Consumes: store read `GetRefundsByCustomerAsync` (port `IPaymentStore`); domain `Refund { Id, CustomerId, Date, Amount, Voided }` (+ new `Memo`).
- Produces:
  - `Refund` record gains `public string? Memo { get; init; }`.
  - `PaymentService.GetRefundsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) : Task<IReadOnlyList<Refund>>` (date-descending).
  - `GET /clients/{clientId}/refunds?customerId=` → `200` date-desc list, `400` when `customerId` missing.

- [ ] **Step 1: Write the failing endpoint test**

Create `Modules/Receivables/Accounting101.Receivables.Tests/GetRefundsEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;   // AccountRequest
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the read endpoint that powers the UI's Refunds list: it returns a customer's refunds
/// (amount + surfaced memo + voided) as a date-descending list, and rejects a missing customerId.</summary>
public sealed class GetRefundsEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
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
    public async Task GET_refunds_returns_date_descending_list_with_memo_and_voided()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        // Create $100 of unapplied credit: issue a $100 invoice, pay $200 allocating $100 → $100 credit.
        Guid invoiceId = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 2), 200m, "check",
                [new Allocation(invoiceId, 100m)]))).EnsureSuccessStatusCode();

        Refund first = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer.Id, new DateOnly(2026, 3, 5), 30m, "partial")))
            .Content.ReadFromJsonAsync<Refund>())!;
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer.Id, new DateOnly(2026, 3, 10), 40m, "balance")))
            .EnsureSuccessStatusCode();
        (await clerk.PostAsync($"/clients/{clientId}/refunds/{first.Id}/void", null)).EnsureSuccessStatusCode();

        Refund[] list = (await clerk.GetFromJsonAsync<Refund[]>(
            $"/clients/{clientId}/refunds?customerId={customer.Id}"))!;

        Assert.Equal(2, list.Length);
        Assert.Equal(40m, list[0].Amount);          // 3/10 newest first
        Assert.Equal("balance", list[0].Memo);
        Assert.False(list[0].Voided);
        Assert.Equal(30m, list[1].Amount);          // 3/5
        Assert.Equal("partial", list[1].Memo);
        Assert.True(list[1].Voided);
    }

    [Fact]
    public async Task GET_refunds_without_customerId_is_400()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/refunds");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~GetRefundsEndpointTests"`
Expected: FAIL — `Refund.Memo` doesn't exist (compile error), or once added, the `/refunds` GET returns 404.

- [ ] **Step 3: Surface memo on the `Refund` domain record**

In `Modules/Receivables/Accounting101.Receivables/Disposition.cs`, add a `Memo` property to `Refund`, after `Amount` and before `Voided`:

```csharp
public sealed record Refund
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public string? Memo { get; init; }
    public bool Voided { get; init; }
}
```

- [ ] **Step 4: Populate Memo in the store mapper**

In `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs`, update `MapRefund`:

```csharp
    private static Refund MapRefund(DocumentResult<RefundBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date,
        Amount = r.Body.Amount, Memo = r.Body.Memo, Voided = IsVoided(r.State),
    };
```

- [ ] **Step 5: Add `GetRefundsByCustomerAsync` to `PaymentService`**

In `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`, add (next to `GetPaymentsByCustomerAsync`):

```csharp
/// <summary>The customer's refunds (cash returned against credit), date-descending. Read-only; powers the
/// Refunds list. Includes voided refunds (greyed in the UI).</summary>
public async Task<IReadOnlyList<Refund>> GetRefundsByCustomerAsync(
    Guid clientId, Guid customerId, CancellationToken ct = default)
{
    IReadOnlyList<Refund> refunds = await payments.GetRefundsByCustomerAsync(clientId, customerId, ct);
    return refunds.OrderByDescending(r => r.Date).ToList();
}
```

- [ ] **Step 6: Add the `ListRefunds` handler + route**

In `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs`, register the route in `MapReceivablesEndpoints` (next to `clients.MapPost("/refunds", RecordRefund);`):

```csharp
        clients.MapGet("/refunds", ListRefunds);
```

Add the handler (mirror `ListPayments`):

```csharp
    private static async Task<IResult> ListRefunds(
        Guid clientId, Guid? customerId, PaymentService service, CancellationToken cancellationToken)
    {
        if (customerId is null || customerId == Guid.Empty)
            return Results.Problem("customerId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<Refund> refunds = await service.GetRefundsByCustomerAsync(clientId, customerId.Value, cancellationToken);
        return Results.Ok(refunds);
    }
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~GetRefundsEndpointTests"`
Expected: PASS (both facts).

- [ ] **Step 8: Run the full module suite (no regressions from the Memo change)**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`
Expected: PASS — all existing tests still green (the `Memo` addition is additive, init-only, default null).

- [ ] **Step 9: Commit**

```bash
git add Modules/Receivables/
git commit -m "feat(receivables): GET /refunds read endpoint + surface refund memo

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: UI model & service — refund types + three service methods

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.ts`
- Modify: `UI/Angular/src/app/core/receivables/receivables.service.ts`
- Test: `UI/Angular/src/app/core/receivables/receivables.service.spec.ts`

**Interfaces:**
- Consumes: existing `ReceivablesService.base()`, `client.clientId()` guard (returns `EMPTY` when no client), `HttpParams`.
- Produces (model, in `receivables.ts`):
  - `interface Refund { id: string; customerId: string; date: string; amount: number; memo: string | null; voided: boolean; }`
  - `interface RefundRequest { customerId: string; date: string; amount: number; memo: string | null; }`
- Produces (service, in `receivables.service.ts`):
  - `listRefunds(customerId: string): Observable<Refund[]>` → `GET /refunds?customerId=`
  - `recordRefund(req: RefundRequest): Observable<unknown>` → `POST /refunds`
  - `voidRefund(id: string, reason?: string | null): Observable<unknown>` → `POST /refunds/{id}/void`

- [ ] **Step 1: Write the failing service tests**

In `UI/Angular/src/app/core/receivables/receivables.service.spec.ts`, add to the `ReceivablesService` describe block:

```typescript
  it('listRefunds GETs /refunds?customerId=', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    let result: unknown[] | undefined;
    svc.listRefunds('cu1').subscribe(r => (result = r));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/refunds' && r.params.get('customerId') === 'cu1');
    expect(req.request.method).toBe('GET');
    req.flush([{ id: 'rf1', customerId: 'cu1', date: '2026-06-30', amount: 50, memo: 'x', voided: false }]);
    expect(result!.length).toBe(1);
  });

  it('recordRefund POSTs to /refunds', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.recordRefund({ customerId: 'cu1', date: '2026-06-30', amount: 50, memo: 'overpay' }).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/refunds');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ customerId: 'cu1', date: '2026-06-30', amount: 50, memo: 'overpay' });
    req.flush({});
  });

  it('voidRefund POSTs the reason to /refunds/{id}/void', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.voidRefund('rf1', 'oops').subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf1/void');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ reason: 'oops' });
    req.flush({});
  });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test`
Expected: FAIL — `svc.listRefunds` / `recordRefund` / `voidRefund` are not functions.

- [ ] **Step 3: Add the model types**

In `UI/Angular/src/app/core/receivables/receivables.ts`, append (near the `CreditDocument` block from Slice B):

```typescript
export interface Refund { id: string; customerId: string; date: string; amount: number; memo: string | null; voided: boolean; }
export interface RefundRequest { customerId: string; date: string; amount: number; memo: string | null; }
```

- [ ] **Step 4: Add the service methods**

In `UI/Angular/src/app/core/receivables/receivables.service.ts`, extend the model import (the line importing from `'./receivables'`) to include `Refund, RefundRequest`, then add these methods (after `voidCredit`, before the closing brace):

```typescript
  listRefunds(customerId: string): Observable<Refund[]> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<Refund[]>(this.base('/refunds'), { params: new HttpParams().set('customerId', customerId) });
  }
  recordRefund(req: RefundRequest): Observable<unknown> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post(this.base('/refunds'), req);
  }
  voidRefund(id: string, reason?: string | null): Observable<unknown> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.post(this.base(`/refunds/${id}/void`), { reason: reason ?? null });
  }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test`
Expected: PASS — the three new service tests green, no regressions.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/receivables/
git commit -m "feat(ui): receivables refund model + service (listRefunds/recordRefund/voidRefund)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: UI — add the Refunds tab to the shell

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/receivables-shell.ts`
- Test: `UI/Angular/src/app/features/receivables/receivables-shell.spec.ts`

**Interfaces:**
- Produces: a `data-testid="tab-refunds"` nav link with `routerLink="refunds"` in `ReceivablesShell`. (The `refunds` route is added in Task 4 — the tab 404s until then, which is fine.)

- [ ] **Step 1: Write the failing shell test**

In `UI/Angular/src/app/features/receivables/receivables-shell.spec.ts`, add (mirroring the existing `tab-credits` test):

```typescript
  it('renders the Refunds tab linking to refunds', () => {
    const f = TestBed.createComponent(ReceivablesShell); f.detectChanges();
    const tab = f.nativeElement.querySelector('[data-testid="tab-refunds"]') as HTMLAnchorElement;
    expect(tab).toBeTruthy();
    expect(tab.textContent!.trim()).toBe('Refunds');
    expect(tab.getAttribute('href')).toContain('refunds');
  });
```

> If the existing spec uses a `setup()`/TestBed configuration helper, follow it; the shell injects no services (mirror the `tab-credits` test already in this file).

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd UI/Angular && npm test`
Expected: FAIL — no element with `data-testid="tab-refunds"`.

- [ ] **Step 3: Add the Refunds tab to the shell template**

In `UI/Angular/src/app/features/receivables/receivables-shell.ts`, add a 5th tab after the Credits link (before `</nav>`):

```html
        <a routerLink="refunds"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-refunds">Refunds</a>
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd UI/Angular && npm test`
Expected: PASS — the Refunds tab renders.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/receivables/receivables-shell.ts UI/Angular/src/app/features/receivables/receivables-shell.spec.ts
git commit -m "feat(ui): add Refunds tab to the Receivables shell

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: UI — `RefundList` (the Refunds home)

**Files:**
- Create: `UI/Angular/src/app/features/receivables/refund-list.ts`
- Test: `UI/Angular/src/app/features/receivables/refund-list.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (import + `refunds` route)

**Interfaces:**
- Consumes: `ReceivablesService` (`load`, `customers`, `selectedCustomerId`, `listRefunds`, `voidRefund`); `Refund`; `money`/`displayDate`; `extractProblem`; `<app-customer-select>`; `HlmButton`, `HlmTableImports`.
- Produces: `RefundList` at route `receivables/refunds`. An **Issue refund** link → `/receivables/refunds/new?customer=<id>` (disabled-styled with no customer). Table **Date · Amount · Memo · Status** + a Void action on non-voided rows.

- [ ] **Step 1: Write the failing component tests**

Create `UI/Angular/src/app/features/receivables/refund-list.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RefundList } from './refund-list';
import { ClientContextService } from '../../core/client/client-context.service';

function setup() {
  localStorage.clear();
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const refund = (id: string, amount: number, memo: string | null, voided = false) =>
  ({ id, customerId: 'cu1', date: '2026-06-30', amount, memo, voided });

function loadCustomerAndRefunds(ctrl: HttpTestingController, f: any, rows: unknown[]) {
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  f.detectChanges();
  f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/refunds') && r.params.get('customerId') === 'cu1').flush(rows);
  f.detectChanges();
}

describe('RefundList', () => {
  it('loads refunds for the selected customer and renders amount/memo', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'overpayment')]);
    const text = f.nativeElement.textContent;
    expect(text).toContain('50.00');
    expect(text).toContain('overpayment');
  });

  it('Void shows on non-voided rows, posts to the right path, and reloads', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'x')]);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void') as HTMLButtonElement;
    expect(voidBtn).toBeTruthy();
    voidBtn.click();
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf1/void').flush({});
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/refunds')).flush([refund('rf1', 50, 'x', true)]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Voided');
  });

  it('hides Void on a voided row', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'x', true)]);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void');
    expect(voidBtn).toBeUndefined();
  });

  it('Issue refund link targets the editor for the selected customer; disabled with none', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    const link = () => [...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Issue refund') as HTMLAnchorElement;
    expect(link().className).toContain('opacity-50');
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/refunds')).flush([]);
    f.detectChanges();
    expect(link().getAttribute('href')).toContain('/receivables/refunds/new');
    expect(link().getAttribute('href')).toContain('customer=cu1');
  });

  it('shows the empty states', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No customers yet');
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test`
Expected: FAIL — cannot find module `./refund-list`.

- [ ] **Step 3: Implement `RefundList`**

Create `UI/Angular/src/app/features/receivables/refund-list.ts` (mirrors `CreditList`: `combineLatest([customerId, refresh$])` reload, `refresh$` a `BehaviorSubject`, `doVoid` triggers `refresh$.next()`):

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { toObservable, toSignal, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BehaviorSubject, catchError, combineLatest, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { Refund } from '../../core/receivables/receivables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { CustomerSelect } from '../../shared/customer-select';

@Component({
  selector: 'app-refund-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, CustomerSelect],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Refunds</h1>
        <app-customer-select />
        <a hlmBtn size="sm" class="ms-auto"
           routerLink="/receivables/refunds/new"
           [queryParams]="{ customer: customerId() }"
           [class.pointer-events-none]="!customerId()"
           [class.opacity-50]="!customerId()">
          Issue refund
        </a>
      </div>

      @if (svc.customers().length === 0) {
        <p class="text-muted-foreground text-sm">No customers yet — <a routerLink="/receivables/customers" class="underline">add one first</a>.</p>
      } @else if (!customerId()) {
        <p class="text-muted-foreground text-sm">Select a customer to view refunds.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (refunds().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No refunds recorded.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>Date</th><th hlmTh>Amount</th><th hlmTh>Memo</th><th hlmTh>Status</th><th hlmTh></th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (r of refunds(); track r.id) {
                  <tr hlmTr [class.opacity-50]="r.voided">
                    <td hlmTd>{{ fmtDate(r.date) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(r.amount) }}</td>
                    <td hlmTd>{{ r.memo ?? '—' }}</td>
                    <td hlmTd>{{ r.voided ? 'Voided' : 'Active' }}</td>
                    <td hlmTd>
                      @if (!r.voided) {
                        <button hlmBtn size="sm" variant="outline" (click)="doVoid(r)" [disabled]="busy()">Void</button>
                      } @else { <span class="text-muted-foreground">—</span> }
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
export class RefundList {
  readonly svc = inject(ReceivablesService);
  private readonly destroyRef = inject(DestroyRef);
  readonly customerId = this.svc.selectedCustomerId;
  readonly listError = signal<string | null>(null);
  readonly busy = signal(false);
  private readonly refresh$ = new BehaviorSubject(0);

  readonly refunds = toSignal(
    combineLatest([toObservable(this.customerId), this.refresh$]).pipe(
      switchMap(([cid]) => {
        if (!cid) return of([] as Refund[]);
        this.listError.set(null);
        return this.svc.listRefunds(cid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as Refund[]); }),
        );
      }),
    ),
    { initialValue: [] as Refund[] },
  );

  constructor() { this.svc.load(); }

  doVoid(r: Refund): void {
    this.busy.set(true); this.listError.set(null);
    this.svc.voidRefund(r.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.refresh$.next(this.refresh$.value + 1); },
      error: e => { this.listError.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
```

- [ ] **Step 4: Add the `refunds` route**

In `UI/Angular/src/app/app.routes.ts`, add the import (near the other receivables imports):
```typescript
import { RefundList } from './features/receivables/refund-list';
```
And add the child route inside the `receivables` children (after the `credits` route):
```typescript
    { path: 'refunds', component: RefundList },
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test`
Expected: PASS — all five `RefundList` tests green.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/refund-list.ts UI/Angular/src/app/features/receivables/refund-list.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): Refunds tab home (RefundList) with per-doc void

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: UI — `RefundEditor` (the amount-only form)

**Files:**
- Create: `UI/Angular/src/app/features/receivables/refund-editor.ts`
- Test: `UI/Angular/src/app/features/receivables/refund-editor.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (import + `refunds/new` route)

**Interfaces:**
- Consumes: `ReceivablesService` (`load`, `customerName`, `creditBalance`, `recordRefund`); `money`/`displayDate`; `extractProblem`; `CurrencyInput`; `HlmInputImports`, `HlmLabelImports`, `HlmButton`; `ActivatedRoute`/`Router`.
- Produces: `RefundEditor` at route `receivables/refunds/new?customer=<id>` (redirects to `/receivables/refunds` if `customer` absent).

**Form model (signals):** `amount: number`, `date` (today), `memo: string`, `creditBalance: number`. Public members the spec drives: `amount`, `date`, `memo`, `creditBalance`, `valid` (computed), `save`.

- [ ] **Step 1: Write the failing component tests**

Create `UI/Angular/src/app/features/receivables/refund-editor.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RefundEditor } from './refund-editor';
import { ClientContextService } from '../../core/client/client-context.service';

function setup(customer: string | null) {
  localStorage.clear();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: (k: string) => (k === 'customer' ? customer : null) } } } },
    ],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

function loadCreditBalance(ctrl: HttpTestingController, f: any, balance: number) {
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/customers/cu1/credit-balance')).flush({ customerId: 'cu1', creditBalance: balance });
  f.detectChanges();
}

describe('RefundEditor', () => {
  it('redirects to /receivables/refunds when reached without a customer', () => {
    setup(null);
    const nav = spyOn(TestBed.inject(Router), 'navigate');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    expect(nav).toHaveBeenCalledWith(['/receivables/refunds']);
  });

  it('defaults the amount to the loaded available credit', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 75);
    expect(f.componentInstance.amount()).toBe(75);
    expect(f.componentInstance.valid()).toBe(true);
  });

  it('is invalid when the amount exceeds available credit', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 75);
    f.componentInstance.amount.set(100); f.detectChanges();
    expect(f.componentInstance.valid()).toBe(false);
  });

  it('is invalid when there is no available credit', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 0);
    expect(f.componentInstance.amount()).toBe(0);
    expect(f.componentInstance.valid()).toBe(false);
  });

  it('submits the refund payload and navigates to the refunds list', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 75);
    const nav = spyOn(TestBed.inject(Router), 'navigate');
    f.componentInstance.amount.set(50); f.componentInstance.memo.set('overpay'); f.detectChanges();
    f.componentInstance.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/refunds');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ customerId: 'cu1', date: f.componentInstance.date(), amount: 50, memo: 'overpay' });
    req.flush({});
    expect(nav).toHaveBeenCalledWith(['/receivables/refunds']);
  });

  it('relays a 422 error inline', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 75);
    f.componentInstance.amount.set(50); f.detectChanges();
    f.componentInstance.save();
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds').flush(
      { type: 'about:blank', title: 'Unprocessable', detail: 'Refund of 50 exceeds available credit 40.', status: 422 },
      { status: 422, statusText: 'Unprocessable Entity' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('exceeds available credit');
  });
});
```

> NOTE on the spy: the project test runner is Vitest. If `spyOn` is undefined in this spec, use `vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true)` (the pattern used in `adjustment-editor.spec.ts`). Match whatever that sibling spec does.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test`
Expected: FAIL — cannot find module `./refund-editor`.

- [ ] **Step 3: Implement `RefundEditor`**

Create `UI/Angular/src/app/features/receivables/refund-editor.ts` (mirrors `PaymentEditor`, amount-only):

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

@Component({
  selector: 'app-refund-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables/refunds" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Refunds</a>
      <h1 class="text-2xl font-bold">Issue refund</h1>
      <p class="text-sm text-muted-foreground">{{ svc.customerName(customerId!) }}</p>
      <p class="text-sm" [class.text-destructive]="amount() > creditBalance()">
        Available credit {{ money(creditBalance()) }}
      </p>

      <div class="grid grid-cols-2 gap-4 max-w-md">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Refund amount</label>
          <app-currency-input ariaLabel="Refund amount" [value]="amount()" (valueChange)="amount.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
      </div>
      <div class="flex flex-col gap-1 max-w-md">
        <label hlmLabel>Memo</label>
        <input hlmInput type="text" placeholder="reason…" [value]="memo()" (input)="memo.set($any($event.target).value)" />
      </div>

      <p class="text-xs text-muted-foreground">
        Issuing a refund posts a cash entry that needs approval before it affects the statements.
        The customer's credit balance updates immediately.
      </p>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Issue refund</button>
        <a hlmBtn variant="outline" routerLink="/receivables/refunds">Cancel</a>
      </div>
    </div>
  `,
})
export class RefundEditor {
  readonly svc = inject(ReceivablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly customerId = this.route.snapshot.queryParamMap.get('customer');

  readonly amount = signal(0);
  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly memo = signal('');
  readonly creditBalance = signal(0);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly valid = computed(() => this.amount() > 0 && this.amount() <= this.creditBalance());

  constructor() {
    if (!this.customerId) { void this.router.navigate(['/receivables/refunds']); return; }
    this.svc.load();
    this.svc.creditBalance(this.customerId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(b => {
      this.creditBalance.set(b);
      this.amount.set(b);                 // default to the full available credit
    });
  }

  save(): void {
    if (!this.valid() || !this.customerId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordRefund({ customerId: this.customerId, date: this.date(), amount: this.amount(), memo: this.memo().trim() || null })
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: () => { this.busy.set(false); void this.router.navigate(['/receivables/refunds']); },
        error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
      });
  }

  money(n: number): string { return fmtMoney(n); }
}
```

> NOTE: `takeUntilDestroyed(this.destroyRef)` inside `save()` requires `this.destroyRef` (a `DestroyRef` captured in a field) — already declared above. This matches the hygiene pattern now used across receivables.

- [ ] **Step 4: Add the `refunds/new` route**

In `UI/Angular/src/app/app.routes.ts`, add the import:
```typescript
import { RefundEditor } from './features/receivables/refund-editor';
```
And the child route (after the `refunds` route from Task 4):
```typescript
    { path: 'refunds/new', component: RefundEditor },
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test`
Expected: PASS — all `RefundEditor` tests green.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/refund-editor.ts UI/Angular/src/app/features/receivables/refund-editor.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): RefundEditor (amount-only, capped at available credit)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Run the full backend solution tests**

Run: `dotnet test`
Expected: PASS — full solution green (no regression from the Memo change or the new endpoint).

- [ ] **Step 2: Run the full UI suite**

Run: `cd UI/Angular && npm test`
Expected: PASS — all specs green (service + shell + refund-list + refund-editor + existing).

- [ ] **Step 3: Build the UI (type-check the production build)**

Run: `cd UI/Angular && npm run build`
Expected: build succeeds with no type errors (a pre-existing bundle-budget WARNING is fine).

- [ ] **Step 4: Manual smoke (optional, with dev stack running)**

Receivables → Refunds → pick a customer with credit → Issue refund → amount defaults to available credit → submit → returns to the list; the entry appears in Approvals; Void shows on non-voided rows.

---

## Self-Review

**1. Spec coverage:**
- `GET /refunds?customerId=` (date-desc, 400 on missing) → Task 1. ✅
- Surface `Refund.Memo` → Task 1 Steps 3–4. ✅
- UI model + 3 service methods → Task 2. ✅
- Refunds tab in shell → Task 3. ✅
- `RefundList` (columns, void on non-voided, empty states, issue-refund link) → Task 4. ✅
- `RefundEditor` (redirect without customer, amount default-to-credit, cap, submit payload, error relay, zero-credit invalid) → Task 5. ✅
- Routes `refunds` + `refunds/new` → Tasks 4 & 5. ✅
- Deferred (credit overview / analytics / deep-links) → out of scope, not planned. ✅

**2. Placeholder scan:** All steps contain concrete code/commands. The two `>` notes (Vitest `vi.spyOn` fallback in Task 5; the `destroyRef` reminder) are guidance with exact code, not TODOs.

**3. Type consistency:** `Refund` / `RefundRequest` field names match across Tasks 1/2/4/5 (`id/customerId/date/amount/memo/voided`). Service method names (`listRefunds`/`recordRefund`/`voidRefund`) match between Task 2 (definition) and Tasks 4/5 (consumption). Backend `Refund` record field order/names match the UI interface. `doVoid`/`refresh$`/`valid`/`amount` names consistent.

**Note for the implementer:** Everything is mechanical and mirrors the shipped Slice B (Credits) patterns. The one project-specific gotcha (`void` is reserved in Angular templates → name the method `doVoid`) is already baked into the code above.
