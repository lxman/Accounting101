# AR/AP Polish Batch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close three deferred cross-module polish items — deterministic same-date row ordering in the account builders, culture-independent `asOf` parsing in both account endpoints, and an explanatory over-allocation line in the four allocation editors.

**Architecture:** Three independent items, each touching both Receivables (AR) and Payables (AP) as line-for-line mirrors. Backend changes are additive sort keys and a stricter parse; the frontend change is a purely additive computed + one template line (the Save gate is unchanged). Item 1 from the original backlog ("keyboard-bypassable disabled buttons") was investigated and dropped — no concrete defect.

**Tech Stack:** .NET 10 / C# 13, xUnit (backend); Angular 22 + Signal Forms, vitest via `ng test` (frontend).

## Global Constraints

- USD-only; camelCase on the wire.
- TDD: every change is covered by a test that fails first.
- AR and AP fixes are line-for-line mirrors — keep them symmetric.
- Pure-fold builders stay pure and deterministic given their inputs.
- The public row DTOs (`OpenInvoiceLine`, `OpenBillLine`, `CreditActivityLine`, `StatementLine`) are unchanged; new sort keys are internal only.
- No change to allocation *validation* logic or the Save-disabled gate; Item 2 is additive explanation only.
- No unrelated refactoring.
- Do NOT stage `UI/Angular/src/app/core/api/environment.ts` (local dev `devClientId`, must stay uncommitted).

---

### Task 1: Same-date row ordering tiebreakers

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs:39` (OpenInvoices) and `:104` (CreditActivity)
- Modify: `Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs:33` (OpenBills) and `:81` (CreditActivity)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/CustomerAccountBuilderTests.cs`
- Test: `Modules/Payables/Accounting101.Payables.Tests/VendorAccountBuilderTests.cs`

**Interfaces:**
- Consumes: nothing new. Existing records — `OpenInvoiceLine(Guid Id, string Number, DateOnly IssueDate, DateOnly? DueDate, decimal OpenBalance, int DaysOverdue)`, `OpenBillLine(Guid Id, string? Number, DateOnly BillDate, DateOnly? DueDate, decimal OpenBalance, int DaysOverdue)`, `CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance)`.
- Produces: no signature changes. Same-date output ordering becomes deterministic: OpenInvoices/OpenBills by `Number`; CreditActivity by (Date, type-Order, source Id).

- [ ] **Step 1: Write the failing AR tests**

Add to `CustomerAccountBuilderTests.cs` (inside the class):

```csharp
[Fact]
public void OpenInvoices_orders_same_date_by_number()
{
    Invoice inv1002 = IssuedInvoice(Guid.NewGuid(), "1002", new(2026, 3, 1), null, 100m);
    Invoice inv1001 = IssuedInvoice(Guid.NewGuid(), "1001", new(2026, 3, 1), null, 100m);
    // fed in reversed number order; same issue date
    IReadOnlyList<OpenInvoiceLine> open =
        CustomerAccountBuilder.OpenInvoices([inv1002, inv1001], new Dictionary<Guid, decimal>(), asOf: new(2026, 3, 1));

    Assert.Equal(["1001", "1002"], open.Select(l => l.Number));
}

[Fact]
public void CreditActivity_orders_same_date_deterministically_by_type_then_id()
{
    DateOnly d = new(2026, 3, 5);
    Guid pA = new("00000000-0000-0000-0000-000000000001");
    Guid pB = new("00000000-0000-0000-0000-000000000002");
    // Two same-date overpayments fed high-Id first (Id tiebreak) + a same-date credit application (type Order after overpayments).
    List<Payment> payments =
    [
        new() { Id = pB, CustomerId = Guid.NewGuid(), Date = d, Amount = 20m, Allocations = [] }, // unapplied 20
        new() { Id = pA, CustomerId = Guid.NewGuid(), Date = d, Amount = 10m, Allocations = [] }, // unapplied 10
    ];
    List<CreditApplication> apps =
        [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = d, Allocations = [new Allocation(Guid.NewGuid(), 5m)] }];

    IReadOnlyList<CreditActivityLine> lines = CustomerAccountBuilder.CreditActivity(payments, apps, []);

    // Overpayments first (Order 0), ordered by Id (pA=…01 before pB=…02) → 10 then 20; then Credit applied (Order 1) → -5.
    Assert.Equal([10m, 20m, -5m], lines.Select(l => l.Amount));
    Assert.Equal(["Overpayment", "Overpayment", "Credit applied"], lines.Select(l => l.Type));
}
```

