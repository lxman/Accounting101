# Invoicing Cash Application Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the invoicing module record customer payments (allocated across invoices, with over-payment held as customer credit), apply that credit explicitly, and derive each invoice's open balance and settlement status — all posting balanced journal entries through the engine under maker-checker.

**Architecture:** Mirror the existing invoice slice. Pure recipe (`PaymentPosting`) turns a payment/credit-application into one balanced `PostEntryRequest`; an evidentiary `DocumentPaymentStore` persists the documents through `IDocumentStore`; `PaymentService` orchestrates validate → record → post (lands `PendingApproval`) and void → reverse. Open balance, settlement status, and customer credit balance are derived from stored allocations, never stored. Customer Credits is a `Customer`-dimensioned control account so per-customer credit ties out through the engine subledger.

**Tech Stack:** .NET 10, C# 14, xUnit, EphemeralMongo. ASP.NET Core minimal API in `Accounting101.Invoicing.Api`. The pure module (`Accounting101.Invoicing`) stays Contracts-only.

## Global Constraints

- Money is `decimal`, never floating point.
- The pure module depends only on `Accounting101.Ledger.Contracts`; all ASP.NET lives in `.Api`.
- Every ledger entry the module posts lands `PendingApproval` — the module never approves (SoD is the host's job).
- Settlement entries carry `SourceRef` = document id and `SourceType` = `"Payment"` / `"CreditApplication"`.
- Namespaces follow folder structure; project files mirror the existing invoicing projects.
- USD-only; no currency field is added.

---

### Task 1: Settlement derivation (pure)

**Files:**
- Create: `Modules/Accounting101.Invoicing/SettlementStatus.cs`
- Create: `Modules/Accounting101.Invoicing/Settlement.cs`
- Test: `Modules/Accounting101.Invoicing.Tests/SettlementTests.cs`

**Interfaces:**
- Produces: `enum SettlementStatus { Open, PartiallyPaid, Paid }`; `static decimal Settlement.OpenBalance(decimal invoiceTotal, decimal applied)`; `static SettlementStatus Settlement.Status(decimal invoiceTotal, decimal applied)`.

- [ ] **Step 1: Write the failing test**

```csharp
using Accounting101.Invoicing;

namespace Accounting101.Invoicing.Tests;

public sealed class SettlementTests
{
    [Theory]
    [InlineData(100, 0, 100, SettlementStatus.Open)]
    [InlineData(100, 40, 60, SettlementStatus.PartiallyPaid)]
    [InlineData(100, 100, 0, SettlementStatus.Paid)]
    [InlineData(100, 120, -20, SettlementStatus.Paid)] // over-applied still reads Paid
    public void Derives_open_balance_and_status(decimal total, decimal applied, decimal expectedOpen, SettlementStatus expectedStatus)
    {
        Assert.Equal(expectedOpen, Settlement.OpenBalance(total, applied));
        Assert.Equal(expectedStatus, Settlement.Status(total, applied));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~SettlementTests`
Expected: FAIL — `SettlementStatus` / `Settlement` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

`SettlementStatus.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Accounting101.Invoicing;

/// <summary>How far an issued invoice is toward being paid. Derived from applied allocations, never stored.
/// Orthogonal to the Draft/Issued/Void document lifecycle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SettlementStatus { Open, PartiallyPaid, Paid }
```

`Settlement.cs`:
```csharp
namespace Accounting101.Invoicing;

/// <summary>Pure settlement math: an invoice's open balance and status given the total applied to it.</summary>
public static class Settlement
{
    public static decimal OpenBalance(decimal invoiceTotal, decimal applied) => invoiceTotal - applied;

    public static SettlementStatus Status(decimal invoiceTotal, decimal applied) =>
        applied <= 0m ? SettlementStatus.Open
        : applied >= invoiceTotal ? SettlementStatus.Paid
        : SettlementStatus.PartiallyPaid;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~SettlementTests`
Expected: PASS (4 cases).

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing/SettlementStatus.cs Modules/Accounting101.Invoicing/Settlement.cs Modules/Accounting101.Invoicing.Tests/SettlementTests.cs
git commit -m "feat(invoicing): settlement status + open-balance derivation (pure)"
```

---

### Task 2: Payment value types

**Files:**
- Create: `Modules/Accounting101.Invoicing/Allocation.cs`
- Create: `Modules/Accounting101.Invoicing/PaymentBody.cs`
- Create: `Modules/Accounting101.Invoicing/Payment.cs`
- Create: `Modules/Accounting101.Invoicing/PaymentPostingAccounts.cs`
- Test: `Modules/Accounting101.Invoicing.Tests/PaymentTypesTests.cs`

**Interfaces:**
- Produces:
  - `record Allocation(Guid InvoiceId, decimal Amount)`
  - `record PaymentBody(Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations)`
  - `record CreditApplicationBody(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations)`
  - `record Payment { Guid Id; Guid CustomerId; DateOnly Date; decimal Amount; string? Method; IReadOnlyList<Allocation> Allocations; bool Voided; decimal Allocated; decimal Unapplied }`
  - `record CreditApplication { Guid Id; Guid CustomerId; DateOnly Date; IReadOnlyList<Allocation> Allocations; bool Voided; decimal Applied }`
  - `record PaymentPostingAccounts { Guid ReceivableAccountId; Guid CashAccountId; Guid CustomerCreditsAccountId }`

- [ ] **Step 1: Write the failing test**

```csharp
using Accounting101.Invoicing;

namespace Accounting101.Invoicing.Tests;

public sealed class PaymentTypesTests
{
    [Fact]
    public void Payment_computes_allocated_and_unapplied()
    {
        Payment p = new()
        {
            Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new DateOnly(2026, 3, 31), Amount = 500m,
            Allocations = [new Allocation(Guid.NewGuid(), 300m), new Allocation(Guid.NewGuid(), 100m)],
        };
        Assert.Equal(400m, p.Allocated);
        Assert.Equal(100m, p.Unapplied);
        Assert.False(p.Voided);
    }

    [Fact]
    public void CreditApplication_computes_applied()
    {
        CreditApplication c = new()
        {
            Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Date = new DateOnly(2026, 3, 31),
            Allocations = [new Allocation(Guid.NewGuid(), 75m), new Allocation(Guid.NewGuid(), 25m)],
        };
        Assert.Equal(100m, c.Applied);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentTypesTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Write minimal implementation**

`Allocation.cs`:
```csharp
namespace Accounting101.Invoicing;

/// <summary>The atom that reduces an invoice's open balance: an amount applied to one invoice,
/// regardless of the funding document.</summary>
public sealed record Allocation(Guid InvoiceId, decimal Amount);
```

`PaymentBody.cs`:
```csharp
namespace Accounting101.Invoicing;

/// <summary>Stored body of a payment — cash received from a customer, with its allocations across invoices.
/// Allocations may sum to less than Amount; the remainder becomes customer credit.</summary>
public sealed record PaymentBody(
    Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Stored body of a credit application — existing customer credit applied to invoices (no cash).</summary>
public sealed record CreditApplicationBody(
    Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
```

`Payment.cs`:
```csharp
namespace Accounting101.Invoicing;

/// <summary>A recorded customer payment. Voided is derived from the document lifecycle.</summary>
public sealed record Payment
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public string? Method { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }

    public decimal Allocated => Allocations.Sum(a => a.Amount);
    public decimal Unapplied => Amount - Allocated;
}

/// <summary>An application of existing customer credit to invoices.</summary>
public sealed record CreditApplication
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }

    public decimal Applied => Allocations.Sum(a => a.Amount);
}
```

`PaymentPostingAccounts.cs`:
```csharp
namespace Accounting101.Invoicing;

/// <summary>Chart accounts the payment recipes post to. Kept separate from InvoicePostingAccounts because
/// the invoice recipe has no Cash account; the two share Receivable by configuration, not by type.</summary>
public sealed record PaymentPostingAccounts
{
    /// <summary>Accounts Receivable — the control account credited as allocations settle invoices (Customer dim).</summary>
    public required Guid ReceivableAccountId { get; init; }

    /// <summary>Cash — debited for the full payment amount.</summary>
    public required Guid CashAccountId { get; init; }

    /// <summary>Customer Credits — a liability control account (Customer dim) holding unapplied over-payment.</summary>
    public required Guid CustomerCreditsAccountId { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentTypesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing/Allocation.cs Modules/Accounting101.Invoicing/PaymentBody.cs Modules/Accounting101.Invoicing/Payment.cs Modules/Accounting101.Invoicing/PaymentPostingAccounts.cs Modules/Accounting101.Invoicing.Tests/PaymentTypesTests.cs
git commit -m "feat(invoicing): payment/credit-application value types"
```

---

### Task 3: Payment recipe (pure)

**Files:**
- Create: `Modules/Accounting101.Invoicing/PaymentPosting.cs`
- Test: `Modules/Accounting101.Invoicing.Tests/PaymentPostingTests.cs`

**Interfaces:**
- Consumes: `PaymentBody`, `PaymentPostingAccounts`, `Accounting101.Ledger.Contracts.PostEntryRequest`, `PostLineRequest`.
- Produces: `static PostEntryRequest PaymentPosting.ComposePayment(Guid paymentId, PaymentBody body, PaymentPostingAccounts accounts)`; constants `PaymentPosting.PaymentSourceType = "Payment"`, `PaymentPosting.CreditApplicationSourceType = "CreditApplication"`, `PaymentPosting.CustomerDimension = "Customer"`.

- [ ] **Step 1: Write the failing test**

```csharp
using Accounting101.Invoicing;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

public sealed class PaymentPostingTests
{
    private static readonly PaymentPostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(),
        CashAccountId = Guid.NewGuid(),
        CustomerCreditsAccountId = Guid.NewGuid(),
    };

    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    [Fact]
    public void Fully_allocated_payment_posts_cash_and_ar_only()
    {
        Guid customer = Guid.NewGuid();
        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, null,
            [new Allocation(Guid.NewGuid(), 200m), new Allocation(Guid.NewGuid(), 300m)]);

        PostEntryRequest entry = PaymentPosting.ComposePayment(Guid.NewGuid(), body, Accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == Accounts.CashAccountId).Amount);
        PostLineRequest ar = entry.Lines.Single(l => l.AccountId == Accounts.ReceivableAccountId);
        Assert.Equal(500m, ar.Amount);
        Assert.Equal("Credit", ar.Direction);
        Assert.Equal(customer, ar.Dimensions!["Customer"]);
        Assert.DoesNotContain(entry.Lines, l => l.AccountId == Accounts.CustomerCreditsAccountId);
        Assert.Equal("Payment", entry.SourceType);
    }

    [Fact]
    public void Over_payment_routes_the_remainder_to_customer_credits()
    {
        Guid customer = Guid.NewGuid();
        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, null,
            [new Allocation(Guid.NewGuid(), 300m)]);

        PostEntryRequest entry = PaymentPosting.ComposePayment(Guid.NewGuid(), body, Accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(300m, entry.Lines.Single(l => l.AccountId == Accounts.ReceivableAccountId).Amount);
        PostLineRequest credit = entry.Lines.Single(l => l.AccountId == Accounts.CustomerCreditsAccountId);
        Assert.Equal(200m, credit.Amount);
        Assert.Equal(customer, credit.Dimensions!["Customer"]);
    }

    [Fact]
    public void Pure_deposit_posts_cash_and_credit_only()
    {
        PaymentBody body = new(Guid.NewGuid(), new DateOnly(2026, 3, 31), 500m, null, []);

        PostEntryRequest entry = PaymentPosting.ComposePayment(Guid.NewGuid(), body, Accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == Accounts.CustomerCreditsAccountId).Amount);
        Assert.DoesNotContain(entry.Lines, l => l.AccountId == Accounts.ReceivableAccountId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentPostingTests`
Expected: FAIL — `PaymentPosting` does not exist.

- [ ] **Step 3: Write minimal implementation**

`PaymentPosting.cs` (ComposeCreditApplication is added in Task 4):
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

/// <summary>The cash-application recipes: a payment or credit application composes into one balanced
/// journal entry. Pure — request in, wire DTO out — leaving sequencing, approval, and persistence to the engine.</summary>
public static class PaymentPosting
{
    public const string PaymentSourceType = "Payment";
    public const string CreditApplicationSourceType = "CreditApplication";
    public const string CustomerDimension = "Customer";

    public static PostEntryRequest ComposePayment(Guid paymentId, PaymentBody body, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal allocated = body.Allocations.Sum(a => a.Amount);
        decimal remainder = body.Amount - allocated;
        Dictionary<string, Guid> dim = new() { [CustomerDimension] = body.CustomerId };

        List<PostLineRequest> lines = [new(accounts.CashAccountId, "Debit", body.Amount)];
        if (allocated != 0m)
            lines.Add(new(accounts.ReceivableAccountId, "Credit", allocated, Dimensions: dim));
        if (remainder != 0m)
            lines.Add(new(accounts.CustomerCreditsAccountId, "Credit", remainder, Dimensions: dim));

        return new PostEntryRequest(
            Id: null, EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: paymentId, SourceType: PaymentSourceType);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentPostingTests`
Expected: PASS (3 cases).

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing/PaymentPosting.cs Modules/Accounting101.Invoicing.Tests/PaymentPostingTests.cs
git commit -m "feat(invoicing): payment recipe — cash/AR/customer-credit split (pure)"
```

---

### Task 4: Credit-application recipe (pure)

**Files:**
- Modify: `Modules/Accounting101.Invoicing/PaymentPosting.cs`
- Test: `Modules/Accounting101.Invoicing.Tests/PaymentPostingTests.cs` (add a method)

**Interfaces:**
- Produces: `static PostEntryRequest PaymentPosting.ComposeCreditApplication(Guid id, CreditApplicationBody body, PaymentPostingAccounts accounts)`.

- [ ] **Step 1: Write the failing test** (append to `PaymentPostingTests`)

```csharp
    [Fact]
    public void Credit_application_moves_customer_credits_to_ar()
    {
        Guid customer = Guid.NewGuid();
        CreditApplicationBody body = new(customer, new DateOnly(2026, 4, 1),
            [new Allocation(Guid.NewGuid(), 120m), new Allocation(Guid.NewGuid(), 80m)]);

        PostEntryRequest entry = PaymentPosting.ComposeCreditApplication(Guid.NewGuid(), body, Accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        PostLineRequest debit = entry.Lines.Single(l => l.AccountId == Accounts.CustomerCreditsAccountId);
        PostLineRequest credit = entry.Lines.Single(l => l.AccountId == Accounts.ReceivableAccountId);
        Assert.Equal("Debit", debit.Direction);
        Assert.Equal(200m, debit.Amount);
        Assert.Equal("Credit", credit.Direction);
        Assert.Equal(200m, credit.Amount);
        Assert.Equal(customer, debit.Dimensions!["Customer"]);
        Assert.Equal(customer, credit.Dimensions!["Customer"]);
        Assert.Equal("CreditApplication", entry.SourceType);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentPostingTests`
Expected: FAIL — `ComposeCreditApplication` does not exist.

- [ ] **Step 3: Write minimal implementation** (add to `PaymentPosting`)

```csharp
    public static PostEntryRequest ComposeCreditApplication(Guid id, CreditApplicationBody body, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal applied = body.Allocations.Sum(a => a.Amount);
        Dictionary<string, Guid> dim = new() { [CustomerDimension] = body.CustomerId };

        List<PostLineRequest> lines =
        [
            new(accounts.CustomerCreditsAccountId, "Debit", applied, Dimensions: dim),
            new(accounts.ReceivableAccountId, "Credit", applied, Dimensions: dim),
        ];

        return new PostEntryRequest(
            Id: null, EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: id, SourceType: CreditApplicationSourceType);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentPostingTests`
Expected: PASS (4 cases).

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing/PaymentPosting.cs Modules/Accounting101.Invoicing.Tests/PaymentPostingTests.cs
git commit -m "feat(invoicing): credit-application recipe (pure)"
```

---

### Task 5: Payment store port + fake + document-backed implementation

**Files:**
- Create: `Modules/Accounting101.Invoicing/PaymentPorts.cs`
- Create: `Modules/Accounting101.Invoicing/DocumentPaymentStore.cs`
- Modify: `Modules/Accounting101.Invoicing.Tests/Fakes.cs` (add `InMemoryPaymentStore`)
- Test: `Modules/Accounting101.Invoicing.Tests/DocumentPaymentStoreTests.cs`

**Interfaces:**
- Consumes: `Accounting101.Ledger.Contracts.IDocumentStore` (`CreateAsync`, `FinalizeAsync`, `VoidAsync`, `GetAsync<T>`, `QueryAsync<T>`, `DocumentResult<T>`, `DocumentLifecycle`).
- Produces:
  - `interface IPaymentStore` with:
    - `Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default)`
    - `Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default)`
    - `Task VoidAsync(Guid clientId, Guid documentId, CancellationToken ct = default)`
    - `Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)`
    - `Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)`
    - `Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)`
  - `interface IPaymentAccountsProvider { Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default); }`
  - `sealed class DocumentPaymentStore(IDocumentStore documents) : IPaymentStore` using collections `"payments"` and `"credit-applications"`.
  - `internal sealed class InMemoryPaymentStore : IPaymentStore` (test fake).

- [ ] **Step 1: Write the failing test**

```csharp
using Accounting101.Invoicing;

