# Money/Settlement Edge-Case Scenario Library — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an HTTP-level, deterministic edge-case test library for the money/settlement surface of Receivables and Payables, asserting exact outcomes (status + message substring for rejections; precise balances + subledger tie-out for accepted/integrity sequences).

**Architecture:** Organized xUnit E2E tests in the existing module test projects (`Accounting101.Receivables.Tests`, `Accounting101.Payables.Tests`), each in a new `Settlement/` subfolder, driven through the real host via the existing `WebApplicationFactory<Program>` + EphemeralMongo fixtures (`ReceivablesHostFixture`, `PayablesHostFixture`). Each module gets one shared static scenario helper plus four test files. No product code changes unless a scenario surfaces a real bug.

**Tech Stack:** .NET 10, xUnit, `Microsoft.AspNetCore.Mvc.Testing`, EphemeralMongo, `Microsoft.AspNetCore.Mvc.ProblemDetails`.

## Global Constraints

- All new files live under `<TestProject>/Settlement/`. Namespace stays the flat project namespace (`Accounting101.Receivables.Tests` / `Accounting101.Payables.Tests`) so the existing fixtures resolve without extra `using`s.
- Each test class uses `IClassFixture<ReceivablesHostFixture>` or `IClassFixture<PayablesHostFixture>` (matches the existing E2E pattern; one EphemeralMongo per class).
- Rejection assertions: assert the exact `HttpStatusCode` **and** that `ProblemDetails.Detail` contains the documented substring, case-insensitively. Never assert the full message verbatim.
- "Books balance" is asserted via `BalanceSheetResponse.IsBalanced` (the Assets = Liabilities + Equity invariant — equivalent to trial-balance-net-zero for a fully classified chart, and the DTO is confirmed). The spec's "trial balance nets to zero" is realized this way.
- Accepted/integrity assertions use exact `decimal` literals, `SubledgerReconciliationResponse.TiesOut == true`, and `BalanceSheetResponse.IsBalanced == true`.
- Message substrings MUST match the live service messages (each listed verbatim per task; sourced from `PaymentService`/`BillPaymentService`).
- Do NOT modify product code to make a test pass. If a scenario reveals wrong behavior (a 500 where 422 is expected, a rounding drift, a mis-routed reason), STOP and surface it as a finding for a decision — fix product vs. adjust expectation. Never skip a scenario to go green.
- Commit trailer, verbatim, on every commit:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- Test run command (from repo root), per project, filtered to the class under test, e.g.:
  `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~AllocationBoundary"`

## Confirmed contracts (read once; used across tasks)

Request DTOs (already exist):
- AR: `DraftInvoiceRequest(Guid CustomerId, IReadOnlyList<InvoiceLine> Lines, decimal TaxRate, DateOnly IssueDate, DateOnly? DueDate, string? Memo)`; `RecordPaymentRequest(Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations)`; `CreditApplicationRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations)`; `WriteOffRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo)`; `CreditNoteRequest(...)` same shape; `RefundRequest(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo)`; `CreateCustomerRequest(string Name, string? Email)`; `VoidInvoiceRequest(string? Reason)`.
- AP: `DraftBillRequest(Guid VendorId, DateOnly BillDate, DateOnly? DueDate, string? VendorReference, string? Memo, IReadOnlyList<BillLineBody> Lines)`; `BillLineBody(string Description, decimal Amount, Guid ExpenseAccountId)`; `RecordBillPaymentRequest(Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations)`; `VendorCreditApplicationRequest(Guid VendorId, DateOnly Date, IReadOnlyList<Allocation> Allocations)`; `CreateVendorRequest(string Name, string? Email)`.
- Shared: `Allocation(Guid TargetId, decimal Amount)`; `InvoiceLine { string Description; decimal Quantity; decimal UnitPrice; bool Taxable; string? RevenueCategory; }`.

Endpoint routes + status mapping:
- AR record endpoints map `InvalidOperationException` → **422**: `POST /clients/{c}/payments`, `/credit-applications`, `/write-offs`, `/credit-notes`, `/refunds`. AR void endpoints map it → **409**: `POST /clients/{c}/payments/{id}/void`, `/write-offs/{id}/void`, `/credit-notes/{id}/void`, `/refunds/{id}/void`, `/invoices/{id}/void`. `GET /clients/{c}/invoices/{id}` returns `InvoiceView`.
- AP record endpoints → **422**: `POST /clients/{c}/bill-payments`, `/vendor-credit-applications`. AP void → **409**: `POST /clients/{c}/bill-payments/{id}/void`. `GET /clients/{c}/bills/{id}` returns `BillView`.
- Statements: `GET /clients/{c}/statements/balance-sheet?asOf=yyyy-MM-dd` → `BalanceSheetResponse`; `GET /clients/{c}/statements/income-statement?from=&to=` → `IncomeStatementResponse`.
- Subledger: `GET /clients/{c}/subledger/reconciliation?account={accountId}&dimension=Customer|Vendor` → `SubledgerReconciliationResponse`.

Response shapes (already exist in `Accounting101.Ledger.Contracts`):
- `BalanceSheetResponse(DateOnly AsOf, StatementSectionResponse Assets, StatementSectionResponse Liabilities, StatementSectionResponse Equity, decimal TotalAssets, decimal TotalLiabilitiesAndEquity, bool IsBalanced)`.
- `StatementSectionResponse(string Title, IReadOnlyList<StatementLineResponse> Lines, decimal Total)`; `StatementLineResponse(Guid? AccountId, string? Number, string Name, decimal Amount)`.
- `IncomeStatementResponse(DateOnly From, DateOnly To, StatementSectionResponse Revenue, StatementSectionResponse Expenses, decimal NetIncome)`.
- `SubledgerReconciliationResponse(Guid Account, string Dimension, DateOnly? AsOf, decimal ControlBalance, decimal SubledgerTotal, decimal Variance, bool TiesOut)`.
- `EntryResponse(... IReadOnlyList<EntryLineResponse> Lines ...)`; `EntryLineResponse(Guid AccountId, string Direction, decimal Amount, IReadOnlyDictionary<string,Guid> Dimensions, string? LineMemo)`.

