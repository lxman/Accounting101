# Customer Account (360) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A read-only Customer Account ("360") screen at `/receivables/customers/:id` showing one customer's header balances, AR aging, open invoices, AR running-balance statement, and credit-activity ledger — backed by one aggregate `GET /customers/{id}/account` endpoint.

**Architecture:** A backend `CustomerAccountService` reads the customer's existing documents (invoices, payments, credit-apps, write-offs, credit-notes, refunds) and folds them — via pure static builder functions — into a `CustomerAccountView`. The aging/statement/credit math is server-authoritative and unit-tested in isolation. The UI is one component that calls the endpoint and renders the sections; customer-list rows become clickable to reach it.

**Tech Stack:** ASP.NET Core minimal APIs (.NET 10) + Angular 22 (standalone, signals, zoneless) + Spartan-ng helm + Tailwind. Backend tests: xUnit (`dotnet test`). UI tests: `@angular/build:unit-test` (vitest) via `npm test`.

## Global Constraints

- **Currency:** USD-only; money via `money()` from `core/format/display`, dates via `displayDate()`.
- **Read-only screen.** No edit/void actions here (the customer *editor* is a separate future slice).
- **All computations exclude voided documents.**
- **Authorization:** the read endpoint uses the same Read authorization as the other module GETs (under the existing `RequireAuthorization()` `clients` group; no module credential).
- **`asOf`:** optional `DateOnly` query param; defaults to the server's today (`DateOnly.FromDateTime(DateTime.UtcNow)`). Present for deterministic aging in tests and a future date picker; v1 UI omits it.
- **Aging buckets** (by days past due, where `DaysOverdue = max(0, asOf − DueDate)`, and `0` when `DueDate` is null): `Current` (0), `D1To30` (1–30), `D31To60` (31–60), `D61To90` (61–90), `D90Plus` (≥ 91). Bucket sums equal `ArBalance`.
- **Reconciliation invariants:** aging buckets sum to `ArBalance`; the statement's final running balance equals `ArBalance`; the credit ledger's final running balance equals `CreditBalance`.
- **View-model records live in the core `Accounting101.Receivables` project** (like `CreditDocument` — the service returns them and the Api project references core, not vice-versa).
- **Exact API base in tests:** `http://localhost:5000`.

---

## File Structure

**Backend (`Modules/Receivables/`):**
- `Accounting101.Receivables/CustomerAccountView.cs` (new) — the view-model records.
- `Accounting101.Receivables/CustomerAccountBuilder.cs` (new) — pure static fold functions.
- `Accounting101.Receivables/CustomerAccountService.cs` (new) — reads stores, calls builders.
- `Accounting101.Receivables.Api/ReceivablesEndpoints.cs` — `GetCustomerAccount` handler + route.
- `Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs` — register `CustomerAccountService`.
- `Accounting101.Receivables.Tests/CustomerAccountBuilderTests.cs` (new) — pure unit tests.
- `Accounting101.Receivables.Tests/GetCustomerAccountEndpointTests.cs` (new) — E2E.

**UI (`UI/Angular/src/app/`):**
- `core/receivables/receivables.ts` — the five interfaces.
- `core/receivables/receivables.service.ts` — `getCustomerAccount`.
- `core/receivables/receivables.service.spec.ts` — service test.
- `features/receivables/customer-account.ts` (+ `.spec.ts`) — the screen.
- `features/receivables/customer-list.ts` (+ `.spec.ts`) — clickable rows.
- `app.routes.ts` — `customers/:id` route.

---

## Task 1: Backend — view-model records + pure builder functions (unit-tested)

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables/CustomerAccountView.cs`
- Create: `Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/CustomerAccountBuilderTests.cs`

**Interfaces:**
- Consumes: domain types `Invoice` (computed `Total`, `Status`, `IssueDate`, `DueDate`, `Number`), `Payment` (`Allocations`, `Unapplied`, `Voided`, `Date`), `CreditApplication` (`Allocations`, `Applied`, `Voided`, `Date`), `WriteOff`/`CreditNote` (`Allocations`, `Total`, `Voided`, `Date`, `Memo`), `Refund` (`Amount`, `Voided`, `Date`); `Allocation(TargetId, Amount)`; `InvoiceStatus.Issued`; `Settlement.OpenBalance`.
- Produces (in namespace `Accounting101.Receivables`):
  - records `CustomerAccountView`, `AgingBuckets`, `OpenInvoiceLine`, `StatementLine`, `CreditActivityLine`.
  - `static class CustomerAccountBuilder` with: `AppliedByInvoice(...)`, `OpenInvoices(...)`, `Aging(...)`, `Statement(...)`, `CreditActivity(...)`, `ArBalance(...)`.

- [ ] **Step 1: Create the view-model records**

Create `Modules/Receivables/Accounting101.Receivables/CustomerAccountView.cs`:

```csharp
namespace Accounting101.Receivables;

/// <summary>The full read-only account view for one customer: header balances, AR aging, open invoices,
/// the AR running-balance statement, and the credit-activity ledger. Server-computed; nothing stored.</summary>
public sealed record CustomerAccountView(
    Customer Customer,
    decimal ArBalance,
    decimal CreditBalance,
    AgingBuckets Aging,
    IReadOnlyList<OpenInvoiceLine> OpenInvoices,
    IReadOnlyList<StatementLine> StatementLines,
    IReadOnlyList<CreditActivityLine> CreditLines);

/// <summary>Open AR bucketed by days past due. Sums to <see cref="CustomerAccountView.ArBalance"/>.</summary>
public sealed record AgingBuckets(decimal Current, decimal D1To30, decimal D31To60, decimal D61To90, decimal D90Plus);

