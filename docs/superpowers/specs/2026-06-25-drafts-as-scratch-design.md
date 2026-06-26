# Drafts as scratch — plain-tier invoice drafts with edit/discard, promote-on-issue — Design

**Date:** 2026-06-25
**Status:** Spec for review

## Context & principle

A draft invoice is a **work-in-progress form, not a fact**. Like a paper form: you fill it out, erase and fix a field, or wad it up and throw it away — and neither the business nor the auditor cares about the pile of crumpled drafts in the trash. The only events the books record are **Issue / Void / Approve**.

Today drafts violate this: `DocumentInvoiceStore` persists a draft in the **evidentiary** `invoices` collection (created `Active`/Draft, then finalized on issue). The evidentiary tier deliberately has **no delete** (`IDocumentStore.DeleteAsync` is "plain only") — that is *why* a stranded draft can't be removed and accumulates. The fix is to put drafts where they belong — a **plain** collection — and promote to the evidentiary numbered invoice only at issue.

This is "Option A" from the design discussion. The document store was built for it: the **plain** tier has exactly `PutAsync` (create/replace) + `DeleteAsync`; the **evidentiary** tier has `CreateAsync → FinalizeAsync → Void/Supersede` and no delete.

## Design

### 1. Storage split (`DocumentInvoiceStore` + `IInvoiceStore`)

Add a plain collection `invoice-drafts`. Drafts live there; only issued/voided invoices live in evidentiary `invoices`.

| Action (paper analogy) | `IInvoiceStore` method | `IDocumentStore` op | Collection | Audit trace |
|---|---|---|---|---|
| Fill out a form (draft) | `CreateDraftAsync` | `PutAsync(newId, body)` | `invoice-drafts` (plain) | none |
| Erase & fix (edit) | **`UpdateDraftAsync`** (new) | `PutAsync(sameId, body)` | `invoice-drafts` (plain) | none |
| Wad it up (discard) | **`DiscardDraftAsync`** (new) | `DeleteAsync(id)` | `invoice-drafts` (plain) | none — gone |
| File it (issue) | **`PromoteDraftAsync`** (replaces `FinalizeAsync`) | read plain draft → `CreateAsync(invoices, body)` → `FinalizeAsync(invoices, newId)` → `DeleteAsync(invoice-drafts, draftId)` | both | Issue is the first audited fact |
| Void an issued invoice | `VoidAsync` (unchanged) | `VoidAsync(invoices, id)` | `invoices` (evidentiary) | as today |

- **Manifest registration** (`Modules/Receivables/Accounting101.Receivables.Api/ReceivablesServiceExtensions.cs`, alongside the existing `manifest.Evidentiary("invoices", "Customer")`): add `manifest.Plain("invoice-drafts")`. (The `.Plain(collection)` builder takes no indexed tags; drafts are stored with the `Customer` tag and queried via the universal `QueryAsync` — unindexed scan, acceptable for a per-client draft set. If draft-by-customer listing proves hot, a `Plain`-with-indexed-tags overload is a later refinement, called out, not built now.)
- **`PromoteDraftAsync` replaces `FinalizeAsync`** in `IInvoiceStore`. The rename is deliberate: the operation no longer finalizes a doc *in place* — it **creates a new evidentiary document** from the draft body and deletes the draft. Returning a misleadingly-named `FinalizeAsync` that silently creates a new id would hide exactly the behavior change callers must know about. It returns the issued `Invoice` (new id + assigned number).
- **Reads** (`GetAsync`, `GetByCustomerAsync`): an id is in exactly one place — a draft in `invoice-drafts`, an issued/voided invoice in `invoices` (a draft id is deleted when promoted). `GetAsync(id)` checks `invoice-drafts` first, then `invoices`. `GetByCustomerAsync` queries both and merges (drafts read as `Status = Draft`, issued/voided as today). Listing can later expose a status filter (draft vs issued) cheaply since the collections are already separate — but the existing `GetByCustomerAsync` contract (all of a customer's invoices, drafts included) is preserved.
- **`InMemoryInvoiceStore`** (the `IInvoiceStore` test double used by `PaymentServiceTests`) mirrors the same contract: a separate draft map, `UpdateDraftAsync`/`DiscardDraftAsync`, and `PromoteDraftAsync` that moves a draft into the issued map with a new id + number.

### 2. Service (`InvoiceService`)

- `DraftAsync` — unchanged validation (customer exists, ≥1 line); now persists via the plain `CreateDraftAsync`.
- **`EditDraftAsync(clientId, invoiceId, …fields)`** (new) — re-run the same validation as draft, then `UpdateDraftAsync`. Only on a Draft; if the id is an issued invoice, fail with a clear "an issued invoice cannot be edited; void and re-issue" message.
- **`DiscardDraftAsync(clientId, invoiceId)`** (new) — `DiscardDraftAsync` on the store. Only on a Draft; an issued invoice must use Void (which reverses its entry), not discard.
- `IssueAsync` — keep the pre-flight ordering that prevents orphans: **(1)** compose the would-be A/R entry and **validate it** via the engine's side-effect-free `POST /entries/validate` (existing pre-flight); **(2)** only if valid, **`PromoteDraftAsync`** (creates the evidentiary invoice, assigns the number); **(3)** recompose the entry with the assigned number and **post** it (`PendingApproval`, per SoD — the module never approves); **(4)** the plain draft is deleted as part of promote. A bad date/chart violation is caught at step 1, before any evidentiary write — the draft stays intact, nothing is created. The residual promote→post window (infrastructure failure only; validation failures can't reach it) is the same irreducible TOCTOU the current design has, and remains the **orphan backstop's** responsibility (existing backlog, out of scope here).