Fixture members reused:
- `ReceivablesHostFixture`: `SeedSodClientAsync()` → `(Guid ClientId, HttpClient ControllerHttp, HttpClient ClerkHttp, HttpClient ApproverHttp)`; account-id props `ReceivableAccountId, RevenueAccountId, SalesTaxPayableAccountId, CashAccountId, CustomerCreditsAccountId, BadDebtExpenseAccountId, SalesReturnsAccountId`.
- `PayablesHostFixture`: `SeedSodClientAsync()` same tuple shape; account-id props `PayableAccountId, CashAccountId, VendorCreditsAccountId, RentExpenseAccountId, UtilitiesExpenseAccountId`.
- `AccountRequest` is constructed `new AccountRequest { Number, Name, Type, RequiredDimension }` (and `IsRetainedEarnings` optional).

Rounding rule (AR): `Invoice.Tax = decimal.Round(TaxRate * TaxableBase, 2, MidpointRounding.AwayFromZero)`, `Invoice.Total = Subtotal + Tax`. Bills carry NO tax line (the tax-rounding scenario is AR-only).

Settled-target nuance: a fully-paid invoice/bill stays `Issued`/`Entered`, so paying it again surfaces via the open-balance guard (`"exceeds its open balance"`), NOT the status guard.

---

### Task 1: AR shared helper + allocation-boundary rejections

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables.Tests/Settlement/SettlementScenario.cs`
- Create: `Modules/Receivables/Accounting101.Receivables.Tests/Settlement/AllocationBoundaryE2eTests.cs`

**Interfaces:**
- Produces (used by Tasks 2-4): the static helper `SettlementScenario` with methods
  `SetUpChartAsync(HttpClient controller, Guid clientId, ReceivablesHostFixture f)`,
  `CreateCustomerAsync(HttpClient clerk, Guid clientId, string name = "Stark") -> Task<Guid>`,
  `IssueInvoiceAsync(HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId, decimal unitPrice, decimal taxRate = 0m, bool taxable = false) -> Task<Guid>` (drafts a single-line invoice with IssueDate 2026-03-01, issues, approves; returns invoice id),
  `DraftInvoiceAsync(HttpClient clerk, Guid clientId, Guid customerId, decimal unitPrice) -> Task<Guid>` (drafts only, no issue),
  `ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)`,
  `AssertProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string substring)`,
  `AssertBalancedAsync(HttpClient http, Guid clientId, DateOnly asOf)`.

- [ ] **Step 1: Write the shared helper**

Create `Settlement/SettlementScenario.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Receivables.Tests;