- [ ] **Step 2: Write the failing AP tests**

Add to `VendorAccountBuilderTests.cs` (inside the class):

```csharp
[Fact]
public void OpenBills_orders_same_date_by_number()
{
    Bill b1002 = EnteredBill(Guid.NewGuid(), 100m, new DateOnly(2026, 3, 1), null, "B-1002");
    Bill b1001 = EnteredBill(Guid.NewGuid(), 100m, new DateOnly(2026, 3, 1), null, "B-1001");
    var open = VendorAccountBuilder.OpenBills([b1002, b1001], new Dictionary<Guid, decimal>(), new DateOnly(2026, 3, 1));

    Assert.Equal(["B-1001", "B-1002"], open.Select(l => l.Number));
}

[Fact]
public void CreditActivity_orders_same_date_deterministically_by_type_then_id()
{
    DateOnly d = new(2026, 3, 5);
    Guid pA = new("00000000-0000-0000-0000-000000000001");
    Guid pB = new("00000000-0000-0000-0000-000000000002");
    // Two same-date overpayments fed high-Id first + a same-date vendor-credit application.
    List<BillPayment> payments =
    [
        new() { Id = pB, VendorId = Guid.NewGuid(), Date = d, Amount = 20m, Method = null, Allocations = [] }, // unapplied 20
        new() { Id = pA, VendorId = Guid.NewGuid(), Date = d, Amount = 10m, Method = null, Allocations = [] }, // unapplied 10
    ];
    List<VendorCreditApplication> apps =
        [new() { Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = d, Allocations = [new Allocation(Guid.NewGuid(), 5m)] }];

    var lines = VendorAccountBuilder.CreditActivity(payments, apps);

    Assert.Equal([10m, 20m, -5m], lines.Select(l => l.Amount));
    Assert.Equal(["Overpayment", "Overpayment", "Credit applied"], lines.Select(l => l.Type));
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~CustomerAccountBuilderTests.OpenInvoices_orders_same_date_by_number|FullyQualifiedName~CustomerAccountBuilderTests.CreditActivity_orders_same_date_deterministically_by_type_then_id"`
Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~VendorAccountBuilderTests.OpenBills_orders_same_date_by_number|FullyQualifiedName~VendorAccountBuilderTests.CreditActivity_orders_same_date_deterministically_by_type_then_id"`
Expected: FAIL. The OpenInvoices/OpenBills tests may pass or fail depending on incoming order (they rely on stable sort of already-ordered input); the CreditActivity tests FAIL because same-date rows currently keep input order (pB before pA → 20 then 10) with no type/Id tiebreak.

- [ ] **Step 4: Implement AR — `CustomerAccountBuilder.cs`**

OpenInvoices (line 39): add the `Number` tiebreak:

```csharp
            .Where(l => l.OpenBalance > 0m)
            .OrderBy(l => l.IssueDate).ThenBy(l => l.Number).ToList();
```

CreditActivity: carry a type `Order` and the source `Id` in the raw tuple, and tiebreak on them. Replace the whole method body (lines 92-110) with:

```csharp
    public static IReadOnlyList<CreditActivityLine> CreditActivity(
        IReadOnlyList<Payment> payments, IReadOnlyList<CreditApplication> creditApps, IReadOnlyList<Refund> refunds)
    {
        List<(DateOnly Date, int Order, Guid Id, string Type, string? Reference, decimal Amount)> raw = [];
        foreach (Payment p in payments.Where(p => !p.Voided && p.Unapplied > 0m))
            raw.Add((p.Date, 0, p.Id, "Overpayment", null, p.Unapplied));
        foreach (CreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "Credit applied", null, -c.Applied));
        foreach (Refund r in refunds.Where(r => !r.Voided))
            raw.Add((r.Date, 2, r.Id, "Refund", r.Memo, -r.Amount));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order).ThenBy(r => r.Id)
            .Select(r =>
            {
                balance += r.Amount;
                return new CreditActivityLine(r.Date, r.Type, r.Reference, r.Amount, balance);
            }).ToList();
    }
```

