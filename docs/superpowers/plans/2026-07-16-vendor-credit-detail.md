# Vendor-Credit-Application Detail Implementation Plan (Slice 2c-3a)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a vendor-credit-application detail screen reachable by whole-row drill-in from the Vendor Credits list, backed by a new `GET /clients/{id}/vendor-credit-applications/{id}` returning the application, its allocations (resolved to bill numbers), and its journal entry id — and fix the pre-existing vendor-credits-list bug (the list reads per-application allocations the backend never folds).

**Architecture:** Backend (Payables): a new `GetCreditApplicationAsync` by-id store getter (the gap) + `VendorCreditView` (reusing the shared `BillAllocationLine`) + `BillPaymentService.GetVendorCreditViewAsync` (allocations folded from the application's GL posting — Posted pick, `Bill`-dimensioned lines resolved to bill numbers; a credit application applies fully, so no unapplied); a `GET /vendor-credit-applications/{id}` endpoint; and a credit-applications-list allocations fold so `VendorCreditList` gets real data. Frontend: `VendorCreditView` interface + `getVendorCredit` + a `vendor-credit-detail` screen + `credits/:id` route (payables), and whole-row drill-in on `VendorCreditList`. A thinner mirror of Slice 2c-2 (AP bill-payment detail).

**Tech Stack:** .NET 10 minimal APIs + MongoDB (EphemeralMongo in tests); Angular 22 (standalone, OnPush, zoneless), Tailwind v4, Spartan Helm; xUnit (backend), Vitest runner + TestBed (frontend).

## Global Constraints

- **Backend:** namespaces follow folder structure (`Accounting101.Payables`). New detail endpoint returns `VendorCreditView` and follows the exact shape of `GetBill`: `return view is null ? Results.NotFound() : Results.Ok(view)`. The endpoint group already carries `.RequireAuthorization()`. **Rider auto-converts explicit types to `var` — stage explicit file lists and check for stray churn before each commit.**
- **Frontend:** standalone components, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. New detail route is ungated. The Vendor-Credits-list drill-in is same-area (a Vendor-Credits viewer already holds `ap.read`), so rows are unconditionally clickable. FE test runner is **Vitest** — `vi.spyOn` (global), not Jasmine; nav spies chain `.mockResolvedValue(true)`.
- **Wire shapes** identical backend↔frontend (host `JsonNamingPolicy.CamelCase`): `VendorCreditView{ credit: VendorCreditApplication, allocations: BillAllocationLine[], journalEntryId: string | null }`; `BillAllocationLine{ billId, billNumber: string|null, amount }` (already exists both sides from 2c-2). The credit-applications-list item serializes to the existing FE `VendorCreditApplication` shape `{ id, vendorId, date, allocations: {targetId, amount}[], voided }`.
- Posting pick is `{Status:"Active", Posting:"Posted", ReversalOf:null}` everywhere allocations/journal ids are read.
- The "View journal entry" link is `gl.read`-gated via `*appCan`.
- Only touch files named per task. Do NOT touch receivables, statement builders / customer-account / vendor-account (that is 2c-3b), bill-payment-* (2c-2, done), or other modules.
- Backend test run (focused): `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorCreditApplicationListEndpointTests"`. FE unit test: `npx ng test --include='<glob>' --watch=false` from `UI/Angular`. FE compile gate: `npx ng build --configuration development`.
- `environment.ts` stays modified/uncommitted (local dev config, never commit).
- Branch `feat/vendor-credit-detail`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: Backend — by-id getter + VendorCreditView + GET /vendor-credit-applications/{id}

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/PayablesPorts.cs` (add `GetCreditApplicationAsync` to `IBillPaymentStore`)
- Modify: `Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs` (implement it)
- Create: `Modules/Payables/Accounting101.Payables/VendorCreditView.cs`
- Modify: `Modules/Payables/Accounting101.Payables/BillPaymentService.cs` (add `GetVendorCreditViewAsync`)
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs` (add `GetVendorCredit` handler + route)
- Test (extend): `Modules/Payables/Accounting101.Payables.Tests/VendorCreditApplicationListEndpointTests.cs`

**Interfaces:**
- Consumes: `IBillPaymentStore.GetCreditApplicationsByVendorAsync` (existing), `IBillStore.GetAsync`, `ILedgerClient.GetEntriesBySourceRefAsync`, `BillPosting.BillDimension`, `BillAllocationLine` (from 2c-2), `EntryResponse`/`EntryLineResponse`, `VendorCreditApplication`, `Bill`.
- Produces: `IBillPaymentStore.GetCreditApplicationAsync(Guid, Guid, CancellationToken) → VendorCreditApplication?`; `VendorCreditView(VendorCreditApplication Credit, IReadOnlyList<BillAllocationLine> Allocations, Guid? JournalEntryId)`; `BillPaymentService.GetVendorCreditViewAsync(Guid, Guid, CancellationToken) → VendorCreditView?`; route `GET /clients/{clientId}/vendor-credit-applications/{creditApplicationId:guid}`.

- [ ] **Step 1: Add the test helpers + write the failing tests**

`VendorCreditApplicationListEndpointTests.cs` needs the multi-step setup (chart, enter bill, overpay to create vendor credit, apply credit). Add these helpers (copied from the sibling `VendorCreditApplicationDimensionTests.cs`) INSIDE the test class, then the two detail tests. Ensure `using System.Net;` is present (add if missing).

Helpers to add:
```csharp
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId, "2000", "Accounts Payable", "Liability", null, ["Vendor", "Bill"]);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId, "1300", "Vendor Credits", "Asset", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId, "5200", "Rent Expense", "Expense", null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension, string[]? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest
            {
                Number = number, Name = name, Type = type,
                RequiredDimension = requiredDimension, RequiredDimensions = requiredDimensions,
            }))
            .EnsureSuccessStatusCode();

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

    /// <summary>Over-pays firstBill to leave vendor credit, then applies `applyAmount` of it to targetBill.
    /// Returns the approved credit application.</summary>
    private async Task<VendorCreditApplication> ApplyVendorCreditAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid vendorId, Guid firstBillId, Guid targetBillId, decimal applyAmount)
    {
        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendorId, new DateOnly(2026, 3, 31), 100m, "check",
                    [new Allocation(firstBillId, 40m)])))   // pay 100, allocate 40 → 60 vendor credit
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        VendorCreditApplication creditApp = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
                new VendorCreditApplicationRequest(vendorId, new DateOnly(2026, 4, 15), [new Allocation(targetBillId, applyAmount)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<VendorCreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditApp.Id);
        return creditApp;
    }
```

Detail tests to add (reference the new `VendorCreditView` — the RED is a build failure):
```csharp
    [Fact]
    public async Task GET_vendor_credit_by_id_returns_allocations_and_journal_entry_id()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        Bill firstBill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "March Rent");   // overpaid → credit
        Bill targetBill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "April Rent");  // credit applied here
        VendorCreditApplication creditApp = await ApplyVendorCreditAsync(clerk, approver, clientId, vendor.Id, firstBill.Id, targetBill.Id, 60m);

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={creditApp.Id}"))!;
        Guid expectedEntryId = entries.Single(e => e is { Status: "Active", ReversalOf: null }).Id;

        VendorCreditView view = (await clerk.GetFromJsonAsync<VendorCreditView>(
            $"/clients/{clientId}/vendor-credit-applications/{creditApp.Id}"))!;

        Assert.Equal(creditApp.Id, view.Credit.Id);
        Assert.False(view.Credit.Voided);
        Assert.Equal(expectedEntryId, view.JournalEntryId);
        BillAllocationLine alloc = Assert.Single(view.Allocations);
        Assert.Equal(targetBill.Id, alloc.BillId);
        Assert.Equal(60m, alloc.Amount);
        Assert.Equal(targetBill.Number, alloc.BillNumber);
    }

    [Fact]
    public async Task GET_vendor_credit_by_unknown_id_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/vendor-credit-applications/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorCreditApplicationListEndpointTests"`
Expected: BUILD FAILURE — `VendorCreditView` does not exist.

- [ ] **Step 3: Add the `GetCreditApplicationAsync` by-id getter**

In `PayablesPorts.cs`, add to `IBillPaymentStore` immediately after `GetCreditApplicationsByVendorAsync` (line 26):
```csharp
    Task<VendorCreditApplication?> GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default);
```

In `DocumentBillPaymentStore.cs`, add next to `GetCreditApplicationsByVendorAsync` (~line 47), mirroring it (the collection const `VendorCreditApplications` and mapper `MapCredit` already exist in this file):
```csharp
    public async Task<VendorCreditApplication?> GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default)
    {
        DocumentResult<VendorCreditApplicationBody>? r = await documents.GetAsync<VendorCreditApplicationBody>(clientId, VendorCreditApplications, creditApplicationId, ct);
        return r is null ? null : MapCredit(r);
    }
```

- [ ] **Step 4: Create `VendorCreditView.cs`**

Create `Modules/Payables/Accounting101.Payables/VendorCreditView.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>A vendor credit application plus the bills it was applied to and the id of its posted journal
/// entry — what the vendor-credit detail endpoint returns. Allocations are folded from the GL posting
/// (Posted-only) and reuse the shared BillAllocationLine; a credit application applies existing credit fully,
/// so the allocations' total IS the amount (no unapplied remainder). JournalEntryId drills to the GL entry.</summary>
public sealed record VendorCreditView(
    VendorCreditApplication Credit,
    IReadOnlyList<BillAllocationLine> Allocations,
    Guid? JournalEntryId);
```

- [ ] **Step 5: Add `GetVendorCreditViewAsync` to `BillPaymentService`**

In `BillPaymentService.cs`, add near `GetBillPaymentViewAsync` (~line 106). Uses the primary-ctor `payments`/`bills`/`ledger`:
```csharp
    /// <summary>A single vendor credit application plus the bills it was applied to and its posted journal
    /// entry id — for the detail screen. Allocations and the journal id come from the Posted posting; a
    /// credit application applies fully, so the allocations' total is the amount. Returns null if not found.</summary>
    public async Task<VendorCreditView?> GetVendorCreditViewAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default)
    {
        VendorCreditApplication? credit = await payments.GetCreditApplicationAsync(clientId, creditApplicationId, ct);
        if (credit is null) return null;

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, creditApplicationId, ct);
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

        return new VendorCreditView(credit, allocations, postingEntry?.Id);
    }
```

- [ ] **Step 6: Add the `GetVendorCredit` endpoint + route**

In `PayablesEndpoints.cs`, register the route immediately after the `ListCreditApplications` map (`clients.MapGet("/vendor-credit-applications", ListCreditApplications);`, line 30):
```csharp
        clients.MapGet("/vendor-credit-applications/{creditApplicationId:guid}", GetVendorCredit);
```
Add the handler next to `ListCreditApplications` (~after line 234), mirroring `GetBill` (injects `BillPaymentService payments`):
```csharp
    private static async Task<IResult> GetVendorCredit(
        Guid clientId, Guid creditApplicationId, BillPaymentService payments, CancellationToken cancellationToken)
    {
        VendorCreditView? view = await payments.GetVendorCreditViewAsync(clientId, creditApplicationId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorCreditApplicationListEndpointTests"`
Expected: PASS (the two new tests + the pre-existing list tests).

- [ ] **Step 8: Commit**

Stage the explicit file list (guard against Rider `var` churn):
```bash
git add Modules/Payables/Accounting101.Payables/PayablesPorts.cs Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs Modules/Payables/Accounting101.Payables/VendorCreditView.cs Modules/Payables/Accounting101.Payables/BillPaymentService.cs Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs Modules/Payables/Accounting101.Payables.Tests/VendorCreditApplicationListEndpointTests.cs
git commit -m "feat(payables): GET /vendor-credit-applications/{id} returning application + allocations"
```

**Note:** adding `GetCreditApplicationAsync` to `IBillPaymentStore` requires every implementer to add it. If the test project has a fake/in-memory `IBillPaymentStore`, add the one-line member there too (mirroring its existing `GetCreditApplicationsByVendorAsync`) and include that file in the commit — the solution must compile.

---

### Task 2: Backend — vendor-credit-applications list allocations fold (fixes VendorCreditList)

**Files:**
- Create: `Modules/Payables/Accounting101.Payables/VendorCreditApplicationWithAllocations.cs`
- Modify: `Modules/Payables/Accounting101.Payables/BillPaymentService.cs` (add `GetCreditApplicationsWithAllocationsByVendorAsync`)
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs` (`ListCreditApplications` returns the folded shape)
- Test (modify): `Modules/Payables/Accounting101.Payables.Tests/VendorCreditApplicationListEndpointTests.cs`

**Interfaces:**
- Consumes: `IBillPaymentStore.GetCreditApplicationsByVendorAsync`, `ILedgerClient.GetEntriesBySourceRefAsync`, `BillPosting.BillDimension`, `Allocation`.
- Produces: `VendorCreditApplicationWithAllocations(Guid Id, Guid VendorId, DateOnly Date, bool Voided, IReadOnlyList<Allocation> Allocations)`; `BillPaymentService.GetCreditApplicationsWithAllocationsByVendorAsync(Guid, Guid, CancellationToken) → IReadOnlyList<VendorCreditApplicationWithAllocations>`; `GET /clients/{id}/vendor-credit-applications?vendorId=` now returns `VendorCreditApplicationWithAllocations[]`.

**Background:** `GET /vendor-credit-applications?vendorId=` currently returns raw `VendorCreditApplication[]` with no allocations, but `VendorCreditList` reads `c.allocations` (`{{ c.allocations.length }}` + `applied(c)` sum) — wrong/crashing on real data. Fold allocations in. The FE already expects `allocations: {targetId, amount}[]`, so no FE change. `ListCreditApplications` currently injects `IBillPaymentStore` directly — switch it to `BillPaymentService`.

- [ ] **Step 1: Write the failing test (+ update the existing list test)**

In `VendorCreditApplicationListEndpointTests.cs`, add a new fold test and update the existing list test to deserialize the new shape. Both reference `VendorCreditApplicationWithAllocations` (RED = build failure).

New test (uses Task 1's helpers):
```csharp
    [Fact]
    public async Task List_folds_each_applications_allocations()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        Bill firstBill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "March Rent");
        Bill targetBill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "April Rent");
        VendorCreditApplication creditApp = await ApplyVendorCreditAsync(clerk, approver, clientId, vendor.Id, firstBill.Id, targetBill.Id, 60m);

        VendorCreditApplicationWithAllocations[] list = (await clerk.GetFromJsonAsync<VendorCreditApplicationWithAllocations[]>(
            $"/clients/{clientId}/vendor-credit-applications?vendorId={vendor.Id}"))!;

        VendorCreditApplicationWithAllocations row = Assert.Single(list, a => a.Id == creditApp.Id);
        Allocation alloc = Assert.Single(row.Allocations);
        Assert.Equal(targetBill.Id, alloc.TargetId);
        Assert.Equal(60m, alloc.Amount);
    }
