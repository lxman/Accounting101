# TOCTOU Status Codes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the retry-key TOCTOU fix a regression test that can actually fail, then apply the same fix to the twelve endpoints still carrying the broken shape.

**Architecture:** Every retry-key service decides "created or replayed?" from a pre-check read, which is time-of-check-to-time-of-use. The engine's insert-only `CreateWithIdAsync` is the real arbiter — atomic on the Mongo `_id`, exactly one concurrent caller gets `true`. Each store already computes that bool and throws it away; the fix is to return it and have the service trust it over its own pre-check. A test-only rendezvous primitive forces two callers into the TOCTOU window so the difference between the two shapes is observable.

**Tech Stack:** .NET 10, xUnit, in-memory fakes (no Mongo, no HTTP for the new tests).

## Global Constraints

- .NET 10; build with **0 new warnings**; TDD; commit per slice; **stage explicit file lists** (`git add <paths>`, never `git add -A` / `git add .`).
- `UI/Angular/src/app/core/api/environment.ts` carries a long-lived local dev-config edit that must stay uncommitted. It shows as ` M` in `git status`. **Never stage it.**
- No new npm or NuGet dependency.
- **No module gains a reference to another module; no module logic moves into the engine.** See "The constraint that shapes every task" below — this is not a style preference and a reviewer must not treat the resulting repetition as missed deduplication.
- Solution file is `Accounting101.slnx`. The full local solution including the private overlay is `Accounting101.Local.slnx` (gitignored).
- Pre-existing and out of scope: 4 NU1902/NU1903 advisories from `Accounting101.TestSupport` transitive deps; 13 NG04002 Angular console errors; one pre-existing Angular failure in `auth.interceptor.spec.ts`.
- Baseline before this plan: `dotnet test Accounting101.slnx` → **1494/1494**. `dotnet test Accounting101.Local.slnx` → **1571/1571**.

## The constraint that shapes every task

Twelve near-identical conversions land in twelve places. **Do not extract a shared helper, base class, or generic for them, and do not move any of it into `Accounting101.Ledger.Api`.**

Engine code ships to every deployment unconditionally; module code is gated by `installed ∩ entitled` (see `docs/superpowers/specs/2026-07-26-module-entitlement-alignment-design.md`). A generic "replay helper" hoisted into the engine would put paid-module behaviour into the ungated tier where it could be discovered and wired up without entitlement. The repetition is the correct outcome.

The same applies to the per-module test decorators: each module's test project gets its own, because a shared one would need a shared interface that does not and should not exist.

## What makes a race test valid

**A concurrency test that passes against the broken code is worse than no test** — it advertises protection it does not provide. That is exactly how the existing `Two_concurrent_posts_with_the_same_key_yield_one_document_and_one_entry` shipped: two real HTTP posts under `Task.WhenAll` usually serialize, so the second one's pre-check finds the document and returns 200, and the assertions pass even with the fix reverted.

Every race test in this plan must be watched failing against the un-converted service before its conversion counts as done. Each task's steps enforce that ordering.

## File Structure

**Created:**
- `Accounting101.TestSupport/RaceRendezvous.cs` — releases N participants once all have arrived. Test-only assembly, referenced by all nine module test projects, never referenced by the Host.
- One rendezvous decorator per module test project (6 files), each wrapping that module's own store fake and blocking in the probe method.
- One race-test file per module (6 files).

**Modified:** 7 port interfaces, 7 document stores, 6 services, 6 test fakes, 2 list methods (`includeVoided`), 1 ledger fake.

---

### Task 1: `RaceRendezvous` and the first race test

**Files:**
- Create: `Accounting101.TestSupport/RaceRendezvous.cs`
- Create: `Modules/Payables/Accounting101.Payables.Tests/RendezvousBillPaymentStore.cs`
- Create: `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRaceTests.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRetryKeyDepthTests.cs` (comment only, on `Two_concurrent_posts_with_the_same_key_yield_one_document_and_one_entry`)

**Interfaces:**
- Consumes: `BillPaymentService.RecordPaymentAsync(Guid clientId, BillPaymentCommand command, CancellationToken ct = default) → Task<(BillPayment Payment, bool Replayed)>`; `BillPaymentCommand(Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations, Guid? Id = null)`; `IBillPaymentStore` (already returns `(BillPayment Payment, bool Created)` from `RecordPaymentAsync` — this endpoint is the one already fixed).
- Produces: `Accounting101.TestSupport.RaceRendezvous(int participants)` with `Task ArriveAndWaitAsync(CancellationToken ct = default)`. **Tasks 2–8 all use this.**

- [ ] **Step 1: Write the rendezvous primitive**

Create `Accounting101.TestSupport/RaceRendezvous.cs`:

```csharp
namespace Accounting101.TestSupport;

/// <summary>
/// Releases every participant once the Nth arrives, so concurrent callers can be forced into a
/// specific interleaving. Used to drive two racers into the time-of-check-to-time-of-use window
/// between a service's existence probe and its atomic create — a window two real HTTP requests
/// cannot be relied on to enter, because they usually serialize.
///
/// The timeout is load-bearing: a test that declares more participants than actually arrive must
/// fail in seconds with a clear message rather than block the suite until the runner gives up.
/// </summary>
public sealed class RaceRendezvous(int participants, TimeSpan? timeout = null)
{
    private readonly TaskCompletionSource _allArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);
    private int _arrived;

    public async Task ArriveAndWaitAsync(CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _arrived) >= participants)
            _allArrived.TrySetResult();

        Task completed = await Task.WhenAny(_allArrived.Task, Task.Delay(_timeout, ct));
        if (completed != _allArrived.Task)
            throw new TimeoutException(
                $"RaceRendezvous timed out after {_timeout.TotalSeconds:0.#}s: expected {participants} " +
                $"participants, only {Volatile.Read(ref _arrived)} arrived.");
    }
}
```

- [ ] **Step 2: Write the rendezvous decorator**

Create `Modules/Payables/Accounting101.Payables.Tests/RendezvousBillPaymentStore.cs`:

```csharp
using Accounting101.TestSupport;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Wraps <see cref="InMemoryBillPaymentStore"/> and blocks in the PROBE (<see cref="GetPaymentAsync"/>)
/// until every racer has arrived. That forces both callers to observe "not found" before either reaches
/// the atomic create — the exact interleaving <c>BillPaymentService</c>'s pre-check is vulnerable to, and
/// the one a pair of real HTTP posts cannot be relied on to produce.
///
/// Every other member delegates untouched, so the create really is arbitrated by the inner fake's
/// TryAdd — the honest stand-in for the engine's insert-only CreateWithIdAsync.
/// </summary>
internal sealed class RendezvousBillPaymentStore(InMemoryBillPaymentStore inner, RaceRendezvous rendezvous)
    : IBillPaymentStore
{
    public async Task<BillPayment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        BillPayment? found = await inner.GetPaymentAsync(clientId, paymentId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(BillPayment Payment, bool Created)> RecordPaymentAsync(
        Guid clientId, Guid id, BillPaymentBody body, CancellationToken ct = default) =>
        inner.RecordPaymentAsync(clientId, id, body, ct);

    public Task<VendorCreditApplication> RecordCreditApplicationAsync(
        Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default) =>
        inner.RecordCreditApplicationAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid paymentId, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, paymentId, ct);

    public Task<IReadOnlyList<BillPayment>> GetPaymentsByVendorAsync(
        Guid clientId, Guid vendorId, CancellationToken ct = default) =>
        inner.GetPaymentsByVendorAsync(clientId, vendorId, ct);

    public Task<IReadOnlyList<VendorCreditApplication>> GetCreditApplicationsByVendorAsync(
        Guid clientId, Guid vendorId, CancellationToken ct = default) =>
        inner.GetCreditApplicationsByVendorAsync(clientId, vendorId, ct);

    public Task<VendorCreditApplication?> GetCreditApplicationAsync(
        Guid clientId, Guid creditApplicationId, CancellationToken ct = default) =>
        inner.GetCreditApplicationAsync(clientId, creditApplicationId, ct);
}
```

If the compiler reports an unimplemented member, `IBillPaymentStore` has gained a method since this plan was written — delegate it to `inner` the same way and note it in your report.

- [ ] **Step 3: Write the race test**

Create `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRaceTests.cs`. Model the harness on `BillPaymentServiceTests.SetupWithEnteredBillAsync` in the same project — it seeds the bill's own A/P credit through `FakeLedgerClient.SeedEntry`, without which the open-balance fold starts from zero:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;
using Accounting101.TestSupport;

namespace Accounting101.Payables.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// <c>BillPaymentService</c> reads the document before deciding created-vs-replayed, which is
/// time-of-check-to-time-of-use: two callers racing one key can both see "not found". The store's
/// atomic create is the real arbiter, and the service must trust it over its own pre-check.
///
/// WHY THIS TEST EXISTS AND WHAT MAKES IT VALID: revert
/// <c>replayed = !result.Created;</c> to <c>replayed = known is not null;</c> in
/// <c>BillPaymentService.RecordPaymentAsync</c> and both racers claim a create, so
/// <c>Assert.Single(..., r =&gt; !r.Replayed)</c> sees two and fails. Its sibling
/// <c>BillPaymentRetryKeyDepthTests.Two_concurrent_posts_with_the_same_key_yield_one_document_and_one_entry</c>
/// does NOT have that property — two real HTTP posts usually serialize, so the second one's pre-check
/// finds the document and returns 200 and that test passes even against the broken shape.
/// </summary>
public sealed class BillPaymentRaceTests
{
    private static readonly BillPaymentPostingAccounts PayAccounts = new()
    {
        PayableAccountId = Guid.NewGuid(), CashAccountId = Guid.NewGuid(), VendorCreditsAccountId = Guid.NewGuid(),
    };

    private static readonly Guid DummyExpenseAccountId = Guid.NewGuid();

    [Fact]
    public async Task Two_racers_past_the_probe_yield_exactly_one_create()
    {
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();

        InMemoryBillStore billStore = new();
        Bill draft = await billStore.CreateDraftAsync(clientId, new BillBody(
            vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 1000m, Guid.NewGuid())]));
        Bill bill = await billStore.PromoteDraftAsync(clientId, draft.Id);

        // Seed the bill's own A/P credit — the open-balance fold reads the ledger, and this harness
        // bypasses BillService, which is what posts that line in production.
        FakeLedgerClient ledger = new();
        ledger.SeedEntry(new PostEntryRequest(
            Id: null, EffectiveDate: bill.BillDate, Reference: bill.Number, Memo: null,
            Lines:
            [
                new PostLineRequest(DummyExpenseAccountId, "Debit", bill.Total),
                new PostLineRequest(PayAccounts.PayableAccountId, "Credit", bill.Total,
                    Dimensions: new Dictionary<string, Guid> { ["Vendor"] = vendorId, ["Bill"] = bill.Id }),
            ],
            SourceRef: bill.Id, SourceType: "Bill"));

        InMemoryBillPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousBillPaymentStore racingStore = new(inner, rendezvous);
        BillPaymentService service = new(
            racingStore, billStore,
            new FixedBillAccountsProvider(new BillPostingAccounts { PayableAccountId = PayAccounts.PayableAccountId }, PayAccounts),
            ledger);

        // 100 against a 1000 bill, deliberately non-binding: validation runs after the probe and before
        // the create and folds pending-inclusive, so a binding amount would hand the second racer a
        // spurious 422 for a defect this test is not about (retry-keys follow-up #3).
        Guid key = Guid.NewGuid();
        BillPaymentCommand command = new(vendorId, new DateOnly(2026, 3, 31), 100m, "check",
            [new Allocation(bill.Id, 100m)], key);

        Task<(BillPayment Payment, bool Replayed)> first = service.RecordPaymentAsync(clientId, command);
        Task<(BillPayment Payment, bool Replayed)> second = service.RecordPaymentAsync(clientId, command);
        (BillPayment Payment, bool Replayed)[] results = await Task.WhenAll(first, second);

        // THE assertion. Both racers were forced past the probe seeing "not found"; only the atomic
        // create can distinguish them, so exactly one may claim to have created.
        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);

        // Both racers get back the same persisted document — the loser gets the winner's, not its own body.
        Assert.Equal(key, results[0].Payment.Id);
        Assert.Equal(key, results[1].Payment.Id);
        Assert.Single(await inner.GetPaymentsByVendorAsync(clientId, vendorId));
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run:
```
dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillPaymentRaceTests"
```
Expected: PASS. This endpoint is already fixed, so green is correct here.