namespace Accounting101.Invoicing.Tests;

public sealed class DocumentPaymentStoreTests(DocumentStoreFixture fixture) : IClassFixture<DocumentStoreFixture>
{
    [Fact]
    public async Task Records_a_payment_then_reads_it_back_by_id_and_customer()
    {
        Guid clientId = Guid.NewGuid();
        Guid customer = Guid.NewGuid();
        IPaymentStore store = new DocumentPaymentStore(fixture.NewStore(clientId));

        PaymentBody body = new(customer, new DateOnly(2026, 3, 31), 500m, "check",
            [new Allocation(Guid.NewGuid(), 300m)]);
        Payment recorded = await store.RecordPaymentAsync(clientId, body);

        Assert.Equal(500m, recorded.Amount);
        Assert.Equal(200m, recorded.Unapplied);
        Assert.False(recorded.Voided);

        Payment? byId = await store.GetPaymentAsync(clientId, recorded.Id);
        Assert.NotNull(byId);
        Assert.Single((await store.GetPaymentsByCustomerAsync(clientId, customer)));

        await store.VoidAsync(clientId, recorded.Id);
        Assert.True((await store.GetPaymentAsync(clientId, recorded.Id))!.Voided);
    }
}
```

> **NOTE on the fixture:** Reuse the existing `DocumentStoreFixture` used by `DocumentInvoiceStoreTests`. Read `Modules/Accounting101.Invoicing.Tests/DocumentStoreFixture.cs` and call whatever method it exposes to get an `IDocumentStore` for a client (the invoice store tests show the exact usage). If it exposes the store differently than `NewStore(clientId)`, match that call instead — keep the test's intent (record → read by id → read by customer → void) identical.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~DocumentPaymentStoreTests`
Expected: FAIL — `IPaymentStore` / `DocumentPaymentStore` do not exist.

