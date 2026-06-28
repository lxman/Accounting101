# Bank Reconciliation — Slice 3 (Adjustments Posting) — Design

**Date:** 2026-06-28
**Status:** Approved (design)

## Context

Bank reconciliation, four slices:

1. Core — statements + matching + report (read-only) ✅ SHIPPED (master `2c2970e`)
2. Auto-match by amount ✅ SHIPPED (master `0ccb092`)
3. **Adjustments posting (fees/interest, maker-checker)** ← THIS SPEC
4. CSV/OFX import

Slices 1-2 are **read-only on the GL**: they match the bank against the ledger
and surface a `reconciledDifference`, but a *bank-only* item (a fee the bank
charged, interest it paid) that the books don't yet have leaves a non-zero
residual that manual clearing can't close. Slice 3 makes Reconciliation **post**
those adjustments to the GL — its first GL writes — so the residual can be
booked and cleared.

## Goal

Let a clerk record a bank-only adjustment against a reconciliation; the module
posts a balanced journal entry (PendingApproval, `ViaModule="reconciliation"`)
through module-identity posting; a distinct Approver approves it via the engine's
existing approval flow (maker-checker / SoD); once Posted it is a normal eligible
cash entry that clearing — or Slice 2 auto-match — drives the residual to zero.
Adjustments are voidable. The cleared-method math is unchanged.

## The residual sign (drives the composer)

`reconciledDifference = ClosingBalance − (OpeningBalance + clearedTotal)`.
- A **fee** (bank charged $5; money out; not in the books) makes the bank close
  $5 **lower** than the books expect → residual **−5**. The book fix is to reduce
  cash: **Dr offset (expense) / Cr Cash** for $5.
- **Interest** (bank paid $5; money in; not in the books) → residual **+5**. The
  fix increases cash: **Dr Cash / Cr offset (income)** for $5.

These map directly to the two adjustment kinds below. Once the adjustment is
**approved** (Posted) and dated ≤ the statement date, it appears as an eligible
cash entry whose `CashEffect` is `−5` (Charge) / `+5` (Credit); clearing it makes
`clearedTotal` absorb the residual and `reconciledDifference` → 0.

## Scope

**In scope:** Reconciliation becomes a posting module — a full `ILedgerClient`
(Post/Reverse/Void/GetEntriesBySourceRef) with a **credentialed** HTTP client; a
pure `AdjustmentPosting` composer; a `BankAdjustment` evidentiary document +
store; a focused `AdjustmentService`; the adjustment endpoints (record / list /
get / void); a `LedgerClientException` so posting failures relay as clean 4xx;
registration + manifest updates; unit + service + E2E tests.

