# Duplicate Handling Policy — Bill Duplicate Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Write down the duplicate-handling principle, then give Payables a server-side bill duplicate lookup that sees drafts, and switch the e-invoicing import screen onto it.

**Architecture:** The module that owns the documents answers the duplicate question. `BillService` gains a read that filters the vendor's bills — drafts and entered, minus voided — by trimmed case-insensitive vendor reference. A thin `GET /bills/duplicates` exposes it. The Angular screen drops its bounded client-side scan and calls that endpoint instead, deleting the `findDuplicates` helper it no longer needs.

**Tech Stack:** .NET 10 minimal APIs, xUnit; Angular 22 signals, Vitest 4 + jsdom.

**Spec:** `docs/superpowers/specs/2026-07-25-duplicate-handling-policy-design.md`

## Global Constraints

- Repo `C:\Users\jorda\RiderProjects\Accounting101`, branch `feat/duplicate-handling-policy` (already checked out). Do not create or switch branches.
- Build with **0 warnings**. TDD: failing test → run it → implement → run it → commit.
- **Stage explicit file lists in `git add`** — never `git add -A` or `git add .`. `UI/Angular/src/app/core/api/environment.ts` carries an unrelated long-lived local edit that MUST stay uncommitted; verify with `git status --short` after each commit that it is still listed as modified.
- No new npm or NuGet dependency.
- Payables gains no reference to another module. The e-invoicing module gains no reference to anything — the Angular screen calls Payables directly, exactly as it already does to draft the bill.
- .NET tests: `dotnet test Modules/Payables/Accounting101.Payables.Tests` from the repo root.
- Angular: run from `UI/Angular`. Single spec `npx ng test --include=<path> --watch=false`; full suite `npm test -- --watch=false`.

## Governing standard

**Don't hand-hold more than necessary.** A duplicate check earns its place only where duplication moves money. This plan adds detection for vendor bills and nothing else. Do not add uniqueness checks, warnings, or validation to any other surface, however tempting the symmetry.

## Why this exists

The e-invoicing import screen warns when a vendor already has a bill with the same reference. It never fires for a bill that is still a **draft**, because it scans `GET /bills`, and `BillPaymentService.ListBillViewsAsync:224` filters to `BillStatus.Entered`. Importing the same invoice twice before entering the first — the likeliest real sequence — produces no warning. The right read already exists: `IBillStore.GetByVendorAsync` concatenates drafts and entered (`DocumentBillStore.cs:68-73`). The screen was pointed at the wrong source.

---

### Task 1: The principle

**Files:**
- Modify: `docs/design-principles.md`

**Interfaces:**
- Consumes: nothing.
- Produces: the written rule every later task implements. No code.

This is documentation, so it has no test cycle. It is its own task because it is the thing the rest derives from and a reviewer should be able to accept or reject the wording on its own.

- [ ] **Step 1: Read the surrounding document**

Read `docs/design-principles.md`. Note its numbered top-level sections (`## 1. The thesis: the journal is the only truth`, `## 2. Corrections are events, not edits`, …) and match that voice: declarative, reasoned, no hedging.

- [ ] **Step 2: Insert the new section**

Insert the following **immediately after the section on corrections (`## 2. Corrections are events, not edits`) and before the section that currently follows it**, renumbering the subsequent sections so numbering stays contiguous:

```markdown
## 3. Duplicate submissions are two different questions

Two different questions live here, and conflating them is why our answers diverged. They get
different mechanisms and neither borrows the other's.

**A retry is invisible.** The same operation delivered twice — a network retry, a double-click, a
redelivery — must produce one result. Where a repeat moves money, the write accepts a caller-supplied
id: same id and same content replays the existing document; same id and different content is a 422.
With no key there is no protection, and that is the caller's choice, not a defect.

**A business duplicate is a human decision.** A genuinely new submission that *resembles* something
already recorded — the same invoice number from the same vendor — is never auto-resolved. Re-issues,
corrections and annual repeats are all legitimate. The system reports what it found, links to it, and
lets the operator decide. It does not block.

**The module that owns the documents answers the question.** Duplicate lookup is a server-side query
on the owning module, never a scan assembled by a caller from a list endpoint. A caller cannot know
what a list is not showing it, and a bounded scan that reports "none found" is a lie of omission. The
lookup states its own completeness.

**We do not police untidiness.** Duplicate vendor records, duplicate customers, a customer invoiced
twice — the cost is embarrassment, not money. Detecting those is hand-holding and we don't do it.
```

