# Credit Detail Implementation Plan (Slice 2b-2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a credit-detail screen reachable by whole-row drill-in from the Credits list, backed by a new type-qualified `GET /clients/{id}/credits/{type}/{creditId}` returning the credit, the invoices it was applied to (allocations resolved to invoice numbers), and its posted journal entry id, with a `gl.read`-gated "View journal entry" drill.

**Architecture:** Backend (Receivables module): a `CreditView` read-model (credit + allocations + journal entry id) and `CreditAllocationLine`, a `PaymentService.GetCreditViewAsync` that loads the per-type document, folds the amount via `SettlementRelief`, and reads the credit's GL posting to recover allocations (invoice-dimensioned lines resolved to invoice numbers) and the journal entry id, plus a new `GetCreditApplicationAsync` store getter (the one gap), exposed at `GET /clients/{id}/credits/{type}/{creditId}` mirroring `GetRefund`/`GetInvoice`. Frontend: a `credit-detail` screen (header + allocations table + `gl.read`-gated journal link) + `credits/:type/:id` route + a `getCredit` service method, and the credit-list rows made whole-row clickable (Void button insulated, memo truncated). No new capability wiring — the GET is `ar.read`-gated by the engine's scoped document store.

**Tech Stack:** .NET 10 minimal APIs + MongoDB (EphemeralMongo in tests); Angular 22 (standalone, OnPush, zoneless), Tailwind v4, Spartan Helm; xUnit (backend), Vitest runner + TestBed (frontend).

## Global Constraints

- **Backend:** namespaces follow folder structure (`Accounting101.Receivables`). The new endpoint returns `CreditView` and follows the exact shape of `GetRefund` (`ReceivablesEndpoints.cs`): `return view is null ? Results.NotFound() : Results.Ok(view)`. The endpoint group already carries `.RequireAuthorization()`. **Rider auto-converts explicit types to `var` — stage explicit file lists and check for stray churn before each commit.**
- **Frontend:** standalone components, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. New detail route is ungated like every other detail route. The credit-list drill-in is same-area (a Credits-list viewer already holds `ar.read`), so rows are unconditionally clickable. FE test runner is **Vitest** — `vi.spyOn` (available globally, as in `refund-list.spec.ts`), not Jasmine `spyOn`; nav spies chain `.mockResolvedValue(true)`.
- **Wire shapes** identical backend↔frontend (host `JsonNamingPolicy.CamelCase`): `CreditView{ credit: CreditDocument, allocations: CreditAllocationLine[], journalEntryId: string | null }`; `CreditAllocationLine{ invoiceId: string, invoiceNumber: string | null, amount: number }`. `Guid? JournalEntryId` → `string | null`; `string? InvoiceNumber` → `string | null`.
- The "View journal entry" link is a cross-area AR→GL drill — gate it on `gl.read` via `*appCan` from the start (2b-1 fast-follow lesson).
- Only touch the files named per task. Do NOT touch refund-* (2b-1, merged), the customer/vendor statement lists (2c), payables credit-* screens, or any other module.
- Backend test run (focused): `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetCreditsEndpointTests"`. FE unit test: `npx ng test --include='<glob>' --watch=false` from `UI/Angular`. FE compile gate: `npx ng build --configuration development` from `UI/Angular`.
- `environment.ts` stays modified/uncommitted (local dev config, never commit).
- Branch `feat/credit-detail`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: Backend — CreditView + GetCreditApplicationAsync + GET /credits/{type}/{id}

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables/CreditView.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs` (make `InvoiceDimension` public)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs` (add `GetCreditApplicationAsync` to `IPaymentStore`)
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs` (implement `GetCreditApplicationAsync`)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` (add `GetCreditViewAsync`)
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (add `GetCredit` handler + route)
- Test (extend): `Modules/Receivables/Accounting101.Receivables.Tests/GetCreditsEndpointTests.cs`