/// <summary>Shared setup + assertion helpers for the receivables money/settlement edge-case scenarios,
/// driven end-to-end through the real host.</summary>
internal static class SettlementScenario
{
    internal static async Task SetUpChartAsync(HttpClient controller, Guid clientId, ReceivablesHostFixture f)
    {
        await PutAccountAsync(controller, clientId, f.ReceivableAccountId,      "1100", "Accounts Receivable", "Asset",     "Customer");
        await PutAccountAsync(controller, clientId, f.RevenueAccountId,         "4000", "Revenue",             "Revenue",   null);
        await PutAccountAsync(controller, clientId, f.SalesTaxPayableAccountId, "2200", "Sales Tax Payable",   "Liability", null);
        await PutAccountAsync(controller, clientId, f.CashAccountId,            "1000", "Cash",                "Asset",     null);
        await PutAccountAsync(controller, clientId, f.CustomerCreditsAccountId, "2300", "Customer Credits",    "Liability", "Customer");
        await PutAccountAsync(controller, clientId, f.BadDebtExpenseAccountId,  "6000", "Bad Debt Expense",    "Expense",   null);
        await PutAccountAsync(controller, clientId, f.SalesReturnsAccountId,    "4900", "Sales Returns",       "Revenue",   null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    internal static async Task<Guid> CreateCustomerAsync(HttpClient clerk, Guid clientId, string name = "Stark")
    {
        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest(name, null)))
            .Content.ReadFromJsonAsync<Customer>())!;
        return customer.Id;
    }

    internal static async Task<Guid> DraftInvoiceAsync(HttpClient clerk, Guid clientId, Guid customerId, decimal unitPrice)
    {
        DraftInvoiceRequest req = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = unitPrice, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", req))
            .Content.ReadFromJsonAsync<Invoice>())!;
        return draft.Id;
    }

    internal static async Task<Guid> IssueInvoiceAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId,
        decimal unitPrice, decimal taxRate = 0m, bool taxable = false)
    {
        DraftInvoiceRequest req = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = unitPrice, Taxable = taxable }],
            TaxRate: taxRate, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", req))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, issued.Id);
        return issued.Id;
    }

    internal static async Task ApproveBySourceRefAsync(
        HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string substring)
    {
        Assert.Equal(status, resp.StatusCode);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(substring, problem!.Detail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task AssertBalancedAsync(HttpClient http, Guid clientId, DateOnly asOf)
    {
        BalanceSheetResponse sheet = (await http.GetFromJsonAsync<BalanceSheetResponse>(
            $"/clients/{clientId}/statements/balance-sheet?asOf={asOf:yyyy-MM-dd}"))!;
        Assert.True(sheet.IsBalanced,
            $"Balance sheet not balanced as of {asOf}: assets {sheet.TotalAssets} vs L+E {sheet.TotalLiabilitiesAndEquity}");
    }
}
```

- [ ] **Step 2: Write the allocation-boundary tests**

Create `Settlement/AllocationBoundaryE2eTests.cs`. Substrings used (verbatim source):
`"Allocations cannot exceed the payment amount."`, `"...exceeds its open balance."`, `"Invoice {id} does not exist."`, `"...is {status} — only issued invoices can be paid."`, `"...belongs to a different customer."`, `"A payment amount must be greater than zero."`, `"Every allocation amount must be greater than zero."`.

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>Allocation-boundary rejections on POST /payments — each maps to 422 with a specific reason.</summary>
public sealed class AllocationBoundaryE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, controller, clerk, approver);
    }

    [Fact]
    public async Task Allocations_exceeding_payment_amount_are_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(invoice, 80m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "cannot exceed the payment amount");
    }

    [Fact]
    public async Task Allocation_exceeding_open_balance_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 200m, "check", [new Allocation(invoice, 200m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Paying_an_already_settled_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Payment paid = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 100m, "check", [new Allocation(invoice, 100m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, paid.Id);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 6), 50m, "check", [new Allocation(invoice, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Allocation_to_a_nonexistent_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, _) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(Guid.NewGuid(), 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "does not exist");
    }

    [Fact]
    public async Task Allocation_to_a_draft_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, _) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid draft = await DraftInvoiceAsync(clerk, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(draft, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "only issued invoices can be paid");
    }

    [Fact]
    public async Task Allocation_to_a_voided_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);
        // Voiding an issued invoice is an Approver operation (Reverse/Void permission); the Clerk has only
        // Read for raw GL. Matches the established ReceivablesVoidTests pattern.
        (await approver.PostAsJsonAsync($"/clients/{clientId}/invoices/{invoice}/void",
            new VoidInvoiceRequest("test"))).EnsureSuccessStatusCode();

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 6), 50m, "check", [new Allocation(invoice, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "only issued invoices can be paid");
    }

    [Fact]
    public async Task Allocation_to_another_customers_invoice_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customerA = await CreateCustomerAsync(clerk, clientId, "Stark");
        Guid customerB = await CreateCustomerAsync(clerk, clientId, "Wayne");
        Guid invoiceA = await IssueInvoiceAsync(clerk, approver, clientId, customerA, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customerB, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(invoiceA, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "belongs to a different customer");
    }

    [Fact]
    public async Task Zero_payment_amount_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, _) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 0m, "check", []));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "payment amount must be greater than zero");
    }

    [Fact]
    public async Task Negative_allocation_amount_is_rejected()
    {
        (Guid clientId, _, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(invoice, -10m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "allocation amount must be greater than zero");
    }
}
```

- [ ] **Step 3: Run the tests, expect PASS**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~AllocationBoundaryE2eTests"`
Expected: all 9 tests PASS. (These pin existing behavior; they should pass on first run. If any returns a different status/message, STOP — that is a finding per Global Constraints.)

- [ ] **Step 4: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Tests/Settlement/SettlementScenario.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/Settlement/AllocationBoundaryE2eTests.cs
git commit -m "$(cat <<'EOF'
test(receivables): HTTP allocation-boundary edge cases + shared scenario helper

Pins the 422 + reason mapping for over-allocation, exceeds-open-balance,
settled/nonexistent/draft/voided/wrong-customer targets, and zero/negative
amounts on POST /payments.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: AR disposition-limit rejections (write-off / credit-note / refund / credit-application / void)

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables.Tests/Settlement/DispositionLimitE2eTests.cs`

**Interfaces:**
- Consumes: `SettlementScenario` (Task 1).

- [ ] **Step 1: Write the disposition-limit tests**

Substrings (verbatim source): write-off/credit-note over balance → `"...exceeds its open balance."`; refund/credit-application over credit → `"...exceeds available credit ..."`; void already-voided → `"Payment {id} is already voided."`; void missing → `"Payment {id} not found."`.

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>Disposition-limit rejections: over-disposition (422) and void guards (409).</summary>
public sealed class DispositionLimitE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, clerk, approver);
    }

    [Fact]
    public async Task WriteOff_over_open_balance_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 60m, "check", [new Allocation(invoice, 60m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice, 50m)], "uncollectible"));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task CreditNote_over_open_balance_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice, 150m)], "too much"));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Refund_exceeding_available_credit_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        // Overpay by 20 → customer credit 20.
        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 120m, "check", [new Allocation(invoice, 100m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer, new DateOnly(2026, 3, 6), 50m, "too much"));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds available credit");
    }

    [Fact]
    public async Task CreditApplication_exceeding_available_credit_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice1 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        // Overpay by 10 → customer credit 10.
        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 110m, "check", [new Allocation(invoice1, 100m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        Guid invoice2 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);
        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
            new CreditApplicationRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice2, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds available credit");
    }

    [Fact]
    public async Task Voiding_an_already_voided_payment_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 100m, "check", [new Allocation(invoice, 100m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // Voiding an approved (posted) payment reverses a posted GL entry — an Approver action. Both voids
        // go through the approver; the second hits the already-voided guard before any ledger call.
        (await approver.PostAsJsonAsync($"/clients/{clientId}/payments/{pay.Id}/void",
            new VoidInvoiceRequest("first void"))).EnsureSuccessStatusCode();

        HttpResponseMessage resp = await approver.PostAsJsonAsync($"/clients/{clientId}/payments/{pay.Id}/void",
            new VoidInvoiceRequest("second void"));

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "already voided");
    }

    [Fact]
    public async Task Voiding_a_nonexistent_payment_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments/{Guid.NewGuid()}/void",
            new VoidInvoiceRequest("nope"));

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "not found");
    }
}
```

- [ ] **Step 2: Run the tests, expect PASS**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~DispositionLimitE2eTests"`
Expected: all 6 PASS. Any deviation is a finding — STOP.

- [ ] **Step 3: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Tests/Settlement/DispositionLimitE2eTests.cs
git commit -m "$(cat <<'EOF'
test(receivables): HTTP disposition-limit edge cases

Pins 422 for over-write-off, over-credit-note, refund/credit-application
exceeding available credit; 409 for void-of-voided and void-of-missing.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: AR rounding sweep

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables.Tests/Settlement/RoundingE2eTests.cs`

**Interfaces:**
- Consumes: `SettlementScenario` (Task 1).

Computed expectations: line 100.10 taxable, rate 0.0825 → tax = Round(100.10 × 0.0825, 2, AwayFromZero) = Round(8.258250, 2) = **8.26**; total = **108.36**.

- [ ] **Step 1: Write the rounding tests**

```csharp
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using Accounting101.Ledger.Contracts;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>Rounding sweep: sales-tax rounds to the cent in both the posted entry and the statements,
/// and an uneven multi-invoice allocation leaves every open balance exact with the subledger tying out.</summary>
public sealed class RoundingE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, clerk, approver);
    }

    [Fact]
    public async Task Sales_tax_rounds_to_the_cent_in_entry_and_statements()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, unitPrice: 100.10m, taxRate: 0.0825m, taxable: true);

        // Invoice total reflects rounded tax: 100.10 + 8.26 = 108.36.
        InvoiceView view = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice}"))!;
        Assert.Equal(108.36m, view.Invoice.Total);
        Assert.Equal(108.36m, view.OpenBalance);

        // Balance sheet: A/R asset line = 108.36, sales-tax-payable liability line = 8.26, and it balances.
        DateOnly asOf = new(2026, 3, 31);
        BalanceSheetResponse sheet = (await clerk.GetFromJsonAsync<BalanceSheetResponse>(
            $"/clients/{clientId}/statements/balance-sheet?asOf={asOf:yyyy-MM-dd}"))!;
        Assert.True(sheet.IsBalanced);
        Assert.Equal(108.36m, sheet.Assets.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId).Amount);
        Assert.Equal(8.26m, sheet.Liabilities.Lines.Single(l => l.AccountId == fixture.SalesTaxPayableAccountId).Amount);

        // Income statement: revenue is the pre-tax subtotal exactly.
        IncomeStatementResponse income = (await clerk.GetFromJsonAsync<IncomeStatementResponse>(
            $"/clients/{clientId}/statements/income-statement?from=2026-01-01&to=2026-03-31"))!;
        Assert.Equal(100.10m, income.Revenue.Total);
    }

    [Fact]
    public async Task Uneven_split_across_invoices_leaves_exact_balances_and_subledger_ties_out()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid inv1 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 33.33m);
        Guid inv2 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 33.33m);
        Guid inv3 = await IssueInvoiceAsync(clerk, approver, clientId, customer, 33.34m);

        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 31), 100.00m, "check",
                    [new Allocation(inv1, 33.33m), new Allocation(inv2, 33.33m), new Allocation(inv3, 33.34m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        foreach (Guid id in new[] { inv1, inv2, inv3 })
        {
            InvoiceView v = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{id}"))!;
            Assert.Equal(0m, v.OpenBalance);
            Assert.Equal(SettlementStatus.Paid, v.SettlementStatus);
        }

        SubledgerReconciliationResponse ar = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(ar.TiesOut);
        await AssertBalancedAsync(clerk, clientId, new DateOnly(2026, 3, 31));
    }
}
```

- [ ] **Step 2: Run the tests, expect PASS**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~RoundingE2eTests"`
Expected: both PASS. If the tax or a balance is off by a cent, STOP — that is a finding.

