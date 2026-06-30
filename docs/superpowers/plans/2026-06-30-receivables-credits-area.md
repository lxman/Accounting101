# Receivables Credits Area (Slice B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Credits tab to the Receivables hub — list/void a customer's allocation-based dispositions (credit note · write-off · apply credit) and record them through one unified "Adjust" form — backed by a single new `GET /credits` read endpoint.

**Architecture:** One new backend read endpoint (`GET /clients/{id}/credits?customerId=`) returns a unified, date-descending `CreditDocument` list across the three existing disposition stores. The UI adds a 4th hub tab with a `CreditList` home (mirrors `PaymentList`) and an `AdjustmentEditor` form (mirrors `PaymentEditor`) that dispatches to the three existing POST endpoints by type. All write paths, their stores, and their void endpoints already exist; this slice only adds the unified read + the UI.

**Tech Stack:** ASP.NET Core minimal APIs (.NET 10) + Angular 22 (standalone, signals, zoneless) + Spartan-ng helm + Tailwind. Backend tests: xUnit (`dotnet test`). UI tests: `@angular/build:unit-test` (vitest) via `npm test`.

## Global Constraints

- **Currency:** USD-only; no FX. Money formatted via existing `money()` from `core/format/display`.
- **Settlement is document-driven:** recording a disposition reduces the targeted invoice's open balance immediately; the posted GL entry lands `PendingApproval` and only affects the statements after approval. Every form states this.
- **Authorization:** the read endpoint uses the same Read authorization as the other module GETs (no module credential). Routes sit under the existing `RequireAuthorization()` group.
- **`credit-application` has no void endpoint** (backend gap, deferred). The Credits list hides Void for that type; `voidCredit` is never called with `'credit-application'`.
- **No new customer-select.** Reuse `<app-customer-select>` bound to the persisted per-client selection (`ReceivablesService.selectedCustomerId`). The chosen customer carries across all four tabs.
- **No contextual entry points.** Credits are recorded only from the Credits tab (matching the one-place payment decision).
- **Memo is surfaced (user decision 2026-06-30):** `WriteOff`/`CreditNote` domain records gain a `Memo` field populated from the already-persisted document body, so the Credits list Memo column shows real memos. `credit-application` has no memo (always null).
- **Exact API base in tests:** `http://localhost:5000` (from `core/api/environment`).

---

## File Structure

**Backend (`Modules/Receivables/`):**
- `Accounting101.Receivables/Disposition.cs` — add `Memo` to `WriteOff` + `CreditNote`.
- `Accounting101.Receivables/DocumentPaymentStore.cs` — populate `Memo` in `MapWriteOff`/`MapCreditNote`.
- `Accounting101.Receivables/CreditDocument.cs` — new `CreditDocument` read model (in the **core** project, namespace `Accounting101.Receivables`; the core `PaymentService` returns it and core cannot reference `.Api`).
- `Accounting101.Receivables/PaymentService.cs` — new `GetCreditsByCustomerAsync` mapper/passthrough.
- `Accounting101.Receivables.Api/ReceivablesEndpoints.cs` — new `ListCredits` handler + route.
- `Accounting101.Receivables.Tests/GetCreditsEndpointTests.cs` — new endpoint test.

**UI (`UI/Angular/src/app/`):**
- `core/receivables/receivables.ts` — `CreditType`, `CreditDocument`, three request types.
- `core/receivables/receivables.service.ts` — `listCredits`/`recordCreditNote`/`recordWriteOff`/`applyCredit`/`voidCredit`.
- `core/receivables/receivables.service.spec.ts` — service tests for the new methods.
- `features/receivables/receivables-shell.ts` (+ `.spec.ts`) — add the Credits tab.
- `features/receivables/credit-list.ts` (+ `.spec.ts`) — the Credits home.
- `features/receivables/adjustment-editor.ts` (+ `.spec.ts`) — the unified Adjust form.
- `app.routes.ts` — `credits` + `credits/new` routes.

---

## Task 1: Backend — `GET /credits` unified read endpoint (with memo surfaced)

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/Disposition.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs` (`MapWriteOff` ~line 146, `MapCreditNote` ~line 152)
- Create: `Modules/Receivables/Accounting101.Receivables/CreditDocument.cs` (core project — verified: core does NOT reference `.Api`, so the read model returned by `PaymentService` must live here)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (route list ~line 24; add handler near `ListPayments` ~line 171)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/GetCreditsEndpointTests.cs`

**Interfaces:**
- Consumes: existing store reads `GetCreditNotesByCustomerAsync`, `GetWriteOffsByCustomerAsync`, `GetCreditApplicationsByCustomerAsync`; domain records `CreditNote`/`WriteOff` (computed `Total`), `CreditApplication` (computed `Applied`); `Allocation(Guid TargetId, decimal Amount)`.
- Produces:
  - `record CreditDocument(string Type, Guid Id, Guid CustomerId, DateOnly Date, decimal Amount, string? Memo, IReadOnlyList<Allocation> Allocations, bool Voided)` in namespace `Accounting101.Receivables` (core project).
  - `PaymentService.GetCreditsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) : Task<IReadOnlyList<CreditDocument>>`.
  - `GET /clients/{clientId}/credits?customerId=` → `200` unified date-desc list, `400` when `customerId` missing.

- [ ] **Step 1: Write the failing endpoint test**

