# Accounts Payable (Bills) Module Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the full accounts-payable cycle as a new `Accounting101.Payables` module (vendors, bills, bill payments with vendor credit, derived settlement, void) mirroring the A/R module flipped to the liability/expense side, after extracting the settlement primitives shared with A/R into one library.

**Architecture:** A new pure `Accounting101.Settlement` library holds the domain-agnostic allocation/settlement types; invoicing is refactored to consume it; then a new `Accounting101.Payables` (pure) + `.Api` module mirrors invoicing's structure. Each document posts ONE balanced journal entry through the engine (lands `PendingApproval`); open balances / settlement status / vendor credit are derived from stored allocations, never stored. Vendor Credits is a `Vendor`-dimensioned ASSET control account, the mirror of A/R's liability Customer Credits.

**Tech Stack:** .NET 10, C# 14, xUnit, EphemeralMongo. ASP.NET Core minimal API in `.Api`. Pure modules depend only on `Accounting101.Ledger.Contracts` (+ `Accounting101.Settlement`).

## Global Constraints

- Money is `decimal`, never floating point.
- Pure modules (`Accounting101.Settlement`, `Accounting101.Payables`) take no ASP.NET dependency; ASP.NET lives in `.Api`.
- Every ledger entry the module posts lands `PendingApproval` — the module never approves (SoD is the host's job).
- Entries carry `SourceRef` = document id and `SourceType` = `"Bill"` / `"BillPayment"` / `"VendorCreditApplication"`.
- Vendor Credits is an ASSET control account requiring the `Vendor` dimension; A/P is a liability control account requiring `Vendor`.
- Namespaces follow folder structure. New module projects mirror the existing `Accounting101.Invoicing*` project files (same Sdk/FrameworkReference/Using items).
- USD-only; no currency field.

## Reference files (mirror these — read them when a task says to)

The payables module parallels invoicing file-for-file. When a task says "mirror X", read X and flip Customer→Vendor, Invoice→Bill, Revenue→Expense, Receivable→Payable, Customer Credits (liability)→Vendor Credits (asset):
- `Modules/Accounting101.Invoicing/DocumentCustomerStore.cs`, `Customer.cs`, `CustomerBody.cs`
- `Modules/Accounting101.Invoicing/DocumentInvoiceStore.cs`, `Invoice.cs`, `InvoiceBody.cs`, `InvoiceStatus.cs`
- `Modules/Accounting101.Invoicing/DocumentPaymentStore.cs`, `PaymentPorts.cs`, `PaymentService.cs`, `InvoiceView.cs`
- `Modules/Accounting101.Invoicing.Api/ConfiguredInvoiceAccountsProvider.cs`, `InvoicingEndpoints.cs`, `InvoicingRequests.cs`, `InvoicingServiceExtensions.cs`
- `Modules/Accounting101.Invoicing.Tests/Fakes.cs`, `DocumentStoreFixture.cs`, `InvoicingHostFixture.cs`, `InvoicingIssueTests.cs`, `CashApplicationTests.cs`

---

### Task 1: Shared settlement library

**Files:**
- Create: `Modules/Accounting101.Settlement/Accounting101.Settlement.csproj`
- Create: `Modules/Accounting101.Settlement/Allocation.cs`
- Create: `Modules/Accounting101.Settlement/SettlementStatus.cs`
- Create: `Modules/Accounting101.Settlement/SettlementFilter.cs`
- Create: `Modules/Accounting101.Settlement/Settlement.cs`
- Create: `Modules/Accounting101.Settlement.Tests/Accounting101.Settlement.Tests.csproj`
- Create: `Modules/Accounting101.Settlement.Tests/SettlementTests.cs`
- Modify: `Accounting101.slnx` (add both projects)

**Interfaces:**
- Produces: namespace `Accounting101.Settlement` — `record Allocation(Guid TargetId, decimal Amount)`; `enum SettlementStatus { Open, PartiallyPaid, Paid }`; `enum SettlementFilter { Open, Paid }`; `static class Settlement` with `decimal OpenBalance(decimal total, decimal applied)` and `SettlementStatus Status(decimal total, decimal applied)`.

- [ ] **Step 1: Create the projects.** Copy `Modules/Accounting101.Invoicing/Accounting101.Invoicing.csproj` to the new `Accounting101.Settlement.csproj` but REMOVE any `<ProjectReference>` / `<FrameworkReference>` (this library is pure, zero-dependency — keep only the `<PropertyGroup>` with TargetFramework net10.0, Nullable enable, ImplicitUsings enable). Create `Accounting101.Settlement.Tests.csproj` by copying `Modules/Accounting101.Invoicing.Tests/Accounting101.Invoicing.Tests.csproj` and changing its single `<ProjectReference>` to point at `..\Accounting101.Settlement\Accounting101.Settlement.csproj`. Add both projects to `Accounting101.slnx` (mirror how an existing `<Project Path="..." />` line looks).

- [ ] **Step 2: Write the failing test** (`SettlementTests.cs`):

```csharp
using Accounting101.Settlement;

namespace Accounting101.Settlement.Tests;

public sealed class SettlementTests
{
    [Theory]
    [InlineData(100, 0, 100, SettlementStatus.Open)]
    [InlineData(100, 40, 60, SettlementStatus.PartiallyPaid)]
    [InlineData(100, 100, 0, SettlementStatus.Paid)]
    [InlineData(100, 120, -20, SettlementStatus.Paid)]
    public void Derives_open_balance_and_status(decimal total, decimal applied, decimal expectedOpen, SettlementStatus expectedStatus)
    {
        Assert.Equal(expectedOpen, Accounting101.Settlement.Settlement.OpenBalance(total, applied));
        Assert.Equal(expectedStatus, Accounting101.Settlement.Settlement.Status(total, applied));
    }

    [Fact]
    public void Allocation_carries_a_generic_target_id()
    {
        Guid target = Guid.NewGuid();
        Allocation a = new(target, 50m);
        Assert.Equal(target, a.TargetId);
        Assert.Equal(50m, a.Amount);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Settlement.Tests`
Expected: FAIL — types do not exist (compile error).

- [ ] **Step 4: Write the implementation.**

`Allocation.cs`:
```csharp
namespace Accounting101.Settlement;

/// <summary>The atom that reduces a document's open balance: an amount applied to one target document
/// (an invoice or a bill), regardless of the funding document.</summary>
public sealed record Allocation(Guid TargetId, decimal Amount);
```

`SettlementStatus.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Accounting101.Settlement;

/// <summary>How far a document is toward being settled. Derived from applied allocations, never stored;
/// orthogonal to a document's own lifecycle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SettlementStatus { Open, PartiallyPaid, Paid }
```

`SettlementFilter.cs`:
```csharp
namespace Accounting101.Settlement;

/// <summary>Filter for listing documents by settlement: Open = any unpaid balance; Paid = fully settled.</summary>
public enum SettlementFilter { Open, Paid }
```

`Settlement.cs`:
```csharp
namespace Accounting101.Settlement;

/// <summary>Pure settlement math: a document's open balance and status given the total applied to it.</summary>
public static class Settlement
{
    public static decimal OpenBalance(decimal total, decimal applied) => total - applied;

    public static SettlementStatus Status(decimal total, decimal applied) =>
        applied <= 0m ? SettlementStatus.Open
        : applied >= total ? SettlementStatus.Paid
        : SettlementStatus.PartiallyPaid;
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test Modules/Accounting101.Settlement.Tests`
Expected: PASS (5 cases).

- [ ] **Step 6: Commit**

```bash
git add Modules/Accounting101.Settlement Modules/Accounting101.Settlement.Tests Accounting101.slnx
git commit -m "feat(settlement): shared allocation + settlement primitives library"
```

---

### Task 2: Refactor invoicing onto the shared library

**Files:**
- Modify: `Modules/Accounting101.Invoicing/Accounting101.Invoicing.csproj` (add ProjectReference to Settlement)
- Delete: `Modules/Accounting101.Invoicing/Allocation.cs`, `Settlement.cs`, `SettlementStatus.cs`, `SettlementFilter.cs`
- Modify: `Modules/Accounting101.Invoicing/PaymentPosting.cs`, `PaymentService.cs`, `InvoiceView.cs`
- Modify: `Modules/Accounting101.Invoicing.Api/InvoicingRequests.cs`, `InvoicingEndpoints.cs`
- Modify: any test files referencing these types

**Interfaces:**
- Consumes: `Accounting101.Settlement` (Task 1).
- Produces: invoicing module with no local settlement types; `Allocation.TargetId` replaces `Allocation.InvoiceId` everywhere.

- [ ] **Step 1: Add the reference and delete the local copies.** Add `<ProjectReference Include="..\..\Modules\Accounting101.Settlement\Accounting101.Settlement.csproj" />` to `Accounting101.Invoicing.csproj` (match the relative-path style of its existing reference to `Accounting101.Ledger.Contracts`). Delete the four files listed above.

- [ ] **Step 2: Build to see every break**

Run: `dotnet build Modules/Accounting101.Invoicing/Accounting101.Invoicing.csproj`
Expected: FAIL — compile errors at each use of the deleted types and `Allocation.InvoiceId`.

- [ ] **Step 3: Fix every reference.** Add `using Accounting101.Settlement;` to each file that used the deleted types (`PaymentPosting.cs`, `PaymentService.cs`, `InvoiceView.cs`, `InvoicingRequests.cs`, `InvoicingEndpoints.cs`, and any test file). Rename `Allocation.InvoiceId` → `Allocation.TargetId` at every usage. In `PaymentService.cs` these are in `ValidateAllocationsAsync`, `AppliedToInvoiceAsync`, and `ListInvoiceViewsAsync` (the `.Where(x => x.InvoiceId == invoiceId)` / `a.InvoiceId` references become `.TargetId`). Do NOT change any behavior — this is a mechanical rename + namespace move.

- [ ] **Step 4: Build, then run the full invoicing suite**

Run: `dotnet build Accounting101.slnx`
Expected: Build succeeded, 0 warnings.
Run: `dotnet test Modules/Accounting101.Invoicing.Tests`
Expected: PASS — 48/48 (the refactor preserved all behavior).

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Invoicing Modules/Accounting101.Invoicing.Api Modules/Accounting101.Invoicing.Tests
git commit -m "refactor(invoicing): consume the shared Accounting101.Settlement library"
```

---

### Task 3: Scaffold the payables projects

**Files:**
- Create: `Modules/Accounting101.Payables/Accounting101.Payables.csproj`
- Create: `Modules/Accounting101.Payables.Api/Accounting101.Payables.Api.csproj`
- Create: `Modules/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj`
- Modify: `Accounting101.slnx`

**Interfaces:**
- Produces: three buildable empty projects mirroring the invoicing projects, referencing `Accounting101.Settlement` and `Accounting101.Ledger.Contracts` (pure) / the engine Api (Api project) as invoicing does.

- [ ] **Step 1: Create the project files by mirroring invoicing.** Read `Modules/Accounting101.Invoicing/Accounting101.Invoicing.csproj`, `Modules/Accounting101.Invoicing.Api/Accounting101.Invoicing.Api.csproj`, and `Modules/Accounting101.Invoicing.Tests/Accounting101.Invoicing.Tests.csproj`. Create the three payables csproj files identical in Sdk/FrameworkReference/Using items, with these reference changes:
  - `Accounting101.Payables.csproj`: same references as `Accounting101.Invoicing.csproj` (→ `Accounting101.Ledger.Contracts`) PLUS a `<ProjectReference>` to `..\Accounting101.Settlement\Accounting101.Settlement.csproj`.
  - `Accounting101.Payables.Api.csproj`: mirror `Accounting101.Invoicing.Api.csproj` (same Sdk/FrameworkReference/Using), referencing `..\Accounting101.Payables\Accounting101.Payables.csproj` and the same engine Api project the invoicing Api references.
  - `Accounting101.Payables.Tests.csproj`: mirror `Accounting101.Invoicing.Tests.csproj`, referencing `..\Accounting101.Payables\Accounting101.Payables.csproj`, `..\Accounting101.Payables.Api\Accounting101.Payables.Api.csproj`, and the same engine/test references the invoicing tests use (it boots the host fixture, so it needs whatever `Accounting101.Invoicing.Tests.csproj` references for that).
  Add all three to `Accounting101.slnx`.

- [ ] **Step 2: Build**

Run: `dotnet build Accounting101.slnx`
Expected: Build succeeded, 0 warnings (empty projects compile).

- [ ] **Step 3: Commit**

```bash
git add Modules/Accounting101.Payables Modules/Accounting101.Payables.Api Modules/Accounting101.Payables.Tests Accounting101.slnx
git commit -m "chore(payables): scaffold module projects"
```

---

### Task 4: Vendor entity + store

**Files:**
- Create: `Modules/Accounting101.Payables/Vendor.cs`, `VendorBody.cs`, `VendorPorts.cs`, `DocumentVendorStore.cs`
- Create: `Modules/Accounting101.Payables.Tests/Fakes.cs` (start it with `InMemoryVendorStore`)
- Test: `Modules/Accounting101.Payables.Tests/DocumentVendorStoreTests.cs`

**Interfaces:**
- Consumes: `Accounting101.Ledger.Contracts.IDocumentStore` (reference tier: `PutAsync`, `GetAsync<T>`).
- Produces: `record Vendor { Guid Id; string Name; string? Email }`; `record VendorBody(string Name, string? Email)`; `interface IVendorStore { Task SaveAsync(Guid clientId, Vendor v, CancellationToken ct = default); Task<Vendor?> GetAsync(Guid clientId, Guid vendorId, CancellationToken ct = default); }`; `sealed class DocumentVendorStore(IDocumentStore documents) : IVendorStore` over collection `"vendors"`; `internal sealed class InMemoryVendorStore : IVendorStore`.

- [ ] **Step 1: Read the parallel** `Modules/Accounting101.Invoicing/DocumentCustomerStore.cs`, `Customer.cs`, `CustomerBody.cs`, and the `ICustomerStore` in `InvoicingPorts.cs`, plus `InMemoryCustomerStore` in the invoicing `Fakes.cs`. The vendor store is the same shape with Customer→Vendor.

- [ ] **Step 2: Write the failing test** (`DocumentVendorStoreTests.cs`). Mirror the customer-store test if one exists; otherwise:

```csharp
using Accounting101.Payables;

namespace Accounting101.Payables.Tests;

public sealed class DocumentVendorStoreTests(PayablesDocumentStoreFixture fixture) : IClassFixture<PayablesDocumentStoreFixture>
{
    [Fact]
    public async Task Saves_then_reads_a_vendor_back()
    {
        IVendorStore store = new DocumentVendorStore(fixture.Store);
        Vendor v = new() { Id = Guid.NewGuid(), Name = "PropCo", Email = null };
        await store.SaveAsync(fixture.ClientId, v);

        Vendor? read = await store.GetAsync(fixture.ClientId, v.Id);
        Assert.NotNull(read);
        Assert.Equal("PropCo", read!.Name);
    }
}
```

> The `PayablesDocumentStoreFixture` is created in this task: copy `Modules/Accounting101.Invoicing.Tests/DocumentStoreFixture.cs` to `Modules/Accounting101.Payables.Tests/PayablesDocumentStoreFixture.cs`, rename the class, change the `ModuleIdentity`/`RegisterModuleAsync` key to `"payables"`, and build the manifest with `.Reference("vendors").Evidentiary("bills", "Vendor").Evidentiary("bill-payments", "Vendor").Evidentiary("vendor-credit-applications", "Vendor")`.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~DocumentVendorStoreTests`
Expected: FAIL — types do not exist.

- [ ] **Step 4: Implement** (mirror the customer files). `Vendor.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>A vendor — the payables module's reference entity (mirrors the invoicing Customer).</summary>
public sealed record Vendor
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Email { get; init; }
}
```
`VendorBody.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>The stored body of a vendor (reference data).</summary>
public sealed record VendorBody(string Name, string? Email);
```
`VendorPorts.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>The module's vendor store — reference data via the engine's document store.</summary>
public interface IVendorStore
{
    Task SaveAsync(Guid clientId, Vendor vendor, CancellationToken ct = default);
    Task<Vendor?> GetAsync(Guid clientId, Guid vendorId, CancellationToken ct = default);
}
```
`DocumentVendorStore.cs` — mirror `DocumentCustomerStore.cs` exactly with Customer→Vendor, collection `"vendors"`, body `VendorBody(vendor.Name, vendor.Email)`, mapping back to `Vendor { Id = id, Name = body.Name, Email = body.Email }`. Add `InMemoryVendorStore` to the payables `Fakes.cs` mirroring `InMemoryCustomerStore`.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~DocumentVendorStoreTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Modules/Accounting101.Payables/Vendor.cs Modules/Accounting101.Payables/VendorBody.cs Modules/Accounting101.Payables/VendorPorts.cs Modules/Accounting101.Payables/DocumentVendorStore.cs Modules/Accounting101.Payables.Tests/Fakes.cs Modules/Accounting101.Payables.Tests/PayablesDocumentStoreFixture.cs Modules/Accounting101.Payables.Tests/DocumentVendorStoreTests.cs
git commit -m "feat(payables): vendor entity + document store"
```

---

### Task 5: Bill value types

**Files:**
- Create: `Modules/Accounting101.Payables/Bill.cs`, `BillBody.cs`, `BillStatus.cs`, `BillView.cs`, `BillPostingAccounts.cs`
- Test: `Modules/Accounting101.Payables.Tests/BillTypesTests.cs`

**Interfaces:**
- Consumes: `Accounting101.Settlement.SettlementStatus`.
- Produces:
  - `enum BillStatus { Draft, Entered, Void }`
  - `record BillLine { string Description; decimal Amount; Guid ExpenseAccountId }`
  - `record Bill { Guid Id; Guid VendorId; string? Number; DateOnly BillDate; DateOnly? DueDate; string? VendorReference; string? Memo; BillStatus Status; IReadOnlyList<BillLine> Lines; decimal Total }`
  - `record BillLineBody(string Description, decimal Amount, Guid ExpenseAccountId)`
  - `record BillBody(Guid VendorId, DateOnly BillDate, DateOnly? DueDate, string? VendorReference, string? Memo, IReadOnlyList<BillLineBody> Lines)`
  - `record BillView(Bill Bill, decimal OpenBalance, Accounting101.Settlement.SettlementStatus SettlementStatus)`
  - `record BillPostingAccounts { Guid PayableAccountId }`

- [ ] **Step 1: Write the failing test** (`BillTypesTests.cs`):

```csharp
using Accounting101.Payables;

namespace Accounting101.Payables.Tests;

public sealed class BillTypesTests
{
    [Fact]
    public void Bill_total_sums_its_lines()
    {
        Bill bill = new()
        {
            Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), BillDate = new DateOnly(2026, 3, 1),
            Status = BillStatus.Draft,
            Lines =
            [
                new BillLine { Description = "Rent", Amount = 6000m, ExpenseAccountId = Guid.NewGuid() },
                new BillLine { Description = "Utilities", Amount = 800m, ExpenseAccountId = Guid.NewGuid() },
            ],
        };
        Assert.Equal(6800m, bill.Total);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillTypesTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement.** `BillStatus.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Accounting101.Payables;

/// <summary>Where a bill sits in its own lifecycle. Draft has no ledger effect; entering it posts the
/// A/P entry; voiding it reverses that entry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BillStatus { Draft, Entered, Void }
```
`Bill.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>One line of a bill: an expense amount coded to a specific expense account.</summary>
public sealed record BillLine
{
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    public required Guid ExpenseAccountId { get; init; }
}

/// <summary>A vendor bill: the commercial document the payables module owns. Its money rolls up into one
/// balanced journal entry when the bill is entered; the per-line detail stays here.</summary>
public sealed record Bill
{
    public required Guid Id { get; init; }
    public required Guid VendorId { get; init; }
    /// <summary>Internal bill number from the engine's gapless sequence at enter; null while a draft.</summary>
    public string? Number { get; init; }
    public required DateOnly BillDate { get; init; }
    public DateOnly? DueDate { get; init; }
    /// <summary>The vendor's own invoice number (external reference).</summary>
    public string? VendorReference { get; init; }
    public string? Memo { get; init; }
    public BillStatus Status { get; init; } = BillStatus.Draft;
    public required IReadOnlyList<BillLine> Lines { get; init; }

    public decimal Total => Lines.Sum(l => l.Amount);
}
```
`BillBody.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>One stored bill line (no computed fields), so the body round-trips through the document store.</summary>
public sealed record BillLineBody(string Description, decimal Amount, Guid ExpenseAccountId);

/// <summary>The stored shape of a bill — commercial content only. Number and status derive from the
/// engine's envelope.</summary>
public sealed record BillBody(
    Guid VendorId, DateOnly BillDate, DateOnly? DueDate, string? VendorReference, string? Memo,
    IReadOnlyList<BillLineBody> Lines);
```
`BillView.cs`:
```csharp
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>A bill plus its derived settlement facet — what a read endpoint returns.</summary>
public sealed record BillView(Bill Bill, decimal OpenBalance, SettlementStatus SettlementStatus);
```
`BillPostingAccounts.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>The single chart account the bill recipe credits. Expense accounts come from the bill lines,
/// so they are not configured here.</summary>
public sealed record BillPostingAccounts
{
    /// <summary>Accounts Payable — the control account credited for the bill total, tagged by vendor.</summary>
    public required Guid PayableAccountId { get; init; }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillTypesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Payables/Bill.cs Modules/Accounting101.Payables/BillBody.cs Modules/Accounting101.Payables/BillStatus.cs Modules/Accounting101.Payables/BillView.cs Modules/Accounting101.Payables/BillPostingAccounts.cs Modules/Accounting101.Payables.Tests/BillTypesTests.cs
git commit -m "feat(payables): bill value types"
```

---

### Task 6: Bill recipe (pure)

**Files:**
- Create: `Modules/Accounting101.Payables/BillPosting.cs`
- Test: `Modules/Accounting101.Payables.Tests/BillPostingTests.cs`

**Interfaces:**
- Consumes: `Bill`, `BillPostingAccounts`, `Accounting101.Ledger.Contracts.PostEntryRequest`/`PostLineRequest`.
- Produces: `static PostEntryRequest BillPosting.ComposeBill(Bill bill, BillPostingAccounts accounts)`; constants `BillPosting.BillSourceType = "Bill"`, `BillPaymentSourceType = "BillPayment"`, `VendorCreditApplicationSourceType = "VendorCreditApplication"`, `VendorDimension = "Vendor"`.

- [ ] **Step 1: Write the failing test** (`BillPostingTests.cs`):

```csharp
using Accounting101.Payables;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables.Tests;

public sealed class BillPostingTests
{
    private static decimal Signed(PostLineRequest l) => l.Direction == "Debit" ? l.Amount : -l.Amount;

    [Fact]
    public void A_bill_debits_each_expense_account_and_credits_ap()
    {
        Guid vendor = Guid.NewGuid(), rent = Guid.NewGuid(), utilities = Guid.NewGuid();
        BillPostingAccounts accounts = new() { PayableAccountId = Guid.NewGuid() };
        Bill bill = new()
        {
            Id = Guid.NewGuid(), VendorId = vendor, Number = "BILL-00001", BillDate = new DateOnly(2026, 3, 1),
            Status = BillStatus.Entered,
            Lines =
            [
                new BillLine { Description = "Rent", Amount = 6000m, ExpenseAccountId = rent },
                new BillLine { Description = "Utilities", Amount = 800m, ExpenseAccountId = utilities },
            ],
        };

        PostEntryRequest entry = BillPosting.ComposeBill(bill, accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(6000m, entry.Lines.Single(l => l.AccountId == rent).Amount);
        Assert.Equal(800m, entry.Lines.Single(l => l.AccountId == utilities).Amount);
        PostLineRequest ap = entry.Lines.Single(l => l.AccountId == accounts.PayableAccountId);
        Assert.Equal("Credit", ap.Direction);
        Assert.Equal(6800m, ap.Amount);
        Assert.Equal(vendor, ap.Dimensions!["Vendor"]);
        Assert.Equal("Bill", entry.SourceType);
        Assert.Equal(bill.Id, entry.SourceRef);
    }

    [Fact]
    public void Lines_sharing_an_expense_account_collapse_into_one_debit()
    {
        Guid shared = Guid.NewGuid();
        BillPostingAccounts accounts = new() { PayableAccountId = Guid.NewGuid() };
        Bill bill = new()
        {
            Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), BillDate = new DateOnly(2026, 3, 1), Status = BillStatus.Entered,
            Lines =
            [
                new BillLine { Description = "Utilities A", Amount = 300m, ExpenseAccountId = shared },
                new BillLine { Description = "Utilities B", Amount = 200m, ExpenseAccountId = shared },
            ],
        };

        PostEntryRequest entry = BillPosting.ComposeBill(bill, accounts);

        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == shared).Amount);
        Assert.Equal(2, entry.Lines.Count); // one expense debit + one A/P credit
        Assert.Equal(0m, entry.Lines.Sum(Signed));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPostingTests`
Expected: FAIL — `BillPosting` does not exist.

- [ ] **Step 3: Implement** `BillPosting.cs` (the credit-application/payment recipes are added in Task 8):
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>The payables recipes: a bill, a bill payment, or a vendor-credit application each composes into
/// one balanced journal entry. Pure — request in, wire DTO out — leaving sequencing, approval, and
/// persistence to the engine.</summary>
public static class BillPosting
{
    public const string BillSourceType = "Bill";
    public const string BillPaymentSourceType = "BillPayment";
    public const string VendorCreditApplicationSourceType = "VendorCreditApplication";
    public const string VendorDimension = "Vendor";

    public static PostEntryRequest ComposeBill(Bill bill, BillPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(accounts);

        // Debit each line's expense account; lines sharing an account collapse to one debit, ordered by
        // account id for determinism. Credit A/P for the total, tagged by vendor.
        List<PostLineRequest> lines = bill.Lines
            .GroupBy(line => line.ExpenseAccountId)
            .OrderBy(group => group.Key)
            .Select(group => new PostLineRequest(group.Key, "Debit", group.Sum(line => line.Amount)))
            .ToList();

        lines.Add(new(accounts.PayableAccountId, "Credit", bill.Total,
            Dimensions: new Dictionary<string, Guid> { [VendorDimension] = bill.VendorId }));

        return new PostEntryRequest(
            Id: null, EffectiveDate: bill.BillDate, Reference: bill.Number, Memo: bill.Memo,
            Lines: lines, SourceRef: bill.Id, SourceType: BillSourceType);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPostingTests`
Expected: PASS (2 cases).

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Payables/BillPosting.cs Modules/Accounting101.Payables.Tests/BillPostingTests.cs
git commit -m "feat(payables): bill recipe — per-line expense debits / A/P credit (pure)"
```

---

### Task 7: Bill payment + vendor-credit value types

**Files:**
- Create: `Modules/Accounting101.Payables/BillPaymentBody.cs`, `BillPayment.cs`, `BillPaymentPostingAccounts.cs`
- Test: `Modules/Accounting101.Payables.Tests/BillPaymentTypesTests.cs`

**Interfaces:**
- Consumes: `Accounting101.Settlement.Allocation`.
- Produces:
  - `record BillPaymentBody(Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations)`
  - `record VendorCreditApplicationBody(Guid VendorId, DateOnly Date, IReadOnlyList<Allocation> Allocations)`
  - `record BillPayment { Guid Id; Guid VendorId; DateOnly Date; decimal Amount; string? Method; IReadOnlyList<Allocation> Allocations; bool Voided; decimal Allocated; decimal Unapplied }`
  - `record VendorCreditApplication { Guid Id; Guid VendorId; DateOnly Date; IReadOnlyList<Allocation> Allocations; bool Voided; decimal Applied }`
  - `record BillPaymentPostingAccounts { Guid PayableAccountId; Guid CashAccountId; Guid VendorCreditsAccountId }`

- [ ] **Step 1: Write the failing test** (`BillPaymentTypesTests.cs`):

```csharp
using Accounting101.Payables;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

public sealed class BillPaymentTypesTests
{
    [Fact]
    public void BillPayment_computes_allocated_and_unapplied()
    {
        BillPayment p = new()
        {
            Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = new DateOnly(2026, 3, 31), Amount = 500m,
            Allocations = [new Allocation(Guid.NewGuid(), 300m)],
        };
        Assert.Equal(300m, p.Allocated);
        Assert.Equal(200m, p.Unapplied);
        Assert.False(p.Voided);
    }

    [Fact]
    public void VendorCreditApplication_computes_applied()
    {
        VendorCreditApplication c = new()
        {
            Id = Guid.NewGuid(), VendorId = Guid.NewGuid(), Date = new DateOnly(2026, 4, 1),
            Allocations = [new Allocation(Guid.NewGuid(), 50m), new Allocation(Guid.NewGuid(), 25m)],
        };
        Assert.Equal(75m, c.Applied);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPaymentTypesTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement.** `BillPaymentBody.cs`:
```csharp
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>Stored body of a bill payment — cash paid to a vendor, with its allocations across bills.
/// Allocations may sum to less than Amount; the remainder becomes vendor credit (a prepayment).</summary>
public sealed record BillPaymentBody(
    Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations);

/// <summary>Stored body of a vendor-credit application — existing vendor credit applied to bills (no cash).</summary>
public sealed record VendorCreditApplicationBody(
    Guid VendorId, DateOnly Date, IReadOnlyList<Allocation> Allocations);
```
`BillPayment.cs`:
```csharp
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>A recorded payment to a vendor. Voided is derived from the document lifecycle.</summary>
public sealed record BillPayment
{
    public required Guid Id { get; init; }
    public required Guid VendorId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public string? Method { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }

    public decimal Allocated => Allocations.Sum(a => a.Amount);
    public decimal Unapplied => Amount - Allocated;
}

/// <summary>An application of existing vendor credit to bills.</summary>
public sealed record VendorCreditApplication
{
    public required Guid Id { get; init; }
    public required Guid VendorId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }

    public decimal Applied => Allocations.Sum(a => a.Amount);
}
```
`BillPaymentPostingAccounts.cs`:
```csharp
namespace Accounting101.Payables;

/// <summary>Chart accounts the payment recipes post to.</summary>
public sealed record BillPaymentPostingAccounts
{
    /// <summary>Accounts Payable — debited as allocations settle bills (Vendor dim).</summary>
    public required Guid PayableAccountId { get; init; }

    /// <summary>Cash — credited for the full payment amount.</summary>
    public required Guid CashAccountId { get; init; }

    /// <summary>Vendor Credits — a Vendor-dimensioned ASSET control account holding over-payment (a
    /// prepayment the vendor owes back).</summary>
    public required Guid VendorCreditsAccountId { get; init; }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPaymentTypesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Payables/BillPaymentBody.cs Modules/Accounting101.Payables/BillPayment.cs Modules/Accounting101.Payables/BillPaymentPostingAccounts.cs Modules/Accounting101.Payables.Tests/BillPaymentTypesTests.cs
git commit -m "feat(payables): bill-payment + vendor-credit value types"
```

---

### Task 8: Bill-payment + vendor-credit recipes (pure)

**Files:**
- Modify: `Modules/Accounting101.Payables/BillPosting.cs`
- Test: `Modules/Accounting101.Payables.Tests/BillPostingTests.cs` (append two methods)

**Interfaces:**
- Produces: `static PostEntryRequest BillPosting.ComposeBillPayment(Guid paymentId, BillPaymentBody body, BillPaymentPostingAccounts accounts)`; `static PostEntryRequest BillPosting.ComposeVendorCreditApplication(Guid id, VendorCreditApplicationBody body, BillPaymentPostingAccounts accounts)`.

- [ ] **Step 1: Append the failing tests** to `BillPostingTests`:

```csharp
    [Fact]
    public void A_bill_payment_debits_ap_and_routes_overpayment_to_vendor_credits()
    {
        Guid vendor = Guid.NewGuid();
        BillPaymentPostingAccounts accounts = new()
        {
            PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
        };
        BillPaymentBody body = new(vendor, new DateOnly(2026, 3, 31), 500m, "check", [new Allocation(Guid.NewGuid(), 300m)]);

        PostEntryRequest entry = BillPosting.ComposeBillPayment(Guid.NewGuid(), body, accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        Assert.Equal(500m, entry.Lines.Single(l => l.AccountId == accounts.CashAccountId).Amount); // Cr Cash total
        Assert.Equal("Credit", entry.Lines.Single(l => l.AccountId == accounts.CashAccountId).Direction);
        PostLineRequest ap = entry.Lines.Single(l => l.AccountId == accounts.PayableAccountId);
        Assert.Equal("Debit", ap.Direction);
        Assert.Equal(300m, ap.Amount);
        Assert.Equal(vendor, ap.Dimensions!["Vendor"]);
        PostLineRequest credits = entry.Lines.Single(l => l.AccountId == accounts.VendorCreditsAccountId);
        Assert.Equal("Debit", credits.Direction);   // asset increases
        Assert.Equal(200m, credits.Amount);
        Assert.Equal(vendor, credits.Dimensions!["Vendor"]);
        Assert.Equal("BillPayment", entry.SourceType);
    }

    [Fact]
    public void A_vendor_credit_application_debits_ap_and_credits_vendor_credits()
    {
        Guid vendor = Guid.NewGuid();
        BillPaymentPostingAccounts accounts = new()
        {
            PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
        };
        VendorCreditApplicationBody body = new(vendor, new DateOnly(2026, 4, 2), [new Allocation(Guid.NewGuid(), 150m)]);

        PostEntryRequest entry = BillPosting.ComposeVendorCreditApplication(Guid.NewGuid(), body, accounts);

        Assert.Equal(0m, entry.Lines.Sum(Signed));
        PostLineRequest ap = entry.Lines.Single(l => l.AccountId == accounts.PayableAccountId);
        PostLineRequest credits = entry.Lines.Single(l => l.AccountId == accounts.VendorCreditsAccountId);
        Assert.Equal("Debit", ap.Direction);
        Assert.Equal(150m, ap.Amount);
        Assert.Equal("Credit", credits.Direction); // asset decreases
        Assert.Equal(150m, credits.Amount);
        Assert.Equal(vendor, ap.Dimensions!["Vendor"]);
        Assert.Equal(vendor, credits.Dimensions!["Vendor"]);
        Assert.Equal("VendorCreditApplication", entry.SourceType);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPostingTests`
Expected: FAIL — the two new methods do not exist.

- [ ] **Step 3: Implement** (add to `BillPosting`):
```csharp
    public static PostEntryRequest ComposeBillPayment(Guid paymentId, BillPaymentBody body, BillPaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal allocated = body.Allocations.Sum(a => a.Amount);
        decimal remainder = body.Amount - allocated;
        Dictionary<string, Guid> dim = new() { [VendorDimension] = body.VendorId };

        List<PostLineRequest> lines = [];
        if (allocated != 0m)
            lines.Add(new(accounts.PayableAccountId, "Debit", allocated, Dimensions: dim));
        if (remainder != 0m)
            lines.Add(new(accounts.VendorCreditsAccountId, "Debit", remainder, Dimensions: dim));
        lines.Add(new(accounts.CashAccountId, "Credit", body.Amount));

        return new PostEntryRequest(
            Id: null, EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: paymentId, SourceType: BillPaymentSourceType);
    }

    public static PostEntryRequest ComposeVendorCreditApplication(Guid id, VendorCreditApplicationBody body, BillPaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);

        decimal applied = body.Allocations.Sum(a => a.Amount);
        Dictionary<string, Guid> dim = new() { [VendorDimension] = body.VendorId };

        List<PostLineRequest> lines =
        [
            new(accounts.PayableAccountId, "Debit", applied, Dimensions: dim),
            new(accounts.VendorCreditsAccountId, "Credit", applied, Dimensions: dim),
        ];

        return new PostEntryRequest(
            Id: null, EffectiveDate: body.Date, Reference: null, Memo: null,
            Lines: lines, SourceRef: id, SourceType: VendorCreditApplicationSourceType);
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPostingTests`
Expected: PASS (4 cases).

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Payables/BillPosting.cs Modules/Accounting101.Payables.Tests/BillPostingTests.cs
git commit -m "feat(payables): bill-payment + vendor-credit recipes (pure)"
```

---

### Task 9: Bill store + bill-payment store

**Files:**
- Create: `Modules/Accounting101.Payables/PayablesPorts.cs`, `DocumentBillStore.cs`, `DocumentBillPaymentStore.cs`
- Modify: `Modules/Accounting101.Payables.Tests/Fakes.cs` (add `InMemoryBillStore`, `InMemoryBillPaymentStore`)
- Test: `Modules/Accounting101.Payables.Tests/DocumentBillStoreTests.cs`

**Interfaces:**
- Consumes: `Accounting101.Ledger.Contracts.IDocumentStore` (`CreateAsync`, `FinalizeAsync`, `VoidAsync`, `GetAsync<T>`, `QueryAsync<T>`, `DocumentResult<T>`, `DocumentLifecycle`).
- Produces:
  - `interface IBillStore { Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default); Task<Bill> FinalizeAsync(Guid clientId, Guid billId, CancellationToken ct = default); Task VoidAsync(Guid clientId, Guid billId, CancellationToken ct = default); Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default); Task<IReadOnlyList<Bill>> GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default); }`
  - `interface IBillPaymentStore { Task<BillPayment> RecordPaymentAsync(Guid clientId, BillPaymentBody body, CancellationToken ct = default); Task<VendorCreditApplication> RecordCreditApplicationAsync(Guid clientId, VendorCreditApplicationBody body, CancellationToken ct = default); Task VoidAsync(Guid clientId, Guid paymentId, CancellationToken ct = default); Task<BillPayment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default); Task<IReadOnlyList<BillPayment>> GetPaymentsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default); Task<IReadOnlyList<VendorCreditApplication>> GetCreditApplicationsByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default); }`
  - `interface IBillAccountsProvider { Task<BillPostingAccounts> GetBillAccountsAsync(Guid clientId, CancellationToken ct = default); Task<BillPaymentPostingAccounts> GetPaymentAccountsAsync(Guid clientId, CancellationToken ct = default); }`
  - `DocumentBillStore(IDocumentStore documents)` over collection `"bills"` (number `BILL-{seq:D5}`); `DocumentBillPaymentStore(IDocumentStore documents)` over `"bill-payments"` / `"vendor-credit-applications"`; fakes `InMemoryBillStore`, `InMemoryBillPaymentStore`.

- [ ] **Step 1: Read the parallels** `DocumentInvoiceStore.cs` (for `DocumentBillStore` — same draft/finalize/void/get/get-by lifecycle, tag `{Vendor: id}`, number `BILL-{seq:D5}`, status mapped from `DocumentLifecycle`: `Finalized`→`Entered`, `Voided`/`Superseded`→`Void`, else `Draft`) and `DocumentPaymentStore.cs` (for `DocumentBillPaymentStore` — create+finalize each document, void over the `"bill-payments"` collection). Also read `PaymentPorts.cs` and the `InMemoryInvoiceStore`/`InMemoryPaymentStore` fakes.

- [ ] **Step 2: Write the failing test** (`DocumentBillStoreTests.cs`) — mirror `DocumentPaymentStoreTests` using the `PayablesDocumentStoreFixture` from Task 4 (`fixture.Store`, `fixture.ClientId`):

```csharp
using Accounting101.Payables;

namespace Accounting101.Payables.Tests;

public sealed class DocumentBillStoreTests(PayablesDocumentStoreFixture fixture) : IClassFixture<PayablesDocumentStoreFixture>
{
    [Fact]
    public async Task Drafts_then_enters_a_bill_and_reads_it_by_vendor()
    {
        IBillStore store = new DocumentBillStore(fixture.Store);
        Guid vendor = Guid.NewGuid();
        BillBody body = new(vendor, new DateOnly(2026, 3, 1), null, "V-123", null,
            [new BillLineBody("Rent", 6000m, Guid.NewGuid())]);

        Bill draft = await store.CreateDraftAsync(fixture.ClientId, body);
        Assert.Equal(BillStatus.Draft, draft.Status);
        Assert.Null(draft.Number);

        Bill entered = await store.FinalizeAsync(fixture.ClientId, draft.Id);
        Assert.Equal(BillStatus.Entered, entered.Status);
        Assert.NotNull(entered.Number);

        Assert.Single(await store.GetByVendorAsync(fixture.ClientId, vendor));
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~DocumentBillStoreTests`
Expected: FAIL — types do not exist.

- [ ] **Step 4: Implement** `PayablesPorts.cs` (the three interfaces above), `DocumentBillStore.cs` (mirror `DocumentInvoiceStore.cs`: `Map` builds `Bill` from `DocumentResult<BillBody>`, `Number = result.Sequence is { } seq ? $"BILL-{seq:D5}" : null`, `Status` from `result.State`, `Lines` mapped from `BillLineBody` to `BillLine`), and `DocumentBillPaymentStore.cs` (mirror `DocumentPaymentStore.cs`: collections `"bill-payments"` and `"vendor-credit-applications"`, create→finalize, `VoidAsync` over `"bill-payments"`, tags `{Vendor: id}`, `Voided` from lifecycle). Add `InMemoryBillStore` and `InMemoryBillPaymentStore` to the payables `Fakes.cs` mirroring `InMemoryInvoiceStore`/`InMemoryPaymentStore` (with Customer→Vendor and `GetByVendorAsync` returning ALL bills including voided — matching the real store; the service filters).

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~DocumentBillStoreTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Modules/Accounting101.Payables/PayablesPorts.cs Modules/Accounting101.Payables/DocumentBillStore.cs Modules/Accounting101.Payables/DocumentBillPaymentStore.cs Modules/Accounting101.Payables.Tests/Fakes.cs Modules/Accounting101.Payables.Tests/DocumentBillStoreTests.cs
git commit -m "feat(payables): bill + bill-payment document stores"
```

---

### Task 10: BillService (draft / enter / void a bill)

**Files:**
- Create: `Modules/Accounting101.Payables/BillService.cs`
- Test: `Modules/Accounting101.Payables.Tests/BillServiceTests.cs`

**Interfaces:**
- Consumes: `IBillStore`, `IVendorStore`, `IBillAccountsProvider.GetBillAccountsAsync`, `ILedgerClient` (`PostAsync`, `GetEntriesBySourceRefAsync`, `ReverseAsync`, `VoidAsync`), `BillPosting.ComposeBill`, `Bill`.
- Produces: `sealed class BillService(IBillStore bills, IVendorStore vendors, IBillAccountsProvider accounts, ILedgerClient ledger)` with `Task<Bill> DraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)`, `Task<Bill> EnterAsync(Guid clientId, Guid billId, CancellationToken ct = default)`, `Task<Bill> VoidAsync(Guid clientId, Guid billId, string? reason = null, CancellationToken ct = default)`, `Task<Bill?> GetAsync(...)`.

- [ ] **Step 1: Read the parallel** `Modules/Accounting101.Invoicing/InvoiceService.cs` — `DraftAsync` (validate vendor exists + ≥1 line), `EnterAsync` (mirror `IssueAsync`: require Draft, finalize, compose, post — lands PendingApproval), `VoidAsync` (mirror: find the active non-reversal entry by source ref; Posted→Reverse else→Void; then void the document).

- [ ] **Step 2: Write the failing test** (`BillServiceTests.cs`):

```csharp
using Accounting101.Payables;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables.Tests;

public sealed class BillServiceTests
{
    private static readonly BillPostingAccounts BillAccounts = new() { PayableAccountId = Guid.NewGuid() };
    private static readonly BillPaymentPostingAccounts PayAccounts = new()
    {
        PayableAccountId = BillAccounts.PayableAccountId, CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
    };

    internal sealed record Harness(BillService Bills, FakeLedgerClient Ledger, InMemoryBillStore BillStore, InMemoryVendorStore Vendors);

    internal static async Task<(Harness h, Guid clientId, Guid vendorId)> SetupAsync()
    {
        Guid clientId = Guid.NewGuid(), vendorId = Guid.NewGuid();
        InMemoryVendorStore vendors = new();
        await vendors.SaveAsync(clientId, new Vendor { Id = vendorId, Name = "PropCo" });
        InMemoryBillStore billStore = new();
        FakeLedgerClient ledger = new();
        BillService bills = new(billStore, vendors, new FixedBillAccountsProvider(BillAccounts, PayAccounts), ledger);
        return (new Harness(bills, ledger, billStore, vendors), clientId, vendorId);
    }

    [Fact]
    public async Task Entering_a_bill_posts_a_pending_ap_entry()
    {
        (Harness h, Guid clientId, Guid vendorId) = await SetupAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, new BillBody(vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 6000m, Guid.NewGuid())]));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        Assert.Equal(BillStatus.Entered, entered.Status);
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal("Bill", entry.SourceType);
        Assert.Equal(draft.Id, entry.SourceRef);
    }

    [Fact]
    public async Task Drafting_a_bill_for_an_unknown_vendor_is_rejected()
    {
        (Harness h, Guid clientId, _) = await SetupAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Bills.DraftAsync(clientId,
            new BillBody(Guid.NewGuid(), new DateOnly(2026, 3, 1), null, null, null, [new BillLineBody("X", 1m, Guid.NewGuid())])));
    }
}

internal sealed class FixedBillAccountsProvider(BillPostingAccounts bill, BillPaymentPostingAccounts pay) : IBillAccountsProvider
{
    public Task<BillPostingAccounts> GetBillAccountsAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult(bill);
    public Task<BillPaymentPostingAccounts> GetPaymentAccountsAsync(Guid clientId, CancellationToken ct = default) => Task.FromResult(pay);
}
```

- [ ] **Step 2b: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillServiceTests`
Expected: FAIL — `BillService` / `FixedBillAccountsProvider` do not exist.

- [ ] **Step 3: Implement** `BillService.cs` mirroring `InvoiceService.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>The bill lifecycle: draft a bill, enter it (finalize — assigns the number — then post its A/P
/// entry, which lands PendingApproval for a separate approver), and void it (reverse the entry if posted,
/// or withdraw it if still pending). The module never self-approves.</summary>
public sealed class BillService(
    IBillStore bills, IVendorStore vendors, IBillAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<Bill> DraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await vendors.GetAsync(clientId, body.VendorId, ct) is null)
            throw new InvalidOperationException($"Vendor {body.VendorId} does not exist.");
        if (body.Lines.Count == 0)
            throw new InvalidOperationException("A bill needs at least one line.");
        if (body.Lines.Any(l => l.Amount <= 0m))
            throw new InvalidOperationException("Every bill line amount must be greater than zero.");
        if (body.Lines.Any(l => l.ExpenseAccountId == Guid.Empty))
            throw new InvalidOperationException("Every bill line needs an expense account.");
        return await bills.CreateDraftAsync(clientId, body, ct);
    }

    public async Task<Bill> EnterAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        Bill draft = await RequireAsync(clientId, billId, ct);
        if (draft.Status != BillStatus.Draft)
            throw new InvalidOperationException($"Only a draft bill can be entered; {billId} is {draft.Status}.");
        if (draft.Total <= 0m)
            throw new InvalidOperationException($"Bill {billId} must total more than zero.");

        Bill entered = await bills.FinalizeAsync(clientId, billId, ct);
        BillPostingAccounts posting = await accounts.GetBillAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeBill(entered, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return entered;
    }

    public async Task<Bill> VoidAsync(Guid clientId, Guid billId, string? reason = null, CancellationToken ct = default)
    {
        Bill bill = await RequireAsync(clientId, billId, ct);
        if (bill.Status != BillStatus.Entered)
            throw new InvalidOperationException($"Only an entered bill can be voided; {billId} is {bill.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, billId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for bill {bill.Number} to void.");

        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(bill.BillDate, reason ?? $"Voided bill {billId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided bill {billId}"), ct);

        await bills.VoidAsync(clientId, billId, ct);
        return await RequireAsync(clientId, billId, ct);
    }

    public Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default) =>
        bills.GetAsync(clientId, billId, ct);

    private async Task<Bill> RequireAsync(Guid clientId, Guid billId, CancellationToken ct) =>
        await bills.GetAsync(clientId, billId, ct) ?? throw new InvalidOperationException($"Bill {billId} not found.");
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Payables/BillService.cs Modules/Accounting101.Payables.Tests/BillServiceTests.cs
git commit -m "feat(payables): BillService — draft / enter / void a bill"
```

---

### Task 11: BillPaymentService — record payment, settlement reads, credit application, void, filtered list

**Files:**
- Create: `Modules/Accounting101.Payables/BillPaymentService.cs`
- Test: `Modules/Accounting101.Payables.Tests/BillPaymentServiceTests.cs`

**Interfaces:**
- Consumes: `IBillPaymentStore`, `IBillStore` (`GetAsync`, `GetByVendorAsync`), `IBillAccountsProvider.GetPaymentAccountsAsync`, `ILedgerClient`, `BillPosting.ComposeBillPayment`/`ComposeVendorCreditApplication`, `Accounting101.Settlement` (`Settlement`, `SettlementStatus`, `SettlementFilter`, `Allocation`), `Bill.Total`/`.Status`/`.VendorId`, `BillView`.
- Produces: `sealed class BillPaymentService(IBillPaymentStore payments, IBillStore bills, IBillAccountsProvider accounts, ILedgerClient ledger)` with `RecordPaymentAsync(Guid clientId, BillPaymentBody body, CancellationToken)`, `RecordCreditApplicationAsync(Guid clientId, VendorCreditApplicationBody body, CancellationToken)`, `VoidPaymentAsync(Guid clientId, Guid paymentId, string? reason = null, CancellationToken)`, `GetBillViewAsync(Guid clientId, Guid billId, CancellationToken)`, `GetVendorCreditBalanceAsync(Guid clientId, Guid vendorId, CancellationToken)`, `ListBillViewsAsync(Guid clientId, Guid vendorId, SettlementFilter? filter, CancellationToken)`.

- [ ] **Step 1: Read the parallel** `Modules/Accounting101.Invoicing/PaymentService.cs` in full — this service is its mirror (Customer→Vendor, Invoice→Bill, A/R→A/P). Same validation, same derivation, same void pattern, same settlement-filtered list (which must exclude voided bills via `inv.Status != BillStatus.Void`).

- [ ] **Step 2: Write the failing test** (`BillPaymentServiceTests.cs`) — mirror `PaymentServiceTests` covering: record payment posts a pending "BillPayment" entry; rejects allocations > amount; rejects allocation > a bill's open balance; partial payment → `PartiallyPaid` + open balance; over-payment → vendor credit balance rises + bill `Paid`; apply credit → bill balance drops + credit falls + a "VendorCreditApplication" entry; reject credit application exceeding available credit; void payment restores the bill open balance; list excludes voided bills. Use a `Harness` analogous to `BillServiceTests` exposing `.Payments` (BillPaymentService), `.Bills` (BillService or InMemoryBillStore), `.Ledger`. Build a helper `SetupWithEnteredBillAsync(decimal total)` that drafts+enters a bill (via the fake `InMemoryBillStore` directly: `CreateDraftAsync` then `FinalizeAsync`) so the bill exists with `Total`. Each test asserts real values (mirror the exact assertions in `PaymentServiceTests`, flipped to bills/vendors). Include a `Voided_bill_does_not_appear_in_list_views` test (void a bill via the fake, assert absent from both the all-list and the `SettlementFilter.Open` list).

- [ ] **Step 2b: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPaymentServiceTests`
Expected: FAIL — `BillPaymentService` does not exist.

- [ ] **Step 3: Implement** `BillPaymentService.cs` mirroring `PaymentService.cs` exactly, flipped:
```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Payables;

/// <summary>The cash-disbursement lifecycle: record a bill payment (allocate across bills, hold over-payment
/// as vendor credit), apply existing vendor credit, and void. Each document posts one balanced entry that
/// lands PendingApproval — approval is the client's normal maker-checker flow. Open balances and vendor
/// credit are derived from stored allocations, never stored.</summary>
public sealed class BillPaymentService(
    IBillPaymentStore payments, IBillStore bills, IBillAccountsProvider accounts, ILedgerClient ledger)
{
    public async Task<BillPayment> RecordPaymentAsync(Guid clientId, BillPaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Amount <= 0m)
            throw new InvalidOperationException("A payment amount must be greater than zero.");
        if (body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("Every allocation amount must be greater than zero.");
        if (body.Allocations.Sum(a => a.Amount) > body.Amount)
            throw new InvalidOperationException("Allocations cannot exceed the payment amount.");

        await ValidateAllocationsAsync(clientId, body.VendorId, body.Allocations, ct);

        BillPayment recorded = await payments.RecordPaymentAsync(clientId, body, ct);
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeBillPayment(recorded.Id, body, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }

    public async Task<VendorCreditApplication> RecordCreditApplicationAsync(Guid clientId, VendorCreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Allocations.Count == 0 || body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit application needs positive allocations.");

        decimal applying = body.Allocations.Sum(a => a.Amount);
        decimal available = await GetVendorCreditBalanceAsync(clientId, body.VendorId, ct);
        if (applying > available)
            throw new InvalidOperationException($"Credit application of {applying} exceeds available credit {available}.");

        await ValidateAllocationsAsync(clientId, body.VendorId, body.Allocations, ct);

        VendorCreditApplication recorded = await payments.RecordCreditApplicationAsync(clientId, body, ct);
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeVendorCreditApplication(recorded.Id, body, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return recorded;
    }

    public async Task<BillPayment> VoidPaymentAsync(Guid clientId, Guid paymentId, string? reason = null, CancellationToken ct = default)
    {
        BillPayment payment = await payments.GetPaymentAsync(clientId, paymentId, ct)
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

    public async Task<BillView?> GetBillViewAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        Bill? bill = await bills.GetAsync(clientId, billId, ct);
        if (bill is null) return null;
        decimal applied = await AppliedToBillAsync(clientId, bill.VendorId, billId, ct);
        return new BillView(bill, Settlement.OpenBalance(bill.Total, applied), Settlement.Status(bill.Total, applied));
    }

    public async Task<decimal> GetVendorCreditBalanceAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<VendorCreditApplication> cs = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);
        decimal created = ps.Where(p => !p.Voided).Sum(p => p.Unapplied);
        decimal spent = cs.Where(c => !c.Voided).Sum(c => c.Applied);
        return created - spent;
    }

    public async Task<IReadOnlyList<BillView>> ListBillViewsAsync(Guid clientId, Guid vendorId, SettlementFilter? filter, CancellationToken ct = default)
    {
        IReadOnlyList<Bill> vendorBills = await bills.GetByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<VendorCreditApplication> cs = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);

        Dictionary<Guid, decimal> applied = new();
        foreach (Allocation a in ps.Where(p => !p.Voided).SelectMany(p => p.Allocations)
                     .Concat(cs.Where(c => !c.Voided).SelectMany(c => c.Allocations)))
            applied[a.TargetId] = applied.GetValueOrDefault(a.TargetId) + a.Amount;

        IEnumerable<BillView> views = vendorBills
            .Where(bill => bill.Status != BillStatus.Void)
            .Select(bill =>
            {
                decimal ap = applied.GetValueOrDefault(bill.Id);
                return new BillView(bill, Settlement.OpenBalance(bill.Total, ap), Settlement.Status(bill.Total, ap));
            });

        views = filter switch
        {
            SettlementFilter.Open => views.Where(v => v.SettlementStatus != SettlementStatus.Paid),
            SettlementFilter.Paid => views.Where(v => v.SettlementStatus == SettlementStatus.Paid),
            _ => views,
        };
        return views.ToList();
    }

    private async Task ValidateAllocationsAsync(Guid clientId, Guid vendorId, IReadOnlyList<Allocation> allocations, CancellationToken ct)
    {
        foreach (Allocation a in allocations)
        {
            Bill bill = await bills.GetAsync(clientId, a.TargetId, ct)
                ?? throw new InvalidOperationException($"Bill {a.TargetId} does not exist.");
            if (bill.Status == BillStatus.Void)
                throw new InvalidOperationException($"Bill {a.TargetId} is voided.");
            if (bill.VendorId != vendorId)
                throw new InvalidOperationException($"Bill {a.TargetId} belongs to a different vendor.");

            decimal alreadyApplied = await AppliedToBillAsync(clientId, vendorId, a.TargetId, ct);
            if (alreadyApplied + a.Amount > bill.Total)
                throw new InvalidOperationException($"Allocation to bill {a.TargetId} exceeds its open balance.");
        }
    }

    private async Task<decimal> AppliedToBillAsync(Guid clientId, Guid vendorId, Guid billId, CancellationToken ct)
    {
        IReadOnlyList<BillPayment> ps = await payments.GetPaymentsByVendorAsync(clientId, vendorId, ct);
        IReadOnlyList<VendorCreditApplication> cs = await payments.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);
        decimal fromPayments = ps.Where(p => !p.Voided).SelectMany(p => p.Allocations).Where(x => x.TargetId == billId).Sum(x => x.Amount);
        decimal fromCredits = cs.Where(c => !c.Voided).SelectMany(c => c.Allocations).Where(x => x.TargetId == billId).Sum(x => x.Amount);
        return fromPayments + fromCredits;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPaymentServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Payables/BillPaymentService.cs Modules/Accounting101.Payables.Tests/BillPaymentServiceTests.cs
git commit -m "feat(payables): BillPaymentService — payments, vendor credit, void, derived reads"
```

---

### Task 12: Configured accounts provider + DI wiring

**Files:**
- Create: `Modules/Accounting101.Payables.Api/ConfiguredBillAccountsProvider.cs`, `PayablesServiceExtensions.cs`
- Test: `Modules/Accounting101.Payables.Tests/ConfiguredBillAccountsProviderTests.cs`
- Modify: the host composition root (`Backend/Accounting101.Host/Program.cs`) to call `AddPayables` — only if the host already calls `AddInvoicing`; mirror that line.

**Interfaces:**
- Consumes: `IConfiguration`, `IBillAccountsProvider`, `BillPostingAccounts`, `BillPaymentPostingAccounts`, the engine host's `AddModule`/`ModuleIdentity`/manifest API (see `InvoicingServiceExtensions.cs`), `IBillStore`/`DocumentBillStore`, `IBillPaymentStore`/`DocumentBillPaymentStore`, `IVendorStore`/`DocumentVendorStore`, `BillService`, `BillPaymentService`, `ILedgerClient`/`HttpLedgerClient`.
- Produces: `sealed class ConfiguredBillAccountsProvider(IConfiguration configuration) : IBillAccountsProvider` reading `Payables:Accounts:Payable|Cash|VendorCredits`; `static IServiceCollection PayablesServiceExtensions.AddPayables(this IServiceCollection services, IConfiguration configuration)`.

- [ ] **Step 1: Read the parallels** `ConfiguredInvoiceAccountsProvider.cs` and `InvoicingServiceExtensions.cs`.

- [ ] **Step 2: Write the failing provider test** (`ConfiguredBillAccountsProviderTests.cs`):

```csharp
using Accounting101.Payables;
using Accounting101.Payables.Api;
using Microsoft.Extensions.Configuration;

namespace Accounting101.Payables.Tests;

public sealed class ConfiguredBillAccountsProviderTests
{
    [Fact]
    public async Task Reads_the_payable_cash_and_vendor_credits_accounts()
    {
        Guid ap = Guid.NewGuid(), cash = Guid.NewGuid(), credits = Guid.NewGuid();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payables:Accounts:Payable"] = ap.ToString(),
            ["Payables:Accounts:Cash"] = cash.ToString(),
            ["Payables:Accounts:VendorCredits"] = credits.ToString(),
        }).Build();

        var provider = new ConfiguredBillAccountsProvider(config);
        Assert.Equal(ap, (await provider.GetBillAccountsAsync(Guid.NewGuid())).PayableAccountId);
        BillPaymentPostingAccounts pay = await provider.GetPaymentAccountsAsync(Guid.NewGuid());
        Assert.Equal(ap, pay.PayableAccountId);
        Assert.Equal(cash, pay.CashAccountId);
        Assert.Equal(credits, pay.VendorCreditsAccountId);
    }

    [Fact]
    public async Task Throws_when_an_account_is_not_configured()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ConfiguredBillAccountsProvider(config).GetBillAccountsAsync(Guid.NewGuid()));
    }
}
```

- [ ] **Step 2b: Run to verify it fails**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~ConfiguredBillAccountsProviderTests`
Expected: FAIL.