**Out of scope (later / not built):** CSV/OFX import (Slice 4); a Reconciliation
approval endpoint (maker-checker reuses the engine's `/entries/{id}/approve`);
auto-suggesting an adjustment from the residual (the clerk chooses the offset
account + amount); configured fee/interest accounts (the offset account is
caller-supplied); editing a posted adjustment (void + re-record instead);
multi-line adjustments (one offset + one cash line only).

## Architecture

Mirrors the **Cash module's** posting path (the established pattern). Slice 1's
read-only `IReconciliationLedgerReader` stays for worksheet reads; Slice 3 adds a
separate posting seam.

### Domain (`Accounting101.Banking.Reconciliation`)

```
enum AdjustmentKind { Charge, Credit }              // Charge = fee (Cr Cash), Credit = interest (Dr Cash)
enum BankAdjustmentStatus { Posted, Void }

record BankAdjustment {
  Guid Id; string? Number /* "ADJ-{seq:D5}" */; Guid ReconciliationId; Guid CashAccountId;
  Guid OffsetAccountId; AdjustmentKind Kind; decimal Amount; DateOnly Date; string? Memo;
  BankAdjustmentStatus Status /* default Posted */ }

record BankAdjustmentBody(                          // stored evidentiary body (Number/Status/Id derived)
  Guid ReconciliationId, Guid CashAccountId, Guid OffsetAccountId,
  AdjustmentKind Kind, decimal Amount, DateOnly Date, string? Memo);
```

- `IBankAdjustmentStore`: `RecordAsync(clientId, BankAdjustmentBody)→Task<BankAdjustment>`,
  `VoidAsync(clientId, id)→Task`, `GetAsync(clientId, id)→Task<BankAdjustment?>`,
  `GetByReconciliationAsync(clientId, reconciliationId)→Task<IReadOnlyList<BankAdjustment>>`.
  `DocumentBankAdjustmentStore` = evidentiary (create→finalize→`ADJ-` number),
  mirroring `DocumentCashDisbursementStore`; list filters `Body.ReconciliationId`.

### Posting seam (full `ILedgerClient` — mirror Cash's)

```
interface ILedgerClient {
  Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken = default);
  Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken = default);
  Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken = default);
  Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken = default);
}
```

`HttpLedgerClient` (Api), ctor `(HttpClient http, IHttpContextAccessor context,
[FromKeyedServices("reconciliation")] ModuleCredential credential)`:
- `PostAsync` attaches `X-Module-Key`/`X-Module-Secret` (so the engine authorizes
  the module post and stamps `ViaModule="reconciliation"`) + forwards the bearer.
- `Reverse`/`Void`/`GetEntriesBySourceRef` forward only the bearer (no credential).
- On a non-success response, `Post`/`Reverse`/`Void` throw a `LedgerClientException`
  (status + reason parsed from the body) instead of `EnsureSuccessStatusCode`'s
  opaque 500 — so a closed-period post or an unknown offset account relays as a
  clean 4xx. (`LedgerClientException` + `EnsureSuccessAsync`/`ReasonFrom` mirror
  the Receivables/Payables clients.)

### Composer (pure, `AdjustmentPosting.cs`)

```
static PostEntryRequest Compose(Guid adjustmentId, BankAdjustmentBody body)
```
- Validates `body.Amount > 0` and `body.OffsetAccountId != body.CashAccountId`
  (else `ArgumentException`).
- **Charge:** `Dr OffsetAccountId Amount`, `Cr CashAccountId Amount`.
  **Credit:** `Dr CashAccountId Amount`, `Cr OffsetAccountId Amount`.
- `Id = EntryIdentity.ForSource("BankAdjustment", adjustmentId)` (idempotent),
  `EffectiveDate = body.Date`, `SourceRef = adjustmentId`,
  `SourceType = "BankAdjustment"`, `Reference = "ADJ"`, `Memo = body.Memo`,
  `Type` left default (Standard).

### Service (`AdjustmentService.cs`)

Deps: `IReconciliationStore reconciliations`, `IBankAdjustmentStore adjustments`,
`ILedgerClient ledger`.
- `RecordAdjustmentAsync(clientId, reconciliationId, RecordAdjustmentInput input)`
  — load the reconciliation (not-found or Completed → `InvalidOperationException`),
  build `BankAdjustmentBody` (CashAccountId from the reconciliation; `Date` =
  `input.Date ?? reconciliation.StatementDate`), validate (amount > 0, offset ≠
  cash → `ArgumentException`), `adjustments.RecordAsync`, `AdjustmentPosting.Compose`,
  `ledger.PostAsync` (PendingApproval). Returns the `BankAdjustment`.
- `VoidAdjustmentAsync(clientId, reconciliationId, adjustmentId, reason)` — load
  the adjustment (404 via null → handler), find its entry via
  `GetEntriesBySourceRefAsync(adjustmentId)`; if the entry is `Posted` →
  `ReverseAsync` (reversal dated the adjustment date), else `VoidAsync`; then
  `adjustments.VoidAsync`. Returns the voided `BankAdjustment`. (Mirrors
  `CashService.VoidDisbursementAsync`.)
- `GetAdjustmentAsync`, `ListAdjustmentsAsync(clientId, reconciliationId)`.

`RecordAdjustmentInput(Guid OffsetAccountId, decimal Amount, AdjustmentKind Kind, DateOnly? Date, string? Memo)`.

### HTTP surface (under `/clients/{clientId:guid}`, RequireAuthorization)

| Route | Verb | Body | Success | Errors |
|---|---|---|---|---|
| `/reconciliations/{id:guid}/adjustments` | POST | `RecordAdjustmentRequest(OffsetAccountId, Amount, Kind, Date?, Memo?)` | 201 `BankAdjustment` | 422 (amount ≤ 0 / offset == cash); 409 (reconciliation not found / Completed); relayed engine 4xx (e.g. closed period, unknown account) |
| `/reconciliations/{id:guid}/adjustments` | GET | — | 200 `IReadOnlyList<BankAdjustment>` | — |
| `/reconciliations/{id:guid}/adjustments/{adjId:guid}` | GET | — | 200 `BankAdjustment` | 404 |
| `/reconciliations/{id:guid}/adjustments/{adjId:guid}/void` | POST | `VoidReasonRequest?(Reason?)` | 200 `BankAdjustment` | 409 (no entry / already void); relayed engine 4xx |

Error mapping: `ArgumentException`→422, `InvalidOperationException`→409,
`LedgerClientException`→its relayed status, null adjustment→404.

### Registration (`AddReconciliation`)

Add to the existing registration:
- `manifest.Evidentiary("bank-adjustments")`.
- `services.AddScoped<IBankAdjustmentStore>(sp => new DocumentBankAdjustmentStore(sp.GetRequiredKeyedService<IDocumentStore>("reconciliation")))`.
- `services.AddScoped<AdjustmentService>()`.
- A second named client `"ReconciliationPostingClient"` →
  `.AddTypedClient<ILedgerClient, HttpLedgerClient>()` (credentialed). The Slice 1
  `"ReconciliationLedgerClient"` (read-only reader) is unchanged. The
  `ModuleCredential` keyed `"reconciliation"` already exists from `AddModule`.

## Data flow

```
POST /reconciliations/{id}/adjustments { offset, amount, Charge }
  → load reconciliation (open) → BankAdjustmentBody (cash = rec.CashAccountId, date = rec.StatementDate)
  → adjustments.RecordAsync (ADJ- doc) → AdjustmentPosting.Compose (Dr offset / Cr cash)
  → ledger.PostAsync (PendingApproval, ViaModule=reconciliation)   [credentialed]
POST /clients/{c}/entries/{entryId}/approve   (engine; distinct Approver, SoD)   → Posted
GET  /reconciliations/{id}  → the adjustment now shows as an eligible cash entry (CashEffect −amount)
POST /reconciliations/{id}/auto-match?apply=true  (or /clear)  → clears it → reconciledDifference 0
POST /reconciliations/{id}/complete  → 200
```

## Error handling

- `amount ≤ 0` or `offset == cash account` → 422.
- Reconciliation not found / Completed → 409.
- Engine rejects the post (closed period for the chosen date, unknown offset
  account, unbalanced — shouldn't happen) → `LedgerClientException` relayed as the
  engine's status + message (not a 500).
- Void when the entry was never found / already void → 409.

## Testing

- **Composer unit tests** (`AdjustmentPostingTests`, pure): a Charge composes
  Dr offset / Cr cash for the amount; a Credit composes Dr cash / Cr offset; the
  entry carries `SourceType="BankAdjustment"`, `SourceRef=id`, the
  `EntryIdentity.ForSource` id, and `EffectiveDate=body.Date`; amount ≤ 0 and
  offset == cash throw.
- **Service tests** (`AdjustmentServiceTests`, fakes — in-memory adjustment store +
  a fake `ILedgerClient` that records the posted request and models
  approve/reverse/void): recording a Charge posts a PendingApproval entry with the
  right lines and stores an `ADJ-` doc; recording against a Completed reconciliation
  throws; void of a pending adjustment calls `VoidAsync` and marks the doc Void;
  void of an approved (Posted) adjustment calls `ReverseAsync`; list returns the
  reconciliation's adjustments.
- **E2E** (`AdjustmentE2eTests`, real host): seed SoD client + chart (cash, an
  expense offset account); post + approve a cash deposit so the cash account has a
  balance; record a statement that *foots but includes a bank fee line the books
  lack* (so the reconciliation has a non-zero residual after clearing the real
  entries); start a reconciliation; **record a Charge adjustment** → 201, entry is
  PendingApproval; **approve it** via the engine endpoint (distinct Approver);
  the worksheet now lists the adjustment entry; clear it (or auto-match) → balanced;
  complete → 200. Plus: record + **void** an adjustment (pending → voided);
  recording with `amount = 0` → 422.

## Success criteria

- A bank-only fee/interest can be recorded as an adjustment that posts a balanced
  PendingApproval entry stamped `ViaModule="reconciliation"`, with module-credential
  authorization.
- Maker-checker holds: the posting is PendingApproval; a distinct Approver approves
  it via the engine; once Posted it clears the residual and the reconciliation
  completes.
- Adjustments are voidable (reverse if Posted, void if Pending) and listable per
  reconciliation.
- Posting failures relay as clean 4xx, not 500.
- The cleared-method math and Slices 1-2 behavior are unchanged; no other module
  changes. New unit + service + E2E tests green; existing suites stay green.
