# Vendor Account 360 UI — Slice P-D Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A read-only per-vendor 360 screen at `/payables/vendors/:id` — header balances, AP aging, open bills, AP statement, credit-activity ledger — assembled by one aggregate backend endpoint built from pure folds.

**Architecture:** Mirror the shipped customer 360. Backend: a pure `VendorAccountBuilder` (in the payables core project) + view records, a `VendorAccountService` reading the existing stores, and a `GET /vendors/{id}/account` endpoint. Frontend: additive core interfaces + service method, the `vendor-account` screen, a vendor-list row-click change, and the `vendors/:id` route.

**Tech Stack:** C# / .NET 10 / xUnit / EphemeralMongo (backend); Angular 22 standalone components, signals, Vitest via `ng test` (frontend).

## Global Constraints

- Commit trailer on EVERY commit, exactly: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- UI test runner is **`ng test`** (`@angular/build:unit-test`), NOT raw vitest. Scoped run: `npx ng test --include="<spec path>" --watch=false`. NEVER create a `vitest.config.ts`.
- Stage ONLY the files each task changes — never `git add -A`/`commit -am`. The working tree has an unrelated `UI/Angular/src/app/core/api/environment.ts` (devClientId) change that must NOT be committed.
- **camelCase wire-key trap:** `AgingBuckets` fields `D1To30/D31To60/D61To90/D90Plus` serialize to `d1To30/d31To60/d61To90/d90Plus` (interior capital `T` preserved by `JsonNamingPolicy.CamelCase`). The UI interface MUST use `d1To30` (not `d1to30`). A serialization-guard test pins these keys.
- The 360 **credit balance** is `Σ non-voided payment.Unapplied − Σ non-voided creditApp.Applied` — the SAME formula as `BillPaymentService.GetVendorCreditBalanceAsync`, so the credit ledger reconciles.
- All folds ignore voided documents; aging takes an explicit `asOf`.
- Angular components: `ChangeDetectionStrategy.OnPush`, standalone; `takeUntilDestroyed` on inline `.subscribe()`; `provideZonelessChangeDetection()` in specs.
- Branch off `master`; ff-merge + push + delete branch on "merge and push".

---

### Task 1: Backend — VendorAccountBuilder + view records + unit/serialization tests

**Files:**
- Create: `Modules/Payables/Accounting101.Payables/VendorAccountView.cs` (records)
- Create: `Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs` (pure folds)
- Test (create): `Modules/Payables/Accounting101.Payables.Tests/VendorAccountBuilderTests.cs`
- Test (create): `Modules/Payables/Accounting101.Payables.Tests/PayablesAgingBucketsSerializationTests.cs`

**Interfaces:**
- Produces records: `VendorAccountView`, `AgingBuckets`, `OpenBillLine`, `StatementLine`, `CreditActivityLine` (namespace `Accounting101.Payables`).
- Produces `VendorAccountBuilder` static methods: `AppliedByBill`, `OpenBills`, `Aging`, `ApBalance`, `Statement`, `CreditActivity`.

- [ ] **Step 1: Write the failing builder + serialization tests**

Create `VendorAccountBuilderTests.cs`:

```csharp
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

public sealed class VendorAccountBuilderTests
{
    private static Bill EnteredBill(Guid id, decimal amount, DateOnly billDate, DateOnly? due, string? number = "B-1") =>
        new()
        {
            Id = id, VendorId = Guid.NewGuid(), Number = number, BillDate = billDate, DueDate = due,
            Status = BillStatus.Entered, Lines = [new BillLine { Description = "x", Amount = amount, ExpenseAccountId = Guid.NewGuid() }],
        };

    private static BillPayment Payment(Guid billId, decimal amount, decimal alloc, DateOnly date, bool voided = false) =>
        new() { Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = date, Amount = amount, Method = null,
                Allocations = [new Allocation(billId, alloc)], Voided = voided };

    private static VendorCreditApplication CreditApp(Guid billId, decimal alloc, DateOnly date, bool voided = false) =>
        new() { Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = date, Allocations = [new Allocation(billId, alloc)], Voided = voided };

    [Fact]
    public void AppliedByBill_sums_nonvoided_payment_and_credit_allocations()
    {
        Guid bill = Guid.NewGuid();
        var applied = VendorAccountBuilder.AppliedByBill(
            [Payment(bill, 100m, 80m, new DateOnly(2026, 3, 1)), Payment(bill, 50m, 50m, new DateOnly(2026, 3, 2), voided: true)],
            [CreditApp(bill, 20m, new DateOnly(2026, 3, 3))]);
        Assert.Equal(100m, applied[bill]); // 80 + 20; the voided 50 excluded
    }

    [Fact]
    public void OpenBills_keeps_only_entered_with_positive_open_oldest_first_with_overdue()
    {
        Guid b1 = Guid.NewGuid(), b2 = Guid.NewGuid();
        var bills = new List<Bill> {
            EnteredBill(b2, 100m, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)),  // older billDate
            EnteredBill(b1, 100m, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)),
        };
        var applied = new Dictionary<Guid, decimal> { [b1] = 100m }; // b1 fully paid → excluded
        var open = VendorAccountBuilder.OpenBills(bills, applied, new DateOnly(2026, 4, 15));
        Assert.Single(open);
        Assert.Equal(b2, open[0].BillId);
        Assert.Equal(100m, open[0].OpenBalance);
        Assert.Equal(46, open[0].DaysOverdue); // 2026-02-28 → 2026-04-15
    }

    [Fact]
    public void Aging_buckets_by_fencepost()
    {
        OpenBillLine L(int overdue) => new(Guid.NewGuid(), null, new DateOnly(2026, 1, 1), null, 10m, overdue);
        var aging = VendorAccountBuilder.Aging([L(0), L(1), L(30), L(31), L(60), L(61), L(90), L(91)]);
        Assert.Equal(10m, aging.Current);  // overdue 0
        Assert.Equal(20m, aging.D1To30);   // 1, 30
        Assert.Equal(20m, aging.D31To60);  // 31, 60
        Assert.Equal(20m, aging.D61To90);  // 61, 90
        Assert.Equal(10m, aging.D90Plus);  // 91
    }

    [Fact]
    public void Statement_orders_charges_before_settlements_same_date_and_ends_at_ap_balance()
    {
        Guid bill = Guid.NewGuid();
        var bills = new List<Bill> { EnteredBill(bill, 100m, new DateOnly(2026, 3, 1), null) };
        var lines = VendorAccountBuilder.Statement(
            bills, [Payment(bill, 30m, 30m, new DateOnly(2026, 3, 1))], []);
        Assert.Equal("Bill", lines[0].Type);     // charge first
        Assert.Equal(100m, lines[0].Balance);
        Assert.Equal("Payment", lines[1].Type);  // settlement second (same date)
        Assert.Equal(70m, lines[1].Balance);     // running AP = 100 - 30
    }

    [Fact]
    public void CreditActivity_overpayment_plus_application_minus_running_balance()
    {
        Guid bill = Guid.NewGuid();
        // payment of 150 allocating 100 → 50 overpayment; later credit application of 20.
        var lines = VendorAccountBuilder.CreditActivity(
            [Payment(bill, 150m, 100m, new DateOnly(2026, 3, 1))],
            [CreditApp(bill, 20m, new DateOnly(2026, 3, 5))]);
        Assert.Equal("Overpayment", lines[0].Type);
        Assert.Equal(50m, lines[0].Amount);
        Assert.Equal(50m, lines[0].CreditBalance);
        Assert.Equal(-20m, lines[1].Amount);
        Assert.Equal(30m, lines[1].CreditBalance); // 50 - 20
    }
}
```

Create `PayablesAgingBucketsSerializationTests.cs`:

```csharp
using System.Text.Json;

namespace Accounting101.Payables.Tests;

/// <summary>Wire-contract guard: pins the exact JSON keys the UI AgingBuckets interface must mirror.
/// Interior capitals are preserved by camelCase: d1To30, NOT d1to30.</summary>
public sealed class PayablesAgingBucketsSerializationTests
{
    [Fact]
    public void AgingBuckets_serializes_with_camelCase_wire_keys()
    {
        AgingBuckets buckets = new(1m, 2m, 3m, 4m, 5m);
        string json = JsonSerializer.Serialize(buckets, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"current\"", json);
        Assert.Contains("\"d1To30\"", json);
        Assert.Contains("\"d31To60\"", json);
        Assert.Contains("\"d61To90\"", json);
        Assert.Contains("\"d90Plus\"", json);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorAccount|FullyQualifiedName~PayablesAgingBuckets"`
