# Bank Reconciliation — Slice 3 (Adjustments Posting) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a clerk record a bank-only adjustment (fee/interest) against a reconciliation; the module posts a balanced PendingApproval entry via module-identity (`ViaModule="reconciliation"`); a distinct Approver approves it via the engine; once Posted it is an eligible cash entry that clearing drives the residual to zero. Adjustments are doc-backed (`ADJ-`), voidable, and posting failures relay as clean 4xx.

**Architecture:** Reconciliation becomes a posting module, mirroring the Cash module's posting path. Adds a full `ILedgerClient` (Post/Reverse/Void/GetEntriesBySourceRef) + a credentialed `HttpLedgerClient` + a `LedgerClientException` relay, a pure `AdjustmentPosting` composer, a `BankAdjustment` evidentiary doc + store, a focused `AdjustmentService`, and the adjustment endpoints. Slice 1's read-only reader stays for worksheet reads; the cleared-method math is unchanged.

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs, xUnit, EphemeralMongo for E2E. Extends the Slice 1-2 Reconciliation module.

## Global Constraints

- New code only under `Modules/Banking/Reconciliation/`. Slices 1-2 behavior and the cleared-method math must stay unchanged. No other module touched.
- **Maker-checker reuses the engine** — the module posts PendingApproval and never self-approves; approval is the engine's `POST /clients/{c}/entries/{entryId}/approve` (SoD-enforced). No approval endpoint in Reconciliation.
- The credentialed `HttpLedgerClient.PostAsync` attaches `X-Module-Key`/`X-Module-Secret` (the `ModuleCredential` keyed `"reconciliation"` that `AddModule` already mints) so the engine stamps `ViaModule="reconciliation"`. Reverse/Void/GetEntriesBySourceRef forward the bearer only.
- Composer: **Charge** (fee) → Dr offset / Cr cash; **Credit** (interest) → Dr cash / Cr offset. `EntryIdentity.ForSource("BankAdjustment", id)`, `SourceType="BankAdjustment"`, `SourceRef=id`, `EffectiveDate=body.Date`. Amount must be > 0 and offset ≠ cash account.
- Errors: `ArgumentException`→422, `InvalidOperationException`→409 (reconciliation/adjustment not-found or wrong state), `LedgerClientException`→its relayed status, null adjustment→404.
- Money is `decimal`. Commit trailer, verbatim, on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Confirmed patterns to mirror

- **Cash composer** (`Modules/Banking/Cash/Accounting101.Banking.Cash/CashPosting.cs`): pure static, builds `PostEntryRequest(Id, EffectiveDate, Reference, Memo, Lines, SourceRef, SourceType)` with `EntryIdentity.ForSource(...)`.
- **Cash void flow** (`CashService.VoidDisbursementAsync`): `GetEntriesBySourceRefAsync(id)` → `entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })` → if `entry.Posting == "Posted"` `ReverseAsync(entry.Id, new ReverseRequest(date, reason))` else `VoidAsync(entry.Id, new VoidRequest(reason))` → store `VoidAsync`.
- **Full ILedgerClient + credentialed HttpLedgerClient + relay**: `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs` (ctor `[FromKeyedServices("payables")] ModuleCredential`; `EnsureSuccessAsync`/`ReasonFrom`) and `Modules/Payables/Accounting101.Payables/LedgerClientException.cs`. Mirror with key `"reconciliation"`, WITHOUT the `ApproveAsync` method (the module never approves).
- **Evidentiary store** (`Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDisbursementStore.cs`): `CreateAsync`→`FinalizeAsync`→`GetAsync`→Map; Number `$"ADJ-{seq:D5}"` from `result.Sequence`; Status `Voided`/`Superseded`→Void else Posted.
- **Wire types** (`Accounting101.Ledger.Contracts`): `PostEntryRequest(Guid? Id, DateOnly EffectiveDate, string? Reference, string? Memo, IReadOnlyList<PostLineRequest> Lines, Guid? SourceRef = null, string? SourceType = null, string? Type = null)`; `PostLineRequest(Guid AccountId, string Direction, decimal Amount, IReadOnlyDictionary<string,Guid>? Dimensions = null)`; `PostEntryResponse(Guid Id, string Status, string Posting)`; `EntryResponse(Guid Id, long SequenceNumber, DateOnly EffectiveDate, string Type, string Status, string Posting, int LineCount, Guid? Supersedes, Guid? SupersededBy, Guid? ReversalOf, Guid? ReversedBy, IReadOnlyList<EntryLineResponse> Lines, Guid? SourceRef = null, string? SourceType = null, string? Reference = null, string? Memo = null, string? ViaModule = null)`; `ReverseRequest(DateOnly ReversalDate, string? Reason)`; `VoidRequest(string? Reason)`; `EntryIdentity.ForSource(string sourceType, Guid sourceRef)→Guid`.
- **Slice 1 surface**: `Reconciliation { Guid Id; ...; Guid CashAccountId; DateOnly StatementDate; ReconciliationStatus Status; ... }`; `IReconciliationStore.GetAsync(clientId, id)→Task<Reconciliation?>` / `.SaveAsync` / `.CreateAsync(clientId, cashAccountId, bankStatementId, statementDate)`. Tests: `Fakes.cs` has `InMemoryReconciliationStore`; `ReconciliationE2eTests.cs` has private static `SetUpChartAsync(controller, clientId, fixture)` + `ApproveBySourceRefAsync(reader, approver, clientId, sourceRef)`; fixture `ReconciliationHostFixture` exposes `CashAccountId`, `MembersCapitalAccountId`, `InterestExpenseAccountId`, `SeedSodClientAsync()`, and repoints the named `"ReconciliationLedgerClient"` loopback in `ConfigureTestServices`.
- **Registration** (`…Reconciliation.Api/ReconciliationServiceExtensions.cs`): existing `AddReconciliation` calls `services.AddModule(new ModuleIdentity("reconciliation"), "Reconciliation", manifest => { manifest.Evidentiary("bank-statements"); manifest.Plain("reconciliations"); })` then registers stores + `ReconciliationService` + the named `"ReconciliationLedgerClient"`. `ModuleCredential` + `ModuleIdentity` are in `Accounting101.Ledger.Api.Auth`; `AddModule` in `Accounting101.Ledger.Api.Hosting`.
- **Endpoints** (`…Reconciliation.Api/ReconciliationEndpoints.cs`): `MapReconciliationEndpoints` maps a group `clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization()`; handlers resolve services per-parameter from DI. (Adjustment routes will be added here — no Program.cs change.)

