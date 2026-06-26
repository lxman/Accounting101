# Receivables Cash-Side Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the A/R clerk's cash-side dispositions — write-off/bad-debt, credit note, customer refund — to the Receivables module, plus an idempotency retrofit, so removing raw `Post` from the Clerk (slice 6) doesn't strand them.

**Architecture:** Purely additive to the existing Receivables module. Each disposition mirrors the existing `Payment` pattern: a body + document → an evidentiary store collection → service methods on `PaymentService` → an endpoint → a pure posting recipe in `PaymentPosting`. The only edits to existing behavior are the idempotency retrofit (`Id: null` → `EntryIdentity.ForSource(...)`) and widening the derived-settlement aggregations to count the new sources.

**Tech Stack:** C#/.NET 10, MongoDB via the engine document store, xUnit + EphemeralMongo + `WebApplicationFactory<Program>`.

**Spec:** `docs/superpowers/specs/2026-06-26-receivables-cash-side-completion-design.md`

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- All dispositions post `PendingApproval`; the module **never approves its own entries** (SoD).
- Configured account ids (no hardcoded numbers); reuse the shared `Accounting101.Settlement` `Allocation` type and `Settlement` math — do not duplicate.
- **No `viaModule`/credential wiring** in this slice (that's slice 5); the new entries post via the existing token-forwarding `HttpLedgerClient` (`ViaModule = null`).
- Additive only: existing `Payment`/`CreditApplication`/`Invoice` paths keep working. Sole behavior edits: idempotency retrofit + aggregation widening.
- Write-off & credit note **allocate to specific invoices** (reuse the allocation guards); refund is validated **≤ available customer credit**.
- Run test classes one at a time (EphemeralMongo/host-boot flakiness). Stage explicit file lists; do NOT commit in a worktree.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

**Source types (exact strings):** `"Payment"`, `"CreditApplication"` (existing); `"WriteOff"`, `"CreditNote"`, `"Refund"` (new). **Collections:** `payments`, `credit-applications` (existing); `write-offs`, `credit-notes`, `refunds` (new), all evidentiary, Customer-tagged. **Config keys:** `Receivables:Accounts:BadDebtExpense`, `Receivables:Accounts:SalesReturns` (new).

---

## Task 1: Posting layer — accounts, bodies, idempotency retrofit, three recipes (pure)

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentPostingAccounts.cs` (add two account ids)
- Create: `Modules/Receivables/Accounting101.Receivables/DispositionBodies.cs` (the three new input bodies)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentPosting.cs` (idempotency retrofit + three new recipes)
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ConfiguredPaymentAccountsProvider.cs` (read two new keys)
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesHostFixture.cs` (expose + configure two new account ids)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/PaymentPostingTests.cs` (add recipe tests)

**Interfaces:**
- Produces: `PaymentPostingAccounts` gains `Guid BadDebtExpenseAccountId`, `Guid SalesReturnsAccountId` (both `required`).
- Produces bodies: `WriteOffBody(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo)`, `CreditNoteBody(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo)`, `RefundBody(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo)`.
- Produces recipes: `PaymentPosting.ComposeWriteOff(Guid, WriteOffBody, PaymentPostingAccounts) → PostEntryRequest`, `ComposeCreditNote(Guid, CreditNoteBody, PaymentPostingAccounts) → PostEntryRequest`, `ComposeRefund(Guid, RefundBody, PaymentPostingAccounts) → PostEntryRequest`. New constants `WriteOffSourceType="WriteOff"`, `CreditNoteSourceType="CreditNote"`, `RefundSourceType="Refund"`.
- Consumes: existing `EntryIdentity.ForSource(string, Guid)` (Contracts; already used by `InvoicePosting`), `PostEntryRequest`/`PostLineRequest`, `Allocation` (Settlement).

- [ ] **Step 1: Write the failing tests** — append to `PaymentPostingTests.cs`. (The existing file constructs `PaymentPostingAccounts`; once the two new `required` ids are added, every construction must set them — update existing helper accordingly.)

```csharp
// Helper at top of the test class (or update the existing accounts factory):
private static PaymentPostingAccounts Accounts() => new()
{
    ReceivableAccountId = Guid.NewGuid(),
    CashAccountId = Guid.NewGuid(),
    CustomerCreditsAccountId = Guid.NewGuid(),
    BadDebtExpenseAccountId = Guid.NewGuid(),
    SalesReturnsAccountId = Guid.NewGuid(),
};

[Fact]
public void ComposeWriteOff_debits_bad_debt_credits_receivable_balanced()
{
    PaymentPostingAccounts acc = Accounts();
    Guid customer = Guid.NewGuid();
    Guid invoice = Guid.NewGuid();
    WriteOffBody body = new(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], "uncollectible");

    PostEntryRequest entry = PaymentPosting.ComposeWriteOff(Guid.NewGuid(), body, acc);

    Assert.Equal("WriteOff", entry.SourceType);
    PostLineRequest debit = entry.Lines.Single(l => l.Direction == "Debit");
    PostLineRequest credit = entry.Lines.Single(l => l.Direction == "Credit");
    Assert.Equal(acc.BadDebtExpenseAccountId, debit.AccountId);
    Assert.Equal(250m, debit.Amount);
    Assert.Null(debit.Dimensions);                                  // expense line carries no Customer dim
    Assert.Equal(acc.ReceivableAccountId, credit.AccountId);
    Assert.Equal(250m, credit.Amount);
    Assert.Equal(customer, credit.Dimensions!["Customer"]);          // A/R credit carries the Customer dim
    Assert.Equal(entry.Lines.Where(l => l.Direction == "Debit").Sum(l => l.Amount),
                 entry.Lines.Where(l => l.Direction == "Credit").Sum(l => l.Amount));
}