- [ ] **Step 5: Implement AP — `VendorAccountBuilder.cs`**

OpenBills (line 33): add the `Number` tiebreak:

```csharp
            .Where(l => l.OpenBalance > 0m)
            .OrderBy(l => l.BillDate).ThenBy(l => l.Number).ToList();
```

CreditActivity: replace the whole method body (lines 71-87) with:

```csharp
    public static IReadOnlyList<CreditActivityLine> CreditActivity(
        IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps)
    {
        List<(DateOnly Date, int Order, Guid Id, string Type, string? Reference, decimal Amount)> raw = [];
        foreach (BillPayment p in payments.Where(p => !p.Voided && p.Unapplied > 0m))
            raw.Add((p.Date, 0, p.Id, "Overpayment", null, p.Unapplied));
        foreach (VendorCreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "Credit applied", null, -c.Applied));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order).ThenBy(r => r.Id)
            .Select(r =>
            {
                balance += r.Amount;
                return new CreditActivityLine(r.Date, r.Type, r.Reference, r.Amount, balance);
            }).ToList();
    }
```

- [ ] **Step 6: Run the full builder test suites to verify they pass**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~CustomerAccountBuilderTests"`
Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~VendorAccountBuilderTests"`
Expected: PASS, all builder tests green (the four new tests plus the pre-existing folds, which use distinct dates and are unaffected).

- [ ] **Step 7: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs \
        Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/CustomerAccountBuilderTests.cs \
        Modules/Payables/Accounting101.Payables.Tests/VendorAccountBuilderTests.cs
git commit -m "fix(360): deterministic same-date ordering in account builders

OpenInvoices/OpenBills tiebreak by number; CreditActivity tiebreaks by a
per-type order then source id, so same-date rows no longer depend on the
upstream query order.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Culture-independent asOf parse

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (add `using System.Globalization;`; line 189)
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs` (add `using System.Globalization;`; line 258)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/GetCustomerAccountEndpointTests.cs`
- Test: `Modules/Payables/Accounting101.Payables.Tests/VendorAccountEndpointE2eTests.cs`

**Interfaces:**
- Consumes: existing host fixtures — AR `ReceivablesHostFixture.SeedSodClientAsync()` → `(Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver)`; AP `PayablesHostFixture.SeedSodClientAsync()` (same shape). AR customer creation: `POST /clients/{clientId}/customers` with `new CreateCustomerRequest("Stark", null)` → `Customer`. AP vendor creation: `POST /clients/{clientId}/vendors` with `new CreateVendorRequest("PropCo", null)` → `Vendor`.
- Produces: no signature change. `GET …/account?asOf=` now accepts only `yyyy-MM-dd` regardless of server culture; anything else → 400.

- [ ] **Step 1: Write the failing AR endpoint test**

Add to `GetCustomerAccountEndpointTests.cs` (inside the class):

```csharp
[Fact]
public async Task GET_account_rejects_non_iso_asOf_and_accepts_iso()
{
    (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
    Customer customer = (await (await clerk.PostAsJsonAsync(
        $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
        .Content.ReadFromJsonAsync<Customer>())!;

    // Slash-format date: accepted by the old current-culture TryParse (en-US), rejected by strict ISO.
    HttpResponseMessage bad = await clerk.GetAsync(
        $"/clients/{clientId}/customers/{customer.Id}/account?asOf={Uri.EscapeDataString("06/15/2026")}");
    Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

    // ISO parses regardless of server culture.
    HttpResponseMessage ok = await clerk.GetAsync(
        $"/clients/{clientId}/customers/{customer.Id}/account?asOf=2026-06-15");
    Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
}
```

- [ ] **Step 2: Write the failing AP endpoint test**

Add to `VendorAccountEndpointE2eTests.cs` (inside the class):