Expected: BUILD FAILS — `VendorAccountBuilder`/`AgingBuckets`/`OpenBillLine`/etc. don't exist.

- [ ] **Step 3: Create the view records**

Create `VendorAccountView.cs`:

```csharp
namespace Accounting101.Payables;

/// <summary>The read-only 360 view for one vendor — balances plus the four ledgers.</summary>
public sealed record VendorAccountView(
    Vendor Vendor, decimal ApBalance, decimal CreditBalance, AgingBuckets Aging,
    IReadOnlyList<OpenBillLine> OpenBills, IReadOnlyList<StatementLine> StatementLines,
    IReadOnlyList<CreditActivityLine> CreditLines);

public sealed record AgingBuckets(decimal Current, decimal D1To30, decimal D31To60, decimal D61To90, decimal D90Plus);

public sealed record OpenBillLine(Guid BillId, string? Number, DateOnly BillDate, DateOnly? DueDate, decimal OpenBalance, int DaysOverdue);

public sealed record StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance);

public sealed record CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance);
```

- [ ] **Step 4: Create the builder**

Create `VendorAccountBuilder.cs`:

```csharp
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>Pure folds that turn a vendor's stored documents into the account view's parts. Every fold
/// ignores voided documents and is deterministic given its inputs (aging takes an explicit asOf).
/// Mirror of the receivables CustomerAccountBuilder, minus AR-only document types.</summary>
public static class VendorAccountBuilder
{
    public static Dictionary<Guid, decimal> AppliedByBill(
        IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps)
    {
        Dictionary<Guid, decimal> applied = new();
        void Add(IEnumerable<Allocation> allocs)
        {
            foreach (Allocation a in allocs) applied[a.TargetId] = applied.GetValueOrDefault(a.TargetId) + a.Amount;
        }
        Add(payments.Where(p => !p.Voided).SelectMany(p => p.Allocations));
        Add(creditApps.Where(c => !c.Voided).SelectMany(c => c.Allocations));
        return applied;
    }

    public static IReadOnlyList<OpenBillLine> OpenBills(
        IReadOnlyList<Bill> bills, IReadOnlyDictionary<Guid, decimal> applied, DateOnly asOf) =>
        bills.Where(b => b.Status == BillStatus.Entered)
            .Select(b =>
            {
                decimal open = Settlement.Settlement.OpenBalance(b.Total, applied.GetValueOrDefault(b.Id));
                int overdue = b.DueDate is { } due ? Math.Max(0, asOf.DayNumber - due.DayNumber) : 0;
                return new OpenBillLine(b.Id, b.Number, b.BillDate, b.DueDate, open, overdue);
            })
            .Where(l => l.OpenBalance > 0m)
            .OrderBy(l => l.BillDate).ToList();

    public static AgingBuckets Aging(IReadOnlyList<OpenBillLine> openBills)
    {
        decimal cur = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0;
        foreach (OpenBillLine l in openBills)
        {
            if (l.DaysOverdue <= 0) cur += l.OpenBalance;
            else if (l.DaysOverdue <= 30) b1 += l.OpenBalance;
            else if (l.DaysOverdue <= 60) b2 += l.OpenBalance;
            else if (l.DaysOverdue <= 90) b3 += l.OpenBalance;
            else b4 += l.OpenBalance;
        }
        return new AgingBuckets(cur, b1, b2, b3, b4);
    }

    public static decimal ApBalance(IReadOnlyList<OpenBillLine> openBills) => openBills.Sum(l => l.OpenBalance);

    public static IReadOnlyList<StatementLine> Statement(
        IReadOnlyList<Bill> bills, IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps)
    {
        List<(DateOnly Date, int Order, string Type, string? Reference, decimal Charge, decimal Payment)> raw = [];
        foreach (Bill b in bills.Where(b => b.Status == BillStatus.Entered))
            raw.Add((b.BillDate, 0, "Bill", b.Number, b.Total, 0m));
        foreach (BillPayment p in payments.Where(p => !p.Voided))
            raw.Add((p.Date, 1, "Payment", null, 0m, p.Allocations.Sum(a => a.Amount)));
        foreach (VendorCreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, 1, "Credit applied", null, 0m, c.Allocations.Sum(a => a.Amount)));

        decimal balance = 0m;
        return raw.OrderBy(r => r.Date).ThenBy(r => r.Order)
            .Select(r =>
            {
                balance += r.Charge - r.Payment;
                return new StatementLine(r.Date, r.Type, r.Reference, r.Charge, r.Payment, balance);
            }).ToList();
    }

    public static IReadOnlyList<CreditActivityLine> CreditActivity(
        IReadOnlyList<BillPayment> payments, IReadOnlyList<VendorCreditApplication> creditApps)
    {
        List<(DateOnly Date, string Type, string? Reference, decimal Amount)> raw = [];
        foreach (BillPayment p in payments.Where(p => !p.Voided && p.Unapplied > 0m))
            raw.Add((p.Date, "Overpayment", null, p.Unapplied));
        foreach (VendorCreditApplication c in creditApps.Where(c => !c.Voided))
            raw.Add((c.Date, "Credit applied", null, -c.Applied));

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

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorAccountBuilder|FullyQualifiedName~PayablesAgingBuckets"`
Expected: PASS (5 builder + 1 serialization).

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/VendorAccountView.cs Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs Modules/Payables/Accounting101.Payables.Tests/VendorAccountBuilderTests.cs Modules/Payables/Accounting101.Payables.Tests/PayablesAgingBucketsSerializationTests.cs
git commit -m "feat(payables): VendorAccountBuilder + view records (pure 360 folds)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Backend — VendorAccountService + `GET /vendors/{id}/account`