**Interfaces:**
- Consumes: `IPaymentStore.GetCreditNoteAsync`/`GetWriteOffAsync`, `IInvoiceStore.GetAsync`, `IPaymentAccountsProvider.GetAsync`, `ILedgerClient.GetEntriesBySourceRefAsync`, `SettlementRelief.ForSourceAsync`, `EntryResponse`/`EntryLineResponse`, `PaymentPosting.InvoiceDimension`.
- Produces: `CreditAllocationLine(Guid InvoiceId, string? InvoiceNumber, decimal Amount)`; `CreditView(CreditDocument Credit, IReadOnlyList<CreditAllocationLine> Allocations, Guid? JournalEntryId)`; `IPaymentStore.GetCreditApplicationAsync(Guid, Guid, CancellationToken) → CreditApplication?`; `PaymentService.GetCreditViewAsync(Guid, string, Guid, CancellationToken) → CreditView?`; route `GET /clients/{clientId}/credits/{type}/{creditId:guid}`.

- [ ] **Step 1: Write the failing tests**

Add three test methods to `GetCreditsEndpointTests.cs`, inside the existing class (it already has `SetUpChartAsync`, `IssueInvoiceAsync`, `ApproveBySourceRefAsync`, and `using System.Net;`). Reference the new `CreditView`/`CreditAllocationLine` types (they will not exist yet — that is the RED; the test project fails to compile):

```csharp
    [Fact]
    public async Task GET_credit_note_by_id_returns_folded_amount_allocations_and_journal_entry_id()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv1 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);
        Guid inv2 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);

        // A credit note allocated across two invoices: 60 to inv1, 40 to inv2 (total 100).
        CreditNote creditNote = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 10),
                [new Allocation(inv1, 60m), new Allocation(inv2, 40m)], "returned goods")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreditNote>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditNote.Id);

        InvoiceView iv1 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{inv1}"))!;
        InvoiceView iv2 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{inv2}"))!;

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={creditNote.Id}"))!;
        Guid expectedEntryId = entries.Single(e => e is { Status: "Active", ReversalOf: null }).Id;

        CreditView view = (await clerk.GetFromJsonAsync<CreditView>(
            $"/clients/{clientId}/credits/credit-note/{creditNote.Id}"))!;

        Assert.Equal("credit-note", view.Credit.Type);
        Assert.Equal(creditNote.Id, view.Credit.Id);
        Assert.Equal(100m, view.Credit.Amount);
        Assert.Equal("returned goods", view.Credit.Memo);
        Assert.False(view.Credit.Voided);
        Assert.Equal(expectedEntryId, view.JournalEntryId);

        Assert.Equal(2, view.Allocations.Count);
        CreditAllocationLine a1 = view.Allocations.Single(a => a.InvoiceId == inv1);
        Assert.Equal(60m, a1.Amount);
        Assert.Equal(iv1.Invoice.Number, a1.InvoiceNumber);
        CreditAllocationLine a2 = view.Allocations.Single(a => a.InvoiceId == inv2);
        Assert.Equal(40m, a2.Amount);
        Assert.Equal(iv2.Invoice.Number, a2.InvoiceNumber);
    }

    [Fact]
    public async Task GET_credit_application_by_id_has_null_memo_and_resolved_allocations()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid invSrc = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);     // overpaid → credit
        Guid invTarget = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);  // credit-application target

        // Overpay invSrc by 50 → 50 of unapplied customer credit.
        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 2), 150m, "check",
                    [new Allocation(invSrc, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        CreditApplication creditApp = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
                new CreditApplicationRequest(customer.Id, new DateOnly(2026, 3, 8), [new Allocation(invTarget, 50m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditApp.Id);

        CreditView view = (await clerk.GetFromJsonAsync<CreditView>(
            $"/clients/{clientId}/credits/credit-application/{creditApp.Id}"))!;

        Assert.Equal("credit-application", view.Credit.Type);
        Assert.Null(view.Credit.Memo);
        Assert.Equal(50m, view.Credit.Amount);
        CreditAllocationLine only = Assert.Single(view.Allocations);
        Assert.Equal(invTarget, only.InvoiceId);
        Assert.Equal(50m, only.Amount);
    }

    [Fact]
    public async Task GET_credit_by_unknown_id_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/credits/credit-note/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GET_credit_by_unknown_type_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/credits/bogus/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetCreditsEndpointTests"`