### 3. Endpoints (`ReceivablesEndpoints`)

- `POST /invoices` (DraftInvoice) — unchanged; creates a plain draft.
- **`PUT /invoices/{invoiceId}`** (new, EditDraft) — edit a draft's fields. Drafts only → 200 with the updated draft; issued → 409/422 with the "void and re-issue" message.
- **`DELETE /invoices/{invoiceId}`** (new, DiscardDraft) — discard a draft. Drafts only → 204; issued → 409/422 directing to void.
- `POST /invoices/{invoiceId}/issue` — **now consumes the draft and returns the issued invoice** (new id + number). See the consequence below.
- `POST /invoices/{invoiceId}/void` — unchanged (issued invoices).
- `GET /invoices/{invoiceId}` and `GET /invoices?customerId=` — return drafts (plain) and issued (evidentiary) as today, via the two-collection reads.

### The one behavior change (signed off)

Because an evidentiary document is assigned its **id and gapless number at creation**, the issued invoice is a **distinct artifact** from the draft that produced it. `POST /invoices/{draftId}/issue` returns the **new issued invoice**; the draft id is consumed and no longer resolves. This matches the analogy — the scribbled form is not the filed, numbered invoice. It is a visible API change (issue returns a new id), accepted in design.

## Forward compatibility — templates / recurring (planned follow-up, NOT built here)

Because drafts are now cheap, freely-kept plain documents, a "stack of pre-made drafts for regular situations" (rent, monthly retainer, standard package) falls out naturally as a **follow-up slice**: a `clone-from-draft` operation (copy a kept draft's body into a fresh draft, set the date, issue the clone) plus perhaps a `template` tag for listing. Issue consumes the *clone*, so the template survives for next month. This slice's design is forward-compatible with that — nothing here needs to pre-build it.

## Out of scope

- Templates / recurring (the follow-up above).
- Structured error messages (separate slice).
- Partial-failure recovery for the promote→post window (existing orphan-backstop backlog).
- A `Plain`-with-indexed-tags overload (only if draft-by-customer listing proves hot).

## Testing

Module tests (`Accounting101.Receivables.Tests`, real host + `InMemoryInvoiceStore` unit level):

- **Edit a draft** persists the change (PUT then GET shows new fields); editing an **issued** invoice is refused with the void-and-re-issue message.
- **Discard a draft** removes it (DELETE then GET → 404); the draft leaves **no evidentiary trace** (it was never in `invoices`); discarding an **issued** invoice is refused (use void).
- **Issue promotes:** after issue, the draft id no longer resolves, a new evidentiary invoice exists with an assigned number, the A/R entry is posted `PendingApproval`, and issue returns the new id.
- **Pre-flight failure leaves the draft intact:** a draft whose entry would violate the period freeze / chart fails issue, **no** evidentiary invoice is created, and the draft is still editable/issuable — proving promote happens only after validation (no orphan).
- **Void unchanged:** an issued invoice still voids (reverses its entry).
- **Reads span both tiers:** GET by id resolves a draft (plain) and an issued invoice (evidentiary); `GET /invoices?customerId=` returns both a customer's drafts and issued invoices.
- Existing `ReceivablesIssueTests` / `ReceivablesVoidTests` / `CashApplicationTests` updated for the new issue-returns-new-id semantics where they assumed the draft id survives issue.

## Global constraints

- .NET 10; build 0 warnings; commit per task; TDD.
- Drafts never enter the evidentiary record; only Issue/Void are audited facts.
- Issue stays pre-flight-then-promote-then-post (no orphan from a validation failure).
- Module never approves its own entries (post `PendingApproval`).
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