**Files:**
- Create: `Modules/Payables/Accounting101.Payables/VendorAccountService.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesServiceExtensions.cs` (register service)
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs` (route + handler)
- Test (create): `Modules/Payables/Accounting101.Payables.Tests/VendorAccountEndpointE2eTests.cs`

**Interfaces:**
- Consumes: `VendorAccountBuilder`, `VendorAccountView` (Task 1); `IVendorStore.GetAsync`; `IBillStore.GetByVendorAsync`; `IBillPaymentStore.GetPaymentsByVendorAsync` + `GetCreditApplicationsByVendorAsync`.
- Produces: `VendorAccountService.GetAccountAsync(Guid clientId, Guid vendorId, DateOnly asOf, CancellationToken) : Task<VendorAccountView?>`; `GET /clients/{clientId}/vendors/{vendorId}/account?asOf=`.

- [ ] **Step 1: Write the failing E2E test**

Create `VendorAccountEndpointE2eTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>Drives the real host and reconciles the vendor 360 invariants: AP balance = Σ open;
/// statement ends at AP balance; credit ledger ends at the vendor credit balance; 404 for unknown vendor.</summary>
public sealed class VendorAccountEndpointE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,        "2000", "Accounts Payable", "Liability", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,           "1000", "Cash",             "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId,  "1300", "Vendor Credits",   "Asset",     "Vendor");
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId,    "5200", "Rent Expense",     "Expense",   null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();

    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Vendor_account_reconciles()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // Bill 1 = $1000 due 2026-03-31, enter+approve, pay $1200 (allocate $1000) → $200 vendor credit.
        Bill bill1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", new DraftBillRequest(
            vendor.Id, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, null,
            [new BillLineBody("Rent", 1000m, fixture.RentExpenseAccountId)]))).Content.ReadFromJsonAsync<Bill>())!;
        Bill entered1 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill1.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered1.Id);
        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor.Id, new DateOnly(2026, 3, 15), 1200m, "check",
                [new Allocation(bill1.Id, 1000m)]))).Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // Bill 2 = $800 due 2026-02-15 (overdue as of asOf), enter+approve, leave unpaid.
        Bill bill2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", new DraftBillRequest(
            vendor.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 15), null, null,
            [new BillLineBody("Rent", 800m, fixture.RentExpenseAccountId)]))).Content.ReadFromJsonAsync<Bill>())!;
        Bill entered2 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill2.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered2.Id);

        VendorAccountView acct = (await clerk.GetFromJsonAsync<VendorAccountView>(
            $"/clients/{clientId}/vendors/{vendor.Id}/account?asOf=2026-04-15"))!;

        // bill1 fully paid → not open; bill2 ($800) open.
        Assert.Equal(800m, acct.ApBalance);
        Assert.Equal(800m, acct.OpenBills.Sum(b => b.OpenBalance));
        Assert.Equal(800m, acct.StatementLines[^1].Balance);             // statement ends at AP balance
        Assert.Equal(200m, acct.CreditLines[^1].CreditBalance);          // credit ledger ends at available credit

        // Reconcile credit ledger against the canonical credit-balance endpoint.
        var bal = (await clerk.GetFromJsonAsync<CreditBalanceDto>(
            $"/clients/{clientId}/vendors/{vendor.Id}/credit-balance"))!;
        Assert.Equal(bal.CreditBalance, acct.CreditLines[^1].CreditBalance);

        // bill2 is overdue (2026-02-15 → 2026-04-15 = 59 days) → 31-60 bucket.
        Assert.Equal(800m, acct.Aging.D31To60);
    }

    [Fact]
    public async Task Unknown_vendor_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage response = await clerk.GetAsync($"/clients/{clientId}/vendors/{Guid.NewGuid()}/account");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record CreditBalanceDto(Guid VendorId, decimal CreditBalance);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorAccountEndpointE2eTests"`