- [ ] **Step 3: Verify numbering is contiguous**

Run: `grep -n '^## ' docs/design-principles.md`
Expected: section numbers run 1, 2, 3, 4, … with no gaps and no repeats.

- [ ] **Step 4: Commit**

```bash
git add docs/design-principles.md
git commit -m "docs(principles): duplicate submissions — retry vs business duplicate"
git status --short
```

Confirm `UI/Angular/src/app/core/api/environment.ts` is still listed as modified.

---

### Task 2: BillService.FindDuplicatesAsync

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/BillService.cs`
- Test: `Modules/Payables/Accounting101.Payables.Tests/BillDuplicateTests.cs` (create)

**Interfaces:**
- Consumes: `IBillStore.GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct)` returning `Task<IReadOnlyList<Bill>>`, which already concatenates drafts and entered. `Bill` has `Id`, `VendorId`, `Number`, `BillDate`, `DueDate`, `VendorReference`, `Memo`, `Status`, `Lines`. `BillStatus` is `Draft | Entered | Void`.
- Produces: `BillService.FindDuplicatesAsync(Guid clientId, Guid vendorId, string reference, CancellationToken ct = default)` returning `Task<IReadOnlyList<Bill>>`. Task 3 calls it.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Payables/Accounting101.Payables.Tests/BillDuplicateTests.cs`:

```csharp
namespace Accounting101.Payables.Tests;

/// <summary>The duplicate lookup behind the e-invoicing import warning. The case that matters most is
/// the draft: the screen's previous source (the bill list) filters to Entered, so importing the same
/// invoice twice before entering the first produced no warning at all.</summary>
public sealed class BillDuplicateTests
{
    private static BillBody Body(Guid vendorId, string? reference) =>
        new(vendorId, new DateOnly(2026, 3, 1), null, reference, null,
            [new BillLineBody("Rent", 100m, Guid.NewGuid())]);

    [Fact]
    public async Task A_draft_with_a_matching_reference_is_returned()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-9"));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Equal(draft.Id, Assert.Single(hits).Id);
    }

    [Fact]
    public async Task An_entered_bill_with_a_matching_reference_is_returned()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-9"));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Equal(entered.Id, Assert.Single(hits).Id);
    }

    [Fact]
    public async Task A_voided_bill_is_not_a_duplicate()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-9"));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);
        await h.Bills.VoidAsync(clientId, entered.Id, "duplicate");

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Matching_ignores_case_and_surrounding_whitespace_on_both_sides()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        await h.Bills.DraftAsync(clientId, Body(vendorId, "  inv-9 "));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, " INV-9  ");

        Assert.Single(hits);
    }

    [Fact]
    public async Task A_bill_with_no_vendor_reference_never_matches()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        await h.Bills.DraftAsync(clientId, Body(vendorId, null));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Empty(hits);
    }

    [Fact]
    public async Task A_blank_reference_matches_nothing()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-9"));

        Assert.Empty(await h.Bills.FindDuplicatesAsync(clientId, vendorId, "   "));
    }

    [Fact]
    public async Task No_matches_returns_an_empty_list()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        await h.Bills.DraftAsync(clientId, Body(vendorId, "INV-1"));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.NotNull(hits);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Another_vendors_bill_with_the_same_reference_is_not_a_duplicate()
    {
        (BillServiceTests.Harness h, Guid clientId, Guid vendorId) = await BillServiceTests.SetupAsync();
        Guid otherVendorId = Guid.NewGuid();
        await h.Vendors.SaveAsync(clientId, new Vendor { Id = otherVendorId, Name = "OtherCo" });
        await h.Bills.DraftAsync(clientId, Body(otherVendorId, "INV-9"));

        IReadOnlyList<Bill> hits = await h.Bills.FindDuplicatesAsync(clientId, vendorId, "INV-9");

        Assert.Empty(hits);
    }
}
```

Vendor references are the *vendor's own* numbering, so two vendors can legitimately both issue an
"INV-9". Scoping is the store's job (`GetByVendorAsync`), but it is worth pinning here because the
whole feature is meaningless if it leaks across vendors.