- [ ] **Step 3: Implement.** `ConfiguredBillAccountsProvider.cs`:
```csharp
using Accounting101.Payables;

namespace Accounting101.Payables.Api;

/// <summary>Supplies the payables posting accounts from configuration
/// (Payables:Accounts:Payable|Cash|VendorCredits). A single configured set for now.</summary>
public sealed class ConfiguredBillAccountsProvider(IConfiguration configuration) : IBillAccountsProvider
{
    public Task<BillPostingAccounts> GetBillAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new BillPostingAccounts { PayableAccountId = Read("Payables:Accounts:Payable") });

    public Task<BillPaymentPostingAccounts> GetPaymentAccountsAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(new BillPaymentPostingAccounts
        {
            PayableAccountId = Read("Payables:Accounts:Payable"),
            CashAccountId = Read("Payables:Accounts:Cash"),
            VendorCreditsAccountId = Read("Payables:Accounts:VendorCredits"),
        });

    private Guid Read(string key) =>
        Guid.TryParse(configuration[key], out Guid id)
            ? id
            : throw new InvalidOperationException($"Payables posting account '{key}' is not configured.");
}
```
`PayablesServiceExtensions.cs` (mirror `InvoicingServiceExtensions.cs`):
```csharp
using Accounting101.Payables;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;

namespace Accounting101.Payables.Api;

/// <summary>Installs the payables module into the host: module identity + collection manifest, the
/// document-store-backed stores and services, the config-backed accounts provider, and the loopback
/// ledger HttpClient.</summary>
public static class PayablesServiceExtensions
{
    public static IServiceCollection AddPayables(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModule(new ModuleIdentity("payables"), "Payables", manifest =>
        {
            manifest.Reference("vendors");
            manifest.Evidentiary("bills", "Vendor");
            manifest.Evidentiary("bill-payments", "Vendor");
            manifest.Evidentiary("vendor-credit-applications", "Vendor");
        });

        services.AddScoped<IVendorStore, DocumentVendorStore>();
        services.AddScoped<IBillStore, DocumentBillStore>();
        services.AddScoped<IBillPaymentStore, DocumentBillPaymentStore>();
        services.AddScoped<BillService>();
        services.AddScoped<BillPaymentService>();
        services.AddSingleton<IBillAccountsProvider, ConfiguredBillAccountsProvider>();

        services.AddHttpClient<ILedgerClient, HttpLedgerClient>(client =>
            client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"));

        return services;
    }
}
```
> NOTE: confirm the exact `using` namespaces and the `AddModule`/`HttpLedgerClient` references by copying them verbatim from `InvoicingServiceExtensions.cs`. If the host's `Program.cs` registers invoicing via `AddInvoicing(...)`, add a sibling `AddPayables(...)` line there (read `Program.cs` first; mirror the invoicing line exactly).