/// <summary>One open (issued, not fully settled) invoice with its age.</summary>
public sealed record OpenInvoiceLine(Guid InvoiceId, string? Number, DateOnly IssueDate, DateOnly? DueDate, decimal OpenBalance, int DaysOverdue);

/// <summary>One AR statement line. Charge increases the running balance; Payment decreases it.</summary>
public sealed record StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance);

/// <summary>One credit-ledger line. Amount is signed (+ overpayment, − application/refund); CreditBalance is the running total.</summary>
public sealed record CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance);
```

- [ ] **Step 2: Write the failing builder unit tests**

Create `Modules/Receivables/Accounting101.Receivables.Tests/CustomerAccountBuilderTests.cs`:

```csharp
using Accounting101.Receivables;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Pure-function tests for the customer-account folds: applied-per-invoice, open invoices + age,
/// aging buckets, the AR statement running balance, and the credit-activity ledger. No host needed.</summary>
public sealed class CustomerAccountBuilderTests
{
    private static Invoice IssuedInvoice(Guid id, string number, DateOnly issue, DateOnly? due, decimal amount) => new()
    {
        Id = id, CustomerId = Guid.NewGuid(), Number = number, IssueDate = issue, DueDate = due,
        Status = InvoiceStatus.Issued, TaxRate = 0m,
        Lines = [new InvoiceLine { Description = "x", Quantity = 1m, UnitPrice = amount, Taxable = false }],
    };

    [Fact]
    public void AppliedByInvoice_sums_nonvoided_allocations_across_doc_types()
    {
        Guid inv = Guid.NewGuid();
        List<Payment> payments =
        [
            new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 2), Amount = 40m, Allocations = [new Allocation(inv, 40m)] },
            new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 3), Amount = 10m, Allocations = [new Allocation(inv, 10m)], Voided = true },
        ];
        List<CreditNote> notes = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 4), Allocations = [new Allocation(inv, 25m)] }];

        Dictionary<Guid, decimal> applied = CustomerAccountBuilder.AppliedByInvoice(payments, [], [], notes);

        Assert.Equal(65m, applied[inv]);   // 40 (payment) + 25 (note); voided 10 excluded
    }

    [Fact]
    public void OpenInvoices_computes_open_balance_and_days_overdue()
    {
        Guid inv = Guid.NewGuid();
        Invoice invoice = IssuedInvoice(inv, "1001", new(2026, 3, 1), new(2026, 3, 31), 100m);
        Dictionary<Guid, decimal> applied = new() { [inv] = 30m };

        IReadOnlyList<OpenInvoiceLine> open = CustomerAccountBuilder.OpenInvoices([invoice], applied, asOf: new(2026, 4, 20));

        OpenInvoiceLine line = Assert.Single(open);
        Assert.Equal(70m, line.OpenBalance);     // 100 - 30
        Assert.Equal(20, line.DaysOverdue);      // 2026-04-20 minus 2026-03-31
    }

    [Fact]
    public void OpenInvoices_excludes_fully_paid_and_uses_zero_overdue_when_no_due_date()
    {
        Guid paid = Guid.NewGuid(), noDue = Guid.NewGuid();
        Invoice paidInv = IssuedInvoice(paid, "1001", new(2026, 3, 1), new(2026, 3, 31), 100m);
        Invoice noDueInv = IssuedInvoice(noDue, "1002", new(2026, 3, 1), null, 50m);
        Dictionary<Guid, decimal> applied = new() { [paid] = 100m };   // fully paid

        IReadOnlyList<OpenInvoiceLine> open = CustomerAccountBuilder.OpenInvoices([paidInv, noDueInv], applied, asOf: new(2026, 6, 1));

        OpenInvoiceLine line = Assert.Single(open);                   // paid one excluded
        Assert.Equal(noDue, line.InvoiceId);
        Assert.Equal(0, line.DaysOverdue);                            // null due date → 0
    }

    [Fact]
    public void Aging_buckets_by_days_overdue_and_sums_to_ar_balance()
    {
        List<OpenInvoiceLine> lines =
        [
            new(Guid.NewGuid(), "a", new(2026, 1, 1), null, 100m, 0),     // Current
            new(Guid.NewGuid(), "b", new(2026, 1, 1), null, 200m, 15),    // 1-30
            new(Guid.NewGuid(), "c", new(2026, 1, 1), null, 300m, 45),    // 31-60
            new(Guid.NewGuid(), "d", new(2026, 1, 1), null, 400m, 75),    // 61-90
            new(Guid.NewGuid(), "e", new(2026, 1, 1), null, 500m, 120),   // 90+
        ];

        AgingBuckets aging = CustomerAccountBuilder.Aging(lines);

        Assert.Equal(100m, aging.Current);
        Assert.Equal(200m, aging.D1To30);
        Assert.Equal(300m, aging.D31To60);
        Assert.Equal(400m, aging.D61To90);
        Assert.Equal(500m, aging.D90Plus);
        Assert.Equal(1500m, aging.Current + aging.D1To30 + aging.D31To60 + aging.D61To90 + aging.D90Plus);
    }

    [Fact]
    public void Statement_orders_by_date_charges_first_with_running_balance()
    {
        Guid i1 = Guid.NewGuid(), i2 = Guid.NewGuid();
        Invoice inv1 = IssuedInvoice(i1, "1001", new(2026, 3, 1), null, 1000m);
        Invoice inv2 = IssuedInvoice(i2, "1002", new(2026, 3, 25), null, 1500m);
        List<Payment> payments = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 15), Amount = 400m, Allocations = [new Allocation(i1, 400m)] }];
        List<CreditNote> notes = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 20), Allocations = [new Allocation(i1, 200m)] }];

        IReadOnlyList<StatementLine> lines = CustomerAccountBuilder.Statement([inv1, inv2], payments, notes, [], []);

        Assert.Equal(4, lines.Count);
        Assert.Equal(1000m, lines[0].Charge); Assert.Equal(1000m, lines[0].Balance);   // 3/1 invoice
        Assert.Equal(400m, lines[1].Payment); Assert.Equal(600m, lines[1].Balance);    // 3/15 payment
        Assert.Equal(200m, lines[2].Payment); Assert.Equal(400m, lines[2].Balance);    // 3/20 credit note
        Assert.Equal(1500m, lines[3].Charge); Assert.Equal(1900m, lines[3].Balance);   // 3/25 invoice
    }

    [Fact]
    public void CreditActivity_signs_and_runs_to_final_balance()
    {
        List<Payment> payments = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 2), Amount = 150m, Allocations = [new Allocation(Guid.NewGuid(), 50m)] }]; // 100 unapplied
        List<CreditApplication> apps = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 10), Allocations = [new Allocation(Guid.NewGuid(), 30m)] }];
        List<Refund> refunds = [new() { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new(2026, 3, 20), Amount = 20m }];

        IReadOnlyList<CreditActivityLine> lines = CustomerAccountBuilder.CreditActivity(payments, apps, refunds);

        Assert.Equal(3, lines.Count);
        Assert.Equal(100m, lines[0].Amount); Assert.Equal(100m, lines[0].CreditBalance);   // overpayment +100
        Assert.Equal(-30m, lines[1].Amount); Assert.Equal(70m, lines[1].CreditBalance);    // applied -30
        Assert.Equal(-20m, lines[2].Amount); Assert.Equal(50m, lines[2].CreditBalance);    // refund -20
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~CustomerAccountBuilderTests"`
Expected: FAIL — `CustomerAccountBuilder` does not exist (compile error).

