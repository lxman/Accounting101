# AR Payment Detail Implementation Plan (Slice 2c-1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an AR payment-detail screen reachable by whole-row drill-in from the Payments list, backed by a new `GET /clients/{id}/payments/{paymentId}` returning the payment, its allocations (resolved to invoice numbers), the unapplied remainder, and its journal entry id — and fix the pre-existing payments-list bug (the list + invoice-detail read per-payment allocations the backend never folds).

**Architecture:** Backend (Receivables): rename the shipped `CreditAllocationLine` → shared `InvoiceAllocationLine` (own file); add `PaymentView` + `PaymentService.GetPaymentViewAsync` (allocations folded from the payment's GL posting like credit detail, plus `Unapplied = Amount − Σallocations`); add `GET /payments/{id}`; and make the payments-list endpoint fold each payment's allocations so `PaymentList` and `InvoiceDetail` (both read `p.allocations` from `GET /payments?customerId=`) get real data. Frontend: `PaymentView` interface + `getPayment` + a `payment-detail` screen + `payments/:id` route, and whole-row drill-in on `PaymentList`.

**Tech Stack:** .NET 10 minimal APIs + MongoDB (EphemeralMongo in tests); Angular 22 (standalone, OnPush, zoneless), Tailwind v4, Spartan Helm; xUnit (backend), Vitest runner + TestBed (frontend).

## Global Constraints

- **Backend:** namespaces follow folder structure (`Accounting101.Receivables`). New detail endpoint returns `PaymentView` and follows the exact shape of `GetRefund`: `return view is null ? Results.NotFound() : Results.Ok(view)`. The endpoint group already carries `.RequireAuthorization()`. **Rider auto-converts explicit types to `var` — stage explicit file lists and check for stray churn before each commit.**
- **The rename must leave no lingering `CreditAllocationLine` references on the side being changed**, so the solution compiles at each commit. Wire field names (`invoiceId`, `invoiceNumber`, `amount`) are unchanged — it is a pure type-name refactor.
- **Frontend:** standalone components, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. New detail route is ungated. The Payments-list drill-in is same-area (a Payments viewer already holds `ar.read`), so rows are unconditionally clickable. FE test runner is **Vitest** — `vi.spyOn` (global), not Jasmine; nav spies chain `.mockResolvedValue(true)`.
- **Wire shapes** identical backend↔frontend (host `JsonNamingPolicy.CamelCase`): `PaymentView{ payment: Payment, allocations: InvoiceAllocationLine[], unapplied: number, journalEntryId: string | null }`; `InvoiceAllocationLine{ invoiceId, invoiceNumber: string|null, amount }`. The payments-list item serializes to the existing FE `Payment` shape `{ id, customerId, date, amount, method: string|null, allocations: {targetId, amount}[], voided }`.
- Posting pick is `{Status:"Active", Posting:"Posted", ReversalOf:null}` (the 2b-2 lesson) everywhere allocations/journal ids are read.
- The "View journal entry" link is `gl.read`-gated via `*appCan`.
- Only touch files named per task. Do NOT touch payables (2c-2), statement builders / customer-account / vendor-account (2c-3), refund-* (done), or other modules.
- Backend test run (focused): `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetPaymentsEndpointTests"`; the rename also requires `GetCreditsEndpointTests` green. FE unit test: `npx ng test --include='<glob>' --watch=false` from `UI/Angular`. FE compile gate: `npx ng build --configuration development`.
- `environment.ts` stays modified/uncommitted (local dev config, never commit).
- Branch `feat/payment-detail`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: Backend — shared InvoiceAllocationLine rename + PaymentView + GET /payments/{id}

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables/InvoiceAllocationLine.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables/CreditView.cs` (drop the moved record; retype `CreditView.Allocations`)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` (rename usage in `GetCreditViewAsync`; add `GetPaymentViewAsync`)
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/GetCreditsEndpointTests.cs` (rename usages)
- Create: `Modules/Receivables/Accounting101.Receivables/PaymentView.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (add `GetPayment` handler + route)
- Test (extend): `Modules/Receivables/Accounting101.Receivables.Tests/GetPaymentsEndpointTests.cs`

**Interfaces:**
- Consumes: `IPaymentStore.GetPaymentAsync`, `IInvoiceStore.GetAsync`, `ILedgerClient.GetEntriesBySourceRefAsync`, `PaymentPosting.InvoiceDimension` (public), `EntryResponse`/`EntryLineResponse`, `Payment`.
- Produces: `InvoiceAllocationLine(Guid InvoiceId, string? InvoiceNumber, decimal Amount)` (shared, renamed); `PaymentView(Payment Payment, IReadOnlyList<InvoiceAllocationLine> Allocations, decimal Unapplied, Guid? JournalEntryId)`; `PaymentService.GetPaymentViewAsync(Guid, Guid, CancellationToken) → PaymentView?`; route `GET /clients/{clientId}/payments/{paymentId:guid}`.

- [ ] **Step 1: Write the failing tests**

Add three test methods to `GetPaymentsEndpointTests.cs`, inside the existing class (it already has `SetUpChartAsync`, `IssueInvoiceAsync`, `ApproveBySourceRefAsync`, and `using System.Net;`). Reference the new `PaymentView`/`InvoiceAllocationLine` types (they will not exist yet — that is the RED):

```csharp
    [Fact]
    public async Task GET_payment_by_id_returns_allocations_unapplied_and_journal_entry_id()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv1 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);
        Guid inv2 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);

        // Pay 150 allocating 60→inv1 and 40→inv2 (total 100 applied), leaving 50 unapplied.
        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 5), 150m, "check",
                    [new Allocation(inv1, 60m), new Allocation(inv2, 40m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        InvoiceView iv1 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{inv1}"))!;
        InvoiceView iv2 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{inv2}"))!;

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={payment.Id}"))!;
        Guid expectedEntryId = entries.Single(e => e is { Status: "Active", ReversalOf: null }).Id;

        PaymentView view = (await clerk.GetFromJsonAsync<PaymentView>(
            $"/clients/{clientId}/payments/{payment.Id}"))!;

        Assert.Equal(payment.Id, view.Payment.Id);
        Assert.Equal(150m, view.Payment.Amount);
        Assert.Equal("check", view.Payment.Method);
        Assert.False(view.Payment.Voided);
        Assert.Equal(expectedEntryId, view.JournalEntryId);
        Assert.Equal(50m, view.Unapplied);

        Assert.Equal(2, view.Allocations.Count);
        InvoiceAllocationLine a1 = view.Allocations.Single(a => a.InvoiceId == inv1);
        Assert.Equal(60m, a1.Amount);
        Assert.Equal(iv1.Invoice.Number, a1.InvoiceNumber);
        InvoiceAllocationLine a2 = view.Allocations.Single(a => a.InvoiceId == inv2);
        Assert.Equal(40m, a2.Amount);
        Assert.Equal(iv2.Invoice.Number, a2.InvoiceNumber);
    }

    [Fact]
    public async Task GET_fully_allocated_payment_has_zero_unapplied()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne", null)))
            .Content.ReadFromJsonAsync<Customer>())!;
        Guid inv = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);

        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 6), 100m, "check",
                    [new Allocation(inv, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        PaymentView view = (await clerk.GetFromJsonAsync<PaymentView>(
            $"/clients/{clientId}/payments/{payment.Id}"))!;

        Assert.Equal(0m, view.Unapplied);
        Assert.Equal(100m, Assert.Single(view.Allocations).Amount);
    }

    [Fact]
    public async Task GET_payment_by_unknown_id_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/payments/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetPaymentsEndpointTests"`
Expected: BUILD FAILURE — `PaymentView` / `InvoiceAllocationLine` do not exist.

- [ ] **Step 3: Create `InvoiceAllocationLine.cs` (the renamed shared record)**

Create `Modules/Receivables/Accounting101.Receivables/InvoiceAllocationLine.cs`:
```csharp
namespace Accounting101.Receivables;

/// <summary>One invoice an allocation-based document (credit or payment) was applied to: the invoice's id,
/// its number (null if unnumbered), and the amount applied to it. Recovered from the document's GL entry
/// lines (each allocation line carries an "Invoice" dimension and the allocated amount).</summary>
public sealed record InvoiceAllocationLine(Guid InvoiceId, string? InvoiceNumber, decimal Amount);
```

- [ ] **Step 4: Remove the old record from `CreditView.cs` and retype `CreditView`**

In `Modules/Receivables/Accounting101.Receivables/CreditView.cs`, delete the `CreditAllocationLine` record declaration (and its doc comment) and change `CreditView`'s `Allocations` type. The file becomes:
```csharp
namespace Accounting101.Receivables;

/// <summary>A credit (note, write-off, or application) plus the invoices it was applied to and the id of
/// its posted journal entry — what the credit detail endpoint returns. Credit reuses the unified
/// CreditDocument shape so the detail header matches the list row; Allocations are folded from the GL
/// posting; JournalEntryId lets the UI drill to the GL entry (null if none is found).</summary>
public sealed record CreditView(
    CreditDocument Credit,
    IReadOnlyList<InvoiceAllocationLine> Allocations,
    Guid? JournalEntryId);
```

- [ ] **Step 5: Rename the usage in `GetCreditViewAsync` and in `GetCreditsEndpointTests`**

In `PaymentService.cs`, inside `GetCreditViewAsync`, change the allocations list element type from `CreditAllocationLine` to `InvoiceAllocationLine` (the `List<CreditAllocationLine> allocations = [];` declaration and the `allocations.Add(new CreditAllocationLine(...))` call).

In `GetCreditsEndpointTests.cs`, change the two `CreditAllocationLine` local-variable types (`CreditAllocationLine a1 = ...`, `CreditAllocationLine a2 = ...`) to `InvoiceAllocationLine`.

(After this step there must be zero `CreditAllocationLine` references in backend code — `grep -rn CreditAllocationLine Modules` should return nothing.)

- [ ] **Step 6: Create `PaymentView.cs`**

Create `Modules/Receivables/Accounting101.Receivables/PaymentView.cs`:
```csharp
namespace Accounting101.Receivables;

/// <summary>A customer payment plus the invoices it was applied to, the unapplied remainder held as
/// customer credit, and the id of its posted journal entry — what the payment detail endpoint returns.
/// Allocations are folded from the GL posting (Posted-only); Unapplied = Amount − Σallocations (the
/// overpayment held as credit); JournalEntryId lets the UI drill to the GL entry (null if none found).</summary>
public sealed record PaymentView(
    Payment Payment,
    IReadOnlyList<InvoiceAllocationLine> Allocations,
    decimal Unapplied,
    Guid? JournalEntryId);
```

- [ ] **Step 7: Add `GetPaymentViewAsync` to `PaymentService`**

In `PaymentService.cs`, add this method just after `GetPaymentsByCustomerAsync` (~line 92). It mirrors `GetCreditViewAsync`'s allocation fold and uses the same Posted posting pick:
```csharp
    /// <summary>A single payment plus the invoices it was applied to, its unapplied remainder (held as
    /// customer credit), and its posted journal entry id — for the detail screen. Allocations and the
    /// journal id come from the Posted posting; Unapplied = Amount − Σallocations. Returns null if the
    /// payment does not exist.</summary>
    public async Task<PaymentView?> GetPaymentViewAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        Payment? payment = await payments.GetPaymentAsync(clientId, paymentId, ct);
        if (payment is null) return null;

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, paymentId, ct);
        EntryResponse? postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null });

        List<InvoiceAllocationLine> allocations = [];
        if (postingEntry is not null)
        {
            foreach (IGrouping<Guid, EntryLineResponse> group in postingEntry.Lines
                         .Where(l => l.Dimensions.ContainsKey(PaymentPosting.InvoiceDimension))
                         .GroupBy(l => l.Dimensions[PaymentPosting.InvoiceDimension]))
            {
                Invoice? invoice = await invoices.GetAsync(clientId, group.Key, ct);
                allocations.Add(new InvoiceAllocationLine(group.Key, invoice?.Number, group.Sum(l => l.Amount)));
            }
        }

        decimal unapplied = payment.Amount - allocations.Sum(a => a.Amount);
        return new PaymentView(payment, allocations, unapplied, postingEntry?.Id);
    }
```

- [ ] **Step 8: Add the `GetPayment` endpoint + route**

In `ReceivablesEndpoints.cs`, register the route immediately after the `ListPayments` map (`clients.MapGet("/payments", ListPayments);`, line 26):
```csharp
        clients.MapGet("/payments/{paymentId:guid}", GetPayment);
```
Add the handler next to `ListPayments` (~after line 179), mirroring `GetRefund`:
```csharp
    private static async Task<IResult> GetPayment(
        Guid clientId, Guid paymentId, PaymentService service, CancellationToken cancellationToken)
    {
        PaymentView? view = await service.GetPaymentViewAsync(clientId, paymentId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetPaymentsEndpointTests"`
Expected: PASS (the three new tests + the two pre-existing).
Then run the rename regression: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetCreditsEndpointTests"`
Expected: PASS (name-only change).

- [ ] **Step 10: Commit**

Stage the explicit file list (guard against Rider `var` churn):
```bash
git add Modules/Receivables/Accounting101.Receivables/InvoiceAllocationLine.cs Modules/Receivables/Accounting101.Receivables/CreditView.cs Modules/Receivables/Accounting101.Receivables/PaymentService.cs Modules/Receivables/Accounting101.Receivables/PaymentView.cs Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs Modules/Receivables/Accounting101.Receivables.Tests/GetCreditsEndpointTests.cs Modules/Receivables/Accounting101.Receivables.Tests/GetPaymentsEndpointTests.cs
git commit -m "feat(receivables): GET /payments/{id} returning payment + allocations + unapplied; share InvoiceAllocationLine"
```

---

### Task 2: Backend — payments list allocations fold (fixes PaymentList + InvoiceDetail)

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables/PaymentWithAllocations.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` (add `GetPaymentsWithAllocationsByCustomerAsync`)
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (`ListPayments` returns the folded shape)
- Test (modify): `Modules/Receivables/Accounting101.Receivables.Tests/GetPaymentsEndpointTests.cs`

**Interfaces:**
- Consumes: `IPaymentStore.GetPaymentsByCustomerAsync`, `ILedgerClient.GetEntriesBySourceRefAsync`, `PaymentPosting.InvoiceDimension`, `Allocation`.
- Produces: `PaymentWithAllocations(Guid Id, Guid CustomerId, DateOnly Date, decimal Amount, string? Method, bool Voided, IReadOnlyList<Allocation> Allocations)`; `PaymentService.GetPaymentsWithAllocationsByCustomerAsync(Guid, Guid, CancellationToken) → IReadOnlyList<PaymentWithAllocations>`; `GET /clients/{id}/payments?customerId=` now returns `PaymentWithAllocations[]`.

**Background:** `GET /payments?customerId=` currently returns raw `Payment[]` with no allocations, but both `PaymentList` (Allocated/Unapplied columns) and `InvoiceDetail` ("Applied payments" section) read `p.allocations` — so those columns/section are wrong (and crash) on real data. Fold allocations into the list response. The FE already expects `allocations: {targetId, amount}[]` on each payment, so no FE change is needed.

- [ ] **Step 1: Write the failing test (extend the existing list test)**

In `GetPaymentsEndpointTests.cs`, replace the body of the existing `GET_payments_returns_customer_payments_with_no_allocation_array_and_the_invoice_fold_reflects_it` test's read-back + assertions to consume the folded shape and assert the allocations. Change the deserialization and assertions (keep the setup — issue invoice, pay 60 allocating 60, approve):

```csharp
        PaymentWithAllocations[] list = (await clerk.GetFromJsonAsync<PaymentWithAllocations[]>(
            $"/clients/{clientId}/payments?customerId={customer.Id}"))!;

        Assert.Single(list);
        Assert.Equal(payment.Id, list[0].Id);
        Assert.Equal(60m, list[0].Amount);
        Allocation alloc = Assert.Single(list[0].Allocations);
        Assert.Equal(invoiceId, alloc.TargetId);
        Assert.Equal(60m, alloc.Amount);

        InvoiceView view = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoiceId}"))!;
        Assert.Equal(40m, view.OpenBalance);