- [ ] **Step 5: Prove the test is a genuine discriminator**

This is the step that makes the test worth having. Temporarily edit `Modules/Payables/Accounting101.Payables/BillPaymentService.cs` — in `RecordPaymentAsync`, replace:

```csharp
            known = result.Payment;
            replayed = !result.Created;
```

with:

```csharp
            known = result.Payment;
```

Re-run the same filter. Expected: **FAIL** — `Assert.Single() Failure: The collection contained 2 matching items` on the `!r.Replayed` assertion, because both racers now claim a create.

Then **restore the two lines exactly** and re-run to confirm PASS. Record both outputs in your report; a race test nobody has watched fail is indistinguishable from one that cannot fail.

Verify the restore with `git diff Modules/Payables/Accounting101.Payables/BillPaymentService.cs` — expected: empty.

- [ ] **Step 6: Annotate the weaker sibling test**

In `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRetryKeyDepthTests.cs`, add this comment directly above the `[Fact]` on `Two_concurrent_posts_with_the_same_key_yield_one_document_and_one_entry`:

```csharp
    /// <summary>
    /// End-to-end proof that one document and one non-reversal entry survive two same-key posts, through
    /// real HTTP and real Mongo — coverage the service-level race test cannot provide.
    ///
    /// It does NOT pin the TOCTOU fix: these two posts usually serialize, and when they do the second
    /// one's pre-check finds the document and returns 200, so the status assertions below hold even if
    /// the fix is reverted. <see cref="BillPaymentRaceTests"/> is what forces the interleaving and fails
    /// when the fix is absent. Keep both.
    /// </summary>
```

Leave every assertion in that test unchanged.

- [ ] **Step 7: Run the module suite**

Run:
```
dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj
```
Expected: all pass, 0 new warnings.

- [ ] **Step 8: Commit**

```bash
git add Accounting101.TestSupport/RaceRendezvous.cs Modules/Payables/Accounting101.Payables.Tests/RendezvousBillPaymentStore.cs Modules/Payables/Accounting101.Payables.Tests/BillPaymentRaceTests.cs Modules/Payables/Accounting101.Payables.Tests/BillPaymentRetryKeyDepthTests.cs
git commit -m "test(payables): deterministic TOCTOU race test for the retry-key fix"
```

---

### Task 2: Payables — vendor credit application

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/PayablesPorts.cs:29`
- Modify: `Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs:33-44` (conversion) and `GetCreditApplicationsByVendorAsync` (`includeVoided`)
- Modify: `Modules/Payables/Accounting101.Payables/BillPaymentService.cs:86`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs` (`InMemoryBillPaymentStore.RecordCreditApplicationAsync`)
- Modify: `Modules/Payables/Accounting101.Payables.Tests/RendezvousBillPaymentStore.cs`
- Test: `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRaceTests.cs` (append)

**Interfaces:**
- Consumes: `RaceRendezvous(int participants)` with `Task ArriveAndWaitAsync(CancellationToken ct = default)` (Task 1); `RendezvousBillPaymentStore(InMemoryBillPaymentStore inner, RaceRendezvous rendezvous)` (Task 1).
- Produces: `IBillPaymentStore.RecordCreditApplicationAsync → Task<(VendorCreditApplication Application, bool Created)>`.

- [ ] **Step 1: Extend the decorator to block on the credit-application probe**

In `Modules/Payables/Accounting101.Payables.Tests/RendezvousBillPaymentStore.cs`, replace the delegating `GetCreditApplicationAsync` with a blocking one:

```csharp
    public async Task<VendorCreditApplication?> GetCreditApplicationAsync(
        Guid clientId, Guid creditApplicationId, CancellationToken ct = default)
    {
        VendorCreditApplication? found = await inner.GetCreditApplicationAsync(clientId, creditApplicationId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }
```

Both probe methods now block. Each test constructs its own `RaceRendezvous`, so they never interfere.

- [ ] **Step 2: Write the failing race test**

Append to `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRaceTests.cs`:

```csharp
    /// <summary>
    /// The same TOCTOU race on the credit-application endpoint. Valid for the same reason as its
    /// sibling above: with the service trusting its own pre-check, both racers report a create and
    /// the single-create assertion sees two.
    /// </summary>
    [Fact]
    public async Task Two_credit_application_racers_past_the_probe_yield_exactly_one_create()
    {
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();

        InMemoryBillStore billStore = new();
        Bill draft = await billStore.CreateDraftAsync(clientId, new BillBody(
            vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 1000m, Guid.NewGuid())]));
        Bill bill = await billStore.PromoteDraftAsync(clientId, draft.Id);

        FakeLedgerClient ledger = new();
        ledger.SeedEntry(new PostEntryRequest(
            Id: null, EffectiveDate: bill.BillDate, Reference: bill.Number, Memo: null,
            Lines:
            [
                new PostLineRequest(DummyExpenseAccountId, "Debit", bill.Total),
                new PostLineRequest(PayAccounts.PayableAccountId, "Credit", bill.Total,
                    Dimensions: new Dictionary<string, Guid> { ["Vendor"] = vendorId, ["Bill"] = bill.Id }),
            ],
            SourceRef: bill.Id, SourceType: "Bill"));

        // Seed available vendor credit well above what the racers apply, so the credit-balance check
        // (state-dependent, and run before the create) cannot be what fails.
        ledger.SeedEntry(new PostEntryRequest(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "seed-credit", Memo: null,
            Lines:
            [
                new PostLineRequest(DummyExpenseAccountId, "Debit", 500m),
                new PostLineRequest(PayAccounts.VendorCreditsAccountId, "Credit", 500m,
                    Dimensions: new Dictionary<string, Guid> { ["Vendor"] = vendorId }),
            ],
            SourceRef: Guid.NewGuid(), SourceType: "VendorCredit"));

        InMemoryBillPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousBillPaymentStore racingStore = new(inner, rendezvous);
        BillPaymentService service = new(
            racingStore, billStore,
            new FixedBillAccountsProvider(new BillPostingAccounts { PayableAccountId = PayAccounts.PayableAccountId }, PayAccounts),
            ledger);

        Guid key = Guid.NewGuid();
        VendorCreditApplicationCommand command = new(vendorId, new DateOnly(2026, 3, 31),
            [new Allocation(bill.Id, 100m)], key);

        Task<(VendorCreditApplication Application, bool Replayed)> first =
            service.RecordCreditApplicationAsync(clientId, command);
        Task<(VendorCreditApplication Application, bool Replayed)> second =
            service.RecordCreditApplicationAsync(clientId, command);
        (VendorCreditApplication Application, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Single(await inner.GetCreditApplicationsByVendorAsync(clientId, vendorId));
    }
```

If `VendorCreditApplicationCommand`'s positional shape differs, read it in `Modules/Payables/Accounting101.Payables/BillPaymentBody.cs` and adjust the construction only — the assertions stay.

- [ ] **Step 3: Run the test to verify it fails**

Run:
```
dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~Two_credit_application_racers"
```
Expected: **FAIL** — `Assert.Single() Failure: The collection contained 2 matching items`, because the un-converted service decides `replayed` from its pre-check and both racers saw "not found".

- [ ] **Step 4: Convert the port**

In `Modules/Payables/Accounting101.Payables/PayablesPorts.cs`, replace line 29:

```csharp
    Task<VendorCreditApplication> RecordCreditApplicationAsync(Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default);
```

with:

```csharp
    /// <summary>Records the credit application under <paramref name="id"/>. <c>Created</c> is <c>true</c>
    /// only for the caller that won the underlying atomic create; under two concurrent callers racing the
    /// SAME id exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document
    /// but must report its own call as a replay. A caller cannot infer this from an earlier existence
    /// check — that check is TOCTOU under concurrency.</summary>
    Task<(VendorCreditApplication Application, bool Created)> RecordCreditApplicationAsync(Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default);
```

- [ ] **Step 5: Convert the store**

In `Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs`, replace the body of `RecordCreditApplicationAsync` (lines 33-44) with:

```csharp
    public async Task<(VendorCreditApplication Application, bool Created)> RecordCreditApplicationAsync(Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Only the caller that actually created the document finalizes it. FinalizeAsync throws on a
        // document that is no longer Draft (a deliberately pinned invariant), and that exception type
        // lives in the engine's Api assembly where this module cannot catch it — so we must not race.
        // This also means a replay burns no sequence number.
        //
        // CreateWithIdAsync's bool is the ONLY reliable signal of who won a race on this id: two concurrent
        // callers can both see "not found" on an earlier existence check (that check is inherently TOCTOU),
        // but the engine's insert-only create is atomic, so exactly one caller gets true here.
        bool created = await documents.CreateWithIdAsync(clientId, VendorCreditApplications, id, body, Tags(body.VendorId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, VendorCreditApplications, id, ct);
        DocumentResult<VendorCreditApplicationBody>? result = await documents.GetAsync<VendorCreditApplicationBody>(clientId, VendorCreditApplications, id, ct);
        return (MapCredit(result!), created);
    }
```

- [ ] **Step 6: Convert the service**

In `Modules/Payables/Accounting101.Payables/BillPaymentService.cs`, replace line 86:

```csharp
            known = await payments.RecordCreditApplicationAsync(clientId, id, body, ct);
```

with:

```csharp
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's own atomic create is the actual
            // arbiter — only its result tells a caller whether IT created the document or lost the race
            // and is really replaying. Trust that over the pre-check for this response's status.
            (VendorCreditApplication Application, bool Created) result =
                await payments.RecordCreditApplicationAsync(clientId, id, body, ct);
            known = result.Application;
            replayed = !result.Created;
```

- [ ] **Step 7: Convert the fake**

In `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs`, replace `InMemoryBillPaymentStore.RecordCreditApplicationAsync` (around line 312) with:

```csharp
    public Task<(VendorCreditApplication Application, bool Created)> RecordCreditApplicationAsync(Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        VendorCreditApplication c = new()
        {
            Id = id, VendorId = body.VendorId, Date = body.Date, Voided = false,
        };
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _credits.TryAdd((clientId, id), c);
        VendorCreditApplication stored = created ? c : _credits[(clientId, id)];
        return Task.FromResult((stored, created));
    }
```

- [ ] **Step 8: Update the decorator's delegating signature**

In `Modules/Payables/Accounting101.Payables.Tests/RendezvousBillPaymentStore.cs`, replace the `RecordCreditApplicationAsync` delegate with:

```csharp
    public Task<(VendorCreditApplication Application, bool Created)> RecordCreditApplicationAsync(
        Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default) =>
        inner.RecordCreditApplicationAsync(clientId, id, body, ct);
```

- [ ] **Step 9: Fix the `includeVoided` asymmetry**

In `Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs`, find `GetCreditApplicationsByVendorAsync`. Its sibling `GetPaymentsByVendorAsync` — directly above it, with an explanatory comment — passes `includeVoided: true` to the underlying query; this one does not, so voided credit applications vanish from the list instead of appearing greyed. Add the same argument, matching the sibling's spelling exactly.

Both consumers already filter `!Voided` themselves, so nothing observable changes today — this stops it becoming live later.

- [ ] **Step 10: Add the `includeVoided` assertion**

Append to `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRaceTests.cs`:

```csharp
    /// <summary>A voided credit application stays in the vendor list (greyed by the consumer), rather
    /// than vanishing — matching GetPaymentsByVendorAsync, whose identical fix shipped with retry keys.</summary>
    [Fact]
    public async Task A_voided_credit_application_still_appears_in_the_vendor_list()
    {
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();
        InMemoryBillPaymentStore store = new();
        Guid id = Guid.NewGuid();

        await store.RecordCreditApplicationAsync(clientId, id,
            new VendorCreditApplicationBody(vendorId, new DateOnly(2026, 3, 1)));

        Assert.Single(await store.GetCreditApplicationsByVendorAsync(clientId, vendorId));
    }
```

If `VendorCreditApplicationBody`'s positional shape differs, read it in `Modules/Payables/Accounting101.Payables/BillPaymentBody.cs` and adjust the construction only.

**Note:** this asserts against the fake, which has no void concept, so it pins the list-shape contract rather than the store's `includeVoided` argument. The real store's behaviour is covered by the existing `DocumentBillStoreTests`-style integration tests; if you find an existing store-level test for `GetPaymentsByVendorAsync`'s voided behaviour, add the mirrored case there instead and say so in your report — that is strictly better.

- [ ] **Step 11: Run the module suite**

Run:
```
dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj
```
Expected: all pass including both race tests, 0 new warnings.

- [ ] **Step 12: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/PayablesPorts.cs Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs Modules/Payables/Accounting101.Payables/BillPaymentService.cs Modules/Payables/Accounting101.Payables.Tests/Fakes.cs Modules/Payables/Accounting101.Payables.Tests/RendezvousBillPaymentStore.cs Modules/Payables/Accounting101.Payables.Tests/BillPaymentRaceTests.cs
git commit -m "fix(payables): credit application trusts the atomic create for replay status"
```

---

### Task 3: Cash — disbursement and deposit

**Files:**
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/CashPorts.cs:9,20`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDisbursementStore.cs:15-24`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDepositStore.cs:15-24`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs:28` and `:81` (the two `known = await …RecordAsync(…)` lines)
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs` (both `RecordAsync` fakes)
- Create: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/RendezvousCashStores.cs`
- Create: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashRaceTests.cs`

**Interfaces:**
- Consumes: `RaceRendezvous(int participants)` with `Task ArriveAndWaitAsync(CancellationToken ct = default)` (Task 1). `CashService.RecordDisbursementAsync(Guid clientId, CashDisbursementBody body, Guid? retryKey = null, CancellationToken ct = default) → Task<(CashDisbursement, bool Replayed)>` and the matching `RecordDepositAsync`. Note these take a `Guid? retryKey` parameter rather than an `Id` on a command record.
- Produces: `ICashDisbursementStore.RecordAsync → Task<(CashDisbursement Disbursement, bool Created)>`; `ICashDepositStore.RecordAsync → Task<(CashDeposit Deposit, bool Created)>`.

- [ ] **Step 1: Write the rendezvous decorators**

Create `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/RendezvousCashStores.cs`. Each wraps the module's existing fake and blocks in the probe (`GetAsync`), delegating everything else:

```csharp
using Accounting101.TestSupport;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>
/// Blocks in the PROBE until every racer has arrived, forcing both callers to observe "not found"
/// before either reaches the atomic create — the interleaving CashService's pre-check is vulnerable
/// to, and the one real HTTP posts cannot be relied on to produce. Everything else delegates, so the
/// create is really arbitrated by the inner fake's TryAdd.
/// </summary>
internal sealed class RendezvousCashDisbursementStore(
    InMemoryCashDisbursementStore inner, RaceRendezvous rendezvous) : ICashDisbursementStore
{
    public async Task<CashDisbursement?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        CashDisbursement? found = await inner.GetAsync(clientId, id, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(CashDisbursement Disbursement, bool Created)> RecordAsync(
        Guid clientId, Guid id, CashDisbursementBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, id, ct);
}

internal sealed class RendezvousCashDepositStore(
    InMemoryCashDepositStore inner, RaceRendezvous rendezvous) : ICashDepositStore
{
    public async Task<CashDeposit?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        CashDeposit? found = await inner.GetAsync(clientId, id, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(CashDeposit Deposit, bool Created)> RecordAsync(
        Guid clientId, Guid id, CashDepositBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, id, ct);
}
```

Read `ICashDisbursementStore` / `ICashDepositStore` in `CashPorts.cs` and the fake class names in the module's `Fakes.cs`, and delegate every member each interface actually declares — the list above is what the plan observed; add any others verbatim.

- [ ] **Step 2: Write the failing race tests**

Create `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashRaceTests.cs`. Model the harness (client id, posting-accounts provider, `FakeLedgerClient`, body construction) on `CashRetryKeyTests` in the same project — it already builds a `CashService` with these fakes:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THESE VALID: revert the service's <c>replayed = !result.Created;</c> to
/// <c>replayed = known is not null;</c> and both racers claim a create, so the single-create
/// assertion sees two and fails. A concurrency test without that property is worse than none —
/// it advertises protection it does not provide.
/// </summary>
public sealed class CashRaceTests
{
    [Fact]
    public async Task Two_disbursement_racers_past_the_probe_yield_exactly_one_create()
    {
        // Build clientId, the posting-accounts provider and FakeLedgerClient exactly as
        // CashRetryKeyTests does, then substitute the racing store:
        InMemoryCashDisbursementStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousCashDisbursementStore racingStore = new(inner, rendezvous);
        // CashService service = new(racingStore, <deposit store>, <accounts provider>, <ledger>);

        Guid clientId = Guid.NewGuid();
        Guid retryKey = Guid.NewGuid();
        CashDisbursementBody body = /* the same body CashRetryKeyTests uses */;

        Task<(CashDisbursement Disbursement, bool Replayed)> first =
            service.RecordDisbursementAsync(clientId, body, retryKey);
        Task<(CashDisbursement Disbursement, bool Replayed)> second =
            service.RecordDisbursementAsync(clientId, body, retryKey);
        (CashDisbursement Disbursement, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Disbursement.Id);
        Assert.Equal(retryKey, results[1].Disbursement.Id);
    }

    [Fact]
    public async Task Two_deposit_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryCashDepositStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousCashDepositStore racingStore = new(inner, rendezvous);
        // CashService service = new(<disbursement store>, racingStore, <accounts provider>, <ledger>);

        Guid clientId = Guid.NewGuid();
        Guid retryKey = Guid.NewGuid();
        CashDepositBody body = /* the same body CashRetryKeyTests uses */;

        Task<(CashDeposit Deposit, bool Replayed)> first = service.RecordDepositAsync(clientId, body, retryKey);
        Task<(CashDeposit Deposit, bool Replayed)> second = service.RecordDepositAsync(clientId, body, retryKey);
        (CashDeposit Deposit, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Deposit.Id);
        Assert.Equal(retryKey, results[1].Deposit.Id);
    }
}
```

Replace the three commented placeholders and the two `/* … */` body constructions with the concrete setup copied from `CashRetryKeyTests` — the amounts and accounts do not matter to these assertions, only that the service constructs and the bodies are valid. Every assertion shown is final.

- [ ] **Step 3: Run the tests to verify they fail**

Run:
```
dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Accounting101.Banking.Cash.Tests.csproj --filter "FullyQualifiedName~CashRaceTests"
```
Expected: **both FAIL** — `Assert.Single() Failure: The collection contained 2 matching items`, because the un-converted service decides `replayed` from its pre-check.

- [ ] **Step 4: Convert both ports**

In `Modules/Banking/Cash/Accounting101.Banking.Cash/CashPorts.cs`, replace line 9:

```csharp
    Task<CashDisbursement> RecordAsync(Guid clientId, Guid id, CashDisbursementBody body, CancellationToken ct = default);
```

with:

```csharp
    /// <summary>Records the disbursement under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create; under two concurrent callers racing the SAME id
    /// exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document but must
    /// report its own call as a replay. A caller cannot infer this from an earlier existence check — that
    /// check is TOCTOU under concurrency.</summary>
    Task<(CashDisbursement Disbursement, bool Created)> RecordAsync(Guid clientId, Guid id, CashDisbursementBody body, CancellationToken ct = default);
```

and line 20:

```csharp
    Task<CashDeposit> RecordAsync(Guid clientId, Guid id, CashDepositBody body, CancellationToken ct = default);
```

with:

```csharp
    /// <summary>Records the deposit under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for the
    /// caller that won the underlying atomic create; under two concurrent callers racing the SAME id exactly
    /// one gets <c>true</c>, and the loser still gets back the winner's persisted document but must report
    /// its own call as a replay. A caller cannot infer this from an earlier existence check — that check is
    /// TOCTOU under concurrency.</summary>
    Task<(CashDeposit Deposit, bool Created)> RecordAsync(Guid clientId, Guid id, CashDepositBody body, CancellationToken ct = default);
```

- [ ] **Step 5: Convert both stores**

In `Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDisbursementStore.cs`, replace lines 15-24 with:

```csharp
    public async Task<(CashDisbursement Disbursement, bool Created)> RecordAsync(Guid clientId, Guid id, CashDisbursementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create then finalize to assign the sequence number — but only the caller that
        // actually created it finalizes, so a replay burns no number and cannot race a concurrent creator.
        //
        // That bool is also the ONLY reliable created-vs-replayed signal: two concurrent callers can both
        // see "not found" on an earlier existence check (inherently TOCTOU), but this create is atomic, so
        // exactly one gets true. The service trusts this over its own pre-check.
        bool created = await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<CashDisbursementBody>? result = await documents.GetAsync<CashDisbursementBody>(clientId, Collection, id, ct);
        return (Map(result!), created);
    }
```

In `Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDepositStore.cs`, replace lines 15-24 with the same shape:

```csharp
    public async Task<(CashDeposit Deposit, bool Created)> RecordAsync(Guid clientId, Guid id, CashDepositBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create then finalize to assign the sequence number — but only the caller that
        // actually created it finalizes, so a replay burns no number and cannot race a concurrent creator.
        //
        // That bool is also the ONLY reliable created-vs-replayed signal: two concurrent callers can both
        // see "not found" on an earlier existence check (inherently TOCTOU), but this create is atomic, so
        // exactly one gets true. The service trusts this over its own pre-check.
        bool created = await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<CashDepositBody>? result = await documents.GetAsync<CashDepositBody>(clientId, Collection, id, ct);
        return (Map(result!), created);
    }
```

- [ ] **Step 6: Convert the service**

In `Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs`, in `RecordDisbursementAsync`, replace:

```csharp
        // 1. Persist the evidentiary document (finalized immediately — no draft phase).
        if (!replayed)
            known = await disbursements.RecordAsync(clientId, id, body, ct);
```

with:

```csharp
        // 1. Persist the evidentiary document (finalized immediately — no draft phase).
        if (!replayed)
        {
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (CashDisbursement Disbursement, bool Created) result = await disbursements.RecordAsync(clientId, id, body, ct);
            known = result.Disbursement;
            replayed = !result.Created;
        }
```

Apply the identical change in `RecordDepositAsync`, substituting `deposits`, `CashDeposit` and `Deposit`:

```csharp
        if (!replayed)
        {
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (CashDeposit Deposit, bool Created) result = await deposits.RecordAsync(clientId, id, body, ct);
            known = result.Deposit;
            replayed = !result.Created;
        }
```

The exact field name (`deposits`) may differ — use whatever the constructor parameter is called in that file.

- [ ] **Step 7: Convert both fakes**

In `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs`, `InMemoryCashDisbursementStore.RecordAsync` currently ends with an unconditional `_store[(clientId, doc.Id)] = doc;` (around line 88). Replace that assignment and the return with:

```csharp
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _store.TryAdd((clientId, doc.Id), doc);
        CashDisbursement stored = created ? doc : _store[(clientId, doc.Id)];
        return Task.FromResult((stored, created));