Expected: BUILD FAILURE — `CreditView` / `CreditAllocationLine` do not exist (the RED for typed tests).

- [ ] **Step 3: Create the `CreditView` + `CreditAllocationLine` records**

Create `Modules/Receivables/Accounting101.Receivables/CreditView.cs`:

```csharp
namespace Accounting101.Receivables;

/// <summary>One invoice a credit was applied to: the invoice's id, its number (null if unnumbered),
/// and the amount of this credit applied to it. Recovered from the credit's GL entry lines (each
/// allocation line carries an "Invoice" dimension and the allocated amount).</summary>
public sealed record CreditAllocationLine(Guid InvoiceId, string? InvoiceNumber, decimal Amount);

/// <summary>A credit (note, write-off, or application) plus the invoices it was applied to and the id of
/// its posted journal entry — what the credit detail endpoint returns. Credit reuses the unified
/// CreditDocument shape so the detail header matches the list row; Allocations are folded from the GL
/// posting; JournalEntryId lets the UI drill to the GL entry (null if none is found).</summary>
public sealed record CreditView(
    CreditDocument Credit,
    IReadOnlyList<CreditAllocationLine> Allocations,
    Guid? JournalEntryId);
```

- [ ] **Step 4: Make `InvoiceDimension` public in `PaymentPosting.cs`**

In `Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs`, change the dimension constant from private to public so the service can reference it (avoids a magic string). Change:
```csharp
    private const string InvoiceDimension = "Invoice";
```
to:
```csharp
    public const string InvoiceDimension = "Invoice";
```

- [ ] **Step 5: Add `GetCreditApplicationAsync` to the store port + implementation**

In `Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs`, add to `IPaymentStore` immediately after the `GetCreditApplicationsByCustomerAsync` line (line 12):
```csharp
    Task<CreditApplication?> GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default);
```

In `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs`, add this method next to `GetCreditApplicationsByCustomerAsync` (~line 50), mirroring `GetCreditNoteAsync` (the collection constant `CreditApplications = "credit-applications"` and mapper `MapCredit` already exist in this file):
```csharp
    public async Task<CreditApplication?> GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default)
    {
        DocumentResult<CreditApplicationBody>? r = await documents.GetAsync<CreditApplicationBody>(clientId, CreditApplications, creditApplicationId, ct);
        return r is null ? null : MapCredit(r);
    }
```

- [ ] **Step 6: Add `GetCreditViewAsync` to `PaymentService`**

In `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`, add this method just after `GetCreditsByCustomerAsync` (~line 139). It uses the primary-constructor params `payments`, `invoices`, `accounts`, `ledger` already in scope, the same amount fold `GetCreditsByCustomerAsync` uses, and the same `Active`/`ReversalOf == null` posting pick as `GetRefundViewAsync`:

```csharp
    /// <summary>A single credit (note, write-off, or application) plus the invoices it was applied to and
    /// its posted journal entry id — for the detail screen's drill-in. Amount is the document's AR relief
    /// folded Posted-only (identical to the Credits list); allocations are recovered from the posting's
    /// Invoice-dimensioned lines and resolved to invoice numbers; memo is null for credit applications.
    /// Returns null for an unknown type or a missing document.</summary>
    public async Task<CreditView?> GetCreditViewAsync(Guid clientId, string type, Guid creditId, CancellationToken ct = default)
    {
        Guid customerId;
        DateOnly date;
        string? memo;
        bool voided;
        switch (type)
        {
            case "credit-note":
                CreditNote? note = await payments.GetCreditNoteAsync(clientId, creditId, ct);
                if (note is null) return null;
                (customerId, date, memo, voided) = (note.CustomerId, note.Date, note.Memo, note.Voided);
                break;
            case "write-off":
                WriteOff? writeOff = await payments.GetWriteOffAsync(clientId, creditId, ct);
                if (writeOff is null) return null;
                (customerId, date, memo, voided) = (writeOff.CustomerId, writeOff.Date, writeOff.Memo, writeOff.Voided);
                break;
            case "credit-application":
                CreditApplication? app = await payments.GetCreditApplicationAsync(clientId, creditId, ct);
                if (app is null) return null;
                (customerId, date, memo, voided) = (app.CustomerId, app.Date, null, app.Voided);
                break;
            default:
                return null;
        }

        PaymentPostingAccounts postingAccounts = await accounts.GetAsync(clientId, ct);
        decimal amount = await SettlementRelief.ForSourceAsync(
            ledger, clientId, creditId, postingAccounts.ReceivableAccountId, ct, postedOnly: true);
        CreditDocument credit = new(type, creditId, customerId, date, amount, memo, voided);

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, creditId, ct);
        EntryResponse? postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });

        List<CreditAllocationLine> allocations = [];
        if (postingEntry is not null)
        {
            // GroupBy preserves first-appearance order of each invoice key → posting-line order.
            foreach (IGrouping<Guid, EntryLineResponse> group in postingEntry.Lines
                         .Where(l => l.Dimensions.ContainsKey(PaymentPosting.InvoiceDimension))
                         .GroupBy(l => l.Dimensions[PaymentPosting.InvoiceDimension]))
            {
                Invoice? invoice = await invoices.GetAsync(clientId, group.Key, ct);
                allocations.Add(new CreditAllocationLine(group.Key, invoice?.Number, group.Sum(l => l.Amount)));
            }
        }

        return new CreditView(credit, allocations, postingEntry?.Id);
    }
```

- [ ] **Step 7: Add the `GetCredit` endpoint + route**

In `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs`:

Register the route immediately after the `ListCredits` map (`clients.MapGet("/credits", ListCredits);`, line 28) — add:
```csharp
        clients.MapGet("/credits/{type}/{creditId:guid}", GetCredit);
```

Add the handler next to `ListCredits` (~after line 199), mirroring `GetRefund`:
```csharp
    private static async Task<IResult> GetCredit(
        Guid clientId, string type, Guid creditId, PaymentService service, CancellationToken cancellationToken)
    {
        CreditView? view = await service.GetCreditViewAsync(clientId, type, creditId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetCreditsEndpointTests"`
Expected: PASS (the four new tests + the pre-existing ones).

- [ ] **Step 9: Commit**

Stage the explicit file list (guard against Rider `var` churn):
```bash
git add Modules/Receivables/Accounting101.Receivables/CreditView.cs Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs Modules/Receivables/Accounting101.Receivables/PaymentService.cs Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs Modules/Receivables/Accounting101.Receivables.Tests/GetCreditsEndpointTests.cs
git commit -m "feat(receivables): GET /credits/{type}/{id} returning credit + allocations + journal entry id"
```

---

### Task 2: Frontend — credit-detail screen + route

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.ts` (add `CreditAllocationLine` + `CreditView` interfaces)
- Modify: `UI/Angular/src/app/core/receivables/receivables.service.ts` (add `getCredit`)
- Create: `UI/Angular/src/app/features/receivables/credit-detail.ts`
- Create: `UI/Angular/src/app/features/receivables/credit-detail.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add `credits/:type/:id` route)

**Interfaces:**
- Consumes: Task 1's `CreditView` wire shape; `ReceivablesService`, `ClientContextService`, `CreditType`, `CanDirective`.
- Produces: `CreditAllocationLine` + `CreditView` TS interfaces; `ReceivablesService.getCredit(type, id): Observable<CreditView>`; `CreditDetail` component; route `credits/:type/:id`.

- [ ] **Step 1: Write the failing component spec**