```
(Rename the test method if its old name — "with_no_allocation_array" — no longer fits; e.g. `GET_payments_returns_customer_payments_with_folded_allocations`. Update the class doc comment's claim accordingly.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetPaymentsEndpointTests"`
Expected: BUILD FAILURE — `PaymentWithAllocations` does not exist.

- [ ] **Step 3: Create `PaymentWithAllocations.cs`**

Create `Modules/Receivables/Accounting101.Receivables/PaymentWithAllocations.cs`:
```csharp
namespace Accounting101.Receivables;

/// <summary>A payment as the Payments list reads it: the stored document fields plus the per-invoice
/// allocations folded from its GL posting (Posted-only). The module stores no allocation array; this is
/// the read shape the list + invoice-detail "applied payments" section consume (each Allocation is
/// {invoice id, amount}).</summary>
public sealed record PaymentWithAllocations(
    Guid Id, Guid CustomerId, DateOnly Date, decimal Amount, string? Method, bool Voided,
    IReadOnlyList<Allocation> Allocations);
```

- [ ] **Step 4: Add `GetPaymentsWithAllocationsByCustomerAsync` to `PaymentService`**

In `PaymentService.cs`, add just after `GetPaymentViewAsync` (from Task 1):
```csharp
    /// <summary>The customer's payments each with its per-invoice allocations folded from the GL (Posted-only)
    /// — what the Payments list and the invoice-detail "applied payments" section consume. Voided payments
    /// are included (greyed in the UI); a not-yet-Posted payment folds to no allocations.</summary>
    public async Task<IReadOnlyList<PaymentWithAllocations>> GetPaymentsWithAllocationsByCustomerAsync(
        Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        List<PaymentWithAllocations> result = [];
        foreach (Payment p in ps)
        {
            IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, p.Id, ct);
            EntryResponse? postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null });
            List<Allocation> allocs = [];
            if (postingEntry is not null)
                foreach (IGrouping<Guid, EntryLineResponse> group in postingEntry.Lines
                             .Where(l => l.Dimensions.ContainsKey(PaymentPosting.InvoiceDimension))
                             .GroupBy(l => l.Dimensions[PaymentPosting.InvoiceDimension]))
                    allocs.Add(new Allocation(group.Key, group.Sum(l => l.Amount)));
            result.Add(new PaymentWithAllocations(p.Id, p.CustomerId, p.Date, p.Amount, p.Method, p.Voided, allocs));
        }
        return result;
    }
```

