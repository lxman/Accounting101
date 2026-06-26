# Drafts as Scratch — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Move invoice drafts out of the audited `invoices` collection into a plain `invoice-drafts` collection so they can be freely edited and discarded; promote a draft into the evidentiary numbered invoice only at issue.

**Architecture:** Drafts become plain-tier documents (`PutAsync`/`DeleteAsync` — create/edit/discard, no audit trace). Issue reads the plain draft, creates the evidentiary invoice (`CreateAsync`+`FinalizeAsync`, assigning the gapless number), posts the A/R entry `PendingApproval`, and deletes the plain draft. Only Issue/Void are audited facts. The issued invoice is a new artifact with its own id+number, distinct from the consumed draft.

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs, the engine document store (`IDocumentStore`, plain/reference/evidentiary tiers), xUnit + EphemeralMongo + WebApplicationFactory.

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- Drafts never enter the evidentiary record; only Issue/Void are audited facts.
- Issue stays **pre-flight validate → promote → post** so a validation failure happens before any evidentiary write (no orphan); the residual infra-failure window stays the orphan-backstop's job (out of scope).
- The module never approves its own entries (post `PendingApproval`).
- Spec: `docs/superpowers/specs/2026-06-25-drafts-as-scratch-design.md`.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit file lists; check for stray churn. Do NOT commit in a worktree.

---

## Task 1: Store layer — drafts in a plain collection, promote on issue

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs` (register plain collection)
- Modify: `Modules/Receivables/Accounting101.Receivables/IInvoiceStore.cs` (contract)
- Modify: `Modules/Receivables/Accounting101.Receivables/DocumentInvoiceStore.cs` (real impl)
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/InMemoryInvoiceStore.cs` (test double — keep contract parity)
- Modify: `Modules/Receivables/Accounting101.Receivables/InvoiceService.cs` (IssueAsync call site only: `FinalizeAsync` → `PromoteDraftAsync`)
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/DocumentStoreFixture.cs` (register `invoice-drafts` plain in the test manifest)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/InvoiceStoreDraftTests.cs` (create)

**Interfaces:**
- Produces (`IInvoiceStore`): `Task<Invoice> CreateDraftAsync(Guid clientId, InvoiceBody body, CT)` (now plain); `Task<Invoice> UpdateDraftAsync(Guid clientId, Guid invoiceId, InvoiceBody body, CT)` (new); `Task DiscardDraftAsync(Guid clientId, Guid invoiceId, CT)` (new); `Task<Invoice> PromoteDraftAsync(Guid clientId, Guid invoiceId, CT)` (replaces `FinalizeAsync`; returns the issued invoice with new id+number); `VoidAsync`/`GetAsync`/`GetByCustomerAsync` unchanged signatures (reads now span both collections).

- [ ] **Step 1: Write the failing store tests** — `InvoiceStoreDraftTests` against the real `DocumentInvoiceStore` via `DocumentStoreFixture` (same pattern as the existing store tests). Cover:

```csharp
// 1. Create + edit a draft: CreateDraftAsync -> Status Draft, no Number; UpdateDraftAsync changes a field;
//    GetAsync reads the change back; the draft is NOT in the evidentiary "invoices" collection.
// 2. Discard: DiscardDraftAsync removes it; GetAsync -> null; nothing in "invoices".
// 3. Promote: PromoteDraftAsync returns an Invoice with a NEW id (!= draftId) and an assigned Number;
//    GetAsync(draftId) -> null (consumed); GetAsync(issuedId) -> Status Issued with that Number.
// 4. Reads span both tiers: a customer with one draft + one issued -> GetByCustomerAsync returns BOTH
//    (one Draft, one Issued).
// 5. UpdateDraftAsync / DiscardDraftAsync on a non-draft id throw InvalidOperationException.
```

- [ ] **Step 2: Run, confirm fail** — the new methods don't exist yet / drafts still land in evidentiary. `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "InvoiceStoreDraftTests"`.