```

If `VendorCreditApplicationListEndpointTests.cs` already has an existing list test that deserializes `VendorCreditApplication[]` (e.g. asserting a bare listing), update its read-back type to `VendorCreditApplicationWithAllocations[]` (the extra `allocations` field is additive; a `VendorCreditApplication`-typed deserialize also still works since `VendorCreditApplication` is a field-subset). Leave any `Requires_vendorId` / client-isolation tests unchanged.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorCreditApplicationListEndpointTests"`
Expected: BUILD FAILURE — `VendorCreditApplicationWithAllocations` does not exist.

- [ ] **Step 3: Create `VendorCreditApplicationWithAllocations.cs`**

Create `Modules/Payables/Accounting101.Payables/VendorCreditApplicationWithAllocations.cs`:
```csharp
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>A vendor credit application as the Credits list reads it: the stored document fields plus the
/// per-bill allocations folded from its GL posting (Posted-only). The module stores no allocation array;
/// this is the read shape the list consumes (each Allocation is {bill id, amount}).</summary>
public sealed record VendorCreditApplicationWithAllocations(
    Guid Id, Guid VendorId, DateOnly Date, bool Voided,
    IReadOnlyList<Allocation> Allocations);
```

- [ ] **Step 4: Add `GetCreditApplicationsWithAllocationsByVendorAsync` to `BillPaymentService`**

