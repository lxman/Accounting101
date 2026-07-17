# Statement & Credit-Activity Drill-In Implementation Plan (Slice 2c-3b)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the statement-of-account and credit-activity rows on the Customer 360 (AR) and Vendor 360 (AP) screens drill into the underlying document's detail screen on whole-row click/Enter — completing the drill-in arc (every target already exists).

**Architecture:** Additive, read-only enrichment. Append `Guid Id` + `string Kind` (a route-aligned lowercase slug) to the four line records (`StatementLine`/`CreditActivityLine` in both `CustomerAccountView` and `VendorAccountView`), threading each from the source document already in scope in the pure-fold builders. Frontend: add `id`/`kind` to the FE interfaces, a per-component `open(line)` that switches on `kind` to the existing route, and whole-row click/keydown on both tables. No endpoints, caps, routes, or detail screens change.

**Tech Stack:** .NET 10 (pure-function builders, xUnit); Angular 22 (standalone, OnPush, zoneless), Tailwind v4; Vitest + TestBed (frontend).

## Global Constraints

- **Backend:** namespaces follow folder structure. New record fields are **appended** (additive wire; host `JsonNamingPolicy.CamelCase` → `id`, `kind`). Display `Type` strings are **unchanged**. No endpoint signature changes. **Rider auto-converts explicit types to `var`** — stage the explicit file list per task and check `git diff --cached --stat` for stray churn before each commit.
- **`Kind` vocabulary (exact slugs — the load-bearing contract):**
  - AR — `invoice` (Invoice), `payment` (Payment; both the statement "Payment" and the credit-activity "Overpayment"), `credit-note` (CreditNote), `write-off` (WriteOff), `credit-application` (CreditApplication), `refund` (Refund).
  - AP — `bill` (Bill), `payment` (BillPayment; statement "Payment" and credit-activity "Overpayment"), `credit-application` (VendorCreditApplication).
  - The AR credit slugs (`credit-note`/`write-off`/`credit-application`) are exactly the existing FE `CreditType` union values and double as the `:type` segment of `credits/:type/:id`.
