# Payables Bill Lifecycle — Invoice Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align the vendor-bill lifecycle with the invoice lifecycle — drafts become plain, editable, discardable scratch; `enter` promotes a draft to a new evidentiary bill (new id, numbered) and posts its A/P entry; `enter` preflights the post so a rejection leaves no orphan.

**Architecture:** Mirror the receivables two-tier document model exactly. Add a plain `bill-drafts` collection alongside the existing evidentiary `bills` collection. `DocumentBillStore`/`InMemoryBillStore` gain `UpdateDraftAsync` / `DiscardDraftAsync` / `PromoteDraftAsync` and drop `FinalizeAsync`; `PromoteDraftAsync` creates a new evidentiary document and deletes the draft. `BillService.EnterAsync` is rewritten to promote-then-post (Task 1) and then preflight-before-promote (Task 2). Two new HTTP endpoints (`PUT`/`DELETE` on `/bills/{id}`) and UI edit/discard + enter-navigates-to-new-id round it out. Payments, credit applications, and the vendor 360 are untouched — they only key off entered ids, which never change after enter.

**Tech Stack:** C# 13 / .NET 10 (ASP.NET Core minimal API), xUnit; Angular 22 + TypeScript + Tailwind + Spartan NG (signal forms), `ng test`.

## Global Constraints

- Single-currency USD only; do not introduce FX.
- JSON over the wire is camelCase; C# types stay PascalCase. (Digit-prefixed fields like `D1To30` serialize as `d1To30` — only the leading char is lowercased; guard any new such fields.)
- TDD: write the failing test, run it red, implement, run it green, commit. No placeholder steps.
- Evidentiary collections are append-only (`CreateAsync`/`FinalizeAsync`/`VoidAsync`); plain collections are mutable (`PutAsync`/`DeleteAsync`). Never call `DeleteAsync` on an evidentiary collection.
- A draft (unentered bill) is inert: no journal entry, no settlement. Only `Enter`/`Void`/`Approve` are audit events.
- Duplicate bill entry is a clerk mistake, handled rider-side — do NOT add vendor-reference fuzzy dedup or idempotency keys.
- The posting-layer `EntryIdentity` (UUIDv5 source-ref) is the only structural duplicate protection; it is unchanged.
- Commits: one logical unit per task, conventional-commit messages, end every task `git add` + `git commit` on the working branch.

**Reference spec:** `docs/superpowers/specs/2026-06-30-payables-bill-lifecycle-invoice-parity-design.md`

---

## File Structure

**Backend — `Modules/Payables/Accounting101.Payables/`**
- `PayablesPorts.cs` — rewrite `IBillStore` (drop `FinalizeAsync`; add `UpdateDraftAsync`, `DiscardDraftAsync`, `PromoteDraftAsync`).
- `DocumentBillStore.cs` — full rewrite to two-tier (plain drafts + evidentiary bills), mirror of `DocumentInvoiceStore.cs`.
- `BillService.cs` — add `EditDraftAsync` + `DiscardDraftAsync`; rewrite `EnterAsync` to promote-then-post (Task 1) then preflight (Task 2).
- `ILedgerClient.cs` — add `ValidateAsync` (Task 2 only).

**Backend — `Modules/Payables/Accounting101.Payables.Api/`**
- `PayablesServiceExtensions.cs` — register `manifest.Plain("bill-drafts")`.
- `PayablesEndpoints.cs` — add `PUT /bills/{id}` (EditBill) and `DELETE /bills/{id}` (DiscardBill).
- `HttpLedgerClient.cs` — add `ValidateAsync` impl (Task 2 only).

**Backend tests — `Modules/Payables/Accounting101.Payables.Tests/`**
- `Fakes.cs` — rewrite `InMemoryBillStore` to two-tier; add `ValidateAsync` + `OnValidate` hook to `FakeLedgerClient` (Task 2).
- `PayablesDocumentStoreFixture.cs` — register `.Plain("bill-drafts")` in the test manifest.
- `DocumentBillStoreTests.cs` — rewrite for two-tier.
- `BillServiceTests.cs` — update enter (new id); add edit/discard.
- `BillPaymentServiceTests.cs` — mechanical: `FinalizeAsync` → `PromoteDraftAsync`.
- `BillDraftLifecycleTests.cs` — NEW, mirrors `ReceivablesDraftLifecycleTests`.
- `HttpLedgerClientTests.cs` — add a `ValidateAsync` route test (Task 2).

**UI — `UI/Angular/src/app/`**
- `core/payables/payables.service.ts` — add `editBill`, `discardBill`.
- `features/payables/bill-detail.ts` — `enter()` re-points to the returned entered id; draft case gains Edit + Discard.
- `features/payables/bill-editor.ts` — edit mode (load draft, PUT on save) + Discard action.
- `features/payables/bill-detail.spec.ts`, `bill-editor.spec.ts` — update/add.
- `app.routes.ts` — add `bills/:id/edit`.

---