Create `UI/Angular/src/app/features/receivables/credit-detail.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CreditDetail } from './credit-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot(type: string, id: string, caps: string[] = ['gl.read']) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['type', type], ['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(CreditDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('CreditDetail', () => {
  it('renders header, allocations table with total, and journal link', () => {
    const { fixture, ctrl } = boot('credit-note', 'cn1');
    ctrl.expectOne('http://localhost:5000/clients/C1/credits/credit-note/cn1').flush({
      credit: { type: 'credit-note', id: 'cn1', customerId: 'cu1', date: '2026-06-30', amount: 100, memo: 'returned goods', voided: false },
      allocations: [
        { invoiceId: 'inv1', invoiceNumber: '1042', amount: 60 },
        { invoiceId: 'inv2', invoiceNumber: '1051', amount: 40 },
      ],
      journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Credit note');
    expect(text).toContain('returned goods');
    expect(text).toContain('1042');
    expect(text).toContain('60.00');
    expect(text).toContain('1051');
    expect(text).toContain('40.00');
    expect(text).toContain('100.00');   // total
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('renders a credit-application with a dash memo and no journal link when null', () => {
    const { fixture, ctrl } = boot('credit-application', 'ca1');
    ctrl.expectOne('http://localhost:5000/clients/C1/credits/credit-application/ca1').flush({
      credit: { type: 'credit-application', id: 'ca1', customerId: 'cu1', date: '2026-06-30', amount: 50, memo: null, voided: false },
      allocations: [{ invoiceId: 'inv4', invoiceNumber: '1099', amount: 50 }],
      journalEntryId: null,
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Apply credit');
    expect(text).toContain('—');   // memo dash
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });

  it('hides the journal link when the user lacks gl.read', () => {
    const { fixture, ctrl } = boot('credit-note', 'cn3', []);
    ctrl.expectOne('http://localhost:5000/clients/C1/credits/credit-note/cn3').flush({
      credit: { type: 'credit-note', id: 'cn3', customerId: 'cu1', date: '2026-06-30', amount: 20, memo: 'x', voided: false },
      allocations: [{ invoiceId: 'inv1', invoiceNumber: '1042', amount: 20 }],
      journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run: `npx ng test --include='**/credit-detail.spec.ts' --watch=false`
Expected: FAIL — cannot resolve `./credit-detail` (component does not exist).

- [ ] **Step 3: Add the `CreditAllocationLine` + `CreditView` interfaces**

In `UI/Angular/src/app/core/receivables/receivables.ts`, add immediately after the `CreditDocument` interface (ends line 51, before `CreditNoteRequest`):
```ts
export interface CreditAllocationLine { invoiceId: string; invoiceNumber: string | null; amount: number; }
export interface CreditView { credit: CreditDocument; allocations: CreditAllocationLine[]; journalEntryId: string | null; }
```

- [ ] **Step 4: Add the `getCredit` service method**

In `UI/Angular/src/app/core/receivables/receivables.service.ts`:

Add `CreditView` to the import from `'./receivables'` (line 7 — append `, CreditView` to the destructured list).

Add the method next to `getRefund` (line 72):
```ts
  getCredit(type: string, id: string): Observable<CreditView> { return this.http.get<CreditView>(this.base(`/credits/${type}/${id}`)); }
```

- [ ] **Step 5: Create the `credit-detail` component**

Create `UI/Angular/src/app/features/receivables/credit-detail.ts`:

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { CreditAllocationLine, CreditType, CreditView } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-credit-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables/credits" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Credits</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">{{ label(v.credit.type) }}</h1>
          <span class="text-sm px-2 py-0.5 rounded border border-border">{{ v.credit.voided ? 'Voided' : 'Active' }}</span>
        </div>
        <div class="text-sm flex flex-col gap-1">
          <div><span class="text-muted-foreground">Date</span> · {{ formatDate(v.credit.date) }}</div>
          <div><span class="text-muted-foreground">Amount</span> · <span class="tabular-nums">{{ money(v.credit.amount) }}</span></div>
          <div><span class="text-muted-foreground">Memo</span> · {{ v.credit.memo ?? '—' }}</div>
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
                  <td class="py-0.5">Total</td>
                  <td class="py-0.5 text-right tabular-nums">{{ money(sum(v.allocations)) }}</td>
                </tr>
              </tbody>
            </table>
          }
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
export class CreditDetail {
  private readonly svc = inject(ReceivablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly type = this.route.snapshot.paramMap.get('type')!;
  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<CreditView | null>(null);
  readonly loadError = signal<string | null>(null);

  constructor() {
    this.svc.getCredit(this.type, this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.view.set(v),
      error: (e) => this.loadError.set(extractProblem(e).detail),
    });
  }

  sum(lines: CreditAllocationLine[]): number { return lines.reduce((s, a) => s + a.amount, 0); }
  label(t: CreditType): string {
    return t === 'credit-note' ? 'Credit note' : t === 'write-off' ? 'Write-off' : 'Apply credit';
  }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 6: Add the route**

In `UI/Angular/src/app/app.routes.ts`:

Add the import next to the other receivables feature imports (after line 25 `import { RefundDetail } ...`):
```ts
import { CreditDetail } from './features/receivables/credit-detail';
```

Add the route after the `credits/new` entry (line 116), in the RECEIVABLES route block (mirroring `refunds/:id` at line 119) — NOT the payables block:
```ts
    { path: 'credits/:type/:id', component: CreditDetail },