[Fact]
public void ComposeCreditNote_debits_sales_returns_credits_receivable()
{
    PaymentPostingAccounts acc = Accounts();
    Guid customer = Guid.NewGuid();
    CreditNoteBody body = new(customer, new DateOnly(2026, 3, 1), [new Allocation(Guid.NewGuid(), 40m)], "return");

    PostEntryRequest entry = PaymentPosting.ComposeCreditNote(Guid.NewGuid(), body, acc);

    Assert.Equal("CreditNote", entry.SourceType);
    Assert.Equal(acc.SalesReturnsAccountId, entry.Lines.Single(l => l.Direction == "Debit").AccountId);
    PostLineRequest credit = entry.Lines.Single(l => l.Direction == "Credit");
    Assert.Equal(acc.ReceivableAccountId, credit.AccountId);
    Assert.Equal(customer, credit.Dimensions!["Customer"]);
    Assert.Equal(40m, credit.Amount);
}

[Fact]
public void ComposeRefund_debits_customer_credits_credits_cash()
{
    PaymentPostingAccounts acc = Accounts();
    Guid customer = Guid.NewGuid();
    RefundBody body = new(customer, new DateOnly(2026, 3, 1), 75m, "overpayment returned");

    PostEntryRequest entry = PaymentPosting.ComposeRefund(Guid.NewGuid(), body, acc);

    Assert.Equal("Refund", entry.SourceType);
    PostLineRequest debit = entry.Lines.Single(l => l.Direction == "Debit");
    PostLineRequest credit = entry.Lines.Single(l => l.Direction == "Credit");
    Assert.Equal(acc.CustomerCreditsAccountId, debit.AccountId);
    Assert.Equal(customer, debit.Dimensions!["Customer"]);           // Customer Credits draw-down carries the dim
    Assert.Equal(acc.CashAccountId, credit.AccountId);
    Assert.Null(credit.Dimensions);                                  // Cash carries no dim
    Assert.Equal(75m, debit.Amount);
    Assert.Equal(75m, credit.Amount);
}

[Fact]
public void Recipes_carry_deterministic_distinct_source_ids()
{
    PaymentPostingAccounts acc = Accounts();
    Guid docId = Guid.NewGuid();
    Guid customer = Guid.NewGuid();
    DateOnly d = new(2026, 3, 1);

    Guid? payment = PaymentPosting.ComposePayment(docId, new PaymentBody(customer, d, 10m, null, []), acc).Id;
    Guid? credit  = PaymentPosting.ComposeCreditApplication(docId, new CreditApplicationBody(customer, d, [new Allocation(Guid.NewGuid(), 10m)]), acc).Id;
    Guid? wo      = PaymentPosting.ComposeWriteOff(docId, new WriteOffBody(customer, d, [new Allocation(Guid.NewGuid(), 10m)], null), acc).Id;
    Guid? note    = PaymentPosting.ComposeCreditNote(docId, new CreditNoteBody(customer, d, [new Allocation(Guid.NewGuid(), 10m)], null), acc).Id;
    Guid? refund  = PaymentPosting.ComposeRefund(docId, new RefundBody(customer, d, 10m, null), acc).Id;

    Guid?[] ids = [payment, credit, wo, note, refund];
    Assert.All(ids, id => Assert.NotNull(id));                       // idempotency retrofit: no more Id: null
    Assert.Equal(5, ids.Distinct().Count());                        // same doc id, different source type → distinct entry id
    Assert.Equal(EntryIdentity.ForSource("WriteOff", docId), wo);   // deterministic
}
```

- [ ] **Step 2: Run, confirm fail** — `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter PaymentPostingTests` → compile errors (new members absent).

- [ ] **Step 3: Implement.**

`PaymentPostingAccounts.cs` — add inside the record:
```csharp
    /// <summary>Bad Debt Expense — debited when an uncollectible invoice is written off.</summary>
    public required Guid BadDebtExpenseAccountId { get; init; }

    /// <summary>Sales Returns &amp; Allowances (contra-revenue) — debited by a credit note against an invoice.</summary>
    public required Guid SalesReturnsAccountId { get; init; }