## Task 1: Two-tier bill store + BillService migration (promote-then-post)

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/PayablesPorts.cs`
- Modify: `Modules/Payables/Accounting101.Payables/DocumentBillStore.cs`
- Modify: `Modules/Payables/Accounting101.Payables/BillService.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesServiceExtensions.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs` (`InMemoryBillStore`)
- Modify: `Modules/Payables/Accounting101.Payables.Tests/PayablesDocumentStoreFixture.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/DocumentBillStoreTests.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/BillServiceTests.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/BillPaymentServiceTests.cs`
- Create: `Modules/Payables/Accounting101.Payables.Tests/BillDraftLifecycleTests.cs`

**Interfaces:**
- Consumes: `IDocumentStore` (`PutAsync`, `DeleteAsync` for plain; `CreateAsync`, `FinalizeAsync`, `VoidAsync` for evidentiary; `GetAsync`, `QueryAsync`). `BillBody`, `Bill`, `BillLine`, `BillStatus`, `BillPosting.ComposeBill`, `IBillAccountsProvider.GetBillAccountsAsync`, `ILedgerClient.PostAsync`, `IVendorStore.GetAsync`.
- Produces: `IBillStore.CreateDraftAsync`, `UpdateDraftAsync(clientId, billId, body)`, `DiscardDraftAsync(clientId, billId)`, `PromoteDraftAsync(clientId, billId) -> Bill`, `VoidAsync`, `GetAsync`, `GetByVendorAsync`. `BillService.EditDraftAsync(clientId, billId, body) -> Bill`, `BillService.DiscardDraftAsync(clientId, billId)`, and `BillService.EnterAsync` now returns the entered bill with a **new id** (callers must read the id from the response).

- [ ] **Step 1: Write the failing lifecycle test (RED)**

Create `Modules/Payables/Accounting101.Payables.Tests/BillDraftLifecycleTests.cs`:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables.Tests;

public sealed class BillDraftLifecycleTests
{
    private sealed record Harness(BillService Bills, InMemoryBillStore Store, FakeLedgerClient Ledger);

    private static async Task<(Harness h, Guid clientId, Guid vendorId)> MakeAsync()
    {
        InMemoryVendorStore vendors = new();
        InMemoryBillStore bills = new();
        FakeLedgerClient ledger = new();
        FixedBillAccountsProvider accounts = new(new BillPostingAccounts { PayableAccountId = Guid.NewGuid() });
        BillService service = new(bills, vendors, accounts, ledger);
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();
        await vendors.SaveAsync(clientId, new Vendor { Id = vendorId, Name = "Acme" });
        return (new Harness(service, bills, ledger), clientId, vendorId);
    }

    private static BillBody Body(Guid vendorId) => new(
        vendorId, new DateOnly(2026, 3, 1), null, "VENDOR-REF", null,
        [new BillLineBody("Rent", 100m, Guid.NewGuid())]);

    [Fact]
    public async Task Draft_is_editable_and_keeps_it_a_draft()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        BillBody edited = Body(vendorId) with { Memo = "updated" };
        Bill updated = await h.Bills.EditDraftAsync(clientId, draft.Id, edited);

        Assert.Equal(BillStatus.Draft, updated.Status);
        Assert.Null(updated.Number);
        Assert.Equal("updated", (await h.Store.GetAsync(clientId, draft.Id))!.Memo);
    }

    [Fact]
    public async Task Draft_is_discardable_and_leaves_no_trace()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        await h.Bills.DiscardDraftAsync(clientId, draft.Id);

        Assert.Null(await h.Store.GetAsync(clientId, draft.Id));
    }

    [Fact]
    public async Task Discard_only_works_on_a_draft()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Bills.DiscardDraftAsync(clientId, entered.Id));
    }

    [Fact]
    public async Task Enter_creates_a_new_id_deletes_the_draft_and_assigns_a_number()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        Assert.NotEqual(draft.Id, entered.Id);
        Assert.Equal(BillStatus.Entered, entered.Status);
        Assert.NotNull(entered.Number);
        Assert.Null(await h.Store.GetAsync(clientId, draft.Id));              // draft gone
        Assert.NotNull(await h.Store.GetAsync(clientId, entered.Id));         // entered present
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(entered.Id, entry.SourceRef);                            // posted under the ENTERED id
    }

    [Fact]
    public async Task Enter_only_works_on_a_draft()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Bills.EnterAsync(clientId, entered.Id));
    }

    [Fact]
    public async Task Void_keeps_the_entered_id_and_marks_void()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, Body(vendorId));
        Bill entered = await h.Bills.EnterAsync(clientId, draft.Id);
        // The fake ledger posts PendingApproval; void withdraws the pending entry, then voids the doc.
        Bill voided = await h.Bills.VoidAsync(clientId, entered.Id);

        Assert.Equal(entered.Id, voided.Id);
        Assert.Equal(BillStatus.Void, voided.Status);
    }
}
```

- [ ] **Step 2: Run the new test to verify it fails (RED)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillDraftLifecycleTests"`
Expected: FAIL — `IBillStore` has no `UpdateDraftAsync`/`DiscardDraftAsync`/`PromoteDraftAsync`; `BillService` has no `EditDraftAsync`/`DiscardDraftAsync`; `EnterAsync` still calls the removed `FinalizeAsync`. (Solution will not compile.)

- [ ] **Step 3: Rewrite `IBillStore`**

Replace the `IBillStore` interface in `Modules/Payables/Accounting101.Payables/PayablesPorts.cs` with:

```csharp
/// <summary>The module's bill store. Drafts live in a plain collection (editable, discardable scratch);
/// entered bills live in an evidentiary collection (append-only). Enter promotes a draft to a NEW evidentiary
/// document with a new id and deletes the draft — mirrors the invoicing module's two-tier split.</summary>
public interface IBillStore
{
    Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default);
    Task<Bill> UpdateDraftAsync(Guid clientId, Guid billId, BillBody body, CancellationToken ct = default);
    Task DiscardDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default);
    Task<Bill> PromoteDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid billId, CancellationToken ct = default);
    Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default);
    Task<IReadOnlyList<Bill>> GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default);
}
```

(`FinalizeAsync` is removed.)

- [ ] **Step 4: Rewrite `DocumentBillStore`**

Replace the entire contents of `Modules/Payables/Accounting101.Payables/DocumentBillStore.cs` with:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>
/// Persists bills through the engine's document store using a two-tier split:
/// <list type="bullet">
///   <item><b>bill-drafts</b> (plain) — freely editable and discardable scratch copies; never part of the
///   evidentiary record.</item>
///   <item><b>bills</b> (evidentiary) — append-only numbered documents; entered only on enter (via
///   <see cref="PromoteDraftAsync"/>). Number and status are derived from the engine envelope, never stored.</item>
/// </list>
/// The module owns no database connection; the engine's <see cref="IDocumentStore"/> is the only dependency.
/// </summary>
public sealed class DocumentBillStore(IDocumentStore documents) : IBillStore
{
    private const string Drafts = "bill-drafts";     // plain
    private const string Collection = "bills";       // evidentiary