- [ ] **Step 5: Point `ListPayments` at the folded method**

In `ReceivablesEndpoints.cs`, change the `ListPayments` handler body to call the new method:
```csharp
    private static async Task<IResult> ListPayments(
        Guid clientId, Guid? customerId, PaymentService service, CancellationToken cancellationToken)
    {
        if (customerId is null || customerId == Guid.Empty)
            return Results.Problem("customerId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<PaymentWithAllocations> payments = await service.GetPaymentsWithAllocationsByCustomerAsync(clientId, customerId.Value, cancellationToken);
        return Results.Ok(payments);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetPaymentsEndpointTests"`
Expected: PASS (the updated list test asserting folded allocations + the 400 test + Task 1's three detail tests).

- [ ] **Step 7: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables/PaymentWithAllocations.cs Modules/Receivables/Accounting101.Receivables/PaymentService.cs Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs Modules/Receivables/Accounting101.Receivables.Tests/GetPaymentsEndpointTests.cs
git commit -m "fix(receivables): fold per-payment allocations into the payments list (fixes Payments list + invoice-detail applied-payments)"
```

---

### Task 3: Frontend — InvoiceAllocationLine rename + payment-detail screen + route

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.ts` (rename interface; add `PaymentView`)
- Modify: `UI/Angular/src/app/features/receivables/credit-detail.ts` (rename import + `sum` param type)
- Modify: `UI/Angular/src/app/core/receivables/receivables.service.ts` (add `getPayment`)
- Create: `UI/Angular/src/app/features/receivables/payment-detail.ts`
- Create: `UI/Angular/src/app/features/receivables/payment-detail.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add `payments/:id` route)

**Interfaces:**
- Consumes: Task 1's `PaymentView` wire shape; `ReceivablesService`, `ClientContextService`, `Payment`, `CanDirective`.
- Produces: `InvoiceAllocationLine` (renamed) + `PaymentView` TS interfaces; `ReceivablesService.getPayment(id): Observable<PaymentView>`; `PaymentDetail` component; route `payments/:id`.

- [ ] **Step 1: Write the failing component spec**

Create `UI/Angular/src/app/features/receivables/payment-detail.spec.ts`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PaymentDetail } from './payment-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot(id: string, caps: string[] = ['gl.read']) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(PaymentDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('PaymentDetail', () => {
  it('renders header, method, allocations with total, unapplied line, and journal link', () => {
    const { fixture, ctrl } = boot('p1');
    ctrl.expectOne('http://localhost:5000/clients/C1/payments/p1').flush({
      payment: { id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 150, method: 'check', allocations: [], voided: false },
      allocations: [
        { invoiceId: 'inv1', invoiceNumber: '1042', amount: 60 },
        { invoiceId: 'inv2', invoiceNumber: '1051', amount: 40 },
      ],
      unapplied: 50, journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('check');
    expect(text).toContain('1042');
    expect(text).toContain('60.00');
    expect(text).toContain('1051');
    expect(text).toContain('40.00');
    expect(text).toContain('100.00');   // allocations total
    expect(text).toContain('50.00');    // unapplied
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('omits the journal link when journalEntryId is null', () => {
    const { fixture, ctrl } = boot('p2');
    ctrl.expectOne('http://localhost:5000/clients/C1/payments/p2').flush({
      payment: { id: 'p2', customerId: 'cu1', date: '2026-06-30', amount: 25, method: null, allocations: [], voided: false },
      allocations: [], unapplied: 25, journalEntryId: null,
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });

  it('hides the journal link when the user lacks gl.read', () => {
    const { fixture, ctrl } = boot('p3', []);
    ctrl.expectOne('http://localhost:5000/clients/C1/payments/p3').flush({
      payment: { id: 'p3', customerId: 'cu1', date: '2026-06-30', amount: 30, method: 'cash', allocations: [], voided: false },
      allocations: [{ invoiceId: 'inv1', invoiceNumber: '1042', amount: 30 }], unapplied: 0, journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run: `npx ng test --include='**/payment-detail.spec.ts' --watch=false`
Expected: FAIL — cannot resolve `./payment-detail`.

- [ ] **Step 3: Rename the FE interface + add `PaymentView`**

In `UI/Angular/src/app/core/receivables/receivables.ts`:
- Rename the interface on line 52: `export interface CreditAllocationLine { ... }` → `export interface InvoiceAllocationLine { invoiceId: string; invoiceNumber: string | null; amount: number; }`
- Update `CreditView` (line 53) to reference it: `export interface CreditView { credit: CreditDocument; allocations: InvoiceAllocationLine[]; journalEntryId: string | null; }`
- Add after the `Payment` interface (line 41) or alongside `CreditView`:
```ts
export interface PaymentView { payment: Payment; allocations: InvoiceAllocationLine[]; unapplied: number; journalEntryId: string | null; }
```

- [ ] **Step 4: Rename in `credit-detail.ts`**

In `UI/Angular/src/app/features/receivables/credit-detail.ts`:
- Line 5 import: change `CreditAllocationLine` → `InvoiceAllocationLine`.
- Line 78: `sum(lines: CreditAllocationLine[])` → `sum(lines: InvoiceAllocationLine[])`.

(After this step, `grep -rn CreditAllocationLine UI/Angular/src` must return nothing.)

- [ ] **Step 5: Add the `getPayment` service method**

In `receivables.service.ts`:
- Add `PaymentView` to the import from `'./receivables'` (line 7 — append `, PaymentView`).
- Add the method next to `getRefund` (line 72):
```ts
  getPayment(id: string): Observable<PaymentView> { return this.http.get<PaymentView>(this.base(`/payments/${id}`)); }
```

- [ ] **Step 6: Create the `payment-detail` component**

Create `UI/Angular/src/app/features/receivables/payment-detail.ts`:
```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { InvoiceAllocationLine, PaymentView } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-payment-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables/payments" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Payments</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Payment</h1>
          <span class="text-sm px-2 py-0.5 rounded border border-border">{{ v.payment.voided ? 'Voided' : 'Active' }}</span>
        </div>
        <div class="text-sm flex flex-col gap-1">
          <div><span class="text-muted-foreground">Date</span> · {{ formatDate(v.payment.date) }}</div>
          <div><span class="text-muted-foreground">Amount</span> · <span class="tabular-nums">{{ money(v.payment.amount) }}</span></div>
          <div><span class="text-muted-foreground">Method</span> · {{ v.payment.method ?? '—' }}</div>
        </div>

        <div class="flex flex-col gap-1">
          <h2 class="text-sm font-semibold">Applied to</h2>
          @if (v.allocations.length === 0) {
            <p class="text-muted-foreground text-sm">No allocations.</p>
          } @else {
            <table class="text-sm w-full max-w-md">
              <tbody>
                @for (a of v.allocations; track a.invoiceId) {
                  <tr>
                    <td class="py-0.5">Invoice {{ a.invoiceNumber ?? '—' }}</td>
                    <td class="py-0.5 text-right tabular-nums">{{ money(a.amount) }}</td>
                  </tr>
                }
                <tr class="border-t border-border font-semibold">
                  <td class="py-0.5">Total applied</td>
                  <td class="py-0.5 text-right tabular-nums">{{ money(sum(v.allocations)) }}</td>
                </tr>
              </tbody>
            </table>
          }
          <div class="text-sm mt-1"><span class="text-muted-foreground">Unapplied (held as customer credit)</span> · <span class="tabular-nums">{{ money(v.unapplied) }}</span></div>
        </div>

        @if (v.journalEntryId) {
          <a *appCan="'gl.read'" [routerLink]="['/journal', v.journalEntryId]" class="text-sm text-primary hover:underline w-fit">View journal entry →</a>
        }
      } @else if (loadError()) {
        <p class="text-destructive text-sm">{{ loadError() }}</p>
      } @else {
        <p class="text-muted-foreground text-sm">Loading…</p>
      }
    </div>
  `,
})
export class PaymentDetail {
  private readonly svc = inject(ReceivablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<PaymentView | null>(null);
  readonly loadError = signal<string | null>(null);

  constructor() {
    this.svc.getPayment(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.view.set(v),
      error: (e) => this.loadError.set(extractProblem(e).detail),
    });
  }

  sum(lines: InvoiceAllocationLine[]): number { return lines.reduce((s, a) => s + a.amount, 0); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 7: Add the route**

In `UI/Angular/src/app/app.routes.ts`:
- Add the import next to the other receivables feature imports (after line 25 `import { RefundDetail } ...`):
```ts
import { PaymentDetail } from './features/receivables/payment-detail';
```
- Add the route after the `payments/new` entry (line 113), in the RECEIVABLES block, mirroring `refunds/:id`:
```ts
    { path: 'payments/:id', component: PaymentDetail },