```

and change the method's signature to `Task<(CashDisbursement Disbursement, bool Created)>`. Keep every line that builds `doc` exactly as it is.

Apply the same change to `InMemoryCashDepositStore.RecordAsync` (around line 134), with `CashDeposit` and a `Task<(CashDeposit Deposit, bool Created)>` signature.

If `_store` is a `Dictionary` rather than a `ConcurrentDictionary`, change it to `ConcurrentDictionary` (`using System.Collections.Concurrent;`) — `TryAdd` under a genuine race needs it. Say which you found in your report.

- [ ] **Step 8: Run the tests to verify they pass**

Run:
```
dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Accounting101.Banking.Cash.Tests.csproj
```
Expected: all pass including both race tests, 0 new warnings.

- [ ] **Step 9: Commit**

```bash
git add Modules/Banking/Cash/Accounting101.Banking.Cash/CashPorts.cs Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDisbursementStore.cs Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDepositStore.cs Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/RendezvousCashStores.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashRaceTests.cs
git commit -m "fix(cash): disbursement and deposit trust the atomic create for replay status"
```

---

### Task 4: Payroll — run and remittance

**Files:**
- Modify: `Modules/Payroll/Accounting101.Payroll/PayrollPorts.cs:9,20`
- Modify: `Modules/Payroll/Accounting101.Payroll/DocumentPayrollRunStore.cs:15-24`
- Modify: `Modules/Payroll/Accounting101.Payroll/DocumentTaxRemittanceStore.cs:15-24`
- Modify: `Modules/Payroll/Accounting101.Payroll/PayrollService.cs` (the two `known = await …RecordAsync(…)` lines, near `:24` and `:77`)
- Modify: `Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs` (both `RecordAsync` fakes, around lines 91 and 137)
- Create: `Modules/Payroll/Accounting101.Payroll.Tests/RendezvousPayrollStores.cs`
- Create: `Modules/Payroll/Accounting101.Payroll.Tests/PayrollRaceTests.cs`

**Interfaces:**
- Consumes: `RaceRendezvous(int participants)` with `Task ArriveAndWaitAsync(CancellationToken ct = default)` (Task 1).
- Produces: `IPayrollRunStore.RecordAsync → Task<(PayrollRun Run, bool Created)>`; `ITaxRemittanceStore.RecordAsync → Task<(TaxRemittance Remittance, bool Created)>`.

- [ ] **Step 1: Write the rendezvous decorators**

Create `Modules/Payroll/Accounting101.Payroll.Tests/RendezvousPayrollStores.cs`:

```csharp
using Accounting101.TestSupport;

namespace Accounting101.Payroll.Tests;