```

- [ ] **Step 7: Run the spec + compile gate**

Run: `npx ng test --include='**/credit-detail.spec.ts' --watch=false` → all three specs PASS.
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/receivables/receivables.ts UI/Angular/src/app/core/receivables/receivables.service.ts UI/Angular/src/app/features/receivables/credit-detail.ts UI/Angular/src/app/features/receivables/credit-detail.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): credit detail screen with allocations + journal-entry drill"
```

---

### Task 3: Frontend — credit-list drill-in + memo truncation

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/credit-list.ts`
- Modify (extend): `UI/Angular/src/app/features/receivables/credit-list.spec.ts`

**Interfaces:**
- Consumes: `Router`, `TruncateDirective`, `CreditType`, the `credits/:type/:id` route (Task 2).
- Produces: nothing.

- [ ] **Step 1: Write the failing tests**

Add `Router` to the router import at the top of `credit-list.spec.ts`:
```ts
import { provideRouter, Router } from '@angular/router';
```
Add these two specs inside `describe('CreditList', ...)` (`vi` is available globally, as in `refund-list.spec.ts`):

```ts
  it('navigates to the credit detail when a row is clicked', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [credit('cn1', 'credit-note', 100, 'x')]);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).toHaveBeenCalledWith(['/receivables/credits', 'credit-note', 'cn1']);
  });

  it('does not navigate when the Void button is clicked', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [credit('cn1', 'credit-note', 100, 'x')]);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void') as HTMLButtonElement;
    voidBtn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).not.toHaveBeenCalled();
    // flush the void POST + the reload the void triggers, so HttpTestingController stays clean
    ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes/cn1/void').flush({});
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/credits')).flush([credit('cn1', 'credit-note', 100, 'x', true)]);
  });
```

- [ ] **Step 2: Run to verify they fail**

Run: `npx ng test --include='**/credit-list.spec.ts' --watch=false`
Expected: the row-nav spec FAILS (row not clickable → `navigate` not called). Pre-existing CreditList specs still pass.

- [ ] **Step 3: Wire the drill-in + truncation in `credit-list.ts`**

**3a.** Update imports. Change line 2 from `import { RouterLink } from '@angular/router';` to:
```ts
import { Router, RouterLink } from '@angular/router';
```
Add the directive import (next to the other feature imports, after line 12 `import { CanDirective } ...`):
```ts
import { TruncateDirective } from '../../shared/truncate.directive';
```

**3b.** Add `TruncateDirective` to the `imports` array (line 17):
```ts
  imports: [RouterLink, HlmButton, ...HlmTableImports, CustomerSelect, CanDirective, TruncateDirective],
```

**3c.** Replace the row `<tr>` block (lines 50-62). Change:
```html
                @for (c of credits(); track c.id) {
                  <tr hlmTr [class.opacity-50]="c.voided">
                    <td hlmTd>{{ fmtDate(c.date) }}</td>
                    <td hlmTd>{{ label(c.type) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(c.amount) }}</td>
                    <td hlmTd>{{ c.memo ?? '—' }}</td>
                    <td hlmTd>{{ c.voided ? 'Voided' : 'Active' }}</td>
                    <td hlmTd>
                      @if (c.type !== 'credit-application' && !c.voided) {
                        <button *appCan="'ar.write'" hlmBtn size="sm" variant="outline" (click)="doVoid(c)" [disabled]="busy()">Void</button>
                      } @else { <span class="text-muted-foreground">—</span> }
                    </td>
                  </tr>
                }