```csharp
[Fact]
public async Task GET_account_rejects_non_iso_asOf_and_accepts_iso()
{
    (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
    Vendor vendor = (await (await clerk.PostAsJsonAsync(
        $"/clients/{clientId}/vendors", new CreateVendorRequest("PropCo", null)))
        .Content.ReadFromJsonAsync<Vendor>())!;

    HttpResponseMessage bad = await clerk.GetAsync(
        $"/clients/{clientId}/vendors/{vendor.Id}/account?asOf={Uri.EscapeDataString("06/15/2026")}");
    Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

    HttpResponseMessage ok = await clerk.GetAsync(
        $"/clients/{clientId}/vendors/{vendor.Id}/account?asOf=2026-06-15");
    Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~GetCustomerAccountEndpointTests.GET_account_rejects_non_iso_asOf_and_accepts_iso"`
Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~VendorAccountEndpointE2eTests.GET_account_rejects_non_iso_asOf_and_accepts_iso"`
Expected: FAIL on the `bad` assertion — under en-US the current `DateOnly.TryParse("06/15/2026")` succeeds, so the endpoint returns 200/404, not 400.

- [ ] **Step 4: Implement AR — `ReceivablesEndpoints.cs`**

Add the import at the top of the using block:

```csharp
using System.Globalization;
```

Change line 189 (inside `GetCustomerAccount`):

```csharp
        else if (!DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return Results.Problem("asOf must be a date (yyyy-MM-dd).", statusCode: StatusCodes.Status400BadRequest);
```

- [ ] **Step 5: Implement AP — `PayablesEndpoints.cs`**

Add the import at the top of the using block:

```csharp
using System.Globalization;
```

Change line 258 (inside `GetVendorAccount`):

```csharp
        else if (!DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return Results.Problem("asOf must be a date (yyyy-MM-dd).", statusCode: StatusCodes.Status400BadRequest);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~GetCustomerAccountEndpointTests"`
Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~VendorAccountEndpointE2eTests"`
Expected: PASS (new tests plus the existing reconcile/404 tests, which use ISO `asOf` and are unaffected).

- [ ] **Step 7: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs \
        Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/GetCustomerAccountEndpointTests.cs \
        Modules/Payables/Accounting101.Payables.Tests/VendorAccountEndpointE2eTests.cs
git commit -m "fix(360): parse asOf as strict invariant-culture yyyy-MM-dd