Create `Modules/Receivables/Accounting101.Receivables.Tests/GetCreditsEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;   // AccountRequest
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the unified read endpoint that powers the UI's Credits list: it returns a customer's
/// credit-notes, write-offs, and credit-applications as one date-descending list (correct type tag,
/// amount = Σ allocations, memo for notes/write-offs and null for credit-applications) and rejects a
/// missing customerId.</summary>
public sealed class GetCreditsEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", "Customer");
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
        await PutAccountAsync(controller, clientId, fixture.BadDebtExpenseAccountId, "6000", "Bad Debt Expense", "Expense", null);
        await PutAccountAsync(controller, clientId, fixture.SalesReturnsAccountId, "4900", "Sales Returns", "Revenue", null);
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
    public async Task GET_credits_returns_unified_date_descending_list_with_memo()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv1 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);   // → write-off
        Guid inv2 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);   // → credit-note
        Guid inv3 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);   // → overpaid (creates credit)
        Guid inv4 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);   // → credit-application target

        // Overpay inv3 by 50 → 50 of unapplied customer credit.
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 2), 150m, "check",
                [new Allocation(inv3, 100m)]))).EnsureSuccessStatusCode();

        (await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer.Id, new DateOnly(2026, 3, 5), [new Allocation(inv1, 100m)], "uncollectible")))
            .EnsureSuccessStatusCode();
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
            new CreditApplicationRequest(customer.Id, new DateOnly(2026, 3, 8), [new Allocation(inv4, 50m)])))
            .EnsureSuccessStatusCode();
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 10), [new Allocation(inv2, 100m)], "returned goods")))
            .EnsureSuccessStatusCode();

        CreditDocument[] list = (await clerk.GetFromJsonAsync<CreditDocument[]>(
            $"/clients/{clientId}/credits?customerId={customer.Id}"))!;

        Assert.Equal(3, list.Length);                          // payment is not a credit doc
        Assert.Equal("credit-note", list[0].Type);             // 3/10 — newest first
        Assert.Equal(100m, list[0].Amount);
        Assert.Equal("returned goods", list[0].Memo);
        Assert.False(list[0].Voided);

        Assert.Equal("credit-application", list[1].Type);      // 3/8
        Assert.Equal(50m, list[1].Amount);
        Assert.Null(list[1].Memo);

        Assert.Equal("write-off", list[2].Type);               // 3/5
        Assert.Equal(100m, list[2].Amount);
        Assert.Equal("uncollectible", list[2].Memo);
    }

    [Fact]
    public async Task GET_credits_without_customerId_is_400()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/credits");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~GetCreditsEndpointTests"`
Expected: FAIL — `CreditDocument` does not exist (compile error), or once that's added, the `/credits` GET returns 404.

- [ ] **Step 3: Surface memo onto the WriteOff and CreditNote domain records**

In `Modules/Receivables/Accounting101.Receivables/Disposition.cs`, add a `Memo` property to both records (the data is already in the document body — `WriteOffBody.Memo` / `CreditNoteBody.Memo`):

In `WriteOff` (after `Allocations`, before `Voided`):
```csharp
    public string? Memo { get; init; }
```
In `CreditNote` (same placement):
```csharp
    public string? Memo { get; init; }
```

- [ ] **Step 4: Populate Memo in the store mappers**

In `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs`, update both mappers to copy the body memo:

`MapWriteOff`:
```csharp
    private static WriteOff MapWriteOff(DocumentResult<WriteOffBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date,
        Allocations = r.Body.Allocations, Memo = r.Body.Memo, Voided = IsVoided(r.State),
    };
```
`MapCreditNote`:
```csharp
    private static CreditNote MapCreditNote(DocumentResult<CreditNoteBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date,
        Allocations = r.Body.Allocations, Memo = r.Body.Memo, Voided = IsVoided(r.State),
    };
```

- [ ] **Step 5: Add the `CreditDocument` read model (in the core project)**

Create `Modules/Receivables/Accounting101.Receivables/CreditDocument.cs`. It lives in **core** (namespace `Accounting101.Receivables`) — not `.Api` — because the core `PaymentService` returns it and core has no reference to `.Api` (verified: would be a circular dependency). The endpoint in `.Api` sees it fine (Api → core), exactly as `ListPayments` returns the core `Payment` type. The test (`namespace Accounting101.Receivables.Tests`) resolves it via enclosing-namespace lookup, no extra `using` needed.

```csharp
using Accounting101.Settlement;   // Allocation

namespace Accounting101.Receivables;

/// <summary>A unified read view of a customer's allocation-based dispositions — credit note, write-off,
/// or credit application — for the Credits list. Amount is Σ allocations; Memo is null for credit
/// applications (which carry none).</summary>
public sealed record CreditDocument(
    string Type,            // "credit-note" | "write-off" | "credit-application"
    Guid Id, Guid CustomerId, DateOnly Date,
    decimal Amount, string? Memo,
    IReadOnlyList<Allocation> Allocations, bool Voided);
```

- [ ] **Step 6: Add `GetCreditsByCustomerAsync` to `PaymentService`**

In `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`, add (next to `GetPaymentsByCustomerAsync`). `CreditDocument` is in the same `Accounting101.Receivables` namespace (Step 5), so no extra `using` is needed:

```csharp
/// <summary>The customer's allocation-based dispositions — credit notes, write-offs, and credit
/// applications — as one date-descending list. Read-only; powers the Credits list. Memo comes from the
/// stored note/write-off; credit applications carry none.</summary>
public async Task<IReadOnlyList<CreditDocument>> GetCreditsByCustomerAsync(
    Guid clientId, Guid customerId, CancellationToken ct = default)
{
    IReadOnlyList<CreditNote> notes = await payments.GetCreditNotesByCustomerAsync(clientId, customerId, ct);
    IReadOnlyList<WriteOff> writeOffs = await payments.GetWriteOffsByCustomerAsync(clientId, customerId, ct);
    IReadOnlyList<CreditApplication> apps = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);

    IEnumerable<CreditDocument> all =
        notes.Select(n => new CreditDocument("credit-note", n.Id, n.CustomerId, n.Date, n.Total, n.Memo, n.Allocations, n.Voided))
        .Concat(writeOffs.Select(w => new CreditDocument("write-off", w.Id, w.CustomerId, w.Date, w.Total, w.Memo, w.Allocations, w.Voided)))
        .Concat(apps.Select(a => new CreditDocument("credit-application", a.Id, a.CustomerId, a.Date, a.Applied, null, a.Allocations, a.Voided)));

    return all.OrderByDescending(c => c.Date).ToList();
}
```

- [ ] **Step 7: Add the `ListCredits` handler + route**

In `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs`, register the route in `MapReceivablesEndpoints` (next to `clients.MapGet("/payments", ListPayments);`):

```csharp
        clients.MapGet("/credits", ListCredits);
```

Add the handler (mirror `ListPayments`, near it):

```csharp
    private static async Task<IResult> ListCredits(
        Guid clientId, Guid? customerId, PaymentService service, CancellationToken cancellationToken)
    {
        if (customerId is null || customerId == Guid.Empty)
            return Results.Problem("customerId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<CreditDocument> credits = await service.GetCreditsByCustomerAsync(clientId, customerId.Value, cancellationToken);
        return Results.Ok(credits);
    }
```

- [ ] **Step 8: Run the test to verify it passes**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~GetCreditsEndpointTests"`
Expected: PASS (both facts).