`BillServiceTests.SetupAsync()` and its nested `Harness` record are `internal` in the same test assembly, so they are reachable from this class. Reuse them rather than duplicating the harness.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter FullyQualifiedName~BillDuplicateTests`
Expected: FAIL to compile — `'BillService' does not contain a definition for 'FindDuplicatesAsync'`.

- [ ] **Step 3: Implement**

Add to `Modules/Payables/Accounting101.Payables/BillService.cs`, after the existing read methods and before any private helpers:

```csharp
    /// <summary>
    /// Bills already on file for this vendor carrying the same vendor reference — the lookup behind the
    /// import screen's duplicate warning. Advisory only: nothing in this module consults it, because
    /// re-issues, corrections and annual repeats are all legitimate and only a human can tell.
    ///
    /// Includes drafts as well as entered bills. That is the point: the likeliest duplicate is an
    /// invoice imported twice while the first is still a draft, and the bill list cannot see those
    /// (<see cref="BillPaymentService.ListBillViewsAsync"/> filters to Entered). Voided bills are
    /// excluded, because a voided bill is exactly when re-entering the invoice is correct.
    ///
    /// Complete by construction: reads the vendor's bills directly, no paging and no cap. A draft and
    /// its entered successor never both appear — <see cref="EnterAsync"/> deletes the draft.
    /// </summary>
    public async Task<IReadOnlyList<Bill>> FindDuplicatesAsync(
        Guid clientId, Guid vendorId, string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return [];

        string needle = reference.Trim();
        IReadOnlyList<Bill> all = await bills.GetByVendorAsync(clientId, vendorId, ct);

        return
        [
            .. all
                .Where(b => b.Status != BillStatus.Void
                            && string.Equals(b.VendorReference?.Trim(), needle, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.BillDate)
        ];
    }
```

A null `VendorReference` yields `string.Equals(null, needle, …)`, which is false — no separate null branch needed.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter FullyQualifiedName~BillDuplicateTests`
Expected: PASS — 8 tests.

- [ ] **Step 5: Run the whole Payables suite for regressions**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: PASS, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/BillService.cs Modules/Payables/Accounting101.Payables.Tests/BillDuplicateTests.cs
git commit -m "feat(payables): bill duplicate lookup that sees drafts"
git status --short
```

---

### Task 3: The endpoint

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs`
- Test: `Modules/Payables/Accounting101.Payables.Tests/BillDuplicateEndpointTests.cs` (create)
- Test: `Modules/Payables/Accounting101.Payables.Tests/BillListPagingTests.cs` (extend)

**Interfaces:**
- Consumes: `BillService.FindDuplicatesAsync(Guid, Guid, string, CancellationToken)` from Task 2.
- Produces: `GET /clients/{clientId}/bills/duplicates?vendorId={guid}&reference={string}` returning `200` with a bare JSON array of `Bill`, or `400` when either parameter is missing or blank. Task 5 calls it.

No new authorization gate: like every other Payables read this reaches documents through `IBillStore`, whose `ScopedDocumentStore` is where `ModuleAccess.AuthorizeAsync` already enforces entitlement and capability.

- [ ] **Step 1: Write the failing endpoint tests**

Create `Modules/Payables/Accounting101.Payables.Tests/BillDuplicateEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;

namespace Accounting101.Payables.Tests;

/// <summary>The duplicate lookup over HTTP. Both query parameters are required, and the result is
/// scoped to one vendor — another vendor's bill with the same reference is not this vendor's duplicate.</summary>
public sealed class BillDuplicateEndpointTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Without_a_vendorId_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync($"/clients/{clientId}/bills/duplicates?reference=INV-9");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Without_a_reference_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bills/duplicates?vendorId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task With_no_matching_bills_returns_an_empty_array()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bills/duplicates?vendorId={Guid.NewGuid()}&reference=INV-9");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<Bill>? matches = await response.Content.ReadFromJsonAsync<List<Bill>>();
        Assert.NotNull(matches);
        Assert.Empty(matches);
    }
}
```

`PayablesHostFixture.SeedClientAsync()` returns `(Guid clientId, HttpClient http)` — the same shape `BillListValidationTests` uses. Do not edit that file; it is only the reference for this idiom.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter FullyQualifiedName~BillDuplicateEndpointTests`
Expected: FAIL — the route does not exist, so the requests return 404 rather than 400/200.

- [ ] **Step 3: Register the route**

In `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs`, inside `MapPayablesEndpoints`, add this line immediately after `clients.MapGet("/bills", ListBills);`:

```csharp
        clients.MapGet("/bills/duplicates", FindDuplicateBills);
```

Ordering against `/bills/{billId:guid}` does not matter — the `:guid` constraint means the literal `duplicates` can never match that route — but keep it beside the other bill reads for legibility.

- [ ] **Step 4: Add the handler**

Add to the same file, immediately after the `ListBills` handler method:

```csharp
    /// <summary>Bills this vendor already has carrying the given reference. Advisory: the caller decides
    /// what to do about them. Returns the domain type per this class's convention.</summary>
    private static async Task<IResult> FindDuplicateBills(
        Guid clientId, Guid? vendorId, string? reference, BillService service, CancellationToken cancellationToken)
    {
        if (vendorId is null || vendorId == Guid.Empty)
            return Results.Problem("vendorId query parameter is required.", statusCode: StatusCodes.Status400BadRequest);

        if (string.IsNullOrWhiteSpace(reference))
            return Results.Problem("reference query parameter is required.", statusCode: StatusCodes.Status400BadRequest);

        return Results.Ok(await service.FindDuplicatesAsync(clientId, vendorId.Value, reference, cancellationToken));
    }
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter FullyQualifiedName~BillDuplicateEndpointTests`
Expected: PASS — 3 tests.

- [ ] **Step 6: Pin why the bill list and the duplicate lookup differ**

Append this test to `Modules/Payables/Accounting101.Payables.Tests/BillListPagingTests.cs`, inside its existing class:

```csharp
    /// <summary>The bill list is a settlement view, so it shows only Entered bills — drafts have no
    /// A/P balance to settle. This is deliberate, and it is why the duplicate lookup is a separate
    /// read rather than a filter over this one: a duplicate that is still a draft is invisible here.
    /// If you are about to "fix" one of these two reads to match the other, read this first.</summary>
    [Fact]
    public async Task Bill_list_shows_only_entered_bills_by_design()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await BillSettlementScenario.SetUpChartAsync(controller, clientId, fixture);
        Guid vendorId = await BillSettlementScenario.CreateVendorAsync(clerk, clientId);

        // A draft, deliberately left un-entered.
        DraftBillRequest req = new(vendorId, new DateOnly(2026, 3, 1), null, "REF", null,
            [new BillLineBody("Rent", 1000m, fixture.RentExpenseAccountId)]);
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", req)).EnsureSuccessStatusCode();

        PagedResponse<BillView> page = (await clerk.GetFromJsonAsync<PagedResponse<BillView>>(
            $"/clients/{clientId}/bills?vendorId={vendorId}"))!;

        Assert.Equal(0, page.Total);       // the draft exists, but this read cannot see it
    }