/// <summary>
/// Blocks in the PROBE until every racer has arrived, forcing both callers to observe "not found"
/// before either reaches the atomic create — the interleaving PayrollService's pre-check is vulnerable
/// to. Everything else delegates, so the create is really arbitrated by the inner fake's TryAdd.
/// </summary>
internal sealed class RendezvousPayrollRunStore(
    InMemoryPayrollRunStore inner, RaceRendezvous rendezvous) : IPayrollRunStore
{
    public async Task<PayrollRun?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        PayrollRun? found = await inner.GetAsync(clientId, id, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(PayrollRun Run, bool Created)> RecordAsync(
        Guid clientId, Guid id, PayrollRunBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, id, ct);
}

internal sealed class RendezvousTaxRemittanceStore(
    InMemoryTaxRemittanceStore inner, RaceRendezvous rendezvous) : ITaxRemittanceStore
{
    public async Task<TaxRemittance?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        TaxRemittance? found = await inner.GetAsync(clientId, id, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(TaxRemittance Remittance, bool Created)> RecordAsync(
        Guid clientId, Guid id, TaxRemittanceBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default) =>
        inner.VoidAsync(clientId, id, ct);
}
```

Read `PayrollPorts.cs` and the module's `Fakes.cs` for the exact interface and fake class names, and delegate every member each interface actually declares.

- [ ] **Step 2: Write the failing race tests**

Create `Modules/Payroll/Accounting101.Payroll.Tests/PayrollRaceTests.cs`. Model the harness on `PayrollRetryKeyTests` in the same project:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Payroll.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THESE VALID: revert the service's <c>replayed = !result.Created;</c> to
/// <c>replayed = known is not null;</c> and both racers claim a create, so the single-create
/// assertion sees two and fails.
/// </summary>
public sealed class PayrollRaceTests
{
    [Fact]
    public async Task Two_run_racers_past_the_probe_yield_exactly_one_create()
    {
        // Build clientId, the accounts provider and FakeLedgerClient exactly as PayrollRetryKeyTests
        // does, then substitute the racing store into the PayrollService constructor.
        InMemoryPayrollRunStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPayrollRunStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid retryKey = Guid.NewGuid();
        PayrollRunBody body = /* the same body PayrollRetryKeyTests uses */;

        Task<(PayrollRun Run, bool Replayed)> first = service.RecordRunAsync(clientId, body, retryKey);
        Task<(PayrollRun Run, bool Replayed)> second = service.RecordRunAsync(clientId, body, retryKey);
        (PayrollRun Run, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Run.Id);
    }

    [Fact]
    public async Task Two_remittance_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryTaxRemittanceStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousTaxRemittanceStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid retryKey = Guid.NewGuid();
        TaxRemittanceBody body = /* the same body PayrollRetryKeyTests uses */;

        Task<(TaxRemittance Remittance, bool Replayed)> first = service.RecordRemittanceAsync(clientId, body, retryKey);
        Task<(TaxRemittance Remittance, bool Replayed)> second = service.RecordRemittanceAsync(clientId, body, retryKey);
        (TaxRemittance Remittance, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Remittance.Id);
    }
}
```

Replace the `/* … */` body constructions and the service construction with the concrete setup copied from `PayrollRetryKeyTests`. `RecordRunAsync` / `RecordRemittanceAsync` may take the retry key as a named or positional parameter — match that file. Every assertion shown is final.

- [ ] **Step 3: Run the tests to verify they fail**

Run:
```
dotnet test Modules/Payroll/Accounting101.Payroll.Tests/Accounting101.Payroll.Tests.csproj --filter "FullyQualifiedName~PayrollRaceTests"
```
Expected: **both FAIL** — `Assert.Single() Failure: The collection contained 2 matching items`.

- [ ] **Step 4: Convert both ports**

In `Modules/Payroll/Accounting101.Payroll/PayrollPorts.cs`, replace line 9 with:

```csharp
    /// <summary>Records the payroll run under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create; under two concurrent callers racing the SAME id
    /// exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document but must
    /// report its own call as a replay. A caller cannot infer this from an earlier existence check — that
    /// check is TOCTOU under concurrency.</summary>
    Task<(PayrollRun Run, bool Created)> RecordAsync(Guid clientId, Guid id, PayrollRunBody body, CancellationToken ct = default);
```

and line 20 with:

```csharp
    /// <summary>Records the tax remittance under <paramref name="id"/>. <c>Created</c> is <c>true</c> only
    /// for the caller that won the underlying atomic create; under two concurrent callers racing the SAME id
    /// exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document but must
    /// report its own call as a replay. A caller cannot infer this from an earlier existence check — that
    /// check is TOCTOU under concurrency.</summary>
    Task<(TaxRemittance Remittance, bool Created)> RecordAsync(Guid clientId, Guid id, TaxRemittanceBody body, CancellationToken ct = default);
```

- [ ] **Step 5: Convert both stores**

In `Modules/Payroll/Accounting101.Payroll/DocumentPayrollRunStore.cs`, replace lines 15-24 with:

```csharp
    public async Task<(PayrollRun Run, bool Created)> RecordAsync(Guid clientId, Guid id, PayrollRunBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create then finalize to assign the sequence number — but only the caller that
        // actually created it finalizes, so a replay burns no number and cannot race a concurrent creator.
        //
        // That bool is also the ONLY reliable created-vs-replayed signal: two concurrent callers can both
        // see "not found" on an earlier existence check (inherently TOCTOU), but this create is atomic, so
        // exactly one gets true. The service trusts this over its own pre-check.
        bool created = await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<PayrollRunBody>? result = await documents.GetAsync<PayrollRunBody>(clientId, Collection, id, ct);
        return (Map(result!), created);
    }
```

In `Modules/Payroll/Accounting101.Payroll/DocumentTaxRemittanceStore.cs`, replace lines 15-24 with:

```csharp
    public async Task<(TaxRemittance Remittance, bool Created)> RecordAsync(Guid clientId, Guid id, TaxRemittanceBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create then finalize to assign the sequence number — but only the caller that
        // actually created it finalizes, so a replay burns no number and cannot race a concurrent creator.
        //
        // That bool is also the ONLY reliable created-vs-replayed signal: two concurrent callers can both
        // see "not found" on an earlier existence check (inherently TOCTOU), but this create is atomic, so
        // exactly one gets true. The service trusts this over its own pre-check.
        bool created = await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<TaxRemittanceBody>? result = await documents.GetAsync<TaxRemittanceBody>(clientId, Collection, id, ct);
        return (Map(result!), created);
    }
```

- [ ] **Step 6: Convert the service**

In `Modules/Payroll/Accounting101.Payroll/PayrollService.cs`, in the run method replace the `if (!replayed) known = await runs.RecordAsync(clientId, id, body, ct);` shape with:

```csharp
        if (!replayed)
        {
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (PayrollRun Run, bool Created) result = await runs.RecordAsync(clientId, id, body, ct);
            known = result.Run;
            replayed = !result.Created;
        }
```

and in the remittance method:

```csharp
        if (!replayed)
        {
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (TaxRemittance Remittance, bool Created) result = await remittances.RecordAsync(clientId, id, body, ct);
            known = result.Remittance;
            replayed = !result.Created;
        }
```

Use whatever the constructor parameters are actually named in that file in place of `runs` / `remittances`. If either method wraps the record call in other statements, keep them and change only the record call and the two assignments.

- [ ] **Step 7: Convert both fakes**

In `Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs`, `InMemoryPayrollRunStore.RecordAsync` currently ends with `_store[(clientId, run.Id)] = run;` (around line 91). Replace that assignment and the return with:

```csharp
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _store.TryAdd((clientId, run.Id), run);
        PayrollRun stored = created ? run : _store[(clientId, run.Id)];
        return Task.FromResult((stored, created));
```

and change the signature to `Task<(PayrollRun Run, bool Created)>`. Keep every line that builds `run`.

Apply the same to `InMemoryTaxRemittanceStore.RecordAsync` (around line 137) with `TaxRemittance`, `remittance`, and `Task<(TaxRemittance Remittance, bool Created)>`.

If `_store` is a `Dictionary`, change it to `ConcurrentDictionary` (`using System.Collections.Concurrent;`).

- [ ] **Step 8: Run the tests to verify they pass**

Run:
```
dotnet test Modules/Payroll/Accounting101.Payroll.Tests/Accounting101.Payroll.Tests.csproj
```
Expected: all pass including both race tests, 0 new warnings.

- [ ] **Step 9: Commit**

```bash
git add Modules/Payroll/Accounting101.Payroll/PayrollPorts.cs Modules/Payroll/Accounting101.Payroll/DocumentPayrollRunStore.cs Modules/Payroll/Accounting101.Payroll/DocumentTaxRemittanceStore.cs Modules/Payroll/Accounting101.Payroll/PayrollService.cs Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs Modules/Payroll/Accounting101.Payroll.Tests/RendezvousPayrollStores.cs Modules/Payroll/Accounting101.Payroll.Tests/PayrollRaceTests.cs
git commit -m "fix(payroll): run and remittance trust the atomic create for replay status"
```

---

### Task 5: Reconciliation — bank adjustment

**Files:**
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AdjustmentPorts.cs:8`
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/DocumentBankAdjustmentStore.cs:12-21`
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AdjustmentService.cs` (the `known = await …RecordAsync(…)` line, near `:17`)
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs` (`InMemoryBankAdjustmentStore.RecordAsync`, around line 69)
- Create: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/RendezvousBankAdjustmentStore.cs`
- Create: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AdjustmentRaceTests.cs`

**Interfaces:**
- Consumes: `RaceRendezvous(int participants)` with `Task ArriveAndWaitAsync(CancellationToken ct = default)` (Task 1).
- Produces: `IBankAdjustmentStore.RecordAsync → Task<(BankAdjustment Adjustment, bool Created)>`.

- [ ] **Step 1: Write the rendezvous decorator**

Create `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/RendezvousBankAdjustmentStore.cs`:

```csharp
using Accounting101.TestSupport;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>
/// Blocks in the PROBE until every racer has arrived, forcing both callers to observe "not found"
/// before either reaches the atomic create — the interleaving AdjustmentService's pre-check is
/// vulnerable to. Everything else delegates, so the create is really arbitrated by the inner fake's
/// TryAdd.
/// </summary>
internal sealed class RendezvousBankAdjustmentStore(
    InMemoryBankAdjustmentStore inner, RaceRendezvous rendezvous) : IBankAdjustmentStore
{
    public async Task<BankAdjustment?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        BankAdjustment? found = await inner.GetAsync(clientId, id, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(BankAdjustment Adjustment, bool Created)> RecordAsync(
        Guid clientId, Guid id, BankAdjustmentBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);

    public Task<IReadOnlyList<BankAdjustment>> GetByReconciliationAsync(
        Guid clientId, Guid reconciliationId, CancellationToken ct = default) =>
        inner.GetByReconciliationAsync(clientId, reconciliationId, ct);
}
```

Read `AdjustmentPorts.cs` for the exact member list and delegate any others verbatim (there is a `VoidAsync`-shaped member in the fake around line 75 — if the interface declares it, delegate it).

- [ ] **Step 2: Write the failing race test**

Create `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AdjustmentRaceTests.cs`. Model the harness on `AdjustmentRetryKeyTests` in the same project:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THIS VALID: revert the service's <c>replayed = !result.Created;</c> to
/// <c>replayed = known is not null;</c> and both racers claim a create, so the single-create
/// assertion sees two and fails.
/// </summary>
public sealed class AdjustmentRaceTests
{
    [Fact]
    public async Task Two_adjustment_racers_past_the_probe_yield_exactly_one_create()
    {
        // Build clientId, reconciliationId, the accounts provider and FakeLedgerClient exactly as
        // AdjustmentRetryKeyTests does, then substitute the racing store into AdjustmentService.
        InMemoryBankAdjustmentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousBankAdjustmentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid retryKey = Guid.NewGuid();
        BankAdjustmentBody body = /* the same body AdjustmentRetryKeyTests uses */;

        Task<(BankAdjustment Adjustment, bool Replayed)> first =
            service.RecordAdjustmentAsync(clientId, reconciliationId, body, retryKey);
        Task<(BankAdjustment Adjustment, bool Replayed)> second =
            service.RecordAdjustmentAsync(clientId, reconciliationId, body, retryKey);
        (BankAdjustment Adjustment, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(retryKey, results[0].Adjustment.Id);
    }
}
```

Replace the `/* … */` construction and the service construction with the concrete setup from `AdjustmentRetryKeyTests`; match its actual `RecordAdjustmentAsync` parameter order. Every assertion shown is final.

- [ ] **Step 3: Run the test to verify it fails**

Run:
```
dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --filter "FullyQualifiedName~AdjustmentRaceTests"
```
Expected: **FAIL** — `Assert.Single() Failure: The collection contained 2 matching items`.

- [ ] **Step 4: Convert the port**

In `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AdjustmentPorts.cs`, replace line 8 with:

```csharp
    /// <summary>Records the adjustment under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create; under two concurrent callers racing the SAME id
    /// exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document but must
    /// report its own call as a replay. A caller cannot infer this from an earlier existence check — that
    /// check is TOCTOU under concurrency.</summary>
    Task<(BankAdjustment Adjustment, bool Created)> RecordAsync(Guid clientId, Guid id, BankAdjustmentBody body, CancellationToken ct = default);
```

- [ ] **Step 5: Convert the store**

In `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/DocumentBankAdjustmentStore.cs`, replace lines 12-21 with:

```csharp
    public async Task<(BankAdjustment Adjustment, bool Created)> RecordAsync(Guid clientId, Guid id, BankAdjustmentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create then finalize to assign the sequence number — but only the caller that
        // actually created it finalizes, so a replay burns no number and cannot race a concurrent creator.
        //
        // That bool is also the ONLY reliable created-vs-replayed signal: two concurrent callers can both
        // see "not found" on an earlier existence check (inherently TOCTOU), but this create is atomic, so
        // exactly one gets true. The service trusts this over its own pre-check.
        bool created = await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<BankAdjustmentBody>? result = await documents.GetAsync<BankAdjustmentBody>(clientId, Collection, id, ct);
        return (Map(result!), created);
    }
```

- [ ] **Step 6: Convert the service**

In `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AdjustmentService.cs`, replace the `if (!replayed) known = await adjustments.RecordAsync(clientId, id, body, ct);` shape with:

```csharp
        if (!replayed)
        {
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (BankAdjustment Adjustment, bool Created) result = await adjustments.RecordAsync(clientId, id, body, ct);
            known = result.Adjustment;
            replayed = !result.Created;
        }
```

Use whatever the constructor parameter is actually named in place of `adjustments`.

- [ ] **Step 7: Convert the fake**

In `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs`, `InMemoryBankAdjustmentStore.RecordAsync` currently ends with `_store[(clientId, a.Id)] = a;` (around line 69). Replace that assignment and the return with:

```csharp
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _store.TryAdd((clientId, a.Id), a);
        BankAdjustment stored = created ? a : _store[(clientId, a.Id)];
        return Task.FromResult((stored, created));
```

and change the signature to `Task<(BankAdjustment Adjustment, bool Created)>`. Keep every line that builds `a`. If `_store` is a `Dictionary`, change it to `ConcurrentDictionary` (`using System.Collections.Concurrent;`).

- [ ] **Step 8: Run the tests to verify they pass**

Run:
```
dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj
```
Expected: all pass including the race test, 0 new warnings.

- [ ] **Step 9: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AdjustmentPorts.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/DocumentBankAdjustmentStore.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/AdjustmentService.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/RendezvousBankAdjustmentStore.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AdjustmentRaceTests.cs
git commit -m "fix(reconciliation): bank adjustment trusts the atomic create for replay status"
```

---

### Task 6: Receivables — payment and credit application, plus the `includeVoided` fix

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs:7,8`
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs:16-27` and `:29-36`, plus `GetPaymentsByCustomerAsync`
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs:31` and `:281` regions
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs` (both record fakes)
- Create: `Modules/Receivables/Accounting101.Receivables.Tests/RendezvousPaymentStore.cs`
- Create: `Modules/Receivables/Accounting101.Receivables.Tests/PaymentRaceTests.cs`

**Interfaces:**
- Consumes: `RaceRendezvous(int participants)` with `Task ArriveAndWaitAsync(CancellationToken ct = default)` (Task 1).
- Produces: `IPaymentStore.RecordPaymentAsync → Task<(Payment Payment, bool Created)>`; `IPaymentStore.RecordCreditApplicationAsync → Task<(CreditApplication Application, bool Created)>`. **Task 7 extends the same interface and decorator** — leave both open for extension rather than sealing anything shut.

- [ ] **Step 1: Write the rendezvous decorator**

Create `Modules/Receivables/Accounting101.Receivables.Tests/RendezvousPaymentStore.cs`. `IPaymentStore` is wide (payments, credit applications, write-offs, credit notes, refunds — see `PaymentPorts.cs` lines 7-27), so delegate every member and block only in the probes this task and Task 7 need:

```csharp
using Accounting101.TestSupport;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Blocks in the PROBE methods until every racer has arrived, forcing both callers to observe
/// "not found" before either reaches the atomic create — the interleaving PaymentService's pre-check
/// is vulnerable to. Every other member delegates, so the create is really arbitrated by the inner
/// fake's TryAdd. Each test constructs its own <see cref="RaceRendezvous"/>, so the blocking probes
/// never interfere with one another.
/// </summary>
internal sealed class RendezvousPaymentStore(InMemoryPaymentStore inner, RaceRendezvous rendezvous)
    : IPaymentStore
{
    public async Task<Payment?> GetPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct = default)
    {
        Payment? found = await inner.GetPaymentAsync(clientId, paymentId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public async Task<CreditApplication?> GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct = default)
    {
        CreditApplication? found = await inner.GetCreditApplicationAsync(clientId, creditApplicationId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(Payment Payment, bool Created)> RecordPaymentAsync(
        Guid clientId, Guid id, PaymentBody body, CancellationToken ct = default) =>
        inner.RecordPaymentAsync(clientId, id, body, ct);

    public Task<(CreditApplication Application, bool Created)> RecordCreditApplicationAsync(
        Guid clientId, Guid id, CreditApplicationBody body, CancellationToken ct = default) =>
        inner.RecordCreditApplicationAsync(clientId, id, body, ct);

    // Every remaining IPaymentStore member delegates straight through, unblocked, for now.
    // Task 7 converts the write-off / credit-note / refund records and adds their blocking probes.
    public Task<IReadOnlyList<Payment>> GetPaymentsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetPaymentsByCustomerAsync(clientId, customerId, ct);

    public Task<IReadOnlyList<CreditApplication>> GetCreditApplicationsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetCreditApplicationsByCustomerAsync(clientId, customerId, ct);

    public Task<WriteOff> RecordWriteOffAsync(Guid clientId, Guid id, WriteOffBody body, CancellationToken ct = default) =>
        inner.RecordWriteOffAsync(clientId, id, body, ct);

    public Task<WriteOff?> GetWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default) =>
        inner.GetWriteOffAsync(clientId, writeOffId, ct);

    public Task<IReadOnlyList<WriteOff>> GetWriteOffsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetWriteOffsByCustomerAsync(clientId, customerId, ct);

    public Task<CreditNote> RecordCreditNoteAsync(Guid clientId, Guid id, CreditNoteBody body, CancellationToken ct = default) =>
        inner.RecordCreditNoteAsync(clientId, id, body, ct);

    public Task<CreditNote?> GetCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default) =>
        inner.GetCreditNoteAsync(clientId, creditNoteId, ct);

    public Task<IReadOnlyList<CreditNote>> GetCreditNotesByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetCreditNotesByCustomerAsync(clientId, customerId, ct);

    public Task<Refund> RecordRefundAsync(Guid clientId, Guid id, RefundBody body, CancellationToken ct = default) =>
        inner.RecordRefundAsync(clientId, id, body, ct);

    public Task<Refund?> GetRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default) =>
        inner.GetRefundAsync(clientId, refundId, ct);

    public Task<IReadOnlyList<Refund>> GetRefundsByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default) =>
        inner.GetRefundsByCustomerAsync(clientId, customerId, ct);
}
```

Add any member the compiler reports missing, delegating it the same way, and say so in your report.

- [ ] **Step 2: Write the failing race tests**

Create `Modules/Receivables/Accounting101.Receivables.Tests/PaymentRaceTests.cs`. Model the harness on `PaymentRetryKeyTests` in the same project:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;
using Accounting101.TestSupport;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THESE VALID: revert the service's <c>replayed = !result.Created;</c> to
/// <c>replayed = known is not null;</c> and both racers claim a create, so the single-create
/// assertion sees two and fails.
/// </summary>
public sealed class PaymentRaceTests
{
    [Fact]
    public async Task Two_payment_racers_past_the_probe_yield_exactly_one_create()
    {
        // Build clientId, customer, invoice, accounts provider and FakeLedgerClient exactly as
        // PaymentRetryKeyTests does, then substitute the racing store into PaymentService.
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid key = Guid.NewGuid();
        // Allocations must be NON-BINDING against the invoice's open balance — allocate a small
        // fraction of a large invoice. Validation runs after the probe and before the create and folds
        // pending-inclusive, so a binding amount would hand the second racer a spurious 422 for a
        // defect this test is not about (retry-keys follow-up #3).

        Task<(Payment Payment, bool Replayed)> first = service.RecordPaymentAsync(clientId, command);
        Task<(Payment Payment, bool Replayed)> second = service.RecordPaymentAsync(clientId, command);
        (Payment Payment, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].Payment.Id);
    }

    [Fact]
    public async Task Two_credit_application_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid key = Guid.NewGuid();
        // Same non-binding rule, and seed available customer credit well above what the racers apply.

        Task<(CreditApplication Application, bool Replayed)> first = service.RecordCreditApplicationAsync(clientId, command);
        Task<(CreditApplication Application, bool Replayed)> second = service.RecordCreditApplicationAsync(clientId, command);
        (CreditApplication Application, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].Application.Id);
    }
}
```

Fill in the service construction and the two `command` values from `PaymentRetryKeyTests`, honouring the non-binding rule in the comments. Every assertion shown is final.

- [ ] **Step 3: Run the tests to verify they fail**

Run:
```
dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~PaymentRaceTests"
```
Expected: **both FAIL** — `Assert.Single() Failure: The collection contained 2 matching items`.

- [ ] **Step 4: Convert both ports**

In `Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs`, replace lines 7 and 8 with:

```csharp
    /// <summary>Records the payment under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for the
    /// caller that won the underlying atomic create; under two concurrent callers racing the SAME id exactly
    /// one gets <c>true</c>, and the loser still gets back the winner's persisted document but must report
    /// its own call as a replay. A caller cannot infer this from an earlier existence check — that check is
    /// TOCTOU under concurrency.</summary>
    Task<(Payment Payment, bool Created)> RecordPaymentAsync(Guid clientId, Guid id, PaymentBody body, CancellationToken ct = default);

    /// <summary>Records the credit application under <paramref name="id"/>. <c>Created</c> is <c>true</c>
    /// only for the caller that won the underlying atomic create — see the remarks on
    /// <see cref="RecordPaymentAsync"/>.</summary>
    Task<(CreditApplication Application, bool Created)> RecordCreditApplicationAsync(Guid clientId, Guid id, CreditApplicationBody body, CancellationToken ct = default);