---

### Task 1: Domain — adjustment types, posting seam, composer (TDD on composer)

**Files:**
- Create: `…/Accounting101.Banking.Reconciliation/BankAdjustment.cs`, `BankAdjustmentBody.cs`, `AdjustmentPorts.cs`, `LedgerClientException.cs`, `AdjustmentPosting.cs`
- Create (test): `…/Accounting101.Banking.Reconciliation.Tests/AdjustmentPostingTests.cs`

**Interfaces:**
- Produces: `AdjustmentKind`, `BankAdjustmentStatus`, `BankAdjustment`, `BankAdjustmentBody`, `IBankAdjustmentStore`, `ILedgerClient` (full), `LedgerClientException`, `AdjustmentPosting.Compose` — consumed by Tasks 2-5.

- [ ] **Step 1: Write the failing composer tests**

Create `AdjustmentPostingTests.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class AdjustmentPostingTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Offset = Guid.NewGuid();
    private static readonly DateOnly D = new(2026, 1, 31);

    private static BankAdjustmentBody Body(AdjustmentKind kind, decimal amount = 5m) =>
        new(Guid.NewGuid(), Cash, Offset, kind, amount, D, "bank fee");

    [Fact]
    public void Charge_debits_the_offset_and_credits_cash()
    {
        Guid id = Guid.NewGuid();
        PostEntryRequest e = AdjustmentPosting.Compose(id, Body(AdjustmentKind.Charge));
        Assert.Equal(EntryIdentity.ForSource("BankAdjustment", id), e.Id);
        Assert.Equal("BankAdjustment", e.SourceType);
        Assert.Equal(id, e.SourceRef);
        Assert.Equal(D, e.EffectiveDate);
        Assert.Contains(e.Lines, l => l.AccountId == Offset && l.Direction == "Debit" && l.Amount == 5m);
        Assert.Contains(e.Lines, l => l.AccountId == Cash && l.Direction == "Credit" && l.Amount == 5m);
    }

    [Fact]
    public void Credit_debits_cash_and_credits_the_offset()
    {
        PostEntryRequest e = AdjustmentPosting.Compose(Guid.NewGuid(), Body(AdjustmentKind.Credit));
        Assert.Contains(e.Lines, l => l.AccountId == Cash && l.Direction == "Debit" && l.Amount == 5m);
        Assert.Contains(e.Lines, l => l.AccountId == Offset && l.Direction == "Credit" && l.Amount == 5m);
    }

    [Fact]
    public void A_non_positive_amount_is_rejected() =>
        Assert.Throws<ArgumentException>(() => AdjustmentPosting.Compose(Guid.NewGuid(), Body(AdjustmentKind.Charge, 0m)));

    [Fact]
    public void An_offset_equal_to_cash_is_rejected()
    {
        BankAdjustmentBody body = new(Guid.NewGuid(), Cash, Cash, AdjustmentKind.Charge, 5m, D, null);
        Assert.Throws<ArgumentException>(() => AdjustmentPosting.Compose(Guid.NewGuid(), body));
    }
}
```

- [ ] **Step 2: Run, verify it FAILS (no types yet)**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~AdjustmentPostingTests" --nologo`
Expected: FAIL to compile.

- [ ] **Step 3: Create the domain files**

`BankAdjustment.cs`:
```csharp
namespace Accounting101.Banking.Reconciliation;

/// <summary>A bank-only adjustment booked during reconciliation. Charge = a bank fee (reduces cash);
/// Credit = bank interest (increases cash).</summary>
public enum AdjustmentKind { Charge, Credit }

public enum BankAdjustmentStatus { Posted, Void }

/// <summary>An evidentiary record of a bank adjustment — posted in one step (PendingApproval entry),
/// voidable. Number/status derived from the engine's document envelope.</summary>
public sealed record BankAdjustment
{
    public required Guid Id { get; init; }
    public string? Number { get; init; }                 // "ADJ-{seq:D5}"
    public required Guid ReconciliationId { get; init; }
    public required Guid CashAccountId { get; init; }
    public required Guid OffsetAccountId { get; init; }
    public required AdjustmentKind Kind { get; init; }
    public required decimal Amount { get; init; }
    public required DateOnly Date { get; init; }
    public string? Memo { get; init; }
    public BankAdjustmentStatus Status { get; init; } = BankAdjustmentStatus.Posted;
}
```

`BankAdjustmentBody.cs`:
```csharp
namespace Accounting101.Banking.Reconciliation;

/// <summary>Stored body of a bank adjustment (Number/Status/Id derived).</summary>
public sealed record BankAdjustmentBody(
    Guid ReconciliationId, Guid CashAccountId, Guid OffsetAccountId,
    AdjustmentKind Kind, decimal Amount, DateOnly Date, string? Memo);
```