- [ ] **Step 3: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Tests/Settlement/RoundingE2eTests.cs
git commit -m "$(cat <<'EOF'
test(receivables): rounding sweep — tax-to-the-cent + uneven allocation split

Asserts sales tax rounds to 8.26 in entry and statements (100.10 @ 8.25%),
and a 100.00 payment split 33.33/33.33/33.34 leaves all open balances zero
with the A/R subledger tying out.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: AR settlement-integrity sweep

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables.Tests/Settlement/SettlementIntegrityE2eTests.cs`

**Interfaces:**
- Consumes: `SettlementScenario` (Task 1).

Sequence + expected open balances on a 1000 invoice: pay 600 → 400; credit-note 100 → 300; write-off 300 → 0; void the write-off → back to 300. After every approved step the books balance and both subledgers tie out.

- [ ] **Step 1: Write the integrity test**

```csharp
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using Accounting101.Ledger.Contracts;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>A messy disposition sequence on one invoice keeps the books balanced and both subledgers tying
/// out at every approved step, with exact derived open balances throughout.</summary>
public sealed class SettlementIntegrityE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private static readonly DateOnly AsOf = new(2026, 3, 31);

    [Fact]
    public async Task Partial_pay_credit_note_write_off_then_void_stay_consistent()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 1000m);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 1000m);

        // Partial payment 600 → open 400.
        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 600m, "check", [new Allocation(invoice, 600m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 400m);

        // Credit note 100 → open 300.
        CreditNote cn = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
                new CreditNoteRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice, 100m)], "adjustment")))
            .Content.ReadFromJsonAsync<CreditNote>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, cn.Id);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 300m);

        // Write off the remaining 300 → open 0.
        WriteOff wo = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
                new WriteOffRequest(customer, new DateOnly(2026, 3, 7), [new Allocation(invoice, 300m)], "uncollectible")))
            .Content.ReadFromJsonAsync<WriteOff>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, wo.Id);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 0m);

        // Void the write-off → open back to 300. Voiding an approved (posted) write-off reverses a posted
        // GL entry — an Approver action. The reversal lands PendingApproval; under SoD the Approver authored
        // it, so a distinct actor (the Controller) approves it (matches ReceivablesVoidTests).
        (await approver.PostAsJsonAsync($"/clients/{clientId}/write-offs/{wo.Id}/void",
            new VoidInvoiceRequest("re-evaluated"))).EnsureSuccessStatusCode();
        await ApproveBySourceRefAsync(controller, controller, clientId, wo.Id);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 300m);
    }

    private async Task AssertConsistentAsync(HttpClient http, Guid clientId, Guid invoice, decimal expectedOpen)
    {
        InvoiceView v = (await http.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice}"))!;
        Assert.Equal(expectedOpen, v.OpenBalance);

        await AssertBalancedAsync(http, clientId, AsOf);

        SubledgerReconciliationResponse ar = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(ar.TiesOut, $"A/R subledger did not tie out (variance {ar.Variance}) at open {expectedOpen}");

        SubledgerReconciliationResponse credits = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.CustomerCreditsAccountId}&dimension=Customer"))!;
        Assert.True(credits.TiesOut, $"Customer-credits subledger did not tie out (variance {credits.Variance})");
    }
}
```

> Implementer note: the void path approves the spawned reversal/withdrawal via `ApproveBySourceRefAsync` on the write-off id (the void endpoint reverses the posted entry, which lands PendingApproval). If the void instead withdrew a still-pending entry, the approve loop is a no-op — both cases are handled.

- [ ] **Step 2: Run the test, expect PASS**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~SettlementIntegrityE2eTests"`
Expected: PASS. A failed balance, tie-out, or open-balance at any step is a finding — STOP.