```

This mirrors the setup the file's existing tests use — `fixture.SeedSodClientAsync()` returning
`(clientId, controller, clerk, approver)`, then `BillSettlementScenario.SetUpChartAsync` and
`CreateVendorAsync`, with `fixture.RentExpenseAccountId` for the line. The one difference is
deliberate: the file's private `EnterAsync` helper drafts *and* enters, so this test does not use it —
the whole point is a bill that stays a draft.

- [ ] **Step 7: Run the whole Payables suite**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: PASS, 0 warnings.

- [ ] **Step 8: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs Modules/Payables/Accounting101.Payables.Tests/BillDuplicateEndpointTests.cs Modules/Payables/Accounting101.Payables.Tests/BillListPagingTests.cs
git commit -m "feat(payables): GET /bills/duplicates, and pin why the bill list differs"
git status --short
```

---

### Task 4: PayablesService.billDuplicates

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.service.ts`
- Test: `UI/Angular/src/app/core/payables/payables.service.spec.ts` (extend; create if absent)

**Interfaces:**
- Consumes: `GET /clients/{id}/bills/duplicates?vendorId=&reference=` from Task 3; the existing `Bill` interface in `core/payables/payables.ts`.
- Produces: `PayablesService.billDuplicates(vendorId: string, reference: string): Observable<Bill[]>`. Task 5 calls it.

- [ ] **Step 1: Write the failing test**

Add to `UI/Angular/src/app/core/payables/payables.service.spec.ts`, inside its existing top-level `describe`:

```ts
  it('fetches bill duplicates for a vendor and reference', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    const svc = TestBed.inject(PayablesService);
    const ctrl = TestBed.inject(HttpTestingController);

    let got: Bill[] | undefined;
    svc.billDuplicates('v1', 'INV-9').subscribe(r => (got = r));

    const req = ctrl.expectOne(r =>
      r.url === 'http://localhost:5000/clients/C1/bills/duplicates'
      && r.params.get('vendorId') === 'v1'
      && r.params.get('reference') === 'INV-9');
    expect(req.request.method).toBe('GET');
    req.flush([{ id: 'b1', vendorId: 'v1', number: 'BILL-00001', billDate: '2026-03-01', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Draft', lines: [] }]);

    expect(got?.length).toBe(1);
    expect(got?.[0].status).toBe('Draft');
    ctrl.verify();
  });

  it('makes no duplicates request when no client is selected', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(PayablesService);
    const ctrl = TestBed.inject(HttpTestingController);

    let emitted = false;
    svc.billDuplicates('v1', 'INV-9').subscribe({ next: () => (emitted = true) });

    expect(emitted).toBe(false);
    ctrl.verify();
  });
```

`UI/Angular/src/app/core/payables/payables.service.spec.ts` already exists — add both tests to its existing top-level `describe`, and add any of these imports it does not already have: `provideZonelessChangeDetection` from `@angular/core`, `provideHttpClient` from `@angular/common/http`, `HttpTestingController`/`provideHttpClientTesting` from `@angular/common/http/testing`, `ClientContextService`, and the `Bill` type from `./payables`.

- [ ] **Step 2: Run to verify it fails**

Run: `npx ng test --include=src/app/core/payables/payables.service.spec.ts --watch=false`
Expected: FAIL — `svc.billDuplicates is not a function`.

- [ ] **Step 3: Implement**

Add to `UI/Angular/src/app/core/payables/payables.service.ts`, immediately after the existing `listBills` method:

```ts
  /** Bills this vendor already has carrying `reference`. Server-side and complete — unlike a scan over
   *  listBills, it sees drafts, which is the case that matters: the likeliest duplicate is an invoice
   *  imported twice while the first is still a draft. Advisory; nothing blocks on it. */
  billDuplicates(vendorId: string, reference: string): Observable<Bill[]> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    const params = new HttpParams().set('vendorId', vendorId).set('reference', reference);
    return this.http.get<Bill[]>(this.base('/bills/duplicates'), { params });
  }