- [ ] **Step 3: Implement**

`ReceivablesServiceExtensions.cs` — register the plain collection next to the evidentiary one:
```csharp
manifest.Reference("customers");
manifest.Plain("invoice-drafts");                 // <-- add: drafts are scratch, freely edited/discarded
manifest.Evidentiary("invoices", "Customer");
manifest.Evidentiary("payments", "Customer");
manifest.Evidentiary("credit-applications", "Customer");
```
Mirror the same `.Plain("invoice-drafts")` in `DocumentStoreFixture.cs`'s test manifest.

`IInvoiceStore.cs` — replace `FinalizeAsync` and add the two draft ops:
```csharp
Task<Invoice> CreateDraftAsync(Guid clientId, InvoiceBody body, CancellationToken cancellationToken = default);
Task<Invoice> UpdateDraftAsync(Guid clientId, Guid invoiceId, InvoiceBody body, CancellationToken cancellationToken = default);
Task DiscardDraftAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);
Task<Invoice> PromoteDraftAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default); // was FinalizeAsync
Task VoidAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);
Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default);
Task<IReadOnlyList<Invoice>> GetByCustomerAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default);
```

`DocumentInvoiceStore.cs` — two collection constants and the new bodies:
```csharp
private const string Drafts = "invoice-drafts";   // plain
private const string Collection = "invoices";      // evidentiary

public async Task<Invoice> CreateDraftAsync(Guid clientId, InvoiceBody body, CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(body);
    Guid id = Guid.NewGuid();
    await documents.PutAsync(clientId, Drafts, id, body, Tags(body.CustomerId), ct);
    DocumentResult<InvoiceBody>? r = await documents.GetAsync<InvoiceBody>(clientId, Drafts, id, ct);
    return Map(r!);
}

public async Task<Invoice> UpdateDraftAsync(Guid clientId, Guid invoiceId, InvoiceBody body, CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(body);
    if (await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, ct) is null)
        throw new InvalidOperationException($"Invoice {invoiceId} is not an editable draft.");
    await documents.PutAsync(clientId, Drafts, invoiceId, body, Tags(body.CustomerId), ct);
    DocumentResult<InvoiceBody>? r = await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, ct);
    return Map(r!);
}

public async Task DiscardDraftAsync(Guid clientId, Guid invoiceId, CancellationToken ct = default)
{
    if (await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, ct) is null)
        throw new InvalidOperationException($"Invoice {invoiceId} is not a discardable draft.");
    await documents.DeleteAsync(clientId, Drafts, invoiceId, ct);
}

public async Task<Invoice> PromoteDraftAsync(Guid clientId, Guid invoiceId, CancellationToken ct = default)
{
    DocumentResult<InvoiceBody>? draft = await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, ct)
        ?? throw new InvalidOperationException($"Invoice {invoiceId} is not a draft awaiting issue.");
    Guid issuedId = await documents.CreateAsync(clientId, Collection, draft.Body, Tags(draft.Body.CustomerId), ct);
    await documents.FinalizeAsync(clientId, Collection, issuedId, ct);
    await documents.DeleteAsync(clientId, Drafts, invoiceId, ct);
    DocumentResult<InvoiceBody>? issued = await documents.GetAsync<InvoiceBody>(clientId, Collection, issuedId, ct);
    return Map(issued!);
}

public async Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken ct = default)
{
    DocumentResult<InvoiceBody>? draft = await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, ct);
    if (draft is not null) return Map(draft);
    DocumentResult<InvoiceBody>? issued = await documents.GetAsync<InvoiceBody>(clientId, Collection, invoiceId, ct);
    return issued is null ? null : Map(issued);
}

public async Task<IReadOnlyList<Invoice>> GetByCustomerAsync(Guid clientId, Guid customerId, CancellationToken ct = default)
{
    IReadOnlyList<DocumentResult<InvoiceBody>> drafts = await documents.QueryAsync<InvoiceBody>(clientId, Drafts, Tags(customerId), ct);
    IReadOnlyList<DocumentResult<InvoiceBody>> issued = await documents.QueryAsync<InvoiceBody>(clientId, Collection, Tags(customerId), ct);
    return drafts.Concat(issued).Select(Map).ToList();
}
```
`VoidAsync` stays on `Collection`. `Map` is unchanged (a plain `Active` draft already falls to `_ => Draft`; finalized → Issued). Update the class doc comment to describe the two-collection split.