In `BillPaymentService.cs`, add just after `GetVendorCreditViewAsync` (from Task 1):
```csharp
    /// <summary>The vendor's credit applications each with its per-bill allocations folded from the GL
    /// (Posted-only) — what the Credits list consumes. Voided applications are included (greyed in the UI);
    /// a not-yet-Posted application folds to no allocations.</summary>
    public async Task<IReadOnlyList<VendorCreditApplicationWithAllocations>> GetCreditApplicationsWithAllocationsByVendorAsync(
        Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<VendorCreditApplication> apps = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);
        List<VendorCreditApplicationWithAllocations> result = [];
        foreach (VendorCreditApplication c in apps)
        {
            IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, c.Id, ct);
            EntryResponse? postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null });
            List<Allocation> allocs = [];
            if (postingEntry is not null)
                foreach (IGrouping<Guid, EntryLineResponse> group in postingEntry.Lines
                             .Where(l => l.Dimensions.ContainsKey(BillPosting.BillDimension))
                             .GroupBy(l => l.Dimensions[BillPosting.BillDimension]))
                    allocs.Add(new Allocation(group.Key, group.Sum(l => l.Amount)));
            result.Add(new VendorCreditApplicationWithAllocations(c.Id, c.VendorId, c.Date, c.Voided, allocs));
        }
        return result;
    }
```