```

`Bill`, `HttpParams`, `EMPTY` and `Observable` are all already imported by this file — verify before adding any import.

- [ ] **Step 4: Run to verify it passes**

Run: `npx ng test --include=src/app/core/payables/payables.service.spec.ts --watch=false`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.service.ts UI/Angular/src/app/core/payables/payables.service.spec.ts
git commit -m "feat(ui): PayablesService.billDuplicates"
git status --short
```

---

### Task 5: Switch the screen over and delete the scan

**Files:**
- Modify: `UI/Angular/src/app/features/einvoicing/einvoice-import.ts`
- Modify: `UI/Angular/src/app/features/einvoicing/einvoice-import.spec.ts`
- Modify: `UI/Angular/src/app/core/einvoicing/einvoicing.ts`
- Modify: `UI/Angular/src/app/core/einvoicing/einvoicing.spec.ts`

**Interfaces:**
- Consumes: `PayablesService.billDuplicates(vendorId, reference): Observable<Bill[]>` from Task 4.
- Produces: nothing downstream. This is the last task.

**Read the current file in full before editing.** The duplicate-check effect carries two distinct race guards (`generation` and `#lastDuplicateKey`) that must survive this change; only the data source and the bounded-scan bookkeeping go.

- [ ] **Step 1: Update the tests first**

In `UI/Angular/src/app/features/einvoicing/einvoice-import.spec.ts`:

**(a)** Replace every `/bills` GET expectation with the duplicates endpoint. The existing tests use forms like:

```ts
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));
```

Each becomes:

```ts
    ctrl.match(r => r.url.endsWith('/bills/duplicates')).forEach(r => r.flush([]));
```