- [ ] **Step 4: Run the provider test + build the host**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~ConfiguredBillAccountsProviderTests`
Expected: PASS.
Run: `dotnet build Accounting101.slnx`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Payables.Api Modules/Accounting101.Payables.Tests/ConfiguredBillAccountsProviderTests.cs Backend/Accounting101.Host/Program.cs
git commit -m "feat(payables): configured accounts provider + DI/manifest/host wiring"
```

---

### Task 13: Web endpoints + request DTOs

**Files:**
- Create: `Modules/Accounting101.Payables.Api/PayablesRequests.cs`, `PayablesEndpoints.cs`
- Modify: the host (`Backend/Accounting101.Host/Program.cs` or wherever invoicing endpoints are mapped) to call `MapPayablesEndpoints` — mirror the `MapInvoicingEndpoints` line.

**Interfaces:**
- Consumes: `BillService`, `BillPaymentService`, `BillView`, `Vendor`, `Bill`, `BillPayment`, `VendorCreditApplication`, `Accounting101.Settlement.Allocation`/`SettlementFilter`.
- Produces (DTOs): `record CreateVendorRequest(string Name, string? Email)`; `record DraftBillRequest(Guid VendorId, DateOnly BillDate, DateOnly? DueDate, string? VendorReference, string? Memo, IReadOnlyList<BillLineBody> Lines)`; `record RecordBillPaymentRequest(Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations)`; `record VendorCreditApplicationRequest(Guid VendorId, DateOnly Date, IReadOnlyList<Allocation> Allocations)`; `record VoidReasonRequest(string? Reason)`. Routes under `/clients/{clientId:guid}`: `POST /vendors`, `POST /bills`, `POST /bills/{billId:guid}/enter`, `POST /bills/{billId:guid}/void`, `POST /bill-payments`, `POST /bill-payments/{paymentId:guid}/void`, `POST /vendor-credit-applications`, `GET /bills/{billId:guid}` (→ BillView), `GET /vendors/{vendorId:guid}/credit-balance`, `GET /bills?vendorId=&settlement=`.