```

- [ ] **Step 8: Run the spec + compile gate**

Run: `npx ng test --include='**/payment-detail.spec.ts' --watch=false` → all three specs PASS.
Run: `npx ng test --include='**/credit-detail.spec.ts' --watch=false` → still PASS (rename is name-only).
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 9: Commit**

```bash
git add UI/Angular/src/app/core/receivables/receivables.ts UI/Angular/src/app/features/receivables/credit-detail.ts UI/Angular/src/app/core/receivables/receivables.service.ts UI/Angular/src/app/features/receivables/payment-detail.ts UI/Angular/src/app/features/receivables/payment-detail.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): payment detail screen with allocations + unapplied + journal drill; share InvoiceAllocationLine"
```

---

### Task 4: Frontend — PaymentList whole-row drill-in

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/payment-list.ts`
- Modify (extend): `UI/Angular/src/app/features/receivables/payment-list.spec.ts`

**Interfaces:**
- Consumes: `Router`, the `payments/:id` route (Task 3).
- Produces: nothing.

**Note:** the list's Allocated/Unapplied columns become correct automatically via Task 2 (the backend now folds `allocations`); this task only adds row navigation. `PaymentList` has no in-row buttons and no memo cell, so there is nothing to insulate or truncate.

- [ ] **Step 1: Write the failing test**