**(b)** Rewrite the duplicate tests. Replace the existing `'warns when the vendor already has a bill with the same reference'`, `'says what it actually checked when the scan is bounded'`, and `'does not check when the document carries no vendor reference'` tests with:

```ts
  it('warns when the vendor already has a bill with the same reference', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    fixture.detectChanges();
    const req = ctrl.expectOne(r =>
      r.url === 'http://localhost:5000/clients/C1/bills/duplicates'
      && r.params.get('vendorId') === 'v1'
      && r.params.get('reference') === 'INV-9');
    req.flush([{ id: 'b1', vendorId: 'v1', number: 'BILL-00001', billDate: '2026-03-01', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [] }]);
    fixture.detectChanges();

    expect(cmp.duplicates().map(b => b.id)).toEqual(['b1']);
    expect(fixture.nativeElement.textContent).toContain('already has a bill');
    ctrl.verify();
  });

  it('warns about a duplicate that is still a draft', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    fixture.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/bills/duplicates')).flush([
      { id: 'b1', vendorId: 'v1', number: null, billDate: '2026-03-01', dueDate: null,
        vendorReference: 'INV-9', memo: null, status: 'Draft', lines: [] },
    ]);
    fixture.detectChanges();

    expect(cmp.duplicates().map(b => b.id)).toEqual(['b1']);
    expect(fixture.nativeElement.textContent).toContain('Draft');
    ctrl.verify();
  });

  it('does not check when the document carries no vendor reference', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable({ vendorReference: null }));
    fixture.detectChanges();
    cmp.vendorId.set('v1');
    fixture.detectChanges();
    expect(cmp.duplicateChecked()).toBe(false);
    ctrl.verify();                                            // no duplicates request at all
  });

  it('a duplicate never blocks the confirm', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    cmp.patchLine(0, { expenseAccountId: 'a1' });             // fully coded, so nothing else gates it
    fixture.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/bills/duplicates')).flush([
      { id: 'b1', vendorId: 'v1', number: 'BILL-00001', billDate: '2026-03-01', dueDate: null,
        vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [] },
    ]);
    fixture.detectChanges();

    expect(cmp.duplicates().length).toBe(1);
    expect(cmp.canConfirm()).toBe(true);                      // a warning, not a gate
    ctrl.verify();
  });
```

The Draft test is the regression for the shipped defect: a draft match must warn, and before this
change nothing appeared at all. The confirm test pins the policy's "it does not block" clause against
the code rather than only against prose. The bounded-scan test is deleted outright — there is no bound
left to report.

**(c)** Leave every other test in the file untouched, including the two stale-response regression tests, which still exercise guards this task keeps.

In `UI/Angular/src/app/core/einvoicing/einvoicing.spec.ts`, delete the whole `describe('findDuplicates', …)` block and nothing else.

- [ ] **Step 2: Run to verify they fail**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: FAIL — the component still calls `listBills`, so the expected `/bills/duplicates` request is never made.

- [ ] **Step 3: Replace the effect's data source**

In `einvoice-import.ts`, replace the whole duplicate-check `effect(() => { … })` (currently lines 365-410, beginning with the comment `// Re-runs whenever the vendor or the document changes`) with:

```ts
    // Re-runs whenever the vendor or the document changes — including after an inline create.
    effect(() => {
      const vendorId = this.vendorId();
      const reference = this.proposal()?.vendorReference ?? null;
      if (!vendorId || !reference) {
        this.#lastDuplicateKey = null;
        this.duplicates.set([]);
        this.duplicateChecked.set(false);
        return;
      }
      const key = `${vendorId}|${reference}`;
      if (this.#lastDuplicateKey === key) return;
      this.#lastDuplicateKey = key;
      // Clear stale results before issuing the new lookup — otherwise a vendor change that goes
      // straight into a failing/slow lookup leaves the previous vendor's duplicates (and their
      // live bill links) on screen, now misattributed to the new vendor.
      this.duplicates.set([]);
      this.duplicateChecked.set(false);
      // Captured for the same reason confirm()/createVendor() capture it: a response that outlives
      // a reset() — the operator moved on to a different document before it arrived — must not
      // write duplicate state for a document that's no longer on screen. The #lastDuplicateKey
      // guard above only prevents redundant refetches for an unchanged pair; it does nothing once
      // reset() has already cleared vendorId/proposal out from under an in-flight request.
      const gen = this.generation;
      this.pay.billDuplicates(vendorId, reference).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: matches => {
          // The generation guard discards a lookup whose document was reset(); the key guard
          // discards one whose vendor was superseded by a later lookup before this one landed —
          // distinct races, both needed (see the "switch vendors mid-scan" regression test).
          if (gen !== this.generation || this.#lastDuplicateKey !== key) return;
          this.duplicates.set(matches);
          this.duplicateChecked.set(true);
        },
        // A failed lookup is not a failed import — say nothing rather than claim a clean result.
        error: () => {
          if (gen !== this.generation || this.#lastDuplicateKey !== key) return;
          this.duplicates.set([]);
          this.duplicateChecked.set(false);
        },
      });
    });
```