- [ ] **Step 1: Read the parallels** `InvoicingRequests.cs` and `InvoicingEndpoints.cs`. Mirror exactly (Customer→Vendor, Invoice→Bill, issue→enter, the settlement-filtered list, the InvoiceView→BillView GET, the credit-balance GET). Bill enter maps to `BillService.EnterAsync`; void maps to `BillService.VoidAsync` (409 on `InvalidOperationException`); record/credit/draft map to their services (422 on `InvalidOperationException`); `GET /bills/{id}` returns `BillPaymentService.GetBillViewAsync` (404 if null); `GET /bills` parses `settlement` like the invoicing list (400 on unknown). Need a vendor-creation handler calling `BillService`? Vendors are created via a small method on... there is no VendorService in this plan. Add a thin `CreateVendor` handler that constructs a `Vendor { Id = Guid.NewGuid(), Name, Email }` and calls `IVendorStore.SaveAsync` directly (inject `IVendorStore` into the handler), returning `Results.Created`.

- [ ] **Step 2: Implement** `PayablesRequests.cs` (the five DTOs above) and `PayablesEndpoints.cs` with `public static void MapPayablesEndpoints(this IEndpointRouteBuilder app)` mirroring `MapInvoicingEndpoints`. Add the `MapPayablesEndpoints()` call to the host beside `MapInvoicingEndpoints()` (read the host file first).