    public async Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = Guid.NewGuid();
        await documents.PutAsync(clientId, Drafts, id, body, Tags(body.VendorId), ct);
        DocumentResult<BillBody>? r = await documents.GetAsync<BillBody>(clientId, Drafts, id, ct);
        return Map(r!);
    }

    public async Task<Bill> UpdateDraftAsync(Guid clientId, Guid billId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct) is null)
            throw new InvalidOperationException($"Bill {billId} is not an editable draft.");
        await documents.PutAsync(clientId, Drafts, billId, body, Tags(body.VendorId), ct);
        DocumentResult<BillBody>? r = await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct);
        return Map(r!);
    }

    public async Task DiscardDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct) is null)
            throw new InvalidOperationException($"Bill {billId} is not a discardable draft.");
        await documents.DeleteAsync(clientId, Drafts, billId, ct);
    }

    public async Task<Bill> PromoteDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        DocumentResult<BillBody>? draft = await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct)
            ?? throw new InvalidOperationException($"Bill {billId} is not a draft awaiting enter.");
        Guid enteredId = await documents.CreateAsync(clientId, Collection, draft.Body, Tags(draft.Body.VendorId), ct);
        await documents.FinalizeAsync(clientId, Collection, enteredId, ct);
        await documents.DeleteAsync(clientId, Drafts, billId, ct);
        DocumentResult<BillBody>? entered = await documents.GetAsync<BillBody>(clientId, Collection, enteredId, ct);
        return Map(entered!);
    }

    public Task VoidAsync(Guid clientId, Guid billId, CancellationToken ct = default) =>
        documents.VoidAsync(clientId, Collection, billId, ct);

    public async Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        DocumentResult<BillBody>? draft = await documents.GetAsync<BillBody>(clientId, Drafts, billId, ct);
        if (draft is not null) return Map(draft);
        DocumentResult<BillBody>? entered = await documents.GetAsync<BillBody>(clientId, Collection, billId, ct);
        return entered is null ? null : Map(entered);
    }

    public async Task<IReadOnlyList<Bill>> GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<DocumentResult<BillBody>> drafts = await documents.QueryAsync<BillBody>(clientId, Drafts, Tags(vendorId), cancellationToken: ct);
        IReadOnlyList<DocumentResult<BillBody>> entered = await documents.QueryAsync<BillBody>(clientId, Collection, Tags(vendorId), cancellationToken: ct);
        return drafts.Concat(entered).Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags(Guid vendorId) => new() { ["Vendor"] = vendorId.ToString() };

    private static Bill Map(DocumentResult<BillBody> result) => new()
    {
        Id = result.Id,
        VendorId = result.Body.VendorId,
        Number = result.Sequence is { } seq ? $"BILL-{seq:D5}" : null,
        BillDate = result.Body.BillDate,
        DueDate = result.Body.DueDate,
        VendorReference = result.Body.VendorReference,
        Memo = result.Body.Memo,
        Status = result.State switch
        {
            DocumentLifecycle.Finalized => BillStatus.Entered,
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => BillStatus.Void,
            _ => BillStatus.Draft,
        },
        Lines = result.Body.Lines
            .Select(l => new BillLine { Description = l.Description, Amount = l.Amount, ExpenseAccountId = l.ExpenseAccountId })
            .ToList(),
    };
}
```

- [ ] **Step 5: Rewrite `InMemoryBillStore` (test fake)**

In `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs`, replace the `InMemoryBillStore` class (the member currently spanning `CreateDraftAsync`/`FinalizeAsync`/`VoidAsync`/`GetAsync`/`GetByVendorAsync`) with:

```csharp
internal sealed class InMemoryBillStore : IBillStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Bill> _drafts = new();
    private readonly ConcurrentDictionary<(Guid, Guid), Bill> _entered = new();
    private int _next;

    public Task<Bill> CreateDraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Bill draft = new()
        {
            Id = Guid.NewGuid(), VendorId = body.VendorId, Number = null,
            BillDate = body.BillDate, DueDate = body.DueDate,
            VendorReference = body.VendorReference, Memo = body.Memo, Status = BillStatus.Draft,
            Lines = body.Lines.Select(l => new BillLine { Description = l.Description, Amount = l.Amount, ExpenseAccountId = l.ExpenseAccountId }).ToList(),
        };
        _drafts[(clientId, draft.Id)] = draft;
        return Task.FromResult(draft);
    }

    public Task<Bill> UpdateDraftAsync(Guid clientId, Guid billId, BillBody body, CancellationToken ct = default)
    {
        if (!_drafts.ContainsKey((clientId, billId)))
            throw new InvalidOperationException($"Bill {billId} is not an editable draft.");
        Bill updated = new()
        {
            Id = billId, VendorId = body.VendorId, Number = null,
            BillDate = body.BillDate, DueDate = body.DueDate,
            VendorReference = body.VendorReference, Memo = body.Memo, Status = BillStatus.Draft,
            Lines = body.Lines.Select(l => new BillLine { Description = l.Description, Amount = l.Amount, ExpenseAccountId = l.ExpenseAccountId }).ToList(),
        };
        _drafts[(clientId, billId)] = updated;
        return Task.FromResult(updated);
    }

    public Task DiscardDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (!_drafts.TryRemove((clientId, billId), out _))
            throw new InvalidOperationException($"Bill {billId} is not a discardable draft.");
        return Task.CompletedTask;
    }

    public Task<Bill> PromoteDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (!_drafts.TryRemove((clientId, billId), out Bill? draft))
            throw new InvalidOperationException($"Bill {billId} is not a draft awaiting enter.");
        Guid enteredId = Guid.NewGuid();
        Bill entered = draft with { Id = enteredId, Number = $"BILL-{Interlocked.Increment(ref _next):D5}", Status = BillStatus.Entered };
        _entered[(clientId, enteredId)] = entered;
        return Task.FromResult(entered);
    }

    public Task VoidAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (_entered.TryGetValue((clientId, billId), out Bill? b))
            _entered[(clientId, billId)] = b with { Status = BillStatus.Void };
        return Task.CompletedTask;
    }

    public Task<Bill?> GetAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        if (_drafts.TryGetValue((clientId, billId), out Bill? draft))
            return Task.FromResult<Bill?>(draft);
        return Task.FromResult(_entered.GetValueOrDefault((clientId, billId)));
    }

    /// <summary>Returns ALL bills (drafts + entered, including voided) — the service filters itself.</summary>
    public Task<IReadOnlyList<Bill>> GetByVendorAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        IEnumerable<Bill> drafts = _drafts.Where(kv => kv.Key.Item1 == clientId && kv.Value.VendorId == vendorId).Select(kv => kv.Value);
        IEnumerable<Bill> entered = _entered.Where(kv => kv.Key.Item1 == clientId && kv.Value.VendorId == vendorId).Select(kv => kv.Value);
        return Task.FromResult<IReadOnlyList<Bill>>(drafts.Concat(entered).ToList());
    }
}
```

- [ ] **Step 6: Register the plain drafts collection (host + test fixture)**

In `Modules/Payables/Accounting101.Payables.Api/PayablesServiceExtensions.cs`, change the manifest block (currently lines 16–20) to insert the plain drafts line before the evidentiary `bills` line:

```csharp
        services.AddModule(new ModuleIdentity("payables"), "Payables", manifest =>
        {
            manifest.Reference("vendors");
            manifest.Plain("bill-drafts");                 // drafts are scratch — editable, discardable
            manifest.Evidentiary("bills", "Vendor");
            manifest.Evidentiary("bill-payments", "Vendor");
            manifest.Evidentiary("vendor-credit-applications", "Vendor");
        });
```

In `Modules/Payables/Accounting101.Payables.Tests/PayablesDocumentStoreFixture.cs`, make the same insertion in the `ModuleManifestBuilder` chain (currently lines 40–45):

```csharp
        ModuleManifest manifest = new ModuleManifestBuilder()
            .Reference("vendors")
            .Plain("bill-drafts")
            .Evidentiary("bills", "Vendor")
            .Evidentiary("bill-payments", "Vendor")
            .Evidentiary("vendor-credit-applications", "Vendor")
            .Build();