```

`DispositionBodies.cs` (new):
```csharp
using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>Write off an uncollectible invoice balance to bad-debt expense. Allocations target the invoices
/// being cleared; the amount written off equals their sum (no unapplied remainder).</summary>
public sealed record WriteOffBody(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Reduce an invoice balance without cash (return/billing adjustment), via contra-revenue.</summary>
public sealed record CreditNoteBody(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Pay a customer's unapplied credit balance back as cash.</summary>
public sealed record RefundBody(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo);
```

`PaymentPosting.cs` — add the constants, retrofit the two existing recipe `Id:` arguments, and add three recipes:
```csharp
    public const string WriteOffSourceType = "WriteOff";
    public const string CreditNoteSourceType = "CreditNote";
    public const string RefundSourceType = "Refund";

    // In ComposePayment: change `Id: null,` to:
    //   Id: EntryIdentity.ForSource(PaymentSourceType, paymentId),
    // In ComposeCreditApplication: change `Id: null,` to:
    //   Id: EntryIdentity.ForSource(CreditApplicationSourceType, id),

    public static PostEntryRequest ComposeWriteOff(Guid writeOffId, WriteOffBody body, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);
        decimal allocated = body.Allocations.Sum(a => a.Amount);
        Dictionary<string, Guid> dim = new() { [CustomerDimension] = body.CustomerId };
        List<PostLineRequest> lines =
        [
            new(accounts.BadDebtExpenseAccountId, "Debit", allocated),
            new(accounts.ReceivableAccountId, "Credit", allocated, Dimensions: dim),
        ];
        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(WriteOffSourceType, writeOffId), EffectiveDate: body.Date,
            Reference: null, Memo: body.Memo, Lines: lines, SourceRef: writeOffId, SourceType: WriteOffSourceType);
    }

    public static PostEntryRequest ComposeCreditNote(Guid creditNoteId, CreditNoteBody body, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);
        decimal allocated = body.Allocations.Sum(a => a.Amount);
        Dictionary<string, Guid> dim = new() { [CustomerDimension] = body.CustomerId };
        List<PostLineRequest> lines =
        [
            new(accounts.SalesReturnsAccountId, "Debit", allocated),
            new(accounts.ReceivableAccountId, "Credit", allocated, Dimensions: dim),
        ];
        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(CreditNoteSourceType, creditNoteId), EffectiveDate: body.Date,
            Reference: null, Memo: body.Memo, Lines: lines, SourceRef: creditNoteId, SourceType: CreditNoteSourceType);
    }

    public static PostEntryRequest ComposeRefund(Guid refundId, RefundBody body, PaymentPostingAccounts accounts)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(accounts);
        Dictionary<string, Guid> dim = new() { [CustomerDimension] = body.CustomerId };
        List<PostLineRequest> lines =
        [
            new(accounts.CustomerCreditsAccountId, "Debit", body.Amount, Dimensions: dim),
            new(accounts.CashAccountId, "Credit", body.Amount),
        ];
        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(RefundSourceType, refundId), EffectiveDate: body.Date,
            Reference: null, Memo: body.Memo, Lines: lines, SourceRef: refundId, SourceType: RefundSourceType);
    }
```

`ConfiguredPaymentAccountsProvider.cs` — add to the constructed record:
```csharp
            BadDebtExpenseAccountId = Read("Receivables:Accounts:BadDebtExpense"),
            SalesReturnsAccountId = Read("Receivables:Accounts:SalesReturns"),
```

`ReceivablesHostFixture.cs` — add two properties and two `UseSetting` lines:
```csharp
    public Guid BadDebtExpenseAccountId { get; } = Guid.NewGuid();
    public Guid SalesReturnsAccountId { get; } = Guid.NewGuid();
    // in ConfigureWebHost, alongside the other Receivables:Accounts settings:
    builder.UseSetting("Receivables:Accounts:BadDebtExpense", BadDebtExpenseAccountId.ToString());
    builder.UseSetting("Receivables:Accounts:SalesReturns", SalesReturnsAccountId.ToString());