Expected: FAIL — `GET …/account` returns 404 for the real vendor too (route not mapped).

- [ ] **Step 3: Create the service**

Create `VendorAccountService.cs`:

```csharp
namespace Accounting101.Payables;

/// <summary>Assembles the read-only <see cref="VendorAccountView"/> for one vendor by reading its
/// documents and folding them with <see cref="VendorAccountBuilder"/>. Read-only; computes, never stores.</summary>
public sealed class VendorAccountService(IVendorStore vendors, IBillStore bills, IBillPaymentStore payments)
{
    public async Task<VendorAccountView?> GetAccountAsync(
        Guid clientId, Guid vendorId, DateOnly asOf, CancellationToken ct = default)
    {
        Vendor? vendor = await vendors.GetAsync(clientId, vendorId, ct);
        if (vendor is null) return null;

        IReadOnlyList<Bill> vendorBills = await bills.GetByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<VendorCreditApplication> cs = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);

        Dictionary<Guid, decimal> applied = VendorAccountBuilder.AppliedByBill(ps, cs);
        IReadOnlyList<OpenBillLine> open = VendorAccountBuilder.OpenBills(vendorBills, applied, asOf);
        decimal credit = ps.Where(p => !p.Voided).Sum(p => p.Unapplied)
                         - cs.Where(c => !c.Voided).Sum(c => c.Applied);

        return new VendorAccountView(
            vendor,
            VendorAccountBuilder.ApBalance(open),
            credit,
            VendorAccountBuilder.Aging(open),
            open,
            VendorAccountBuilder.Statement(vendorBills, ps, cs),
            VendorAccountBuilder.CreditActivity(ps, cs));
    }
}
```

- [ ] **Step 4: Register the service**

In `PayablesServiceExtensions.cs`, add a registration alongside the other `AddScoped` calls (e.g. next to where `BillPaymentService` is registered):

```csharp
        services.AddScoped<VendorAccountService>();
```

- [ ] **Step 5: Map the route + handler**

In `PayablesEndpoints.cs`, add the route after `clients.MapGet("/vendors/{vendorId:guid}/credit-balance", GetCreditBalance);`:

```csharp
        clients.MapGet("/vendors/{vendorId:guid}/account", GetVendorAccount);
```

Add the handler (mirror of the receivables `GetCustomerAccount`):

```csharp
    private static async Task<IResult> GetVendorAccount(
        Guid clientId, Guid vendorId, string? asOf, VendorAccountService service, CancellationToken cancellationToken)
    {
        DateOnly date;
        if (string.IsNullOrEmpty(asOf))
            date = DateOnly.FromDateTime(DateTime.UtcNow);
        else if (!DateOnly.TryParse(asOf, out date))
            return Results.Problem("asOf must be a date (yyyy-MM-dd).", statusCode: StatusCodes.Status400BadRequest);

        VendorAccountView? view = await service.GetAccountAsync(clientId, vendorId, date, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }
```

- [ ] **Step 6: Run the E2E to verify it passes**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter "FullyQualifiedName~VendorAccountEndpointE2eTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Run the full payables suite (no regressions)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: all green (was 67; now 75: +5 builder +1 serialization +2 E2E).

