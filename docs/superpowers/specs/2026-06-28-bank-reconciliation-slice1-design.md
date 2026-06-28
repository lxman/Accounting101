# Bank Reconciliation — Slice 1 (Statements + Matching + Report) — Design

**Date:** 2026-06-28
**Status:** Approved (design)

## Context

Accounting101 has an empty `Accounting101.Banking.Reconciliation` stub (just a
`ReconciliationModule` class; no `.Api`/`.Tests`). Bank reconciliation is being
built as four shippable slices:

1. **Core — statements + matching + report (read-only)** ← THIS SPEC
2. Auto-match by amount
3. Adjustments posting (fees/interest, maker-checker)
4. CSV/OFX import

Slice 1 is the foundation: load a bank statement, match its lines against the
ledger's cash-account entries by manually clearing entries, and produce a
reconciliation report that says whether the books agree with the bank.

## Goal

A working, read-only bank-reconciliation core that mirrors the existing module
patterns (Cash/Receivables/Payables): record a `BankStatement`, start a
`Reconciliation` against it, clear/unclear the ledger cash entries, and read a
worksheet that computes the reconciled difference and a "balanced" verdict.
**Slice 1 never posts to the GL** — it owns its own documents and only *reads*
ledger entries.

## Scope

**In scope:** the `Accounting101.Banking.Reconciliation` domain + `.Api` + `.Tests`
projects; bank-statement entry (structured JSON); a reconciliation document
tracking the cleared set; the worksheet + cleared-method math; manual
clear/unclear; complete-when-balanced.

**Out of scope (later slices / not built):** auto-matching (Slice 2), posting
fee/interest adjustments to the GL (Slice 3), CSV/OFX file import (Slice 4),
multi-period carry-forward of outstanding items, per-statement-line ↔ per-entry
explicit pairing, and any GL mutation whatsoever.

## Architecture

A new module mirroring the Cash module's one-step pattern, but **read-only on the
ledger**. Three projects:

- `Accounting101.Banking.Reconciliation` — domain records + request bodies +
  views + the cleared-method math + the `IReconciliationLedgerReader` seam +
  document-store interfaces.
- `Accounting101.Banking.Reconciliation.Api` — HTTP endpoints, module
  registration (`AddReconciliation`), the `HttpReconciliationLedgerReader`
  (forwards the caller's bearer token to the engine's read endpoints; **no module
  credential**, since this slice posts nothing).
- `Accounting101.Banking.Reconciliation.Tests` — service-level unit tests (the
  cleared-method math against fakes) + an E2E host fixture.

### Domain records

```
BankStatement        { Guid Id; string? Number /* "BST-{seq:D5}" */; Guid CashAccountId;
                       DateOnly StatementDate; decimal OpeningBalance; decimal ClosingBalance;
                       IReadOnlyList<BankStatementLine> Lines; BankStatementStatus Status /* Posted|Void */ }
BankStatementLine    { Guid Id; DateOnly Date; decimal Amount /* signed: + into account, − out */;
                       string Description; string? ExternalRef }
Reconciliation       { Guid Id; string? Number /* "REC-{seq:D5}" */; Guid CashAccountId;
                       Guid BankStatementId; DateOnly StatementDate;
                       ReconciliationStatus Status /* InProgress|Completed */;
                       IReadOnlyList<Guid> ClearedEntryIds }
```

Request bodies: `BankStatementBody(Guid CashAccountId, DateOnly StatementDate,
decimal OpeningBalance, decimal ClosingBalance, IReadOnlyList<BankStatementLineBody> Lines)`;
`BankStatementLineBody(DateOnly Date, decimal Amount, string Description, string? ExternalRef)`;
`StartReconciliationBody(Guid BankStatementId)`; `ClearBody(IReadOnlyList<Guid> EntryIds)`.

### The cleared-method math (the heart of the slice)

Each ledger entry touching the cash account has a **signed book cash effect** =
the signed amount of its cash-account line (Debit → `+Amount`, Credit →
`−Amount`). Given a reconciliation's `ClearedEntryIds` and its statement:

- `clearedTotal` = Σ(book cash effect of the cleared entries)
- `reconciledDifference = ClosingBalance − (OpeningBalance + clearedTotal)`
- `balanced` ⇔ `reconciledDifference == 0`

Interpretation: uncleared entries are *outstanding* (booked but not yet on the
bank — correctly excluded from the cleared total). A non-zero
`reconciledDifference` is the net of *bank-only* items (fees/interest) not yet in
the books — surfaced here, posted in Slice 3.

### The worksheet (the GET on a reconciliation)

`ReconciliationWorksheet` =
- the `Reconciliation` (status, cleared ids),
- the statement (opening/closing/lines),
- the **ledger entries touching the cash account through `StatementDate`** —
  each as `{ entryId, date, reference, sourceType, cashEffect (signed), cleared (bool) }`,
- `bookBalance` (ledger cash balance as-of `StatementDate`),
- `clearedTotal`, `reconciledDifference`, `balanced`.

### Read ledger seam

`IReconciliationLedgerReader`:
- `Task<IReadOnlyList<EntryResponse>> GetEntriesTouchingAccountAsync(Guid clientId, Guid accountId, CancellationToken ct = default)`
  → the engine's `GET /clients/{clientId}/entries?account={accountId}`.
- `Task<decimal> GetCashBalanceAsync(Guid clientId, Guid accountId, DateOnly asOf, CancellationToken ct = default)`
  → reads the cash account's signed balance from `GET /clients/{clientId}/trial-balance?asOf=`.

The service computes each entry's cash effect from `EntryResponse.Lines`
(filtering to the cash account; Debit `+`, Credit `−`). Entries dated after
`StatementDate` are excluded from the worksheet (the reconciliation is as-of the
statement date).