`InvoiceService.cs` — IssueAsync call site only: change `await invoices.FinalizeAsync(clientId, invoiceId, ct)` to `await invoices.PromoteDraftAsync(clientId, invoiceId, ct)`. The pre-flight validate must remain BEFORE this call (it already is). Do not add edit/discard here — that's Task 2.

`InMemoryInvoiceStore.cs` — keep parity: hold two dictionaries (`_drafts`, `_issued`); `CreateDraftAsync` adds to `_drafts` (Status Draft, no number); `UpdateDraftAsync` replaces in `_drafts` (throw if absent); `DiscardDraftAsync` removes from `_drafts` (throw if absent); `PromoteDraftAsync` removes from `_drafts`, inserts into `_issued` under a **new** Guid with the next number, returns it; `GetAsync` checks `_drafts` then `_issued`; `GetByCustomerAsync` concatenates both. Keep any existing settlement helpers operating on `_issued` only.

- [ ] **Step 4: Run, confirm pass** — `InvoiceStoreDraftTests` green; run the full `Accounting101.Receivables.Tests` build to confirm the contract change compiles everywhere (callers of the old `FinalizeAsync` are only `InvoiceService.IssueAsync` + `InMemoryInvoiceStore` users in tests; the endpoint/issue tests still pass at this point because `IssueAsync` behavior is unchanged except the returned invoice now has a fresh id — those are updated in Task 3, so a few may fail here; if `ReceivablesIssueTests`/`ReceivablesVoidTests`/`CashApplicationTests` fail ONLY because they reuse the draft id after issue, that is expected and fixed in Task 3 — note which, do not fix here).

- [ ] **Step 5: Build clean, commit**
```bash
git add Modules/Receivables/Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs \
        Modules/Receivables/Accounting101.Receivables/IInvoiceStore.cs \
        Modules/Receivables/Accounting101.Receivables/DocumentInvoiceStore.cs \
        Modules/Receivables/Accounting101.Receivables/InvoiceService.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/InMemoryInvoiceStore.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/DocumentStoreFixture.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/InvoiceStoreDraftTests.cs
git commit -m "feat(receivables): drafts live in a plain collection, promote to evidentiary on issue"
```

---

## Task 2: Service — edit and discard a draft

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/InvoiceService.cs` (add `EditDraftAsync`, `DiscardDraftAsync`)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/InvoiceServiceDraftTests.cs` (create)

**Interfaces:**
- Consumes: `IInvoiceStore.UpdateDraftAsync` / `DiscardDraftAsync` (Task 1).
- Produces: `Task<Invoice> EditDraftAsync(Guid clientId, Guid invoiceId, Guid customerId, IReadOnlyList<InvoiceLine> lines, decimal taxRate, DateOnly issueDate, DateOnly? dueDate = null, string? memo = null, CT)`; `Task DiscardDraftAsync(Guid clientId, Guid invoiceId, CT)`.

- [ ] **Step 1: Write the failing tests** — `InvoiceServiceDraftTests` (unit, using `InMemoryInvoiceStore` + a fake customers store, same harness style as existing service tests):
```csharp
// 1. EditDraftAsync re-validates (customer must exist, >=1 line) and updates the draft body; GET shows new fields.
// 2. EditDraftAsync on an issued invoice id throws (not a draft) — message mentions void & re-issue.
// 3. EditDraftAsync with zero lines / unknown customer throws (same validation as DraftAsync).
// 4. DiscardDraftAsync removes a draft; a subsequent GET -> null.
// 5. DiscardDraftAsync on an issued invoice id throws (use void).
```