```

- [ ] **Step 4: Run, confirm pass** — `PaymentPostingTests` green; **full solution builds 0 warnings** (the new `required` fields must be set at every `PaymentPostingAccounts` construction site — provider done above; the existing E2E uses the provider, so it now requires the two config keys the fixture now sets).

- [ ] **Step 5: Commit** — `feat(receivables): disposition posting recipes + idempotency retrofit + accounts`.

---

## Task 2: Write-off & credit note — documents, store, service, aggregation widening

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables/Disposition.cs` (the `WriteOff`, `CreditNote` documents)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs` (extend `IPaymentStore`)
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs` (implement new methods)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` (record/void methods + widen `AppliedToInvoiceAsync` and `ListInvoiceViewsAsync`)
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs` (extend `InMemoryPaymentStore`)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/PaymentServiceTests.cs` (add write-off/credit-note tests)

**Interfaces:**
- Produces documents: `WriteOff { Guid Id; Guid CustomerId; DateOnly Date; IReadOnlyList<Allocation> Allocations; bool Voided; decimal Total => Allocations.Sum(a => a.Amount); }`; `CreditNote` (identical shape).
- Produces `IPaymentStore` additions: `RecordWriteOffAsync`, `GetWriteOffAsync`, `GetWriteOffsByCustomerAsync`, `VoidWriteOffAsync`; same four for credit note (`...CreditNote...`).
- Produces `PaymentService` additions: `RecordWriteOffAsync(Guid, WriteOffBody, CancellationToken) → Task<WriteOff>`, `VoidWriteOffAsync(Guid, Guid, string?, CancellationToken) → Task<WriteOff>`; same pair for credit note (`...CreditNote...`).
- Consumes: Task 1's bodies + recipes; existing `ValidateAllocationsAsync`, `AppliedToInvoiceAsync`.

- [ ] **Step 1: Write the failing tests** — append to `PaymentServiceTests.cs`. Use the existing fixture pattern in that file (an `InMemoryInvoiceStore` seeded with an Issued invoice, `InMemoryPaymentStore`, `FakeLedgerClient`, a fixed accounts provider). Mirror an existing payment test for setup.

```csharp
[Fact]
public async Task WriteOff_settles_invoice_and_records_balanced_entry()
{
    // Arrange: an Issued invoice of total 250 for customer C (reuse the file's setup helper).
    (PaymentService service, FakeLedgerClient ledger, Guid clientId, Guid customer, Guid invoice) =
        await SetupIssuedInvoiceAsync(total: 250m);

    WriteOff wo = await service.RecordWriteOffAsync(clientId,
        new WriteOffBody(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], "uncollectible"));

    InvoiceView view = (await service.GetInvoiceViewAsync(clientId, invoice))!;
    Assert.Equal(0m, view.OpenBalance);
    Assert.Equal(SettlementStatus.Paid, view.SettlementStatus);
    PostEntryRequest entry = ledger.LastPosted!;                 // expose last posted entry on the fake
    Assert.Equal("WriteOff", entry.SourceType);
    Assert.Equal(wo.Id, entry.SourceRef);
}

[Fact]
public async Task CreditNote_reduces_open_balance()
{
    (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) =
        await SetupIssuedInvoiceAsync(total: 100m);

    await service.RecordCreditNoteAsync(clientId,
        new CreditNoteBody(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 30m)], "partial return"));

    InvoiceView view = (await service.GetInvoiceViewAsync(clientId, invoice))!;
    Assert.Equal(70m, view.OpenBalance);
    Assert.Equal(SettlementStatus.PartiallyPaid, view.SettlementStatus);
}

[Fact]
public async Task WriteOff_over_open_balance_is_rejected()
{
    (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) =
        await SetupIssuedInvoiceAsync(total: 100m);
    await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordWriteOffAsync(clientId,
        new WriteOffBody(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 150m)], null)));
}

[Fact]
public async Task Void_write_off_restores_open_balance()
{
    (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) =
        await SetupIssuedInvoiceAsync(total: 250m);
    WriteOff wo = await service.RecordWriteOffAsync(clientId,
        new WriteOffBody(customer, new DateOnly(2026, 3, 1), [new Allocation(invoice, 250m)], null));

    await service.VoidWriteOffAsync(clientId, wo.Id, "keyed in error");

    InvoiceView view = (await service.GetInvoiceViewAsync(clientId, invoice))!;
    Assert.Equal(250m, view.OpenBalance);
    Assert.Equal(SettlementStatus.Open, view.SettlementStatus);
}
```
(If the file has no `SetupIssuedInvoiceAsync` helper, write one mirroring its existing payment-test arrangement; if `FakeLedgerClient` has no `LastPosted`, add a `public PostEntryRequest? LastPosted` set in `PostAsync`. Also add a cross-customer-rejected and a non-Issued-invoice-rejected test, mirroring the payment guards.)

- [ ] **Step 2: Run, confirm fail** — compile errors (methods/documents absent).

- [ ] **Step 3: Implement.**

`Disposition.cs` (new):
```csharp
using Accounting101.Settlement;

namespace Accounting101.Receivables;

/// <summary>An uncollectible invoice balance written off to bad-debt expense. A non-cash settlement.</summary>
public sealed record WriteOff
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }
    public decimal Total => Allocations.Sum(a => a.Amount);
}

/// <summary>A credit note reducing invoice balances without cash, via contra-revenue.</summary>
public sealed record CreditNote
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<Allocation> Allocations { get; init; }
    public bool Voided { get; init; }
    public decimal Total => Allocations.Sum(a => a.Amount);
}
```

`PaymentPorts.cs` — add to `IPaymentStore`:
```csharp
    Task<WriteOff> RecordWriteOffAsync(Guid clientId, WriteOffBody body, CancellationToken ct = default);
    Task<WriteOff?> GetWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default);
    Task<IReadOnlyList<WriteOff>> GetWriteOffsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task VoidWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default);

    Task<CreditNote> RecordCreditNoteAsync(Guid clientId, CreditNoteBody body, CancellationToken ct = default);
    Task<CreditNote?> GetCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default);
    Task<IReadOnlyList<CreditNote>> GetCreditNotesByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default);
    Task VoidCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default);