- [ ] **Step 5: Point `ListCreditApplications` at the folded method**

In `PayablesEndpoints.cs`, change the `ListCreditApplications` handler to inject `BillPaymentService` and return the folded shape:
```csharp
    private static async Task<IResult> ListCreditApplications(
        Guid clientId, Guid? vendorId, BillPaymentService payments, CancellationToken cancellationToken)
    {
        if (vendorId is null || vendorId == Guid.Empty)
            return Results.Problem("vendorId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);
        IReadOnlyList<VendorCreditApplicationWithAllocations> result = await payments.GetCreditApplicationsWithAllocationsByVendorAsync(clientId, vendorId.Value, cancellationToken);
        return Results.Ok(result);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorCreditApplicationListEndpointTests"`
Expected: PASS (new fold test + updated list test + Task 1's detail tests + any unchanged 400/isolation tests).

- [ ] **Step 7: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/VendorCreditApplicationWithAllocations.cs Modules/Payables/Accounting101.Payables/BillPaymentService.cs Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs Modules/Payables/Accounting101.Payables.Tests/VendorCreditApplicationListEndpointTests.cs
git commit -m "fix(payables): fold per-application allocations into the vendor-credits list (fixes Vendor Credits list)"
```

---

### Task 3: Frontend — VendorCreditView interface + vendor-credit-detail screen + route

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.ts` (add `VendorCreditView`)
- Modify: `UI/Angular/src/app/core/payables/payables.service.ts` (add `getVendorCredit`)
- Create: `UI/Angular/src/app/features/payables/vendor-credit-detail.ts`
- Create: `UI/Angular/src/app/features/payables/vendor-credit-detail.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add `credits/:id` route in the payables block)

**Interfaces:**
- Consumes: Task 1's `VendorCreditView` wire shape; `PayablesService`, `ClientContextService`, `VendorCreditApplication`, `BillAllocationLine` (already FE-side), `CanDirective`.
- Produces: `VendorCreditView` TS interface; `PayablesService.getVendorCredit(id): Observable<VendorCreditView>`; `VendorCreditDetail` component; route `credits/:id` (payables).

- [ ] **Step 1: Write the failing component spec**

Create `UI/Angular/src/app/features/payables/vendor-credit-detail.spec.ts`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorCreditDetail } from './vendor-credit-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot(id: string, caps: string[] = ['gl.read']) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(VendorCreditDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('VendorCreditDetail', () => {
  it('renders header, allocations with total, and journal link', () => {
    const { fixture, ctrl } = boot('ca1');
    ctrl.expectOne('http://localhost:5000/clients/C1/vendor-credit-applications/ca1').flush({
      credit: { id: 'ca1', vendorId: 'v1', date: '2026-06-30', allocations: [], voided: false },
      allocations: [
        { billId: 'b1', billNumber: 'BILL-00001', amount: 60 },
        { billId: 'b2', billNumber: 'BILL-00002', amount: 40 },
      ],
      journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Credit applied');
    expect(text).toContain('BILL-00001');
    expect(text).toContain('60.00');
    expect(text).toContain('BILL-00002');
    expect(text).toContain('40.00');
    expect(text).toContain('100.00');   // allocations total = the amount
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('omits the journal link when journalEntryId is null', () => {
    const { fixture, ctrl } = boot('ca2');
    ctrl.expectOne('http://localhost:5000/clients/C1/vendor-credit-applications/ca2').flush({
      credit: { id: 'ca2', vendorId: 'v1', date: '2026-06-30', allocations: [], voided: false },
      allocations: [{ billId: 'b1', billNumber: 'BILL-00001', amount: 25 }], journalEntryId: null,
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });

  it('hides the journal link when the user lacks gl.read', () => {
    const { fixture, ctrl } = boot('ca3', []);
    ctrl.expectOne('http://localhost:5000/clients/C1/vendor-credit-applications/ca3').flush({
      credit: { id: 'ca3', vendorId: 'v1', date: '2026-06-30', allocations: [], voided: false },
      allocations: [{ billId: 'b1', billNumber: 'BILL-00001', amount: 30 }], journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run: `npx ng test --include='**/vendor-credit-detail.spec.ts' --watch=false`
Expected: FAIL — cannot resolve `./vendor-credit-detail`.

- [ ] **Step 3: Add the `VendorCreditView` interface**

In `UI/Angular/src/app/core/payables/payables.ts`, add immediately after the `VendorCreditApplication` interface (ends line 66):
```ts
export interface VendorCreditView { credit: VendorCreditApplication; allocations: BillAllocationLine[]; journalEntryId: string | null; }
```
(`BillAllocationLine` is already exported in this file from 2c-2. If the import/reference isn't in scope, it is a same-file interface — no import needed.)

- [ ] **Step 4: Add the `getVendorCredit` service method**

In `UI/Angular/src/app/core/payables/payables.service.ts`:
- Add `VendorCreditView` to the import from `'./payables'` (line 8 — append `, VendorCreditView`).
- Add the method next to `getBillPayment` (line 76):
```ts
  getVendorCredit(id: string): Observable<VendorCreditView> { return this.http.get<VendorCreditView>(this.base(`/vendor-credit-applications/${id}`)); }
```

- [ ] **Step 5: Create the `vendor-credit-detail` component**

Create `UI/Angular/src/app/features/payables/vendor-credit-detail.ts`:
```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { PayablesService } from '../../core/payables/payables.service';
import { BillAllocationLine, VendorCreditView } from '../../core/payables/payables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-vendor-credit-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/payables/credits" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Credits</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Credit applied</h1>
          <span class="text-sm px-2 py-0.5 rounded border border-border">{{ v.credit.voided ? 'Voided' : 'Active' }}</span>
        </div>
        <div class="text-sm flex flex-col gap-1">
          <div><span class="text-muted-foreground">Date</span> · {{ formatDate(v.credit.date) }}</div>
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
export class VendorCreditDetail {
  private readonly svc = inject(PayablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<VendorCreditView | null>(null);
  readonly loadError = signal<string | null>(null);

  constructor() {
    this.svc.getVendorCredit(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
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
- Add the import next to the other payables feature imports (after line 37 `import { VendorCreditList } ...`):
```ts
import { VendorCreditDetail } from './features/payables/vendor-credit-detail';
```
- Add the route after the payables `credits/new` entry (line 138), in the PAYABLES block:
```ts
    { path: 'credits/:id', component: VendorCreditDetail },
```

- [ ] **Step 7: Run the spec + compile gate**

Run: `npx ng test --include='**/vendor-credit-detail.spec.ts' --watch=false` → all three specs PASS.
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.ts UI/Angular/src/app/core/payables/payables.service.ts UI/Angular/src/app/features/payables/vendor-credit-detail.ts UI/Angular/src/app/features/payables/vendor-credit-detail.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): vendor-credit detail screen with allocations + journal drill"
```

---

### Task 4: Frontend — VendorCreditList whole-row drill-in

**Files:**
- Modify: `UI/Angular/src/app/features/payables/vendor-credit-list.ts`
- Modify (extend): `UI/Angular/src/app/features/payables/vendor-credit-list.spec.ts`

**Interfaces:**
- Consumes: `Router`, the `credits/:id` payables route (Task 3).
- Produces: nothing.

**Note:** the list's Applied/Bills columns become correct automatically via Task 2 (the backend now folds `allocations`); this task only adds row navigation. `VendorCreditList` has no in-row buttons and no memo cell, so there is nothing to insulate or truncate.

- [ ] **Step 1: Write the failing test**

Add `Router` to the router import at the top of `vendor-credit-list.spec.ts`:
```ts
import { provideRouter, Router } from '@angular/router';
```
Add this spec inside `describe('VendorCreditList', ...)` (`vi` is available globally):
```ts
  it('navigates to the vendor-credit detail when a row is clicked', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const f = TestBed.createComponent(VendorCreditList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    svc.setSelectedVendor('v1');
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/credit-balance').flush({ vendorId: 'v1', creditBalance: 50 });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/vendor-credit-applications' && r.params.get('vendorId') === 'v1')
      .flush([{ id: 'ca1', vendorId: 'v1', date: '2026-04-02', allocations: [{ targetId: 'b2', amount: 40 }], voided: false }]);
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).toHaveBeenCalledWith(['/payables/credits', 'ca1']);
  });
```

- [ ] **Step 2: Run to verify it fails**

Run: `npx ng test --include='**/vendor-credit-list.spec.ts' --watch=false`
Expected: the new spec FAILS (row not clickable → `navigate` not called). Pre-existing VendorCreditList specs still pass.

- [ ] **Step 3: Wire the drill-in in `vendor-credit-list.ts`**

**3a.** Change line 2 from `import { RouterLink } from '@angular/router';` to:
```ts
import { Router, RouterLink } from '@angular/router';
```

**3b.** Replace the row `<tr>` opening tag (line 51). Change:
```html
                  <tr hlmTr [class.opacity-50]="c.voided">
```
to:
```html
                  <tr hlmTr role="button" tabindex="0"
                      class="cursor-pointer hover:bg-muted/50"
                      [class.opacity-50]="c.voided"
                      (click)="open(c.id)"
                      (keydown.enter)="open(c.id)">
```

**3c.** Inject `Router` and add `open`. After `readonly vendorId = this.svc.selectedVendorId;` (line 68), add:
```ts
  private readonly router = inject(Router);
```
Add the method (e.g. after `applied`, ~line 93):
```ts
  open(id: string): void { void this.router.navigate(['/payables/credits', id]); }
```

- [ ] **Step 4: Run the specs to verify they pass**

Run: `npx ng test --include='**/vendor-credit-list.spec.ts' --watch=false`
Expected: all specs PASS (pre-existing + 1 new), output pristine.

- [ ] **Step 5: Compile gate**

Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/payables/vendor-credit-list.ts UI/Angular/src/app/features/payables/vendor-credit-list.spec.ts
git commit -m "feat(ui): vendor-credit-list whole-row drill-in"
```

---

## Self-Review

**Spec coverage:**
- Backend `GET /vendor-credit-applications/{id}` returning `VendorCreditView{credit, allocations, journalEntryId}` via `GetVendorCreditViewAsync` (Posted pick, allocations resolved to bill numbers, no unapplied) + new `GetCreditApplicationAsync` by-id getter → Task 1. ✓
- Reuses shared `BillAllocationLine` (no new type) → Task 1. ✓
- Vendor-credit-applications list allocations fold (fixes VendorCreditList; no FE consumer change; `ListCreditApplications` switched to `BillPaymentService`) → Task 2. ✓
- `ap.read` gating automatic (no code). ✓
- vendor-credit-detail screen (header + allocations table + total + `gl.read`-gated journal link; no method/unapplied) + `credits/:id` ungated payables route + `getVendorCredit` service + `VendorCreditView` type → Task 3. ✓
- VendorCreditList whole-row drill-in (unconditional, same-area; no button/memo) → Task 4. ✓
- Tests: backend detail (allocations + bill number + entry id; 404) + list fold assertion (Task 1/2); FE detail renders header/allocations/total + journal link present/absent/gated (Task 3); FE list row-nav (Task 4). ✓

**Placeholder scan:** every step has complete code; no TBD.

**Type/name consistency:** `VendorCreditView{credit, allocations, journalEntryId}` + `BillAllocationLine{billId, billNumber, amount}` identical backend record ↔ FE interface; `getVendorCredit`/`GetVendorCreditViewAsync`/`GetVendorCredit` names consistent; route `/payables/credits/:id` matches `open(id)` navigation and the spec's `navigate(['/payables/credits', 'ca1'])`; `VendorCreditApplicationWithAllocations` serializes to the existing FE `VendorCreditApplication` shape (`{id, vendorId, date, allocations:{targetId,amount}[], voided}`) so VendorCreditList consumes it unchanged; `BillPosting.BillDimension` is the public const the folds reference; `GetCreditApplicationAsync` defined in Task 1 (port + impl + fake) and consumed by both view methods.

## Execution Handoff

Two execution options:
1. **Subagent-Driven (recommended)** — fresh implementer per task, per-task review, final whole-branch review.
2. **Inline Execution** — execute in this session with checkpoints.