```

- [ ] **Step 7: Rewrite `BillService` (add Edit/Discard; Enter = promote-then-post)**

Replace the `DraftAsync`, `EnterAsync` region and add `EditDraftAsync`/`DiscardDraftAsync` in `Modules/Payables/Accounting101.Payables/BillService.cs`. The class header, `VoidAsync`, `GetAsync`, and `RequireAsync` are unchanged. Replace the three methods `DraftAsync`+`EnterAsync` (currently lines 11–38) with:

```csharp
    public async Task<Bill> DraftAsync(Guid clientId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await ValidateBodyAsync(clientId, body, ct);
        return await bills.CreateDraftAsync(clientId, body, ct);
    }

    /// <summary>Enter a draft: finalize (assigns the number) on a NEW evidentiary id, delete the draft,
    /// then post its A/P entry (PendingApproval). Returns the entered bill — its id differs from the draft's.</summary>
    public async Task<Bill> EnterAsync(Guid clientId, Guid billId, CancellationToken ct = default)
    {
        Bill draft = await RequireAsync(clientId, billId, ct);
        if (draft.Status != BillStatus.Draft)
            throw new InvalidOperationException($"Only a draft bill can be entered; {billId} is {draft.Status}.");
        if (draft.Total <= 0m)
            throw new InvalidOperationException($"Bill {billId} must total more than zero.");

        Bill entered = await bills.PromoteDraftAsync(clientId, billId, ct);
        BillPostingAccounts posting = await accounts.GetBillAccountsAsync(clientId, ct);
        PostEntryRequest entry = BillPosting.ComposeBill(entered, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return entered;
    }

    /// <summary>Edit a draft bill: re-validate and replace the draft body. Throws if the id is not a draft.</summary>
    public async Task<Bill> EditDraftAsync(Guid clientId, Guid billId, BillBody body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await ValidateBodyAsync(clientId, body, ct);
        return await bills.UpdateDraftAsync(clientId, billId, body, ct);
    }

    /// <summary>Discard a draft bill (hard delete, no audit trace). Throws if the id is not a draft —
    /// use <see cref="VoidAsync"/> to cancel an entered bill instead.</summary>
    public Task DiscardDraftAsync(Guid clientId, Guid billId, CancellationToken ct = default) =>
        bills.DiscardDraftAsync(clientId, billId, ct);

    private async Task ValidateBodyAsync(Guid clientId, BillBody body, CancellationToken ct)
    {
        if (await vendors.GetAsync(clientId, body.VendorId, ct) is null)
            throw new InvalidOperationException($"Vendor {body.VendorId} does not exist.");
        if (body.Lines.Count == 0)
            throw new InvalidOperationException("A bill needs at least one line.");
        if (body.Lines.Any(l => l.Amount <= 0m))
            throw new InvalidOperationException("Every bill line amount must be greater than zero.");
        if (body.Lines.Any(l => l.ExpenseAccountId == Guid.Empty))
            throw new InvalidOperationException("Every bill line needs an expense account.");
    }
```

Leave `VoidAsync` (currently lines 40–57) exactly as-is — it resolves the spawned entry by the bill id and reverses/withdraws; since callers pass the **entered** id (and the post used that same id), it stays correct.

Update the class XML doc comment (lines 5–7) to read: *"The bill lifecycle: draft a bill (plain, editable, discardable scratch), enter it (promote to a new evidentiary id — assigns the number — then post its A/P entry, PendingApproval), and void it (reverse the entry if posted, or withdraw it if still pending). The module never self-approves."*

- [ ] **Step 8: Fix mechanical `FinalizeAsync` call sites in tests**

In `Modules/Payables/Accounting101.Payables.Tests/BillPaymentServiceTests.cs`, rename every `billStore.FinalizeAsync(...)` / `h.BillStore.FinalizeAsync(...)` call to `...PromoteDraftAsync(...)`. There are four occurrences:

- Line 23: `Bill entered = await billStore.FinalizeAsync(clientId, draft.Id);` → `PromoteDraftAsync`
- Line 98: `Bill second = await h.BillStore.FinalizeAsync(clientId, draft2.Id);` → `PromoteDraftAsync`
- Line 139: `Bill second = await h.BillStore.FinalizeAsync(clientId, d2.Id);` → `PromoteDraftAsync`
- Line 251 (`EnterAnotherBillAsync`): `return await h.BillStore.FinalizeAsync(clientId, draft.Id);` → `PromoteDraftAsync`

These helpers capture the returned `entered`/`second`/`first` bill and use its `.Id` for allocations — that id is now the entered id, which is exactly what settlement expects. No assertion changes are needed here.

- [ ] **Step 9: Update `DocumentBillStoreTests` and `BillServiceTests`**

Rewrite `Modules/Payables/Accounting101.Payables.Tests/DocumentBillStoreTests.cs` to exercise the real document store across two collections:

```csharp
using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables.Tests;

public sealed class DocumentBillStoreTests(PayablesDocumentStoreFixture fixture) : IClassFixture<PayablesDocumentStoreFixture>
{
    private static BillBody Body(Guid vendorId) => new(
        vendorId, new DateOnly(2026, 3, 1), null, null, null,
        [new BillLineBody("Rent", 100m, Guid.NewGuid())]);

    [Fact]
    public async Task Draft_lands_in_the_plain_collection_with_no_number()
    {
        Guid vendorId = Guid.NewGuid();
        DocumentBillStore store = new(fixture.Store);

        Bill draft = await store.CreateDraftAsync(fixture.ClientId, Body(vendorId));

        Assert.Equal(BillStatus.Draft, draft.Status);
        Assert.Null(draft.Number);
    }

    [Fact]
    public async Task Promote_creates_a_new_entered_id_and_deletes_the_draft()
    {
        Guid vendorId = Guid.NewGuid();
        DocumentBillStore store = new(fixture.Store);
        Bill draft = await store.CreateDraftAsync(fixture.ClientId, Body(vendorId));

        Bill entered = await store.PromoteDraftAsync(fixture.ClientId, draft.Id);

        Assert.NotEqual(draft.Id, entered.Id);
        Assert.Equal(BillStatus.Entered, entered.Status);
        Assert.StartsWith("BILL-", entered.Number);
        Assert.Null(await store.GetAsync(fixture.ClientId, draft.Id));
        Assert.NotNull(await store.GetAsync(fixture.ClientId, entered.Id));
    }

    [Fact]
    public async Task Update_and_discard_only_act_on_drafts()
    {
        Guid vendorId = Guid.NewGuid();
        DocumentBillStore store = new(fixture.Store);
        Bill draft = await store.CreateDraftAsync(fixture.ClientId, Body(vendorId));

        Bill updated = await store.UpdateDraftAsync(fixture.ClientId, draft.Id, Body(vendorId) with { Memo = "m" });
        Assert.Equal("m", updated.Memo);

        await store.DiscardDraftAsync(fixture.ClientId, draft.Id);
        Assert.Null(await store.GetAsync(fixture.ClientId, draft.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DiscardDraftAsync(fixture.ClientId, draft.Id));
    }
}
```

In `Modules/Payables/Accounting101.Payables.Tests/BillServiceTests.cs`, update the existing enter test so it asserts the id changes, and add edit/discard cases. Replace the body of the existing `"Draft then enter produces an Entered bill"` test (the one calling `EnterAsync`) with an assertion that `entered.Id != draft.Id` and `entered.Number` starts with `BILL-`; keep the `BillStatus.Entered` assertion. Add:

```csharp
    [Fact]
    public async Task Edit_draft_replaces_body_and_keeps_it_a_draft()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, BillBody(vendorId));

        Bill updated = await h.Bills.EditDraftAsync(clientId, draft.Id, BillBody(vendorId) with { Memo = "edited" });

        Assert.Equal(BillStatus.Draft, updated.Status);
        Assert.Equal("edited", (await h.BillStore.GetAsync(clientId, draft.Id))!.Memo);
    }

    [Fact]
    public async Task Discard_draft_removes_it()
    {
        var (h, clientId, vendorId) = await MakeAsync();
        Bill draft = await h.Bills.DraftAsync(clientId, BillBody(vendorId));

        await h.Bills.DiscardDraftAsync(clientId, draft.Id);

        Assert.Null(await h.BillStore.GetAsync(clientId, draft.Id));
    }