- [ ] **Step 3: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Tests/Settlement/SettlementIntegrityE2eTests.cs
git commit -m "$(cat <<'EOF'
test(receivables): settlement-integrity sweep across a messy disposition sequence

Issue -> partial pay -> credit note -> write off -> void; asserts books
balance, both subledgers tie out, and the derived open balance is exact at
every approved step.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: AP shared helper + bill allocation-boundary rejections

**Files:**
- Create: `Modules/Payables/Accounting101.Payables.Tests/Settlement/BillSettlementScenario.cs`
- Create: `Modules/Payables/Accounting101.Payables.Tests/Settlement/BillAllocationBoundaryE2eTests.cs`

**Interfaces:**
- Produces (used by Tasks 6-8): static helper `BillSettlementScenario` with
  `SetUpChartAsync(HttpClient controller, Guid clientId, PayablesHostFixture f)`,
  `CreateVendorAsync(HttpClient clerk, Guid clientId, string name = "PropCo") -> Task<Guid>`,
  `EnterBillAsync(HttpClient clerk, HttpClient approver, Guid clientId, Guid vendorId, decimal amount) -> Task<Guid>` (single Rent-expense line, BillDate 2026-03-01, enter + approve; returns bill id),
  `DraftBillAsync(HttpClient clerk, Guid clientId, Guid vendorId, decimal amount) -> Task<Guid>` (draft only),
  `ApproveBySourceRefAsync(...)`, `AssertProblemAsync(...)`, `AssertBalancedAsync(...)`.

- [ ] **Step 1: Write the shared AP helper**

Create `Settlement/BillSettlementScenario.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Payables.Tests;

/// <summary>Shared setup + assertion helpers for the payables money/settlement edge-case scenarios.</summary>
internal static class BillSettlementScenario
{
    internal static async Task SetUpChartAsync(HttpClient controller, Guid clientId, PayablesHostFixture f)
    {
        await PutAccountAsync(controller, clientId, f.PayableAccountId,          "2000", "Accounts Payable",  "Liability", "Vendor");
        await PutAccountAsync(controller, clientId, f.CashAccountId,             "1000", "Cash",              "Asset",     null);
        await PutAccountAsync(controller, clientId, f.VendorCreditsAccountId,    "1300", "Vendor Credits",    "Asset",     "Vendor");
        await PutAccountAsync(controller, clientId, f.RentExpenseAccountId,      "5200", "Rent Expense",      "Expense",   null);
        await PutAccountAsync(controller, clientId, f.UtilitiesExpenseAccountId, "5300", "Utilities Expense", "Expense",   null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    internal static async Task<Guid> CreateVendorAsync(HttpClient clerk, Guid clientId, string name = "PropCo")
    {
        Vendor vendor = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest(name, null)))
            .Content.ReadFromJsonAsync<Vendor>())!;
        return vendor.Id;
    }

    internal static async Task<Guid> DraftBillAsync(HttpClient clerk, Guid clientId, Guid vendorId, decimal amount, Guid expenseAccount)
    {
        DraftBillRequest req = new(vendorId, new DateOnly(2026, 3, 1), null, "REF", null,
            [new BillLineBody("Rent", amount, expenseAccount)]);
        Bill draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", req))
            .Content.ReadFromJsonAsync<Bill>())!;
        return draft.Id;
    }

    internal static async Task<Guid> EnterBillAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid vendorId, decimal amount, Guid expenseAccount)
    {
        Guid id = await DraftBillAsync(clerk, clientId, vendorId, amount, expenseAccount);
        Bill entered = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered.Id);
        return entered.Id;
    }

    internal static async Task ApproveBySourceRefAsync(
        HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string substring)
    {
        Assert.Equal(status, resp.StatusCode);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(substring, problem!.Detail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task AssertBalancedAsync(HttpClient http, Guid clientId, DateOnly asOf)
    {
        BalanceSheetResponse sheet = (await http.GetFromJsonAsync<BalanceSheetResponse>(
            $"/clients/{clientId}/statements/balance-sheet?asOf={asOf:yyyy-MM-dd}"))!;
        Assert.True(sheet.IsBalanced,
            $"Balance sheet not balanced as of {asOf}: assets {sheet.TotalAssets} vs L+E {sheet.TotalLiabilitiesAndEquity}");
    }
}
```