- [ ] **Step 9: Run the full module test suite (no regressions from the Memo/mapper change)**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`
Expected: PASS — all existing tests still green (the `Memo` addition is additive; `init`-only with default null).

- [ ] **Step 10: Commit**

```bash
git add Modules/Receivables/
git commit -m "feat(receivables): unified GET /credits read endpoint + surface disposition memo

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: UI model & service — credit types + the five service methods

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.ts`
- Modify: `UI/Angular/src/app/core/receivables/receivables.service.ts`
- Test: `UI/Angular/src/app/core/receivables/receivables.service.spec.ts`

**Interfaces:**
- Consumes: existing `PaymentAllocation { targetId: string; amount: number }`; `ReceivablesService.base()`, `client.clientId()` guard pattern (returns `EMPTY` when no client).
- Produces (model, in `receivables.ts`):
  - `type CreditType = 'credit-note' | 'write-off' | 'credit-application'`
  - `interface CreditDocument { type: CreditType; id: string; customerId: string; date: string; amount: number; memo: string | null; allocations: PaymentAllocation[]; voided: boolean; }`
  - `interface CreditNoteRequest { customerId: string; date: string; allocations: PaymentAllocation[]; memo: string | null; }`
  - `interface WriteOffRequest { customerId: string; date: string; allocations: PaymentAllocation[]; memo: string | null; }`
  - `interface CreditApplyRequest { customerId: string; date: string; allocations: PaymentAllocation[]; }`
- Produces (service, in `receivables.service.ts`):
  - `listCredits(customerId: string): Observable<CreditDocument[]>` → `GET /credits?customerId=`
  - `recordCreditNote(req: CreditNoteRequest): Observable<unknown>` → `POST /credit-notes`
  - `recordWriteOff(req: WriteOffRequest): Observable<unknown>` → `POST /write-offs`
  - `applyCredit(req: CreditApplyRequest): Observable<unknown>` → `POST /credit-applications`
  - `voidCredit(type: CreditType, id: string, reason?: string | null): Observable<unknown>` → `POST /credit-notes/{id}/void` or `/write-offs/{id}/void`

- [ ] **Step 1: Write the failing service tests**

In `UI/Angular/src/app/core/receivables/receivables.service.spec.ts`, add to the `ReceivablesService` describe block (and add `CreditDocument` to the model import at top if you assert on it):

```typescript
  it('listCredits GETs /credits?customerId=', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    let result: unknown[] | undefined;
    svc.listCredits('cu1').subscribe(c => (result = c));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/credits' && r.params.get('customerId') === 'cu1');
    expect(req.request.method).toBe('GET');
    req.flush([{ type: 'credit-note', id: 'cn1', customerId: 'cu1', date: '2026-06-30', amount: 100, memo: 'x', allocations: [{ targetId: 'inv1', amount: 100 }], voided: false }]);
    expect(result!.length).toBe(1);
  });

  it('recordCreditNote POSTs to /credit-notes', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.recordCreditNote({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 100 }], memo: 'returned' }).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 100 }], memo: 'returned' });
    req.flush({});
  });

  it('recordWriteOff POSTs to /write-offs', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.recordWriteOff({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 100 }], memo: 'bad debt' }).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/write-offs');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.memo).toBe('bad debt');
    req.flush({});
  });

  it('applyCredit POSTs to /credit-applications (no memo field)', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.applyCredit({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 50 }] }).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/credit-applications');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 50 }] });
    req.flush({});
  });

  it('voidCredit maps type to the right path (credit-note / write-off)', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.voidCredit('credit-note', 'cn1', 'oops').subscribe();
    const a = ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes/cn1/void');
    expect(a.request.method).toBe('POST'); expect(a.request.body).toEqual({ reason: 'oops' }); a.flush({});
    svc.voidCredit('write-off', 'wo1').subscribe();
    const b = ctrl.expectOne('http://localhost:5000/clients/C1/write-offs/wo1/void');
    expect(b.request.method).toBe('POST'); expect(b.request.body).toEqual({ reason: null }); b.flush({});
  });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test`
Expected: FAIL — `svc.listCredits` / `recordCreditNote` / `recordWriteOff` / `applyCredit` / `voidCredit` are not functions (compile/type error).

- [ ] **Step 3: Add the model types**

In `UI/Angular/src/app/core/receivables/receivables.ts`, append (after the `Payment`/`RecordPaymentRequest` block, near line 45):

```typescript
export type CreditType = 'credit-note' | 'write-off' | 'credit-application';
export interface CreditDocument {
  type: CreditType; id: string; customerId: string; date: string;
  amount: number; memo: string | null; allocations: PaymentAllocation[]; voided: boolean;
}
export interface CreditNoteRequest { customerId: string; date: string; allocations: PaymentAllocation[]; memo: string | null; }
export interface WriteOffRequest   { customerId: string; date: string; allocations: PaymentAllocation[]; memo: string | null; }
export interface CreditApplyRequest { customerId: string; date: string; allocations: PaymentAllocation[]; }
```

- [ ] **Step 4: Add the service methods**

In `UI/Angular/src/app/core/receivables/receivables.service.ts`:

First extend the model import (line 7) to include the new types:
```typescript
import { Customer, DraftInvoiceRequest, Invoice, InvoiceListQuery, InvoiceView, Payment, RecordPaymentRequest, CreditDocument, CreditType, CreditNoteRequest, WriteOffRequest, CreditApplyRequest } from './receivables';
```

Then add these methods (after `creditBalance`, before the closing brace):
```typescript
  listCredits(customerId: string): Observable<CreditDocument[]> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<CreditDocument[]>(this.base('/credits'), { params: new HttpParams().set('customerId', customerId) });
  }
  recordCreditNote(req: CreditNoteRequest): Observable<unknown> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post(this.base('/credit-notes'), req);
  }
  recordWriteOff(req: WriteOffRequest): Observable<unknown> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post(this.base('/write-offs'), req);
  }
  applyCredit(req: CreditApplyRequest): Observable<unknown> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post(this.base('/credit-applications'), req);
  }
  voidCredit(type: CreditType, id: string, reason?: string | null): Observable<unknown> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    const segment = type === 'write-off' ? 'write-offs' : 'credit-notes';   // credit-application: never called (no endpoint)
    return this.http.post(this.base(`/${segment}/${id}/void`), { reason: reason ?? null });
  }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test`
Expected: PASS — the five new service tests green, no regressions.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/receivables/
git commit -m "feat(ui): receivables credit model + service (listCredits/record*/applyCredit/voidCredit)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: UI — add the Credits tab to the shell + routes

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/receivables-shell.ts`
- Test: `UI/Angular/src/app/features/receivables/receivables-shell.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `CreditList`, `AdjustmentEditor` components (created in Tasks 4 & 5). To keep this task independently runnable, the routes are added in Task 4/5's commits; **this task adds only the shell tab** (the nav link). The `credits` child route is added in Task 4.
- Produces: a `data-testid="tab-credits"` nav link with `routerLink="credits"` in `ReceivablesShell`.

- [ ] **Step 1: Write/extend the failing shell test**

In `UI/Angular/src/app/features/receivables/receivables-shell.spec.ts`, add a test asserting the Credits tab exists (match the file's existing setup idiom — provideRouter/zoneless; if the spec already renders the shell and checks tabs, add an assertion):

```typescript
  it('renders the Credits tab linking to credits', () => {
    const ctrl = setup();   // or the file's existing harness
    const f = TestBed.createComponent(ReceivablesShell); f.detectChanges();
    const tab = f.nativeElement.querySelector('[data-testid="tab-credits"]') as HTMLAnchorElement;
    expect(tab).toBeTruthy();
    expect(tab.textContent!.trim()).toBe('Credits');
    expect(tab.getAttribute('href')).toContain('credits');
  });
```

> If `receivables-shell.spec.ts` has no `setup()` helper, mirror `payment-list.spec.ts`'s providers: `provideZonelessChangeDetection(), provideRouter([])`. The shell injects no services.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd UI/Angular && npm test`
Expected: FAIL — no element with `data-testid="tab-credits"`.

- [ ] **Step 3: Add the Credits tab to the shell template**

In `UI/Angular/src/app/features/receivables/receivables-shell.ts`, add a 4th tab after the Customers link (before `</nav>`):

```html
        <a routerLink="credits"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-credits">Credits</a>
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd UI/Angular && npm test`
Expected: PASS — the Credits tab renders.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/receivables/receivables-shell.ts UI/Angular/src/app/features/receivables/receivables-shell.spec.ts
git commit -m "feat(ui): add Credits tab to the Receivables shell

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: UI — `CreditList` (the Credits home)

**Files:**
- Create: `UI/Angular/src/app/features/receivables/credit-list.ts`
- Test: `UI/Angular/src/app/features/receivables/credit-list.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (import + `credits` route)

**Interfaces:**
- Consumes: `ReceivablesService` (`load`, `customers`, `selectedCustomerId`, `listCredits`, `voidCredit`, `customerName`); `CreditDocument`, `CreditType`; `money`/`displayDate`; `extractProblem`; `<app-customer-select>`; `HlmButton`, `HlmTableImports`.
- Produces: `CreditList` component at route `receivables/credits`. A `Record adjustment` link → `/receivables/credits/new?customer=<id>` (disabled-styled with no customer). Table columns **Date · Type · Amount · Memo · Status**; Void button only on non-voided credit-note/write-off.

- [ ] **Step 1: Write the failing component tests**

Create `UI/Angular/src/app/features/receivables/credit-list.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CreditList } from './credit-list';
import { ClientContextService } from '../../core/client/client-context.service';