```

(`MakeAsync` is the existing helper in `BillServiceTests.cs` that returns `(Harness, clientId, vendorId)`; the harness exposes `Bills` and `BillStore`. For the body, reuse the same `new BillBody(vendorId, …)` construction the existing `DraftAsync` test in that file uses — and make sure each `BillLineBody` carries a non-empty `ExpenseAccountId`, since `DraftAsync`/`EditDraftAsync` now enforce it. The existing `"Draft then enter produces an Entered bill"` test in this file is the one to amend with the `NotEqual(draft.Id, entered.Id)` + `BILL-` number assertions.)

- [ ] **Step 10: Run the payables test project (GREEN)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj`
Expected: PASS — all previously-green tests still pass, and the new `BillDraftLifecycleTests`/`DocumentBillStoreTests`/`BillServiceTests` cases pass.

- [ ] **Step 11: Audit E2E tests for draft-id-after-enter usage**

Grep the payables test tree for any test that calls `/enter` and then uses the **draft** id (rather than the returned entered id) downstream:

Run: `grep -rn "/enter" Modules/Payables/Accounting101.Payables.Tests`
For each hit, confirm the pattern is `Bill entered = (await (.../enter...)).Body` and that subsequent requests use `entered.Id` (not the draft id). Files to check: `VendorAccountEndpointE2eTests.cs`, `VendorCreditApplicationListEndpointTests.cs`, `Settlement/BillSettlementScenario.cs`, `Settlement/*.E2eTests.cs`, `PayablesE2eTests.cs`, `ModuleViaPayablesTests.cs`. If any uses the draft id after enter, switch it to `entered.Id`.

Re-run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj`
Expected: PASS.

- [ ] **Step 12: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/PayablesPorts.cs \
        Modules/Payables/Accounting101.Payables/DocumentBillStore.cs \
        Modules/Payables/Accounting101.Payables/BillService.cs \
        Modules/Payables/Accounting101.Payables.Api/PayablesServiceExtensions.cs \
        Modules/Payables/Accounting101.Payables.Tests/Fakes.cs \
        Modules/Payables/Accounting101.Payables.Tests/PayablesDocumentStoreFixture.cs \
        Modules/Payables/Accounting101.Payables.Tests/DocumentBillStoreTests.cs \
        Modules/Payables/Accounting101.Payables.Tests/BillServiceTests.cs \
        Modules/Payables/Accounting101.Payables.Tests/BillPaymentServiceTests.cs \
        Modules/Payables/Accounting101.Payables.Tests/BillDraftLifecycleTests.cs
git commit -m "feat(payables): two-tier bill lifecycle — plain drafts, promote-on-enter

Drafts now live in a plain bill-drafts collection (editable, discardable);
enter promotes a draft to a new evidentiary bill (new id + number) and
deletes the draft, mirroring the invoice lifecycle. Payments, credits, and
the vendor 360 are unchanged (they key off entered ids only).

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Preflight the A/P post before promote (closes the orphan window)

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables/ILedgerClient.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs` (`FakeLedgerClient`)
- Modify: `Modules/Payables/Accounting101.Payables/BillService.cs` (`EnterAsync`)
- Modify: `Modules/Payables/Accounting101.Payables.Tests/BillDraftLifecycleTests.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Tests/HttpLedgerClientTests.cs`

**Interfaces:**
- Consumes: the engine's `POST clients/{clientId}/entries/validate` endpoint (same one receivables uses); `LedgerClientException`.
- Produces: `ILedgerClient.ValidateAsync(clientId, PostEntryRequest entry)`. `BillService.EnterAsync` now calls it before `PromoteDraftAsync`; a rejection throws `LedgerClientException` and leaves the bill as Draft.

- [ ] **Step 1: Write the failing test (RED)**

Add to `BillDraftLifecycleTests.cs` (and add a `FakeLedgerClient.OnValidate` setter usage). Append this test:

```csharp
    [Fact]
    public async Task Enter_that_fails_preflight_leaves_the_bill_a_draft_and_posts_nothing()
    {
        // Same harness shape as the other tests, but we need the fake ledger's OnValidate hook.
        InMemoryVendorStore vendors = new();
        InMemoryBillStore bills = new();
        FakeLedgerClient ledger = new();
        ledger.OnValidate = _ => throw new LedgerClientException(409, "period closed");
        BillService service = new(bills, vendors,
            new FixedBillAccountsProvider(new BillPostingAccounts { PayableAccountId = Guid.NewGuid() }), ledger);
        Guid clientId = Guid.NewGuid();
        Guid vendorId = Guid.NewGuid();
        await vendors.SaveAsync(clientId, new Vendor { Id = vendorId, Name = "Acme" });

        Bill draft = await service.DraftAsync(clientId, Body(vendorId));

        await Assert.ThrowsAsync<LedgerClientException>(() => service.EnterAsync(clientId, draft.Id));
        Assert.Empty(ledger.Posted);
        Bill? stillDraft = await bills.GetAsync(clientId, draft.Id);
        Assert.NotNull(stillDraft);
        Assert.Equal(BillStatus.Draft, stillDraft!.Status);   // NOT entered — no orphan
    }
```

(If `Body(vendorId)` is a static helper on the test class, reuse it; otherwise inline the same `BillBody` used above.)