> The `EnterBillAsync`/`DraftBillAsync` helpers take the expense account explicitly so callers pass `fixture.RentExpenseAccountId`.

- [ ] **Step 2: Write the bill allocation-boundary tests**

Substrings (verbatim source): `"Allocations cannot exceed the payment amount."`, `"...exceeds its open balance."`, `"Bill {id} does not exist."`, `"...is {status} — only entered bills can be paid."`, `"...belongs to a different vendor."`, `"A payment amount must be greater than zero."`, `"Every allocation amount must be greater than zero."`.

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>Allocation-boundary rejections on POST /bill-payments — each maps to 422 with a specific reason.</summary>
public sealed class BillAllocationBoundaryE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, clerk, approver);
    }

    [Fact]
    public async Task Allocations_exceeding_payment_amount_are_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(bill, 80m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "cannot exceed the payment amount");
    }

    [Fact]
    public async Task Allocation_exceeding_open_balance_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 200m, "check", [new Allocation(bill, 200m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Paying_an_already_settled_bill_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        BillPayment paid = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 100m, "check", [new Allocation(bill, 100m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, paid.Id);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 6), 50m, "check", [new Allocation(bill, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds its open balance");
    }

    [Fact]
    public async Task Allocation_to_a_nonexistent_bill_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(Guid.NewGuid(), 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "does not exist");
    }

    [Fact]
    public async Task Allocation_to_a_draft_bill_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid draft = await DraftBillAsync(clerk, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(draft, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "only entered bills can be paid");
    }

    [Fact]
    public async Task Allocation_to_another_vendors_bill_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendorA = await CreateVendorAsync(clerk, clientId, "PropCo");
        Guid vendorB = await CreateVendorAsync(clerk, clientId, "UtilCo");
        Guid billA = await EnterBillAsync(clerk, approver, clientId, vendorA, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendorB, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(billA, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "belongs to a different vendor");
    }

    [Fact]
    public async Task Zero_payment_amount_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 0m, "check", []));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "payment amount must be greater than zero");
    }

    [Fact]
    public async Task Negative_allocation_amount_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 50m, "check", [new Allocation(bill, -10m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "allocation amount must be greater than zero");
    }
}
```

- [ ] **Step 3: Run the tests, expect PASS**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillAllocationBoundaryE2eTests"`
Expected: all 8 PASS. Deviation → finding → STOP.

- [ ] **Step 4: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Tests/Settlement/BillSettlementScenario.cs \
        Modules/Payables/Accounting101.Payables.Tests/Settlement/BillAllocationBoundaryE2eTests.cs
git commit -m "$(cat <<'EOF'
test(payables): HTTP bill allocation-boundary edge cases + shared scenario helper

Mirrors the receivables allocation-boundary suite on bill-payments: 422 +
reason for over-allocation, exceeds-open-balance, settled/nonexistent/draft/
wrong-vendor targets, and zero/negative amounts.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: AP bill disposition-limit rejections

**Files:**
- Create: `Modules/Payables/Accounting101.Payables.Tests/Settlement/BillDispositionLimitE2eTests.cs`

**Interfaces:**
- Consumes: `BillSettlementScenario` (Task 5).

AP has no write-off/credit-note/refund — only vendor-credit-application (422) and bill-payment void (409).

- [ ] **Step 1: Write the disposition-limit tests**

Substrings: vendor-credit over credit → `"...exceeds available credit ..."`; void already-voided → `"Payment {id} is already voided."`; void missing → `"Payment {id} not found."`.

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>Vendor-credit over-application (422) and bill-payment void guards (409).</summary>
public sealed class BillDispositionLimitE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private async Task<(Guid clientId, HttpClient clerk, HttpClient approver)> ArrangeAsync()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        return (clientId, clerk, approver);
    }

    [Fact]
    public async Task VendorCreditApplication_exceeding_available_credit_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill1 = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        // Overpay bill1 by 10 → vendor credit 10.
        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 110m, "check", [new Allocation(bill1, 100m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        Guid bill2 = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);
        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
            new VendorCreditApplicationRequest(vendor, new DateOnly(2026, 3, 6), [new Allocation(bill2, 50m)]));

        await AssertProblemAsync(resp, HttpStatusCode.UnprocessableEntity, "exceeds available credit");
    }

    [Fact]
    public async Task Voiding_an_already_voided_bill_payment_is_rejected()
    {
        (Guid clientId, HttpClient clerk, HttpClient approver) = await ArrangeAsync();
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 100m, "check", [new Allocation(bill, 100m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // Voiding an approved (posted) bill payment reverses a posted GL entry — an Approver action. Both
        // voids go through the approver; the second hits the already-voided guard before any ledger call.
        (await approver.PostAsJsonAsync($"/clients/{clientId}/bill-payments/{pay.Id}/void",
            new VoidReasonRequest("first void"))).EnsureSuccessStatusCode();

        HttpResponseMessage resp = await approver.PostAsJsonAsync($"/clients/{clientId}/bill-payments/{pay.Id}/void",
            new VoidReasonRequest("second void"));

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "already voided");
    }

    [Fact]
    public async Task Voiding_a_nonexistent_bill_payment_is_rejected()
    {
        (Guid clientId, HttpClient clerk, _) = await ArrangeAsync();

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments/{Guid.NewGuid()}/void",
            new VoidReasonRequest("nope"));

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "not found");
    }
}
```

- [ ] **Step 2: Run the tests, expect PASS**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillDispositionLimitE2eTests"`
Expected: all 3 PASS. Deviation → finding → STOP.