`AdjustmentPorts.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Bank adjustments as evidentiary documents (one-step record; voidable).</summary>
public interface IBankAdjustmentStore
{
    Task<BankAdjustment> RecordAsync(Guid clientId, BankAdjustmentBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<BankAdjustment?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<BankAdjustment>> GetByReconciliationAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default);
}

/// <summary>The module's POSTING window onto the engine (Slice 3): posts adjustments via module identity
/// and reverses/voids them. Distinct from the Slice 1 read-only reader. Mirrors the Cash module's seam.</summary>
public interface ILedgerClient
{
    Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default);
    Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default);
    Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default);
}
```

`LedgerClientException.cs` (mirror of Payables'):
```csharp
namespace Accounting101.Banking.Reconciliation;

/// <summary>A ledger call returned a non-success status. Carries the engine's HTTP status and reason so the
/// module can relay the real cause (a closed-period 409, an unknown-account 422) instead of an opaque 500.</summary>
public sealed class LedgerClientException(int statusCode, string reason)
    : Exception($"Ledger request failed ({statusCode}): {reason}")
{
    public int StatusCode { get; } = statusCode;
    public string Reason { get; } = reason;
}
```

`AdjustmentPosting.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>The bank-adjustment recipe: composes one balanced journal entry. Charge (a fee) debits the
/// offset account and credits cash; Credit (interest) debits cash and credits the offset. Pure.</summary>
public static class AdjustmentPosting
{
    public const string SourceType = "BankAdjustment";

    public static PostEntryRequest Compose(Guid adjustmentId, BankAdjustmentBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Amount <= 0m)
            throw new ArgumentException($"Adjustment amount must be positive; got {body.Amount}.", nameof(body));
        if (body.OffsetAccountId == body.CashAccountId)
            throw new ArgumentException("The offset account must differ from the cash account.", nameof(body));

        (Guid debit, Guid credit) = body.Kind == AdjustmentKind.Charge
            ? (body.OffsetAccountId, body.CashAccountId)   // fee: Dr offset / Cr cash
            : (body.CashAccountId, body.OffsetAccountId);  // interest: Dr cash / Cr offset

        List<PostLineRequest> lines =
        [
            new PostLineRequest(debit, "Debit", body.Amount),
            new PostLineRequest(credit, "Credit", body.Amount),
        ];

        return new PostEntryRequest(
            Id: EntryIdentity.ForSource(SourceType, adjustmentId),
            EffectiveDate: body.Date,
            Reference: "ADJ",
            Memo: body.Memo,
            Lines: lines,
            SourceRef: adjustmentId,
            SourceType: SourceType);
    }
}
```

- [ ] **Step 4: Run, verify the composer tests PASS**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~AdjustmentPostingTests" --nologo`
Expected: 4/4 PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/BankAdjustment.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/BankAdjustmentBody.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AdjustmentPorts.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/LedgerClientException.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AdjustmentPosting.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AdjustmentPostingTests.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): adjustment domain + posting seam + composer (slice 3)

BankAdjustment records, the full ILedgerClient posting seam + LedgerClientException
relay, and the pure AdjustmentPosting composer (Charge: Dr offset/Cr cash;
Credit: Dr cash/Cr offset; EntryIdentity.ForSource). Unit-tested.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `DocumentBankAdjustmentStore`

**Files:**
- Create: `…/Accounting101.Banking.Reconciliation/DocumentBankAdjustmentStore.cs`

**Interfaces:**
- Consumes: `IBankAdjustmentStore`, `BankAdjustment`, `BankAdjustmentBody` (Task 1), `IDocumentStore` (Ledger.Contracts).

- [ ] **Step 1: Create the store (mirror `DocumentCashDisbursementStore`)**

`DocumentBankAdjustmentStore.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Persists bank adjustments through the engine's document store as evidentiary data: created
/// mutable then immediately finalized into an append-only numbered document, and voidable. Number/status
/// derived from the envelope. Lists by reconciliation.</summary>
public sealed class DocumentBankAdjustmentStore(IDocumentStore documents) : IBankAdjustmentStore
{
    private const string Collection = "bank-adjustments";

    public async Task<BankAdjustment> RecordAsync(Guid clientId, BankAdjustmentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(), ct);
        await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<BankAdjustmentBody>? result = await documents.GetAsync<BankAdjustmentBody>(clientId, Collection, id, ct);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, id, ct);

    public async Task<BankAdjustment?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        DocumentResult<BankAdjustmentBody>? result = await documents.GetAsync<BankAdjustmentBody>(clientId, Collection, id, ct);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<BankAdjustment>> GetByReconciliationAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<BankAdjustmentBody>> results =
            await documents.QueryAsync<BankAdjustmentBody>(clientId, Collection, Tags(), ct);
        return results.Where(r => r.Body.ReconciliationId == reconciliationId).Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags() => new();

    private static BankAdjustment Map(DocumentResult<BankAdjustmentBody> r) => new()
    {
        Id = r.Id,
        Number = r.Sequence is { } seq ? $"ADJ-{seq:D5}" : null,
        ReconciliationId = r.Body.ReconciliationId,
        CashAccountId = r.Body.CashAccountId,
        OffsetAccountId = r.Body.OffsetAccountId,
        Kind = r.Body.Kind,
        Amount = r.Body.Amount,
        Date = r.Body.Date,
        Memo = r.Body.Memo,
        Status = r.State is DocumentLifecycle.Voided or DocumentLifecycle.Superseded ? BankAdjustmentStatus.Void : BankAdjustmentStatus.Posted,
    };
}
```

- [ ] **Step 2: Build the domain project, then commit**

Run: `dotnet build Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/Accounting101.Banking.Reconciliation.csproj --nologo`
Expected: Build succeeded.

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/DocumentBankAdjustmentStore.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): DocumentBankAdjustmentStore (evidentiary, ADJ-)

Evidentiary create+finalize with an ADJ- number; lists by reconciliation;
voidable. Mirrors DocumentCashDisbursementStore.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `AdjustmentService` + service tests