- [ ] **Step 3: Write minimal implementation**

`PaymentPorts.cs`:
```csharp
namespace Accounting101.Invoicing;

/// <summary>The module's payment store — evidentiary documents (payments + credit applications) backed by
/// the engine's document store. Voided is derived from the document lifecycle.</summary>
public interface IPaymentStore
{
    Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default);
    Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid documentId, CancellationToken ct = default);
    Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
}

/// <summary>Resolves the chart accounts the payment recipes post to for a given client.</summary>
public interface IPaymentAccountsProvider
{
    Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default);
}
```

`DocumentPaymentStore.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

/// <summary>Persists payments and credit applications through the engine's document store as evidentiary
/// data: created, immediately finalized (locked — there is no draft for a payment), and voidable. The
/// module owns no database connection.</summary>
public sealed class DocumentPaymentStore(IDocumentStore documents) : IPaymentStore
{
    private const string Payments = "payments";
    private const string CreditApplications = "credit-applications";

    public async Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Payments, body, Tags(body.CustomerId), ct);
        await documents.FinalizeAsync(clientId, Payments, id, ct);
        DocumentResult<PaymentBody>? result = await documents.GetAsync<PaymentBody>(clientId, Payments, id, ct);
        return MapPayment(result!);
    }

    public async Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, CreditApplications, body, Tags(body.CustomerId), ct);
        await documents.FinalizeAsync(clientId, CreditApplications, id, ct);
        DocumentResult<CreditApplicationBody>? result = await documents.GetAsync<CreditApplicationBody>(clientId, CreditApplications, id, ct);
        return MapCredit(result!);
    }

    public Task VoidAsync(Guid clientId, Guid documentId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Payments, documentId, ct);

    public async Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        DocumentResult<PaymentBody>? result = await documents.GetAsync<PaymentBody>(clientId, Payments, paymentId, ct);
        return result is null ? null : MapPayment(result);
    }

    public async Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<PaymentBody>> results =
            await documents.QueryAsync<PaymentBody>(clientId, Payments, Tags(customerId), ct);
        return results.Select(MapPayment).ToList();
    }

    public async Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<CreditApplicationBody>> results =
            await documents.QueryAsync<CreditApplicationBody>(clientId, CreditApplications, Tags(customerId), ct);
        return results.Select(MapCredit).ToList();
    }

    private static Dictionary<string, string> Tags(Guid customerId) => new() { ["Customer"] = customerId.ToString() };

    private static bool IsVoided(DocumentLifecycle state) =>
        state is DocumentLifecycle.Voided or DocumentLifecycle.Superseded;

    private static Payment MapPayment(DocumentResult<PaymentBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date, Amount = r.Body.Amount,
        Method = r.Body.Method, Allocations = r.Body.Allocations, Voided = IsVoided(r.State),
    };

    private static CreditApplication MapCredit(DocumentResult<CreditApplicationBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date,
        Allocations = r.Body.Allocations, Voided = IsVoided(r.State),
    };
}
```