```

`DocumentPaymentStore.cs` — add collection constants `WriteOffs = "write-offs"`, `CreditNotes = "credit-notes"`, and implement the eight methods mirroring the payment ones (Create→Finalize→Get for record; `QueryAsync` for by-customer; `VoidAsync(clientId, <collection>, id, ct)` for void; map functions building the documents). Example pair (replicate for credit note):
```csharp
    public async Task<WriteOff> RecordWriteOffAsync(Guid clientId, WriteOffBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, WriteOffs, body, Tags(body.CustomerId), ct);
        await documents.FinalizeAsync(clientId, WriteOffs, id, ct);
        DocumentResult<WriteOffBody>? r = await documents.GetAsync<WriteOffBody>(clientId, WriteOffs, id, ct);
        return MapWriteOff(r!);
    }
    public async Task<WriteOff?> GetWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default)
    {
        DocumentResult<WriteOffBody>? r = await documents.GetAsync<WriteOffBody>(clientId, WriteOffs, writeOffId, ct);
        return r is null ? null : MapWriteOff(r);
    }
    public async Task<IReadOnlyList<WriteOff>> GetWriteOffsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<WriteOffBody>> rs = await documents.QueryAsync<WriteOffBody>(clientId, WriteOffs, Tags(customerId), ct);
        return rs.Select(MapWriteOff).ToList();
    }
    public Task VoidWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, WriteOffs, writeOffId, ct);

    private static WriteOff MapWriteOff(DocumentResult<WriteOffBody> r) => new()
    {
        Id = r.Id, CustomerId = r.Body.CustomerId, Date = r.Body.Date,
        Allocations = r.Body.Allocations, Voided = IsVoided(r.State),
    };
```

`PaymentService.cs` — add record/void methods (mirror `RecordPaymentAsync`/`VoidPaymentAsync`) and widen the two read-side aggregations:
```csharp
    public async Task<WriteOff> RecordWriteOffAsync(Guid clientId, WriteOffBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Allocations.Count == 0 || body.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A write-off needs positive allocations.");
        await ValidateAllocationsAsync(clientId, body.CustomerId, body.Allocations, ct);
        WriteOff recorded = await payments.RecordWriteOffAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeWriteOff(recorded.Id, body, posting), ct);
        return recorded;
    }

    public async Task<WriteOff> VoidWriteOffAsync(Guid clientId, Guid writeOffId, string? reason = null, CancellationToken ct = default)
    {
        WriteOff writeOff = await payments.GetWriteOffAsync(clientId, writeOffId, ct)
            ?? throw new InvalidOperationException($"Write-off {writeOffId} not found.");
        if (writeOff.Voided) throw new InvalidOperationException($"Write-off {writeOffId} is already voided.");
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, writeOffId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for write-off {writeOffId} to void.");
        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(writeOff.Date, reason ?? $"Voided write-off {writeOffId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided write-off {writeOffId}"), ct);
        await payments.VoidWriteOffAsync(clientId, writeOffId, ct);
        return (await payments.GetWriteOffAsync(clientId, writeOffId, ct))!;
    }
```
Replicate the pair for credit note (`RecordCreditNoteAsync`/`VoidCreditNoteAsync`, `"A credit note needs positive allocations."`, `ComposeCreditNote`, store's credit-note methods). Then widen:
```csharp
    // AppliedToInvoiceAsync — add write-offs + credit notes to the sum:
    IReadOnlyList<WriteOff> ws = await payments.GetWriteOffsByCustomerAsync(clientId, customerId, ct);
    IReadOnlyList<CreditNote> ns = await payments.GetCreditNotesByCustomerAsync(clientId, customerId, ct);
    decimal fromWriteOffs = ws.Where(w => !w.Voided).SelectMany(w => w.Allocations).Where(x => x.TargetId == invoiceId).Sum(x => x.Amount);
    decimal fromCreditNotes = ns.Where(n => !n.Voided).SelectMany(n => n.Allocations).Where(x => x.TargetId == invoiceId).Sum(x => x.Amount);
    return fromPayments + fromCredits + fromWriteOffs + fromCreditNotes;

    // ListInvoiceViewsAsync — fold the same two sources into the `applied` dictionary
    // (after the existing payments+credit-applications loop):
    foreach (Allocation a in ws.Where(w => !w.Voided).SelectMany(w => w.Allocations)
                 .Concat(ns.Where(n => !n.Voided).SelectMany(n => n.Allocations)))
        applied[a.TargetId] = applied.GetValueOrDefault(a.TargetId) + a.Amount;