- [ ] **Step 3: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Tests/Settlement/BillDispositionLimitE2eTests.cs
git commit -m "$(cat <<'EOF'
test(payables): HTTP bill disposition-limit edge cases

422 for vendor-credit application exceeding available credit; 409 for
void-of-voided and void-of-missing bill payments.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: AP bill rounding sweep

**Files:**
- Create: `Modules/Payables/Accounting101.Payables.Tests/Settlement/BillRoundingE2eTests.cs`

**Interfaces:**
- Consumes: `BillSettlementScenario` (Task 5).

Bills carry no tax line, so the only rounding scenario is the uneven multi-bill allocation split (tax-rounding is AR-only — see Task 3).

- [ ] **Step 1: Write the rounding test**

```csharp
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using Accounting101.Ledger.Contracts;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>An uneven multi-bill payment split leaves every open balance exact with the A/P subledger
/// tying out and the books balanced.</summary>
public sealed class BillRoundingE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Uneven_split_across_bills_leaves_exact_balances_and_subledger_ties_out()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid b1 = await EnterBillAsync(clerk, approver, clientId, vendor, 33.33m, fixture.RentExpenseAccountId);
        Guid b2 = await EnterBillAsync(clerk, approver, clientId, vendor, 33.33m, fixture.RentExpenseAccountId);
        Guid b3 = await EnterBillAsync(clerk, approver, clientId, vendor, 33.34m, fixture.RentExpenseAccountId);

        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 31), 100.00m, "check",
                    [new Allocation(b1, 33.33m), new Allocation(b2, 33.33m), new Allocation(b3, 33.34m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        foreach (Guid id in new[] { b1, b2, b3 })
        {
            BillView v = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{id}"))!;
            Assert.Equal(0m, v.OpenBalance);
            Assert.Equal(SettlementStatus.Paid, v.SettlementStatus);
        }

        SubledgerReconciliationResponse ap = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.PayableAccountId}&dimension=Vendor"))!;
        Assert.True(ap.TiesOut);
        await AssertBalancedAsync(clerk, clientId, new DateOnly(2026, 3, 31));
    }
}
```

- [ ] **Step 2: Run the test, expect PASS**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillRoundingE2eTests"`
Expected: PASS. Deviation → finding → STOP.

- [ ] **Step 3: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Tests/Settlement/BillRoundingE2eTests.cs
git commit -m "$(cat <<'EOF'
test(payables): rounding sweep — uneven multi-bill allocation split

A 100.00 bill payment split 33.33/33.33/33.34 leaves all open balances zero
with the A/P subledger tying out and the books balanced.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: AP bill settlement-integrity sweep

**Files:**
- Create: `Modules/Payables/Accounting101.Payables.Tests/Settlement/BillSettlementIntegrityE2eTests.cs`

**Interfaces:**
- Consumes: `BillSettlementScenario` (Task 5).