> If `VoidAsync` must name the collection that actually holds the document (payments vs credit-applications), and a single id can belong to either, split into `VoidPaymentAsync` / `VoidCreditApplicationAsync` on the interface. For MVP the service only voids payments, so the single `VoidAsync` over the `payments` collection is sufficient; keep it unless the document store rejects an unknown id, in which case add the credit-application overload.

`Fakes.cs` — add `InMemoryPaymentStore`:
```csharp
internal sealed class InMemoryPaymentStore : IPaymentStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, Guid), Payment> _payments = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, Guid), CreditApplication> _credits = new();

    public Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default)
    {
        Payment p = new()
        {
            Id = Guid.NewGuid(), CustomerId = body.CustomerId, Date = body.Date, Amount = body.Amount,
            Method = body.Method, Allocations = body.Allocations, Voided = false,
        };
        _payments[(clientId, p.Id)] = p;
        return Task.FromResult(p);
    }

    public Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default)
    {
        CreditApplication c = new()
        {
            Id = Guid.NewGuid(), CustomerId = body.CustomerId, Date = body.Date,
            Allocations = body.Allocations, Voided = false,
        };
        _credits[(clientId, c.Id)] = c;
        return Task.FromResult(c);
    }

    public Task VoidAsync(Guid clientId, Guid documentId, CancellationToken ct = default)
    {
        if (_payments.TryGetValue((clientId, documentId), out Payment? p))
            _payments[(clientId, documentId)] = p with { Voided = true };
        return Task.CompletedTask;
    }

    public Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default) =>
        Task.FromResult(_payments.GetValueOrDefault((clientId, paymentId)));

    public Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Payment>>(_payments.Where(kv => kv.Key.Item1 == clientId && kv.Value.CustomerId == customerId).Select(kv => kv.Value).ToList());

    public Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CreditApplication>>(_credits.Where(kv => kv.Key.Item1 == clientId && kv.Value.CustomerId == customerId).Select(kv => kv.Value).ToList());
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~DocumentPaymentStoreTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing/PaymentPorts.cs Modules/Accounting101.Invoicing/DocumentPaymentStore.cs Modules/Accounting101.Invoicing.Tests/Fakes.cs Modules/Accounting101.Invoicing.Tests/DocumentPaymentStoreTests.cs
git commit -m "feat(invoicing): payment store — evidentiary persistence + fake"
```

---

### Task 6: PaymentService.RecordPaymentAsync (validate → record → post)

**Files:**
- Create: `Modules/Accounting101.Invoicing/PaymentService.cs`
- Test: `Modules/Accounting101.Invoicing.Tests/PaymentServiceTests.cs`

**Interfaces:**
- Consumes: `IPaymentStore`, `IInvoiceStore` (existing — `GetAsync`), `IPaymentAccountsProvider`, `ILedgerClient` (existing — `PostAsync`, `GetEntriesBySourceRefAsync`, `ReverseAsync`, `VoidAsync`), `PaymentPosting`, `Settlement`, `Invoice` (has `.Total`).
- Produces: `sealed class PaymentService(IPaymentStore payments, IInvoiceStore invoices, IPaymentAccountsProvider accounts, ILedgerClient ledger)` with `Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test**

```csharp
using Accounting101.Invoicing;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

public sealed class PaymentServiceTests
{
    private static readonly PaymentPostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), CustomerCreditsAccountId = Guid.NewGuid(),
    };

    private sealed record Harness(PaymentService Service, FakeLedgerClient Ledger, InMemoryInvoiceStore Invoices, InMemoryPaymentStore Payments);

    private static async Task<(Harness h, Guid clientId, Guid customerId, Invoice invoice)> SetupWithIssuedInvoiceAsync(decimal invoiceTotal)
    {
        Guid clientId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        InMemoryInvoiceStore invoices = new();
        Invoice draft = await invoices.CreateDraftAsync(clientId, new InvoiceBody(
            customerId, new DateOnly(2026, 3, 1), null, 0m, null,
            [new LineBody("Services", 1m, invoiceTotal, false)]));
        Invoice issued = await invoices.FinalizeAsync(clientId, draft.Id);

        FakeLedgerClient ledger = new();
        InMemoryPaymentStore payments = new();
        PaymentService service = new(payments, invoices, new FixedPaymentAccountsProvider(Accounts), ledger);
        return (new Harness(service, ledger, invoices, payments), clientId, customerId, issued);
    }

    [Fact]
    public async Task Records_a_payment_and_posts_a_pending_settlement_entry()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        PaymentBody body = new(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 40m)]);
        Payment recorded = await h.Service.RecordPaymentAsync(clientId, body);

        Assert.NotEqual(Guid.Empty, recorded.Id);
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal("Payment", entry.SourceType);
        Assert.Equal(recorded.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Rejects_a_payment_whose_allocations_exceed_its_amount()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        PaymentBody body = new(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 60m)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordPaymentAsync(clientId, body));
        Assert.Empty(h.Ledger.Posted);
    }

    [Fact]
    public async Task Rejects_an_allocation_exceeding_an_invoice_open_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);

        PaymentBody body = new(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice.Id, 150m)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordPaymentAsync(clientId, body));
    }
}

internal sealed class FixedPaymentAccountsProvider(PaymentPostingAccounts accounts) : IPaymentAccountsProvider
{
    public Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult(accounts);
}
```

> **NOTE:** Confirm the `InvoiceBody` constructor argument order by reading `Modules/Accounting101.Invoicing/InvoiceBody.cs` — it is `(CustomerId, IssueDate, DueDate, TaxRate, Memo, Lines)`. Adjust the `new InvoiceBody(...)` call in the helper to match exactly.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: FAIL — `PaymentService` does not exist.

- [ ] **Step 3: Write minimal implementation**

`PaymentService.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