```
(In `ListInvoiceViewsAsync`, fetch `ws`/`ns` from the store alongside the existing `ps`/`cs`.)

`Fakes.cs` — extend `InMemoryPaymentStore` with dictionaries + the eight methods (mirror its payment/credit-application implementations).

- [ ] **Step 4: Run, confirm pass** — `dotnet test ... --filter PaymentServiceTests`; then `--filter PaymentPostingTests` still green. Full solution 0 warnings.

- [ ] **Step 5: Commit** — `feat(receivables): write-off and credit-note dispositions (service + store)`.

---

## Task 3: Customer refund — document, store, service, credit-balance widening

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/Disposition.cs` (add `Refund`)
- Modify: `PaymentPorts.cs`, `DocumentPaymentStore.cs`, `PaymentService.cs`, `Fakes.cs`
- Test: `PaymentServiceTests.cs` (refund tests)

**Interfaces:**
- Produces document: `Refund { Guid Id; Guid CustomerId; DateOnly Date; decimal Amount; bool Voided; }`.
- Produces `IPaymentStore` additions: `RecordRefundAsync`, `GetRefundAsync`, `GetRefundsByCustomerAsync`, `VoidRefundAsync`.
- Produces `PaymentService` additions: `RecordRefundAsync(Guid, RefundBody, CancellationToken) → Task<Refund>`, `VoidRefundAsync(Guid, Guid, string?, CancellationToken) → Task<Refund>`. Widens `GetCustomerCreditBalanceAsync` to subtract non-voided refunds.

- [ ] **Step 1: Write the failing tests** — append to `PaymentServiceTests.cs`:
```csharp
[Fact]
public async Task Refund_draws_down_customer_credit_balance()
{
    // Arrange: customer overpays a 100 invoice by 40 → 40 credit (reuse the overpayment setup).
    (PaymentService service, FakeLedgerClient ledger, Guid clientId, Guid customer, Guid invoice) =
        await SetupIssuedInvoiceAsync(total: 100m);
    await service.RecordPaymentAsync(clientId,
        new PaymentBody(customer, new DateOnly(2026, 3, 1), 140m, "wire", [new Allocation(invoice, 100m)]));
    Assert.Equal(40m, await service.GetCustomerCreditBalanceAsync(clientId, customer));

    Refund refund = await service.RecordRefundAsync(clientId, new RefundBody(customer, new DateOnly(2026, 3, 2), 40m, "returned"));

    Assert.Equal(0m, await service.GetCustomerCreditBalanceAsync(clientId, customer));
    Assert.Equal("Refund", ledger.LastPosted!.SourceType);
    Assert.Equal(refund.Id, ledger.LastPosted!.SourceRef);
}

[Fact]
public async Task Refund_exceeding_available_credit_is_rejected()
{
    (PaymentService service, _, Guid clientId, Guid customer, _) = await SetupIssuedInvoiceAsync(total: 100m);
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        service.RecordRefundAsync(clientId, new RefundBody(customer, new DateOnly(2026, 3, 2), 25m, null)));  // no credit exists
}

[Fact]
public async Task Void_refund_restores_credit_balance()
{
    (PaymentService service, _, Guid clientId, Guid customer, Guid invoice) = await SetupIssuedInvoiceAsync(total: 100m);
    await service.RecordPaymentAsync(clientId,
        new PaymentBody(customer, new DateOnly(2026, 3, 1), 140m, "wire", [new Allocation(invoice, 100m)]));
    Refund refund = await service.RecordRefundAsync(clientId, new RefundBody(customer, new DateOnly(2026, 3, 2), 40m, null));
    Assert.Equal(0m, await service.GetCustomerCreditBalanceAsync(clientId, customer));

    await service.VoidRefundAsync(clientId, refund.Id, "reissued");

    Assert.Equal(40m, await service.GetCustomerCreditBalanceAsync(clientId, customer));
}
```

- [ ] **Step 2: Run, confirm fail.**

- [ ] **Step 3: Implement.**

`Disposition.cs` — add:
```csharp
/// <summary>Cash paid back to a customer against their unapplied credit balance.</summary>
public sealed record Refund
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public bool Voided { get; init; }
}
```

`PaymentPorts.cs` — add `RecordRefundAsync`/`GetRefundAsync`/`GetRefundsByCustomerAsync`/`VoidRefundAsync` (signatures per Interfaces above). `DocumentPaymentStore.cs` — collection `Refunds = "refunds"`, mirror the record/get/by-customer/void + `MapRefund`. `Fakes.cs` — extend `InMemoryPaymentStore`.