**Files:**
- Create: `…/Accounting101.Banking.Reconciliation/AdjustmentService.cs`
- Modify: `…/Accounting101.Banking.Reconciliation.Tests/Fakes.cs` (append `InMemoryBankAdjustmentStore` + `FakePostingLedger`)
- Create (test): `…/Accounting101.Banking.Reconciliation.Tests/AdjustmentServiceTests.cs`

**Interfaces:**
- Consumes: `IReconciliationStore` (Slice 1), `IBankAdjustmentStore`, `ILedgerClient`, `AdjustmentPosting` (Tasks 1-2).
- Produces: `AdjustmentService`, `RecordAdjustmentInput` (consumed by Task 4 endpoints).

- [ ] **Step 1: Write the service**

`AdjustmentService.cs`:
```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Records bank-only adjustments against a reconciliation and posts them PendingApproval through
/// module identity; voids them (reverse if posted, withdraw if pending). The module never self-approves.</summary>
public sealed class AdjustmentService(
    IReconciliationStore reconciliations, IBankAdjustmentStore adjustments, ILedgerClient ledger)
{
    public async Task<BankAdjustment> RecordAdjustmentAsync(
        Guid clientId, Guid reconciliationId, RecordAdjustmentInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Reconciliation reconciliation = await reconciliations.GetAsync(clientId, reconciliationId, ct)
            ?? throw new InvalidOperationException($"Reconciliation {reconciliationId} not found.");
        if (reconciliation.Status == ReconciliationStatus.Completed)
            throw new InvalidOperationException($"Reconciliation {reconciliationId} is already completed.");
        if (input.Amount <= 0m)
            throw new ArgumentException($"Adjustment amount must be positive; got {input.Amount}.");
        if (input.OffsetAccountId == reconciliation.CashAccountId)
            throw new ArgumentException("The offset account must differ from the cash account.");

        BankAdjustmentBody body = new(
            reconciliationId, reconciliation.CashAccountId, input.OffsetAccountId,
            input.Kind, input.Amount, input.Date ?? reconciliation.StatementDate, input.Memo);

        BankAdjustment adjustment = await adjustments.RecordAsync(clientId, body, ct);
        PostEntryRequest entry = AdjustmentPosting.Compose(adjustment.Id, body);
        await ledger.PostAsync(clientId, entry, ct);   // PendingApproval — module never approves
        return adjustment;
    }

    public async Task<BankAdjustment> VoidAdjustmentAsync(
        Guid clientId, Guid adjustmentId, string? reason = null, CancellationToken ct = default)
    {
        BankAdjustment adjustment = await adjustments.GetAsync(clientId, adjustmentId, ct)
            ?? throw new InvalidOperationException($"Bank adjustment {adjustmentId} not found.");
        if (adjustment.Status != BankAdjustmentStatus.Posted)
            throw new InvalidOperationException($"Only a posted adjustment can be voided; {adjustmentId} is {adjustment.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, adjustmentId, ct);
        EntryResponse entry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for adjustment {adjustment.Number} to void.");

        if (entry.Posting == "Posted")
            await ledger.ReverseAsync(clientId, entry.Id, new ReverseRequest(adjustment.Date, reason ?? $"Voided bank adjustment {adjustmentId}"), ct);
        else
            await ledger.VoidAsync(clientId, entry.Id, new VoidRequest(reason ?? $"Voided bank adjustment {adjustmentId}"), ct);

        await adjustments.VoidAsync(clientId, adjustmentId, ct);
        return (await adjustments.GetAsync(clientId, adjustmentId, ct))!;
    }

    public Task<BankAdjustment?> GetAdjustmentAsync(Guid clientId, Guid adjustmentId, CancellationToken ct = default) =>
        adjustments.GetAsync(clientId, adjustmentId, ct);

    public Task<IReadOnlyList<BankAdjustment>> ListAdjustmentsAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default) =>
        adjustments.GetByReconciliationAsync(clientId, reconciliationId, ct);
}

/// <summary>Clerk-supplied inputs for a bank adjustment (cash account + default date come from the reconciliation).</summary>
public sealed record RecordAdjustmentInput(Guid OffsetAccountId, decimal Amount, AdjustmentKind Kind, DateOnly? Date, string? Memo);
```

- [ ] **Step 2: Append the test fakes**

Append to `Fakes.cs` (it already has `InMemoryReconciliationStore` + the `using System.Collections.Concurrent;`/`Accounting101.Ledger.Contracts;` usings):
```csharp
internal sealed class InMemoryBankAdjustmentStore : IBankAdjustmentStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), BankAdjustment> _store = new();
    private long _seq;
    public Task<BankAdjustment> RecordAsync(Guid clientId, BankAdjustmentBody body, CancellationToken ct = default)
    {
        BankAdjustment a = new()
        {
            Id = Guid.NewGuid(), Number = $"ADJ-{Interlocked.Increment(ref _seq):D5}",
            ReconciliationId = body.ReconciliationId, CashAccountId = body.CashAccountId,
            OffsetAccountId = body.OffsetAccountId, Kind = body.Kind, Amount = body.Amount,
            Date = body.Date, Memo = body.Memo, Status = BankAdjustmentStatus.Posted,
        };
        _store[(clientId, a.Id)] = a;
        return Task.FromResult(a);
    }
    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        if (_store.TryGetValue((clientId, id), out BankAdjustment? a))
            _store[(clientId, id)] = a with { Status = BankAdjustmentStatus.Void };
        return Task.CompletedTask;
    }
    public Task<BankAdjustment?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, id)));
    public Task<IReadOnlyList<BankAdjustment>> GetByReconciliationAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BankAdjustment>>(_store.Values.Where(a => a.ReconciliationId == reconciliationId).ToList());
}

internal sealed class FakePostingLedger : ILedgerClient
{
    public PostEntryRequest? Posted { get; private set; }
    public bool Reversed { get; private set; }
    public bool Voided { get; private set; }
    public string EntryPosting { get; set; } = "PendingApproval";   // set "Posted" to simulate an approved entry
    private readonly Guid _entryId = Guid.NewGuid();

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        Posted = entry;
        return Task.FromResult(new PostEntryResponse(_entryId, "Active", EntryPosting));
    }
    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    { Reversed = true; return Task.FromResult(StubEntry()); }
    public Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    { Voided = true; return Task.FromResult(StubEntry()); }
    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EntryResponse>>(Posted is null ? [] : [StubEntry()]);

    private EntryResponse StubEntry() =>
        new(_entryId, 0, Posted!.EffectiveDate, "Standard", "Active", EntryPosting, Posted.Lines.Count,
            null, null, null, null, [], SourceRef: Posted.SourceRef, SourceType: Posted.SourceType, ViaModule: "reconciliation");
}
```