/// <summary>The cash-application lifecycle: record a payment (allocate across invoices, hold over-payment as
/// customer credit), apply existing credit, and void. Each document posts one balanced entry that lands
/// PendingApproval — approval is the client's normal maker-checker flow. Open balances and credit are
/// derived from stored allocations, never stored.</summary>
public sealed class PaymentService(
    IPaymentStore payments, IInvoiceStore invoices, IPaymentAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<Payment> RecordPaymentAsync(Guid clientId, PaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Amount <= 0m)
            throw new InvalidOperationException("A payment amount must be greater than zero.");
        if (body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("Every allocation amount must be greater than zero.");
        decimal allocated = body.Allocations.Sum(a => a.Amount);
        if (allocated > body.Amount)
            throw new InvalidOperationException("Allocations cannot exceed the payment amount.");

        await ValidateAllocationsAsync(clientId, body.CustomerId, body.Allocations, ct);

        Payment recorded = await payments.RecordPaymentAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        PostEntryRequest entry = PaymentPosting.ComposePayment(recorded.Id, body, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }

    /// <summary>Each allocation must target a live invoice of this customer and not exceed its current open balance.</summary>
    private async Task ValidateAllocationsAsync(Guid clientId, Guid customerId, IReadOnlyList<Allocation> allocations, CancellationToken ct)
    {
        foreach (Allocation a in allocations)
        {
            Invoice invoice = await invoices.GetAsync(clientId, a.InvoiceId, ct)
                ?? throw new InvalidOperationException($"Invoice {a.InvoiceId} does not exist.");
            if (invoice.Status == InvoiceStatus.Void)
                throw new InvalidOperationException($"Invoice {a.InvoiceId} is voided.");
            if (invoice.CustomerId != customerId)
                throw new InvalidOperationException($"Invoice {a.InvoiceId} belongs to a different customer.");

            decimal alreadyApplied = await AppliedToInvoiceAsync(clientId, customerId, a.InvoiceId, ct);
            if (alreadyApplied + a.Amount > invoice.Total)
                throw new InvalidOperationException($"Allocation to invoice {a.InvoiceId} exceeds its open balance.");
        }
    }

    /// <summary>Total non-voided allocations (payments + credit applications) applied to one invoice.</summary>
    private async Task<decimal> AppliedToInvoiceAsync(Guid clientId, Guid customerId, Guid invoiceId, CancellationToken ct)
    {
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        decimal fromPayments = ps.Where(p => !p.Voided).SelectMany(p => p.Allocations).Where(x => x.InvoiceId == invoiceId).Sum(x => x.Amount);
        decimal fromCredits = cs.Where(c => !c.Voided).SelectMany(c => c.Allocations).Where(x => x.InvoiceId == invoiceId).Sum(x => x.Amount);
        return fromPayments + fromCredits;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: PASS (3 cases).

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing/PaymentService.cs Modules/Accounting101.Invoicing.Tests/PaymentServiceTests.cs
git commit -m "feat(invoicing): PaymentService.RecordPaymentAsync with allocation validation"
```

---

### Task 7: Settlement + credit-balance reads on PaymentService

**Files:**
- Modify: `Modules/Accounting101.Invoicing/PaymentService.cs`
- Create: `Modules/Accounting101.Invoicing/InvoiceView.cs`
- Test: `Modules/Accounting101.Invoicing.Tests/PaymentServiceTests.cs` (add methods)

**Interfaces:**
- Produces:
  - `record InvoiceView(Invoice Invoice, decimal OpenBalance, SettlementStatus SettlementStatus)`
  - `Task<InvoiceView?> PaymentService.GetInvoiceViewAsync(Guid clientId, Guid invoiceId, CancellationToken ct = default)`
  - `Task<decimal> PaymentService.GetCustomerCreditBalanceAsync(Guid clientId, Guid customerId, CancellationToken ct = default)`

- [ ] **Step 1: Write the failing test** (append to `PaymentServiceTests`)

```csharp
    [Fact]
    public async Task Invoice_view_reflects_a_partial_payment()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        await h.Service.RecordPaymentAsync(clientId, new PaymentBody(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 40m)]));

        InvoiceView? view = await h.Service.GetInvoiceViewAsync(clientId, invoice.Id);

        Assert.NotNull(view);
        Assert.Equal(60m, view!.OpenBalance);
        Assert.Equal(SettlementStatus.PartiallyPaid, view.SettlementStatus);
    }

    [Fact]
    public async Task Over_payment_raises_the_customer_credit_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        await h.Service.RecordPaymentAsync(clientId, new PaymentBody(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(invoice.Id, 100m)]));

        Assert.Equal(50m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
        InvoiceView? view = await h.Service.GetInvoiceViewAsync(clientId, invoice.Id);
        Assert.Equal(SettlementStatus.Paid, view!.SettlementStatus);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: FAIL — `GetInvoiceViewAsync` / `GetCustomerCreditBalanceAsync` / `InvoiceView` do not exist.

- [ ] **Step 3: Write minimal implementation**

`InvoiceView.cs`:
```csharp
namespace Accounting101.Invoicing;

/// <summary>An invoice plus its derived settlement facet — what a read endpoint returns.</summary>
public sealed record InvoiceView(Invoice Invoice, decimal OpenBalance, SettlementStatus SettlementStatus);
```

Add to `PaymentService` (reuse the private `AppliedToInvoiceAsync` from Task 6):
```csharp
    public async Task<InvoiceView?> GetInvoiceViewAsync(Guid clientId, Guid invoiceId, CancellationToken ct = default)
    {
        Invoice? invoice = await invoices.GetAsync(clientId, invoiceId, ct);
        if (invoice is null) return null;
        decimal applied = await AppliedToInvoiceAsync(clientId, invoice.CustomerId, invoiceId, ct);
        return new InvoiceView(invoice, Settlement.OpenBalance(invoice.Total, applied), Settlement.Status(invoice.Total, applied));
    }

    /// <summary>Unapplied customer credit = non-voided payment remainders minus non-voided credit applications.</summary>
    public async Task<decimal> GetCustomerCreditBalanceAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<Payment> ps = await payments.GetPaymentsByCustomerAsync(clientId, customerId, ct);
        IReadOnlyList<CreditApplication> cs = await payments.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);
        decimal created = ps.Where(p => !p.Voided).Sum(p => p.Unapplied);
        decimal spent = cs.Where(c => !c.Voided).Sum(c => c.Applied);
        return created - spent;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing/PaymentService.cs Modules/Accounting101.Invoicing/InvoiceView.cs Modules/Accounting101.Invoicing.Tests/PaymentServiceTests.cs
git commit -m "feat(invoicing): derived invoice settlement view + customer credit balance"
```

---

### Task 8: PaymentService.RecordCreditApplicationAsync

**Files:**
- Modify: `Modules/Accounting101.Invoicing/PaymentService.cs`
- Test: `Modules/Accounting101.Invoicing.Tests/PaymentServiceTests.cs` (add methods)

**Interfaces:**
- Produces: `Task<CreditApplication> PaymentService.RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test** (append)

```csharp
    [Fact]
    public async Task Applies_existing_credit_to_an_invoice_and_lowers_the_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice first) = await SetupWithIssuedInvoiceAsync(100m);
        // Create $50 of credit via over-payment on the first invoice.
        await h.Service.RecordPaymentAsync(clientId, new PaymentBody(customerId, new DateOnly(2026, 3, 31), 150m, null, [new Allocation(first.Id, 100m)]));
        // A second issued invoice to apply credit against.
        Invoice draft2 = await h.Invoices.CreateDraftAsync(clientId, new InvoiceBody(customerId, new DateOnly(2026, 4, 1), null, 0m, null, [new LineBody("More", 1m, 100m, false)]));
        Invoice second = await h.Invoices.FinalizeAsync(clientId, draft2.Id);

        CreditApplication applied = await h.Service.RecordCreditApplicationAsync(clientId,
            new CreditApplicationBody(customerId, new DateOnly(2026, 4, 2), [new Allocation(second.Id, 50m)]));

        Assert.Equal(50m, applied.Applied);
        Assert.Equal(0m, await h.Service.GetCustomerCreditBalanceAsync(clientId, customerId));
        Assert.Equal(50m, (await h.Service.GetInvoiceViewAsync(clientId, second.Id))!.OpenBalance);
        Assert.Contains(h.Ledger.Posted, e => e.SourceType == "CreditApplication");
    }

    [Fact]
    public async Task Rejects_a_credit_application_exceeding_available_credit()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        // No credit created yet.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordCreditApplicationAsync(clientId,
            new CreditApplicationBody(customerId, new DateOnly(2026, 4, 2), [new Allocation(invoice.Id, 25m)])));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: FAIL — `RecordCreditApplicationAsync` does not exist.