- [ ] **Step 2: Run it to verify it fails (RED)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~Enter_that_fails_preflight"`
Expected: FAIL — `FakeLedgerClient` has no `OnValidate`/`ValidateAsync`; `ILedgerClient` has no `ValidateAsync`; `EnterAsync` does not preflight. (Won't compile.)

- [ ] **Step 3: Add `ValidateAsync` to the payables `ILedgerClient`**

In `Modules/Payables/Accounting101.Payables/ILedgerClient.cs`, add this method to the interface (place it after `VoidAsync`, mirroring the receivables contract):

```csharp
    /// <summary>
    /// Dry-run the would-be post without writing anything. Returns on a clean validation; throws
    /// <see cref="LedgerClientException"/> with the engine's status and reason on any rejection (closed
    /// period, chart violation, unbalanced entry). Lets callers catch a bad date or account before
    /// committing the document, so the document is never entered against an entry the engine would refuse.
    /// </summary>
    Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Implement `ValidateAsync` on `HttpLedgerClient`**

In `Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs`, add this method (mirror of the receivables `HttpLedgerClient.ValidateAsync`, lines 72–81 of the receivables file):

```csharp
    public async Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/validate");
        // Attach the module credential so the engine's pre-flight dry-run authorizes via the module path.
        request.Headers.TryAddWithoutValidation("X-Module-Key", credential.Key);
        request.Headers.TryAddWithoutValidation("X-Module-Secret", credential.Secret);
        request.Content = JsonContent.Create(entry);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }
```

Update the class XML doc: in the `<para>` that lists `PostAsync`, also mention `ValidateAsync` attaches the credential (mirror the receivables doc, lines 15–20).

- [ ] **Step 5: Implement `ValidateAsync` + `OnValidate` hook on `FakeLedgerClient`**

In `Modules/Payables/Accounting101.Payables.Tests/Fakes.cs`, add to `FakeLedgerClient` (mirror the receivables fake, `Fakes.cs` lines 23–34):

```csharp
    /// <summary>
    /// Optional hook: tests set this to drive the validation outcome. When null (the default), validation
    /// succeeds silently. Set to a delegate that throws <see cref="LedgerClientException"/> to simulate a
    /// rejection (e.g. a closed-period 409) without HTTP.
    /// </summary>
    public Func<PostEntryRequest, Task>? OnValidate { get; set; }

    public async Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default)
    {
        if (OnValidate is not null)
            await OnValidate(entry);
    }
```

- [ ] **Step 6: Wire preflight into `BillService.EnterAsync`**

In `Modules/Payables/Accounting101.Payables/BillService.cs`, change `EnterAsync` to preflight on the draft before promoting. Replace the body between the guards and the return with:

```csharp
        // Resolve accounts and pre-flight against the draft (Number is null → Reference is null, which validation ignores).
        // If the engine would reject (closed period, chart violation, unbalanced entry), this throws LedgerClientException
        // and the document remains a Draft — promote has not run, so there is no orphan.
        BillPostingAccounts posting = await accounts.GetBillAccountsAsync(clientId, ct);
        PostEntryRequest preflight = BillPosting.ComposeBill(draft, posting);
        await ledger.ValidateAsync(clientId, preflight, ct);

        // Validation passed — commit the document on a new evidentiary id, then post under that id.
        Bill entered = await bills.PromoteDraftAsync(clientId, billId, ct);
        PostEntryRequest entry = BillPosting.ComposeBill(entered, posting);
        await ledger.PostAsync(clientId, entry, ct);
        return entered;
```

(Remove the previous `Bill entered = await bills.PromoteDraftAsync(...)` / compose / post lines that this replaces.)

- [ ] **Step 7: Add an `HttpLedgerClient` route test**

In `Modules/Payables/Accounting101.Payables.Tests/HttpLedgerClientTests.cs`, add a test asserting the validate call hits `entries/validate` and attaches the module credential. Mirror whatever pattern the existing `PostAsync` test in that file uses (handler stub asserting the request URI and `X-Module-Key` header); the expected request URI is `clients/{clientId}/entries/validate`. If the file has no handler-stub harness already, copy the receivables `HttpLedgerClientTests` validate test verbatim and adjust namespaces to `Accounting101.Payables`.

- [ ] **Step 8: Run the payables test project (GREEN)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj`
Expected: PASS — preflight test passes; no regressions.

- [ ] **Step 9: Commit**

```bash
git add Modules/Payables/Accounting101.Payables/ILedgerClient.cs \
        Modules/Payables/Accounting101.Payables.Api/HttpLedgerClient.cs \
        Modules/Payables/Accounting101.Payables/BillService.cs \
        Modules/Payables/Accounting101.Payables.Tests/Fakes.cs \
        Modules/Payables/Accounting101.Payables.Tests/BillDraftLifecycleTests.cs \
        Modules/Payables/Accounting101.Payables.Tests/HttpLedgerClientTests.cs
git commit -m "feat(payables): preflight bill A/P post before enter

Enter now dry-runs the would-be post before promoting the draft, so an
engine rejection (closed period, chart violation) leaves the bill a Draft
with no orphan entered document — matching the invoice issue flow.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: HTTP endpoints for draft edit + discard

**Files:**
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs`
- Modify: `Modules/Payables/Accounting101.Payables.Api/PayablesRequests.cs` (only if `DraftBillRequest` lives here — confirm)
- Create or extend: a draft-endpoints test (add to `BillListValidationTests.cs` or create `BillDraftEndpointsTests.cs`)

**Interfaces:**
- Consumes: `BillService.EditDraftAsync(clientId, billId, BillBody)`, `BillService.DiscardDraftAsync(clientId, billId)`, `DraftBillRequest` (existing request DTO).
- Produces: `PUT /clients/{clientId}/bills/{billId}` → 200 (updated draft) / 409 (not a draft, bad body); `DELETE /clients/{clientId}/bills/{billId}` → 204 / 409 (not a draft).

- [ ] **Step 1: Write the failing endpoint test (RED)**

Create `Modules/Payables/Accounting101.Payables.Tests/BillDraftEndpointsTests.cs`. Use the existing host fixture pattern (`PayablesHostFixture`) the other endpoint tests in this project use (e.g. `BillListValidationTests.cs`, `VendorListEndpointTests.cs`) — copy its client+clerk setup. The test drafts a bill, edits it, discards it:

```csharp
namespace Accounting101.Payables.Tests;

public sealed class BillDraftEndpointsTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Draft_can_be_edited_via_PUT()
    {
        var (http, clientId, vendorId) = await fixture.NewClientAsync();
        Guid expense = await fixture.EnsureExpenseAccountAsync(clientId);   // helper already used by bill tests

        Bill draft = await (await http.PostAsJsonAsync($"/clients/{clientId}/bills",
            new DraftBillRequest(vendorId, new DateOnly(2026, 3, 1), null, null, null,
                [new BillLineBody("Rent", 100m, expense)]))).BodyAsync<Bill>();

        Bill edited = await (await http.PutAsJsonAsync($"/clients/{clientId}/bills/{draft.Id}",
            new DraftBillRequest(vendorId, new DateOnly(2026, 3, 1), null, "VREF-1", null,
                [new BillLineBody("Rent", 100m, expense)]))).BodyAsync<Bill>();

        Assert.Equal(BillStatus.Draft, edited.Status);
        Assert.Equal("VREF-1", edited.VendorReference);
    }

    [Fact]
    public async Task Draft_can_be_discarded_via_DELETE()
    {
        var (http, clientId, vendorId) = await fixture.NewClientAsync();
        Guid expense = await fixture.EnsureExpenseAccountAsync(clientId);

        Bill draft = await (await http.PostAsJsonAsync($"/clients/{clientId}/bills",
            new DraftBillRequest(vendorId, new DateOnly(2026, 3, 1), null, null, null,
                [new BillLineBody("Rent", 100m, expense)]))).BodyAsync<Bill>();

        HttpResponseMessage deleted = await http.DeleteAsync($"/clients/{clientId}/bills/{draft.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        HttpResponseMessage after = await http.GetAsync($"/clients/{clientId}/bills/{draft.Id}");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);   // draft is gone, not voided
    }
}
```