- [ ] **Step 4: Implement the builder**

Create `Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs`:

```csharp
using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>Pure folds that turn a customer's stored documents into the account view's parts. Every fold
/// ignores voided documents and is deterministic given its inputs (aging takes an explicit asOf).</summary>
public static class CustomerAccountBuilder
{
    /// <summary>Total non-voided amount applied to each invoice across payments, credit-applications,
    /// write-offs, and credit-notes (their allocations).</summary>
    public static Dictionary<Guid, decimal> AppliedByInvoice(
        IReadOnlyList<Payment> payments, IReadOnlyList<CreditApplication> creditApps,
        IReadOnlyList<WriteOff> writeOffs, IReadOnlyList<CreditNote> creditNotes)
    {
        Dictionary<Guid, decimal> applied = new();
        void Add(IEnumerable<Allocation> allocs)
        {
            foreach (Allocation a in allocs) applied[a.TargetId] = applied.GetValueOrDefault(a.TargetId) + a.Amount;
        }
        Add(payments.Where(p => !p.Voided).SelectMany(p => p.Allocations));
        Add(creditApps.Where(c => !c.Voided).SelectMany(c => c.Allocations));
        Add(writeOffs.Where(w => !w.Voided).SelectMany(w => w.Allocations));
        Add(creditNotes.Where(n => !n.Voided).SelectMany(n => n.Allocations));
        return applied;
    }

    /// <summary>Issued invoices with a positive open balance, each with days overdue (0 when not yet due
    /// or no due date), oldest issue first.</summary>
    public static IReadOnlyList<OpenInvoiceLine> OpenInvoices(
        IReadOnlyList<Invoice> invoices, IReadOnlyDictionary<Guid, decimal> applied, DateOnly asOf) =>
        invoices.Where(i => i.Status == InvoiceStatus.Issued)
            .Select(i =>
            {
                decimal open = Settlement.Settlement.OpenBalance(i.Total, applied.GetValueOrDefault(i.Id));
                int overdue = i.DueDate is { } due ? Math.Max(0, asOf.DayNumber - due.DayNumber) : 0;
                return new OpenInvoiceLine(i.Id, i.Number, i.IssueDate, i.DueDate, open, overdue);
            })
            .Where(l => l.OpenBalance > 0m)
            .OrderBy(l => l.IssueDate).ToList();

    public static AgingBuckets Aging(IReadOnlyList<OpenInvoiceLine> openInvoices)
    {
        decimal cur = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0;
        foreach (OpenInvoiceLine l in openInvoices)
        {
            if (l.DaysOverdue <= 0) cur += l.OpenBalance;
            else if (l.DaysOverdue <= 30) b1 += l.OpenBalance;
            else if (l.DaysOverdue <= 60) b2 += l.OpenBalance;
            else if (l.DaysOverdue <= 90) b3 += l.OpenBalance;
            else b4 += l.OpenBalance;
        }
        return new AgingBuckets(cur, b1, b2, b3, b4);
    }

    public static decimal ArBalance(IReadOnlyList<OpenInvoiceLine> openInvoices) => openInvoices.Sum(l => l.OpenBalance);

    /// <summary>The AR statement: a charge per issued invoice, a settlement line per non-voided payment /
    /// credit-note / write-off / credit-application (amount = its allocations), oldest first with charges
    /// before settlements on the same date, carrying a running AR balance.</summary>
    public static IReadOnlyList<StatementLine> Statement(
        IReadOnlyList<Invoice> invoices, IReadOnlyList<Payment> payments,
        IReadOnlyList<CreditNote> creditNotes, IReadOnlyList<WriteOff> writeOffs,
        IReadOnlyList<CreditApplication> creditApps)
    {
        List<(DateOnly Date, int Order, string Type, string? Reference, decimal Charge, decimal Payment)> raw = [];
        foreach (Invoice i in invoices.Where(i => i.Status == InvoiceStatus.Issued))
            raw.Add((i.IssueDate, 0, "Invoice", i.Number, i.Total, 0m));
        foreach (Payment p in payments.Where(p => !p.Voided))
            raw.Add((p.Date, 1, "Payment", null, 0m, p.Allocations.Sum(a => a.Amount)));
        foreach (CreditNote n in creditNotes.Where(n => !n.Voided))
            raw.Add((n.Date, 1, "Credit note", n.Memo, 0m, n.Allocations.Sum(a => a.Amount)));
        foreach (WriteOff w in writeOffs.Where(w => !w.Voided))
            raw.Add((w.Date, 1, "Write-off", w.Memo, 0m, w.Allocations.Sum(a => a.Amount)));
        foreach (CreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, "Credit applied", null, 0m, c.Allocations.Sum(a => a.Amount)));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order)
            .Select(r =>
            {
                balance += r.Charge - r.Payment;
                return new StatementLine(r.Date, r.Type, r.Reference, r.Charge, r.Payment, balance);
            }).ToList();
    }

    /// <summary>The credit ledger: overpayment remainders (+), credit-applications (−), refunds (−), oldest
    /// first, with a running credit balance that ends at the customer's unapplied credit.</summary>
    public static IReadOnlyList<CreditActivityLine> CreditActivity(
        IReadOnlyList<Payment> payments, IReadOnlyList<CreditApplication> creditApps, IReadOnlyList<Refund> refunds)
    {
        List<(DateOnly Date, string Type, string? Reference, decimal Amount)> raw = [];
        foreach (Payment p in payments.Where(p => !p.Voided && p.Unapplied > 0m))
            raw.Add((p.Date, "Overpayment", null, p.Unapplied));
        foreach (CreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, "Credit applied", null, -c.Applied));
        foreach (Refund r in refunds.Where(r => !r.Voided))
            raw.Add((r.Date, "Refund", r.Memo, -r.Amount));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date)
            .Select(r =>
            {
                balance += r.Amount;
                return new CreditActivityLine(r.Date, r.Type, r.Reference, r.Amount, balance);
            }).ToList();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~CustomerAccountBuilderTests"`
