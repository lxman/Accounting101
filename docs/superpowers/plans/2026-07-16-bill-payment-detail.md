# AP Bill-Payment Detail Implementation Plan (Slice 2c-2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an AP bill-payment-detail screen reachable by whole-row drill-in from the Bill Payments list, backed by a new `GET /clients/{id}/bill-payments/{paymentId}` returning the payment, its allocations (resolved to bill numbers), the unapplied remainder, and its journal entry id — and fix the pre-existing bill-payments-list bug (the list + bill-detail read per-payment allocations the backend never folds).

**Architecture:** Backend (Payables): a general `BillAllocationLine` (target-named, mirroring the shared AR `InvoiceAllocationLine`) + `BillPaymentView` + `BillPaymentService.GetBillPaymentViewAsync` (allocations folded from the payment's GL posting — Posted pick, `Bill`-dimensioned lines resolved to bill numbers; `Unapplied = Amount − Σallocations`); a `GET /bill-payments/{id}` endpoint; and a payments-list allocations fold so `BillPaymentList` + `BillDetail` (both read `p.allocations`) get real data. Frontend: `BillPaymentView` interface + `getBillPayment` + a `bill-payment-detail` screen + `payments/:id` route (payables block), and whole-row drill-in on `BillPaymentList`. Exact Payables mirror of the merged 2c-1 (AR payment detail).

**Tech Stack:** .NET 10 minimal APIs + MongoDB (EphemeralMongo in tests); Angular 22 (standalone, OnPush, zoneless), Tailwind v4, Spartan Helm; xUnit (backend), Vitest runner + TestBed (frontend).

## Global Constraints

- **Backend:** namespaces follow folder structure (`Accounting101.Payables`). New detail endpoint returns `BillPaymentView` and follows the exact shape of `GetBill`: `return view is null ? Results.NotFound() : Results.Ok(view)`. The endpoint group already carries `.RequireAuthorization()`. **Rider auto-converts explicit types to `var` — stage explicit file lists and check for stray churn before each commit.**
- **Frontend:** standalone components, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. New detail route is ungated. The Bill-Payments-list drill-in is same-area (a Bill-Payments viewer already holds `ap.read`), so rows are unconditionally clickable. FE test runner is **Vitest** — `vi.spyOn` (global), not Jasmine; nav spies chain `.mockResolvedValue(true)`.
- **Wire shapes** identical backend↔frontend (host `JsonNamingPolicy.CamelCase`): `BillPaymentView{ payment: BillPayment, allocations: BillAllocationLine[], unapplied: number, journalEntryId: string | null }`; `BillAllocationLine{ billId, billNumber: string|null, amount }`. The bill-payments-list item serializes to the existing FE `BillPayment` shape `{ id, vendorId, date, amount, method: string|null, allocations: {targetId, amount}[], voided }`.
- Posting pick is `{Status:"Active", Posting:"Posted", ReversalOf:null}` everywhere allocations/journal ids are read.
- The "View journal entry" link is `gl.read`-gated via `*appCan`.
- Only touch files named per task. Do NOT touch receivables (2c-1, done), statement builders / customer-account / vendor-account (2c-3), or other modules.
- Backend test run (focused): `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~BillPaymentListEndpointTests"`. FE unit test: `npx ng test --include='<glob>' --watch=false` from `UI/Angular`. FE compile gate: `npx ng build --configuration development`.
- `environment.ts` stays modified/uncommitted (local dev config, never commit).
- Branch `feat/bill-payment-detail`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: Backend — BillAllocationLine + BillPaymentView + GET /bill-payments/{id}

**Files:**
- Create: `Modules/Payables/Accounting101.Payables/BillAllocationLine.cs`
- Create: `Modules/Payables/Accounting101.Payables/BillPaymentView.cs`
- Modify: `Modules/Payables/Accounting101.Payables/BillPaymentService.cs` (add `GetBillPaymentViewAsync`)
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs` (add `GetBillPayment` handler + route)
- Test (extend): `Modules/Payables/Accounting101.Payables.Tests/BillPaymentListEndpointTests.cs`

**Interfaces:**
- Consumes: `IBillPaymentStore.GetPaymentAsync`, `IBillStore.GetAsync`, `ILedgerClient.GetEntriesBySourceRefAsync`, `BillPosting.BillDimension` (public const), `EntryResponse`/`EntryLineResponse`, `BillPayment`, `Bill`.
- Produces: `BillAllocationLine(Guid BillId, string? BillNumber, decimal Amount)`; `BillPaymentView(BillPayment Payment, IReadOnlyList<BillAllocationLine> Allocations, decimal Unapplied, Guid? JournalEntryId)`; `BillPaymentService.GetBillPaymentViewAsync(Guid, Guid, CancellationToken) → BillPaymentView?`; route `GET /clients/{clientId}/bill-payments/{paymentId:guid}`.

- [ ] **Step 1: Add the test helpers + write the failing tests**

`BillPaymentListEndpointTests.cs` currently only has `PutAccountAsync` and does pure prepayments. Add three helpers (copied from the sibling `BillPaymentDimensionTests.cs` pattern) INSIDE the `BillPaymentListEndpointTests` class, then the three detail tests. Add `using` if missing (`System.Net`, `Accounting101.Ledger.Contracts`, `Accounting101.Settlement` are already present).

Helpers to add:
```csharp
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId, "2000", "Accounts Payable", "Liability", null, ["Vendor", "Bill"]);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId, "1300", "Vendor Credits", "Asset", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId, "5200", "Rent Expense", "Expense", null);
    }

    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    private async Task<Bill> EnterBillAsync(HttpClient clerk, HttpClient approver, Guid clientId, Guid vendorId, string description)
    {
        DraftBillRequest draftRequest = new(vendorId, BillDate: new DateOnly(2026, 3, 1), DueDate: null,
            VendorReference: null, Memo: null, Lines: [new BillLineBody(description, 100m, fixture.RentExpenseAccountId)]);
        Bill draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", draftRequest))
            .Content.ReadFromJsonAsync<Bill>())!;
        Bill entered = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{draft.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered.Id);
        return entered;
    }