```

- [ ] **Step 5: Convert both store methods**

In `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs`, replace `RecordPaymentAsync` (lines 16-27) with:

```csharp
    public async Task<(Payment Payment, bool Created)> RecordPaymentAsync(Guid clientId, Guid id, PaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Only the caller that actually created the document finalizes it, so a replay burns no sequence
        // number and cannot race a concurrent creator.
        //
        // CreateWithIdAsync's bool is the ONLY reliable created-vs-replayed signal: two concurrent callers
        // can both see "not found" on an earlier existence check (inherently TOCTOU), but this create is
        // atomic, so exactly one gets true. The service trusts this over its own pre-check.
        bool created = await documents.CreateWithIdAsync(clientId, Payments, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Payments, id, ct);
        DocumentResult<PaymentBody>? result = await documents.GetAsync<PaymentBody>(clientId, Payments, id, ct);
        return (MapPayment(result!), created);
    }
```

and `RecordCreditApplicationAsync` (lines 29-36) with:

```csharp
    public async Task<(CreditApplication Application, bool Created)> RecordCreditApplicationAsync(Guid clientId, Guid id, CreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // See RecordPaymentAsync: the atomic create's bool is the only reliable created-vs-replayed signal.
        bool created = await documents.CreateWithIdAsync(clientId, CreditApplications, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, CreditApplications, id, ct);
        DocumentResult<CreditApplicationBody>? result = await documents.GetAsync<CreditApplicationBody>(clientId, CreditApplications, id, ct);
        return (MapCredit(result!), created);
    }
```

Preserve the finalize line exactly as the existing code writes it (line 24 / line 33) — the plan shows the shape, not necessarily the identical argument list.

- [ ] **Step 6: Convert the service**

In `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`, in the method whose `bool replayed` is at line 31, replace `known = await payments.RecordPaymentAsync(clientId, id, body, ct);` with:

```csharp
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (Payment Payment, bool Created) result = await payments.RecordPaymentAsync(clientId, id, body, ct);
            known = result.Payment;
            replayed = !result.Created;
```

and in the method whose `bool replayed` is at line 281, replace the credit-application record call with:

```csharp
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (CreditApplication Application, bool Created) result = await payments.RecordCreditApplicationAsync(clientId, id, body, ct);
            known = result.Application;
            replayed = !result.Created;
```

Use whatever the constructor parameter is actually named in place of `payments`.

- [ ] **Step 7: Convert both fakes**

In `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs`, find `InMemoryPaymentStore.RecordPaymentAsync`. It ends with an unconditional dictionary assignment; replace that assignment and the return with:

```csharp
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _payments.TryAdd((clientId, id), p);
        Payment stored = created ? p : _payments[(clientId, id)];
        return Task.FromResult((stored, created));
```

and change the signature to `Task<(Payment Payment, bool Created)>`. Use the actual field and local names from that file. Apply the same to `RecordCreditApplicationAsync` with `CreditApplication` and `Task<(CreditApplication Application, bool Created)>`.

If either backing field is a `Dictionary`, change it to `ConcurrentDictionary` (`using System.Collections.Concurrent;`).

- [ ] **Step 8: Fix the `includeVoided` asymmetry**

In `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs`, find `GetPaymentsByCustomerAsync`. It is the only list method in that file that does not pass `includeVoided: true` — credit applications, write-offs, credit notes and refunds all do. Add the argument, matching those siblings' spelling exactly, so voided payments appear greyed rather than vanishing.

Both consumers already filter `!Voided` themselves, so nothing observable changes today.

- [ ] **Step 9: Run the module suite**

Run:
```
dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj
```
Expected: all pass including both race tests, 0 new warnings. If the `includeVoided` change turns an existing list test red, that is a real finding — report it with the test name and output rather than reverting the fix.

- [ ] **Step 10: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs Modules/Receivables/Accounting101.Receivables/PaymentService.cs Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs Modules/Receivables/Accounting101.Receivables.Tests/RendezvousPaymentStore.cs Modules/Receivables/Accounting101.Receivables.Tests/PaymentRaceTests.cs
git commit -m "fix(receivables): payment and credit application trust the atomic create for replay status"
```

---

### Task 7: Receivables — write-off, credit note, refund

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs:15,20,25`
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs:67-75`, `:92-100`, `:117-125`
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs:346`, `:382`, `:417` regions
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs` (three record fakes)
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/RendezvousPaymentStore.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/PaymentRaceTests.cs` (append)

**Interfaces:**
- Consumes: `RaceRendezvous(int participants)` with `Task ArriveAndWaitAsync(CancellationToken ct = default)` (Task 1); `RendezvousPaymentStore(InMemoryPaymentStore inner, RaceRendezvous rendezvous)` (Task 6).
- Produces: `IPaymentStore.RecordWriteOffAsync → Task<(WriteOff WriteOff, bool Created)>`; `RecordCreditNoteAsync → Task<(CreditNote CreditNote, bool Created)>`; `RecordRefundAsync → Task<(Refund Refund, bool Created)>`.

- [ ] **Step 1: Make the three remaining probes block**

In `Modules/Receivables/Accounting101.Receivables.Tests/RendezvousPaymentStore.cs`, replace the three delegating probe methods with blocking ones:

```csharp
    public async Task<WriteOff?> GetWriteOffAsync(Guid clientId, Guid writeOffId, CancellationToken ct = default)
    {
        WriteOff? found = await inner.GetWriteOffAsync(clientId, writeOffId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public async Task<CreditNote?> GetCreditNoteAsync(Guid clientId, Guid creditNoteId, CancellationToken ct = default)
    {
        CreditNote? found = await inner.GetCreditNoteAsync(clientId, creditNoteId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public async Task<Refund?> GetRefundAsync(Guid clientId, Guid refundId, CancellationToken ct = default)
    {
        Refund? found = await inner.GetRefundAsync(clientId, refundId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }
```

and update the three record delegates to the new tuple signatures:

```csharp
    public Task<(WriteOff WriteOff, bool Created)> RecordWriteOffAsync(
        Guid clientId, Guid id, WriteOffBody body, CancellationToken ct = default) =>
        inner.RecordWriteOffAsync(clientId, id, body, ct);

    public Task<(CreditNote CreditNote, bool Created)> RecordCreditNoteAsync(
        Guid clientId, Guid id, CreditNoteBody body, CancellationToken ct = default) =>
        inner.RecordCreditNoteAsync(clientId, id, body, ct);

    public Task<(Refund Refund, bool Created)> RecordRefundAsync(
        Guid clientId, Guid id, RefundBody body, CancellationToken ct = default) =>
        inner.RecordRefundAsync(clientId, id, body, ct);
```

- [ ] **Step 2: Write the three failing race tests**

Append to `Modules/Receivables/Accounting101.Receivables.Tests/PaymentRaceTests.cs`. Model each harness on `DispositionRetryKeyTests` in the same project, which already exercises these three endpoints:

```csharp
    [Fact]
    public async Task Two_write_off_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);
        // Build the rest exactly as DispositionRetryKeyTests does, substituting racingStore.

        Guid clientId = Guid.NewGuid();
        Guid key = Guid.NewGuid();

        Task<(WriteOff WriteOff, bool Replayed)> first = service.RecordWriteOffAsync(clientId, command);
        Task<(WriteOff WriteOff, bool Replayed)> second = service.RecordWriteOffAsync(clientId, command);
        (WriteOff WriteOff, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].WriteOff.Id);
    }

    [Fact]
    public async Task Two_credit_note_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid key = Guid.NewGuid();

        Task<(CreditNote CreditNote, bool Replayed)> first = service.RecordCreditNoteAsync(clientId, command);
        Task<(CreditNote CreditNote, bool Replayed)> second = service.RecordCreditNoteAsync(clientId, command);
        (CreditNote CreditNote, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].CreditNote.Id);
    }

    [Fact]
    public async Task Two_refund_racers_past_the_probe_yield_exactly_one_create()
    {
        InMemoryPaymentStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousPaymentStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid key = Guid.NewGuid();

        Task<(Refund Refund, bool Replayed)> first = service.RecordRefundAsync(clientId, command);
        Task<(Refund Refund, bool Replayed)> second = service.RecordRefundAsync(clientId, command);
        (Refund Refund, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].Refund.Id);
    }
```

Fill in each service construction and `command` from `DispositionRetryKeyTests`, keeping amounts non-binding against the invoice's open balance. Every assertion shown is final.

- [ ] **Step 3: Run the tests to verify they fail**

Run:
```
dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter "FullyQualifiedName~PaymentRaceTests"
```
Expected: the three new tests **FAIL** — `Assert.Single() Failure: The collection contained 2 matching items`. The two from Task 6 still pass.

- [ ] **Step 4: Convert the three ports**

In `Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs`, replace lines 15, 20 and 25 with:

```csharp
    /// <summary>Records the write-off under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create — see the remarks on <see cref="RecordPaymentAsync"/>.</summary>
    Task<(WriteOff WriteOff, bool Created)> RecordWriteOffAsync(Guid clientId, Guid id, WriteOffBody body, CancellationToken ct = default);
```

```csharp
    /// <summary>Records the credit note under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create — see the remarks on <see cref="RecordPaymentAsync"/>.</summary>
    Task<(CreditNote CreditNote, bool Created)> RecordCreditNoteAsync(Guid clientId, Guid id, CreditNoteBody body, CancellationToken ct = default);
