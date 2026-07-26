# Retry Keys on Money-Moving POSTs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give all thirteen money-moving POSTs an optional caller-supplied `Id` that becomes the document id, so a retry replays instead of paying twice.

**Architecture:** Every one of the thirteen already composes its ledger entry with `EntryIdentity.ForSource(sourceType, documentId)`, and the ledger already replays idempotently on entry id. The document id is the only missing link — it is minted server-side today (`ScopedDocumentStore.CreateAsync:110`). We add an insert-only `CreateWithIdAsync` to the document store, thread a caller-supplied id through each service, skip *state-dependent* validation on the replay branch, and always post the composed entry so the ledger arbitrates replay-vs-422. No new content comparison is written anywhere.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, MongoDB (official driver), xUnit; Angular 22 with signals, Jasmine/Karma.

**Spec:** `docs/superpowers/specs/2026-07-25-retry-keys-design.md`

## Global Constraints

- .NET 10; Angular 22; build with **0 warnings**; TDD; commit per task; stage **explicit file lists** (never `git add -A`).
- `UI/Angular/src/app/core/api/environment.ts` carries a long-lived local dev-config edit that **must stay uncommitted**.
- No new npm or NuGet dependency.
- No module gains a reference to another module. The only shared change is `IDocumentStore.CreateWithIdAsync` in `Accounting101.Ledger.Contracts`, which every module already references.
- The `Id` stays **optional** at every layer. No existing caller changes behaviour.
- `ModuleDocumentException` and `ModuleAccessDeniedException` live in `Accounting101.Ledger.Api.Documents`. Modules reference only `Accounting101.Ledger.Contracts` and **cannot catch them** — never write module code that tries.
- Backend suite: `dotnet test` from repo root. Angular suite: `npm test -- --watch=false` from `UI/Angular`.
- 13 pre-existing NG04002 console errors in the Angular suite (inventory/banking/fixed-assets specs) are **expected noise**, not failures.

## The two rules every module task follows

**Rule 1 — what runs on which branch.** Stateless guards (amount > 0, allocations positive, shape checks) run on **both** branches; they depend on nothing mutable. State-dependent validation — anything that reads a balance, an open invoice, a credit balance, or an inventory valuation — runs **only on the create branch**. Re-running it on a replay would reject the retry using a balance the first call itself reduced.

**Rule 2 — the entry is always composed from the request, never from the stored document.** This is what makes divergence detectable. Composing from the stored body would make every replay match and the 422 would never fire.

## File Structure

**Engine (Task 1)**
- `Backend/Accounting101.Ledger.Mongo/MongoDocumentStore.cs` — add `InsertIfAbsentAsync`, the atomic primitive (`InsertOneAsync`, duplicate-key → `false`).
- `Backend/Accounting101.Ledger.Contracts/IDocumentStore.cs` — add `CreateWithIdAsync`.
- `Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs` — implement it. Sole implementation of the interface.

**Per module (Tasks 2–9)** — each touches exactly five kinds of file, all existing:
- the `*Requests.cs` record → add trailing `Guid? Id = null`
- the `*Endpoints.cs` handler → pass the id, return 200 on replay / 201 on create
- the `*Service.cs` → the replay branch
- the `Document*Store.cs` + its interface → `RecordAsync(clientId, id, body, ct)`
- `Tests/Fakes.cs` → match the new store signature

**Angular (Tasks 11–15)**
- `UI/Angular/src/app/core/api/problem-details.ts` — add `isDuplicateRetryKey` + friendly copy, beside the existing `extractProblem`.
- five `core/**/**.service.ts` request interfaces → `id?: string`
- ten editor components → mint on construction, re-mint after success.

---

### Task 1: The engine — an insert-only document create

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoDocumentStore.cs` (add method after `PutAsync`, ~line 52)
- Modify: `Backend/Accounting101.Ledger.Contracts/IDocumentStore.cs` (add to the `// Evidentiary` block, ~line 49)
- Modify: `Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs` (add after `CreateAsync`, ~line 114)
- Test: `Backend/Accounting101.Ledger.Api.Tests/DocumentStoreEvidentiaryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Task<bool> IDocumentStore.CreateWithIdAsync<T>(Guid clientId, string collection, Guid id, T body, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default)` — returns `true` when it wrote, `false` when the id was already taken. Every module task consumes this exact signature.

- [ ] **Step 1: Write the failing tests**

Append to `Backend/Accounting101.Ledger.Api.Tests/DocumentStoreEvidentiaryTests.cs`. Match the existing tests' fixture/setup style in that file — read the top of it first and reuse whatever helper builds `store` and `clientId`.

```csharp
[Fact]
public async Task CreateWithId_writes_under_the_supplied_id_and_returns_true()
{
    Guid id = Guid.NewGuid();

    bool created = await store.CreateWithIdAsync(clientId, "invoices", id, new { Total = 10m }, new Dictionary<string, string>());

    Assert.True(created);
    DocumentResult<InvoiceBody>? found = await store.GetAsync<InvoiceBody>(clientId, "invoices", id);
    Assert.NotNull(found);
    Assert.Equal(id, found!.Id);
    Assert.Equal(DocumentLifecycle.Draft, found.State);
}

[Fact]
public async Task CreateWithId_returns_false_when_the_id_is_taken_and_does_not_overwrite()
{
    Guid id = Guid.NewGuid();
    await store.CreateWithIdAsync(clientId, "invoices", id, new { Total = 10m }, new Dictionary<string, string>());

    bool second = await store.CreateWithIdAsync(clientId, "invoices", id, new { Total = 999m }, new Dictionary<string, string>());

    Assert.False(second);
    DocumentResult<InvoiceBody>? found = await store.GetAsync<InvoiceBody>(clientId, "invoices", id);
    Assert.Equal(10m, found!.Body.Total);   // the first body survived
}

[Fact]
public async Task CreateWithId_refusal_burns_no_sequence_number()
{
    Guid first = Guid.NewGuid();
    await store.CreateWithIdAsync(clientId, "invoices", first, new { Total = 10m }, new Dictionary<string, string>());
    long firstNumber = await store.FinalizeAsync(clientId, "invoices", first);

    // A refused create must not touch the counter.
    await store.CreateWithIdAsync(clientId, "invoices", first, new { Total = 10m }, new Dictionary<string, string>());

    Guid next = Guid.NewGuid();
    await store.CreateWithIdAsync(clientId, "invoices", next, new { Total = 20m }, new Dictionary<string, string>());
    Assert.Equal(firstNumber + 1, await store.FinalizeAsync(clientId, "invoices", next));   // gapless
}

[Fact]
public async Task CreateWithId_is_rejected_on_a_non_evidentiary_collection()
{
    await Assert.ThrowsAsync<ModuleDocumentException>(() =>
        store.CreateWithIdAsync(clientId, "settings", Guid.NewGuid(), new { A = 1 }, new Dictionary<string, string>()));
}
```

`InvoiceBody` and the `"settings"`/`"invoices"` collection names come from the existing tests in this file — reuse whatever that file already declares rather than inventing new ones. If it uses a different plain-policy collection than `"settings"`, use that.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter FullyQualifiedName~DocumentStoreEvidentiaryTests`
Expected: FAIL to compile — `'IDocumentStore' does not contain a definition for 'CreateWithIdAsync'`.

- [ ] **Step 3: Add the atomic primitive to MongoDocumentStore**

In `Backend/Accounting101.Ledger.Mongo/MongoDocumentStore.cs`, immediately after `PutAsync`:

```csharp
/// <summary>
/// Insert only when the id is free. Returns false on an <c>_id</c> collision instead of throwing — for
/// caller-declared retry keys a taken id is the expected outcome of a retry, not an error. Atomic: the
/// unique index on <c>_id</c> is the arbiter, so two concurrent callers cannot both receive true.
/// </summary>
public async Task<bool> InsertIfAbsentAsync(
    string collection, ModuleDocument document, IClientSessionHandle? session = null, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(document);
    IMongoCollection<ModuleDocument> c = Collection(collection);
    try
    {
        if (session is null)
            await c.InsertOneAsync(document, null, cancellationToken);
        else
            await c.InsertOneAsync(session, document, null, cancellationToken);
        return true;
    }
    catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
    {
        return false;
    }
}
```

`MongoDB.Driver` is already imported at the top of this file.

- [ ] **Step 4: Add the contract**

In `Backend/Accounting101.Ledger.Contracts/IDocumentStore.cs`, in the `// Evidentiary` block directly under `CreateAsync`:

```csharp
    /// <summary>
    /// Create an evidentiary document under a caller-supplied id. Returns <c>true</c> when it wrote and
    /// <c>false</c> when that id is already taken, leaving the existing document untouched — it never
    /// overwrites. Callers read <c>false</c> as "this operation is already known", which is the basis of
    /// caller-declared retry keys on money-moving writes.
    /// <para>
    /// A bool rather than an exception on the taken path: the collision is the expected outcome of a
    /// retry, and the store's exception types live in the Api assembly where module callers cannot see
    /// them.
    /// </para></summary>
    Task<bool> CreateWithIdAsync<T>(Guid clientId, string collection, Guid id, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);
```

- [ ] **Step 5: Implement it in ScopedDocumentStore**

In `Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs`, directly after `CreateAsync` (which ends ~line 114):

```csharp
    public async Task<bool> CreateWithIdAsync<T>(Guid clientId, string collection, Guid id, T body,
        IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default)
    {
        RequireEvidentiary(collection);
        Ctx ctx = await EnterAsync(clientId, collection, ModuleAccessLevel.Write, cancellationToken);
        ModuleDocument doc = BuildDoc(id, body, tags, DocumentState.Draft, 1, null);
        return await ctx.Store.InsertIfAbsentAsync(ctx.Physical, doc, null, cancellationToken); // draft: no audit
    }
```