### Service

`ReconciliationService` (deps: `IBankStatementStore`, `IReconciliationStore`,
`IReconciliationLedgerReader`):
- `RecordStatementAsync(clientId, BankStatementBody)` — validates (lines non-empty,
  `OpeningBalance + Σ(line amounts) == ClosingBalance`), persists the statement.
- `GetStatementAsync` / `ListStatementsAsync(clientId, cashAccountId)`.
- `StartReconciliationAsync(clientId, BankStatementId)` — creates an InProgress
  reconciliation with an empty cleared set for that statement's cash account/date.
- `GetWorksheetAsync(clientId, reconciliationId)` — builds the worksheet above.
- `ClearAsync` / `UnclearAsync(clientId, reconciliationId, entryIds)` — add/remove
  entry ids from the cleared set (only entries that actually touch the cash
  account and are dated ≤ `StatementDate` may be cleared; otherwise 422).
- `CompleteAsync(clientId, reconciliationId)` — flips to Completed **only if
  balanced**, else throws (→ 409).

### HTTP surface (`/clients/{clientId}`)

| Route | Method | Body | Success | Errors |
|---|---|---|---|---|
| `/bank-statements` | POST | `BankStatementBody` | 201 `BankStatement` | 422 (bad lines / opening+lines≠closing) |
| `/bank-statements/{id}` | GET | — | 200 `BankStatement` | 404 |
| `/bank-statements?cashAccountId=` | GET | — | 200 `IReadOnlyList<BankStatement>` | 400 (missing/empty id) |
| `/reconciliations` | POST | `StartReconciliationBody` | 201 `Reconciliation` | 422 (unknown statement) |
| `/reconciliations/{id}` | GET | — | 200 `ReconciliationWorksheet` | 404 |
| `/reconciliations/{id}/clear` | POST | `ClearBody` | 200 `ReconciliationWorksheet` | 422 (entry not on the cash account / after date), 409 (completed) |
| `/reconciliations/{id}/unclear` | POST | `ClearBody` | 200 `ReconciliationWorksheet` | 409 (completed) |
| `/reconciliations/{id}/complete` | POST | — | 200 `Reconciliation` | 409 (not balanced / already completed) |

All under `RequireAuthorization()`, like the other modules. (Role policy:
recording/clearing is a Clerk/Controller activity; no GL maker-checker applies
because nothing is posted. A dedicated "sign-off" approval is deferred.)

### Document store + manifest

Module key `"reconciliation"`, registered via `AddReconciliation` mirroring
`AddCash`:
- `Evidentiary("bank-statements")` — a statement is immutable evidence of what the
  bank reported.
- `Plain("reconciliations")` — the cleared set is edited (clear/unclear) while
  InProgress; the status flips to Completed. (Plain = freely editable scratch, per
  the existing manifest vocabulary.)

Stores: `IBankStatementStore`/`DocumentBankStatementStore`,
`IReconciliationStore`/`DocumentReconciliationStore`, following the
`DocumentCash*Store` pattern (create → finalize for statements; create + update
for reconciliations). Numbers derived from the document sequence (`"BST-"` /
`"REC-"`).

## Data flow

```
POST /bank-statements (JSON) → validate (opening+lines==closing) → store evidentiary
POST /reconciliations {bankStatementId} → InProgress reconciliation, empty cleared set
GET  /reconciliations/{id}
   → reader.GetEntriesTouchingAccountAsync(cashAccountId) [≤ StatementDate]
   → reader.GetCashBalanceAsync(cashAccountId, StatementDate)
   → compute cashEffect per entry, clearedTotal, reconciledDifference, balanced
   → worksheet
POST /reconciliations/{id}/clear|unclear {entryIds} → mutate cleared set → worksheet
POST /reconciliations/{id}/complete → if balanced → Completed, else 409
```

## Error handling

- Bad statement (empty lines, or `OpeningBalance + Σ lines ≠ ClosingBalance`) →
  422 with a clear message.
- Clearing an entry that doesn't touch the cash account, or is dated after the
  statement date → 422.
- Clear/unclear on a Completed reconciliation → 409.
- Complete when `reconciledDifference ≠ 0` → 409 naming the difference.
- Missing required `cashAccountId` filter on the list endpoint → 400 (mirrors the
  module list-filter convention).

## Testing

- **Service unit tests** (fakes: in-memory stores + a fake ledger reader returning
  canned entries/balance) for the cleared-method math: clearing the matching
  entries drives `reconciledDifference` to 0 and `balanced` true; an outstanding
  (uncleared) entry leaves the books higher than the cleared total but the
  reconciliation can still be balanced against the bank; a bank-only residual
  leaves a non-zero difference; complete is refused while unbalanced and accepted
  when balanced; clearing a non-cash-account or after-date entry throws.
- **E2E** through a host fixture (mirror/extend `CashHostFixture`): seed a chart
  with a cash account; post + approve a couple of cash entries via the Cash module
  (a deposit and a disbursement) so real ledger entries exist; record a matching
  bank statement; start a reconciliation; clear the two entries; assert the
  worksheet's `clearedTotal` / `reconciledDifference == 0` / `balanced == true`;
  assert `complete` is 409 before clearing and 200 after; assert a bank-only
  residual yields a non-zero difference and `complete` 409.

## Success criteria

- The three new projects build and the module registers into the host.
- A statement can be recorded (JSON), a reconciliation started, entries
  cleared/uncleared, and the worksheet computes the cleared-method difference and
  balanced verdict correctly.
- `complete` is gated on `balanced`.
- No GL mutation occurs anywhere in this slice; no product behavior in other
  modules changes.
- New unit + E2E tests green; the existing suites stay green.