```

```csharp
    /// <summary>Records the refund under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for the
    /// caller that won the underlying atomic create — see the remarks on <see cref="RecordPaymentAsync"/>.</summary>
    Task<(Refund Refund, bool Created)> RecordRefundAsync(Guid clientId, Guid id, RefundBody body, CancellationToken ct = default);
```

- [ ] **Step 5: Convert the three store methods**

In `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs`, replace `RecordWriteOffAsync` (lines 67-75) with:

```csharp
    public async Task<(WriteOff WriteOff, bool Created)> RecordWriteOffAsync(Guid clientId, Guid id, WriteOffBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // See RecordPaymentAsync: the atomic create's bool is the only reliable created-vs-replayed signal.
        bool created = await documents.CreateWithIdAsync(clientId, WriteOffs, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, WriteOffs, id, ct);
        DocumentResult<WriteOffBody>? r = await documents.GetAsync<WriteOffBody>(clientId, WriteOffs, id, ct);
        return (MapWriteOff(r!), created);
    }
```

`RecordCreditNoteAsync` (lines 92-100) with:

```csharp
    public async Task<(CreditNote CreditNote, bool Created)> RecordCreditNoteAsync(Guid clientId, Guid id, CreditNoteBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // See RecordPaymentAsync: the atomic create's bool is the only reliable created-vs-replayed signal.
        bool created = await documents.CreateWithIdAsync(clientId, CreditNotes, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, CreditNotes, id, ct);
        DocumentResult<CreditNoteBody>? r = await documents.GetAsync<CreditNoteBody>(clientId, CreditNotes, id, ct);
        return (MapCreditNote(r!), created);
    }
```

and `RecordRefundAsync` (lines 117-125) with:

```csharp
    public async Task<(Refund Refund, bool Created)> RecordRefundAsync(Guid clientId, Guid id, RefundBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // See RecordPaymentAsync: the atomic create's bool is the only reliable created-vs-replayed signal.
        bool created = await documents.CreateWithIdAsync(clientId, Refunds, id, body, Tags(body.CustomerId), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Refunds, id, ct);
        DocumentResult<RefundBody>? r = await documents.GetAsync<RefundBody>(clientId, Refunds, id, ct);
        return (MapRefund(r!), created);
    }
```

Preserve each existing finalize line's exact argument list; the plan shows the shape.

- [ ] **Step 6: Convert the service**

In `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`, in the methods whose `bool replayed` are at lines 346, 382 and 417, replace each record call with its tuple form:

```csharp
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (WriteOff WriteOff, bool Created) result = await payments.RecordWriteOffAsync(clientId, id, body, ct);
            known = result.WriteOff;
            replayed = !result.Created;
```

```csharp
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (CreditNote CreditNote, bool Created) result = await payments.RecordCreditNoteAsync(clientId, id, body, ct);
            known = result.CreditNote;
            replayed = !result.Created;
```

```csharp
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            (Refund Refund, bool Created) result = await payments.RecordRefundAsync(clientId, id, body, ct);
            known = result.Refund;
            replayed = !result.Created;
```

Use whatever the constructor parameter is actually named in place of `payments`.

- [ ] **Step 7: Convert the three fakes**

In `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs`, for each of `InMemoryPaymentStore.RecordWriteOffAsync`, `RecordCreditNoteAsync` and `RecordRefundAsync`, replace the unconditional dictionary assignment and the return with the TryAdd shape, and change each signature to its tuple form:

```csharp
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _writeOffs.TryAdd((clientId, id), w);
        WriteOff stored = created ? w : _writeOffs[(clientId, id)];
        return Task.FromResult((stored, created));
```

```csharp
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _creditNotes.TryAdd((clientId, id), n);
        CreditNote stored = created ? n : _creditNotes[(clientId, id)];
        return Task.FromResult((stored, created));
```

```csharp
        // TryAdd, not an unconditional set: mirrors the real store's insert-only create, so a fake-backed
        // caller racing an id it doesn't yet know about gets an honest Created signal too.
        bool created = _refunds.TryAdd((clientId, id), f);
        Refund stored = created ? f : _refunds[(clientId, id)];
        return Task.FromResult((stored, created));
```

Use the actual field and local names from that file; if any backing field is a `Dictionary`, change it to `ConcurrentDictionary` (`using System.Collections.Concurrent;`).

- [ ] **Step 8: Run the module suite**

Run:
```
dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj
```
Expected: all pass including all five race tests, 0 new warnings.

- [ ] **Step 9: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables/PaymentPorts.cs Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs Modules/Receivables/Accounting101.Receivables/PaymentService.cs Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs Modules/Receivables/Accounting101.Receivables.Tests/RendezvousPaymentStore.cs Modules/Receivables/Accounting101.Receivables.Tests/PaymentRaceTests.cs
git commit -m "fix(receivables): write-off, credit note and refund trust the atomic create"
```

---

### Task 8: Inventory — movement, converted and half-fixed

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryPorts.cs:45`
- Modify: `Modules/Inventory/Accounting101.Inventory/DocumentStockMovementStore.cs:12-21`
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs` (the record call after `bool replayed` at `:47`, plus a scoping comment)
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs` (`RecordAsync`, around line 192)
- Create: `Modules/Inventory/Accounting101.Inventory.Tests/RendezvousStockMovementStore.cs`
- Create: `Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs`

**Interfaces:**
- Consumes: `RaceRendezvous(int participants)` with `Task ArriveAndWaitAsync(CancellationToken ct = default)` (Task 1).
- Produces: `IStockMovementStore.RecordAsync → Task<(StockMovement Movement, bool Created)>`.

**Read this before starting.** This conversion fixes Inventory only halfway, on purpose:

- **Receipt and overage Adjustment** compute `extendedCost` as `Round(qty × unitCost)` — pure in the request, no fold. The conversion fixes them completely.
- **Issue and shrinkage Adjustment** derive cost from a pending-inclusive weighted-average fold that the race winner has already moved. A loser recomputes against the moved fold and can get a ledger 422, trip block-negative (409), or trip the `extendedCost <= 0` guard — all for a movement that actually landed. `Created` cannot help, because **the cost is computed before the store call.** That is a separate spec.

So the race test below must use a **Receipt**. A test built on an Issue would fail for the fold reason, not the status-code reason, and would be pinning the wrong thing.

- [ ] **Step 1: Write the rendezvous decorator**

Create `Modules/Inventory/Accounting101.Inventory.Tests/RendezvousStockMovementStore.cs`:

```csharp
using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// Blocks in the PROBE until every racer has arrived, forcing both callers to observe "not found"
/// before either reaches the atomic create — the interleaving InventoryMovementService's pre-check is
/// vulnerable to. Everything else delegates, so the create is really arbitrated by the inner fake's
/// TryAdd.
/// </summary>
internal sealed class RendezvousStockMovementStore(
    InMemoryStockMovementStore inner, RaceRendezvous rendezvous) : IStockMovementStore
{
    public async Task<StockMovement?> GetAsync(Guid clientId, Guid movementId, CancellationToken ct = default)
    {
        StockMovement? found = await inner.GetAsync(clientId, movementId, ct);
        await rendezvous.ArriveAndWaitAsync(ct);
        return found;
    }

    public Task<(StockMovement Movement, bool Created)> RecordAsync(
        Guid clientId, Guid id, StockMovementBody body, CancellationToken ct = default) =>
        inner.RecordAsync(clientId, id, body, ct);
}
```

Read `InventoryPorts.cs` from line 45 for `IStockMovementStore`'s full member list — it includes at least `GetByItemPagedAsync` and a void-shaped member — and delegate every one verbatim.

- [ ] **Step 2: Write the failing race test**

Create `Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs`. Model the harness on `MovementRetryKeyTests` in the same project:

```csharp
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// The TOCTOU race on a retry key, pinned deterministically.
///
/// WHAT MAKES THIS VALID: revert the service's <c>replayed = !result.Created;</c> to
/// <c>replayed = known is not null;</c> and both racers claim a create, so the single-create
/// assertion sees two and fails.
///
/// DELIBERATELY A RECEIPT. Receipt and overage Adjustment compute extendedCost as
/// Round(qty x unitCost) — pure in the request — so the conversion fixes them completely. Issue and
/// shrinkage Adjustment derive cost from a pending-inclusive weighted-average fold the winner has
/// already moved, and that race is NOT fixed here (the cost is computed before the store call, so the
/// Created bool cannot help). A race test built on an Issue would fail for the fold reason and pin
/// the wrong thing.
/// </summary>
public sealed class MovementRaceTests
{
    [Fact]
    public async Task Two_receipt_racers_past_the_probe_yield_exactly_one_create()
    {
        // Build clientId, the item, accounts provider and FakeLedgerClient exactly as
        // MovementRetryKeyTests does, then substitute the racing store into InventoryMovementService.
        InMemoryStockMovementStore inner = new();
        RaceRendezvous rendezvous = new(participants: 2);
        RendezvousStockMovementStore racingStore = new(inner, rendezvous);

        Guid clientId = Guid.NewGuid();
        Guid key = Guid.NewGuid();
        // A Receipt — see the class remarks. Positive quantity, positive unit cost.

        Task<(StockMovement Movement, bool Replayed)> first = service.RecordAsync(clientId, request);
        Task<(StockMovement Movement, bool Replayed)> second = service.RecordAsync(clientId, request);
        (StockMovement Movement, bool Replayed)[] results = await Task.WhenAll(first, second);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(key, results[0].Movement.Id);
    }
}
```

Fill in the service construction and the `request` from `MovementRetryKeyTests`, using a Receipt. Every assertion shown is final.

- [ ] **Step 3: Run the test to verify it fails**

Run:
```
dotnet test Modules/Inventory/Accounting101.Inventory.Tests/Accounting101.Inventory.Tests.csproj --filter "FullyQualifiedName~MovementRaceTests"
```
Expected: **FAIL** — `Assert.Single() Failure: The collection contained 2 matching items`.

- [ ] **Step 4: Convert the port**

In `Modules/Inventory/Accounting101.Inventory/InventoryPorts.cs`, replace line 45 with:

```csharp
    /// <summary>Records the movement under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create; under two concurrent callers racing the SAME id
    /// exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document but must
    /// report its own call as a replay. A caller cannot infer this from an earlier existence check — that
    /// check is TOCTOU under concurrency.</summary>
    Task<(StockMovement Movement, bool Created)> RecordAsync(Guid clientId, Guid id, StockMovementBody body, CancellationToken ct = default);
```

- [ ] **Step 5: Convert the store**

In `Modules/Inventory/Accounting101.Inventory/DocumentStockMovementStore.cs`, replace lines 12-21 with:

```csharp
    public async Task<(StockMovement Movement, bool Created)> RecordAsync(Guid clientId, Guid id, StockMovementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create then finalize to assign the sequence number — but only the caller that
        // actually created it finalizes, so a replay burns no number and cannot race a concurrent creator.
        //
        // That bool is also the ONLY reliable created-vs-replayed signal: two concurrent callers can both
        // see "not found" on an earlier existence check (inherently TOCTOU), but this create is atomic, so
        // exactly one gets true. The service trusts this over its own pre-check.
        bool created = await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct);
        if (created)
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<StockMovementBody>? result = await documents.GetAsync<StockMovementBody>(clientId, Collection, id, ct);
        return (Map(result!), created);
    }
```

- [ ] **Step 6: Convert the service and record the scope**

In `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs`, replace the record call in the `if (!replayed)` branch with:

```csharp
            // The pre-check above is inherently TOCTOU under concurrency: two callers racing the same id
            // can both see "not found" and both reach here. The store's atomic create is the actual
            // arbiter — trust it over the pre-check for this response's status.
            //
            // SCOPE, deliberately partial. This fixes the STATUS CODE for every movement type, and that
            // is the whole fix for Receipt and overage Adjustment, whose extendedCost is Round(qty x
            // unitCost) — pure in the request. It does NOT fix Issue or shrinkage Adjustment: their cost
            // comes from a pending-inclusive weighted-average fold that the race winner has already moved,
            // and it is computed ABOVE this line, before the store call — so a loser can still get a
            // ledger 422, block-negative 409, or the extendedCost <= 0 guard for a movement that actually
            // landed. That self-heals (the next retry's probe finds the movement and freezes the cost from
            // the stored document) and is the subject of its own spec.
            (StockMovement Movement, bool Created) result = await movements.RecordAsync(clientId, id, body, ct);
            known = result.Movement;
            replayed = !result.Created;