Expected: PASS (all six facts).

- [ ] **Step 6: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables/CustomerAccountView.cs Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs Modules/Receivables/Accounting101.Receivables.Tests/CustomerAccountBuilderTests.cs
git commit -m "feat(receivables): customer-account view model + pure account-fold builders

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Backend — `CustomerAccountService` + endpoint + E2E

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables/CustomerAccountService.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (route list ~line 28, near the other `/customers` routes; handler near `ListCredits`)
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs` (register the service, next to `services.AddScoped<PaymentService>();`)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/GetCustomerAccountEndpointTests.cs`

**Interfaces:**
- Consumes: `ICustomerStore.GetAsync(clientId, customerId, ct)` → `Customer?`; `IInvoiceStore.GetByCustomerAsync(clientId, customerId, ct)` → `IReadOnlyList<Invoice>`; `IPaymentStore` reads (`GetPaymentsByCustomerAsync`, `GetCreditApplicationsByCustomerAsync`, `GetWriteOffsByCustomerAsync`, `GetCreditNotesByCustomerAsync`, `GetRefundsByCustomerAsync`); `CustomerAccountBuilder` (Task 1).
- Produces:
  - `CustomerAccountService.GetAccountAsync(Guid clientId, Guid customerId, DateOnly asOf, CancellationToken ct = default) : Task<CustomerAccountView?>` (null when the customer doesn't exist).
  - `GET /clients/{clientId}/customers/{customerId}/account?asOf=` → `200` with the view, `404` when the customer is unknown, `400` on an unparseable `asOf`.

- [ ] **Step 1: Write the failing E2E endpoint test**

Create `Modules/Receivables/Accounting101.Receivables.Tests/GetCustomerAccountEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;   // AccountRequest
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the aggregate customer-account endpoint reconciles: AR balance = Σ open, aging sums to
/// it, the statement's running balance ends at AR balance, the credit ledger ends at the credit balance,
/// and an unknown customer is 404.</summary>
public sealed class GetCustomerAccountEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", "Customer");
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
        await PutAccountAsync(controller, clientId, fixture.SalesReturnsAccountId, "4900", "Sales Returns", "Revenue", null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();

    private static async Task<Guid> IssueInvoiceAsync(HttpClient clerk, Guid clientId, Guid customerId, decimal amount, DateOnly due)
    {
        DraftInvoiceRequest draftRequest = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: due, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        return issued.Id;
    }

    [Fact]
    public async Task GET_account_reconciles_balances_aging_statement_and_credit()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", "stark@x.com")))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv1 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 1000m, new DateOnly(2026, 3, 31));
        Guid inv2 = await IssueInvoiceAsync(clerk, clientId, customer.Id, 1500m, new DateOnly(2026, 4, 30));

        // partial payment 400 on inv1; credit note 200 on inv1 → inv1 open 400, inv2 open 1500. AR = 1900.
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 15), 400m, "check", [new Allocation(inv1, 400m)]))).EnsureSuccessStatusCode();
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 20), [new Allocation(inv1, 200m)], "goodwill"))).EnsureSuccessStatusCode();

        // overpay inv2-unrelated to create credit, then apply 30 and refund 20 → credit balance 50.
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 18), 100m, "check", []))).EnsureSuccessStatusCode();  // 100 unapplied → credit
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
            new CreditApplicationRequest(customer.Id, new DateOnly(2026, 3, 22), [new Allocation(inv2, 30m)]))).EnsureSuccessStatusCode();
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer.Id, new DateOnly(2026, 3, 25), 20m, "back"))).EnsureSuccessStatusCode();

        CustomerAccountView view = (await clerk.GetFromJsonAsync<CustomerAccountView>(
            $"/clients/{clientId}/customers/{customer.Id}/account?asOf=2026-05-15"))!;

        // inv1 open = 1000 - 400 - 200 = 400; inv2 open = 1500 - 30 = 1470. AR = 1870.
        Assert.Equal(1870m, view.ArBalance);
        Assert.Equal(view.ArBalance, view.Aging.Current + view.Aging.D1To30 + view.Aging.D31To60 + view.Aging.D61To90 + view.Aging.D90Plus);
        Assert.Equal(view.ArBalance, view.StatementLines[^1].Balance);     // statement ends at AR balance
        Assert.Equal(50m, view.CreditBalance);                             // 100 - 30 - 20
        Assert.Equal(view.CreditBalance, view.CreditLines[^1].CreditBalance);
        Assert.Equal("stark@x.com", view.Customer.Email);
        Assert.All(view.OpenInvoices, l => Assert.True(l.OpenBalance > 0m));
    }

    [Fact]
    public async Task GET_account_for_unknown_customer_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/customers/{Guid.NewGuid()}/account");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~GetCustomerAccountEndpointTests"`
Expected: FAIL — the `/account` route returns 404 for the known customer too (route not mapped), or compile error if `CustomerAccountView` deserialization can't bind yet.

- [ ] **Step 3: Implement `CustomerAccountService`**

Create `Modules/Receivables/Accounting101.Receivables/CustomerAccountService.cs`:

```csharp
namespace Accounting101.Receivables;

/// <summary>Assembles the read-only <see cref="CustomerAccountView"/> for one customer by reading its
/// documents and folding them with <see cref="CustomerAccountBuilder"/>. Read-only; computes, never stores.</summary>
public sealed class CustomerAccountService(ICustomerStore customers, IInvoiceStore invoices, IPaymentStore payments)
{
    public async Task<CustomerAccountView?> GetAccountAsync(
        Guid clientId, Guid customerId, DateOnly asOf, CancellationToken ct = default)
    {
        Customer? customer = await customers.GetAsync(clientId, customerId, ct);
        if (customer is null) return null;

        IReadOnlyList<Invoice> invs = await invoices.GetByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<WriteOff> ws = await payments.GetWriteOffsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditNote> ns = await payments.GetCreditNotesByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<Refund> rs = await payments.GetRefundsByCustomerAsync(clientId, customerId, ct);

        Dictionary<Guid, decimal> applied = CustomerAccountBuilder.AppliedByInvoice(ps, cs, ws, ns);
        IReadOnlyList<OpenInvoiceLine> open = CustomerAccountBuilder.OpenInvoices(invs, applied, asOf);
        decimal credit = ps.Where(p => !p.Voided).Sum(p => p.Unapplied)
                         - cs.Where(c => !c.Voided).Sum(c => c.Applied)
                         - rs.Where(r => !r.Voided).Sum(r => r.Amount);

        return new CustomerAccountView(
            customer,
            CustomerAccountBuilder.ArBalance(open),
            credit,
            CustomerAccountBuilder.Aging(open),
            open,
            CustomerAccountBuilder.Statement(invs, ps, ns, ws, cs),
            CustomerAccountBuilder.CreditActivity(ps, cs, rs));
    }
}
```

> The `credit` formula mirrors `PaymentService.GetCustomerCreditBalanceAsync` exactly (non-voided payment remainders − credit-applications − refunds). It is recomputed here (rather than calling `PaymentService`) because this service already reads the same documents — no extra round-trip, no service-on-service coupling.

- [ ] **Step 4: Register the service**

In `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs`, add next to `services.AddScoped<PaymentService>();`:

```csharp
        services.AddScoped<CustomerAccountService>();
```

- [ ] **Step 5: Add the endpoint route + handler**

In `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs`, register the route in `MapReceivablesEndpoints` (next to `clients.MapGet("/customers/{customerId:guid}/credit-balance", GetCreditBalance);`):

```csharp
        clients.MapGet("/customers/{customerId:guid}/account", GetCustomerAccount);
```

Add the handler (near `ListCredits`):

```csharp
    private static async Task<IResult> GetCustomerAccount(
        Guid clientId, Guid customerId, string? asOf, CustomerAccountService service, CancellationToken cancellationToken)
    {
        DateOnly date;
        if (string.IsNullOrEmpty(asOf))
            date = DateOnly.FromDateTime(DateTime.UtcNow);
        else if (!DateOnly.TryParse(asOf, out date))
            return Results.Problem("asOf must be a date (yyyy-MM-dd).", statusCode: StatusCodes.Status400BadRequest);

        CustomerAccountView? view = await service.GetAccountAsync(clientId, customerId, date, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~GetCustomerAccountEndpointTests"`
Expected: PASS (both facts).

- [ ] **Step 7: Run the full module suite**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`
Expected: PASS — all green (no regressions).

- [ ] **Step 8: Commit**

```bash
git add Modules/Receivables/
git commit -m "feat(receivables): GET /customers/{id}/account aggregate endpoint + service

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: UI model & service

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.ts`
- Modify: `UI/Angular/src/app/core/receivables/receivables.service.ts`
- Test: `UI/Angular/src/app/core/receivables/receivables.service.spec.ts`

**Interfaces:**
- Consumes: existing `Customer` interface; `base()` + `clientId()` guard.
- Produces (model): `AgingBuckets`, `OpenInvoiceLine`, `StatementLine`, `CreditActivityLine`, `CustomerAccountView` (camelCase mirrors of the backend records). Service: `getCustomerAccount(customerId: string): Observable<CustomerAccountView>` → `GET /customers/{id}/account`.

- [ ] **Step 1: Write the failing service test**

In `UI/Angular/src/app/core/receivables/receivables.service.spec.ts`, add to the `ReceivablesService` describe block:

```typescript
  it('getCustomerAccount GETs /customers/{id}/account', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    let result: { arBalance: number } | undefined;
    svc.getCustomerAccount('cu1').subscribe(v => (result = v));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/account');
    expect(req.request.method).toBe('GET');
    req.flush({
      customer: { id: 'cu1', name: 'Acme Co', email: null }, arBalance: 1900, creditBalance: 50,
      aging: { current: 0, d1to30: 0, d31to60: 0, d61to90: 0, d90plus: 1900 },
      openInvoices: [], statementLines: [], creditLines: [],
    });
    expect(result!.arBalance).toBe(1900);
  });
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd UI/Angular && npm test`
Expected: FAIL — `svc.getCustomerAccount` is not a function.

- [ ] **Step 3: Add the model interfaces**

In `UI/Angular/src/app/core/receivables/receivables.ts`, append:

```typescript
export interface AgingBuckets { current: number; d1to30: number; d31to60: number; d61to90: number; d90plus: number; }
export interface OpenInvoiceLine { invoiceId: string; number: string | null; issueDate: string; dueDate: string | null; openBalance: number; daysOverdue: number; }
export interface StatementLine { date: string; type: string; reference: string | null; charge: number; payment: number; balance: number; }
export interface CreditActivityLine { date: string; type: string; reference: string | null; amount: number; creditBalance: number; }
export interface CustomerAccountView {
  customer: Customer; arBalance: number; creditBalance: number; aging: AgingBuckets;
  openInvoices: OpenInvoiceLine[]; statementLines: StatementLine[]; creditLines: CreditActivityLine[];
}
```

- [ ] **Step 4: Add the service method**

In `UI/Angular/src/app/core/receivables/receivables.service.ts`, extend the model import (the `from './receivables'` line) to include `CustomerAccountView`, then add (after `voidRefund`, before the closing brace):

```typescript
  getCustomerAccount(customerId: string): Observable<CustomerAccountView> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<CustomerAccountView>(this.base(`/customers/${customerId}/account`));
  }
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `cd UI/Angular && npm test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/receivables/
git commit -m "feat(ui): customer-account model + getCustomerAccount service

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: UI — `CustomerAccount` screen + route

**Files:**
- Create: `UI/Angular/src/app/features/receivables/customer-account.ts`
- Test: `UI/Angular/src/app/features/receivables/customer-account.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (import + `customers/:id` route)

