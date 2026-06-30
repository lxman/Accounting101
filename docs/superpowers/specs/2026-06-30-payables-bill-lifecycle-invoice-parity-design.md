# Payables bill lifecycle — align to invoices

**Date:** 2026-06-30
**Scope:** `Accounting101.Payables` (+ `.Api`), the Angular payables UI, and affected tests.
**Status:** Approved design, pending implementation plan.

## Problem

Bills and invoices follow different document models, and the bill model is wrong.

- **Invoices** use a two-tier split: plain `invoice-drafts` (freely editable and discardable scratch) and evidentiary `invoices` (append-only, entered only on issue). Issuing a draft creates a **new** evidentiary document with a new id and deletes the draft. This honors the decision *"drafts are scratch, not facts"* — only Issue/Void/Approve are audit events.
- **Bills** use a single evidentiary `bills` collection. A draft is born into the evidentiary collection via `CreateAsync`, and `enter` finalizes it **in place** — same id throughout. There is no discard: `IDocumentStore.DeleteAsync` is plain-only, so the only way to remove a draft bill is to void it, which leaves a Voided record in the evidentiary collection.

This has two consequences:

1. **Discarding a draft bill is impossible** without leaving an audit trace — the known "last gap" on the payables side.
2. **Entering a bill can orphan.** `BillService.EnterAsync` finalizes *before* it posts, so if the engine rejects the post (closed period, chart violation), the bill is already `Entered` with no journal entry. Invoices avoid this by pre-flighting the post *before* finalizing.

The bill lifecycle also conflicts with the project's own stated convention: *"a draft (unissued invoice or unentered bill) is inert."*

## Decision

Bring the bill lifecycle into line with the invoice lifecycle: a draft bill is scratch paper. The vendor's authority lives in their source document, not in our half-keyed capture of it, so discarding a botched draft is throwing away scratch — not reneging on a vendor. Full parity, backend and UI.

The vendor-side flavor bills do carry (they originate outside the business) is honored by preserving `VendorReference` as **display provenance** on the entered bill, never as a dedup key. Duplicate bill entry is a clerk mistake, handled rider-side — the same call made on the receivables side. The only structural duplicate protection is the posting-layer `EntryIdentity` (UUIDv5 source-ref) already in place.

## Target design

### 1. Collection manifest

`PayablesServiceExtensions` gains a plain drafts collection, mirroring `ReceivablesServiceExtensions` lines 19–20:

```csharp
manifest.Reference("vendors");
manifest.Plain("bill-drafts");                  // scratch — editable, discardable
manifest.Evidentiary("bills", "Vendor");        // append-only, entered only on enter
manifest.Evidentiary("bill-payments", "Vendor");
manifest.Evidentiary("vendor-credit-applications", "Vendor");
```

`bill-payments` and `vendor-credit-applications` are unchanged.

### 2. `DocumentBillStore` — mirror of `DocumentInvoiceStore`

Two collection constants: `Drafts = "bill-drafts"` (plain), `Collection = "bills"` (evidentiary). `Tags(Guid vendorId)` stays `["Vendor"] = vendorId.ToString()`. Methods:

| Method | Behavior |
|---|---|
| `CreateDraftAsync` | `Guid id = Guid.NewGuid(); PutAsync(clientId, Drafts, id, body, Tags);` read back; return `Map`. |
| `UpdateDraftAsync` | Guard: `GetAsync(Drafts, id)` non-null else throw `"Bill {id} is not an editable draft."`; `PutAsync`; read back; return. |
| `DiscardDraftAsync` | Guard: draft exists else throw `"Bill {id} is not a discardable draft."`; `DeleteAsync(Drafts, id)`. |
| `PromoteDraftAsync` | `draft = GetAsync(Drafts, id) ?? throw`; `enteredId = CreateAsync(Collection, draft.Body, Tags)`; `FinalizeAsync(Collection, enteredId)`; `DeleteAsync(Drafts, id)`; read back entered; return. |
| `VoidAsync` | `VoidAsync(Collection, id)` — evidentiary, in place. |
| `GetAsync` | Try `Drafts` first, then `Collection`; null if neither. |
| `GetByVendorAsync` | `QueryAsync(Drafts, Tags) concat QueryAsync(Collection, Tags)`, mapped. |

`Map` is unchanged in shape. `Number` stays `BILL-{seq:D5}` (null until finalized). Status mapping is unchanged:

```
Finalized            → Entered
Voided / Superseded  → Void
otherwise            → Draft
```

### 3. `BillService` — mirror of `InvoiceService`

- **`DraftAsync`** — unchanged validation (vendor exists; ≥1 line; every amount > 0; every line has an expense account). Still calls `CreateDraftAsync`.
- **`EditDraftAsync`** *(new)* — re-run the same validation against the new body, then `UpdateDraftAsync`.
- **`DiscardDraftAsync`** *(new)* — delegate to store.
- **`EnterAsync`** *(rewritten)* — mirror `InvoiceService.IssueAsync`, including the preflight that closes the orphan window:

  ```
  draft = RequireAsync(...)
  guard draft.Status == Draft else throw
  guard draft.Total > 0 else throw

  posting   = await accounts.GetBillAccountsAsync(...)
  preflight = BillPosting.ComposeBill(draft, posting)          // Number null → Reference null, validation ignores
  await ledger.ValidateAsync(clientId, preflight, ct)          // engine rejects BEFORE promote → no orphan

  entered = await bills.PromoteDraftAsync(clientId, billId, ct) // new id, finalize, delete draft
  entry   = BillPosting.ComposeBill(entered, posting)          // recompose with assigned number
  await ledger.PostAsync(clientId, entry, ct)                  // lands PendingApproval
  return entered
  ```

  Exceptions: `InvalidOperationException` → 409; `LedgerClientException` → relayed status/reason (already the endpoint pattern).