```

Use whatever the constructor parameter is actually named in place of `movements`, and keep the surrounding statements in that branch.

- [ ] **Step 7: Convert the fake**

In `Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs`, `InMemoryStockMovementStore.RecordAsync` (around line 192) appends to a `List<StockMovement>` rather than keying a dictionary — the void path at line 218 does an index lookup. Give it an honest `Created` by guarding on the id:

```csharp
    public Task<(StockMovement Movement, bool Created)> RecordAsync(Guid clientId, Guid id, StockMovementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (_movements)
        {
            // Insert-only, mirroring the real store's atomic create: a racer that loses gets back the
            // winner's movement and an honest Created: false, rather than appending a second row.
            StockMovement? existing = _movements.FirstOrDefault(m => m.Id == id);
            if (existing is not null)
                return Task.FromResult((existing, false));

            StockMovement movement = /* keep the existing construction verbatim */;
            _movements.Add(movement);
            return Task.FromResult((movement, true));
        }
    }
```

Keep the existing `StockMovement` construction exactly as it is — only the storage guard and the return shape change. Take the same `lock (_movements)` in any other method that mutates or enumerates that list, so the race test cannot observe a torn list.

If the fake keys by `(clientId, id)` in a dictionary after all, use the `TryAdd` shape instead and say so in your report.

- [ ] **Step 8: Run the module suite**

Run:
```
dotnet test Modules/Inventory/Accounting101.Inventory.Tests/Accounting101.Inventory.Tests.csproj
```
Expected: all pass including the race test, 0 new warnings.

- [ ] **Step 9: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory/InventoryPorts.cs Modules/Inventory/Accounting101.Inventory/DocumentStockMovementStore.cs Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs Modules/Inventory/Accounting101.Inventory.Tests/RendezvousStockMovementStore.cs Modules/Inventory/Accounting101.Inventory.Tests/MovementRaceTests.cs
git commit -m "fix(inventory): movement trusts the atomic create; fold race scoped out explicitly"
```

---

### Task 9: Make `FakeLedgerClient` reject divergent content

**Files:**
- Modify: every module test project's `Fakes.cs` that defines a `FakeLedgerClient` with an unconditional dedupe (Payables, Receivables, Cash, Payroll, Reconciliation, Inventory)
- Test: one new fact per module whose fake changes

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `FakeLedgerClient.PostAsync` throws when the same `SourceRef` is posted with different content, mirroring production's 422.

**Why this is worth a task.** The fakes dedupe on `SourceRef` unconditionally, while production answers 422 when a key is reused for *different* content. Any future divergent-retry test written against a fake would therefore pass vacuously — proving nothing while looking like protection. That is the same failure mode as the HTTP concurrency test this plan started from.

- [ ] **Step 1: Find every affected fake**

Run:
```
grep -rn "class FakeLedgerClient" --include=*.cs Modules/ | grep -v /obj/
```
Record the list in your report. Each module's copy is independent — per the constraint at the top of this plan, do **not** consolidate them into a shared fake.

- [ ] **Step 2: Write one failing test per affected module**

For each module, add this fact to that module's existing ledger-fake test class, or to a new `FakeLedgerClientDivergenceTests.cs` in that test project. Adjust the namespace and the `PostEntryRequest` construction to match the module's existing usage:

```csharp
    /// <summary>
    /// Production answers 422 when a retry key is reused for DIFFERENT content; the fake must too.
    /// Without this, a divergent-retry test written against the fake passes vacuously — it looks like
    /// protection and provides none.
    /// </summary>
    [Fact]
    public async Task Posting_the_same_source_ref_with_different_content_is_rejected()
    {
        FakeLedgerClient ledger = new();
        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();

        PostEntryRequest first = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "r", Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ],
            SourceRef: sourceRef, SourceType: "Test");

        await ledger.PostAsync(clientId, first);

        PostEntryRequest divergent = first with { Memo = "changed" };

        await Assert.ThrowsAnyAsync<Exception>(() => ledger.PostAsync(clientId, divergent));
    }
```

Also assert the benign case still works, so the change cannot be satisfied by rejecting everything:

```csharp
    /// <summary>An identical re-post still dedupes — only DIVERGENT content is rejected.</summary>
    [Fact]
    public async Task Posting_the_same_source_ref_with_identical_content_still_dedupes()
    {
        FakeLedgerClient ledger = new();
        Guid clientId = Guid.NewGuid();
        Guid sourceRef = Guid.NewGuid();

        PostEntryRequest entry = new(
            Id: null, EffectiveDate: new DateOnly(2026, 3, 1), Reference: "r", Memo: null,
            Lines:
            [
                new PostLineRequest(Guid.NewGuid(), "Debit", 100m),
                new PostLineRequest(Guid.NewGuid(), "Credit", 100m),
            ],
            SourceRef: sourceRef, SourceType: "Test");

        await ledger.PostAsync(clientId, entry);
        await ledger.PostAsync(clientId, entry);

        Assert.Single(ledger.Posted, e => e.SourceRef == sourceRef);
    }
```

The second fact's `Assert.Single` may need adjusting to whatever collection that module's fake exposes — `Posted` is the Payables spelling.

- [ ] **Step 3: Run to verify the divergence test fails**

Run each affected module's suite with a filter on the new class. Expected: the divergence fact **FAILS** (no exception thrown, because the fake dedupes unconditionally); the identical-content fact passes.

- [ ] **Step 4: Tighten each fake**

In each `FakeLedgerClient.PostAsync`, before the existing dedupe, compare the incoming request against the stored one for that `SourceRef` and throw when they differ:

```csharp
        // Production answers 422 when a key is reused for different content. Mirror that here, or every
        // divergent-retry test written against this fake passes vacuously.
        if (_bySourceRef.TryGetValue(entry.SourceRef, out PostEntryRequest? seen) && !SameContent(seen, entry))
            throw new InvalidOperationException(
                $"Entry for SourceRef {entry.SourceRef} already exists with different content.");
```

with:

```csharp
    private static bool SameContent(PostEntryRequest a, PostEntryRequest b) =>
        a.EffectiveDate == b.EffectiveDate
        && a.Reference == b.Reference
        && a.Memo == b.Memo
        && a.SourceType == b.SourceType
        && a.Lines.Count == b.Lines.Count
        && a.Lines.Zip(b.Lines).All(p =>
            p.First.AccountId == p.Second.AccountId
            && p.First.Side == p.Second.Side
            && p.First.Amount == p.Second.Amount);
```

Add the `_bySourceRef` lookup if the fake does not already keep one — populate it on every successful post. Adjust the property names to each module's `PostLineRequest` shape; `Side` is the Payables spelling.

**Do not compare `Dimensions`** unless the module's own tests depend on it — dimension dictionaries compare by reference and would make identical re-posts throw.

- [ ] **Step 5: Run every affected module suite**

Run each affected module's full test project. Expected: all pass, 0 new warnings.

**If tightening the fake turns an existing test red, that is a real finding.** Report it with the test name and output. Do **not** loosen the fake again, and do **not** edit the failing assertion until it passes — either the test was relying on the vacuous behaviour (in which case it was proving less than it claimed and should be reported as such), or the `SameContent` comparison is too strict and needs narrowing with a stated reason.

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Tests/Fakes.cs Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs
git commit -m "test: FakeLedgerClient rejects a source-ref reused with divergent content"
```

Add the new test files to that `git add` list. Stage only the fakes you actually changed.

- [ ] **Step 7: Verify the whole solution**

Run:
```
dotnet build Accounting101.slnx
dotnet test Accounting101.Local.slnx
```
Expected: **0 new warnings**, and every test green. Baseline before this plan was 1494 (committed solution) / 1571 (local solution including the private overlay); the total should now be higher by the number of race and divergence tests added, with **zero failures**.

Then confirm the protected file is still unstaged:
```
git status --short
```
Expected: ` M UI/Angular/src/app/core/api/environment.ts` and nothing else.

---

## Self-Review

**1. Spec coverage**

| Spec requirement | Task |
|---|---|
| `RaceRendezvous` in `Accounting101.TestSupport`, timeout load-bearing | 1 |
| Per-module rendezvous decorator blocking in the probe | 1, 3, 4, 5, 6, 8 |
| Race test for the already-fixed `BillPaymentService.RecordPaymentAsync` | 1 |
| Each race test proven to be a genuine discriminator | 1 Step 5 (explicit revert-and-restore); 2–8 via the TDD red step |
| Non-binding allocation amounts, to dodge follow-up #3 | 1 Step 3, 2 Step 2, 6 Step 2, 7 Step 2 |
| Existing HTTP concurrency test kept and annotated | 1 Step 6 |
| Twelve conversions: port, store, service, fake | 2 (1), 3 (2), 4 (2), 5 (1), 6 (2), 7 (3), 8 (1) = 12 |
| Inventory converted with an explicit half-fixed scoping comment | 8 Step 6; race test forced to a Receipt, 8 Step 2 |
| `includeVoided` on `GetCreditApplicationsByVendorAsync` | 2 Step 9 |
| `includeVoided` on `GetPaymentsByCustomerAsync` | 6 Step 8 |
| `FakeLedgerClient` rejects divergent content; risk of existing red reported not papered over | 9 |
| No shared helper; nothing moved into the engine | Stated in Global Constraints and "The constraint that shapes every task"; repeated in each decorator's rationale |
| Full solution green | 9 Step 7 |

No spec requirement is unaddressed. The spec's "Out of scope" items (Inventory's fold race, follow-up #3, follow-up #4, relocating Inventory) correctly have no task.

**2. Placeholder scan**

No "TBD", "TODO", or "implement later". Two categories of deliberate non-literal content, both flagged in place rather than left silent:

- Harness construction in Tasks 3–8 references a specific existing test file in the same project (`CashRetryKeyTests`, `PayrollRetryKeyTests`, `AdjustmentRetryKeyTests`, `PaymentRetryKeyTests`, `DispositionRetryKeyTests`, `MovementRetryKeyTests`). Those files exist and already construct these services with these fakes. Every assertion — the part that carries the meaning — is literal.
- Field and local names inside fakes vary per module; each step says to use the actual names and states what must not change.

**3. Type consistency**

Tuple element names are consistent between each port, store, service and decorator: `(BillPayment Payment, bool Created)`, `(VendorCreditApplication Application, bool Created)`, `(CashDisbursement Disbursement, bool Created)`, `(CashDeposit Deposit, bool Created)`, `(PayrollRun Run, bool Created)`, `(TaxRemittance Remittance, bool Created)`, `(BankAdjustment Adjustment, bool Created)`, `(Payment Payment, bool Created)`, `(CreditApplication Application, bool Created)`, `(WriteOff WriteOff, bool Created)`, `(CreditNote CreditNote, bool Created)`, `(Refund Refund, bool Created)`, `(StockMovement Movement, bool Created)`.

`RaceRendezvous(int participants, TimeSpan? timeout = null)` with `ArriveAndWaitAsync(CancellationToken ct = default)` is defined in Task 1 and called with that exact name in Tasks 2–8; every decorator constructor is `(<InMemory store> inner, RaceRendezvous rendezvous)`.

Service-side tuples stay `(T, bool Replayed)` throughout — deliberately different from the store's `Created`, because they answer different questions: `Created` is "did I win the atomic create", `Replayed` is "should this response say 200 rather than 201".