- [ ] **Step 3: Build + run the full payables suite**

Run: `dotnet build Accounting101.slnx`
Expected: Build succeeded, 0 warnings.
Run: `dotnet test Modules/Accounting101.Payables.Tests`
Expected: PASS (all prior payables tests; no new unit test here — behavior is covered by the service tests and the next task's e2e).

- [ ] **Step 4: Commit**

```bash
git add Modules/Accounting101.Payables.Api/PayablesRequests.cs Modules/Accounting101.Payables.Api/PayablesEndpoints.cs Backend/Accounting101.Host/Program.cs
git commit -m "feat(payables): vendor/bill/payment/credit endpoints + bill view"
```

---

### Task 14: End-to-end payables test through the real host

**Files:**
- Create: `Modules/Accounting101.Payables.Tests/PayablesHostFixture.cs`
- Create: `Modules/Accounting101.Payables.Tests/PayablesE2eTests.cs`

**Interfaces:**
- Consumes: the host (`Program`), `AccountRequest`, `EntryResponse`, `SubledgerReconciliationResponse` (match shapes from `InvoicingIssueTests.cs`/`CashApplicationTests.cs`), the payables DTOs + `BillView`.

- [ ] **Step 1: Create the host fixture** by copying `Modules/Accounting101.Invoicing.Tests/InvoicingHostFixture.cs` to `PayablesHostFixture.cs`. Keep the EphemeralMongo + `WebApplicationFactory<Program>` boot and the loopback `ILedgerClient` repoint. Replace the invoicing account `UseSetting`s with payables ones and expose the account ids:
```csharp
    public Guid PayableAccountId { get; } = Guid.NewGuid();
    public Guid CashAccountId { get; } = Guid.NewGuid();
    public Guid VendorCreditsAccountId { get; } = Guid.NewGuid();
    public Guid RentExpenseAccountId { get; } = Guid.NewGuid();
    public Guid UtilitiesExpenseAccountId { get; } = Guid.NewGuid();
```
and in `ConfigureWebHost`:
```csharp
        builder.UseSetting("Payables:Accounts:Payable", PayableAccountId.ToString());
        builder.UseSetting("Payables:Accounts:Cash", CashAccountId.ToString());
        builder.UseSetting("Payables:Accounts:VendorCredits", VendorCreditsAccountId.ToString());
```
Keep `SeedSodClientAsync`/`ClientFor` as-is.

- [ ] **Step 2: Write the failing e2e test** (`PayablesE2eTests.cs`). Mirror `CashApplicationTests.cs`. Read it first for the exact `AccountRequest`/`EntryResponse`/`SubledgerReconciliationResponse` usage and the SoD approve-by-source-ref helper. The flow:
  - Provision the chart: A/P `2000` (Liability, `RequiredDimension="Vendor"`), Cash `1000` (Asset), Vendor Credits `1300` (Asset, `RequiredDimension="Vendor"`), Rent `5200` (Expense), Utilities `5300` (Expense). Use the fixture's account ids for A/P, Cash, Vendor Credits, Rent, Utilities.
  - Create a vendor; draft a bill with two expense lines (Rent 6000 → RentExpenseAccountId, Utilities 800 → UtilitiesExpenseAccountId); enter it; approve the A/P entry via a separate approver.
  - Over-pay the bill ($7000 vs $6800) → assert `GET /bills/{id}` returns `Paid` + open balance 0; assert vendor credit balance is $200 (`GET /vendors/{id}/credit-balance`).
  - Enter a second bill; apply the $200 credit to it; approve; assert its open balance dropped by $200.
  - Assert BOTH subledger reconciliations tie out: A/P (`account=PayableAccountId&dimension=Vendor`) and Vendor Credits (`account=VendorCreditsAccountId&dimension=Vendor`) → `TiesOut == true`.

- [ ] **Step 3: Run it to verify it fails, then iterate to green**

Run: `dotnet test Modules/Accounting101.Payables.Tests --filter FullyQualifiedName~PayablesE2eTests`
Expected: FAIL first (wiring), then PASS after fixes. EphemeralMongo is slow; allow time.

- [ ] **Step 4: Run the whole payables suite**

Run: `dotnet test Modules/Accounting101.Payables.Tests`
Expected: PASS (all unit + the e2e).

- [ ] **Step 5: Commit**

```bash
git add Modules/Accounting101.Payables.Tests/PayablesHostFixture.cs Modules/Accounting101.Payables.Tests/PayablesE2eTests.cs
git commit -m "test(payables): end-to-end — bill, over-payment, vendor credit, void, subledgers tie out"
```

---

## Self-review

**Spec coverage:**
- Shared settlement library + invoicing refactor → Tasks 1, 2.
- Module scaffold → Task 3.
- Vendor entity + store → Task 4.
- Bill document + per-line expense account → Tasks 5, 6.
- Bill recipe (Dr expense / Cr A/P, lines collapse) → Task 6.
- Bill payment + vendor credit (asset) + recipes → Tasks 7, 8.
- Stores (bill, bill-payment, vendor-credit) → Tasks 4, 9.
- Derived open balance / settlement status / vendor credit balance → Task 11.
- Validation 422 cases → Tasks 10 (bill), 11 (payment/credit).
- Void-with-restore → Tasks 10 (bill), 11 (payment).
- Posting accounts + provider + DI/manifest/host wiring → Tasks 9 (interface), 12.
- Web surface (vendors, bills, enter/void, payments, credit applications, credit-balance, bill view, settlement-filtered list) → Task 13.
- End-to-end + both subledgers tie out → Task 14.

**Placeholder scan:** No TBD/TODO. The `> NOTE`/`> Step 1: Read the parallel` directives point the implementer at exact existing files to mirror (and to copy verbatim values like `AddModule`/`HttpLedgerClient` usings) — these are verification instructions, not placeholders; the genuinely new logic (recipes, services, the asset Vendor Credits, the shared lib) is given as complete code.

**Type consistency:** `Allocation.TargetId` (shared) is used uniformly in the invoicing refactor (Task 2) and all payables services (Task 11). `BillPosting.ComposeBill`/`ComposeBillPayment`/`ComposeVendorCreditApplication`, `BillService.DraftAsync`/`EnterAsync`/`VoidAsync`, `BillPaymentService.RecordPaymentAsync`/`RecordCreditApplicationAsync`/`VoidPaymentAsync`/`GetBillViewAsync`/`GetVendorCreditBalanceAsync`/`ListBillViewsAsync`, `IBillAccountsProvider.GetBillAccountsAsync`/`GetPaymentAccountsAsync`, `BillPostingAccounts.PayableAccountId`, `BillPaymentPostingAccounts.{PayableAccountId,CashAccountId,VendorCreditsAccountId}`, config keys `Payables:Accounts:Payable|Cash|VendorCredits` — all match across producing and consuming tasks. Bill number format `BILL-{seq:D5}` and the `Vendor` dimension string are consistent between recipe (Task 6/8), store (Task 9), fixture/e2e (Task 14).
