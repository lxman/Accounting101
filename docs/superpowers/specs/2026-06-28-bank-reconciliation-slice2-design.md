# Bank Reconciliation — Slice 2 (Auto-Match by Amount) — Design

**Date:** 2026-06-28
**Status:** Approved (design)

## Context

Bank reconciliation is being built in four slices:

1. Core — statements + matching + report (read-only) ✅ SHIPPED (master `2c2970e`)
2. **Auto-match by amount** ← THIS SPEC
3. Adjustments posting (fees/interest, maker-checker)
4. CSV/OFX import

Slice 1 gave manual reconciliation: record a `BankStatement`, start a
`Reconciliation`, clear/unclear ledger cash entries by hand, and read a
worksheet computing the cleared-method difference + balanced verdict. Slice 2
adds an **auto-match** step that proposes which uncleared eligible entries pair
with which statement lines, by signed amount — turning a manual hunt into a
review-and-confirm.

## Goal

A `POST /reconciliations/{id}/auto-match` endpoint that pairs bank statement
lines to uncleared eligible ledger entries by exact signed amount (date as a
tiebreak), returning a proposal the user reviews. A preview call mutates
nothing; an `?apply=true` call additionally clears the matched entries (through
Slice 1's validated `ClearAsync`) and returns the updated worksheet. Still
**read-only on the GL** — "apply" only edits the reconciliation's own cleared
set, never the ledger.

## Scope

**In scope:** a pure `AutoMatcher` (mirrors `ReconciliationMath`); the match
algorithm (exact signed-amount 1:1 assignment, nearest-date tiebreak); the
`AutoMatchProposal` result DTO; `ReconciliationService.AutoMatchAsync`; the
endpoint with the `apply` flag; unit + service + E2E tests.

**Out of scope (later slices / not built):** fuzzy / near-amount matching, a
hard date-window filter, many-to-one matching (one bank line ↔ several entries,
i.e. combined deposits / split payments), stable per-statement-line ids,
posting fee/interest adjustments (Slice 3), CSV/OFX import (Slice 4), and any GL
mutation.

## Why amount matching is clean

A `BankStatementLine.Amount` is signed from the bank's perspective (+ money into
the account / a deposit clearing, − money out / a payment clearing). An entry's
**book cash effect** (`ReconciliationMath.CashEffect`) is signed Debit-to-cash
`+` / Credit `−`. These share sign convention, so the match key is exact signed
equality: a `+100` deposit line matches an entry whose `CashEffect == +100`; a
`−40` payment line matches `CashEffect == −40`. No sign flipping.

## Architecture

### Pure matcher (`AutoMatcher.cs`, domain project)

A static class beside `ReconciliationMath`. It works on already-prepared inputs
(the service supplies the uncleared eligible entries paired with their computed
cash effect, so the matcher needs no ledger access):

```csharp
public sealed record MatchableEntry(Guid EntryId, DateOnly Date, decimal CashEffect);

public sealed record AutoMatch(
    int StatementLineIndex, decimal Amount, Guid EntryId, DateOnly LineDate, DateOnly EntryDate, int DaysApart);

public sealed record UnmatchedLine(int StatementLineIndex, DateOnly Date, decimal Amount, string Description);

public sealed record AutoMatchProposal(
    IReadOnlyList<AutoMatch> Matches,
    IReadOnlyList<UnmatchedLine> UnmatchedStatementLines,
    IReadOnlyList<MatchableEntry> UnmatchedEntries,
    IReadOnlyList<Guid> MatchedEntryIds);   // flat list = proposal.Matches' EntryIds, for handing to /clear

public static class AutoMatcher
{
    public static AutoMatchProposal Match(
        IReadOnlyList<BankStatementLine> statementLines, IReadOnlyList<MatchableEntry> uncleared);
}
```

**Algorithm:**
- Index the statement lines (0..n). Consider only `uncleared` entries (the
  service excludes already-cleared ones before calling).
- Bucket both sides by signed amount (`line.Amount` == `entry.CashEffect`).
- Within each amount bucket, assign lines to entries **1:1**. When a bucket has
  unequal counts, greedily pair each line to its nearest-date entry
  (`min |entry.Date − line.Date|`), tie-broken by smaller `EntryId` for
  determinism; surplus lines/entries on the heavier side are left unmatched.
- A line with no entry of its amount → `UnmatchedStatementLines`. An uncleared
  entry with no line of its amount → `UnmatchedEntries`.
- `DaysApart` = `Math.Abs(entry.Date.DayNumber − line.Date.DayNumber)`.
- `MatchedEntryIds` = `Matches.Select(m => m.EntryId)` (deterministic order).

### Service (`ReconciliationService.AutoMatchAsync`)