function setup() {
  localStorage.clear();
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const credit = (id: string, type: string, amount: number, memo: string | null, voided = false) =>
  ({ type, id, customerId: 'cu1', date: '2026-06-30', amount, memo, allocations: [{ targetId: 'inv1', amount }], voided });

function loadCustomerAndCredits(ctrl: HttpTestingController, f: any, rows: unknown[]) {
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  f.detectChanges();
  f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/credits') && r.params.get('customerId') === 'cu1').flush(rows);
  f.detectChanges();
}

describe('CreditList', () => {
  it('loads credits for the selected customer and renders type/amount/memo', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [credit('cn1', 'credit-note', 100, 'returned goods')]);
    const text = f.nativeElement.textContent;
    expect(text).toContain('Credit note');
    expect(text).toContain('100.00');
    expect(text).toContain('returned goods');
  });

  it('shows Void for credit-note/write-off and hides it for credit-application', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [
      credit('cn1', 'credit-note', 100, 'x'),
      credit('ca1', 'credit-application', 50, null),
    ]);
    const voidButtons = [...f.nativeElement.querySelectorAll('button')].filter(b => b.textContent.trim() === 'Void');
    expect(voidButtons.length).toBe(1);    // only the credit-note row
  });

  it('void posts to the right path and reloads', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [credit('cn1', 'credit-note', 100, 'x')]);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void') as HTMLButtonElement;
    voidBtn.click();
    ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes/cn1/void').flush({});
    // reload
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/credits')).flush([credit('cn1', 'credit-note', 100, 'x', true)]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Voided');
  });

  it('Record adjustment link targets the editor for the selected customer; disabled with none', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    const link = () => [...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Record adjustment') as HTMLAnchorElement;
    expect(link().className).toContain('opacity-50');
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/credits')).flush([]);
    f.detectChanges();
    expect(link().getAttribute('href')).toContain('/receivables/credits/new');
    expect(link().getAttribute('href')).toContain('customer=cu1');
  });

  it('shows the empty states', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No customers yet');
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test`
Expected: FAIL — cannot find module `./credit-list`.

- [ ] **Step 3: Implement `CreditList`**

Create `UI/Angular/src/app/features/receivables/credit-list.ts` (mirrors `PaymentList`; void triggers a manual reload via a refresh signal):

```typescript
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { CreditDocument, CreditType } from '../../core/receivables/receivables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { CustomerSelect } from '../../shared/customer-select';

@Component({
  selector: 'app-credit-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, CustomerSelect],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Credits</h1>
        <app-customer-select />
        <a hlmBtn size="sm" class="ms-auto"
           routerLink="/receivables/credits/new"
           [queryParams]="{ customer: customerId() }"
           [class.pointer-events-none]="!customerId()"
           [class.opacity-50]="!customerId()">
          Record adjustment
        </a>
      </div>

      @if (svc.customers().length === 0) {
        <p class="text-muted-foreground text-sm">No customers yet — <a routerLink="/receivables/customers" class="underline">add one first</a>.</p>
      } @else if (!customerId()) {
        <p class="text-muted-foreground text-sm">Select a customer to view credits.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (credits().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No credits recorded.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>Date</th><th hlmTh>Type</th><th hlmTh>Amount</th>
                  <th hlmTh>Memo</th><th hlmTh>Status</th><th hlmTh></th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (c of credits(); track c.id) {
                  <tr hlmTr [class.opacity-50]="c.voided">
                    <td hlmTd>{{ fmtDate(c.date) }}</td>
                    <td hlmTd>{{ label(c.type) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(c.amount) }}</td>
                    <td hlmTd>{{ c.memo ?? '—' }}</td>
                    <td hlmTd>{{ c.voided ? 'Voided' : 'Active' }}</td>
                    <td hlmTd>
                      @if (c.type !== 'credit-application' && !c.voided) {
                        <button hlmBtn size="sm" variant="outline" (click)="void(c)" [disabled]="busy()">Void</button>
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
export class CreditList {
  readonly svc = inject(ReceivablesService);
  readonly customerId = this.svc.selectedCustomerId;
  readonly listError = signal<string | null>(null);
  readonly busy = signal(false);
  private readonly refresh = signal(0);

  readonly credits = toSignal(
    toObservable(this.customerId).pipe(
      switchMap(cid => {
        // re-subscribe when refresh changes by reading it here
        this.refresh();
        if (!cid) return of([] as CreditDocument[]);
        this.listError.set(null);
        return this.svc.listCredits(cid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as CreditDocument[]); }),
        );
      }),
    ),
    { initialValue: [] as CreditDocument[] },
  );

  constructor() { this.svc.load(); }

  void(c: CreditDocument): void {
    if (c.type === 'credit-application') return;
    this.busy.set(true); this.listError.set(null);
    this.svc.voidCredit(c.type, c.id).subscribe({
      next: () => { this.busy.set(false); this.reload(); },
      error: e => { this.listError.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  private reload(): void {
    const cid = this.customerId(); if (!cid) return;
    this.svc.listCredits(cid).subscribe({
      next: list => { this.creditsOverride(list); },
      error: e => this.listError.set(extractProblem(e).detail),
    });
  }
  // `credits` is a computed-from-stream signal; the cleanest reload is to re-issue the request and
  // mirror it into a writable override the template reads. Simpler: drive everything off the stream.
  private creditsOverride(_list: CreditDocument[]): void { this.refresh.update(n => n + 1); }

  label(t: CreditType): string {
    return t === 'credit-note' ? 'Credit note' : t === 'write-off' ? 'Write-off' : 'Apply credit';
  }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
```

> **Reload simplification (do this — the override above is a smell):** drop `creditsOverride`/`reload`; have `void()` call `this.refresh.update(n => n + 1)` on success, and ensure the `credits` stream re-fires on `refresh`. Because `toObservable(this.customerId)` only emits on customerId change, combine both signals so a refresh re-triggers the load. Replace the stream source with:
> ```typescript
> import { combineLatest } from 'rxjs';
> // ...
> readonly credits = toSignal(
>   combineLatest([toObservable(this.customerId), toObservable(this.refresh)]).pipe(
>     switchMap(([cid]) => {
>       if (!cid) return of([] as CreditDocument[]);
>       this.listError.set(null);
>       return this.svc.listCredits(cid).pipe(
>         catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as CreditDocument[]); }),
>       );
>     }),
>   ),
>   { initialValue: [] as CreditDocument[] },
> );
> // void() success → this.refresh.update(n => n + 1); remove reload()/creditsOverride().
> ```
> Use this `combineLatest` form as the final implementation; it makes the void→reload test (`Step 1`, third test) pass cleanly.

- [ ] **Step 4: Add the `credits` route**

In `UI/Angular/src/app/app.routes.ts`, add the import (near the other receivables imports, ~line 20):
```typescript
import { CreditList } from './features/receivables/credit-list';
```
And add the child route inside the `receivables` children (after `customers`, ~line 58):
```typescript
    { path: 'credits', component: CreditList },
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test`
Expected: PASS — all five `CreditList` tests green.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/credit-list.ts UI/Angular/src/app/features/receivables/credit-list.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): Credits tab home (CreditList) with per-doc void

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: UI — `AdjustmentEditor` (the unified Adjust form)

**Files:**
- Create: `UI/Angular/src/app/features/receivables/adjustment-editor.ts`
- Test: `UI/Angular/src/app/features/receivables/adjustment-editor.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (import + `credits/new` route)

**Interfaces:**
- Consumes: `ReceivablesService` (`load`, `customerName`, `listInvoices`, `creditBalance`, `recordCreditNote`, `recordWriteOff`, `applyCredit`); `CreditType`; `money`/`displayDate`; `extractProblem`; `CurrencyInput`; `HlmInputImports`, `HlmLabelImports`, `HlmButton`; `ActivatedRoute`/`Router`.
- Produces: `AdjustmentEditor` at route `receivables/credits/new?customer=<id>` (redirects to `/receivables/credits` if `customer` absent).

**Form model:** `type: CreditType` (default `'credit-note'`); `date` (today); `memo` (string); `rows: AdjustRow[]` where `AdjustRow = { invoiceId: string; number: string | null; issueDate: string; openBalance: number; included: boolean; amount: number }`; `creditBalance: number`.

- [ ] **Step 1: Write the failing component tests**

Create `UI/Angular/src/app/features/receivables/adjustment-editor.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { AdjustmentEditor } from './adjustment-editor';
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

const invoicesPage = (rows: { id: string; number: string; open: number }[]) => ({
  items: rows.map(r => ({
    invoice: { id: r.id, customerId: 'cu1', number: r.number, issueDate: '2026-03-01', dueDate: null, status: 'Issued', taxRate: 0, memo: null, lines: [] },
    openBalance: r.open, settlementStatus: 'Open',
  })),
  total: rows.length, skip: 0, limit: 200,
});

function loadInvoices(ctrl: HttpTestingController, f: any, rows: { id: string; number: string; open: number }[], credit = 0) {
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices')).flush(invoicesPage(rows));
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/customers/cu1/credit-balance')).flush({ customerId: 'cu1', creditBalance: credit });
  f.detectChanges();
}

describe('AdjustmentEditor', () => {
  it('redirects to /receivables/credits when reached without a customer', () => {
    setup(null);
    const nav = spyOn(TestBed.inject(Router), 'navigate');
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    expect(nav).toHaveBeenCalledWith(['/receivables/credits']);
  });

  it('ticking a row fills its open balance and counts toward total; unticking clears it', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }]);
    const c = f.componentInstance;
    c.toggleRow(0, true); f.detectChanges();
    expect(c.rows()[0].amount).toBe(100);
    expect(c.total()).toBe(100);
    c.toggleRow(0, false); f.detectChanges();
    expect(c.rows()[0].amount).toBe(0);
    expect(c.total()).toBe(0);
  });

  it('caps an included row amount at its open balance (invalid above)', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }]);
    const c = f.componentInstance;
    c.toggleRow(0, true); c.setAmount(0, 150); f.detectChanges();
    expect(c.valid()).toBe(false);
  });

  it('hides memo for apply-credit and shows it otherwise; caps total at available credit', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }], 40);
    const c = f.componentInstance;
    c.setType('credit-application'); f.detectChanges();
    expect(f.nativeElement.textContent).not.toContain('Memo');
    expect(f.nativeElement.textContent).toContain('Available credit');
    c.toggleRow(0, true); f.detectChanges();           // amount 100 > 40 credit
    expect(c.valid()).toBe(false);
    c.setAmount(0, 40); f.detectChanges();
    expect(c.valid()).toBe(true);
  });

  it('submits the correct payload to the correct endpoint per type', () => {
    for (const [type, segment] of [['credit-note', 'credit-notes'], ['write-off', 'write-offs'], ['credit-application', 'credit-applications']] as const) {
      const ctrl = setup('cu1');
      const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
      loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }], 100);
      const c = f.componentInstance;
      c.setType(type); c.toggleRow(0, true); f.detectChanges();
      c.save();
      const req = ctrl.expectOne(`http://localhost:5000/clients/C1/${segment}`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body.allocations).toEqual([{ targetId: 'inv1', amount: 100 }]);
      if (type === 'credit-application') expect('memo' in req.request.body).toBe(false);
      req.flush({});
      TestBed.resetTestingModule();
    }
  });

  it('relays a 422 error inline', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }]);
    const c = f.componentInstance;
    c.toggleRow(0, true); f.detectChanges();
    c.save();
    ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes').flush(
      { type: 'about:blank', title: 'Unprocessable', detail: 'Allocation exceeds open balance.', status: 422 },
      { status: 422, statusText: 'Unprocessable Entity' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Allocation exceeds open balance.');
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test`
Expected: FAIL — cannot find module `./adjustment-editor`.

- [ ] **Step 3: Implement `AdjustmentEditor`**

Create `UI/Angular/src/app/features/receivables/adjustment-editor.ts` (mirrors `PaymentEditor`; checkbox-include rows, type radio, memo hidden for apply-credit, available-credit cap):

```typescript
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { CreditType } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

interface AdjustRow { invoiceId: string; number: string | null; issueDate: string; openBalance: number; included: boolean; amount: number; }

@Component({
  selector: 'app-adjustment-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables/credits" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Credits</a>
      <h1 class="text-2xl font-bold">Record adjustment</h1>
      <p class="text-sm text-muted-foreground">{{ svc.customerName(customerId!) }}</p>

      <div class="flex gap-4 flex-wrap">
        @for (opt of types; track opt.value) {
          <label class="flex items-center gap-2 text-sm">
            <input type="radio" name="type" [value]="opt.value" [checked]="type() === opt.value"
                   (change)="setType(opt.value)" [attr.aria-label]="opt.label" />
            {{ opt.label }}
          </label>
        }
      </div>

      <div class="grid grid-cols-2 gap-4 max-w-md">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
        @if (type() !== 'credit-application') {
          <div class="flex flex-col gap-1">
            <label hlmLabel>Memo</label>
            <input hlmInput type="text" placeholder="reason…" [value]="memo()" (input)="memo.set($any($event.target).value)" />
          </div>
        }
      </div>

      @if (type() === 'credit-application') {
        <p class="text-sm" [class.text-destructive]="total() > creditBalance()">
          Available credit {{ money(creditBalance()) }}
        </p>
      }

      @if (rows().length === 0) {
        <p class="text-sm text-muted-foreground">No open invoices to adjust.</p>
      } @else {
        <table class="w-full text-sm">
          <thead>
            <tr class="text-left text-muted-foreground">
              <th class="py-1"></th><th>Invoice</th><th>Issued</th>
              <th class="text-right pr-5">Open</th><th class="text-right pr-5">Amount</th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.invoiceId; let i = $index) {
              <tr>
                <td class="py-1">
                  <input type="checkbox" [checked]="r.included" (change)="toggleRow(i, $any($event.target).checked)"
                         [attr.aria-label]="'Include ' + (r.number ?? r.invoiceId)" />
                </td>
                <td>{{ r.number ?? '—' }}</td>
                <td>{{ formatDate(r.issueDate) }}</td>
                <td class="text-right tabular-nums pr-5">{{ money(r.openBalance) }}</td>
                <td class="pr-2">
                  <div class="flex justify-end">
                    <app-currency-input class="w-32" [ariaLabel]="'Amount for ' + (r.number ?? r.invoiceId)"
                         [value]="r.amount" (valueChange)="setAmount(i, $event)" [disabled]="!r.included" />
                  </div>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }

      <div class="text-right text-sm tabular-nums w-72 ms-auto flex justify-between">
        <span>Total</span><span>{{ money(total()) }}</span>
      </div>

      <p class="text-xs text-muted-foreground">
        Recording an adjustment posts an entry that needs approval before it affects the statements.
        The invoice's open balance updates immediately.
      </p>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Record adjustment</button>
        <a hlmBtn variant="outline" routerLink="/receivables/credits">Cancel</a>
      </div>
    </div>
  `,
})
export class AdjustmentEditor {
  readonly svc = inject(ReceivablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly customerId = this.route.snapshot.queryParamMap.get('customer');
  readonly types: { value: CreditType; label: string }[] = [
    { value: 'credit-note', label: 'Credit note' },
    { value: 'write-off', label: 'Write-off' },
    { value: 'credit-application', label: 'Apply credit' },
  ];

  readonly type = signal<CreditType>('credit-note');
  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly memo = signal('');
  readonly rows = signal<AdjustRow[]>([]);
  readonly creditBalance = signal(0);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly total = computed(() =>
    Math.round(this.rows().filter(r => r.included).reduce((s, r) => s + r.amount, 0) * 100) / 100);

  readonly valid = computed(() => {
    const included = this.rows().filter(r => r.included);
    if (included.length === 0) return false;
    if (!included.every(r => r.amount > 0 && r.amount <= r.openBalance)) return false;
    if (this.type() === 'credit-application' && this.total() > this.creditBalance()) return false;
    return true;
  });

  constructor() {
    if (!this.customerId) { void this.router.navigate(['/receivables/credits']); return; }
    this.svc.load();
    this.svc.listInvoices({ customerId: this.customerId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })
      .subscribe(page => this.rows.set(page.items.map(v => ({
        invoiceId: v.invoice.id, number: v.invoice.number, issueDate: v.invoice.issueDate,
        openBalance: v.openBalance, included: false, amount: 0,
      }))));
    this.svc.creditBalance(this.customerId).subscribe(b => this.creditBalance.set(b));
  }

  setType(t: CreditType): void { this.type.set(t); }
  toggleRow(i: number, included: boolean): void {
    this.rows.update(rs => rs.map((r, idx) => idx === i
      ? { ...r, included, amount: included ? r.openBalance : 0 } : r));
  }
  setAmount(i: number, v: number): void {
    this.rows.update(rs => rs.map((r, idx) => idx === i ? { ...r, amount: v } : r));
  }

  save(): void {
    if (!this.valid() || !this.customerId) return;
    this.busy.set(true); this.message.set(null);
    const allocations = this.rows().filter(r => r.included && r.amount > 0)
      .map(r => ({ targetId: r.invoiceId, amount: r.amount }));
    const customerId = this.customerId; const date = this.date(); const memo = this.memo().trim() || null;

    const done = {
      next: () => { this.busy.set(false); void this.router.navigate(['/receivables/credits']); },
      error: (e: unknown) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    };
    switch (this.type()) {
      case 'credit-note': this.svc.recordCreditNote({ customerId, date, allocations, memo }).subscribe(done); break;
      case 'write-off': this.svc.recordWriteOff({ customerId, date, allocations, memo }).subscribe(done); break;
      case 'credit-application': this.svc.applyCredit({ customerId, date, allocations }).subscribe(done); break;
    }
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

> **Note on `<app-currency-input [disabled]>`:** verify `CurrencyInput` exposes a `disabled` input. If it does not, drop the `[disabled]="!r.included"` binding (the cap/validation still hold because unticked rows have `amount:0` and `included:false`, so they never contribute). Check `UI/Angular/src/app/shared/currency-input.ts` before relying on it.

- [ ] **Step 4: Add the `credits/new` route**

In `UI/Angular/src/app/app.routes.ts`, add the import:
```typescript
import { AdjustmentEditor } from './features/receivables/adjustment-editor';
```
And the child route (after the `credits` route from Task 4):
```typescript
    { path: 'credits/new', component: AdjustmentEditor },
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test`
Expected: PASS — all `AdjustmentEditor` tests green.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/adjustment-editor.ts UI/Angular/src/app/features/receivables/adjustment-editor.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): unified AdjustmentEditor (credit note / write-off / apply credit)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Run the full backend solution tests**

Run: `dotnet test`
Expected: PASS — full solution green (no regression from the Memo/mapper change or the new endpoint).

- [ ] **Step 2: Run the full UI suite**

Run: `cd UI/Angular && npm test`
Expected: PASS — all specs green (service + shell + credit-list + adjustment-editor + existing).

- [ ] **Step 3: Build the UI (type-check the production build)**

Run: `cd UI/Angular && npm run build`
Expected: build succeeds with no type errors.

- [ ] **Step 4: Manual smoke (optional, with dev stack running)**

Per `.localdev/start.ps1` (Receivables env vars set): Receivables → Credits → pick a customer → Record adjustment → tick an open invoice → pick a type → submit → returns to the list; the entry appears in Approvals; Void shows only on credit-note/write-off rows.

---

## Self-Review

**1. Spec coverage:**
- Unified `GET /credits?customerId=` (Type discriminator, date-desc, 400 on missing) → Task 1. ✅
- `CreditDocument` DTO (Amount=Σ, Memo null for credit-app) → Task 1 Step 5. ✅
- Memo actually surfaced (the gap the spec assumed worked) → Task 1 Steps 3–4 (per user decision). ✅
- UI model + 5 service methods → Task 2. ✅
- Credits tab in shell → Task 3. ✅
- `CreditList` (columns, void on note/write-off only, empty states, record-adjustment link) → Task 4. ✅
- `AdjustmentEditor` (radio type, checkbox-include rows, memo hidden for apply-credit, available-credit cap, redirect without customer, per-type submit, error relay) → Task 5. ✅
- Routes `credits` + `credits/new` → Tasks 4 & 5. ✅
- Deferred (refund / aging / credit-application void / contextual deep-link) → out of scope, not planned. ✅

**2. Placeholder scan:** All steps contain concrete code/commands. The two remaining `>` notes (`combineLatest` reload in Task 4; `currency-input [disabled]` in Task 5) are decision guards with the exact code to use, not TODOs. The `CreditDocument` placement question is resolved (core project, verified by reading the csproj reference direction).

**3. Type consistency:** `CreditType` / `CreditDocument` / `CreditNoteRequest` / `WriteOffRequest` / `CreditApplyRequest` names match across Tasks 2/4/5. Service method names (`listCredits`/`recordCreditNote`/`recordWriteOff`/`applyCredit`/`voidCredit`) match between Task 2 (definition) and Tasks 4/5 (consumption). Backend `CreditDocument` field order/names match the UI interface (`type/id/customerId/date/amount/memo/allocations/voided`). `AdjustRow` shape matches between the editor template and the spec.

**Note for the implementer:** Everything is mechanical. The one structural fact already verified for you: `CreditDocument` goes in the **core** `Accounting101.Receivables` project (Api references core, not vice-versa), so `PaymentService` can return it and the endpoint can serialize it.