- **`VoidAsync`** — logic unchanged. It resolves the spawned entry via `GetEntriesBySourceRefAsync(clientId, billId)`. Because the post used the **entered** id (and callers pass the entered id), this remains correct. Void keeps the same evidentiary id (entered → voided, in place).

### 4. HTTP endpoints (`PayablesEndpoints`)

Two new routes; all others unchanged in route shape:

```csharp
clients.MapPut   ("/bills/{billId:guid}", EditBill);       // 200 / 409 (not a draft, bad body)
clients.MapDelete("/bills/{billId:guid}", DiscardBill);    // 204 / 409 (not a draft)
```

- `EditBill` — 200 with the updated draft; `InvalidOperationException` → 409.
- `DiscardBill` — 204; `InvalidOperationException` → 409.
- `EnterBill` — route unchanged; returns the entered bill, which now carries a **new id**. Callers must read the id from the response.

### 5. Impact on payments, credits, and the vendor 360 — safe by construction

`BillPaymentService` (allocations keyed by `bill.Id`), `VendorAccountBuilder.OpenBillLine(b.Id, b.Number, …)`, and `VendorAccountService` operate on **Entered** bills only — settlement is gated to `Entered`. The id changes only at the draft→entered transition and is stable forever afterward. Therefore these consumers need **no changes**, provided every caller takes the entered id from the enter response rather than reusing the draft id. Audited callers (`BillSettlementScenario`, `VendorAccountEndpointE2eTests`, `VendorCreditApplicationListEndpointTests`, `BillPaymentServiceTests`) already assign `Bill entered = …` from the response.

No production code holds a draft id across the enter transition.

### 6. UI

- **`PayablesService` (TS)** — add `editBill(id, body)` → `PUT /bills/{id}` and `discardBill(id)` → `DELETE /bills/{id}`.
- **`BillDetail`**
  - `enter()` must capture the returned entered bill and `router.navigate(['/payables/bills', entered.id])`. Today it reloads `this.id` (the draft id), which will 404 once the draft is deleted.
  - Draft case gains a **Discard** button → confirm → `discardBill(this.id)` → navigate back to `/payables`.
  - The existing *"post-void reload returns status Void"* behavior is preserved: void is in place on the evidentiary collection, so the entered id remains valid after void.
- **`BillEditor`**
  - gains **edit mode**: when a draft id is present (route param), load the draft on init and `PUT` on save; without one, keep create mode (`POST`). Header reads "New bill" or "Edit draft".
  - gains a **Discard** action (visible only in edit mode): `DELETE` the draft, navigate to `/payables`.
  - Mirrors the invoice editor's edit/discard affordance; exact placement confirmed against the invoice editor during planning.

### 7. Test changes

**Update:**
- `DocumentBillStoreTests` — enter/void now span two collections; enter yields a new id; draft is gone after enter.
- `BillServiceTests` — enter returns a new id; add edit-draft and discard-draft cases; add the preflight-rejection-leaves-draft case.
- `BillPostingTests` — composition unchanged; covered by reuse.
- `BillPaymentServiceTests`, `VendorAccountEndpointE2eTests`, `VendorCreditApplicationListEndpointTests`, `BillSettlementScenario` — take the entered id from the enter response (most already do).
- `bill-detail.spec.ts` — enter navigates to the new entered id; discard flow.
- `bill-editor.spec.ts` — edit mode (loads draft, PUTs) and discard.

**Add:**
- `BillDraftLifecycleTests` (mirror of `ReceivablesDraftLifecycleTests`): draft is editable; draft is discardable (hard delete, no audit trace); enter yields a new id and deletes the draft; void keeps the id and marks Void; settlement excludes drafts; duplicate entry is not specially handled (clerk-error parity).

### 8. Dev seed / data

No existing entered bills depend on draft-id stability. The dev seed routes bills through `POST /bills` + `POST /bills/{id}/enter`, both of which continue to work. The seeder will be re-checked during planning; if it holds onto a draft id across enter, it will be updated to read the entered id from the response.

## Out of scope

- Vendor-reference fuzzy dedup (deferred — duplicate entry is a clerk mistake, handled rider-side).
- Bill payment / credit-application lifecycle changes (untouched).
- Any change to the `EntryIdentity` posting-layer idempotency (already correct).

## Acceptance criteria

1. `bill-drafts` is a plain collection; `bills` is evidentiary. A draft bill lives only in `bill-drafts`.
2. Drafts are editable (`PUT /bills/{id}`) and discardable (`DELETE /bills/{id}`, hard delete, no Voided trace).
3. `enter` creates a new evidentiary bill with a new id, assigns its number, deletes the draft, and posts its A/P entry `PendingApproval`.
4. `enter` preflights the post; an engine rejection leaves the document as Draft (no orphan).
5. `void` works on entered bills, keeps the id, marks Void, and reverses/withdraws the entry per existing logic.
6. Payments, credit applications, and the vendor 360 behave exactly as before for entered bills.
7. UI: enter navigates to the entered bill's new id; drafts can be edited and discarded in-app.
8. Full solution test suite is green; the new `BillDraftLifecycleTests` passes.