Sequence: bill1 1000, bill2 500. pay1 600 → bill1 open 400; overpay bill2 (pay2 600, allocate 500) → bill2 open 0, vendor credit 100; apply credit 100 to bill1 → bill1 open 300; void **pay1** (a plain payment with no credit entanglement; reversed by the Approver, reversal approved by the Controller under SoD) → bill1 open 900 (only the applied 100 credit remains), bill2 open 0, vendor credit 0. After every approved step the books balance and both subledgers tie out. (We void pay1 rather than pay2 because pay2's overpayment credit has already been applied to bill1; voiding pay2 would drive the vendor-credit balance negative — an incoherent state worth probing separately, not in the integrity sweep.)

- [ ] **Step 1: Write the integrity test**

```csharp
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using Accounting101.Ledger.Contracts;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>A messy bill-settlement sequence (partial pay, overpay-to-credit, apply credit, void) keeps the
/// books balanced and both subledgers tying out at every approved step, with exact derived open balances.</summary>
public sealed class BillSettlementIntegrityE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private static readonly DateOnly AsOf = new(2026, 3, 31);

    [Fact]
    public async Task Partial_pay_overpay_apply_credit_then_void_stay_consistent()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill1 = await EnterBillAsync(clerk, approver, clientId, vendor, 1000m, fixture.RentExpenseAccountId);
        Guid bill2 = await EnterBillAsync(clerk, approver, clientId, vendor, 500m, fixture.RentExpenseAccountId);
        await AssertConsistentAsync(clerk, clientId, bill1, 1000m, bill2, 500m);

        // Partial pay bill1 600 → open 400.
        BillPayment pay1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 600m, "check", [new Allocation(bill1, 600m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay1.Id);
        await AssertConsistentAsync(clerk, clientId, bill1, 400m, bill2, 500m);

        // Overpay bill2: pay 600 allocate 500 → bill2 open 0, vendor credit 100.
        BillPayment pay2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 6), 600m, "check", [new Allocation(bill2, 500m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay2.Id);
        await AssertConsistentAsync(clerk, clientId, bill1, 400m, bill2, 0m);

        // Apply the 100 vendor credit to bill1 → bill1 open 300.
        VendorCreditApplication app = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
                new VendorCreditApplicationRequest(vendor, new DateOnly(2026, 3, 7), [new Allocation(bill1, 100m)])))
            .Content.ReadFromJsonAsync<VendorCreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, app.Id);
        await AssertConsistentAsync(clerk, clientId, bill1, 300m, bill2, 0m);

        // Void pay1 (a plain payment with NO credit entanglement). Voiding an approved (posted) payment
        // reverses a posted GL entry — an Approver action; the reversal lands PendingApproval and, under SoD,
        // is approved by a distinct actor (the Controller). bill1 loses pay1's 600 but keeps the applied 100
        // credit → open 900; bill2 stays 0; vendor credit stays 0.
        // (We void pay1, not pay2: pay2's overpayment credit is already applied to bill1, so voiding pay2
        // would drive the vendor-credit balance negative — an incoherent state worth probing separately, but
        // not what this integrity sweep should assert.)
        (await approver.PostAsJsonAsync($"/clients/{clientId}/bill-payments/{pay1.Id}/void",
            new VoidReasonRequest("reversed"))).EnsureSuccessStatusCode();
        await ApproveBySourceRefAsync(controller, controller, clientId, pay1.Id);
        await AssertConsistentAsync(clerk, clientId, bill1, 900m, bill2, 0m);
    }

    private async Task AssertConsistentAsync(
        HttpClient http, Guid clientId, Guid bill1, decimal open1, Guid bill2, decimal open2)
    {
        BillView v1 = (await http.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{bill1}"))!;
        Assert.Equal(open1, v1.OpenBalance);
        BillView v2 = (await http.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{bill2}"))!;
        Assert.Equal(open2, v2.OpenBalance);

        await AssertBalancedAsync(http, clientId, AsOf);

        SubledgerReconciliationResponse ap = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.PayableAccountId}&dimension=Vendor"))!;
        Assert.True(ap.TiesOut, $"A/P subledger did not tie out (variance {ap.Variance})");

        SubledgerReconciliationResponse vc = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.VendorCreditsAccountId}&dimension=Vendor"))!;
        Assert.True(vc.TiesOut, $"Vendor-credits subledger did not tie out (variance {vc.Variance})");
    }
}
```

> Implementer note on the final step: the void of `pay1` reverses a posted entry (Approver action), and the reversal is approved by the Controller under SoD because the Approver authored it — the same pattern as the AR integrity sweep (Task 4) and the established `ReceivablesVoidTests`. Expected end state: bill1 = 900 (pay1's 600 reversed; the applied 100 credit still stands), bill2 = 0, vendor credit 0, both subledgers tie out. If any open balance, tie-out, or balance differs from the asserted values, STOP and surface it as a finding with observed-vs-expected — do not bend the assertion or touch product code.

- [ ] **Step 2: Run the test, expect PASS**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillSettlementIntegrityE2eTests"`
Expected: PASS, or a clearly-reported finding per the note above.

- [ ] **Step 3: Run BOTH module test projects to confirm no regressions**

Run:
`dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj`
`dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj`
Expected: green (modulo any pre-existing unrelated failures noted in memory, e.g. host-dependent provisioning smoke tests — confirm any failure predates this branch via `git stash` + re-run if unsure).

- [ ] **Step 4: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Tests/Settlement/BillSettlementIntegrityE2eTests.cs
git commit -m "$(cat <<'EOF'
test(payables): settlement-integrity sweep across a messy bill sequence

Enter two bills -> partial pay -> overpay-to-vendor-credit -> apply credit
-> void; asserts books balance, both subledgers tie out, and derived open
balances are exact at every approved step.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- Overpayments → credit balance: exercised in Tasks 2/8 setups (overpay creates credit) and refund/credit-application limits. ✓
- Partial-pay + write-off: Task 4 (AR integrity). ✓
- Uneven allocation rounding: Tasks 3 (AR) + 7 (AP). ✓
- Over-allocation: Tasks 1 + 5. ✓
- Allocation to settled/nonexistent targets: Tasks 1 + 5 (settled, nonexistent, draft, voided, wrong-party). ✓
- Credit memos/refunds: Task 2 (over-credit-note, refund-over-credit). ✓
- Void of a partially-paid invoice: Task 4 voids a write-off on a partially-settled invoice; AR void-of-payment guards in Task 2; AP in Tasks 6/8. ✓ (Note: voiding the *invoice itself* while partially paid is an existing `ReceivablesVoidTests` concern; not duplicated here.)
- Status + substring assertions: every rejection test. ✓
- Exact balances + subledger tie-out: Tasks 3,4,7,8. ✓
- AR + AP mirrored where shape applies: Tasks 1-4 (AR), 5-8 (AP); AP omits write-off/credit-note/refund per spec. ✓
- Discoveries are findings: Global Constraints + per-task "STOP" notes + Task 8 note. ✓

**2. Placeholder scan:** No TBD/TODO; all test bodies are complete; all commands explicit. ✓

**3. Type consistency:** Helper method names/signatures defined in Task 1 (`SettlementScenario`) and Task 5 (`BillSettlementScenario`) are used consistently in dependent tasks. DTO/route names match the confirmed-contracts section. `VoidInvoiceRequest` (AR) vs `VoidReasonRequest` (AP) used correctly per module. `InvoiceView.Invoice.Total` / `BillView.OpenBalance` match the nested view shapes. ✓