```csharp
Task<AutoMatchProposal> AutoMatchAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default);
Task<ReconciliationWorksheet> AutoMatchApplyAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default);
```

- `AutoMatchAsync` — `RequireOpenAsync` (409 if not found or Completed, exactly
  as Slice 1's clear/unclear/complete behave), then
  `EligibleEntriesAsync`, exclude any entry already in `ClearedEntryIds`, project
  each to a `MatchableEntry(Id, EffectiveDate, CashEffect(entry, cashAccountId))`,
  and run `AutoMatcher.Match(statement.Lines, uncleared)`. Returns the proposal.
  No mutation.
- `AutoMatchApplyAsync` — runs `AutoMatchAsync`, then calls the existing
  `ClearAsync(clientId, reconciliationId, proposal.MatchedEntryIds, ct)` (which
  re-validates eligibility, dedups into `ClearedEntryIds`, saves, and returns the
  updated worksheet). If the proposal has no matches, `ClearAsync([])` is a no-op
  that returns the current worksheet. This keeps clearing in one validated place.

### HTTP surface

| Route | Verb | Query | Success | Errors |
|---|---|---|---|---|
| `/reconciliations/{id:guid}/auto-match` | POST | `apply` (bool, default false) | `apply=false` → 200 `AutoMatchProposal`; `apply=true` → 200 `ReconciliationWorksheet` | 409 (reconciliation not found or Completed) |

Under the existing `/clients/{clientId:guid}` authorized group. Error mapping
unchanged from Slice 1 (`InvalidOperationException` → 409). A not-found or
Completed reconciliation returns 409 via `RequireOpenAsync`, consistent with the
sibling clear/unclear/complete endpoints. No new registration, manifest, or credential — auto-match
uses the same read-only ledger seam; apply mutates only the reconciliation
document.

## Data flow

```
POST /reconciliations/{id}/auto-match
  → RequireOpen → EligibleEntries (Active+Posted, ≤ statementDate, on cash acct)
  → drop entries already in ClearedEntryIds
  → MatchableEntry per entry (CashEffect)
  → AutoMatcher.Match(statement.Lines, uncleared)  [pure: bucket by amount, 1:1, nearest-date tiebreak]
  → proposal (matches / unmatched lines / unmatched entries / matchedEntryIds)

POST /reconciliations/{id}/auto-match?apply=true
  → same proposal → ClearAsync(matchedEntryIds)  [Slice 1: re-validate, dedup, save]
  → updated ReconciliationWorksheet
```

## Error handling

- Auto-match (preview or apply) on a not-found or Completed reconciliation → 409
  (via `RequireOpenAsync`, matching the Slice 1 clear/unclear/complete siblings).
- Apply re-uses `ClearAsync`, which already rejects ineligible ids (422) — but
  the proposal only ever contains eligible uncleared entry ids, so apply will not
  surface a 422 in normal operation. (If it ever did, the existing mapping
  applies.)

## Testing

- **Matcher unit tests** (`AutoMatcherTests`, pure, no host): exact 1:1 match by
  signed amount (a `+100` line ↔ a `+100`-effect entry; a `−40` line ↔ a
  `−40`-effect entry); a bucket with two `+100` entries and one `+100` line picks
  the nearer-date entry and leaves the other in `UnmatchedEntries`; a line whose
  amount has no entry lands in `UnmatchedStatementLines`; `MatchedEntryIds`
  mirrors `Matches`; determinism (same input → same pairing).
- **Service test** (`ReconciliationServiceTests`, fakes): `AutoMatchAsync`
  excludes an already-cleared entry from the proposal; `AutoMatchApplyAsync`
  clears the matched ids and the worksheet becomes balanced when the matches
  cover the gap; auto-match on a Completed reconciliation throws
  `InvalidOperationException`.
- **E2E** (`ReconciliationE2eTests` or a new auto-match E2E file): post + approve
  a real cash deposit and disbursement via the Cash module; record a matching
  statement; start a reconciliation; `auto-match` (preview) proposes both entries
  with the right pairing and empty unmatched lists; `auto-match?apply=true`
  returns a balanced worksheet; `complete` → 200. A preview call leaves the
  reconciliation untouched (a follow-up GET worksheet shows nothing cleared until
  apply / clear).

## Success criteria

- `auto-match` (preview) proposes the correct 1:1 pairings by signed amount with
  nearest-date tiebreak, mutating nothing.
- `auto-match?apply=true` clears exactly the matched entries (through the
  validated `ClearAsync`) and returns the updated worksheet.
- Already-cleared entries are excluded from proposals; unmatched lines/entries are
  reported on both sides.
- No GL mutation; no change to other modules; Slice 1 behavior unchanged.
- New unit + service + E2E tests green; existing suites stay green.