Both account endpoints parsed asOf with the current thread culture, so an
ambiguous string parsed differently by locale. Switch to TryParseExact with
InvariantCulture, matching the documented yyyy-MM-dd contract.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Over-allocation summary line (four editors)

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/payment-editor.ts`
- Modify: `UI/Angular/src/app/features/receivables/adjustment-editor.ts`
- Modify: `UI/Angular/src/app/features/payables/bill-payment-editor.ts`
- Modify: `UI/Angular/src/app/features/payables/vendor-credit-apply-editor.ts`
- Test: the matching `.spec.ts` for each of the four.

**Interfaces:**
- Consumes: existing per-editor signals/computeds — `payment-editor`/`bill-payment-editor`: `amount()`, `allocated()`, `rows()` (each row `{ allocation, openBalance }`), `money(n)`; `adjustment-editor`: `type()`, `total()`, `creditBalance()`, `rows()` (each `{ included, amount, openBalance }`), `money(n)`; `vendor-credit-apply-editor`: `allocated()`, `available()`, `rows()`, `money(n)`.
- Produces: a new public computed `allocationWarning: Signal<string | null>` on each editor, rendered next to the disabled Save button. `valid()` and the Save gate are unchanged.

- [ ] **Step 1: Write the failing AR tests**

In `payment-editor.spec.ts`, add inside `describe('PaymentEditor', …)`:

```typescript
it('warns and disables Save when allocations exceed the payment amount', () => {
  const ctrl = setup({ customer: 'cu1' });
  const f = TestBed.createComponent(PaymentEditor); f.detectChanges();
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme', email: null }]);
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices') && r.params.get('settlement') === 'open')
    .flush({ items: [openInvoice('inv1', '1001', 105)], total: 1, skip: 0, limit: 200 });
  ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/credit-balance').flush({ customerId: 'cu1', creditBalance: 0 });
  f.detectChanges();
  const cmp = f.componentInstance as PaymentEditor;
  cmp.onAmount(50); cmp.onRow(0, 80); f.detectChanges();   // allocated 80 > amount 50 (row 80 <= open 105)
  expect(cmp.valid()).toBe(false);
  expect(cmp.allocationWarning()).toContain('exceeds the payment amount');
  expect(f.nativeElement.textContent).toContain('exceeds the payment amount');
  cmp.onRow(0, 40); f.detectChanges();                      // 40 <= amount 50 → valid
  expect(cmp.valid()).toBe(true);
  expect(cmp.allocationWarning()).toBeNull();
  ctrl.verify();
});
```

In `adjustment-editor.spec.ts`, add inside `describe('AdjustmentEditor', …)`:

```typescript
it('warns and disables Save when applied credit exceeds available credit', () => {
  const ctrl = setup('cu1');
  const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
  loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 200 }], 30);  // available credit 30
  const cmp = f.componentInstance as AdjustmentEditor;
  cmp.setType('credit-application');
  cmp.toggleRow(0, true);          // include → amount defaults to open 200; total 200 > credit 30
  f.detectChanges();
  expect(cmp.valid()).toBe(false);
  expect(cmp.allocationWarning()).toContain('exceeds available credit');
  expect(f.nativeElement.textContent).toContain('exceeds available credit');
  cmp.setAmount(0, 20); f.detectChanges();                 // 20 <= credit 30 → valid
  expect(cmp.valid()).toBe(true);
  expect(cmp.allocationWarning()).toBeNull();
  ctrl.verify();
});
```

- [ ] **Step 2: Write the failing AP tests**

In `bill-payment-editor.spec.ts`, add inside `describe('BillPaymentEditor', …)`:

```typescript
it('warns and disables Save when allocations exceed the payment amount', () => {
  const ctrl = setup();
  const f = TestBed.createComponent(BillPaymentEditor); f.detectChanges();
  flushInit(ctrl); f.detectChanges();                       // bill b1 open 100
  const cmp = f.componentInstance;
  cmp.onAmount(50); cmp.onRow(0, 80); f.detectChanges();    // allocated 80 > amount 50 (row 80 <= open 100)
  expect(cmp.valid()).toBe(false);
  expect(cmp.allocationWarning()).toContain('exceeds the payment amount');
  expect(f.nativeElement.textContent).toContain('exceeds the payment amount');
  cmp.onRow(0, 40); f.detectChanges();
  expect(cmp.valid()).toBe(true);
  expect(cmp.allocationWarning()).toBeNull();
  ctrl.verify();
});
```

In `vendor-credit-apply-editor.spec.ts`, add inside `describe('VendorCreditApplyEditor', …)`:

```typescript
it('warns and disables Save when applied exceeds available credit', () => {
  const ctrl = setup();
  const f = TestBed.createComponent(VendorCreditApplyEditor); f.detectChanges();
  flushInit(ctrl); f.detectChanges();                       // available 50; bill b1 open 100
  const cmp = f.componentInstance;
  cmp.onRow(0, 80); f.detectChanges();                      // allocated 80 > available 50 (row 80 <= open 100)
  expect(cmp.valid()).toBe(false);
  expect(cmp.allocationWarning()).toContain('exceeds available credit');
  expect(f.nativeElement.textContent).toContain('exceeds available credit');
  cmp.onRow(0, 40); f.detectChanges();                      // 40 <= available 50 → valid
  expect(cmp.valid()).toBe(true);
  expect(cmp.allocationWarning()).toBeNull();
  ctrl.verify();
});
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `cmp.allocationWarning` does not exist yet (compile/type error in the four new tests), and the text assertions find no message.

- [ ] **Step 4: Implement — `payment-editor.ts`**

Add the computed immediately after the `valid` computed (after line 111):

```typescript
  readonly allocationWarning = computed<string | null>(() => {
    const over = Math.round((this.allocated() - this.amount()) * 100) / 100;
    if (this.amount() > 0 && over > 0)
      return `Allocated ${this.money(this.allocated())} exceeds the payment amount by ${this.money(over)}.`;
    if (this.rows().some(r => r.allocation > r.openBalance))
      return 'A line is allocated more than its open balance.';
    return null;
  });
```

In the template, insert this line immediately before the `@if (message())` line (line 80):

```html
      @if (allocationWarning()) { <p class="text-destructive text-sm">{{ allocationWarning() }}</p> }
```

- [ ] **Step 5: Implement — `bill-payment-editor.ts`**

Add the computed immediately after the `valid` computed (after line 111):