- [ ] **Step 3: Write minimal implementation** (add to `PaymentService`)

```csharp
    public async Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, CreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Allocations.Count == 0 || body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit application needs positive allocations.");

        decimal applying = body.Allocations.Sum(a => a.Amount);
        decimal available = await GetCustomerCreditBalanceAsync(clientId, body.CustomerId, ct);
        if (applying > available)
            throw new InvalidOperationException($"Credit application of {applying} exceeds available credit {available}.");

        await ValidateAllocationsAsync(clientId, body.CustomerId, body.Allocations, ct);

        CreditApplication recorded = await payments.RecordCreditApplicationAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        PostEntryRequest entry = PaymentPosting.ComposeCreditApplication(recorded.Id, body, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing/PaymentService.cs Modules/Accounting101.Invoicing.Tests/PaymentServiceTests.cs
git commit -m "feat(invoicing): apply existing customer credit to invoices"
```

---

### Task 9: PaymentService.VoidPaymentAsync

**Files:**
- Modify: `Modules/Accounting101.Invoicing/PaymentService.cs`
- Test: `Modules/Accounting101.Invoicing.Tests/PaymentServiceTests.cs` (add a method)

**Interfaces:**
- Consumes: `ILedgerClient.GetEntriesBySourceRefAsync`, `.ReverseAsync`, `.VoidAsync` (existing). `EntryResponse` fields `Id`, `Status`, `Posting`, `ReversalOf` (existing — see `Fakes.cs`).
- Produces: `Task<Payment> PaymentService.VoidPaymentAsync(Guid clientId, Guid paymentId, string? reason = null, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test** (append)

```csharp
    [Fact]
    public async Task Voiding_a_payment_restores_the_invoice_open_balance()
    {
        (Harness h, Guid clientId, Guid customerId, Invoice invoice) = await SetupWithIssuedInvoiceAsync(100m);
        Payment p = await h.Service.RecordPaymentAsync(clientId, new PaymentBody(customerId, new DateOnly(2026, 3, 31), 40m, null, [new Allocation(invoice.Id, 40m)]));
        Assert.Equal(60m, (await h.Service.GetInvoiceViewAsync(clientId, invoice.Id))!.OpenBalance);

        await h.Service.VoidPaymentAsync(clientId, p.Id);

        Assert.Equal(100m, (await h.Service.GetInvoiceViewAsync(clientId, invoice.Id))!.OpenBalance);
        Assert.Equal(SettlementStatus.Open, (await h.Service.GetInvoiceViewAsync(clientId, invoice.Id))!.SettlementStatus);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: FAIL — `VoidPaymentAsync` does not exist.

- [ ] **Step 3: Write minimal implementation** (add to `PaymentService`; mirrors `InvoiceService.VoidAsync`)

```csharp
    public async Task<Payment> VoidPaymentAsync(Guid clientId, Guid paymentId, string? reason = null, CancellationToken ct = default)
    {
        Payment payment = await payments.GetPaymentAsync(clientId, paymentId, ct)
            ?? throw new InvalidOperationException($"Payment {paymentId} not found.");
        if (payment.Voided)
            throw new InvalidOperationException($"Payment {paymentId} is already voided.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, paymentId, ct);
        EntryResponse settlement = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for payment {paymentId} to void.");

        if (settlement.Posting == "Posted")
            await ledger.ReverseAsync(clientId, settlement.Id, new ReverseRequest(payment.Date, reason ?? $"Voided payment {paymentId}"), ct);
        else
            await ledger.VoidAsync(clientId, settlement.Id, new VoidRequest(reason ?? $"Voided payment {paymentId}"), ct);

        await payments.VoidAsync(clientId, paymentId, ct);
        return (await payments.GetPaymentAsync(clientId, paymentId, ct))!;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~PaymentServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing/PaymentService.cs Modules/Accounting101.Invoicing.Tests/PaymentServiceTests.cs
git commit -m "feat(invoicing): void a payment — reverse/withdraw and restore balances"
```

---

### Task 10: Configured payment-accounts provider + DI wiring

**Files:**
- Create: `Modules/Accounting101.Invoicing.Api/ConfiguredPaymentAccountsProvider.cs`
- Modify: `Modules/Accounting101.Invoicing.Api/InvoicingServiceExtensions.cs`
- Test: `Modules/Accounting101.Invoicing.Tests/ConfiguredPaymentAccountsProviderTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Extensions.Configuration.IConfiguration`, `IPaymentAccountsProvider`, `PaymentPostingAccounts`.
- Produces: `sealed class ConfiguredPaymentAccountsProvider(IConfiguration configuration) : IPaymentAccountsProvider` reading `Invoicing:Accounts:Receivable`, `Invoicing:Accounts:Cash`, `Invoicing:Accounts:CustomerCredits`.

- [ ] **Step 1: Write the failing test**

```csharp
using Accounting101.Invoicing;
using Accounting101.Invoicing.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Invoicing.Tests;

public sealed class ConfiguredPaymentAccountsProviderTests
{
    [Fact]
    public async Task Reads_the_three_payment_accounts_from_configuration()
    {
        Guid ar = Guid.NewGuid(), cash = Guid.NewGuid(), credits = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Invoicing:Accounts:Receivable"] = ar.ToString(),
            ["Invoicing:Accounts:Cash"] = cash.ToString(),
            ["Invoicing:Accounts:CustomerCredits"] = credits.ToString(),
        }).Build();

        PaymentPostingAccounts accounts = await new ConfiguredPaymentAccountsProvider(config).GetAsync(Guid.NewGuid());

        Assert.Equal(ar, accounts.ReceivableAccountId);
        Assert.Equal(cash, accounts.CashAccountId);
        Assert.Equal(credits, accounts.CustomerCreditsAccountId);
    }

    [Fact]
    public async Task Throws_when_an_account_is_not_configured()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ConfiguredPaymentAccountsProvider(config).GetAsync(Guid.NewGuid()));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~ConfiguredPaymentAccountsProviderTests`
Expected: FAIL — `ConfiguredPaymentAccountsProvider` does not exist.

- [ ] **Step 3: Write minimal implementation**

`ConfiguredPaymentAccountsProvider.cs` (mirror `ConfiguredInvoiceAccountsProvider`):
```csharp
using Accounting101.Invoicing;

namespace Accounting101.Invoicing.Api;

/// <summary>Supplies the chart accounts the payment recipes post to, from configuration
/// (Invoicing:Accounts:Receivable|Cash|CustomerCredits). A single configured set for now.</summary>
public sealed class ConfiguredPaymentAccountsProvider(IConfiguration configuration) : IPaymentAccountsProvider
{
    public Task<PaymentPostingAccounts> GetAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new PaymentPostingAccounts
        {
            ReceivableAccountId = Read("Invoicing:Accounts:Receivable"),
            CashAccountId = Read("Invoicing:Accounts:Cash"),
            CustomerCreditsAccountId = Read("Invoicing:Accounts:CustomerCredits"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Invoicing posting account '{key}' is not configured.");
}
```

Modify `InvoicingServiceExtensions.AddInvoicing` — extend the manifest and register the new services:
```csharp
        services.AddModule(new ModuleIdentity("invoicing"), "Invoicing", manifest =>
        {
            manifest.Reference("customers");
            manifest.Evidentiary("invoices", "Customer");
            manifest.Evidentiary("payments", "Customer");
            manifest.Evidentiary("credit-applications", "Customer");
        });

        services.AddScoped<ICustomerStore, DocumentCustomerStore>();
        services.AddScoped<IInvoiceStore, DocumentInvoiceStore>();
        services.AddScoped<IPaymentStore, DocumentPaymentStore>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<PaymentService>();
        services.AddSingleton<IInvoiceAccountsProvider, ConfiguredInvoiceAccountsProvider>();
        services.AddSingleton<IPaymentAccountsProvider, ConfiguredPaymentAccountsProvider>();
```

> Keep the existing `AddHttpClient<ILedgerClient, HttpLedgerClient>(...)` line unchanged.

- [ ] **Step 4: Run test to verify it passes, then build the host**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~ConfiguredPaymentAccountsProviderTests`
Expected: PASS.
Run: `dotnet build Accounting101.slnx`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing.Api/ConfiguredPaymentAccountsProvider.cs Modules/Accounting101.Invoicing.Api/InvoicingServiceExtensions.cs Modules/Accounting101.Invoicing.Tests/ConfiguredPaymentAccountsProviderTests.cs
git commit -m "feat(invoicing): configured payment accounts + DI/manifest wiring"
```

---

### Task 11: Web requests + endpoints

**Files:**
- Modify: `Modules/Accounting101.Invoicing.Api/InvoicingRequests.cs`
- Modify: `Modules/Accounting101.Invoicing.Api/InvoicingEndpoints.cs`

**Interfaces:**
- Consumes: `PaymentService` (Tasks 6–9), `InvoiceView`, `Allocation`, `PaymentBody`, `CreditApplicationBody`.
- Produces (request DTOs):
  - `record RecordPaymentRequest(Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations)`
  - `record CreditApplicationRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations)`
- Produces (routes): `POST /clients/{clientId}/payments`, `POST /clients/{clientId}/payments/{paymentId}/void`, `POST /clients/{clientId}/credit-applications`, `GET /clients/{clientId}/customers/{customerId}/credit-balance`, and the enriched `GET /clients/{clientId}/invoices/{invoiceId}` returning `InvoiceView`.

- [ ] **Step 1: Add request DTOs** (append to `InvoicingRequests.cs`)

```csharp
/// <summary>Record a customer payment with its allocations across invoices.</summary>
public sealed record RecordPaymentRequest(
    Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Apply a customer's existing credit to invoices.</summary>
public sealed record CreditApplicationRequest(
    Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
```

- [ ] **Step 2: Wire the routes + handlers** (modify `InvoicingEndpoints.MapInvoicingEndpoints` and add handlers)

Add inside `MapInvoicingEndpoints`, after the existing invoice routes:
```csharp
        clients.MapPost("/payments", RecordPayment);
        clients.MapPost("/payments/{paymentId:guid}/void", VoidPayment);
        clients.MapPost("/credit-applications", ApplyCredit);
        clients.MapGet("/customers/{customerId:guid}/credit-balance", GetCreditBalance);
```

Change the existing `GetInvoice` route handler to return the settlement view (replace the `GetInvoice` method body and its dependency on `InvoiceService` with `PaymentService`):
```csharp
    private static async Task<IResult> GetInvoice(
        Guid clientId, Guid invoiceId, PaymentService payments, CancellationToken cancellationToken)
    {
        InvoiceView? view = await payments.GetInvoiceViewAsync(clientId, invoiceId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }
```

Add the new handlers:
```csharp
    private static async Task<IResult> RecordPayment(
        Guid clientId, RecordPaymentRequest request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            Payment recorded = await service.RecordPaymentAsync(clientId,
                new PaymentBody(request.CustomerId, request.Date, request.Amount, request.Method, request.Allocations),
                cancellationToken);
            return Results.Created($"/clients/{clientId}/payments/{recorded.Id}", recorded);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidPayment(
        Guid clientId, Guid paymentId, VoidInvoiceRequest? request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            Payment voided = await service.VoidPaymentAsync(clientId, paymentId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ApplyCredit(
        Guid clientId, CreditApplicationRequest request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            CreditApplication applied = await service.RecordCreditApplicationAsync(clientId,
                new CreditApplicationBody(request.CustomerId, request.Date, request.Allocations), cancellationToken);
            return Results.Created($"/clients/{clientId}/credit-applications/{applied.Id}", applied);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> GetCreditBalance(
        Guid clientId, Guid customerId, PaymentService service, CancellationToken cancellationToken)
    {
        decimal balance = await service.GetCustomerCreditBalanceAsync(clientId, customerId, cancellationToken);
        return Results.Ok(new { customerId, creditBalance = balance });
    }
```

> The `GetInvoice` handler now depends on `PaymentService` instead of `InvoiceService`. That is intentional — the view needs settlement data. The `using Accounting101.Invoicing;` already present covers the new types.

- [ ] **Step 3: Build**

Run: `dotnet build Accounting101.slnx`
Expected: Build succeeded, 0 warnings. (No new unit test here — behavior is covered by the service tests and proven end-to-end in Task 12.)

- [ ] **Step 4: Commit**

```bash
git add Modules/Accounting101.Invoicing.Api/InvoicingRequests.cs Modules/Accounting101.Invoicing.Api/InvoicingEndpoints.cs
git commit -m "feat(invoicing): payment, credit-application, and settlement-view endpoints"
```

---

### Task 12: End-to-end cash-application tests through the real host

**Files:**
- Modify: `Modules/Accounting101.Invoicing.Tests/InvoicingHostFixture.cs` (add Cash + Customer Credits account ids + config)
- Create: `Modules/Accounting101.Invoicing.Tests/CashApplicationTests.cs`

**Interfaces:**
- Consumes: `InvoicingHostFixture` (existing — `SeedSodClientAsync`, `ClientFor`, `ReceivableAccountId`, `RevenueAccountId`, `SalesTaxPayableAccountId`, `ConfigureWebHost` with `UseSetting`). The fixture already maps `Invoicing:Accounts:Receivable` to `ReceivableAccountId`; reuse that same id for payments.

- [ ] **Step 1: Extend the host fixture** (add to `InvoicingHostFixture`)

Add properties next to the existing account ids:
```csharp
    public Guid CashAccountId { get; } = Guid.NewGuid();
    public Guid CustomerCreditsAccountId { get; } = Guid.NewGuid();
```

Add to `ConfigureWebHost`, next to the existing `Invoicing:Accounts:*` settings:
```csharp
        builder.UseSetting("Invoicing:Accounts:Cash", CashAccountId.ToString());
        builder.UseSetting("Invoicing:Accounts:CustomerCredits", CustomerCreditsAccountId.ToString());
```

- [ ] **Step 2: Write the failing end-to-end test**

```csharp
using System.Net.Http.Json;
using Accounting101.Invoicing;
using Accounting101.Invoicing.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

/// <summary>Proves cash application end-to-end through the real host: a payment settles invoices, an
/// over-payment becomes customer credit, that credit applies to a later invoice, and a voided payment
/// restores balances — all while the A/R and Customer Credits subledgers tie out.</summary>
public sealed class CashApplicationTests(InvoicingHostFixture fixture) : IClassFixture<InvoicingHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await Put(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", "Customer");
        await Put(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await Put(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await Put(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await Put(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
    }

    private static async Task Put(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    private static async Task<Guid> IssueInvoiceAsync(HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draft = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice created = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draft)).Content.ReadFromJsonAsync<Invoice>())!;
        await clerk.PostAsync($"/clients/{clientId}/invoices/{created.Id}/issue", null);
        await ApproveBySourceRefAsync(clerk, approver, clientId, created.Id);
        return created.Id;
    }

    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>($"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Payment_overpayment_credit_application_and_void_keep_the_books_tied_out()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid invoice1 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);

        // Over-pay invoice1 by 50 -> invoice Paid, customer credit 50.
        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 31), 150m, "check", [new Allocation(invoice1, 100m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        InvoiceView v1 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice1}"))!;
        Assert.Equal(SettlementStatus.Paid, v1.SettlementStatus);
        Assert.Equal(0m, v1.OpenBalance);

        // A second invoice, then apply the 50 credit to it.
        Guid invoice2 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);
        CreditApplication applied = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
            new CreditApplicationRequest(customer.Id, new DateOnly(2026, 4, 2), [new Allocation(invoice2, 50m)])))
            .Content.ReadFromJsonAsync<CreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, applied.Id);

        InvoiceView v2 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice2}"))!;
        Assert.Equal(50m, v2.OpenBalance);
        Assert.Equal(SettlementStatus.PartiallyPaid, v2.SettlementStatus);

        // Both subledgers tie out.
        SubledgerReconciliationResponse ar = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(ar.TiesOut);
        SubledgerReconciliationResponse credits = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.CustomerCreditsAccountId}&dimension=Customer"))!;
        Assert.True(credits.TiesOut);
    }
}
```

> **NOTE:** Match `AccountRequest`, `EntryResponse`, and `SubledgerReconciliationResponse` to their real shapes — they are already used by `InvoicingIssueTests.cs`; copy the exact property usage from there. Use the same auth pattern (`SeedSodClientAsync` returns controller/clerk/approver clients).

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests --filter FullyQualifiedName~CashApplicationTests`
Expected: FAIL initially if any wiring is missing; iterate until green.

- [ ] **Step 4: Run the whole invoicing suite**

Run: `dotnet test Modules/Accounting101.Invoicing.Tests`
Expected: PASS (all prior tests + the new cash-application unit and e2e tests).

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing.Tests/InvoicingHostFixture.cs Modules/Accounting101.Invoicing.Tests/CashApplicationTests.cs
git commit -m "test(invoicing): cash application end-to-end — payment, credit, void, subledgers tie out"
```

---

## Self-review

**Spec coverage:**
- Allocation atom, Payment + CreditApplication documents → Tasks 2, 3, 4, 5, 6, 8.
- Derived open balance / settlement-status axis → Tasks 1, 7.
- Customer Credits as a Customer-dimensioned control account → Task 3 recipe (dimension on the credit line) + Task 12 chart setup (`RequiredDimension = "Customer"`).
- Ledger effects (both entry shapes, PendingApproval, SourceRef/SourceType) → Tasks 3, 4, 6, 8.
- PaymentPostingAccounts (Receivable/Cash/CustomerCredits) + provider → Tasks 2, 10.
- Corrections (void reverses/withdraws, restores balances) → Task 9, proven in Task 12.
- Validation 422 cases → Task 6 (amount, per-invoice open balance, missing/void invoice, allocations>amount) + Task 8 (credit exceeds available). Non-positive allocation covered in Task 6.
- Web surface (payments, void, credit-applications, credit-balance, enriched invoice view) → Task 11; list-with-settlement-filter is **deferred** — see note below.
- End-to-end + subledger tie-out → Task 12.

**Deferred from spec, intentionally:** `GET /invoices?customerId=&settlement=open|paid` (list with settlement filter). Not in any task — it needs a customer-scoped invoice listing that returns views, which is read-only sugar over `GetInvoiceViewAsync`. Add as a follow-on task only if wanted; flag to the user before execution. A/R aging is already out of scope per the spec.

**Placeholder scan:** No TBD/TODO/"handle errors" — every step has real code. The three `> NOTE` callouts point the implementer at exact existing files to match constructor/DTO shapes (`InvoiceBody`, `DocumentStoreFixture`, `AccountRequest`/`EntryResponse`/`SubledgerReconciliationResponse`) rather than guessing — these are verification instructions, not placeholders.

**Type consistency:** `IPaymentStore` method names are used identically in Tasks 5–9. `PaymentPosting.ComposePayment` / `ComposeCreditApplication`, `Settlement.OpenBalance` / `Status`, `PaymentService.RecordPaymentAsync` / `RecordCreditApplicationAsync` / `VoidPaymentAsync` / `GetInvoiceViewAsync` / `GetCustomerCreditBalanceAsync`, and `InvoiceView(Invoice, OpenBalance, SettlementStatus)` match across producing and consuming tasks. Config keys `Invoicing:Accounts:Cash` / `:CustomerCredits` match between Task 10 (provider) and Task 12 (fixture).