- **FE→route mapping:** AR — `invoice`→`/receivables/invoices/:id`, `payment`→`/receivables/payments/:id`, `refund`→`/receivables/refunds/:id`, `credit-note`|`write-off`|`credit-application`→`/receivables/credits/:type/:id` (type = the slug). AP — `bill`→`/payables/bills/:id`, `payment`→`/payables/payments/:id`, `credit-application`→`/payables/credits/:id`.
- **Frontend:** standalone, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. Rows **unconditionally** clickable (same-area — the account screen already requires the area read cap; every target is same-area). No `*appCan` on the rows. FE test runner is **Vitest** (`vi.spyOn` global; nav spies `.mockResolvedValue(true)`).
- **Wire shapes** identical backend record ↔ FE interface: `StatementLine{ date, type, reference, charge, payment, balance, id: string, kind: string }`; `CreditActivityLine{ date, type, reference, amount, creditBalance, id: string, kind: string }`.
- Only touch the files named per task. Do NOT touch detail screens (2a–2c-3a, done), other modules, or the open-invoices/open-bills tables. Do NOT change amounts, relief math, sort order, or display labels.
- `environment.ts` stays modified/uncommitted (local dev config, never commit).
- Branch `feat/statement-drill-in`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: AR backend — enrich StatementLine/CreditActivityLine with Id + Kind

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/CustomerAccountView.cs` (add `Id`/`Kind` to the two records)
- Modify: `Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs` (thread `Id`/`Kind` through `Statement` + `CreditActivity`)
- Test (extend): `Modules/Receivables/Accounting101.Receivables.Tests/CustomerAccountBuilderTests.cs`

**Interfaces:**
- Consumes: `Invoice`, `Payment`, `CreditNote`, `WriteOff`, `CreditApplication`, `Refund` (each with `.Id`), the existing `reliefByDocument` dictionary.
- Produces: `StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance, Guid Id, string Kind)`; `CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance, Guid Id, string Kind)`. Kind slugs per Global Constraints.

- [ ] **Step 1: Write the failing tests**

Append these two tests to `CustomerAccountBuilderTests.cs` (before the closing brace of the class; match the file's explicit-type style):
```csharp
    [Fact]
    public void Statement_carries_each_rows_source_id_and_kind()
    {
        Guid i = Guid.NewGuid(), p = Guid.NewGuid(), n = Guid.NewGuid(), w = Guid.NewGuid(), c = Guid.NewGuid();
        Invoice invoice = IssuedInvoice(i, "1001", new(2026, 3, 1), null, 1000m);
        List<Payment> payments = [new() { Id = p, CustomerId = Guid.NewGuid(), Date = new(2026, 3, 2), Amount = 100m }];
        List<CreditNote> notes = [new() { Id = n, CustomerId = Guid.NewGuid(), Date = new(2026, 3, 3) }];
        List<WriteOff> writeOffs = [new() { Id = w, CustomerId = Guid.NewGuid(), Date = new(2026, 3, 4) }];
        List<CreditApplication> apps = [new() { Id = c, CustomerId = Guid.NewGuid(), Date = new(2026, 3, 5) }];
        Dictionary<Guid, decimal> relief = new() { [p] = 100m };

        IReadOnlyList<StatementLine> lines = CustomerAccountBuilder.Statement([invoice], payments, notes, writeOffs, apps, relief);

        Assert.Equal((i, "invoice"), (lines[0].Id, lines[0].Kind));
        Assert.Equal((p, "payment"), (lines[1].Id, lines[1].Kind));
        Assert.Equal((n, "credit-note"), (lines[2].Id, lines[2].Kind));
        Assert.Equal((w, "write-off"), (lines[3].Id, lines[3].Kind));
        Assert.Equal((c, "credit-application"), (lines[4].Id, lines[4].Kind));
        Assert.Equal("Invoice", lines[0].Type);   // display label unchanged
    }

    [Fact]
    public void CreditActivity_carries_source_id_and_kind_overpayment_maps_to_payment()
    {
        Guid p = Guid.NewGuid(), c = Guid.NewGuid(), r = Guid.NewGuid();
        List<Payment> payments = [new() { Id = p, CustomerId = Guid.NewGuid(), Date = new(2026, 3, 2), Amount = 150m }]; // 150 unapplied
        List<CreditApplication> apps = [new() { Id = c, CustomerId = Guid.NewGuid(), Date = new(2026, 3, 10) }];
        List<Refund> refunds = [new() { Id = r, CustomerId = Guid.NewGuid(), Date = new(2026, 3, 20), Amount = 20m }];
        Dictionary<Guid, decimal> relief = new() { [c] = 30m };

        IReadOnlyList<CreditActivityLine> lines = CustomerAccountBuilder.CreditActivity(payments, apps, refunds, relief);

        Assert.Equal((p, "payment"), (lines[0].Id, lines[0].Kind));    // Overpayment row → its payment
        Assert.Equal("Overpayment", lines[0].Type);                    // display label unchanged
        Assert.Equal((c, "credit-application"), (lines[1].Id, lines[1].Kind));
        Assert.Equal((r, "refund"), (lines[2].Id, lines[2].Kind));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~CustomerAccountBuilderTests"`
Expected: BUILD FAILURE — `StatementLine`/`CreditActivityLine` have no `Id`/`Kind` member.

- [ ] **Step 3: Add `Id`/`Kind` to the two records**

In `CustomerAccountView.cs`, replace the two record declarations (lines 21 and 24):
```csharp
/// <summary>One AR statement line. Charge increases the running balance; Payment decreases it.
/// Id/Kind identify the source document for drill-in (Kind is a route-aligned slug).</summary>
public sealed record StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance, Guid Id, string Kind);

/// <summary>One credit-ledger line. Amount is signed (+ overpayment, − application/refund); CreditBalance is the running total.
/// Id/Kind identify the source document for drill-in (an Overpayment row's Kind is "payment").</summary>
public sealed record CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance, Guid Id, string Kind);
```

- [ ] **Step 4: Thread `Id`/`Kind` through `Statement`**

In `CustomerAccountBuilder.cs`, replace the body of `Statement` (lines 47–69) — add `Id`/`Kind` to the raw tuple and the final projection; ordering and balance math unchanged:
```csharp
        List<(DateOnly Date, int Order, Guid Id, string Kind, string Type, string? Reference, decimal Charge, decimal Payment)> raw = [];
        foreach (Invoice i in invoices.Where(i => i.Status == InvoiceStatus.Issued))
            raw.Add((i.IssueDate, 0, i.Id, "invoice", "Invoice", i.Number, i.Total, 0m));
        // Settlement.Payment column = the document's AR relief (total cash/credit applied to invoices).
        // The running balance subtracts each settlement's relief in full, while ArBalance floors each
        // invoice's open balance at 0 via Settlement.OpenBalance; these agree as long as allocations never
        // over-apply an invoice (enforced upstream by allocation validation).
        foreach (Payment p in payments.Where(p => !p.Voided))
            raw.Add((p.Date, 1, p.Id, "payment", "Payment", null, 0m, reliefByDocument.GetValueOrDefault(p.Id)));
        foreach (CreditNote n in creditNotes.Where(n => !n.Voided))
            raw.Add((n.Date, 1, n.Id, "credit-note", "Credit note", n.Memo, 0m, reliefByDocument.GetValueOrDefault(n.Id)));
        foreach (WriteOff w in writeOffs.Where(w => !w.Voided))
            raw.Add((w.Date, 1, w.Id, "write-off", "Write-off", w.Memo, 0m, reliefByDocument.GetValueOrDefault(w.Id)));
        foreach (CreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "credit-application", "Credit applied", null, 0m, reliefByDocument.GetValueOrDefault(c.Id)));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order)
            .Select(r =>
            {
                balance += r.Charge - r.Payment;
                return new StatementLine(r.Date, r.Type, r.Reference, r.Charge, r.Payment, balance, r.Id, r.Kind);
            }).ToList();
```

- [ ] **Step 5: Thread `Id`/`Kind` through `CreditActivity`**

In `CustomerAccountBuilder.cs`, replace the body of `CreditActivity` (lines 81–98) — the raw tuple already carries `Guid Id`; add `Kind` beside it:
```csharp
        List<(DateOnly Date, int Order, Guid Id, string Kind, string Type, string? Reference, decimal Amount)> raw = [];
        foreach (Payment p in payments.Where(p => !p.Voided))
        {
            decimal unapplied = p.Amount - reliefByDocument.GetValueOrDefault(p.Id);
            if (unapplied > 0m) raw.Add((p.Date, 0, p.Id, "payment", "Overpayment", null, unapplied));
        }
        foreach (CreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "credit-application", "Credit applied", null, -reliefByDocument.GetValueOrDefault(c.Id)));
        foreach (Refund r in refunds.Where(r => !r.Voided))
            raw.Add((r.Date, 2, r.Id, "refund", "Refund", r.Memo, -r.Amount));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order).ThenBy(r => r.Id)
            .Select(r =>
            {
                balance += r.Amount;
                return new CreditActivityLine(r.Date, r.Type, r.Reference, r.Amount, balance, r.Id, r.Kind);
            }).ToList();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~CustomerAccountBuilderTests"`
Expected: PASS (2 new + all pre-existing builder tests — balances/ordering unchanged).

- [ ] **Step 7: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables/CustomerAccountView.cs Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs Modules/Receivables/Accounting101.Receivables.Tests/CustomerAccountBuilderTests.cs
git commit -m "feat(receivables): carry source id + kind on statement/credit-activity lines for drill-in"
```

**Note:** the account-view service/endpoint that calls the builder does not change — the appended record fields flow through the existing `CustomerAccountView`. If the solution has any other direct constructor of `StatementLine`/`CreditActivityLine` (grep `new StatementLine(` / `new CreditActivityLine(` under `Modules/Receivables`), it must pass the two new args; include that file in the commit. (None expected — only the builder constructs them.)

---

### Task 2: AP backend — enrich StatementLine/CreditActivityLine with Id + Kind

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/VendorAccountView.cs` (add `Id`/`Kind` to the two records)
- Modify: `Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs` (thread `Id`/`Kind` through `Statement` + `CreditActivity`)
- Test (extend): `Modules/Payables/Accounting101.Payables.Tests/VendorAccountBuilderTests.cs`

**Interfaces:**
- Consumes: `Bill`, `BillPayment`, `VendorCreditApplication` (each with `.Id`), the existing `reliefByDocument` dictionary.
- Produces: `StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance, Guid Id, string Kind)`; `CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance, Guid Id, string Kind)`. AP Kind slugs: `bill`, `payment`, `credit-application`.

- [ ] **Step 1: Write the failing tests**

Append these two tests to `VendorAccountBuilderTests.cs` (before the class closing brace; match the file's `var` style and reuse its `EnteredBill`/`Payment`/`CreditApp` helpers):
```csharp
    [Fact]
    public void Statement_carries_each_rows_source_id_and_kind()
    {
        Guid billId = Guid.NewGuid();
        var bills = new List<Bill> { EnteredBill(billId, 100m, new DateOnly(2026, 3, 1), null) };
        BillPayment payment = Payment(40m, new DateOnly(2026, 3, 2));
        VendorCreditApplication creditApp = CreditApp(new DateOnly(2026, 3, 3));
        var relief = new Dictionary<Guid, decimal> { [payment.Id] = 40m };

        var lines = VendorAccountBuilder.Statement(bills, [payment], [creditApp], relief);

        Assert.Equal((billId, "bill"), (lines[0].Id, lines[0].Kind));
        Assert.Equal((payment.Id, "payment"), (lines[1].Id, lines[1].Kind));
        Assert.Equal((creditApp.Id, "credit-application"), (lines[2].Id, lines[2].Kind));
        Assert.Equal("Bill", lines[0].Type);   // display label unchanged
    }

    [Fact]
    public void CreditActivity_carries_source_id_and_kind_overpayment_maps_to_payment()
    {
        BillPayment payment = Payment(150m, new DateOnly(2026, 3, 1));   // 150 unapplied → overpayment
        VendorCreditApplication creditApp = CreditApp(new DateOnly(2026, 3, 5));
        var relief = new Dictionary<Guid, decimal> { [creditApp.Id] = 20m };

        var lines = VendorAccountBuilder.CreditActivity([payment], [creditApp], relief);

        Assert.Equal((payment.Id, "payment"), (lines[0].Id, lines[0].Kind));   // Overpayment → its payment
        Assert.Equal("Overpayment", lines[0].Type);                            // display label unchanged
        Assert.Equal((creditApp.Id, "credit-application"), (lines[1].Id, lines[1].Kind));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorAccountBuilderTests"`
Expected: BUILD FAILURE — `StatementLine`/`CreditActivityLine` have no `Id`/`Kind` member.

- [ ] **Step 3: Add `Id`/`Kind` to the two records**

In `VendorAccountView.cs`, replace the two record declarations (lines 13 and 15):
```csharp
public sealed record StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance, Guid Id, string Kind);

public sealed record CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance, Guid Id, string Kind);
```

- [ ] **Step 4: Thread `Id`/`Kind` through `Statement`**

In `VendorAccountBuilder.cs`, replace the body of `Statement` (lines 42–56):
```csharp
        List<(DateOnly Date, int Order, Guid Id, string Kind, string Type, string? Reference, decimal Charge, decimal Payment)> raw = [];
        foreach (Bill b in bills.Where(b => b.Status == BillStatus.Entered))
            raw.Add((b.BillDate, 0, b.Id, "bill", "Bill", b.Number, b.Total, 0m));
        foreach (BillPayment p in payments.Where(p => !p.Voided))
            raw.Add((p.Date, 1, p.Id, "payment", "Payment", null, 0m, reliefByDocument.GetValueOrDefault(p.Id)));
        foreach (VendorCreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "credit-application", "Credit applied", null, 0m, reliefByDocument.GetValueOrDefault(c.Id)));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order)
            .Select(r =>
            {
                balance += r.Charge - r.Payment;
                return new StatementLine(r.Date, r.Type, r.Reference, r.Charge, r.Payment, balance, r.Id, r.Kind);
            }).ToList();
```

- [ ] **Step 5: Thread `Id`/`Kind` through `CreditActivity`**

In `VendorAccountBuilder.cs`, replace the body of `CreditActivity` (lines 63–78):
```csharp
        List<(DateOnly Date, int Order, Guid Id, string Kind, string Type, string? Reference, decimal Amount)> raw = [];
        foreach (BillPayment p in payments.Where(p => !p.Voided))
        {
            decimal overpayment = p.Amount - reliefByDocument.GetValueOrDefault(p.Id);
            if (overpayment > 0m) raw.Add((p.Date, 0, p.Id, "payment", "Overpayment", null, overpayment));
        }
        foreach (VendorCreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, c.Id, "credit-application", "Credit applied", null, -reliefByDocument.GetValueOrDefault(c.Id)));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order).ThenBy(r => r.Id)
            .Select(r =>
            {
                balance += r.Amount;
                return new CreditActivityLine(r.Date, r.Type, r.Reference, r.Amount, balance, r.Id, r.Kind);
            }).ToList();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorAccountBuilderTests"`
Expected: PASS (2 new + all pre-existing builder tests).

- [ ] **Step 7: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/VendorAccountView.cs Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs Modules/Payables/Accounting101.Payables.Tests/VendorAccountBuilderTests.cs
git commit -m "feat(payables): carry source id + kind on vendor statement/credit-activity lines for drill-in"
```

**Note:** as in Task 1, grep `new StatementLine(` / `new CreditActivityLine(` under `Modules/Payables` for any other constructor; none expected beyond the builder.

---

### Task 3: FE AR — customer-account whole-row drill-in

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.ts` (add `id`/`kind` to `StatementLine` + `CreditActivityLine`)
- Modify: `UI/Angular/src/app/features/receivables/customer-account.ts` (inject `Router`, add `open`, wire both `<tr>`)
- Test (extend): `UI/Angular/src/app/features/receivables/customer-account.spec.ts`

**Interfaces:**
- Consumes: Task 1's wire shape (`id`/`kind` on the two lines); `Router`; the existing receivables routes `invoices/:id`, `payments/:id`, `credits/:type/:id`, `refunds/:id`.
- Produces: nothing downstream.

- [ ] **Step 1: Write the failing test**

In `customer-account.spec.ts`:

**1a.** Add `Router` to the router import (line 3):
```ts
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
```

**1b.** Add `id`/`kind` to the existing `view()` mock's lines so the render test's `track` stays clean — change the `statementLines`/`creditLines` lines (24–25) to:
```ts
  statementLines: [{ date: '2026-03-01', type: 'Invoice', reference: '1001', charge: 1000, payment: 0, balance: 1000, id: 'i1', kind: 'invoice' }],
  creditLines: [{ date: '2026-03-18', type: 'Overpayment', reference: null, amount: 100, creditBalance: 100, id: 'p1', kind: 'payment' }],
```

**1c.** Add this test inside `describe('CustomerAccount', ...)` (`vi` is global):
```ts
  it('drills each statement and credit-activity row into its document detail', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(CustomerAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/account').flush({
      customer: { id: 'cu1', name: 'Acme Co', email: null }, arBalance: 0, creditBalance: 0,
      aging: { current: 0, d1To30: 0, d31To60: 0, d61To90: 0, d90Plus: 0 },
      openInvoices: [],
      statementLines: [
        { date: '2026-03-01', type: 'Invoice', reference: '1001', charge: 1000, payment: 0, balance: 1000, id: 'i1', kind: 'invoice' },
        { date: '2026-03-02', type: 'Payment', reference: null, charge: 0, payment: 100, balance: 900, id: 'p1', kind: 'payment' },
        { date: '2026-03-03', type: 'Credit note', reference: null, charge: 0, payment: 50, balance: 850, id: 'n1', kind: 'credit-note' },
        { date: '2026-03-04', type: 'Write-off', reference: null, charge: 0, payment: 25, balance: 825, id: 'w1', kind: 'write-off' },
        { date: '2026-03-05', type: 'Credit applied', reference: null, charge: 0, payment: 10, balance: 815, id: 'c1', kind: 'credit-application' },
      ],
      creditLines: [
        { date: '2026-03-06', type: 'Overpayment', reference: null, amount: 40, creditBalance: 40, id: 'p2', kind: 'payment' },
        { date: '2026-03-07', type: 'Refund', reference: null, amount: -20, creditBalance: 20, id: 'r1', kind: 'refund' },
      ],
    });
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const rows = [...f.nativeElement.querySelectorAll('tbody tr')] as HTMLElement[];   // openInvoices empty → 5 statement + 2 credit
    rows.forEach(r => r.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    expect(nav.mock.calls.map(c => c[0])).toEqual([
      ['/receivables/invoices', 'i1'],
      ['/receivables/payments', 'p1'],
      ['/receivables/credits', 'credit-note', 'n1'],
      ['/receivables/credits', 'write-off', 'w1'],
      ['/receivables/credits', 'credit-application', 'c1'],
      ['/receivables/payments', 'p2'],
      ['/receivables/refunds', 'r1'],
    ]);
  });
```

- [ ] **Step 2: Run the spec to verify it fails**

Run (from `UI/Angular`): `npx ng test --include='**/customer-account.spec.ts' --watch=false`
Expected: the new spec FAILS — rows not clickable → `navigate` not called. (The render test still passes.)

- [ ] **Step 3: Add `id`/`kind` to the FE interfaces**

In `receivables.ts`, replace lines 81–82:
```ts
export interface StatementLine { date: string; type: string; reference: string | null; charge: number; payment: number; balance: number; id: string; kind: string; }
export interface CreditActivityLine { date: string; type: string; reference: string | null; amount: number; creditBalance: number; id: string; kind: string; }
```

- [ ] **Step 4: Inject `Router` and add `open`**

In `customer-account.ts`:

**4a.** Change the router import (line 2) to add `Router`:
```ts
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
```

**4b.** Inject `Router` — after `private readonly route = inject(ActivatedRoute);` (line 104):
```ts
  private readonly router = inject(Router);
```

**4c.** Add the `open` method after `fmtDate` (line 120):
```ts
  open(line: { kind: string; id: string }): void {
    const k = line.kind;
    const path =
      k === 'invoice' ? ['/receivables/invoices', line.id]
      : k === 'payment' ? ['/receivables/payments', line.id]
      : k === 'refund' ? ['/receivables/refunds', line.id]
      : (k === 'credit-note' || k === 'write-off' || k === 'credit-application') ? ['/receivables/credits', k, line.id]
      : null;
    if (path) void this.router.navigate(path);
  }
```

- [ ] **Step 5: Make both tables' rows clickable**

In `customer-account.ts`, change the **statement** loop+row (lines 63–64) to:
```html
                    @for (s of a.statementLines; track s.id) {
                      <tr role="button" tabindex="0" class="cursor-pointer hover:bg-muted/50"
                          (click)="open(s)" (keydown.enter)="open(s)">
```

Change the **credit-activity** loop+row (lines 84–85) to:
```html
                  @for (c of a.creditLines; track c.id) {
                    <tr role="button" tabindex="0" class="cursor-pointer hover:bg-muted/50"
                        (click)="open(c)" (keydown.enter)="open(c)">
```

- [ ] **Step 6: Run the spec + compile gate**

Run (from `UI/Angular`): `npx ng test --include='**/customer-account.spec.ts' --watch=false` → both specs PASS.
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/receivables/receivables.ts UI/Angular/src/app/features/receivables/customer-account.ts UI/Angular/src/app/features/receivables/customer-account.spec.ts
git commit -m "feat(ui): customer-account statement + credit-activity whole-row drill-in"
```

---

### Task 4: FE AP — vendor-account whole-row drill-in

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.ts` (add `id`/`kind` to `StatementLine` + `CreditActivityLine`)
- Modify: `UI/Angular/src/app/features/payables/vendor-account.ts` (inject `Router`, add `open`, wire both `<tr>`)
- Test (extend): `UI/Angular/src/app/features/payables/vendor-account.spec.ts`

**Interfaces:**
- Consumes: Task 2's wire shape (`id`/`kind`); `Router`; the existing payables routes `bills/:id`, `payments/:id`, `credits/:id`.
- Produces: nothing downstream.

- [ ] **Step 1: Write the failing test**

In `vendor-account.spec.ts`:

**1a.** Add `Router` to the router import (line 3):
```ts
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
```

**1b.** Add `id`/`kind` to the existing `view()` mock's lines (24–25):
```ts
  statementLines: [{ date: '2026-02-01', type: 'Bill', reference: 'B-2', charge: 800, payment: 0, balance: 800, id: 'b2', kind: 'bill' }],
  creditLines: [{ date: '2026-03-15', type: 'Overpayment', reference: null, amount: 200, creditBalance: 200, id: 'p1', kind: 'payment' }],
```

**1c.** Add this test inside `describe('VendorAccount', ...)`:
```ts
  it('drills each statement and credit-activity row into its document detail', () => {
    const ctrl = setup('v1');
    const f = TestBed.createComponent(VendorAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/account').flush({
      vendor: { id: 'v1', name: 'Acme Parts', email: null }, apBalance: 0, creditBalance: 0,
      aging: { current: 0, d1To30: 0, d31To60: 0, d61To90: 0, d90Plus: 0 },
      openBills: [],
      statementLines: [
        { date: '2026-03-01', type: 'Bill', reference: 'BILL-00001', charge: 1000, payment: 0, balance: 1000, id: 'b1', kind: 'bill' },
        { date: '2026-03-02', type: 'Payment', reference: null, charge: 0, payment: 100, balance: 900, id: 'p1', kind: 'payment' },
        { date: '2026-03-03', type: 'Credit applied', reference: null, charge: 0, payment: 60, balance: 840, id: 'c1', kind: 'credit-application' },
      ],
      creditLines: [
        { date: '2026-03-04', type: 'Overpayment', reference: null, amount: 40, creditBalance: 40, id: 'p2', kind: 'payment' },
        { date: '2026-03-05', type: 'Credit applied', reference: null, amount: -10, creditBalance: 30, id: 'c2', kind: 'credit-application' },
      ],
    });
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const rows = [...f.nativeElement.querySelectorAll('tbody tr')] as HTMLElement[];   // openBills empty → 3 statement + 2 credit
    rows.forEach(r => r.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    expect(nav.mock.calls.map(c => c[0])).toEqual([
      ['/payables/bills', 'b1'],
      ['/payables/payments', 'p1'],
      ['/payables/credits', 'c1'],
      ['/payables/payments', 'p2'],
      ['/payables/credits', 'c2'],
    ]);
  });
```

- [ ] **Step 2: Run the spec to verify it fails**

Run (from `UI/Angular`): `npx ng test --include='**/vendor-account.spec.ts' --watch=false`
Expected: the new spec FAILS — rows not clickable.

- [ ] **Step 3: Add `id`/`kind` to the FE interfaces**

In `payables.ts`, replace lines 77–78:
```ts
export interface StatementLine { date: string; type: string; reference: string | null; charge: number; payment: number; balance: number; id: string; kind: string; }
export interface CreditActivityLine { date: string; type: string; reference: string | null; amount: number; creditBalance: number; id: string; kind: string; }
```

- [ ] **Step 4: Inject `Router` and add `open`**

In `vendor-account.ts`:

**4a.** Change the router import (line 2):
```ts
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
```

**4b.** Inject `Router` — after `private readonly route = inject(ActivatedRoute);` (line 104):
```ts
  private readonly router = inject(Router);
```

**4c.** Add the `open` method after `fmtDate` (line 120):
```ts
  open(line: { kind: string; id: string }): void {
    const k = line.kind;
    const path =
      k === 'bill' ? ['/payables/bills', line.id]
      : k === 'payment' ? ['/payables/payments', line.id]
      : k === 'credit-application' ? ['/payables/credits', line.id]
      : null;
    if (path) void this.router.navigate(path);
  }
```

- [ ] **Step 5: Make both tables' rows clickable**

In `vendor-account.ts`, change the **statement** loop+row (lines 63–64) to:
```html
                    @for (s of a.statementLines; track s.id) {
                      <tr role="button" tabindex="0" class="cursor-pointer hover:bg-muted/50"
                          (click)="open(s)" (keydown.enter)="open(s)">
```

Change the **credit-activity** loop+row (lines 84–85) to:
```html
                  @for (c of a.creditLines; track c.id) {
                    <tr role="button" tabindex="0" class="cursor-pointer hover:bg-muted/50"
                        (click)="open(c)" (keydown.enter)="open(c)">
```

- [ ] **Step 6: Run the spec + compile gate**

Run (from `UI/Angular`): `npx ng test --include='**/vendor-account.spec.ts' --watch=false` → both specs PASS.
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.ts UI/Angular/src/app/features/payables/vendor-account.ts UI/Angular/src/app/features/payables/vendor-account.spec.ts
git commit -m "feat(ui): vendor-account statement + credit-activity whole-row drill-in"
```

---

## Self-Review

**Spec coverage:**
- Backend `Id` + `Kind` on all four line records, threaded from in-scope source docs, Posted/relief/sort math unchanged → Tasks 1 (AR) + 2 (AP). ✓
- `Kind` vocabulary exact slugs; Overpayment → `payment`; AR "Credit applied" → `credit-application`; display `Type` unchanged → Tasks 1/2 tests assert id + kind + unchanged Type. ✓
- Additive wire, no endpoint change (builder feeds the existing view record) → Tasks 1/2 notes. ✓
- FE `id`/`kind` interfaces + `open(line)` switch + whole-row click/keydown on both tables + `track row.id`, unconditional (no `*appCan`) → Tasks 3 (AR) + 4 (AP). ✓
- FE→route mapping (AR invoices/payments/refunds + credits/:type/:id; AP bills/payments + credits/:id) → Tasks 3/4 `open()` + nav tests assert the exact route arrays including Overpayment→payments and AR credit kinds→credits/:type/:id. ✓

**Placeholder scan:** every step contains complete code; no TBD.

**Type/name consistency:** record field order `(…, Guid Id, string Kind)` identical AR ↔ AP and matches FE `{…, id, kind}`; `Kind` slugs identical to the FE `open()` switch cases and to the existing `CreditType` union (`credit-note`/`write-off`/`credit-application`); route arrays in the nav tests match each `open()` branch and the existing route table (`credits/:type/:id` AR, `credits/:id` AP); `open(line: { kind: string; id: string })` accepts both `StatementLine` and `CreditActivityLine`. Backend tuple additions keep the existing `OrderBy/ThenBy` (Statement: Date→Order; CreditActivity: Date→Order→Id) so ordering and running balances are unchanged.

## Execution Handoff

Two execution options:
1. **Subagent-Driven (recommended)** — fresh implementer per task, per-task review, final whole-branch review.
2. **Inline Execution** — execute in this session with checkpoints.