If `PayablesHostFixture` exposes different helper names than `NewClientAsync`/`EnsureExpenseAccountAsync`/`BodyAsync<T>`, adopt the names already used in `BillListValidationTests.cs` — keep the assertions identical.

- [ ] **Step 2: Run it to verify it fails (RED)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillDraftEndpointsTests"`
Expected: FAIL — `PUT /bills/{id}` and `DELETE /bills/{id}` are not mapped (404 / not compiled).

- [ ] **Step 3: Map the two endpoints**

In `Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs`, add two routes to the `clients` group (place them next to the existing `MapPost("/bills", DraftBill)` / `MapPost("/bills/{billId:guid}/enter", EnterBill)` lines):

```csharp
        clients.MapPut   ("/bills/{billId:guid}", EditBill);
        clients.MapDelete("/bills/{billId:guid}", DiscardBill);
```

Add the two handler methods (mirror `DraftBill`/`EditInvoice`/`DiscardInvoice` in the receivables endpoints):

```csharp
    private static async Task<IResult> EditBill(
        Guid clientId, Guid billId, DraftBillRequest request, BillService service, CancellationToken cancellationToken)
    {
        try
        {
            Bill updated = await service.EditDraftAsync(
                clientId, billId,
                new BillBody(request.VendorId, request.BillDate, request.DueDate, request.VendorReference, request.Memo, request.Lines),
                cancellationToken);
            return Results.Ok(updated);
        }
        catch (InvalidOperationException ex) // not an editable draft, unknown vendor, or bad body
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> DiscardBill(
        Guid clientId, Guid billId, BillService service, CancellationToken cancellationToken)
    {
        try
        {
            await service.DiscardDraftAsync(clientId, billId, cancellationToken);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex) // not a discardable draft
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
```

- [ ] **Step 4: Run the endpoint tests (GREEN)**

Run: `dotnet test Modules/Payables/Accounting101.Payables.Tests/Accounting101.Payables.Tests.csproj --filter "FullyQualifiedName~BillDraftEndpointsTests"`
Expected: PASS.

- [ ] **Step 5: Run the full backend solution**

Run: `dotnet test`
Expected: PASS — entire solution green (no regressions in receivables, engine, or other modules).

- [ ] **Step 6: Commit**

```bash
git add Modules/Payables/Accounting101.Payables.Api/PayablesEndpoints.cs \
        Modules/Payables/Accounting101.Payables.Tests/BillDraftEndpointsTests.cs
git commit -m "feat(payables): PUT/DELETE bill draft endpoints (edit + discard)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: UI — edit/discard drafts, enter navigates to the entered id

**Files:**
- Modify: `UI/Angular/src/app/core/payables/payables.service.ts`
- Modify: `UI/Angular/src/app/features/payables/bill-detail.ts`
- Modify: `UI/Angular/src/app/features/payables/bill-editor.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`
- Modify: `UI/Angular/src/app/features/payables/bill-detail.spec.ts`
- Modify: `UI/Angular/src/app/features/payables/bill-editor.spec.ts`

**Interfaces:**
- Consumes: `PUT /clients/{id}/bills/{billId}` (returns updated `Bill`), `DELETE /clients/{id}/bills/{billId}` (204), `POST .../enter` (returns entered `Bill` with new id). Existing `getBill`, `draftBill`.
- Produces: `PayablesService.editBill(id, req): Observable<Bill>`, `PayablesService.discardBill(id): Observable<void>`. `BillDetail.enter()` re-points `this.id` to the returned entered id and navigates with `replaceUrl`. `BillDetail` draft case shows Edit + Discard. `BillEditor` edit mode loads the draft and PUTs on save; Discard action deletes. Route `payables/bills/:id/edit` → `BillEditor`.

- [ ] **Step 1: Write the failing UI tests (RED)**

In `bill-detail.spec.ts`, add (mirroring `invoice-detail.spec.ts`'s issue-navigates test at its line ~64):

```typescript
  it('enter promotes the draft and re-points to the entered id', async () => {
    const { inst, nav, http } = await make({ bill: draftBill('d1') });
    http.putOne(`enter`, { id: 'e9', status: 'Entered', number: 'BILL-00001', vendorId: 'v1', billDate: '2026-03-01', lines: [] });
    inst.enter();
    await settle();
    expect(nav).toHaveBeenCalledWith(['/payables/bills', 'e9'], { replaceUrl: true });
  });

  it('discard deletes the draft and returns to the list', async () => {
    const { inst, nav, http } = await make({ bill: draftBill('d1') });
    inst.deleteBill();
    await settle();
    expect(http.deleted(`bills/d1`)).toBe(true);
    expect(nav).toHaveBeenCalledWith(['/payables']);
  });
```

(Adopt the harness helpers `make`, `settle`, `draftBill`, and the `http` stub shape already used in `bill-detail.spec.ts`. The exact stub API — e.g. `putOne`/`deleted` — must match whatever the existing tests in that file use; if those helpers don't exist, copy the equivalent stubs from `invoice-detail.spec.ts`.)

In `bill-editor.spec.ts`, add an edit-mode test (mirror `invoice-editor.spec.ts`):

```typescript
  it('edit mode loads the draft and PUTs on save', async () => {
    const { inst, nav, http } = await make({ route: 'payables/bills/:id/edit', id: 'd1', bill: draftBill('d1') });
    await settle();                       // prefill effect loads the draft
    inst.save();
    await settle();
    expect(http.lastPutUrl).toContain(`bills/d1`);
    expect(nav).toHaveBeenCalledWith(['/payables/bills', 'd1']);
  });
```

- [ ] **Step 2: Run them to verify they fail (RED)**

Run: `cd UI/Angular && npx ng test --watch=false` (focus the two files with `fdescribe` if helpful)
Expected: FAIL — no `editBill`/`discardBill` on the service; `enter()` doesn't navigate; `BillDetail` has no `deleteBill`; `BillEditor` has no edit mode.

- [ ] **Step 3: Add `editBill` + `discardBill` to `PayablesService`**

In `UI/Angular/src/app/core/payables/payables.service.ts`, add (next to `draftBill`/`enter`):

```typescript
  editBill(id: string, req: DraftBillRequest): Observable<Bill> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.put<Bill>(this.base(`/bills/${id}`), req);
  }

  discardBill(id: string): Observable<void> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.delete<void>(this.base(`/bills/${id}`));
  }