- [ ] **Step 4: Delete the bounded-scan bookkeeping**

In `einvoice-import.ts`:

1. Delete the module-level constant and its doc comment (currently line 25):

```ts
const DUPLICATE_SCAN_LIMIT = 200;
```

2. Delete these three members from the class (currently lines 327, 329, 330), keeping `duplicates` and `duplicateChecked`:

```ts
  readonly duplicateScanTotal = signal(0);
  readonly duplicateScanLimit = DUPLICATE_SCAN_LIMIT;
  readonly duplicateScanBounded = computed(() => this.duplicateScanTotal() > DUPLICATE_SCAN_LIMIT);
```

3. Delete `this.duplicateScanTotal.set(0);` from `reset()` (currently line 547), keeping the other three duplicate-state lines there.

4. In the template, replace the `@else if` branch (currently lines 200-204) so the block ends after the warning:

```html
            }
```

That is, the `@if (duplicates().length) { … }` block loses its `@else if (duplicateChecked() && duplicateScanBounded()) { … }` entirely. A complete lookup has nothing to disclaim.

5. Fix the imports: remove `findDuplicates` from the `core/einvoicing/einvoicing` import (line 10) and remove `BillListQuery` from the `core/payables/payables` import (line 12). Keep `Bill`, `DraftBillRequest` and `billTotal` — `Bill` still types the `duplicates` signal.

- [ ] **Step 5: Delete the helper it replaced**

In `UI/Angular/src/app/core/einvoicing/einvoicing.ts`, delete the `findDuplicates` function and its doc comment. `findDuplicates` is that file's only use of `Bill`, so its import on line 1 becomes:

```ts
import { Vendor } from '../payables/payables';
```

`Vendor` stays — `matchVendor` uses it.

- [ ] **Step 6: Run the einvoicing tests**

Run: `npx ng test --include=src/app/features/einvoicing/einvoice-import.spec.ts --watch=false`
Expected: PASS.

Run: `npx ng test --include=src/app/core/einvoicing/einvoicing.spec.ts --watch=false`
Expected: PASS, with the `findDuplicates` block gone.

- [ ] **Step 7: Run the full Angular suite and build**

Run: `npm test -- --watch=false`
Expected: PASS. The suite had 562 tests before this task; expect a lower total, since `findDuplicates`' four tests and the bounded-scan test are deleted and one draft-regression test is added.

Run: `npm run build`
Expected: succeeds. A pre-existing bundle-budget **warning** (~53 kB over 1.00 MB) is expected and is not a failure.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/features/einvoicing/einvoice-import.ts UI/Angular/src/app/features/einvoicing/einvoice-import.spec.ts UI/Angular/src/app/core/einvoicing/einvoicing.ts UI/Angular/src/app/core/einvoicing/einvoicing.spec.ts
git commit -m "feat(einvoicing): use the server-side duplicate lookup, delete the bounded scan"
git status --short
```

---

## Manual verification

After Task 5, against a running stack:

1. Start the API and `npm start`. The demo client needs `einvoicing` in its enabled modules.
2. Payables → Bills → Import e-invoice. Upload a USD invoice, create or pick the vendor, code the lines, create the draft.
3. Import the **same file again** without entering the draft. The amber strip must now appear, listing the draft. That is the defect this plan fixes — before it, nothing appeared.
4. Enter the bill, import a third time: the strip lists the entered bill.
5. Void the bill, import again: no warning.