**Interfaces:**
- Consumes: `ReceivablesService.getCustomerAccount`; `CustomerAccountView` + parts; `money`/`displayDate`; `extractProblem`; `HlmTableImports`, `HlmButton`; `ActivatedRoute`, `RouterLink`.
- Produces: `CustomerAccount` at route `receivables/customers/:id`.

- [ ] **Step 1: Write the failing component tests**

Create `UI/Angular/src/app/features/receivables/customer-account.spec.ts`:

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CustomerAccount } from './customer-account';
import { ClientContextService } from '../../core/client/client-context.service';

function setup(id: string) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: (k: string) => (k === 'id' ? id : null) } } } },
    ],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const view = () => ({
  customer: { id: 'cu1', name: 'Acme Co', email: 'acme@x.com' }, arBalance: 1900, creditBalance: 50,
  aging: { current: 100, d1to30: 200, d31to60: 300, d61to90: 400, d90plus: 900 },
  openInvoices: [{ invoiceId: 'i1', number: '1001', issueDate: '2026-03-01', dueDate: '2026-03-31', openBalance: 400, daysOverdue: 45 }],
  statementLines: [{ date: '2026-03-01', type: 'Invoice', reference: '1001', charge: 1000, payment: 0, balance: 1000 }],
  creditLines: [{ date: '2026-03-18', type: 'Overpayment', reference: null, amount: 100, creditBalance: 100 }],
});