```

(`DraftBillRequest` and `Bill` are already imported at the top of the file.)

- [ ] **Step 4: Update `BillDetail` — enter re-points id; draft gains Edit + Discard**

In `UI/Angular/src/app/features/payables/bill-detail.ts`:

(a) Add `Router` import and injection (the file currently injects neither `Router` nor `ActivatedRoute`'s router — add `Router` to the `@angular/router` import and `private readonly router = inject(Router);`).

(b) Make `id` mutable (mirror `invoice-detail.ts` line 129):
```typescript
  // Not readonly: entering a draft promotes it to a new evidentiary id, after which the page re-points here.
  id = this.route.snapshot.paramMap.get('id')!;
```

(c) Change `enter()` to navigate to the returned entered id (mirror `invoice-detail.ts` lines 176–188):
```typescript
  enter(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.enter(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      // Entering promotes the draft to a brand-new evidentiary bill (new id + number) and deletes the draft.
      // Re-point the page at the entered id; reloading the old draft id would 404 (it's gone).
      next: (entered) => {
        this.id = entered.id;
        this.router.navigate(['/payables/bills', entered.id], { replaceUrl: true });
        this.reload(true);
      },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
```

(d) Add `deleteBill()` (mirror `invoice-detail.ts` lines 198–204):
```typescript
  deleteBill(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.discardBill(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.router.navigate(['/payables']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
```

(e) In the Draft `@case` template block (currently just the Enter button), mirror the invoice detail's draft row (lines 76–82) — add Edit + Discard before Enter:
```html
          @case ('Draft') {
            <div class="flex items-center gap-2">
              <a hlmBtn variant="outline" [routerLink]="['/payables/bills', id, 'edit']">Edit</a>
              <button hlmBtn type="button" variant="outline" (click)="deleteBill()" [disabled]="busy()">Delete</button>
              <button hlmBtn type="button" (click)="enter()" [disabled]="busy()">Enter</button>
            </div>
          }
```

- [ ] **Step 5: Update `BillEditor` — edit mode + Discard**

In `UI/Angular/src/app/features/payables/bill-editor.ts`, mirror `invoice-editor.ts`:

(a) Add `effect` to the Angular core import; add `ActivatedRoute` to the router import; inject `ActivatedRoute`.

(b) Add fields and prefill effect (mirror lines 160–207 of `invoice-editor.ts`):
```typescript
  #loaded = false;
  readonly editId = this.route.snapshot.paramMap.get('id');

  constructor() {
    this.svc.load();
    this.accountsSvc.load();
    if (this.editId) {
      effect(() => {
        if (this.#loaded) return;
        const vendors = this.svc.vendors();
        if (!vendors.length) return;
        this.#loaded = true;
        this.svc.getBill(this.editId!).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(view => {
          this.model.set(this.fromBill(view.bill));
        });
      });
    }
  }

  private fromBill(b: Bill): BillFormValue {
    return {
      vendorId: b.vendorId,
      billDate: b.billDate,
      dueDate: b.dueDate,
      vendorReference: b.vendorReference,
      memo: b.memo,
      lines: (b.lines ?? []).map(l => ({ lineId: crypto.randomUUID(), description: l.description, amount: l.amount, expenseAccountId: l.expenseAccountId })),
    };
  }
```

(Import `Bill` from `../../core/payables/payables` alongside the existing `DraftBillRequest, billTotal` import.)

(c) Change the header to reflect mode (mirror line 48): `<h1 class="text-2xl font-bold">{{ editId ? 'Edit bill' : 'New bill' }}</h1>`.

(d) Disable the vendor select in edit mode (mirror `[disabled]="!!editId"` on the `hlmSelect`).

(e) Change `save()` to PUT when editing (mirror lines 243–255):
```typescript
  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true);
    this.message.set(null);
    const req = this.toRequest();
    const obs = this.editId ? this.svc.editBill(this.editId, req) : this.svc.draftBill(req);
    obs.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => { this.busy.set(false); void this.router.navigate(['/payables/bills', saved.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
```

(f) Add a Discard button visible only in edit mode, in the action row next to Save/Cancel:
```html
      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Save</button>
        <a hlmBtn variant="outline" routerLink="/payables">Cancel</a>
        @if (editId) {
          <button hlmBtn type="button" variant="ghost" (click)="discard()" [disabled]="busy()">Discard</button>
        }
      </div>
```
and the handler:
```typescript
  discard(): void {
    if (!this.editId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.discardBill(this.editId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/payables']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
```

- [ ] **Step 6: Add the edit route**

In `UI/Angular/src/app/app.routes.ts`, add the edit route to the `payables` children (currently lines 80–90), mirroring line 69 for receivables. Insert before `{ path: 'bills/:id', component: BillDetail }`:

```typescript
    { path: 'bills/:id/edit', component: BillEditor },
```

(Ensure `BillEditor` is already imported at the top of `app.routes.ts` — it is, since `bills/new` uses it.)

- [ ] **Step 7: Run the UI tests (GREEN)**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — all existing payables specs and the new edit/discard/enter-navigate cases pass.

- [ ] **Step 8: Commit**

```bash
cd UI/Angular && \
git add src/app/core/payables/payables.service.ts \
        src/app/features/payables/bill-detail.ts src/app/features/payables/bill-detail.spec.ts \
        src/app/features/payables/bill-editor.ts src/app/features/payables/bill-editor.spec.ts \
        src/app/app.routes.ts
git commit -m "feat(ui): bill draft edit/discard + enter navigates to entered id

Bill drafts are now editable (PUT) and discardable (DELETE) in-app, and
entering a draft re-points the detail page to the new evidentiary bill id.
Mirrors the invoice editor/detail.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Final verification

- [ ] **Full backend solution:** `dotnet test` → all green.
- [ ] **Full UI suite:** `cd UI/Angular && npx ng test --watch=false` → all green.
- [ ] **Dev seed audit:** grep the dev-seed scripts for bill-enter usage and confirm none of them reuse the draft id after enter. Run: `grep -rn "/enter\|/bills" .localdev 2>/dev/null; grep -rn "/enter\|/bills" --include=*.ps1 --include=*.http --include=*.json .` For any seed that drafts a bill then enters it, confirm it reads the entered id from the enter response (the draft id is deleted). If a seed holds the draft id across enter, update it to use the returned id.
- [ ] **Spec acceptance check:** walk the eight acceptance criteria in `docs/superpowers/specs/2026-06-30-payables-bill-lifecycle-invoice-parity-design.md` and confirm each is demonstrated by a passing test.