- [ ] **Step 3: Write the service tests**

Create `AdjustmentServiceTests.cs`:
```csharp
namespace Accounting101.Banking.Reconciliation.Tests;

public sealed class AdjustmentServiceTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Offset = Guid.NewGuid();
    private static readonly DateOnly StmtDate = new(2026, 1, 31);

    private static (AdjustmentService svc, InMemoryReconciliationStore recs, InMemoryBankAdjustmentStore adjs, FakePostingLedger ledger) Build()
    {
        InMemoryReconciliationStore recs = new();
        InMemoryBankAdjustmentStore adjs = new();
        FakePostingLedger ledger = new();
        return (new AdjustmentService(recs, adjs, ledger), recs, adjs, ledger);
    }

    [Fact]
    public async Task Recording_a_charge_posts_a_pending_entry_dr_offset_cr_cash_and_stores_a_doc()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, InMemoryBankAdjustmentStore adjs, FakePostingLedger ledger) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);

        BankAdjustment adj = await svc.RecordAdjustmentAsync(clientId, rec.Id,
            new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, "bank fee"));

        Assert.NotNull(ledger.Posted);
        Assert.Contains(ledger.Posted!.Lines, l => l.AccountId == Offset && l.Direction == "Debit" && l.Amount == 5m);
        Assert.Contains(ledger.Posted!.Lines, l => l.AccountId == Cash && l.Direction == "Credit" && l.Amount == 5m);
        Assert.Equal(StmtDate, ledger.Posted!.EffectiveDate);       // defaulted to the statement date
        Assert.NotNull(await adjs.GetAsync(clientId, adj.Id));        // doc stored
        Assert.Equal(BankAdjustmentStatus.Posted, adj.Status);
    }

    [Fact]
    public async Task Recording_against_a_completed_reconciliation_is_rejected()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, _) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        await recs.SaveAsync(clientId, rec with { Status = ReconciliationStatus.Completed });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, null)));
    }

    [Fact]
    public async Task A_non_positive_amount_is_rejected()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, _) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 0m, AdjustmentKind.Charge, null, null)));
    }

    [Fact]
    public async Task Voiding_a_pending_adjustment_withdraws_the_entry_and_marks_the_doc_void()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, FakePostingLedger ledger) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        ledger.EntryPosting = "PendingApproval";
        BankAdjustment adj = await svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, null));

        BankAdjustment voided = await svc.VoidAdjustmentAsync(clientId, adj.Id);

        Assert.True(ledger.Voided);
        Assert.False(ledger.Reversed);
        Assert.Equal(BankAdjustmentStatus.Void, voided.Status);
    }

    [Fact]
    public async Task Voiding_an_approved_adjustment_reverses_the_entry()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, FakePostingLedger ledger) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        ledger.EntryPosting = "Posted";   // simulate the entry already approved
        BankAdjustment adj = await svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, null));

        await svc.VoidAdjustmentAsync(clientId, adj.Id);

        Assert.True(ledger.Reversed);
        Assert.False(ledger.Voided);
    }

    [Fact]
    public async Task List_returns_the_reconciliations_adjustments()
    {
        Guid clientId = Guid.NewGuid();
        (AdjustmentService svc, InMemoryReconciliationStore recs, _, _) = Build();
        Reconciliation rec = await recs.CreateAsync(clientId, Cash, Guid.NewGuid(), StmtDate);
        await svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 5m, AdjustmentKind.Charge, null, null));
        await svc.RecordAdjustmentAsync(clientId, rec.Id, new RecordAdjustmentInput(Offset, 3m, AdjustmentKind.Credit, null, null));

        IReadOnlyList<BankAdjustment> list = await svc.ListAdjustmentsAsync(clientId, rec.Id);
        Assert.Equal(2, list.Count);
    }
}
```

- [ ] **Step 4: Run the service + composer tests, verify PASS**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~AdjustmentServiceTests|FullyQualifiedName~AdjustmentPostingTests" --nologo`
Expected: all PASS (4 composer + 6 service = 10).

- [ ] **Step 5: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AdjustmentService.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AdjustmentServiceTests.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): AdjustmentService + service tests

Record (post PendingApproval via module identity) and void (reverse if posted,
withdraw if pending) bank adjustments; list per reconciliation. Mirrors the
Cash service lifecycle. Tested against in-memory stores + a fake posting ledger.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Api — credentialed posting client + relay, endpoints, registration

**Files:**
- Create: `…/Accounting101.Banking.Reconciliation.Api/HttpLedgerClient.cs`, `AdjustmentRequests.cs`
- Modify: `…/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs` (add the 4 adjustment routes + handlers), `ReconciliationServiceExtensions.cs` (registration + manifest)