- [ ] **Step 8: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/VendorAccountService.cs Modules/Payables/Accounting101.Payables.Api/PayablesServiceExtensions.cs Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs Modules/Payables/Accounting101.Payables.Tests/VendorAccountEndpointE2eTests.cs
git commit -m "feat(payables): VendorAccountService + GET /vendors/{id}/account

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: UI core — VendorAccountView interfaces + service method

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.ts` (append interfaces)
- Modify: `UI/Angular/src/app/core/payables/payables.service.ts` (append method)
- Test: `UI/Angular/src/app/core/payables/payables.service.spec.ts` (append a case)

**Interfaces:**
- Produces types: `AgingBuckets`, `OpenBillLine`, `StatementLine`, `CreditActivityLine`, `VendorAccountView`.
- Produces: `PayablesService.getVendorAccount(vendorId: string): Observable<VendorAccountView>` → `GET /vendors/{vendorId}/account`.

- [ ] **Step 1: Append the failing service test**

In `payables.service.spec.ts`, add a case (reuse the existing `setup()` → `{ svc, ctrl }`):

```typescript
  it('gets a vendor account', () => {
    const { svc, ctrl } = setup();
    svc.getVendorAccount('v1').subscribe();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/account').flush({
      vendor: { id: 'v1', name: 'Acme Parts', email: null }, apBalance: 800, creditBalance: 200,
      aging: { current: 0, d1To30: 0, d31To60: 800, d61To90: 0, d90Plus: 0 },
      openBills: [], statementLines: [], creditLines: [] });
    ctrl.verify();
  });
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/core/payables/payables.service.spec.ts" --watch=false`
Expected: FAIL — `getVendorAccount` not defined.

- [ ] **Step 3: Append the interfaces**

Append to `payables.ts` (the `d1To30` casing matches the backend serialization guard EXACTLY):

```typescript
export interface AgingBuckets { current: number; d1To30: number; d31To60: number; d61To90: number; d90Plus: number; }
export interface OpenBillLine { billId: string; number: string | null; billDate: string; dueDate: string | null; openBalance: number; daysOverdue: number; }
export interface StatementLine { date: string; type: string; reference: string | null; charge: number; payment: number; balance: number; }
export interface CreditActivityLine { date: string; type: string; reference: string | null; amount: number; creditBalance: number; }
export interface VendorAccountView {
  vendor: Vendor; apBalance: number; creditBalance: number; aging: AgingBuckets;
  openBills: OpenBillLine[]; statementLines: StatementLine[]; creditLines: CreditActivityLine[];
}
```

- [ ] **Step 4: Append the service method**

Add `VendorAccountView` to the existing `./payables` import in `payables.service.ts`, then add (after `applyVendorCredit`):

```typescript
  getVendorAccount(vendorId: string): Observable<VendorAccountView> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<VendorAccountView>(this.base(`/vendors/${vendorId}/account`));
  }
```

- [ ] **Step 5: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/core/payables/payables.service.spec.ts" --watch=false`
Expected: PASS (existing cases + the new one).

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.ts UI/Angular/src/app/core/payables/payables.service.ts UI/Angular/src/app/core/payables/payables.service.spec.ts
git commit -m "feat(ui): vendor-account view interfaces + getVendorAccount

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: feature — VendorAccount screen

**Files:**
- Create: `UI/Angular/src/app/features/payables/vendor-account.ts`
- Test: `UI/Angular/src/app/features/payables/vendor-account.spec.ts`

**Interfaces:**
- Consumes: `PayablesService.getVendorAccount`, `VendorAccountView`, `money`/`displayDate`, `extractProblem`, `ActivatedRoute`/`RouterLink`.
- Produces: `VendorAccount` component, selector `app-vendor-account`. Reads `:id`; renders header + aging + open bills + statement + credit activity.

- [ ] **Step 1: Write the failing test**

Create `vendor-account.spec.ts` (mirror of `customer-account.spec.ts`):