- [ ] **Step 2: Run, confirm fail** — methods don't exist. `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "InvoiceServiceDraftTests"`.

- [ ] **Step 3: Implement** in `InvoiceService.cs`:
```csharp
public async Task<Invoice> EditDraftAsync(
    Guid clientId, Guid invoiceId, Guid customerId, IReadOnlyList<InvoiceLine> lines, decimal taxRate,
    DateOnly issueDate, DateOnly? dueDate = null, string? memo = null, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(lines);
    if (await customers.GetAsync(clientId, customerId, cancellationToken) is null)
        throw new InvalidOperationException($"Customer {customerId} does not exist.");
    if (lines.Count == 0)
        throw new InvalidOperationException("An invoice needs at least one line.");

    InvoiceBody body = new(
        customerId, issueDate, dueDate, taxRate, memo,
        lines.Select(l => new LineBody(l.Description, l.Quantity, l.UnitPrice, l.Taxable, l.RevenueCategory)).ToList());

    return await invoices.UpdateDraftAsync(clientId, invoiceId, body, cancellationToken); // throws if not a draft
}

public Task DiscardDraftAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default) =>
    invoices.DiscardDraftAsync(clientId, invoiceId, cancellationToken); // throws if not a draft
```
(The "not a draft" → clear message is enforced by the store's `UpdateDraftAsync`/`DiscardDraftAsync` guards from Task 1; keep the store messages user-facing.)

- [ ] **Step 4: Run, confirm pass** — `InvoiceServiceDraftTests` green; re-run the rest of the Receivables suite (excluding the Task-3 id-semantics fixes) to confirm no new breakage.

- [ ] **Step 5: Build clean, commit**
```bash
git add Modules/Receivables/Accounting101.Receivables/InvoiceService.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/InvoiceServiceDraftTests.cs
git commit -m "feat(receivables): edit and discard a draft invoice (drafts only)"
```

---

## Task 3: Endpoints — edit/discard routes, issue returns the new id, fix existing tests

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (add PUT + DELETE; issue returns issued invoice)
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesIssueTests.cs`, `ReceivablesVoidTests.cs`, `CashApplicationTests.cs` (use the issued id returned by issue, not the draft id)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesDraftLifecycleTests.cs` (create — edit/discard over HTTP)

**Interfaces:**
- Consumes: `InvoiceService.EditDraftAsync` / `DiscardDraftAsync` (Task 2); the issue path already returns the issued `Invoice` (Task 1's `PromoteDraftAsync`).

- [ ] **Step 1: Write the failing tests** — `ReceivablesDraftLifecycleTests` (real host):
```csharp
// 1. PUT /invoices/{id} edits a draft -> 200; GET shows the new fields.
// 2. PUT /invoices/{id} on an issued invoice -> 409 (or 422), body mentions void & re-issue.
// 3. DELETE /invoices/{id} discards a draft -> 204; GET -> 404.
// 4. DELETE /invoices/{id} on an issued invoice -> 409 (or 422), directs to void.
// 5. POST /invoices/{draftId}/issue -> 200 returning an invoice whose Id != draftId and whose Number is set;
//    GET {draftId} -> 404; GET {issuedId} -> Issued.
```
Also update the three existing suites: after `issue`, capture the returned invoice's `Id` and use THAT for subsequent GET/void/payment calls (today they reuse `draft.Id`). E.g. in `ReceivablesVoidTests` the void target becomes the issued id, not `draft.Id`.

- [ ] **Step 2: Run, confirm fail** — routes 404 (not mapped); existing suites fail where they reuse the draft id post-issue.

- [ ] **Step 3: Implement** in `ReceivablesEndpoints.cs`:
- Register routes next to the existing invoice routes:
```csharp
clients.MapPut("/invoices/{invoiceId:guid}", EditInvoice);
clients.MapDelete("/invoices/{invoiceId:guid}", DiscardInvoice);
```
- `EditInvoice(Guid clientId, Guid invoiceId, DraftInvoiceRequest request, InvoiceService service, CancellationToken ct)`: reuse the same request shape as `DraftInvoice`; call `service.EditDraftAsync(...)`; return `Results.Ok(view)`. Catch the "not an editable draft" `InvalidOperationException` → `Results.Conflict(...)` (or the module's existing error helper) with the message.
- `DiscardInvoice(Guid clientId, Guid invoiceId, InvoiceService service, CancellationToken ct)`: call `service.DiscardDraftAsync(...)`; return `Results.NoContent()`. Catch the "not a discardable draft" exception → `Results.Conflict(...)`.
- `IssueInvoice`: it already returns the issued invoice from `service.IssueAsync` (which now returns the promoted invoice with the new id+number) — confirm the response body carries that invoice (the new id), not the route's draft id. Adjust the mapping if it echoes the route id.
- Follow the module's existing error-translation pattern (match how `DraftInvoice`/`IssueInvoice` currently turn `InvalidOperationException` into HTTP results); do not invent a new error shape.

- [ ] **Step 4: Run, confirm pass** — `ReceivablesDraftLifecycleTests` green; the three updated suites green; then run each Receivables test class individually (EphemeralMongo/host-boot flakiness) and confirm the whole module suite passes. Also run `PaymentServiceTests` to confirm settlement still ignores drafts (a customer's drafts appear in `GetByCustomerAsync` but carry no settlement — unchanged from today).

- [ ] **Step 5: Build clean, commit**
```bash
git add Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesIssueTests.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesVoidTests.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/CashApplicationTests.cs \
        Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesDraftLifecycleTests.cs
git commit -m "feat(receivables): PUT/DELETE draft routes; issue returns the new issued invoice"
```

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run individually (EphemeralMongo): `InvoiceStoreDraftTests`, `InvoiceServiceDraftTests`, `ReceivablesDraftLifecycleTests`, `ReceivablesIssueTests`, `ReceivablesVoidTests`, `CashApplicationTests`, `PaymentServiceTests`, and the document-store tier tests — all green.
- [ ] Confirm: a discarded draft leaves no row in `invoices`; issue consumes the draft and returns a new id+number; editing/discarding an issued invoice is refused; void unchanged.
- [ ] Whole-branch review on the most capable model (a cross-cutting store + service + endpoint change with a behavior shift), then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- Spec coverage: plain `invoice-drafts` (Task 1 manifest+store), edit/discard (Task 1 store + Task 2 service + Task 3 endpoints), promote-on-issue with new id (Task 1 `PromoteDraftAsync` + Task 3 issue response), reads span both tiers (Task 1 GetAsync/GetByCustomerAsync), issued-invoice edit/discard refused (Task 1 guards → Task 3 HTTP), tests incl. updated existing suites (Task 3).
- Type consistency: `PromoteDraftAsync` replaces `FinalizeAsync` across `IInvoiceStore`, `DocumentInvoiceStore`, `InMemoryInvoiceStore`, and the one `InvoiceService.IssueAsync` call site; `UpdateDraftAsync`/`DiscardDraftAsync` added to all three.
- Open implementer checks (flagged): (a) whether `IssueInvoice` echoes the route id vs the returned invoice id (Task 3 Step 3); (b) the module's existing `InvalidOperationException`→HTTP translation pattern, to match it for the new routes (Task 3 Step 3); (c) whether `.Plain("invoice-drafts")` supports the `Customer`-tag `QueryAsync` used by `GetByCustomerAsync` (Task 1 — if plain tag-query isn't supported, surface it rather than work around; a `Plain`-with-tags overload would be the fix).