Add `Router` to the router import at the top of `payment-list.spec.ts`:
```ts
import { provideRouter, Router } from '@angular/router';
```
Add this spec inside `describe('PaymentList', ...)` (`vi` is available globally):
```ts
  it('navigates to the payment detail when a row is clicked', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(PaymentList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/payments') && r.params.get('customerId') === 'cu1')
      .flush([payment('p1', 100, 60)]);
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).toHaveBeenCalledWith(['/receivables/payments', 'p1']);
  });
```

- [ ] **Step 2: Run to verify it fails**

Run: `npx ng test --include='**/payment-list.spec.ts' --watch=false`
Expected: the new spec FAILS (row not clickable → `navigate` not called). Pre-existing PaymentList specs still pass.

- [ ] **Step 3: Wire the drill-in in `payment-list.ts`**

**3a.** Change line 2 from `import { RouterLink } from '@angular/router';` to:
```ts
import { Router, RouterLink } from '@angular/router';
```

**3b.** Replace the row `<tr>` opening tag (line 51). Change:
```html
                  <tr hlmTr [class.opacity-50]="p.voided">
```
to:
```html
                  <tr hlmTr role="button" tabindex="0"
                      class="cursor-pointer hover:bg-muted/50"
                      [class.opacity-50]="p.voided"
                      (click)="open(p.id)"
                      (keydown.enter)="open(p.id)">
```