```typescript
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorAccount } from './vendor-account';
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
  vendor: { id: 'v1', name: 'Acme Parts', email: 'acme@x.com' }, apBalance: 800, creditBalance: 200,
  aging: { current: 0, d1To30: 0, d31To60: 800, d61To90: 0, d90Plus: 0 },
  openBills: [{ billId: 'b2', number: 'B-2', billDate: '2026-02-01', dueDate: '2026-02-15', openBalance: 800, daysOverdue: 59 }],
  statementLines: [{ date: '2026-02-01', type: 'Bill', reference: 'B-2', charge: 800, payment: 0, balance: 800 }],
  creditLines: [{ date: '2026-03-15', type: 'Overpayment', reference: null, amount: 200, creditBalance: 200 }],
});

describe('VendorAccount', () => {
  it('renders header, aging, open bills, statement, and credit activity', () => {
    const ctrl = setup('v1');
    const f = TestBed.createComponent(VendorAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/account').flush(view());
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('Acme Parts');
    expect(text).toContain('acme@x.com');
    expect(text).toContain('800.00');         // AP balance + open bill + statement
    expect(text).toContain('B-2');            // open bill + statement ref
    expect(text).toContain('Overpayment');    // credit activity
    expect(text).toContain('200.00');         // credit balance + d31To60 bucket guard value
  });

  it('relays a not-found error', () => {
    const ctrl = setup('nope');
    const f = TestBed.createComponent(VendorAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/nope/account').flush(
      { type: 'about:blank', title: 'Not Found', detail: 'Vendor not found.', status: 404 },
      { status: 404, statusText: 'Not Found' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Vendor not found.');
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/vendor-account.spec.ts" --watch=false`
Expected: FAIL — cannot resolve `./vendor-account`.

- [ ] **Step 3: Create the component** (mirror of `customer-account.ts`)

Create `vendor-account.ts`:

```typescript
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { PayablesService } from '../../core/payables/payables.service';
import { VendorAccountView } from '../../core/payables/payables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-vendor-account',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <a routerLink="/payables/vendors" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Vendors</a>
      @if (error()) {
        <p class="text-destructive text-sm">{{ error() }}</p>
      } @else if (account(); as a) {
        <div class="flex flex-wrap items-baseline gap-x-6 gap-y-1">
          <h1 class="text-2xl font-bold">{{ a.vendor.name }}</h1>
          <span class="text-sm text-muted-foreground">{{ a.vendor.email ?? '—' }}</span>
          <span class="ms-auto text-sm">AP balance <span class="font-semibold tabular-nums">{{ money(a.apBalance) }}</span></span>
          <span class="text-sm">Credit <span class="font-semibold tabular-nums">{{ money(a.creditBalance) }}</span></span>
        </div>

        <div class="flex flex-wrap gap-4 text-sm">
          <div>Current <span class="tabular-nums">{{ money(a.aging.current) }}</span></div>
          <div>1–30 <span class="tabular-nums">{{ money(a.aging.d1To30) }}</span></div>
          <div>31–60 <span class="tabular-nums">{{ money(a.aging.d31To60) }}</span></div>
          <div>61–90 <span class="tabular-nums">{{ money(a.aging.d61To90) }}</span></div>
          <div [class.text-destructive]="a.aging.d90Plus > 0">90+ <span class="tabular-nums">{{ money(a.aging.d90Plus) }}</span></div>
        </div>

        <div class="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div class="lg:col-span-2 flex flex-col gap-4">
            <section class="flex flex-col gap-1">
              <h2 class="font-semibold text-sm">Open bills</h2>
              @if (a.openBills.length === 0) { <p class="text-sm text-muted-foreground">No open bills.</p> }
              @else {
                <table class="w-full text-sm">
                  <thead><tr class="text-left text-muted-foreground"><th class="py-1">Number</th><th>Bill date</th><th>Due</th><th class="text-right">Open</th><th class="text-right">Overdue</th></tr></thead>
                  <tbody>
                    @for (l of a.openBills; track l.billId) {
                      <tr [class.text-destructive]="l.daysOverdue > 0">
                        <td class="py-1">{{ l.number ?? '—' }}</td><td>{{ fmtDate(l.billDate) }}</td>
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
export class VendorAccount {
  private readonly svc = inject(PayablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly account = signal<VendorAccountView | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) { this.error.set('No vendor.'); return; }
    this.svc.getVendorAccount(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: v => this.account.set(v),
      error: e => this.error.set(extractProblem(e).detail),
    });
  }

  money(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/vendor-account.spec.ts" --watch=false`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/payables/vendor-account.ts UI/Angular/src/app/features/payables/vendor-account.spec.ts
