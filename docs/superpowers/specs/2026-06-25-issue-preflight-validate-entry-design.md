# Proactive issue pre-flight via an engine validate-entry primitive — Design

**Date:** 2026-06-25
**Status:** IMPLEMENTED (confirmed 2026-08-09). All three components shipped, plus the Payables parity
fast-follow. Engine primitive: `LedgerEndpoints.ValidateEntry` (`POST /clients/{id}/entries/validate`,
shared no-drift validation routine). Client method: `ILedgerClient.ValidateAsync` on both module SDKs.
Pre-flight: `InvoiceService.IssueAsync` (Receivables) and `BillService.EnterAsync` (Payables) both
validate the draft-composed entry before promote. Tests: `ValidateEntryTests` (incl. validate/post
parity), `InvoiceServiceTests.Issuing_into_a_closed_period_throws_LedgerClientException_and_leaves_invoice_draft`,
Payables `BillDraftLifecycleTests`. The residual TOCTOU race remains out of scope (close-time backstop).
**Branch context:** follows `fix(receivables): surface the engine's rejection reason on issue` (step 1).

## Problem

Issuing an invoice is a **compound act**: the receivables module finalizes the document (assigning the
invoice number) and then posts the A/R entry to the engine. These two halves run against two systems
(the document store and the engine) with no shared transaction, so they can disagree: `FinalizeAsync`
succeeds, the post is rejected, and the invoice is stranded `Issued` with no ledger entry — an orphan.

The sim surfaced exactly this: a clerk stamped invoices with a stale example date (`2024-01-15`,
already closed), `EnsureOpenAsync` correctly refused the post (409), and six invoices orphaned. The
engine did the right thing; the *document layer* let a predictable, clerk-fixable mistake corrupt state.

## Principle

The journal never holds an unbalanced entry, because it checks balance **before** it writes — the
degradation never happens, so there is nothing to repair. The document layer should hold itself to the
same bar: **never finalize a document whose entry the journal would reject.** Validate the would-be post
*before* committing the finalize. A clerk's bad date is then caught while the invoice is still a draft —
fix the date and re-issue, no number burned, no orphan, no supervisor.

Honest limit of the analogy: the journal can be perfectly proactive because its check and write share
one transaction (zero window). Issue spans two systems over HTTP, so a pre-check shrinks the failure
window to the irreducible TOCTOU sliver — the period closes (or a chart changes) between "validate: ok"
and "post." That residual is **out of scope here** and is covered by the month-end-close exception
backstop (separate spec). This spec eliminates every *predictable* rejection; the backstop catches the
rare race.

## Approach

Give the engine a **dry-run validation primitive** that runs the *exact same* post-validation pipeline
(balance, chart, period freeze) but writes nothing, and have the module pre-flight every issue through
it. Using the engine's own rules — not a second copy in the module — is the whole point: a pre-check
that drifts from the real post is worse than none (it passes, then the post fails anyway).

### Component 1 — Engine: `POST /clients/{clientId}/entries/validate`

A side-effect-free dry run of a post.

- **Request:** `PostEntryRequest` (identical to the real post body).
- **Auth:** requires the same `Post` permission — you are validating a post you intend to make.
- **Behavior:** runs the same checks `POST /entries` runs, in the same order, and **writes nothing**:
  1. Map + balance (`MapEntry` → `ArgumentException`/`UnbalancedEntryException`).
  2. Chart validity (`ChartViolationsAsync` — account exists, postable, required dimension present).
  3. Period freeze (`EnsureOpenAsync` — effective date not in a closed period).
- **Responses:**
  - `200 OK` → `{ "valid": true }` — this entry would post.
  - `422 Unprocessable Entity` (ProblemDetails) — unbalanced / bad account / missing dimension (same
    `detail` a real post returns).
  - `409 Conflict` (ProblemDetails) — effective date in a closed period (same `detail` as a real post).
  - `401/403` — auth, as usual.

**No-drift requirement:** `PostEntry` and `ValidateEntry` must run *the same* validation code, not two
copies. Extract the pre-write validation (map+balance, chart, period) into one routine that returns
either a rejection or the validated entry; `PostEntry` calls it then writes the validated entry,
`ValidateEntry` calls it and returns `{valid:true}` or the rejection. The period check (`EnsureOpenAsync`,
today private in `LedgerService`) is exposed as a service method that **both** `PostAsync` and the
validation routine call, so the freeze rule cannot diverge between dry-run and real post.

The engine stays domain-agnostic: `validate` knows nothing about invoices — it validates any entry, so
payables (and any future module) reuse it unchanged.

### Component 2 — Module: `ILedgerClient.ValidateAsync`

- `Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken ct)`.
- `HttpLedgerClient`: `POST clients/{clientId}/entries/validate`, forwarding the caller's token, then
  `await EnsureSuccessAsync(response, ct)` — reusing the step-1 helper. So a 409/422 throws the typed
  `LedgerClientException` (status + reason); a 200 returns normally. "Validation failed" surfaces through
  the same path step 1 built; nothing new is needed to relay the reason.