**3c.** Inject `Router` and add `open`. After `readonly customerId = this.svc.selectedCustomerId;` (line 70), add:
```ts
  private readonly router = inject(Router);
```
Add the method (e.g. after `allocated`, ~line 88):
```ts
  open(id: string): void { void this.router.navigate(['/receivables/payments', id]); }
```

- [ ] **Step 4: Run the specs to verify they pass**

Run: `npx ng test --include='**/payment-list.spec.ts' --watch=false`
Expected: all specs PASS (pre-existing + 1 new), output pristine.

- [ ] **Step 5: Compile gate**

Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/payment-list.ts UI/Angular/src/app/features/receivables/payment-list.spec.ts
git commit -m "feat(ui): payment-list whole-row drill-in"
```

---

## Self-Review

**Spec coverage:**
- Backend `GET /payments/{id}` returning `PaymentView{payment, allocations, unapplied, journalEntryId}` via `GetPaymentViewAsync` (Posted posting pick, allocations resolved to invoice numbers, `Unapplied = Amount − Σallocations`) → Task 1. ✓
- Shared `InvoiceAllocationLine` rename (own file; backend + FE; no lingering old refs) → Task 1 (backend) + Task 3 (FE). ✓
- Payments-list allocations fold (fixes PaymentList + InvoiceDetail; no FE consumer change) → Task 2. ✓
- `ar.read` gating automatic (no code). ✓
- payment-detail screen (header + method + allocations table + total + unapplied line + `gl.read`-gated journal link) + `payments/:id` ungated route + `getPayment` service + `PaymentView` type → Task 3. ✓
- PaymentList whole-row drill-in (unconditional, same-area; no button/memo) → Task 4. ✓
- Tests: backend detail (allocations + numbers + unapplied + entry id; fully-allocated → 0; 404) + list fold assertion (Task 1/2); FE detail renders header/method/allocations/total/unapplied + journal link present/absent/gated (Task 3); FE list row-nav (Task 4). ✓

**Placeholder scan:** every step has complete code; no TBD.

**Type/name consistency:** `PaymentView{payment, allocations, unapplied, journalEntryId}` + `InvoiceAllocationLine{invoiceId, invoiceNumber, amount}` identical backend record ↔ FE interface; `getPayment`/`GetPaymentViewAsync`/`GetPayment` names consistent; route `/receivables/payments/:id` matches `open(id)` navigation and the spec's `navigate(['/receivables/payments', 'p1'])`; `PaymentWithAllocations` serializes to the existing FE `Payment` shape (`{id, customerId, date, amount, method, allocations:{targetId,amount}[], voided}`) so PaymentList + InvoiceDetail consume it unchanged; rename leaves zero `CreditAllocationLine` references after Task 1 (backend) and Task 3 (FE).

## Execution Handoff

Two execution options:
1. **Subagent-Driven (recommended)** — fresh implementer per task, per-task review, final whole-branch review.
2. **Inline Execution** — execute in this session with checkpoints.