```
to:
```html
                @for (c of credits(); track c.id) {
                  <tr hlmTr role="button" tabindex="0"
                      class="cursor-pointer hover:bg-muted/50"
                      [class.opacity-50]="c.voided"
                      (click)="open(c.type, c.id)"
                      (keydown.enter)="open(c.type, c.id)">
                    <td hlmTd>{{ fmtDate(c.date) }}</td>
                    <td hlmTd>{{ label(c.type) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(c.amount) }}</td>
                    <td hlmTd><span appTruncate>{{ c.memo ?? '—' }}</span></td>
                    <td hlmTd>{{ c.voided ? 'Voided' : 'Active' }}</td>
                    <td hlmTd>
                      @if (c.type !== 'credit-application' && !c.voided) {
                        <button *appCan="'ar.write'" hlmBtn size="sm" variant="outline"
                                (click)="$event.stopPropagation(); doVoid(c)"
                                (keydown.enter)="$event.stopPropagation()"
                                [disabled]="busy()">Void</button>
                      } @else { <span class="text-muted-foreground">—</span> }
                    </td>
                  </tr>
                }
```

**3d.** Inject `Router` and add `open`. After `private readonly destroyRef = inject(DestroyRef);` (line 74), add:
```ts
  private readonly router = inject(Router);
```
Add the method (e.g. after `doVoid`, ~line 102):
```ts
  open(type: CreditType, id: string): void { void this.router.navigate(['/receivables/credits', type, id]); }
```

- [ ] **Step 4: Run the specs to verify they pass**

Run: `npx ng test --include='**/credit-list.spec.ts' --watch=false`
Expected: all specs PASS (pre-existing + 2 new), output pristine.

- [ ] **Step 5: Compile gate**

Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/credit-list.ts UI/Angular/src/app/features/receivables/credit-list.spec.ts
git commit -m "feat(ui): credit-list whole-row drill-in + memo truncation"
```

---

## Self-Review

**Spec coverage:**
- Backend `GET /credits/{type}/{id}` returning `CreditView{credit, allocations, journalEntryId}` via `GetCreditViewAsync` (per-type document load, amount fold via `SettlementRelief`, allocations from posting Invoice-dimensioned lines resolved to invoice numbers, `Active`/`ReversalOf==null` posting pick) + new `GetCreditApplicationAsync` getter + `InvoiceDimension` exposed → Task 1. ✓
- `ar.read` gating automatic (no code). ✓
- credit-detail screen (header + allocations table + total + `gl.read`-gated journal link) + `credits/:type/:id` ungated route + `getCredit` service + `CreditView`/`CreditAllocationLine` types → Task 2. ✓
- credit-list whole-row drill-in (unconditional, same-area), Void `stopPropagation` (click + Enter), memo `appTruncate` → Task 3. ✓
- Tests: backend credit-note (amount + 2 allocations w/ numbers + entry id), credit-application (null memo + allocations), 404 unknown id, 404 unknown type (Task 1); FE detail renders header + allocations + total + journal link present/absent/gated, credit-application dash memo (Task 2); FE list row-nav + Void no-nav (Task 3). ✓

**Placeholder scan:** every step has complete code; no TBD.

**Type/name consistency:** `CreditView{credit, allocations, journalEntryId}` + `CreditAllocationLine{invoiceId, invoiceNumber, amount}` identical backend record ↔ FE interface; `getCredit`/`GetCreditViewAsync`/`GetCredit` names consistent; route `/receivables/credits/:type/:id` matches `open(type, id)` navigation and the spec's `navigate` assertion (`['/receivables/credits', 'credit-note', 'cn1']`); `PaymentPosting.InvoiceDimension` referenced after being made public; `GetCreditApplicationAsync` defined in Task 1 port + impl and consumed by `GetCreditViewAsync`; `CreditType` already exported and reused in the detail component and list `open`.

## Execution Handoff

Two execution options:
1. **Subagent-Driven (recommended)** — fresh implementer per task, per-task review, final whole-branch review.
2. **Inline Execution** — execute in this session with checkpoints.