- `FakeLedgerClient` (tests) gains a configurable validation outcome (default: passes) so module tests
  can drive both the accept and reject branches without HTTP.

### Component 3 — Module: `IssueAsync` pre-flights before finalize

`InvoiceService.IssueAsync` is reordered so the post is validated against the draft **before** the
document is finalized:

1. Load the draft; existing guards (`Status == Draft`, `Total > 0`).
2. Resolve posting accounts.
3. Compose the entry from the **draft** — `EffectiveDate = draft.IssueDate`, the revenue/tax/A-R lines,
   the customer dimension. `Reference` is the invoice number, which is null on a draft; validation
   ignores `Reference`, so this is fine.
4. `await ledger.ValidateAsync(clientId, draftEntry, ct)` — if the engine would reject (closed period,
   chart), this throws `LedgerClientException`, which propagates out of `IssueAsync`. **The invoice is
   never finalized: it stays a `Draft`, no number consumed, no orphan.** The issue endpoint's existing
   `catch (LedgerClientException)` relays the 409/422 with the reason to the clerk.
5. Only on a clean validation: `FinalizeAsync` (assign number) → recompose with the now-assigned number
   (so the posted entry's `Reference` carries the invoice number for drill-down) → `PostAsync`.

`Compose` needs no change — it already derives `Reference` from `invoice.Number` (null on a draft) and
everything else from fields a draft already carries.

## Data flow (happy path vs. typo)

```
Clerk issues INV (draft, date 2024-10-15)         Clerk issues INV (draft, date 2024-01-15 — typo)
  └ Compose(draft) → validate ──► 200 valid          └ Compose(draft) → validate ──► 409 "period closed
  └ Finalize (number) ──► post ──► PendingApproval         through 2024-09-30"
  └ entry on the books after approval                 └ IssueAsync throws → endpoint 409 to clerk
                                                       └ invoice UNTOUCHED, still Draft
                                                       └ clerk edits date → re-issues → succeeds
```

## Error handling

- Closed period → 409 with the engine's message; invoice stays Draft.
- Unbalanced / chart violation → 422 with the engine's message; invoice stays Draft. (Recipe always
  balances, so 422 here signals a chart misconfiguration — a real, surfaced problem, not a silent 500.)
- Engine unreachable / 5xx → `LedgerClientException` with that status; invoice stays Draft; nothing is
  half-committed. (Before: a finalize could precede an unreachable engine and orphan.)
- TOCTOU race (period closes between validate and post) → the post fails after finalize → orphan →
  handled by the close-time exception backstop (out of scope).

## Testing

Engine (`Accounting101.Ledger.Api.Tests` / `Ledger.Mongo.Tests`):
- `validate` on a balanced, open-period, chart-valid entry → `200 {valid:true}` **and the journal is
  unchanged** (assert no entry written, sequence counter not advanced).
- `validate` with a closed-period effective date → `409` with the freeze reason; no write.
- `validate` with an unbalanced entry → `422`; no write.
- `validate` with a missing/non-postable account or a missing required dimension → `422`; no write.
- Parity: a `validate` rejection and a real `post` rejection of the same request return the same
  status + detail (guards against drift).

Module (`Accounting101.Receivables.Tests`):
- `HttpLedgerClient.ValidateAsync`: 200 → returns; 409 ProblemDetails → throws `LedgerClientException(409, reason)`.
- `IssueAsync` against a closed-period draft (FakeLedgerClient set to reject) → throws; invoice remains
  `Draft`; `FinalizeAsync` never called; no entry composed-and-posted.
- `IssueAsync` against a valid draft → finalizes + posts exactly as today (regression).
- E2e (strengthen the step-1 test): clerk issues into a closed period → `409` with the reason **and a
  read-back shows the invoice still `Draft`** (no orphan); re-issuing with an open date then succeeds.

## Scope

**In scope:** the engine `validate` endpoint, the shared no-drift validation routine, the
`ILedgerClient.ValidateAsync` client method, and the `IssueAsync` pre-flight + strengthened tests.

**Out of scope (separate specs / fast-follows):**
- The orphan exception queue (derived from the back-link invariant) and the **period-close gate** that
  surfaces it — the backstop for the residual TOCTOU race and genuinely-late items.
- `VoidAsync` handling the entry-less invoice; current-period catch-up posting.
- Real-time push notification (additive adapter on top of the derived queue).
- **Payables parity:** `BillService` enter has the same finalize-then-post shape and should adopt the
  same pre-flight using this same engine primitive — a fast-follow once the receivables shape is proven.

## Global constraints

- .NET 10; tests use EphemeralMongo (real transactions); build with 0 warnings; commit per slice; TDD.
- The `validate` primitive is engine-owned and domain-agnostic — it must not reference any module type.
- Engine enforces only irreducible invariants; this adds no new rule, it exposes the existing post
  validation as a side-effect-free read.