**Interfaces:**
- Consumes: `AdjustmentService`, `RecordAdjustmentInput`, `ILedgerClient`, `LedgerClientException`, `IBankAdjustmentStore`, `DocumentBankAdjustmentStore` (Tasks 1-3).
- Produces: `AddReconciliation` (updated) — host wiring unchanged (routes ride the already-mapped `MapReconciliationEndpoints`).

- [ ] **Step 1: Create the credentialed posting client (mirror Payables' `HttpLedgerClient`, no Approve)**

`HttpLedgerClient.cs`:
```csharp
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Accounting101.Banking.Reconciliation;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>The module's POSTING ledger client. Forwards the caller's bearer; on PostAsync also attaches the
/// module credential (X-Module-Key/Secret) so the engine authorizes the module post and stamps
/// ViaModule="reconciliation". Reverse/Void/GetEntriesBySourceRef forward the bearer only. Non-success
/// responses throw a typed LedgerClientException so the module relays the engine's real status, not a 500.</summary>
public sealed class HttpLedgerClient(
    HttpClient http,
    IHttpContextAccessor context,
    [FromKeyedServices("reconciliation")] ModuleCredential credential) : ILedgerClient
{
    public async Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries");
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<PostEntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/reverse");
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage message = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/{entryId}/void");
        message.Content = JsonContent.Create(request);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Get, $"clients/{clientId}/entries?sourceRef={sourceRef}");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<List<EntryResponse>>(cancellationToken))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new LedgerClientException((int)response.StatusCode, ReasonFrom(body, response));
    }

    private static string ReasonFrom(string body, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
                    {
                        StringBuilder sb = new();
                        foreach (JsonProperty prop in errors.EnumerateObject())
                        {
                            if (sb.Length > 0) sb.Append("; ");
                            sb.Append(prop.Name).Append(": ");
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                                sb.Append(string.Join(", ", prop.Value.EnumerateArray().Select(m => m.GetString() ?? string.Empty)));
                            else
                                sb.Append(prop.Value.GetRawText().Trim('"'));
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }
                    if (root.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String && detail.GetString() is { Length: > 0 } text)
                        return text;
                }
            }
            catch (JsonException) { /* not JSON — relay the raw body */ }
            return body.Trim();
        }
        return response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
    }

    private HttpRequestMessage Forwarded(HttpMethod method, string uri)
    {
        HttpRequestMessage request = new(method, uri);
        string? authorization = context.HttpContext?.Request.Headers.Authorization;
        if (!string.IsNullOrEmpty(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }
}
```

- [ ] **Step 2: Create the request DTOs**

`AdjustmentRequests.cs`:
```csharp
using Accounting101.Banking.Reconciliation;

namespace Accounting101.Banking.Reconciliation.Api;

public sealed record RecordAdjustmentRequest(Guid OffsetAccountId, decimal Amount, AdjustmentKind Kind, DateOnly? Date, string? Memo);

public sealed record VoidReasonRequest(string? Reason);
```

- [ ] **Step 3: Add the adjustment endpoints**

In `ReconciliationEndpoints.cs`, register the routes inside `MapReconciliationEndpoints` (after the existing `/auto-match` line):
```csharp
        clients.MapPost("/reconciliations/{id:guid}/adjustments", RecordAdjustment);
        clients.MapGet("/reconciliations/{id:guid}/adjustments", ListAdjustments);
        clients.MapGet("/reconciliations/{id:guid}/adjustments/{adjId:guid}", GetAdjustment);
        clients.MapPost("/reconciliations/{id:guid}/adjustments/{adjId:guid}/void", VoidAdjustment);
```
and add the handlers (after the `AutoMatch` handler):
```csharp
    private static async Task<IResult> RecordAdjustment(
        Guid clientId, Guid id, RecordAdjustmentRequest request, AdjustmentService service, CancellationToken ct)
    {
        try
        {
            BankAdjustment adjustment = await service.RecordAdjustmentAsync(clientId, id,
                new RecordAdjustmentInput(request.OffsetAccountId, request.Amount, request.Kind, request.Date, request.Memo), ct);
            return Results.Created($"/clients/{clientId}/reconciliations/{id}/adjustments/{adjustment.Id}", adjustment);
        }
        catch (ArgumentException ex) // amount <= 0, offset == cash
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (LedgerClientException ex) // engine rejected the post (closed period, unknown account)
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
        catch (InvalidOperationException ex) // reconciliation not found / completed
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ListAdjustments(Guid clientId, Guid id, AdjustmentService service, CancellationToken ct) =>
        Results.Ok(await service.ListAdjustmentsAsync(clientId, id, ct));

    private static async Task<IResult> GetAdjustment(Guid clientId, Guid id, Guid adjId, AdjustmentService service, CancellationToken ct)
    {
        BankAdjustment? adjustment = await service.GetAdjustmentAsync(clientId, adjId, ct);
        return adjustment is null ? Results.NotFound() : Results.Ok(adjustment);
    }

    private static async Task<IResult> VoidAdjustment(
        Guid clientId, Guid id, Guid adjId, VoidReasonRequest? request, AdjustmentService service, CancellationToken ct)
    {
        try
        {
            BankAdjustment voided = await service.VoidAdjustmentAsync(clientId, adjId, request?.Reason, ct);
            return Results.Ok(voided);
        }
        catch (LedgerClientException ex)
        {
            return Results.Problem(ex.Reason, statusCode: ex.StatusCode);
        }
        catch (InvalidOperationException ex) // not found, already void, or no entry
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
```
Add `using Accounting101.Banking.Reconciliation;` at the top of the file if not already present (the handlers reference `BankAdjustment`, `AdjustmentService`, `RecordAdjustmentInput`, `LedgerClientException`).

- [ ] **Step 4: Update the registration**

In `ReconciliationServiceExtensions.cs`, inside the `AddModule(...)` manifest lambda add the adjustments collection alongside the existing two:
```csharp
            manifest.Evidentiary("bank-adjustments");
```
and after the existing store/service/client registrations add:
```csharp
        services.AddScoped<IBankAdjustmentStore>(sp => new DocumentBankAdjustmentStore(sp.GetRequiredKeyedService<IDocumentStore>("reconciliation")));
        services.AddScoped<AdjustmentService>();

        // Credentialed posting client (Slice 3) — distinct named client from the Slice 1 read-only reader.
        services.AddHttpClient("ReconciliationPostingClient", client =>
                client.BaseAddress = new Uri(configuration["Engine:BaseAddress"] ?? "http://localhost"))
            .AddTypedClient<ILedgerClient, HttpLedgerClient>();
```
(The `ILedgerClient`/`HttpLedgerClient` here are the Slice 3 types — `Accounting101.Banking.Reconciliation.ILedgerClient` and `Accounting101.Banking.Reconciliation.Api.HttpLedgerClient`. Ensure the file's usings cover `Accounting101.Banking.Reconciliation`. The `ModuleCredential` keyed `"reconciliation"` already exists from `AddModule`.)

- [ ] **Step 5: Build the host, verify it composes**

Run: `dotnet build Accounting101.Host/Accounting101.Host.csproj --nologo`
Expected: Build succeeded (the module registers; both named clients + the credential resolve).

- [ ] **Step 6: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/HttpLedgerClient.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/AdjustmentRequests.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationServiceExtensions.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): adjustment endpoints + credentialed posting client

Credentialed HttpLedgerClient (ViaModule=reconciliation) with LedgerClientException
relay; adjustment endpoints (record/list/get/void) on the existing reconciliation
group; AddReconciliation registers the bank-adjustments collection, the store,
AdjustmentService, and the posting client.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: E2E — record → approve → clear → complete, + void

**Files:**
- Modify: `…/Accounting101.Banking.Reconciliation.Tests/ReconciliationHostFixture.cs` (repoint the new posting client)
- Modify: `…/Accounting101.Banking.Reconciliation.Tests/ReconciliationE2eTests.cs` (append the adjustment E2E facts)

**Interfaces:**
- Consumes: the full module + host (Tasks 1-4); the Cash module + engine approve endpoint.

- [ ] **Step 1: Repoint the posting client in the fixture**

In `ReconciliationHostFixture.cs`, find the existing line that repoints the read-only client inside `ConfigureTestServices` — it looks like:
```csharp
services.AddHttpClient("ReconciliationLedgerClient", c => c.BaseAddress = new Uri("http://localhost"))
        .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
```
Add an identical repoint for the posting client immediately after it:
```csharp
services.AddHttpClient("ReconciliationPostingClient", c => c.BaseAddress = new Uri("http://localhost"))
        .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
```
(Match the exact style of the existing repoint in this fixture — same `Server.CreateHandler()` handler — so the adjustment POST loops back to the test server instead of a dead socket. If the existing repoint uses a different shape, mirror that shape exactly.)

- [ ] **Step 2: Append the adjustment E2E facts**

Append these `[Fact]`s to `ReconciliationE2eTests.cs` (inside the class — they reuse `SetUpChartAsync`, `ApproveBySourceRefAsync`, the fixture, and the imports already at the top; `HttpStatusCode` and `Accounting101.Ledger.Contracts` are already imported). The offset account is `fixture.InterestExpenseAccountId` (an expense account already in the chart):
```csharp
    [Fact]
    public async Task A_fee_adjustment_posts_pending_then_clears_the_residual_after_approval()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly date = new(2026, 1, 20);
        DateOnly stmtDate = new(2026, 1, 31);

        // A real deposit (Dr Cash 100), approved → posted. Book cash = 100.
        CashDeposit dep = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits",
                new RecordCashDepositRequest([new CashLineRequest(fixture.MembersCapitalAccountId, 100m)], date, "DEP", null)))
            .Content.ReadFromJsonAsync<CashDeposit>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, dep.Id);

        // Statement foots WITH a $5 bank fee the books lack: 0 + 100 − 5 = 95.
        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, 95m,
                    [new BankStatementLineRequest(date, 100m, "deposit", null), new BankStatementLineRequest(stmtDate, -5m, "service fee", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        // Clear only the deposit → a −5 fee residual remains.
        ReconciliationWorksheet beforeAdj = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Guid depEntryId = beforeAdj.Entries.Single().EntryId;
        await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/clear", new ClearRequest([depEntryId]));
        ReconciliationWorksheet residual = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Assert.Equal(-5m, residual.ReconciledDifference);
        Assert.False(residual.Balanced);

        // Record a Charge adjustment (Dr InterestExpense / Cr Cash 5) → 201; its entry is PendingApproval, ViaModule=reconciliation.
        BankAdjustment adj = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments",
                new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 5m, AdjustmentKind.Charge, null, "service fee")))
            .Content.ReadFromJsonAsync<BankAdjustment>())!;
        EntryResponse[] adjEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>($"/clients/{clientId}/entries?sourceRef={adj.Id}"))!;
        Assert.Single(adjEntries);
        Assert.Equal("PendingApproval", adjEntries[0].Posting);
        Assert.Equal("reconciliation", adjEntries[0].ViaModule);

        // Approve it (distinct Approver — maker-checker), then it becomes an eligible cash entry.
        await ApproveBySourceRefAsync(clerk, approver, clientId, adj.Id);

        ReconciliationWorksheet afterApprove = (await clerk.GetFromJsonAsync<ReconciliationWorksheet>($"/clients/{clientId}/reconciliations/{rec.Id}"))!;
        Guid adjEntryId = afterApprove.Entries.Single(e => !e.Cleared).EntryId;
        ReconciliationWorksheet balanced = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/clear",
                new ClearRequest([adjEntryId]))).Content.ReadFromJsonAsync<ReconciliationWorksheet>())!;
        Assert.Equal(0m, balanced.ReconciledDifference);
        Assert.True(balanced.Balanced);

        Reconciliation done = (await (await clerk.PostAsync($"/clients/{clientId}/reconciliations/{rec.Id}/complete", null))
            .Content.ReadFromJsonAsync<Reconciliation>())!;
        Assert.Equal(ReconciliationStatus.Completed, done.Status);
    }

    [Fact]
    public async Task A_pending_adjustment_can_be_voided()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly stmtDate = new(2026, 1, 31);

        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, -5m,
                    [new BankStatementLineRequest(stmtDate, -5m, "service fee", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        BankAdjustment adj = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments",
                new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 5m, AdjustmentKind.Charge, null, null)))
            .Content.ReadFromJsonAsync<BankAdjustment>())!;

        // Void the still-pending adjustment. The void calls the engine's entry-void, which requires Void
        // permission — drive it with the Approver (the SoD role that carries approve/void), not the Clerk.
        HttpResponseMessage voidResp = await approver.PostAsJsonAsync(
            $"/clients/{clientId}/reconciliations/{rec.Id}/adjustments/{adj.Id}/void", new VoidReasonRequest("recorded in error"));
        Assert.Equal(HttpStatusCode.OK, voidResp.StatusCode);
        BankAdjustment voided = (await voidResp.Content.ReadFromJsonAsync<BankAdjustment>())!;
        Assert.Equal(BankAdjustmentStatus.Void, voided.Status);
    }

    [Fact]
    public async Task An_adjustment_with_a_non_positive_amount_is_rejected_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        DateOnly stmtDate = new(2026, 1, 31);

        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, stmtDate, 0m, 10m,
                    [new BankStatementLineRequest(stmtDate, 10m, "deposit", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
        Reconciliation rec = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id))).Content.ReadFromJsonAsync<Reconciliation>())!;

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments",
            new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 0m, AdjustmentKind.Charge, null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
```

> Implementer note: these are characterization-style E2E for the new behavior — expected to pass. If the Approver lacks Void permission and the void returns 403 (or the adjustment post is refused for the Clerk), that is a genuine FINDING about the role model — STOP and report DONE_WITH_CONCERNS with the exact status and which role, rather than switching roles blindly. Likewise if `ViaModule` is not `"reconciliation"` or the residual math differs, report observed-vs-expected.

- [ ] **Step 3: Run the full Reconciliation test project**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --nologo`
Expected: all PASS (17 existing + 4 composer + 6 service + 3 E2E = 30).

- [ ] **Step 4: Build the whole solution — confirm no regressions**

Run: `dotnet build Accounting101.slnx --nologo`
Expected: Build succeeded (only pre-existing NU19xx transitive warnings).

- [ ] **Step 5: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/ReconciliationHostFixture.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/ReconciliationE2eTests.cs
git commit -m "$(cat <<'EOF'
test(reconciliation): adjustment E2E — record, approve, clear, complete; void

Posts a real deposit, records a footing statement with a bank fee the books
lack, records a Charge adjustment (PendingApproval, ViaModule=reconciliation),
approves it via the engine (distinct Approver), then clears it to balance and
complete. Plus: void a pending adjustment; reject a non-positive amount (422).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- Posting seam (full ILedgerClient) + credentialed client + relay → Tasks 1 (interface + exception) + 4 (HttpLedgerClient). ✓
- Pure composer (Charge/Credit → right Dr/Cr, EntryIdentity, sourceType) → Task 1. ✓
- Evidentiary doc + store (ADJ-, list by reconciliation) → Tasks 1 (interface) + 2 (store). ✓
- AdjustmentService (record posts PendingApproval; void reverse/withdraw; get/list) → Task 3. ✓
- Endpoints (record/list/get/void) + error mapping (422/409/relay/404) → Task 4. ✓
- Registration (manifest bank-adjustments, store, service, posting client) → Task 4. ✓
- Maker-checker reuses engine approve; residual clears after approval → Task 5 E2E. ✓
- Posting failures relay as 4xx → Task 4 (relay) + Task 1 (exception); exercised by the relay path. ✓

**2. Placeholder scan:** No TBD/TODO; full code for every file; commands explicit. Two "mirror the existing line/file" instructions (Task 4 usings, Task 5 fixture repoint) point at concrete named artifacts in the same file/module, not guesses.

**3. Type consistency:** `BankAdjustment`/`BankAdjustmentBody`/`AdjustmentKind`/`BankAdjustmentStatus`/`IBankAdjustmentStore`/`ILedgerClient`/`LedgerClientException`/`AdjustmentPosting.Compose` defined in Task 1 and consumed unchanged in Tasks 2-5. `AdjustmentService`/`RecordAdjustmentInput` defined in Task 3, consumed in Task 4. `RecordAdjustmentRequest`/`VoidReasonRequest` defined in Task 4. Wire types (`PostEntryRequest`/`PostLineRequest`/`PostEntryResponse`/`EntryResponse`/`ReverseRequest`/`VoidRequest`/`EntryIdentity.ForSource`) and Slice 1 types (`Reconciliation`/`IReconciliationStore`/`ReconciliationWorksheet`/`ClearRequest`/`StartReconciliationRequest`/`RecordBankStatementRequest`/`BankStatementLineRequest`) match the quoted source. Cash E2E request types (`RecordCashDepositRequest`/`CashLineRequest`/`CashDeposit`) match the Cash module. The `FakePostingLedger.StubEntry` and the composer-test `EntryResponse` construction match the positional shape used by the existing `ReconciliationServiceTests.CashEntry`. ✓