describe('CustomerAccount', () => {
  it('renders header, aging, open invoices, statement, and credit activity', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(CustomerAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/account').flush(view());
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('Acme Co');
    expect(text).toContain('acme@x.com');
    expect(text).toContain('1,900.00');     // AR balance
    expect(text).toContain('1001');          // open invoice + statement ref
    expect(text).toContain('Overpayment');   // credit activity
  });

  it('relays a not-found error', () => {
    const ctrl = setup('nope');
    const f = TestBed.createComponent(CustomerAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/nope/account').flush(
      { type: 'about:blank', title: 'Not Found', detail: 'Customer not found.', status: 404 },
      { status: 404, statusText: 'Not Found' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Customer not found.');
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test`
Expected: FAIL — cannot find module `./customer-account`.

- [ ] **Step 3: Implement `CustomerAccount`**

Create `UI/Angular/src/app/features/receivables/customer-account.ts`:

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { CustomerAccountView } from '../../core/receivables/receivables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-customer-account',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <a routerLink="/receivables/customers" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Customers</a>
      @if (error()) {
        <p class="text-destructive text-sm">{{ error() }}</p>
      } @else if (account(); as a) {
        <div class="flex flex-wrap items-baseline gap-x-6 gap-y-1">
          <h1 class="text-2xl font-bold">{{ a.customer.name }}</h1>
          <span class="text-sm text-muted-foreground">{{ a.customer.email ?? '—' }}</span>
          <span class="ms-auto text-sm">AR balance <span class="font-semibold tabular-nums">{{ money(a.arBalance) }}</span></span>
          <span class="text-sm">Credit <span class="font-semibold tabular-nums">{{ money(a.creditBalance) }}</span></span>
        </div>

        <div class="flex flex-wrap gap-4 text-sm">
          <div>Current <span class="tabular-nums">{{ money(a.aging.current) }}</span></div>
          <div>1–30 <span class="tabular-nums">{{ money(a.aging.d1to30) }}</span></div>
          <div>31–60 <span class="tabular-nums">{{ money(a.aging.d31to60) }}</span></div>
          <div>61–90 <span class="tabular-nums">{{ money(a.aging.d61to90) }}</span></div>
          <div [class.text-destructive]="a.aging.d90plus > 0">90+ <span class="tabular-nums">{{ money(a.aging.d90plus) }}</span></div>
        </div>

        <div class="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div class="lg:col-span-2 flex flex-col gap-4">
            <section class="flex flex-col gap-1">
              <h2 class="font-semibold text-sm">Open invoices</h2>
              @if (a.openInvoices.length === 0) { <p class="text-sm text-muted-foreground">No open invoices.</p> }
              @else {
                <table class="w-full text-sm">
                  <thead><tr class="text-left text-muted-foreground"><th class="py-1">Number</th><th>Issued</th><th>Due</th><th class="text-right">Open</th><th class="text-right">Overdue</th></tr></thead>
                  <tbody>
                    @for (l of a.openInvoices; track l.invoiceId) {
                      <tr [class.text-destructive]="l.daysOverdue > 0">
                        <td class="py-1">{{ l.number ?? '—' }}</td><td>{{ fmtDate(l.issueDate) }}</td>
                        <td>{{ l.dueDate ? fmtDate(l.dueDate) : '—' }}</td>
                        <td class="text-right tabular-nums">{{ money(l.openBalance) }}</td>
                        <td class="text-right tabular-nums">{{ l.daysOverdue > 0 ? l.daysOverdue + 'd' : '—' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            </section>

            <section class="flex flex-col gap-1">
              <h2 class="font-semibold text-sm">Statement of account</h2>
              @if (a.statementLines.length === 0) { <p class="text-sm text-muted-foreground">No statement activity.</p> }
              @else {
                <table class="w-full text-sm">
                  <thead><tr class="text-left text-muted-foreground"><th class="py-1">Date</th><th>Type</th><th>Ref</th><th class="text-right">Charge</th><th class="text-right">Payment</th><th class="text-right">Balance</th></tr></thead>
                  <tbody>
                    @for (s of a.statementLines; track $index) {
                      <tr>
                        <td class="py-1">{{ fmtDate(s.date) }}</td><td>{{ s.type }}</td><td>{{ s.reference ?? '—' }}</td>
                        <td class="text-right tabular-nums">{{ s.charge ? money(s.charge) : '' }}</td>
                        <td class="text-right tabular-nums">{{ s.payment ? money(s.payment) : '' }}</td>
                        <td class="text-right tabular-nums font-medium">{{ money(s.balance) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            </section>
          </div>

          <section class="flex flex-col gap-1">
            <h2 class="font-semibold text-sm">Credit activity</h2>
            @if (a.creditLines.length === 0) { <p class="text-sm text-muted-foreground">No credit activity.</p> }
            @else {
              <table class="w-full text-sm">
                <thead><tr class="text-left text-muted-foreground"><th class="py-1">Date</th><th>Type</th><th class="text-right">Amount</th><th class="text-right">Balance</th></tr></thead>
                <tbody>
                  @for (c of a.creditLines; track $index) {
                    <tr>
                      <td class="py-1">{{ fmtDate(c.date) }}</td><td>{{ c.type }}</td>
                      <td class="text-right tabular-nums" [class.text-destructive]="c.amount < 0">{{ money(c.amount) }}</td>
                      <td class="text-right tabular-nums font-medium">{{ money(c.creditBalance) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </section>
        </div>
      } @else {
        <p class="text-sm text-muted-foreground">Loading…</p>
      }
    </div>
  `,
})
export class CustomerAccount {
  private readonly svc = inject(ReceivablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly account = signal<CustomerAccountView | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) { this.error.set('No customer.'); return; }
    this.svc.getCustomerAccount(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: v => this.account.set(v),
      error: e => this.error.set(extractProblem(e).detail),
    });
  }

  money(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
```

> `HlmTableImports` is imported for consistency with the other list screens even though these tables are lightweight `<table>` elements; if the linter flags it as unused, drop it from `imports` and the import line. Verify against how `payment-editor.ts` uses plain tables.

- [ ] **Step 4: Add the route**

In `UI/Angular/src/app/app.routes.ts`, add the import (near the other receivables imports):
```typescript
import { CustomerAccount } from './features/receivables/customer-account';
```
And the child route inside the `receivables` children, **after** the `customers` route (so the bare list isn't shadowed):
```typescript
    { path: 'customers/:id', component: CustomerAccount },
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test`
Expected: PASS — both `CustomerAccount` tests green.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/customer-account.ts UI/Angular/src/app/features/receivables/customer-account.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): CustomerAccount (360) screen + route

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: UI — make customer-list rows clickable

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/customer-list.ts`
- Test: `UI/Angular/src/app/features/receivables/customer-list.spec.ts`

**Interfaces:**
- Consumes: `Router`; the customer rows from `svc.customers()`.
- Produces: clicking (or Enter on) a customer row navigates to `['/receivables/customers', id]`.

- [ ] **Step 1: Write the failing test**

In `UI/Angular/src/app/features/receivables/customer-list.spec.ts`, add (match the file's existing TestBed setup; if it has a `setup()` helper, use it; otherwise mirror the providers from `payment-list.spec.ts`):

```typescript
  it('clicking a customer row navigates to the account screen', () => {
    const ctrl = setup();   // returns HttpTestingController; selects client C1
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(CustomerList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    const row = f.nativeElement.querySelector('[data-testid="customer-row"]') as HTMLElement;
    row.click();
    expect(nav).toHaveBeenCalledWith(['/receivables/customers', 'cu1']);
  });
```

> Add the necessary imports to the spec: `Router` from `@angular/router`, `CustomerList` from `./customer-list`. If the spec lacks a `setup()` helper, add one mirroring `payment-list.spec.ts` (providers: `provideZonelessChangeDetection`, `provideRouter([])`, `provideHttpClient`, `provideHttpClientTesting`; select `C1`) and return the `HttpTestingController`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd UI/Angular && npm test`
Expected: FAIL — no element with `data-testid="customer-row"` / no navigation.

- [ ] **Step 3: Make rows clickable**

In `UI/Angular/src/app/features/receivables/customer-list.ts`:

Add `Router` to the imports and inject it. At the top, change:
```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
```
to also import `Router`:
```typescript
import { Router } from '@angular/router';
```
In the class, add the field:
```typescript
  private readonly router = inject(Router);
```
And the method:
```typescript
  open(id: string): void { void this.router.navigate(['/receivables/customers', id]); }
```

Then update the row markup in the `@for (c of svc.customers(); track c.id)` block — make the row a clickable, keyboard-accessible element:
```html
      @for (c of svc.customers(); track c.id) {
        <div data-testid="customer-row"
             class="flex items-center gap-3 py-1 border-b border-border/50 text-sm cursor-pointer hover:bg-muted/50"
             role="button" tabindex="0"
             (click)="open(c.id)" (keydown.enter)="open(c.id)">
          <span>{{ c.name }}</span><span class="text-muted-foreground">{{ c.email }}</span>
        </div>
      }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd UI/Angular && npm test`
Expected: PASS — clicking a row navigates.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/receivables/customer-list.ts UI/Angular/src/app/features/receivables/customer-list.spec.ts
git commit -m "feat(ui): customer-list rows open the customer account (whole-row click)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Run the full backend solution tests**

Run: `dotnet test`
Expected: PASS — full solution green.

- [ ] **Step 2: Run the full UI suite**

Run: `cd UI/Angular && npm test`
Expected: PASS — all specs green.

- [ ] **Step 3: Build the UI**

Run: `cd UI/Angular && npm run build`
Expected: build succeeds with no type errors (a pre-existing bundle-budget WARNING is fine).

- [ ] **Step 4: Manual smoke (optional, dev stack running)**

Receivables → Customers → click a customer → the account screen shows header balances, aging, open invoices, statement, and credit activity; the figures reconcile (aging sums to AR balance, statement ends at AR balance).

---

## Self-Review

**1. Spec coverage:**
- Navigation: clickable customer rows + `customers/:id` route → Tasks 4 & 5. ✅
- Aggregate `GET /customers/{id}/account?asOf=` (404 unknown, 400 bad asOf) → Task 2. ✅
- View model (header balances, aging, open invoices, statement, credit) → Task 1 records + Task 2 assembly. ✅
- Aging definition (days past due, no-due-date → Current, buckets) → Task 1 `OpenInvoices`/`Aging`. ✅
- AR statement (charge per issued invoice, settlement per doc, charges-before-settlements same date, running balance) → Task 1 `Statement`. ✅
- Credit activity (overpayment +, credit-app −, refund −, running balance) → Task 1 `CreditActivity`. ✅
- Reconciliation invariants → Task 2 E2E asserts them. ✅
- Voided excluded → Task 1 builders + Task 1 unit test. ✅
- UI screen sections + empty/error states → Task 4. ✅
- UI model + service → Task 3. ✅
- Deferred (date-range picker, export, editor, drill-through) → out of scope, not planned. ✅

**2. Placeholder scan:** All steps contain concrete code/commands. The two `>` notes (the `credit` recompute rationale; the `HlmTableImports`-unused guard) are guidance, not TODOs.

**3. Type consistency:** Record/interface field names match backend↔UI (`arBalance/creditBalance/aging/openInvoices/statementLines/creditLines`; `AgingBuckets` `current/d1to30/d31to60/d61to90/d90plus`). Builder method names (`AppliedByInvoice`/`OpenInvoices`/`Aging`/`ArBalance`/`Statement`/`CreditActivity`) match between Task 1 (definition) and Task 2 (consumption). `GetAccountAsync` signature matches between service (Task 2 Step 3) and endpoint (Task 2 Step 5). Service method `getCustomerAccount` matches between Task 3 and Task 4.

**Note for the implementer:** The pure builders (Task 1) are the heart and are fully unit-tested; everything else wires them up and mirrors shipped patterns (CreditDocument-in-core placement, the ListPayments-style endpoint, the clickable-row convention from invoice-list).