git commit -m "feat(ui): VendorAccount 360 screen

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: feature — vendor-list row → 360 + route

**Files:**
- Modify: `UI/Angular/src/app/features/payables/vendor-list.ts` (change `open`)
- Modify: `UI/Angular/src/app/features/payables/vendor-list.spec.ts` (update nav test)
- Modify: `UI/Angular/src/app/app.routes.ts` (add `vendors/:id` route)

**Interfaces:**
- Consumes: `VendorAccount` (Task 4).
- Produces: vendor row click → `/payables/vendors/:id`; route `vendors/:id` → VendorAccount.

- [ ] **Step 1: Update the failing nav test**

In `vendor-list.spec.ts`, change the row-click test's expectation from the bills navigation to the account navigation. The existing test asserts `setSelectedVendor` + `navigate(['/payables/bills'])`; replace those assertions with:

```typescript
    const row = f.nativeElement.querySelector('[data-testid="vendor-row"]') as HTMLElement;
    row.click();
    expect(nav).toHaveBeenCalledWith(['/payables/vendors', 'v1']);
```

(Drop any `selectedVendorId()` assertion from that test — selection is no longer set on row click.)

- [ ] **Step 2: Run it to verify it fails**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/vendor-list.spec.ts" --watch=false`
Expected: FAIL — `open()` still navigates to `/payables/bills`.

- [ ] **Step 3: Change `open()`**

In `vendor-list.ts`, change the `open` method to:

```typescript
  open(id: string): void { void this.router.navigate(['/payables/vendors', id]); }
```

(Remove the `setSelectedVendor(id)` call — the 360 reads the vendor from the route, not the persisted selection.)

- [ ] **Step 4: Run it to verify it passes**

Run: `cd UI/Angular && npx ng test --include="src/app/features/payables/vendor-list.spec.ts" --watch=false`
Expected: PASS.

- [ ] **Step 5: Wire the route**

In `app.routes.ts`, add the import near the other payables imports:

```typescript
import { VendorAccount } from './features/payables/vendor-account';
```

In the `payables` children array, add `vendors/:id` AFTER the bare `vendors` route (so it doesn't shadow the list):

```typescript
    { path: 'vendors', component: VendorList },
    { path: 'vendors/:id', component: VendorAccount },
```

- [ ] **Step 6: Run the full suite + type-check**

Run: `cd UI/Angular && npx tsc -p tsconfig.app.json --noEmit && npx ng test --watch=false`
Expected: tsc clean; ALL specs pass (existing + new vendor-account specs). Report totals.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/features/payables/vendor-list.ts UI/Angular/src/app/features/payables/vendor-list.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): vendor-list rows open the vendor 360 + vendors/:id route

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review notes

- **Spec coverage:** builder + view records + serialization guard (T1); service + endpoint + reconciliation E2E (T2); core interfaces + service method (T3); the 360 screen (T4); vendor-list row→360 + route (T5). Deferred (bill draft edit/discard) correctly absent.
- **camelCase trap:** the UI `AgingBuckets` uses `d1To30`/`d31To60`/`d61To90`/`d90Plus` (T3), pinned by the backend `PayablesAgingBucketsSerializationTests` (T1). The customer-360 NaN bug this prevents is the reason both exist.
- **Reconciliation invariant:** the 360 credit balance formula in `VendorAccountService` (T2) is `Σ Unapplied − Σ Applied`, identical to `GetVendorCreditBalanceAsync`; the E2E asserts equality against the `/credit-balance` endpoint.
- **Type consistency:** `VendorAccountView { vendor, apBalance, creditBalance, aging, openBills, statementLines, creditLines }` (UI T3) mirrors the C# record (T1) field-for-field under camelCase. `OpenBillLine`/`StatementLine`/`CreditActivityLine` field names match. Statement `Type` strings ("Bill"/"Payment"/"Credit applied") and credit `Type` strings ("Overpayment"/"Credit applied") are defined once in the builder (T1).
- **Vendor-list change (T5):** the row click previously set the selected vendor + went to Bills; now it only navigates to the 360 — the bills-filtered-by-vendor path is reachable from within the 360's open-bills list and via the Bills tab's vendor select.