`PaymentService.cs`:
```csharp
    public async Task<Refund> RecordRefundAsync(Guid clientId, RefundBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Amount <= 0m) throw new InvalidOperationException("A refund amount must be greater than zero.");
        decimal available = await GetCustomerCreditBalanceAsync(clientId, body.CustomerId, ct);
        if (body.Amount > available)
            throw new InvalidOperationException($"Refund of {body.Amount} exceeds available credit {available}.");
        Refund recorded = await payments.RecordRefundAsync(clientId, body, ct);
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeRefund(recorded.Id, body, posting), ct);
        return recorded;
    }

    public async Task<Refund> VoidRefundAsync(Guid clientId, Guid refundId, string? reason = null, CancellationToken ct = default)
    {
        Refund refund = await payments.GetRefundAsync(clientId, refundId, ct)
            ?? throw new InvalidOperationException($"Refund {refundId} not found.");
        if (refund.Voided) throw new InvalidOperationException($"Refund {refundId} is already voided.");
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, refundId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for refund {refundId} to void.");
        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(refund.Date, reason ?? $"Voided refund {refundId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided refund {refundId}"), ct);
        await payments.VoidRefundAsync(clientId, refundId, ct);
        return (await payments.GetRefundAsync(clientId, refundId, ct))!;
    }
```
Widen `GetCustomerCreditBalanceAsync`:
```csharp
    IReadOnlyList<Refund> rs = await payments.GetRefundsByCustomerAsync(clientId, customerId, ct);
    decimal created = ps.Where(p => !p.Voided).Sum(p => p.Unapplied);
    decimal spent = cs.Where(c => !c.Voided).Sum(c => c.Applied);
    decimal refunded = rs.Where(r => !r.Voided).Sum(r => r.Amount);
    return created - spent - refunded;
```

- [ ] **Step 4: Run, confirm pass** — `PaymentServiceTests` green; `PaymentPostingTests` still green; 0 warnings.

- [ ] **Step 5: Commit** — `feat(receivables): customer refund disposition (service + store)`.

---

## Task 4: API endpoints, manifest, and cross-host integration

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesRequests.cs` (three request records)
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (map + handlers)
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs` (manifest: three evidentiary collections)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesDispositionsE2eTests.cs` (new, real host)

**Interfaces:**
- Consumes Tasks 1-3 `PaymentService` methods.
- Produces requests: `WriteOffRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo)`, `CreditNoteRequest(... Allocations ...)`, `RefundRequest(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo)`.
- Produces endpoints: `POST /clients/{clientId}/write-offs` (+ `/{id}/void`), `POST .../credit-notes` (+ `/{id}/void`), `POST .../refunds` (+ `/{id}/void`).

- [ ] **Step 1: Write the failing E2E tests** — `ReceivablesDispositionsE2eTests.cs`, mirroring `CashApplicationTests` (same `ReceivablesHostFixture`, `SeedSodClientAsync`, `SetUpChartAsync` — extended to also `PutAccount` the Bad Debt Expense `"6000" Expense` and Sales Returns `"4900" Revenue` accounts using `fixture.BadDebtExpenseAccountId`/`fixture.SalesReturnsAccountId`; same `IssueInvoiceAsync` + `ApproveBySourceRefAsync` helpers):
```csharp
[Fact]
public async Task WriteOff_settles_remaining_balance_through_host()
{
    (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
    await SetUpChartAsync(controller, clientId);
    Guid customer = await CreateCustomerAsync(clerk, clientId);
    Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 1000m);

    // partial payment of 600, approved
    HttpResponseMessage pay = await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
        new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 600m, "wire", [new Allocation(invoice, 600m)]));
    Payment payment = (await pay.Content.ReadFromJsonAsync<Payment>())!;
    await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

    // write off the remaining 400, approved
    HttpResponseMessage wo = await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
        new WriteOffRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice, 400m)], "uncollectible"));
    Assert.Equal(HttpStatusCode.Created, wo.StatusCode);
    WriteOff writeOff = (await wo.Content.ReadFromJsonAsync<WriteOff>())!;
    await ApproveBySourceRefAsync(clerk, approver, clientId, writeOff.Id);

    InvoiceView view = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice}"))!;
    Assert.Equal(0m, view.OpenBalance);
    Assert.Equal(SettlementStatus.Paid, view.SettlementStatus);
}

[Fact]
public async Task Refund_of_overpayment_credit_through_host()
{
    (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
    await SetUpChartAsync(controller, clientId);
    Guid customer = await CreateCustomerAsync(clerk, clientId);
    Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 500m);

    Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
        new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 700m, "wire", [new Allocation(invoice, 500m)])))
        .Content.ReadFromJsonAsync<Payment>())!;
    await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

    HttpResponseMessage rf = await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
        new RefundRequest(customer, new DateOnly(2026, 3, 6), 200m, "returned overpayment"));
    Assert.Equal(HttpStatusCode.Created, rf.StatusCode);
    Refund refund = (await rf.Content.ReadFromJsonAsync<Refund>())!;
    await ApproveBySourceRefAsync(clerk, approver, clientId, refund.Id);

    var bal = await clerk.GetFromJsonAsync<CreditBalanceResponse>($"/clients/{clientId}/customers/{customer}/credit-balance");
    Assert.Equal(0m, bal!.CreditBalance);
}