Identical to `CreateAsync` except the id is supplied and the write is insert-only. `CreateAsync` itself is **not** modified — its dozens of callers outside these thirteen must keep working unchanged.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter FullyQualifiedName~DocumentStoreEvidentiaryTests`
Expected: PASS, including the pre-existing `ModuleDocumentException` double-finalize test at line 101 — that invariant is deliberate and must stay green.

- [ ] **Step 7: Run the full backend suite**

Run: `dotnet test`
Expected: PASS. Nothing else in the codebase calls the new method yet.

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Mongo/MongoDocumentStore.cs Backend/Accounting101.Ledger.Contracts/IDocumentStore.cs Backend/Accounting101.Ledger.Api/Documents/ScopedDocumentStore.cs Backend/Accounting101.Ledger.Api.Tests/DocumentStoreEvidentiaryTests.cs
git commit -m "feat(engine): insert-only CreateWithIdAsync on the document store"
```

---

### Task 2: Payables — bill payment (the reference implementation)

This is the pattern every later module task repeats. Read it carefully; the others are the same shape with different type names.

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesRequests.cs:18-19`
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs:198-212`
- Modify: `Modules/Payables/Accounting101.Payables/BillPaymentService.cs:13-31`
- Modify: `Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs:13-20`
- Modify: the `IBillPaymentStore` interface (find it — `Modules/Payables/Accounting101.Payables/`), and `BillPaymentCommand`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs`
- Test: `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRetryKeyTests.cs` (create)

**Interfaces:**
- Consumes: `IDocumentStore.CreateWithIdAsync` from Task 1.
- Produces: `BillPaymentService.RecordPaymentAsync(Guid clientId, BillPaymentCommand command, CancellationToken ct = default)` now returns `Task<(BillPayment Payment, bool Replayed)>`. `IBillPaymentStore.RecordPaymentAsync` now takes `(Guid clientId, Guid id, BillPaymentBody body, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRetryKeyTests.cs`. Model the fixture usage on the existing `PayablesE2eTests.cs` in the same folder — reuse `PayablesHostFixture`, `SeedSodClientAsync`, `SetUpChartAsync`, `CreateVendorAsync`, `EnterBillAsync` from `BillSettlementScenario` exactly as `LedgerErrorRelayE2eTests.cs` does.

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using Microsoft.AspNetCore.Mvc;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>The retry-key contract on POST /bill-payments: absent key behaves as before, a replayed key
/// returns the stored payment without creating a second one, and a key reused for different financial
/// content is a 422 relayed from the ledger.</summary>
public sealed class BillPaymentRetryKeyTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Without_a_key_the_post_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 1), 100m, "check", [new Allocation(bill, 100m)]));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task A_replayed_key_returns_the_stored_payment_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        Guid key = Guid.NewGuid();
        RecordBillPaymentRequest req = new(vendor, new DateOnly(2026, 3, 1), 100m, "check", [new Allocation(bill, 100m)], key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        BillPayment a = (await first.Content.ReadFromJsonAsync<BillPayment>())!;
        BillPayment b = (await second.Content.ReadFromJsonAsync<BillPayment>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        BillPayment[] all = (await clerk.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientId}/bill-payments?vendorId={vendor}"))!;
        Assert.Single(all);
    }

    [Fact]
    public async Task The_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill = await EnterBillAsync(clerk, approver, clientId, vendor, 100m, fixture.RentExpenseAccountId);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 1), 100m, "check", [new Allocation(bill, 100m)], key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 1), 60m, "check", [new Allocation(bill, 60m)], key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPaymentRetryKeyTests`
Expected: FAIL to compile — `RecordBillPaymentRequest` has no 6-argument constructor.

- [ ] **Step 3: Add the optional Id to the request and the command**

`Modules/Payables/Accounting101.Payables.Api/PayablesRequests.cs`, replacing lines 17-19:

```csharp
/// <summary>Record a vendor payment with its allocations across bills. <paramref name="Id"/> is an
/// optional caller-supplied retry key: re-sending it with the same financial content replays the
/// existing payment (200) instead of paying twice; re-sending it with different content is a 422.
/// Omitting it is the caller's choice to go unprotected.</summary>
public sealed record RecordBillPaymentRequest(
    Guid VendorId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations,
    Guid? Id = null);
```

Add the same trailing `Guid? Id = null` to `BillPaymentCommand` (find its declaration in `Modules/Payables/Accounting101.Payables/`).

- [ ] **Step 4: Thread the id through the store**

`Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs`, replacing `RecordPaymentAsync` (lines 13-20):

```csharp
    public async Task<BillPayment> RecordPaymentAsync(Guid clientId, Guid id, BillPaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Only the caller that actually created the document finalizes it. FinalizeAsync throws on a
        // document that is no longer Draft (a deliberately pinned invariant), and that exception type
        // lives in the engine's Api assembly where this module cannot catch it — so we must not race.
        // This also means a replay burns no sequence number.
        if (await documents.CreateWithIdAsync(clientId, BillPayments, id, body, Tags(body.VendorId), ct))
            await documents.FinalizeAsync(clientId, BillPayments, id, ct);
        DocumentResult<BillPaymentBody>? result = await documents.GetAsync<BillPaymentBody>(clientId, BillPayments, id, ct);
        return MapPayment(result!);
    }
```

Change the matching signature on `IBillPaymentStore` to `Task<BillPayment> RecordPaymentAsync(Guid clientId, Guid id, BillPaymentBody body, CancellationToken ct = default);`.

- [ ] **Step 5: Add the replay branch to the service**

`Modules/Payables/Accounting101.Payables/BillPaymentService.cs`, replacing `RecordPaymentAsync` (lines 13-31):

```csharp
    public async Task<(BillPayment Payment, bool Replayed)> RecordPaymentAsync(
        Guid clientId, BillPaymentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Stateless guards run on both branches — they depend on nothing mutable.
        if (command.Amount <= 0m)
            throw new InvalidOperationException("A payment amount must be greater than zero.");
        if (command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("Every allocation amount must be greater than zero.");
        if (command.Allocations.Sum(a => a.Amount) > command.Amount)
            throw new InvalidOperationException("Allocations cannot exceed the payment amount.");

        Guid id = command.Id ?? Guid.NewGuid();
        BillPayment? known = command.Id is not null
            ? await payments.GetPaymentAsync(clientId, id, ct)
            : null;
        bool replayed = known is not null;

        BillPaymentBody body = new(command.VendorId, command.Date, command.Amount, command.Method);

        if (!replayed)
        {
            // State-dependent: allocations are checked against each bill's REMAINING balance, which this
            // very payment reduces. Re-running it on a replay would reject the retry it exists to permit.
            await ValidateAllocationsAsync(clientId, command.VendorId, command.Allocations, ct);
            known = await payments.RecordPaymentAsync(clientId, id, body, ct);
        }

        // Composed from the REQUEST, never from the stored document — that is what lets the ledger see a
        // divergent retry and answer 422. Posting on both branches also repairs an orphan: a retry after a
        // failed post completes the entry that never landed.
        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeBillPayment(id, body, command.Allocations, posting);
        await ledger.PostAsync(clientId, entry, ct);

        return (known!, replayed);
    }
```

- [ ] **Step 6: Return 200 on replay from the endpoint**

`Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs`, replacing `RecordPayment` (lines 198-212):

```csharp
    private static async Task<IResult> RecordPayment(
        Guid clientId, RecordBillPaymentRequest request, BillPaymentService service, CancellationToken cancellationToken)
    {
        try
        {
            (BillPayment recorded, bool replayed) = await service.RecordPaymentAsync(clientId,
                new BillPaymentCommand(request.VendorId, request.Date, request.Amount, request.Method, request.Allocations, request.Id),
                cancellationToken);
            return replayed
                ? Results.Ok(recorded)
                : Results.Created($"/clients/{clientId}/bill-payments/{recorded.Id}", recorded);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
```

- [ ] **Step 7: Update the test fake and every other caller**

In `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs`, change the fake's `RecordPaymentAsync` to the new `(clientId, id, body, ct)` signature and have it store under the supplied `id` rather than a fresh Guid.

Then compile and fix every call site the signature change breaks:

Run: `dotnet build Modules/Payables/Accounting101.Payables.Tests`

Existing tests that call `service.RecordPaymentAsync(...)` directly now get a tuple — destructure or take `.Payment`. Do **not** change what those tests assert.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: PASS, all of it — the three new tests and every pre-existing Payables test.

- [ ] **Step 9: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Api/PayablesRequests.cs Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs Modules/Payables/Accounting101.Payables/BillPaymentService.cs Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs Modules/Payables/Accounting101.Payables.Tests/Fakes.cs Modules/Payables/Accounting101.Payables.Tests/BillPaymentRetryKeyTests.cs
git commit -m "feat(payables): retry key on POST /bill-payments"
```

Also stage the `IBillPaymentStore` interface file and any test files you had to adjust in Step 7.

---

### Task 3: Payables — vendor credit application

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesRequests.cs:21-23`
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs` (the `ApplyCredit` handler)
- Modify: `Modules/Payables/Accounting101.Payables/BillPaymentService.cs:33-52`
- Modify: `Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs:22-29` + `IBillPaymentStore`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs`
- Test: `Modules/Payables/Accounting101.Payables.Tests/VendorCreditRetryKeyTests.cs` (create)

**Interfaces:**
- Consumes: `IDocumentStore.CreateWithIdAsync` (Task 1).
- Produces: `BillPaymentService.RecordCreditApplicationAsync` returns `Task<(VendorCreditApplication Application, bool Replayed)>`; `IBillPaymentStore.RecordCreditApplicationAsync(Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Payables/Accounting101.Payables.Tests/VendorCreditRetryKeyTests.cs` with the same three-case matrix as Task 2, against `POST /clients/{clientId}/vendor-credit-applications` and `VendorCreditApplicationRequest`. To have credit available, first record an overpayment (a bill payment whose amount exceeds its allocations leaves the remainder as vendor credit — see `BillPosting.ComposeBillPayment`'s remainder line). Assert:

- no key → `HttpStatusCode.Created`
- same key twice → `Created` then `OK`, same `Id`, and `GET /vendor-credit-applications?vendorId=` returns one row
- same key with a different allocation amount → `UnprocessableEntity`

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter FullyQualifiedName~VendorCreditRetryKeyTests`
Expected: FAIL to compile — `VendorCreditApplicationRequest` has no 4-argument constructor.

- [ ] **Step 3: Add the optional Id**

`PayablesRequests.cs`, replacing lines 21-23:

```csharp
/// <summary>Apply a vendor's existing credit to bills. <paramref name="Id"/> is an optional
/// caller-supplied retry key — same key and same content replays, different content is a 422.</summary>
public sealed record VendorCreditApplicationRequest(
    Guid VendorId, DateOnly Date, IReadOnlyList<Allocation> Allocations, Guid? Id = null);
```

Add the same trailing `Guid? Id = null` to `VendorCreditApplicationCommand`.

- [ ] **Step 4: Thread the id through the store**

`DocumentBillPaymentStore.cs`, replacing `RecordCreditApplicationAsync` (lines 22-29):

```csharp
    public async Task<VendorCreditApplication> RecordCreditApplicationAsync(Guid clientId, Guid id, VendorCreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await documents.CreateWithIdAsync(clientId, VendorCreditApplications, id, body, Tags(body.VendorId), ct))
            await documents.FinalizeAsync(clientId, VendorCreditApplications, id, ct);
        DocumentResult<VendorCreditApplicationBody>? result = await documents.GetAsync<VendorCreditApplicationBody>(clientId, VendorCreditApplications, id, ct);
        return MapCredit(result!);
    }
```

Update `IBillPaymentStore` to match.

- [ ] **Step 5: Add the replay branch to the service**

`BillPaymentService.cs`, replacing `RecordCreditApplicationAsync` (lines 33-52):

```csharp
    public async Task<(VendorCreditApplication Application, bool Replayed)> RecordCreditApplicationAsync(
        Guid clientId, VendorCreditApplicationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Stateless guard — runs on both branches.
        if (command.Allocations.Count == 0 || command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit application needs positive allocations.");

        Guid id = command.Id ?? Guid.NewGuid();
        VendorCreditApplication? known = command.Id is not null
            ? await payments.GetCreditApplicationAsync(clientId, id, ct)
            : null;
        bool replayed = known is not null;

        VendorCreditApplicationBody body = new(command.VendorId, command.Date);

        if (!replayed)
        {
            // Both of these are state-dependent: the available credit balance and each bill's remaining
            // balance are reduced by this very application, so a replay must not re-check them.
            decimal applying = command.Allocations.Sum(a => a.Amount);
            decimal available = await GetVendorCreditBalanceAsync(clientId, command.VendorId, ct);
            if (applying > available)
                throw new InvalidOperationException($"Credit application of {applying} exceeds available credit {available}.");

            await ValidateAllocationsAsync(clientId, command.VendorId, command.Allocations, ct);
            known = await payments.RecordCreditApplicationAsync(clientId, id, body, ct);
        }

        BillPaymentPostingAccounts posting = await accounts.GetPaymentAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeVendorCreditApplication(id, body, command.Allocations, posting);
        await ledger.PostAsync(clientId, entry, ct);

        return (known!, replayed);
    }
```

- [ ] **Step 6: Return 200 on replay from the endpoint**

In `PayablesEndpoints.cs`, change the `ApplyCredit` handler the same way Task 2 Step 6 changed `RecordPayment`: destructure the tuple, pass `request.Id` into the command, and return `Results.Ok(recorded)` when `replayed` else the existing `Results.Created(...)`. Keep its existing `catch (InvalidOperationException)` → 422 block unchanged.

- [ ] **Step 7: Update the fake and fix call sites**

Run: `dotnet build Modules/Payables/Accounting101.Payables.Tests` and fix what the signature change breaks, exactly as in Task 2 Step 7.

- [ ] **Step 8: Run the tests**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Api/PayablesRequests.cs Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs Modules/Payables/Accounting101.Payables/BillPaymentService.cs Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs Modules/Payables/Accounting101.Payables.Tests/Fakes.cs Modules/Payables/Accounting101.Payables.Tests/VendorCreditRetryKeyTests.cs
git commit -m "feat(payables): retry key on POST /vendor-credit-applications"
```

---

### Task 4: Receivables — payment and credit application

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesRequests.cs:16-22`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (`RecordPayment`, `ApplyCredit` handlers)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs:14-33` and `:253-272`
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs:16-32` + `IPaymentStore`
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs`, `DocumentPaymentStoreTests.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/PaymentRetryKeyTests.cs` (create)

**Interfaces:**
- Consumes: `IDocumentStore.CreateWithIdAsync` (Task 1).
- Produces: `PaymentService.RecordPaymentAsync` → `Task<(Payment Payment, bool Replayed)>`; `PaymentService.RecordCreditApplicationAsync` → `Task<(CreditApplication Application, bool Replayed)>`; `IPaymentStore.RecordPaymentAsync(Guid clientId, Guid id, PaymentBody body, CancellationToken ct = default)` and `IPaymentStore.RecordCreditApplicationAsync(Guid clientId, Guid id, CreditApplicationBody body, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Receivables/Accounting101.Receivables.Tests/PaymentRetryKeyTests.cs` with the three-case matrix for **both** endpoints — `POST /payments` and `POST /credit-applications` — so six tests. Model the fixture usage on the existing `ReceivablesDispositionsE2eTests.cs` in the same folder. Assertions mirror Task 2: no key → `Created`; same key twice → `Created` then `OK` with equal `Id` and a single row from the list endpoint; same key with a different amount → `UnprocessableEntity` whose `Detail` is exactly `"An entry with this id already exists with different content."`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter FullyQualifiedName~PaymentRetryKeyTests`
Expected: FAIL to compile — the request records have no `Id` parameter.

- [ ] **Step 3: Add the optional Id to both requests**

`ReceivablesRequests.cs`, replacing lines 16-22:

```csharp
/// <summary>Record a customer payment with its allocations across invoices. <paramref name="Id"/> is an
/// optional caller-supplied retry key — same key and same content replays, different content is a 422.</summary>
public sealed record RecordPaymentRequest(
    Guid CustomerId, DateOnly Date, decimal Amount, string? Method, IReadOnlyList<Allocation> Allocations,
    Guid? Id = null);

/// <summary>Apply a customer's existing credit to invoices. <paramref name="Id"/> is an optional
/// caller-supplied retry key — same key and same content replays, different content is a 422.</summary>
public sealed record CreditApplicationRequest(
    Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, Guid? Id = null);
```

Add the same trailing `Guid? Id = null` to `PaymentCommand` and `CreditApplicationCommand`.

- [ ] **Step 4: Thread the id through the store**

`DocumentPaymentStore.cs`, replacing lines 16-32:

```csharp
    public async Task<Payment> RecordPaymentAsync(Guid clientId, Guid id, PaymentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Only the creating caller finalizes — see DocumentBillPaymentStore for why. A replay burns no number.
        if (await documents.CreateWithIdAsync(clientId, Payments, id, body, Tags(body.CustomerId), ct))
            await documents.FinalizeAsync(clientId, Payments, id, ct);
        DocumentResult<PaymentBody>? result = await documents.GetAsync<PaymentBody>(clientId, Payments, id, ct);
        return MapPayment(result!);
    }

    public async Task<CreditApplication> RecordCreditApplicationAsync(Guid clientId, Guid id, CreditApplicationBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await documents.CreateWithIdAsync(clientId, CreditApplications, id, body, Tags(body.CustomerId), ct))
            await documents.FinalizeAsync(clientId, CreditApplications, id, ct);
        DocumentResult<CreditApplicationBody>? result = await documents.GetAsync<CreditApplicationBody>(clientId, CreditApplications, id, ct);
        return MapCredit(result!);
    }
```

Update `IPaymentStore` to match both.

- [ ] **Step 5: Add the replay branch to both service methods**

`PaymentService.cs`, replacing `RecordPaymentAsync` (lines 14-33):

```csharp
    public async Task<(Payment Payment, bool Replayed)> RecordPaymentAsync(
        Guid clientId, PaymentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Stateless guards — both branches.
        if (command.Amount <= 0m)
            throw new InvalidOperationException("A payment amount must be greater than zero.");
        if (command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("Every allocation amount must be greater than zero.");
        if (command.Allocations.Sum(a => a.Amount) > command.Amount)
            throw new InvalidOperationException("Allocations cannot exceed the payment amount.");

        Guid id = command.Id ?? Guid.NewGuid();
        Payment? known = command.Id is not null
            ? await payments.GetPaymentAsync(clientId, id, ct)
            : null;
        bool replayed = known is not null;

        PaymentBody body = new(command.CustomerId, command.Date, command.Amount, command.Method);

        if (!replayed)
        {
            // State-dependent: checks each invoice's remaining open balance, which this payment reduces.
            await ValidateAllocationsAsync(clientId, command.CustomerId, command.Allocations, ct);
            known = await payments.RecordPaymentAsync(clientId, id, body, ct);
        }

        // Composed from the request, and posted on both branches — see Task 2 for why.
        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        PostEntryRequest entry = PaymentPosting.ComposePayment(id, body, command.Allocations, posting);
        await ledger.PostAsync(clientId, entry, ct);

        return (known!, replayed);
    }
```

And replacing `RecordCreditApplicationAsync` (lines 253-272):

```csharp
    public async Task<(CreditApplication Application, bool Replayed)> RecordCreditApplicationAsync(
        Guid clientId, CreditApplicationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Allocations.Count == 0 || command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit application needs positive allocations.");

        Guid id = command.Id ?? Guid.NewGuid();
        CreditApplication? known = command.Id is not null
            ? await payments.GetCreditApplicationAsync(clientId, id, ct)
            : null;
        bool replayed = known is not null;

        CreditApplicationBody body = new(command.CustomerId, command.Date);

        if (!replayed)
        {
            decimal applying = command.Allocations.Sum(a => a.Amount);
            decimal available = await GetCustomerCreditBalanceAsync(clientId, command.CustomerId, ct);
            if (applying > available)
                throw new InvalidOperationException($"Credit application of {applying} exceeds available credit {available}.");

            await ValidateAllocationsAsync(clientId, command.CustomerId, command.Allocations, ct);
            known = await payments.RecordCreditApplicationAsync(clientId, id, body, ct);
        }

        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        PostEntryRequest entry = PaymentPosting.ComposeCreditApplication(id, body, command.Allocations, posting);
        await ledger.PostAsync(clientId, entry, ct);

        return (known!, replayed);
    }
```

- [ ] **Step 6: Return 200 on replay from both endpoints**

In `ReceivablesEndpoints.cs`, change `RecordPayment` and `ApplyCredit` to destructure the tuple, pass `request.Id` into the command, and return `Results.Ok(recorded)` when replayed else the existing `Results.Created(...)`. Leave their `catch` blocks unchanged.

- [ ] **Step 7: Update fakes and fix call sites**

Run: `dotnet build Modules/Receivables/Accounting101.Receivables.Tests` and fix breaks in `Fakes.cs` and `DocumentPaymentStoreTests.cs`. Existing direct callers of the two service methods now get tuples.

- [ ] **Step 8: Run the tests**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Api/ReceivablesRequests.cs Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs Modules/Receivables/Accounting101.Receivables/PaymentService.cs Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs Modules/Receivables/Accounting101.Receivables.Tests/PaymentRetryKeyTests.cs
git commit -m "feat(receivables): retry keys on POST /payments and /credit-applications"
```

---

### Task 5: Receivables — write-off, credit note, refund

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesRequests.cs:24-31`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (`RecordWriteOff`, `RecordCreditNote`, `RecordRefund`)
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs:306-317`, `:331-342`, `:356-367`
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs` (`RecordWriteOffAsync`, `RecordCreditNoteAsync`, `RecordRefundAsync`) + `IPaymentStore`
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/DispositionRetryKeyTests.cs` (create)

**Interfaces:**
- Consumes: `IDocumentStore.CreateWithIdAsync` (Task 1).
- Produces: `RecordWriteOffAsync` → `Task<(WriteOff WriteOff, bool Replayed)>`; `RecordCreditNoteAsync` → `Task<(CreditNote CreditNote, bool Replayed)>`; `RecordRefundAsync(Guid clientId, RefundBody body, Guid? retryKey = null, CancellationToken ct = default)` → `Task<(Refund Refund, bool Replayed)>`. Note the refund service method takes a `RefundBody` directly, not a command type — the key is a separate parameter there.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Receivables/Accounting101.Receivables.Tests/DispositionRetryKeyTests.cs` — the three-case matrix for each of `/write-offs`, `/credit-notes` and `/refunds`, so nine tests. Refunds need available credit first (record an overpayment). Divergence for write-off and credit note is a different allocation amount; for refund, a different `Amount`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter FullyQualifiedName~DispositionRetryKeyTests`
Expected: FAIL to compile.

- [ ] **Step 3: Add the optional Id to the three requests**

`ReceivablesRequests.cs`, replacing lines 24-31:

```csharp
/// <summary>Write off invoice balances to bad-debt expense. <paramref name="Id"/> is an optional
/// caller-supplied retry key — same key and same content replays, different content is a 422.</summary>
public sealed record WriteOffRequest(
    Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo, Guid? Id = null);

/// <summary>Issue a credit note against invoices (contra-revenue, no cash). <paramref name="Id"/> is an
/// optional caller-supplied retry key — same key and same content replays, different content is a 422.</summary>
public sealed record CreditNoteRequest(
    Guid CustomerId, DateOnly Date, IReadOnlyList<Allocation> Allocations, string? Memo, Guid? Id = null);

/// <summary>Refund a customer's unapplied credit as cash. <paramref name="Id"/> is an optional
/// caller-supplied retry key — same key and same content replays, different content is a 422.</summary>
public sealed record RefundRequest(
    Guid CustomerId, DateOnly Date, decimal Amount, string? Memo, Guid? Id = null);
```

Add the same trailing `Guid? Id = null` to `WriteOffCommand` and `CreditNoteCommand`.

- [ ] **Step 4: Thread the id through the three store methods**

In `DocumentPaymentStore.cs`, apply the identical transformation to each of `RecordWriteOffAsync`, `RecordCreditNoteAsync` and `RecordRefundAsync` — take `Guid id` as the second parameter, and replace the `CreateAsync` + unconditional `FinalizeAsync` pair with:

```csharp
        if (await documents.CreateWithIdAsync(clientId, WriteOffs, id, body, Tags(body.CustomerId), ct))
            await documents.FinalizeAsync(clientId, WriteOffs, id, ct);
        DocumentResult<WriteOffBody>? r = await documents.GetAsync<WriteOffBody>(clientId, WriteOffs, id, ct);
        return MapWriteOff(r!);
```

substituting `CreditNotes`/`CreditNoteBody`/`MapCreditNote` and `Refunds`/`RefundBody`/`MapRefund` respectively. Update all three signatures on `IPaymentStore`.

- [ ] **Step 5: Add the replay branch to the three service methods**

`PaymentService.cs`, replacing `RecordWriteOffAsync` (lines 306-317):

```csharp
    public async Task<(WriteOff WriteOff, bool Replayed)> RecordWriteOffAsync(
        Guid clientId, WriteOffCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Allocations.Count == 0 || command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A write-off needs positive allocations.");

        Guid id = command.Id ?? Guid.NewGuid();
        WriteOff? known = command.Id is not null ? await payments.GetWriteOffAsync(clientId, id, ct) : null;
        bool replayed = known is not null;

        WriteOffBody body = new(command.CustomerId, command.Date, command.Memo);

        if (!replayed)
        {
            await ValidateAllocationsAsync(clientId, command.CustomerId, command.Allocations, ct);
            known = await payments.RecordWriteOffAsync(clientId, id, body, ct);
        }

        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeWriteOff(id, body, command.Allocations, posting), ct);
        return (known!, replayed);
    }
```

Replacing `RecordCreditNoteAsync` (lines 331-342):

```csharp
    public async Task<(CreditNote CreditNote, bool Replayed)> RecordCreditNoteAsync(
        Guid clientId, CreditNoteCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Allocations.Count == 0 || command.Allocations.Any(a => a.Amount <= 0m))
            throw new InvalidOperationException("A credit note needs positive allocations.");

        Guid id = command.Id ?? Guid.NewGuid();
        CreditNote? known = command.Id is not null ? await payments.GetCreditNoteAsync(clientId, id, ct) : null;
        bool replayed = known is not null;

        CreditNoteBody body = new(command.CustomerId, command.Date, command.Memo);

        if (!replayed)
        {
            await ValidateAllocationsAsync(clientId, command.CustomerId, command.Allocations, ct);
            known = await payments.RecordCreditNoteAsync(clientId, id, body, ct);
        }

        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeCreditNote(id, body, command.Allocations, posting), ct);
        return (known!, replayed);
    }
```

Replacing `RecordRefundAsync` (lines 356-367) — note this one takes the body directly, so the key is its own parameter:

```csharp
    public async Task<(Refund Refund, bool Replayed)> RecordRefundAsync(
        Guid clientId, RefundBody body, Guid? retryKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Amount <= 0m) throw new InvalidOperationException("A refund amount must be greater than zero.");

        Guid id = retryKey ?? Guid.NewGuid();
        Refund? known = retryKey is not null ? await payments.GetRefundAsync(clientId, id, ct) : null;
        bool replayed = known is not null;

        if (!replayed)
        {
            // State-dependent: this refund itself consumes the credit it is checked against.
            decimal available = await GetCustomerCreditBalanceAsync(clientId, body.CustomerId, ct);
            if (body.Amount > available)
                throw new InvalidOperationException($"Refund of {body.Amount} exceeds available credit {available}.");
            known = await payments.RecordRefundAsync(clientId, id, body, ct);
        }

        PaymentPostingAccounts posting = await accounts.GetAsync(clientId, ct);
        await ledger.PostAsync(clientId, PaymentPosting.ComposeRefund(id, body, posting), ct);
        return (known!, replayed);
    }
```

- [ ] **Step 6: Return 200 on replay from the three endpoints**

In `ReceivablesEndpoints.cs`, change `RecordWriteOff`, `RecordCreditNote` and `RecordRefund` to destructure the tuple and return `Results.Ok(...)` when replayed else the existing created/ok result. For the refund handler, pass `request.Id` as the new `retryKey` argument. Preserve each handler's existing `catch` blocks and status codes exactly.

- [ ] **Step 7: Update fakes and fix call sites**

Run: `dotnet build Modules/Receivables/Accounting101.Receivables.Tests` and fix breaks.

- [ ] **Step 8: Run the tests**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Api/ReceivablesRequests.cs Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs Modules/Receivables/Accounting101.Receivables/PaymentService.cs Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs Modules/Receivables/Accounting101.Receivables.Tests/DispositionRetryKeyTests.cs
git commit -m "feat(receivables): retry keys on write-offs, credit notes and refunds"
```

---

### Task 6: Banking.Cash — disbursement and deposit

**Files:**
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashRequests.cs:6-19`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashEndpoints.cs` (`RecordDisbursement`, `RecordDeposit`)
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs:17-28`, `:61-72`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDisbursementStore.cs:15-23`, `DocumentCashDepositStore.cs:15-23` + `CashPorts.cs`
- Modify: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs`, `CashPagingTests.cs`
- Test: `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashRetryKeyTests.cs` (create)

**Interfaces:**
- Consumes: `IDocumentStore.CreateWithIdAsync` (Task 1).
- Produces: `CashService.RecordDisbursementAsync(Guid clientId, CashDisbursementBody body, Guid? retryKey = null, CancellationToken ct = default)` → `Task<(CashDisbursement Disbursement, bool Replayed)>`; `RecordDepositAsync` the same shape with `CashDeposit`. `ICashDisbursementStore.RecordAsync(Guid clientId, Guid id, CashDisbursementBody body, CancellationToken ct = default)`; `ICashDepositStore.RecordAsync` likewise.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashRetryKeyTests.cs` — the three-case matrix for `/cash-disbursements` and `/cash-deposits`, six tests. Model fixture usage on `CashE2eTests.cs`. Divergence is a different line amount.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests --filter FullyQualifiedName~CashRetryKeyTests`
Expected: FAIL to compile.

- [ ] **Step 3: Add the optional Id to both requests**

`CashRequests.cs`, replacing lines 6-19:

```csharp
public sealed record RecordCashDisbursementRequest(
    IReadOnlyList<CashLineRequest> Lines,
    DateOnly Date,
    string? Reference,
    string? Memo,
    Guid? Id = null);

/// <summary>Record a cash deposit (owner contribution, loan proceeds, other non-invoice cash in).
/// The clerk supplies the non-cash lines; the module derives the balancing Cash debit.
/// Number/Status are server-assigned. <paramref name="Id"/> is an optional caller-supplied retry key —
/// same key and same content replays, different content is a 422.</summary>
public sealed record RecordCashDepositRequest(
    IReadOnlyList<CashLineRequest> Lines,
    DateOnly Date,
    string? Reference,
    string? Memo,
    Guid? Id = null);
```

Keep the disbursement record's existing doc-comment above it, extending it with the same `<paramref name="Id"/>` sentence.

- [ ] **Step 4: Thread the id through both stores**

`DocumentCashDisbursementStore.cs`, replacing `RecordAsync` (lines 15-23):

```csharp
    public async Task<CashDisbursement> RecordAsync(Guid clientId, Guid id, CashDisbursementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Evidentiary: create then finalize to assign the sequence number — but only the caller that
        // actually created it finalizes, so a replay burns no number and cannot race a concurrent creator.
        if (await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct))
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<CashDisbursementBody>? result = await documents.GetAsync<CashDisbursementBody>(clientId, Collection, id, ct);
        return Map(result!);
    }
```

Apply the identical change to `DocumentCashDepositStore.cs` with `CashDepositBody`. Update both signatures in `CashPorts.cs`.

- [ ] **Step 5: Add the replay branch to both service methods**

`CashService.cs`, replacing `RecordDisbursementAsync` (lines 17-28):

```csharp
    public async Task<(CashDisbursement Disbursement, bool Replayed)> RecordDisbursementAsync(
        Guid clientId, CashDisbursementBody body, Guid? retryKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        Guid id = retryKey ?? Guid.NewGuid();
        CashDisbursement? known = retryKey is not null ? await disbursements.GetAsync(clientId, id, ct) : null;
        bool replayed = known is not null;

        // 1. Persist the evidentiary document (finalized immediately — no draft phase).
        if (!replayed)
            known = await disbursements.RecordAsync(clientId, id, body, ct);

        // 2. Compose from the request body and post on both branches — the ledger arbitrates a replay
        //    versus a key reused for different content, and a retry repairs a post that never landed.
        CashPostingAccounts postingAccounts = await accounts.GetAccountsAsync(clientId, ct);
        PostEntryRequest entry = CashPosting.ComposeDisbursement(id, body, postingAccounts);
        // 3. Post PendingApproval — the module never approves its own entries.
        await ledger.PostAsync(clientId, entry, ct);
        return (known!, replayed);
    }
```

Apply the identical change to `RecordDepositAsync` (lines 61-72) using `deposits`, `CashDeposit`, `CashPosting.ComposeDeposit`.

- [ ] **Step 6: Return 200 on replay from both endpoints**

In `CashEndpoints.cs`, change `RecordDisbursement` and `RecordDeposit` to destructure the tuple, pass `request.Id` as `retryKey`, and return `Results.Ok(...)` when replayed else the existing `Results.Created(...)`. Preserve existing catch blocks.

- [ ] **Step 7: Update fakes and fix call sites**

Run: `dotnet build Modules/Banking/Cash/Accounting101.Banking.Cash.Tests` and fix breaks in `Fakes.cs` and `CashPagingTests.cs` (both implement or call the store interfaces).

- [ ] **Step 8: Run the tests**

Run: `dotnet test Modules/Banking/Cash/Accounting101.Banking.Cash.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashRequests.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashEndpoints.cs Modules/Banking/Cash/Accounting101.Banking.Cash/CashService.cs Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDisbursementStore.cs Modules/Banking/Cash/Accounting101.Banking.Cash/DocumentCashDepositStore.cs Modules/Banking/Cash/Accounting101.Banking.Cash/CashPorts.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/Fakes.cs Modules/Banking/Cash/Accounting101.Banking.Cash.Tests/CashRetryKeyTests.cs
git commit -m "feat(cash): retry keys on cash disbursements and deposits"
```

---

### Task 7: Payroll — run and tax remittance

**Files:**
- Modify: `Modules/Payroll/Accounting101.Payroll.Api/PayrollRequests.cs:5`, `:16`
- Modify: `Modules/Payroll/Accounting101.Payroll.Api/PayrollEndpoints.cs` (`RecordRun`, `RecordRemittance`)
- Modify: the payroll service's record methods
- Modify: `Modules/Payroll/Accounting101.Payroll/DocumentPayrollRunStore.cs:16-24`, `DocumentTaxRemittanceStore.cs:16-24` + their port interfaces
- Modify: `Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs`, `PayrollPagingTests.cs`
- Test: `Modules/Payroll/Accounting101.Payroll.Tests/PayrollRetryKeyTests.cs` (create)

**Interfaces:**
- Consumes: `IDocumentStore.CreateWithIdAsync` (Task 1).
- Produces: the two payroll record service methods return `Task<(PayrollRun Run, bool Replayed)>` and `Task<(TaxRemittance Remittance, bool Replayed)>`; `IPayrollRunStore.RecordAsync(Guid clientId, Guid id, PayrollRunBody body, CancellationToken ct = default)` and `ITaxRemittanceStore.RecordAsync(Guid clientId, Guid id, TaxRemittanceBody body, CancellationToken ct = default)`.

**This is the highest-value task in the plan** — payroll is the largest single money-mover in the application and has no duplicate guard of any kind today.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Payroll/Accounting101.Payroll.Tests/PayrollRetryKeyTests.cs` — the three-case matrix for `/payroll-runs` and `/tax-remittances`, six tests. Model fixture usage on the existing payroll E2E tests in that folder. Divergence is a different gross/amount.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests --filter FullyQualifiedName~PayrollRetryKeyTests`
Expected: FAIL to compile.

- [ ] **Step 3: Add the optional Id to both requests**

In `PayrollRequests.cs`, add a trailing `Guid? Id = null` parameter to both `RecordPayrollRunRequest` and `RecordTaxRemittanceRequest`, each with a doc-comment sentence: *"`Id` is an optional caller-supplied retry key — same key and same content replays, different content is a 422."* Read the current records first; both are multi-line, so append the parameter to the existing list rather than rewriting them.

- [ ] **Step 4: Thread the id through both stores**

`DocumentPayrollRunStore.cs`, replacing `RecordAsync` (lines 16-24):

```csharp
    public async Task<PayrollRun> RecordAsync(Guid clientId, Guid id, PayrollRunBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Only the creating caller finalizes — a replay burns no sequence number and cannot race.
        if (await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct))
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<PayrollRunBody>? result = await documents.GetAsync<PayrollRunBody>(clientId, Collection, id, ct);
        return Map(result!);
    }
```

Apply the identical change to `DocumentTaxRemittanceStore.cs` with `TaxRemittanceBody`. Update both port interfaces.

- [ ] **Step 5: Add the replay branch to both service methods**

Read the two record methods in the payroll service first. Apply the Task 6 shape exactly:

1. `Guid id = retryKey ?? Guid.NewGuid();`
2. `known = retryKey is not null ? await runs.GetAsync(clientId, id, ct) : null;` and `bool replayed = known is not null;`
3. stateless guards stay above the branch; anything reading a balance or prior state moves inside `if (!replayed)`
4. `known = await runs.RecordAsync(clientId, id, body, ct);` inside the branch
5. compose from the request body with `id`, post on both branches
6. `return (known!, replayed);`

- [ ] **Step 6: Return 200 on replay from both endpoints**

In `PayrollEndpoints.cs`, change `RecordRun` and `RecordRemittance` to destructure the tuple, pass `request.Id` through, and return `Results.Ok(...)` when replayed else the existing created result. Preserve existing catch blocks.

- [ ] **Step 7: Update fakes and fix call sites**

Run: `dotnet build Modules/Payroll/Accounting101.Payroll.Tests` and fix breaks in `Fakes.cs` and `PayrollPagingTests.cs`.

- [ ] **Step 8: Run the tests**

Run: `dotnet test Modules/Payroll/Accounting101.Payroll.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Modules/Payroll/Accounting101.Payroll.Api/PayrollRequests.cs Modules/Payroll/Accounting101.Payroll.Api/PayrollEndpoints.cs Modules/Payroll/Accounting101.Payroll/DocumentPayrollRunStore.cs Modules/Payroll/Accounting101.Payroll/DocumentTaxRemittanceStore.cs Modules/Payroll/Accounting101.Payroll.Tests/Fakes.cs Modules/Payroll/Accounting101.Payroll.Tests/PayrollRetryKeyTests.cs
git commit -m "feat(payroll): retry keys on payroll runs and tax remittances"
```

Also stage the payroll service and port-interface files you changed.

---

### Task 8: Banking.Reconciliation — bank adjustment

**Files:**
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/AdjustmentRequests.cs:5`
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs:29` (`RecordAdjustment`)
- Modify: the adjustment service's record method
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/DocumentBankAdjustmentStore.cs:12-20` + its port interface
- Modify: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs`
- Test: `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AdjustmentRetryKeyTests.cs` (create)

**Interfaces:**
- Consumes: `IDocumentStore.CreateWithIdAsync` (Task 1).
- Produces: the adjustment record service method returns `Task<(BankAdjustment Adjustment, bool Replayed)>`; `IBankAdjustmentStore.RecordAsync(Guid clientId, Guid id, BankAdjustmentBody body, CancellationToken ct = default)`.

**Naming trap:** the route is `POST /reconciliations/{id}/adjustments` and that `id` is the **reconciliation's**. The new body field is the **adjustment's** own id. Do not let the handler's parameters shadow each other — rename the route parameter binding in the handler signature (e.g. `Guid reconciliationId` bound from `{id}`) or name the body field access `request.Id` explicitly at the call site and never introduce a bare local `id`.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AdjustmentRetryKeyTests.cs` — the three-case matrix for `POST /reconciliations/{reconciliationId}/adjustments`. Add a fourth test pinning the trap: the same retry key posted under **two different reconciliations** must not collide — the second is a normal create, because it is a different adjustment. (It will collide on document id if implemented wrong, which is exactly what this test catches.) Assert the second returns `Created` and the two adjustments have different ids... unless the same key is genuinely reused, in which case document-id collision is correct and the test should instead assert the caller gets a 422 for divergent content. **Write it the first way** — generate a fresh key for the second reconciliation — so the test pins that the reconciliation id does not leak into the document id.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests --filter FullyQualifiedName~AdjustmentRetryKeyTests`
Expected: FAIL to compile.

- [ ] **Step 3: Add the optional Id**

`AdjustmentRequests.cs`, replacing line 5:

```csharp
/// <summary>Record a bank adjustment against a reconciliation. <paramref name="Id"/> is an optional
/// caller-supplied retry key for the adjustment itself — unrelated to the reconciliation id in the
/// route. Same key and same content replays, different content is a 422.</summary>
public sealed record RecordAdjustmentRequest(
    Guid OffsetAccountId, decimal Amount, AdjustmentKind Kind, DateOnly? Date, string? Memo, Guid? Id = null);
```

- [ ] **Step 4: Thread the id through the store**

`DocumentBankAdjustmentStore.cs`, replacing `RecordAsync` (lines 12-20):

```csharp
    public async Task<BankAdjustment> RecordAsync(Guid clientId, Guid id, BankAdjustmentBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct))
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<BankAdjustmentBody>? result = await documents.GetAsync<BankAdjustmentBody>(clientId, Collection, id, ct);
        return Map(result!);
    }
```

Update the port interface to match.

- [ ] **Step 5: Add the replay branch to the service**

Read the adjustment record method first, then apply the Task 6 shape: `id` from the retry key or a fresh Guid, an existence check gated on the key being supplied, the state-dependent work (anything that reads the reconciliation's state or the cash balance) inside `if (!replayed)`, compose from the request with `id`, post on both branches, return the tuple.

- [ ] **Step 6: Return 200 on replay from the endpoint**

In `ReconciliationEndpoints.cs`, change `RecordAdjustment` to destructure the tuple and return `Results.Ok(...)` when replayed. Mind the naming trap above.

- [ ] **Step 7: Update the fake and fix call sites**

Run: `dotnet build Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests` and fix breaks.

- [ ] **Step 8: Run the tests**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/AdjustmentRequests.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation/DocumentBankAdjustmentStore.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Fakes.cs Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/AdjustmentRetryKeyTests.cs
git commit -m "feat(reconciliation): retry key on bank adjustments"
```

Also stage the service and port-interface files you changed.

---

### Task 9: Inventory — stock movement

**Files:**
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/InventoryRequests.cs:14` (`RecordMovementRequest`) and its `ToRequest()` mapper
- Modify: `Modules/Inventory/Accounting101.Inventory.Api/InventoryEndpoints.cs:30-50`
- Modify: `Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs:20+` and the `RecordMovement` domain request type
- Modify: `Modules/Inventory/Accounting101.Inventory/DocumentStockMovementStore.cs:12-20` + `InventoryPorts.cs`
- Modify: `Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs`
- Test: `Modules/Inventory/Accounting101.Inventory.Tests/MovementRetryKeyTests.cs` (create)

**Interfaces:**
- Consumes: `IDocumentStore.CreateWithIdAsync` (Task 1).
- Produces: `InventoryMovementService.RecordAsync(Guid clientId, RecordMovement request, CancellationToken ct = default)` returns `Task<(StockMovement Movement, bool Replayed)>`; `IStockMovementStore.RecordAsync(Guid clientId, Guid id, StockMovementBody body, CancellationToken ct = default)`.

**This task carries the most risk in the plan.** `InventoryMovementService.RecordAsync` does more than persist-then-post: it resolves the item, folds the current valuation, and computes a `MovementEffect` whose derived extended cost feeds the posting. A replay must skip all of it.

**Correction, established during implementation (2026-07-25):** an earlier draft of this task and of the spec said `RecordAsync` "mutates the item's stored valuation." **It does not, and has not since the ledger-first cut-over.** `ItemBody` carries only Sku/Name/Description/UnitOfMeasure — no valuation fields — and the method only ever reads (`items.GetAsync`, `valuation.GetAsync`); there is no `items.UpdateAsync` call on this path. On-hand and value are derived on read from the ledger fold plus the movement projection.

The risk is just as real but differently shaped: the double-count vector is a **second movement document plus a second posted entry**, both of which feed that derived fold. The retry key prevents both, so the mitigation is unchanged — only the mechanism description was wrong. The ledger's divergent-content 422 still cannot catch a duplicate here, because the composed entry would be identical.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Inventory/Accounting101.Inventory.Tests/MovementRetryKeyTests.cs` — the three-case matrix for `POST /movements`, plus this one, which is the whole reason this task is separate:

```csharp
[Fact]
public async Task A_replayed_movement_does_not_double_count_the_valuation()
{
    // Arrange: an item with a known on-hand and value, then one receipt of 10 units.
    // Act: POST the same receipt twice with the same retry key.
    // Assert: on-hand and total value reflect ONE receipt, not two — read them back from the
    // item/valuation endpoint, not from the movement list.
}
```

Fill that body in using the fixture and valuation-read helpers the existing inventory tests already use (read `Modules/Inventory/Accounting101.Inventory.Tests/` first). The assertion must read the *valuation*, because the movement-list assertion alone would pass even with the bug.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests --filter FullyQualifiedName~MovementRetryKeyTests`
Expected: FAIL to compile.

- [ ] **Step 3: Add the optional Id**

Add a trailing `Guid? Id = null` to `RecordMovementRequest` in `InventoryRequests.cs` with the standard doc sentence, add the same to the domain `RecordMovement` type, and thread it through the existing `ToRequest()` mapper.

- [ ] **Step 4: Thread the id through the store**

`DocumentStockMovementStore.cs`, replacing `RecordAsync` (lines 12-20):

```csharp
    public async Task<StockMovement> RecordAsync(Guid clientId, Guid id, StockMovementBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await documents.CreateWithIdAsync(clientId, Collection, id, body, Tags(), ct))
            await documents.FinalizeAsync(clientId, Collection, id, ct);
        DocumentResult<StockMovementBody>? result = await documents.GetAsync<StockMovementBody>(clientId, Collection, id, ct);
        return Map(result!);
    }
```

Update `InventoryPorts.cs` to match.

- [ ] **Step 5: Add the replay branch to the service**

In `InventoryMovementService.RecordAsync`, insert the existence check immediately after `ValidateShape(request)` — which is stateless and stays on both branches — and put the state-dependent work inside the `if (!replayed)` block: the item resolve, the active-status check, the valuation fold, the `MovementEffect` computation, and the extended-cost check.

Two adjustments established during implementation, both deviations from this task's first draft:

- **The account resolve must be hoisted ABOVE the branch, not placed inside it.** Composing the entry on both branches needs it, so it is data acquisition rather than a guard — the same distinction settled in Task 8. Cost: a client that is both misconfigured *and* naming an unknown item now gets 409 instead of 404. Untested territory either way.
- **On the replay branch the extended cost comes from the STORED movement, not from a recomputation.** Extended cost is *derived* from the weighted-average fold, and on a replay that fold already contains this movement — so recomputing yields a different cost, or a zero cost on an issue-to-zero (spurious 422), or a spurious block-negative 409. This is a deliberate, scoped exception to "compose from the request": every *other* field of the entry still comes from the request, so date/type/item divergence is still arbitrated by the ledger. The accepted consequence is that a same-key retry differing **only** in quantity or unit cost replays instead of returning 422.

```csharp
        ValidateShape(request);   // stateless — both branches

        Guid id = request.Id ?? Guid.NewGuid();
        StockMovement? known = request.Id is not null ? await movements.GetAsync(clientId, id, ct) : null;
        bool replayed = known is not null;

        if (!replayed)
        {
            // ... the entire existing body from the item resolve through the store write and the
            //     item-valuation mutation, unchanged except that the store write now passes `id`
            //     and the composed entry uses `id` instead of the store-returned movement id ...
        }
```

Then compose the entry from the request and post it on both branches, and `return (known!, replayed);`.

Read the full current method body before editing — it is longer than the excerpt above and every state-dependent line must end up inside the branch.

- [ ] **Step 6: Return 200 on replay from the endpoint**

`InventoryEndpoints.cs`, replacing `RecordMovement` (lines 30-50):

```csharp
    private static async Task<IResult> RecordMovement(
        Guid clientId, RecordMovementRequest request, InventoryMovementService service, CancellationToken cancellationToken)
    {
        try
        {
            (StockMovement movement, bool replayed) = await service.RecordAsync(clientId, request.ToRequest(), cancellationToken);
            return replayed
                ? Results.Ok(new StockMovementView(movement))
                : Results.Created($"/clients/{clientId}/movements/{movement.Id}", new StockMovementView(movement));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
```

- [ ] **Step 7: Update the fake and fix call sites**

Run: `dotnet build Modules/Inventory/Accounting101.Inventory.Tests` and fix breaks.

- [ ] **Step 8: Run the tests**

Run: `dotnet test Modules/Inventory/Accounting101.Inventory.Tests`
Expected: PASS, especially the valuation double-count test.

- [ ] **Step 9: Commit**

```bash
git add Modules/Inventory/Accounting101.Inventory.Api/InventoryRequests.cs Modules/Inventory/Accounting101.Inventory.Api/InventoryEndpoints.cs Modules/Inventory/Accounting101.Inventory/InventoryMovementService.cs Modules/Inventory/Accounting101.Inventory/DocumentStockMovementStore.cs Modules/Inventory/Accounting101.Inventory/InventoryPorts.cs Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs Modules/Inventory/Accounting101.Inventory.Tests/MovementRetryKeyTests.cs
git commit -m "feat(inventory): retry key on stock movements"
```

---

### Task 10: The behaviours that are not per-endpoint

Four properties the three-case matrix does not reach. All on the Payables path, which is now the reference implementation.

**Files:**
- Test: `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRetryKeyDepthTests.cs` (create)

**Interfaces:**
- Consumes: everything from Tasks 1–2. Produces nothing.

- [ ] **Step 1: Write the failing tests**

Create `Modules/Payables/Accounting101.Payables.Tests/BillPaymentRetryKeyDepthTests.cs`:

```csharp
namespace Accounting101.Payables.Tests;

/// <summary>The retry-key behaviours that are properties of the mechanism rather than of any one
/// endpoint: orphan repair, replay against a voided document, concurrency, and the promise that a
/// replay burns no gapless sequence number.</summary>
public sealed class BillPaymentRetryKeyDepthTests
{
    [Fact]
    public async Task A_retry_after_a_failed_ledger_post_completes_the_entry_and_leaves_one_document()
    {
        // Use the in-process service with a fake ILedgerClient that throws on its FIRST PostAsync and
        // succeeds afterwards. Call RecordPaymentAsync twice with the same command (same Id).
        // Assert: the first call throws; the second returns Replayed == true; the fake recorded exactly
        // two post attempts and one succeeded; the store holds exactly one payment under that id.
    }

    [Fact]
    public async Task A_replayed_key_on_a_voided_payment_returns_the_voided_payment_and_creates_nothing()
    {
        // Record with a key, void the payment, then POST the same key + same content again.
        // Assert: 200; the returned payment has Voided == true; the vendor still has exactly one payment.
    }

    [Fact]
    public async Task Two_concurrent_posts_with_the_same_key_yield_one_document_and_one_entry()
    {
        // Fire both POSTs with Task.WhenAll on the same HttpClient-equivalent, same key, same content.
        // Assert: exactly one is 201 and one is 200 (order-independent); the vendor has one payment;
        // GetEntriesBySourceRefAsync returns one non-reversal entry.
    }

    [Fact]
    public async Task A_replay_burns_no_gapless_sequence_number()
    {
        // Record payment A with key K. Replay K. Then record payment B with a fresh key.
        // Assert: B's Number is A's Number + 1 — the replay consumed nothing.
    }
}
```

Fill each body in. For the first, add a fake `ILedgerClient` to `Fakes.cs` (or a local nested class) that counts `PostAsync` calls and throws `new LedgerClientException(500, "boom")` on the first. For the last, read `Number` off `BillPayment` — confirm the property name against the `BillPayment` record before writing the assertion.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter FullyQualifiedName~BillPaymentRetryKeyDepthTests`
Expected: FAIL — each for its own reason. If any of these passes before you have written its body, the test is not asserting anything.

- [ ] **Step 3: Make them pass**

No production change should be needed — Tasks 1 and 2 already implement all four behaviours. **If a test fails, that is a real defect in Task 2's implementation, not a reason to weaken the test.** Fix the implementation.

The one exception: if the concurrency test is flaky against the test Mongo, do not paper over it with a retry loop or a sleep — the whole point is that `_id` uniqueness is the arbiter. Investigate whether `CreateWithIdAsync` is genuinely reaching `InsertOneAsync`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests`
Expected: PASS.

- [ ] **Step 5: Run the whole backend suite**

Run: `dotnet test`
Expected: PASS. All six modules plus the engine.

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Tests/BillPaymentRetryKeyDepthTests.cs Modules/Payables/Accounting101.Payables.Tests/Fakes.cs
git commit -m "test(payables): orphan repair, voided replay, concurrency, no burned sequence"
```

---

### Task 11: Angular — the shared duplicate-key message

**Files:**
- Modify: `UI/Angular/src/app/core/api/problem-details.ts`
- Test: `UI/Angular/src/app/core/api/problem-details.spec.ts` (create if absent, else extend)

**Interfaces:**
- Consumes: the 422 detail string pinned by Task 2's test.
- Produces: `export function retryKeyConflictMessage(err: unknown): string | null` — returns operator-facing copy when the error is a divergent-retry-key 422, and `null` otherwise, so callers fall through to `extractProblem(err).detail`.

- [ ] **Step 1: Write the failing test**

In `UI/Angular/src/app/core/api/problem-details.spec.ts`:

```ts
import { HttpErrorResponse } from '@angular/common/http';
import { retryKeyConflictMessage } from './problem-details';

describe('retryKeyConflictMessage', () => {
  it('recognises the ledger\'s divergent-retry-key 422', () => {
    const err = new HttpErrorResponse({
      status: 422,
      error: { detail: 'An entry with this id already exists with different content.' },
    });

    expect(retryKeyConflictMessage(err))
      .toBe('This was already recorded, and differs from what you just submitted. Open the list and check it before retrying.');
  });

  it('returns null for an ordinary 422 so the caller shows the real message', () => {
    const err = new HttpErrorResponse({
      status: 422,
      error: { detail: 'A payment amount must be greater than zero.' },
    });

    expect(retryKeyConflictMessage(err)).toBeNull();
  });

  it('returns null for a non-422', () => {
    const err = new HttpErrorResponse({ status: 409, error: { detail: 'Period is closed.' } });
    expect(retryKeyConflictMessage(err)).toBeNull();
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/problem-details.spec.ts'`
Expected: FAIL — `retryKeyConflictMessage` is not exported.

- [ ] **Step 3: Implement it**

Append to `UI/Angular/src/app/core/api/problem-details.ts`:

```ts
/**
 * The ledger's answer when a retry key is reused for different financial content. Matching on the
 * engine's exact wording is a deliberate contract, pinned by a backend test
 * (Payables BillPaymentRetryKeyTests.The_same_key_with_different_content_is_a_422) so a reworded
 * engine message breaks the build rather than silently leaking jargon onto an operator's screen.
 *
 * Returns null for anything else, so callers fall through to extractProblem(err).detail.
 */
const DIVERGENT_RETRY_KEY = 'An entry with this id already exists with different content.';

export function retryKeyConflictMessage(err: unknown): string | null {
  const problem = extractProblem(err);
  return problem.status === 422 && problem.detail === DIVERGENT_RETRY_KEY
    ? 'This was already recorded, and differs from what you just submitted. Open the list and check it before retrying.'
    : null;
}
```

Read the existing `Problem` type first — if it does not carry `status`, add it there, or narrow on `err instanceof HttpErrorResponse && err.status === 422` instead. Do not change `extractProblem`'s existing behaviour.

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/problem-details.spec.ts'`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/api/problem-details.ts UI/Angular/src/app/core/api/problem-details.spec.ts
git commit -m "feat(ui): shared message for a reused retry key"
```

---

### Task 12: Angular — Payables editors

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.service.ts:121`, `:143` (request interfaces)
- Modify: `UI/Angular/src/app/features/payables/bill-payment-editor.ts:148-159`
- Modify: `UI/Angular/src/app/features/payables/vendor-credit-apply-editor.ts:134`
- Test: the matching `.spec.ts` for each editor

**Interfaces:**
- Consumes: `retryKeyConflictMessage` (Task 11); the backend `Id` field (Tasks 2–3).
- Produces: the editor pattern every later Angular task repeats.

- [ ] **Step 1: Write the failing tests**

In `bill-payment-editor.spec.ts` (create if absent, following a neighbouring editor spec's setup):

```ts
it('sends a retry key with the payment', () => {
  const spy = spyOn(svc, 'recordBillPayment').and.returnValue(of({} as BillPayment));

  component.save();

  expect(spy.calls.mostRecent().args[0].id).toMatch(
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
});

it('reuses the same key when a failed save is retried', () => {
  const spy = spyOn(svc, 'recordBillPayment').and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));

  component.save();
  component.save();

  expect(spy.calls.argsFor(0)[0].id).toBe(spy.calls.argsFor(1)[0].id);
});

it('mints a fresh key after a successful save', () => {
  const spy = spyOn(svc, 'recordBillPayment').and.returnValue(of({} as BillPayment));

  component.save();
  component.save();

  expect(spy.calls.argsFor(0)[0].id).not.toBe(spy.calls.argsFor(1)[0].id);
});
```

Add the same three to `vendor-credit-apply-editor.spec.ts` against `applyVendorCredit`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/payables/*.spec.ts'`
Expected: FAIL — `id` is `undefined` on the request.

- [ ] **Step 3: Add `id` to the request interfaces**

In `UI/Angular/src/app/core/payables/payables.service.ts`, add `readonly id?: string;` to the request interfaces used by `recordBillPayment` and `applyVendorCredit`. Do not change the method bodies — they already forward the whole object.

- [ ] **Step 4: Mint and re-mint the key in the editors**

In `bill-payment-editor.ts`, add the field and thread it through `save()`:

```ts
  // A caller-supplied retry key: the same key across retries of a FAILED save makes the server replay
  // instead of paying the vendor twice. Re-minted after a success so the next payment is a new operation.
  private retryKey = crypto.randomUUID();

  save(): void {
    if (!this.valid() || !this.vendorId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordBillPayment({
      vendorId: this.vendorId, date: this.date(), amount: this.amount(),
      method: this.method().trim() || null,
      allocations: this.rows().filter(r => r.allocation > 0).map(r => ({ targetId: r.billId, amount: r.allocation })),
      id: this.retryKey,
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.retryKey = crypto.randomUUID();
        this.busy.set(false);
        void this.router.navigate(['/payables/payments']);
      },
      error: (e) => {
        this.message.set(retryKeyConflictMessage(e) ?? extractProblem(e).detail);
        this.busy.set(false);
      },
    });
  }
```

Import `retryKeyConflictMessage` alongside the existing `extractProblem` import. Apply the identical pattern to `vendor-credit-apply-editor.ts:134`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/payables/*.spec.ts'`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/payables/payables.service.ts UI/Angular/src/app/features/payables/bill-payment-editor.ts UI/Angular/src/app/features/payables/bill-payment-editor.spec.ts UI/Angular/src/app/features/payables/vendor-credit-apply-editor.ts UI/Angular/src/app/features/payables/vendor-credit-apply-editor.spec.ts
git commit -m "feat(ui): retry keys on the payables editors"
```

---

### Task 13: Angular — Receivables editors

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.service.ts:101`, `:118`, `:122`, `:126`, `:139` (request interfaces)
- Modify: `UI/Angular/src/app/features/receivables/payment-editor.ts:151`
- Modify: `UI/Angular/src/app/features/receivables/adjustment-editor.ts:201`, `:204`, `:207`
- Modify: `UI/Angular/src/app/features/receivables/refund-editor.ts:83`
- Test: the matching `.spec.ts` for each

**Interfaces:**
- Consumes: `retryKeyConflictMessage` (Task 11); the Task 12 editor pattern.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

Add the same three tests from Task 12 Step 1 to `payment-editor.spec.ts` (against `recordPayment`) and `refund-editor.spec.ts` (against `recordRefund`).

For `adjustment-editor.spec.ts`, which posts one of three endpoints depending on mode, add those three tests **plus**:

```ts
it('sends the same key whichever mode is active, since the three write to different collections', () => {
  const noteSpy = spyOn(svc, 'recordCreditNote').and.returnValue(of({}));
  component.mode.set('credit-note');
  component.save();
  expect(noteSpy.calls.mostRecent().args[0].id).toBeTruthy();
});
```

Read `adjustment-editor.ts` first to get the real mode signal name and values — the three branches are at lines 201, 204 and 207.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/receivables/*.spec.ts'`
Expected: FAIL.

- [ ] **Step 3: Add `id` to the five request interfaces**

In `UI/Angular/src/app/core/receivables/receivables.service.ts`, add `readonly id?: string;` to the request interfaces behind `recordPayment`, `recordCreditNote`, `recordWriteOff`, `applyCredit` and `recordRefund`.

- [ ] **Step 4: Mint and re-mint in the four editors**

Apply the Task 12 Step 4 pattern to each: a `private retryKey = crypto.randomUUID();` field, `id: this.retryKey` in the request, `this.retryKey = crypto.randomUUID();` as the first statement of the success handler, and `retryKeyConflictMessage(e) ?? extractProblem(e).detail` in the error handler.

In `adjustment-editor.ts` all three branches send the same `this.retryKey`, and the single shared `done` handler re-mints once.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/receivables/*.spec.ts'`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/receivables/receivables.service.ts UI/Angular/src/app/features/receivables/payment-editor.ts UI/Angular/src/app/features/receivables/payment-editor.spec.ts UI/Angular/src/app/features/receivables/adjustment-editor.ts UI/Angular/src/app/features/receivables/adjustment-editor.spec.ts UI/Angular/src/app/features/receivables/refund-editor.ts UI/Angular/src/app/features/receivables/refund-editor.spec.ts
git commit -m "feat(ui): retry keys on the receivables editors"
```

---

### Task 14: Angular — Banking editors

**Files:**
- Modify: `UI/Angular/src/app/core/banking/banking.service.ts:40`, `:44`, `:173` (request interfaces)
- Modify: `UI/Angular/src/app/features/banking/cash-voucher-editor.ts:122`
- Modify: `UI/Angular/src/app/features/banking/adjustments-panel.ts:128`
- Test: the matching `.spec.ts` for each

**Interfaces:**
- Consumes: `retryKeyConflictMessage` (Task 11); the Task 12 editor pattern.
- Produces: nothing later tasks depend on.

**`adjustments-panel.ts` is the reason the re-mint rule exists.** It does not navigate away on success — it stays open and can record several adjustments in a row. Without the re-mint, the second adjustment would replay the first and silently return it. Its "fresh key after success" test is the load-bearing one in this task.

- [ ] **Step 1: Write the failing tests**

Add the three Task 12 tests to `cash-voucher-editor.spec.ts` (against `recordDisbursement`, and a fourth asserting the deposit branch also sends a key) and to `adjustments-panel.spec.ts` (against `recordAdjustment`).

For the panel, make the re-mint test explicit about why it matters:

```ts
it('mints a fresh key after each successful adjustment, so a second adjustment is not a replay of the first', () => {
  const spy = spyOn(svc, 'recordAdjustment').and.returnValue(of({} as BankAdjustment));

  component.save();
  component.save();

  expect(spy.calls.argsFor(0)[1].id).not.toBe(spy.calls.argsFor(1)[1].id);
});
```

Note `argsFor(...)[1]` — `recordAdjustment(reconciliationId, req)` takes the request second. Confirm against `banking.service.ts:173`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/banking/*.spec.ts'`
Expected: FAIL.

- [ ] **Step 3: Add `id` to the three request interfaces**

In `UI/Angular/src/app/core/banking/banking.service.ts`, add `readonly id?: string;` to the request interfaces behind `recordDisbursement`, `recordDeposit` and `recordAdjustment`.

- [ ] **Step 4: Mint and re-mint in both components**

Apply the Task 12 Step 4 pattern. In `cash-voucher-editor.ts:122` the `call` ternary chooses deposit or disbursement — build the request once via `this.toRequest()` and spread the key in, so both branches carry it:

```ts
    const req = { ...this.toRequest(), id: this.retryKey };
    const call = this.isDeposit() ? this.svc.recordDeposit(req) : this.svc.recordDisbursement(req);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/banking/*.spec.ts'`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/core/banking/banking.service.ts UI/Angular/src/app/features/banking/cash-voucher-editor.ts UI/Angular/src/app/features/banking/cash-voucher-editor.spec.ts UI/Angular/src/app/features/banking/adjustments-panel.ts UI/Angular/src/app/features/banking/adjustments-panel.spec.ts
git commit -m "feat(ui): retry keys on the cash voucher editor and adjustments panel"
```

---

### Task 15: Angular — Payroll and Inventory editors, and the full sweep

**Files:**
- Modify: `UI/Angular/src/app/core/payroll/payroll.service.ts:40`, `:61`; `UI/Angular/src/app/core/inventory/inventory.service.ts:59`
- Modify: `UI/Angular/src/app/features/payroll/run-editor.ts:97`, `remittance-editor.ts:72`, `UI/Angular/src/app/features/inventory/movement-editor.ts:139`
- Test: the matching `.spec.ts` for each

**Interfaces:**
- Consumes: `retryKeyConflictMessage` (Task 11); the Task 12 editor pattern.
- Produces: nothing.

- [ ] **Step 1: Write the failing tests**

Add the three Task 12 tests to `run-editor.spec.ts` (against `recordRun`), `remittance-editor.spec.ts` (against `recordRemittance`) and `movement-editor.spec.ts` (against `recordMovement`).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/payroll/*.spec.ts' --include='**/inventory/*.spec.ts'`
Expected: FAIL.

- [ ] **Step 3: Add `id` to the three request interfaces**

`readonly id?: string;` on the request interfaces behind `recordRun`, `recordRemittance` and `recordMovement`.

- [ ] **Step 4: Mint and re-mint in the three editors**

Apply the Task 12 Step 4 pattern to all three.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd UI/Angular && npm test -- --watch=false --include='**/payroll/*.spec.ts' --include='**/inventory/*.spec.ts'`
Expected: PASS.

- [ ] **Step 6: Run both full suites**

Run: `cd UI/Angular && npm test -- --watch=false`
Expected: PASS. The 13 pre-existing NG04002 console errors are expected noise, not failures.

Run: `dotnet test` from the repo root.
Expected: PASS.

- [ ] **Step 7: Verify the local dev config was never staged**

Run: `git status --porcelain UI/Angular/src/app/core/api/environment.ts`
Expected: ` M` — modified, unstaged. If it shows `M ` (staged), unstage it with `git restore --staged UI/Angular/src/app/core/api/environment.ts` before committing.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/payroll/payroll.service.ts UI/Angular/src/app/core/inventory/inventory.service.ts UI/Angular/src/app/features/payroll/run-editor.ts UI/Angular/src/app/features/payroll/run-editor.spec.ts UI/Angular/src/app/features/payroll/remittance-editor.ts UI/Angular/src/app/features/payroll/remittance-editor.spec.ts UI/Angular/src/app/features/inventory/movement-editor.ts UI/Angular/src/app/features/inventory/movement-editor.spec.ts
git commit -m "feat(ui): retry keys on the payroll and inventory editors"
```

---

## Manual verification

After Task 15, with the stack running (`.localdev/start.ps1` — note it hardcodes MongoDB 8.2 while only 8.3 is installed, and its dev CORS is pinned to `localhost:4200`):

1. Open **Payables → Record payment**, fill it in, and submit with the network throttled to offline in devtools. The save fails and the button re-enables.
2. Restore the network and click **Record payment** again.
3. Expect **one** payment in the list, not two. Before this work the same sequence produced two.
4. Now change the amount and submit again with the same form still open. Expect the operator-facing message from Task 11, not the ledger's raw wording.
5. Open **Banking → a reconciliation → adjustments**, record two different adjustments back to back without leaving the panel. Expect two distinct adjustments — this is the re-mint rule from Task 14.

## Self-review

**Spec coverage.** Every section of `2026-07-25-retry-keys-design.md` maps to a task: the wire change and store contract → Task 1; the thirteen endpoints → Tasks 2–9 (bill payment, vendor credit application, AR payment, credit application, write-off, credit note, refund, disbursement, deposit, payroll run, tax remittance, bank adjustment, stock movement — thirteen, matching the spec's table row for row); the semantics table → the three-case matrix in each of Tasks 2–9; the four non-per-endpoint behaviours → Task 10; the ten editors and five services → Tasks 11–15; the 422 contract string → pinned in Task 2 Step 1 and consumed in Task 11 Step 3.

**Known limit carried forward.** The spec's accepted limit — a process death between create and finalize leaves a `Draft` document that no retry repairs — is deliberately *not* given a task. It is documented in the store comments written in Tasks 2, 4, 6, 7 and the spec, and the plan adds no repair for it because a repair would reintroduce the double-finalize race.

**Type consistency.** `CreateWithIdAsync` returns `Task<bool>` in Task 1 and is consumed as a bool in the `if (await ...)` of Tasks 2–9. Every module store's `RecordAsync`/`Record*Async` takes `(clientId, id, body, ct)` in that order. Every service returns a named tuple whose second element is `Replayed`, destructured identically in each endpoint. `retryKeyConflictMessage` is defined in Task 11 and called with that exact name in Tasks 12–15.

**Two known softenings, flagged rather than hidden.** Tasks 7, 8 and 9 say "read the current method body first" for the payroll, reconciliation and inventory *services* rather than reproducing them — those three were not read line-by-line while writing this plan, and inventing their bodies would have produced confident-looking wrong code. The transformation to apply is spelled out step by step in each. Task 9 additionally flags exactly which statements must move inside the replay branch, because that is where the real risk sits.