```typescript
  readonly allocationWarning = computed<string | null>(() => {
    const over = Math.round((this.allocated() - this.amount()) * 100) / 100;
    if (this.amount() > 0 && over > 0)
      return `Allocated ${this.money(this.allocated())} exceeds the payment amount by ${this.money(over)}.`;
    if (this.rows().some(r => r.allocation > r.openBalance))
      return 'A line is allocated more than its open balance.';
    return null;
  });
```

In the template, insert immediately before the `@if (message())` line:

```html
      @if (allocationWarning()) { <p class="text-destructive text-sm">{{ allocationWarning() }}</p> }
```

- [ ] **Step 6: Implement — `adjustment-editor.ts`**

Add the computed immediately after the `valid` computed (after line 140):

```typescript
  readonly allocationWarning = computed<string | null>(() => {
    if (this.type() === 'credit-application') {
      const over = Math.round((this.total() - this.creditBalance()) * 100) / 100;
      if (over > 0)
        return `Applied ${this.money(this.total())} exceeds available credit by ${this.money(over)}.`;
    }
    if (this.rows().some(r => r.included && r.amount > r.openBalance))
      return 'A line is adjusted more than its open balance.';
    return null;
  });
```

In the template, insert immediately before the `@if (message())` line (line 101):

```html
      @if (allocationWarning()) { <p class="text-destructive text-sm">{{ allocationWarning() }}</p> }
```

- [ ] **Step 7: Implement — `vendor-credit-apply-editor.ts`**

Add the computed immediately after the `valid` computed (after line 98):

```typescript
  readonly allocationWarning = computed<string | null>(() => {
    const over = Math.round((this.allocated() - this.available()) * 100) / 100;
    if (over > 0)
      return `Applied ${this.money(this.allocated())} exceeds available credit by ${this.money(over)}.`;
    if (this.rows().some(r => r.allocation > r.openBalance))
      return 'A line is applied more than its open balance.';
    return null;
  });
```

In the template, insert immediately before the `@if (message())` line (line 70):

```html
      @if (allocationWarning()) { <p class="text-destructive text-sm">{{ allocationWarning() }}</p> }
```

- [ ] **Step 8: Run the full UI suite to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS, all specs green including the four new over-allocation tests. Output pristine.

- [ ] **Step 9: Commit**

```bash
git add UI/Angular/src/app/features/receivables/payment-editor.ts \
        UI/Angular/src/app/features/receivables/payment-editor.spec.ts \
        UI/Angular/src/app/features/receivables/adjustment-editor.ts \
        UI/Angular/src/app/features/receivables/adjustment-editor.spec.ts \
        UI/Angular/src/app/features/payables/bill-payment-editor.ts \
        UI/Angular/src/app/features/payables/bill-payment-editor.spec.ts \
        UI/Angular/src/app/features/payables/vendor-credit-apply-editor.ts \
        UI/Angular/src/app/features/payables/vendor-credit-apply-editor.spec.ts
git commit -m "feat(ui): explain over-allocation instead of silently disabling Save

The four allocation editors capped allocations and disabled Save with no
reason shown. Add an allocationWarning line next to Save that names the
over-allocation (past the payment amount / available credit, or a line past
its open balance). The Save gate is unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- Item 3 (same-date ordering) → Task 1 (OpenInvoices/OpenBills `Number` tiebreak; CreditActivity type-`Order`+`Id` tiebreak; both builders; determinism tests). ✓
- Item 4 (asOf culture-parse) → Task 2 (`TryParseExact` InvariantCulture in both endpoints; ISO-accepts / non-ISO-rejects tests). ✓
- Item 2 (over-allocation message) → Task 3 (additive `allocationWarning` + template line in all four editors; warn-and-disable / clears-when-valid tests). ✓
- Item 1 (keyboard-bypass) → explicitly dropped in the plan intro and spec. ✓

**Placeholder scan:** none — every code and test step contains full code.

**Type consistency:** `allocationWarning` named identically across all four editors and their tests. Builder sort keys (`Order`, `Id`) are local tuple fields, not DTO changes. Endpoint signatures unchanged. Fixture accessors (`SeedSodClientAsync`, `CreateCustomerRequest`, `CreateVendorRequest`) match the existing test files read during planning.