```

Detail tests to add (reference the new `BillPaymentView`/`BillAllocationLine` — the RED is a build failure):
```csharp
    [Fact]
    public async Task GET_bill_payment_by_id_returns_allocations_unapplied_and_journal_entry_id()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        Bill billA = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "Rent A");   // 100
        Bill billB = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "Rent B");   // 100

        // Pay 150 allocating 100→A and 30→B (total 130 applied), leaving 20 unapplied.
        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor.Id, new DateOnly(2026, 3, 31), 150m, "check",
                    [new Allocation(billA.Id, 100m), new Allocation(billB.Id, 30m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={payment.Id}"))!;
        Guid expectedEntryId = entries.Single(e => e is { Status: "Active", ReversalOf: null }).Id;

        BillPaymentView view = (await clerk.GetFromJsonAsync<BillPaymentView>(
            $"/clients/{clientId}/bill-payments/{payment.Id}"))!;

        Assert.Equal(payment.Id, view.Payment.Id);
        Assert.Equal(150m, view.Payment.Amount);
        Assert.Equal("check", view.Payment.Method);
        Assert.False(view.Payment.Voided);
        Assert.Equal(expectedEntryId, view.JournalEntryId);
        Assert.Equal(20m, view.Unapplied);

        Assert.Equal(2, view.Allocations.Count);
        BillAllocationLine a1 = view.Allocations.Single(a => a.BillId == billA.Id);
        Assert.Equal(100m, a1.Amount);
        Assert.Equal(billA.Number, a1.BillNumber);
        BillAllocationLine a2 = view.Allocations.Single(a => a.BillId == billB.Id);
        Assert.Equal(30m, a2.Amount);
        Assert.Equal(billB.Number, a2.BillNumber);
    }

    [Fact]
    public async Task GET_fully_allocated_bill_payment_has_zero_unapplied()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("Wayne", null))).Content.ReadFromJsonAsync<Vendor>())!;
        Bill bill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "Rent");

        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor.Id, new DateOnly(2026, 3, 6), 100m, "check",
                    [new Allocation(bill.Id, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        BillPaymentView view = (await clerk.GetFromJsonAsync<BillPaymentView>(
            $"/clients/{clientId}/bill-payments/{payment.Id}"))!;

        Assert.Equal(0m, view.Unapplied);
        Assert.Equal(100m, Assert.Single(view.Allocations).Amount);
    }

    [Fact]
    public async Task GET_bill_payment_by_unknown_id_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/bill-payments/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~BillPaymentListEndpointTests"`
Expected: BUILD FAILURE — `BillPaymentView` / `BillAllocationLine` do not exist.

- [ ] **Step 3: Create `BillAllocationLine.cs`**

Create `Modules/Payables/Accounting101.Payables/BillAllocationLine.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>One bill an allocation-based AP document (a bill payment, and any future vendor-credit detail)
/// was applied to: the bill's id, its number (null if still a draft), and the amount applied to it.
/// Recovered from the document's GL entry lines (each allocation line carries a "Bill" dimension and the
/// allocated amount). The general Payables allocation-line type, mirroring Receivables' InvoiceAllocationLine.</summary>
public sealed record BillAllocationLine(Guid BillId, string? BillNumber, decimal Amount);
```

- [ ] **Step 4: Create `BillPaymentView.cs`**

Create `Modules/Payables/Accounting101.Payables/BillPaymentView.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>A vendor bill payment plus the bills it was applied to, the unapplied remainder held as vendor
/// credit, and the id of its posted journal entry — what the bill-payment detail endpoint returns.
/// Allocations are folded from the GL posting (Posted-only); Unapplied = Amount − Σallocations (the
/// overpayment held as credit); JournalEntryId lets the UI drill to the GL entry (null if none found).</summary>
public sealed record BillPaymentView(
    BillPayment Payment,
    IReadOnlyList<BillAllocationLine> Allocations,
    decimal Unapplied,
    Guid? JournalEntryId);
```

- [ ] **Step 5: Add `GetBillPaymentViewAsync` to `BillPaymentService`**

In `BillPaymentService.cs`, add this method (place it near `GetBillViewAsync`, ~line 94). It uses the primary-constructor params `payments` (`IBillPaymentStore`), `bills` (`IBillStore`), `ledger` (`ILedgerClient`) already in scope:
```csharp
    /// <summary>A single bill payment plus the bills it was applied to, its unapplied remainder (held as
    /// vendor credit), and its posted journal entry id — for the detail screen. Allocations and the journal
    /// id come from the Posted posting; Unapplied = Amount − Σallocations. Returns null if the payment does
    /// not exist.</summary>
    public async Task<BillPaymentView?> GetBillPaymentViewAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        BillPayment? payment = await payments.GetPaymentAsync(clientId, paymentId, ct);
        if (payment is null) return null;

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, paymentId, ct);
        EntryResponse? postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null });

        List<BillAllocationLine> allocations = [];
        if (postingEntry is not null)
        {
            foreach (IGrouping<Guid, EntryLineResponse> group in postingEntry.Lines
                         .Where(l => l.Dimensions.ContainsKey(BillPosting.BillDimension))
                         .GroupBy(l => l.Dimensions[BillPosting.BillDimension]))
            {
                Bill? bill = await bills.GetAsync(clientId, group.Key, ct);
                allocations.Add(new BillAllocationLine(group.Key, bill?.Number, group.Sum(l => l.Amount)));
            }
        }

        decimal unapplied = payment.Amount - allocations.Sum(a => a.Amount);
        return new BillPaymentView(payment, allocations, unapplied, postingEntry?.Id);
    }
```

- [ ] **Step 6: Add the `GetBillPayment` endpoint + route**

In `PayablesEndpoints.cs`, register the route immediately after the `ListBillPayments` map (`clients.MapGet("/bill-payments", ListBillPayments);`, line 26):
```csharp
        clients.MapGet("/bill-payments/{paymentId:guid}", GetBillPayment);
```
Add the handler next to `GetBill` (~after line 130), mirroring `GetBill` (which injects `BillPaymentService payments`):
```csharp
    private static async Task<IResult> GetBillPayment(
        Guid clientId, Guid paymentId, BillPaymentService payments, CancellationToken cancellationToken)
    {
        BillPaymentView? view = await payments.GetBillPaymentViewAsync(clientId, paymentId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~BillPaymentListEndpointTests"`
Expected: PASS (the three new tests + the three pre-existing).

- [ ] **Step 8: Commit**

Stage the explicit file list (guard against Rider `var` churn):
```bash
git add Modules/Payables/Accounting101.Payables/BillAllocationLine.cs Modules/Payables/Accounting101.Payables/BillPaymentView.cs Modules/Payables/Accounting101.Payables/BillPaymentService.cs Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs Modules/Payables/Accounting101.Payables.Tests/BillPaymentListEndpointTests.cs
git commit -m "feat(payables): GET /bill-payments/{id} returning payment + allocations + unapplied"
```

---

### Task 2: Backend — bill-payments list allocations fold (fixes BillPaymentList + BillDetail)

**Files:**
- Create: `Modules/Payables/Accounting101.Payables/BillPaymentWithAllocations.cs`
- Modify: `Modules/Payables/Accounting101.Payables/BillPaymentService.cs` (add `GetPaymentsWithAllocationsByVendorAsync`)
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs` (`ListBillPayments` returns the folded shape)
- Test (modify): `Modules/Payables/Accounting101.Payables.Tests/BillPaymentListEndpointTests.cs`

**Interfaces:**
- Consumes: `IBillPaymentStore.GetPaymentsByVendorAsync`, `ILedgerClient.GetEntriesBySourceRefAsync`, `BillPosting.BillDimension`, `Allocation`.
- Produces: `BillPaymentWithAllocations(Guid Id, Guid VendorId, DateOnly Date, decimal Amount, string? Method, bool Voided, IReadOnlyList<Allocation> Allocations)`; `BillPaymentService.GetPaymentsWithAllocationsByVendorAsync(Guid, Guid, CancellationToken) → IReadOnlyList<BillPaymentWithAllocations>`; `GET /clients/{id}/bill-payments?vendorId=` now returns `BillPaymentWithAllocations[]`.

**Background:** `GET /bill-payments?vendorId=` currently returns raw `BillPayment[]` with no allocations, but both `BillPaymentList` (Allocated/Unapplied columns) and `BillDetail` ("Applied payments" section) read `p.allocations` — wrong/crashing on real data. Fold allocations in. The FE already expects `allocations: {targetId, amount}[]`, so no FE change. `ListBillPayments` currently injects `IBillPaymentStore` directly — switch it to `BillPaymentService`.

- [ ] **Step 1: Write the failing test (+ update the prepayment test)**

In `BillPaymentListEndpointTests.cs`, add a new test that folds allocations, and update the existing prepayment test to deserialize the new shape. Both reference `BillPaymentWithAllocations` (RED = build failure).

New test (uses Task 1's helpers):
```csharp
    [Fact]
    public async Task List_folds_each_payments_allocations()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;
        Bill bill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "Rent");

        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor.Id, new DateOnly(2026, 3, 31), 80m, "check",
                    [new Allocation(bill.Id, 80m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        BillPaymentWithAllocations[] list = (await clerk.GetFromJsonAsync<BillPaymentWithAllocations[]>(
            $"/clients/{clientId}/bill-payments?vendorId={vendor.Id}"))!;

        Assert.Single(list);
        Assert.Equal(payment.Id, list[0].Id);
        Allocation alloc = Assert.Single(list[0].Allocations);
        Assert.Equal(bill.Id, alloc.TargetId);
        Assert.Equal(80m, alloc.Amount);
    }
```

Update the existing `Lists_a_vendors_recorded_payments` test: change the read-back type from `BillPayment[]` to `BillPaymentWithAllocations[]` and assert the prepayment folds to no allocations:
```csharp
        BillPaymentWithAllocations[] payments = (await clerk.GetFromJsonAsync<BillPaymentWithAllocations[]>(
            $"/clients/{clientId}/bill-payments?vendorId={vendor.Id}"))!;
        Assert.Single(payments);
        Assert.Equal(500m, payments[0].Amount);
        Assert.Equal(vendor.Id, payments[0].VendorId);
        Assert.Empty(payments[0].Allocations);   // a pure prepayment applies to no bill
```
(Leave `Requires_vendorId` and `Is_client_isolated` unchanged — the latter deserializes as `BillPayment[]` and still works since `BillPayment` is a field-subset of the folded shape.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~BillPaymentListEndpointTests"`
Expected: BUILD FAILURE — `BillPaymentWithAllocations` does not exist.

- [ ] **Step 3: Create `BillPaymentWithAllocations.cs`**

Create `Modules/Payables/Accounting101.Payables/BillPaymentWithAllocations.cs`:
```csharp
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>A bill payment as the Payments list reads it: the stored document fields plus the per-bill
/// allocations folded from its GL posting (Posted-only). The module stores no allocation array; this is the
/// read shape the list + bill-detail "applied payments" section consume (each Allocation is {bill id, amount}).</summary>
public sealed record BillPaymentWithAllocations(
    Guid Id, Guid VendorId, DateOnly Date, decimal Amount, string? Method, bool Voided,
    IReadOnlyList<Allocation> Allocations);
```

- [ ] **Step 4: Add `GetPaymentsWithAllocationsByVendorAsync` to `BillPaymentService`**

In `BillPaymentService.cs`, add just after `GetBillPaymentViewAsync` (from Task 1):
```csharp
    /// <summary>The vendor's bill payments each with its per-bill allocations folded from the GL (Posted-only)
    /// — what the Payments list and the bill-detail "applied payments" section consume. Voided payments are
    /// included (greyed in the UI); a not-yet-Posted payment folds to no allocations.</summary>
    public async Task<IReadOnlyList<BillPaymentWithAllocations>> GetPaymentsWithAllocationsByVendorAsync(
        Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        List<BillPaymentWithAllocations> result = [];
        foreach (BillPayment p in ps)
        {
            IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, p.Id, ct);
            EntryResponse? postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null });
            List<Allocation> allocs = [];
            if (postingEntry is not null)
                foreach (IGrouping<Guid, EntryLineResponse> group in postingEntry.Lines
                             .Where(l => l.Dimensions.ContainsKey(BillPosting.BillDimension))
                             .GroupBy(l => l.Dimensions[BillPosting.BillDimension]))
                    allocs.Add(new Allocation(group.Key, group.Sum(l => l.Amount)));
            result.Add(new BillPaymentWithAllocations(p.Id, p.VendorId, p.Date, p.Amount, p.Method, p.Voided, allocs));
        }
        return result;
    }
```

- [ ] **Step 5: Point `ListBillPayments` at the folded method**

In `PayablesEndpoints.cs`, change the `ListBillPayments` handler to inject `BillPaymentService` and return the folded shape:
```csharp
    private static async Task<IResult> ListBillPayments(
        Guid clientId, Guid? vendorId, BillPaymentService payments, CancellationToken cancellationToken)
    {
        if (vendorId is null || vendorId == Guid.Empty)
            return Results.Problem("vendorId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<BillPaymentWithAllocations> result = await payments.GetPaymentsWithAllocationsByVendorAsync(clientId, vendorId.Value, cancellationToken);
        return Results.Ok(result);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~BillPaymentListEndpointTests"`
Expected: PASS (new fold test + updated prepayment test + the 3 detail tests + the 2 unchanged).

- [ ] **Step 7: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/BillPaymentWithAllocations.cs Modules/Payables/Accounting101.Payables/BillPaymentService.cs Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs Modules/Payables/Accounting101.Payables.Tests/BillPaymentListEndpointTests.cs
git commit -m "fix(payables): fold per-payment allocations into the bill-payments list (fixes Bill Payments list + bill-detail applied-payments)"
```

---

### Task 3: Frontend — BillPaymentView interface + bill-payment-detail screen + route

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.ts` (add `BillAllocationLine` + `BillPaymentView`)
- Modify: `UI/Angular/src/app/core/payables/payables.service.ts` (add `getBillPayment`)
- Create: `UI/Angular/src/app/features/payables/bill-payment-detail.ts`
- Create: `UI/Angular/src/app/features/payables/bill-payment-detail.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add `payments/:id` route in the payables block)

**Interfaces:**
- Consumes: Task 1's `BillPaymentView` wire shape; `PayablesService`, `ClientContextService`, `BillPayment`, `CanDirective`.
- Produces: `BillAllocationLine` + `BillPaymentView` TS interfaces; `PayablesService.getBillPayment(id): Observable<BillPaymentView>`; `BillPaymentDetail` component; route `payments/:id` (payables).

- [ ] **Step 1: Write the failing component spec**

Create `UI/Angular/src/app/features/payables/bill-payment-detail.spec.ts`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillPaymentDetail } from './bill-payment-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot(id: string, caps: string[] = ['gl.read']) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(BillPaymentDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('BillPaymentDetail', () => {
  it('renders header, method, allocations with total, unapplied line, and journal link', () => {
    const { fixture, ctrl } = boot('p1');
    ctrl.expectOne('http://localhost:5000/clients/C1/bill-payments/p1').flush({
      payment: { id: 'p1', vendorId: 'v1', date: '2026-06-30', amount: 150, method: 'check', allocations: [], voided: false },
      allocations: [
        { billId: 'b1', billNumber: 'BILL-00001', amount: 100 },
        { billId: 'b2', billNumber: 'BILL-00002', amount: 30 },
      ],
      unapplied: 20, journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('check');
    expect(text).toContain('BILL-00001');
    expect(text).toContain('100.00');
    expect(text).toContain('BILL-00002');
    expect(text).toContain('30.00');
    expect(text).toContain('130.00');   // allocations total
    expect(text).toContain('20.00');    // unapplied
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('omits the journal link when journalEntryId is null', () => {
    const { fixture, ctrl } = boot('p2');
    ctrl.expectOne('http://localhost:5000/clients/C1/bill-payments/p2').flush({
      payment: { id: 'p2', vendorId: 'v1', date: '2026-06-30', amount: 25, method: null, allocations: [], voided: false },
      allocations: [], unapplied: 25, journalEntryId: null,
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });

  it('hides the journal link when the user lacks gl.read', () => {
    const { fixture, ctrl } = boot('p3', []);
    ctrl.expectOne('http://localhost:5000/clients/C1/bill-payments/p3').flush({
      payment: { id: 'p3', vendorId: 'v1', date: '2026-06-30', amount: 30, method: 'cash', allocations: [], voided: false },
      allocations: [{ billId: 'b1', billNumber: 'BILL-00001', amount: 30 }], unapplied: 0, journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run: `npx ng test --include='**/bill-payment-detail.spec.ts' --watch=false`
Expected: FAIL — cannot resolve `./bill-payment-detail`.

- [ ] **Step 3: Add the `BillAllocationLine` + `BillPaymentView` interfaces**

In `UI/Angular/src/app/core/payables/payables.ts`, add immediately after the `BillPayment` interface (ends line 38):
```ts
export interface BillAllocationLine { billId: string; billNumber: string | null; amount: number; }
export interface BillPaymentView { payment: BillPayment; allocations: BillAllocationLine[]; unapplied: number; journalEntryId: string | null; }
```

- [ ] **Step 4: Add the `getBillPayment` service method**

In `UI/Angular/src/app/core/payables/payables.service.ts`:
- Add `BillPaymentView` to the import from `'./payables'` (line 8 — append `, BillPaymentView`).
- Add the method next to `getBill` (line 71):
```ts
  getBillPayment(id: string): Observable<BillPaymentView> { return this.http.get<BillPaymentView>(this.base(`/bill-payments/${id}`)); }
```

- [ ] **Step 5: Create the `bill-payment-detail` component**

Create `UI/Angular/src/app/features/payables/bill-payment-detail.ts`:
```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { PayablesService } from '../../core/payables/payables.service';
import { BillAllocationLine, BillPaymentView } from '../../core/payables/payables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-bill-payment-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/payables/payments" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Payments</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Bill payment</h1>
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
                @for (a of v.allocations; track a.billId) {
                  <tr>
                    <td class="py-0.5">Bill {{ a.billNumber ?? '—' }}</td>
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
          <div class="text-sm mt-1"><span class="text-muted-foreground">Unapplied (held as vendor credit)</span> · <span class="tabular-nums">{{ money(v.unapplied) }}</span></div>
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
export class BillPaymentDetail {
  private readonly svc = inject(PayablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<BillPaymentView | null>(null);
  readonly loadError = signal<string | null>(null);

  constructor() {
    this.svc.getBillPayment(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.view.set(v),
      error: (e) => this.loadError.set(extractProblem(e).detail),
    });
  }

  sum(lines: BillAllocationLine[]): number { return lines.reduce((s, a) => s + a.amount, 0); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 6: Add the route**

In `UI/Angular/src/app/app.routes.ts`:
- Add the import next to the other payables feature imports (after line 34 `import { BillPaymentList } ...`):
```ts
import { BillPaymentDetail } from './features/payables/bill-payment-detail';
```
- Add the route after the payables `payments/new` entry (line 132), in the PAYABLES block, mirroring the receivables `payments/:id`:
```ts
    { path: 'payments/:id', component: BillPaymentDetail },
```

- [ ] **Step 7: Run the spec + compile gate**

Run: `npx ng test --include='**/bill-payment-detail.spec.ts' --watch=false` → all three specs PASS.
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.ts UI/Angular/src/app/core/payables/payables.service.ts UI/Angular/src/app/features/payables/bill-payment-detail.ts UI/Angular/src/app/features/payables/bill-payment-detail.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): bill-payment detail screen with allocations + unapplied + journal drill"
```

---

### Task 4: Frontend — BillPaymentList whole-row drill-in

**Files:**
- Modify: `UI/Angular/src/app/features/payables/bill-payment-list.ts`
- Modify (extend): `UI/Angular/src/app/features/payables/bill-payment-list.spec.ts`

**Interfaces:**
- Consumes: `Router`, the `payments/:id` payables route (Task 3).
- Produces: nothing.

**Note:** the list's Allocated/Unapplied columns become correct automatically via Task 2 (the backend now folds `allocations`); this task only adds row navigation. `BillPaymentList` has no in-row buttons and no memo cell, so there is nothing to insulate or truncate.

- [ ] **Step 1: Write the failing test**

Add `Router` to the router import at the top of `bill-payment-list.spec.ts`:
```ts
import { provideRouter, Router } from '@angular/router';
```
Add this spec inside `describe('BillPaymentList', ...)` (`vi` is available globally):
```ts
  it('navigates to the bill-payment detail when a row is clicked', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const f = TestBed.createComponent(BillPaymentList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    svc.setSelectedVendor('v1');
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments' && r.params.get('vendorId') === 'v1')
      .flush([{ id: 'p1', vendorId: 'v1', date: '2026-06-01', amount: 100, method: 'check', allocations: [{ targetId: 'b1', amount: 80 }], voided: false }]);
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).toHaveBeenCalledWith(['/payables/payments', 'p1']);
  });
```

- [ ] **Step 2: Run to verify it fails**

Run: `npx ng test --include='**/bill-payment-list.spec.ts' --watch=false`
Expected: the new spec FAILS (row not clickable → `navigate` not called). Pre-existing BillPaymentList specs still pass.

- [ ] **Step 3: Wire the drill-in in `bill-payment-list.ts`**

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

**3c.** Inject `Router` and add `open`. After `readonly vendorId = this.svc.selectedVendorId;` (line 70), add:
```ts
  private readonly router = inject(Router);
```
Add the method (e.g. after `allocated`, ~line 88):
```ts
  open(id: string): void { void this.router.navigate(['/payables/payments', id]); }
```

- [ ] **Step 4: Run the specs to verify they pass**

Run: `npx ng test --include='**/bill-payment-list.spec.ts' --watch=false`
Expected: all specs PASS (pre-existing + 1 new), output pristine.

- [ ] **Step 5: Compile gate**

Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/payables/bill-payment-list.ts UI/Angular/src/app/features/payables/bill-payment-list.spec.ts
git commit -m "feat(ui): bill-payment-list whole-row drill-in"
```

---

## Self-Review

**Spec coverage:**
- Backend `GET /bill-payments/{id}` returning `BillPaymentView{payment, allocations, unapplied, journalEntryId}` via `GetBillPaymentViewAsync` (Posted posting pick, allocations resolved to bill numbers, `Unapplied = Amount − Σallocations`) → Task 1. ✓
- General `BillAllocationLine` (target-named, own file) → Task 1. ✓
- Bill-payments-list allocations fold (fixes BillPaymentList + BillDetail; no FE consumer change; `ListBillPayments` switched to `BillPaymentService`) → Task 2. ✓
- `ap.read` gating automatic (no code). ✓
- bill-payment-detail screen (header + method + allocations table + total + unapplied line + `gl.read`-gated journal link) + `payments/:id` ungated payables route + `getBillPayment` service + `BillPaymentView` type → Task 3. ✓
- BillPaymentList whole-row drill-in (unconditional, same-area; no button/memo) → Task 4. ✓
- Tests: backend detail (allocations + bill numbers + unapplied + entry id; fully-allocated → 0; 404) + list fold assertion (Task 1/2); FE detail renders header/method/allocations/total/unapplied + journal link present/absent/gated (Task 3); FE list row-nav (Task 4). ✓

**Placeholder scan:** every step has complete code; no TBD.

**Type/name consistency:** `BillPaymentView{payment, allocations, unapplied, journalEntryId}` + `BillAllocationLine{billId, billNumber, amount}` identical backend record ↔ FE interface; `getBillPayment`/`GetBillPaymentViewAsync`/`GetBillPayment` names consistent; route `/payables/payments/:id` matches `open(id)` navigation and the spec's `navigate(['/payables/payments', 'p1'])`; `BillPaymentWithAllocations` serializes to the existing FE `BillPayment` shape (`{id, vendorId, date, amount, method, allocations:{targetId,amount}[], voided}`) so BillPaymentList + BillDetail consume it unchanged; `BillPosting.BillDimension` is the public const the folds reference.

## Execution Handoff

Two execution options:
1. **Subagent-Driven (recommended)** — fresh implementer per task, per-task review, final whole-branch review.
2. **Inline Execution** — execute in this session with checkpoints.