// Add a credit-note E2E mirroring the write-off one (assert OpenBalance reduced).
// CreditBalanceResponse: record CreditBalanceResponse(Guid CustomerId, decimal CreditBalance);
```
(Add a `CreateCustomerAsync` helper if `CashApplicationTests` doesn't expose a reusable one — POST `/customers` with `CreateCustomerRequest`.)

- [ ] **Step 2: Run, confirm fail** — endpoints/requests absent (404/compile).

- [ ] **Step 3: Implement.**

`ReceivablesRequests.cs` — add:
```csharp
/// <summary>Write off invoice balances to bad-debt expense.</summary>
public sealed record WriteOffRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Issue a credit note against invoices (contra-revenue, no cash).</summary>
public sealed record CreditNoteRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo);

/// <summary>Refund a customer's unapplied credit as cash.</summary>
public sealed record RefundRequest(Guid CustomerId, DateOnly Date, decimal Amount, string? Memo);
```

`ReceivablesServiceExtensions.cs` — in the manifest lambda, add:
```csharp
            manifest.Evidentiary("write-offs", "Customer");
            manifest.Evidentiary("credit-notes", "Customer");
            manifest.Evidentiary("refunds", "Customer");
```

`ReceivablesEndpoints.cs` — in `MapReceivablesEndpoints`, add the six routes; add handlers mirroring `RecordPayment`/`VoidPayment` (422 on `InvalidOperationException` for record, 409 for void; `Results.Created` with the new id). E.g.:
```csharp
        clients.MapPost("/write-offs", RecordWriteOff);
        clients.MapPost("/write-offs/{writeOffId:guid}/void", VoidWriteOff);
        clients.MapPost("/credit-notes", RecordCreditNote);
        clients.MapPost("/credit-notes/{creditNoteId:guid}/void", VoidCreditNote);
        clients.MapPost("/refunds", RecordRefund);
        clients.MapPost("/refunds/{refundId:guid}/void", VoidRefund);
```
```csharp
    private static async Task<IResult> RecordWriteOff(
        Guid clientId, WriteOffRequest request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            WriteOff recorded = await service.RecordWriteOffAsync(clientId,
                new WriteOffBody(request.CustomerId, request.Date, request.Allocations, request.Memo), cancellationToken);
            return Results.Created($"/clients/{clientId}/write-offs/{recorded.Id}", recorded);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidWriteOff(
        Guid clientId, Guid writeOffId, VoidInvoiceRequest? request, PaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            WriteOff voided = await service.VoidWriteOffAsync(clientId, writeOffId, request?.Reason, cancellationToken);
            return Results.Ok(voided);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
```
Replicate the handler pair for credit note and refund (`CreditNoteRequest`→`CreditNoteBody`→`RecordCreditNoteAsync`; `RefundRequest`→`RefundBody`→`RecordRefundAsync`; reuse `VoidInvoiceRequest` for the void reason, as `VoidPayment` already does).

- [ ] **Step 4: Run, confirm pass** — `dotnet test ... --filter ReceivablesDispositionsE2eTests`; re-run `CashApplicationTests` (existing E2E still green — proves the idempotency retrofit + manifest additions didn't regress payments). Full solution 0 warnings.

- [ ] **Step 5: Commit** — `feat(receivables): disposition endpoints, manifest, and cross-host integration`.

---

## Final verification
- [ ] `dotnet build Accounting101.slnx -c Debug` → 0 warnings.
- [ ] Run individually: `PaymentPostingTests`, `PaymentServiceTests`, `ReceivablesDispositionsE2eTests`, `CashApplicationTests` (regression) — all green.
- [ ] Confirm: write-off (`Dr Bad Debt Expense / Cr A/R`), credit note (`Dr Sales Returns / Cr A/R`), refund (`Dr Customer Credits / Cr Cash`) post `PendingApproval`; settlement status reflects write-offs + credit notes; customer credit balance reflects refunds; all five recipes carry deterministic entry ids; existing payment/invoice paths unchanged; no `viaModule` wiring touched.
- [ ] Whole-branch review on the most capable model, then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- **Spec coverage:** write-off (T1 recipe, T2 service/store/endpoint), credit note (T1/T2/T4), refund (T1 recipe, T3 service/store, T4 endpoint), idempotency retrofit (T1), aggregation widening (T2 applied-to-invoice + list views; T3 credit balance), accounts (T1), manifest (T4), E2E (T4). Scope boundaries (no viaModule, no aging, no sim) respected — none of those files are touched.
- **Type consistency:** `WriteOffBody`/`CreditNoteBody`/`RefundBody` (T1) consumed unchanged by recipes (T1), service (T2/T3), endpoints (T4); `WriteOff`/`CreditNote`/`Refund` documents (T2/T3) returned by store + service + endpoints; `PaymentPostingAccounts` two new ids set in provider (T1) and asserted in tests; source-type strings + collection names match across recipe, store, and manifest.
- **Open implementer checks:** (a) the `FakeLedgerClient` may need a `LastPosted` property (T2 note); (b) `PaymentServiceTests` may need a `SetupIssuedInvoiceAsync` helper if absent — mirror an existing payment test; (c) adding two `required` ids to `PaymentPostingAccounts` makes the build fail at every construction site until set — provider + fixture handled in T1, watch for any other.
