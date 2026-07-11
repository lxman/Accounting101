# Engine hardening group-3 + leftovers — design

**Date:** 2026-07-11
**Branch:** `feat/engine-hardening-group3`
**Status:** approved (design), pre-implementation

## Context

The 2026-06-20 adversarial review left a group-3 "fast-follow hardening" backlog for the
ledger engine. Three items on that list have since shipped independently and are **out of
scope** (verified in code):

- **Idempotent retry** — `PostEntry` (`LedgerEndpoints.cs:69-113`) already replays a duplicate
  caller-supplied `Id` (200 on matching content, 422 on same-id/different-content).
- **Closed-period TOCTOU** — `EnsureOpenForPostAsync` is the authoritative in-transaction guard.
- **Audit-chain tail-truncation** — resolved (`6ee1243`).

This spec covers the **9 remaining items**: 7 engine hardening items plus 2 "little leftovers"
(readCap drift-guard test, per-line readiness coverage). The user chose to do all 9 in one spec,
batch-POST included.

## Non-goals

- No changes to the three already-shipped items above.
- No new currency/FX work (USD-only by decision).
- No refactor beyond what each item requires.

## Items

### 1. Covering index

Extend the existing `client_status_posting` index in `MongoJournalStore` (index setup, ~line 252)
from `ClientId + Status + Posting` to **`ClientId + Status + Posting + EffectiveDate`**, renamed
`client_status_posting_effdate`. The windowed activity/subledger/close folds filter on all four but
today match only the 3-key prefix and residual-scan on date.

- Drop the old `client_status_posting` by name first (index rename is not in-place), then create the
  4-key model. Idempotent on re-run.
- No query change: Mongo uses the 4-key index for the 3-key prefix too. Keep `client_effectiveDate`
  for date-only queries.

**Test:** assert the index set contains `client_status_posting_effdate` with the 4 keys in order and
that `client_status_posting` is gone.

### 2. `asOf` on `GET /accounts/{id}/balance`

`GetAccountBalance` (`LedgerEndpoints.cs:661`) reads the O(1) projection only. Add optional `asOf`
query param; when present, fold from the journal via the existing
`AggregateBalancesAsync(clientId, asOf)` and pluck this account — mirroring `GetTrialBalance`
(line 529). Absent `asOf` → unchanged live projection.

**Test:** `asOf` balance equals the trial-balance value for the same account/date; absent `asOf`
returns the live projection.

### 3. Discovery + typo guards

**Discovery endpoints:**
- `GET /clients/{id}/dimensions` → distinct dimension keys declared across the chart's
  `RequiredDimensions` (union, from `GetChartAsync`). No journal scan.
- `GET /clients/{id}/source-types` → distinct `SourceType` values actually in the journal. New
  `MongoJournalStore.DistinctSourceTypesAsync` — a `Distinct` over `SourceType` filtered by
  `ClientId` (nulls/empties excluded).

Both are member-read endpoints (same auth as other client reads).

**Typo guard** in `ChartFieldViolationsAsync` (`LedgerEndpoints.cs:1130`): for an account **with**
`RequiredDimensions`, reject any line dimension key **not** in that set → 422, keyed
`lines[i].accountId`, message e.g. `unknown dimension 'custommer' on account 1200 "Accounts
Receivable" (expected: customer)`. Accounts with no required dimensions are untouched (informational
tagging still allowed there). Runs inside the shared `ValidateForPostAsync`, so `/entries`,
`/entries/validate`, and `/entries/batch` all inherit it.

**Tests:** discovery endpoints return the expected sets; typo'd key → 422 with the actionable
message; correct key still posts; non-control account with an extra key still posts.

### 4. Actionable parse errors (account/onboarding paths)

`UpsertAccount` (`LedgerEndpoints.cs:831, 839`) and the onboarding opening-lines mapping
(`~1047`) call raw `Enum.Parse<AccountType>` / `<CashFlowActivity>` / `<Direction>`, surfacing
`"Requested value 'dr' was not found"`. Add a `TryParseEnum<T>` helper that produces structured
`ValidationProblem` errors: field name + bad value + valid values, e.g.
`type: "'aset' is not a valid account type. Valid: Asset, Liability, Equity, Revenue, Expense."`
The `/entries` post path already does this (line 931); this brings account/onboarding to parity.

**Tests:** invalid account `type` / `cashFlowActivity` and invalid onboarding opening-line
`direction` each return a 422 `ValidationProblemDetails` naming the field + valid values.

### 5. Batch / atomic multi-entry POST

New `POST /clients/{id}/entries/batch`, body `{ entries: [PostEntryRequest, ...] }`.

- **Cap 500** entries → 422 `too-large` if exceeded (also reject empty array → 422).
- **Validate-all-first:** run the shared `ValidateForPostAsync` (map + chart + typo + balance +
  freeze) for every entry, collecting per-entry errors keyed `entries[i].<field>`. Any failure →
  422, nothing written.
- **Idempotency (per-entry Id, whole-batch):**
  - every supplied Id already exists **and** content matches → `200` replay (array of existing
    responses, input order);
  - none of the supplied Ids exist → write;
  - mixed (some exist, some new) → `422` partial-replay refusal;
  - an existing Id whose content differs → `422` (same rule as single-post).
  - Entries without an Id count as "new".
- **Atomic write:** new `LedgerService.PostBatchAsync(IReadOnlyList<JournalEntry>, actor, ct)` — one
  `InTransactionAsync`, looping `EnsureOpenForPostAsync` + `AppendSequencedAsync` + audit append per
  entry. Each entry gets its own gapless sequence number (session-joined `$inc`); any failure rolls
  the whole batch back.
- **Response:** `201` with `[{id,status,posting}, ...]` in input order.
- **Auth:** same `ResolveForPostAsync` + per-module gate as single post; `ViaModule` stamped
  uniformly across the batch.

**Tests (unit + E2E):** happy-path writes N gapless entries; one bad entry → whole batch rejected,
zero written, sequence counter not advanced; all-match → 200 replay; none-exist → 201; mixed → 422;
>500 → 422; empty → 422.

### 6. Labeling

Add account `number` + `name` to read responses: `AccountBalanceResponse`, trial-balance lines,
subledger lines. Enrich by joining `GetChartAsync` (already loaded/cheap) in the endpoint. New
**optional** fields on the response records — additive, existing consumers unaffected. An account id
absent from the chart yields null number/name (defensive, shouldn't happen).

**Tests:** each response carries the correct number + name for a known account.

### 7. Reversal own-source override

Add optional `sourceRef` + `sourceType` to `ReverseRequest`. When supplied, the reversal entry
carries them instead of inheriting the original's `SourceRef`/`SourceType` (a credit-memo module
tagging the reversal with its own document). Absent → current inherit behavior. Threads through
`MapReversal` / `ReverseAsync`.

**Tests:** override sets the reversal's source fields; absent override inherits the original's.

### 8. readCap drift-guard test (leftover)

The area→readCap map lives in **3 homes** (accepted duplication, cross-language):
`Capabilities.CapabilityForModule` (`Backend/.../Control/Capabilities.cs:76`),
`ReadinessAccess.ReadCapabilityFor` (`Modules/Shared/Accounting101.ModuleKit/ReadinessAccess.cs`),
and `CHART_HEALTH_MODULES` (`UI/Angular/src/app/core/chart-health/chart-health.ts:29`).

- **xUnit:** for each of the 6 module keys, assert
  `ReadinessAccess.ReadCapabilityFor(key) == Capabilities.CapabilityForModule(key, Read)` — fails if
  the two backend homes drift. Also assert an unknown key maps to null in both.
- **Jasmine spec:** assert each `CHART_HEALTH_MODULES` entry's `readCap` equals its expected
  `{area}.read` literal — guards the Angular home against the known-good map (cross-language, so it
  checks the literal, not a live backend call).

### 9. Readiness coverage for configured revenue-by-category accounts (leftover)

`ReceivablesChartRequirements.ForAsync` declares the fixed configured accounts (AR control, default
revenue, cash, etc.) but **not** the per-category revenue accounts in the configured
`RevenueByCategory` map (`InvoicePostingAccounts.RevenueAccountsByCategory`). An invoice line with a
`RevenueCategory` posts to one of those accounts, yet readiness never checks they exist / are the
right type. Extend `ReceivablesChartRequirements` to add one `AccountRequirement` per
`RevenueAccountsByCategory` entry (label `"Revenue: {category}"`, ExpectedType `"Revenue"`, no
required dimensions). Surfaced through the same `chart-readiness` report shape and evaluator — no
checker change.

**Payables is deliberately excluded:** its bill-line expense accounts are chosen per line **at
data-entry time** (`BillLineBody.ExpenseAccountId`), not from configuration, so there is no
config-driven set to pre-validate — readiness cannot know them ahead of a bill. `PayablesChartRequirements`
is unchanged.

**Tests (E2E):** a configured revenue category mapped to a missing/wrong-type account surfaces a
readiness gap naming `"Revenue: {category}"`; a fully-mapped chart still reports ready; an empty
category map adds no requirements (unchanged behavior).

## Testing strategy

- Unit/contract tests per item as listed above.
- E2E for batch (happy + rollback), typo guard through the real post endpoint, and per-line
  readiness through the module `chart-readiness` route.
- Whole solution stays green (`Accounting101.slnx`), plus the UI Jasmine spec.

## Compatibility

Everything is additive except the **typo guard**, a deliberately tightened validation. Real-world
risk is low: modules post exactly the required dimension for control accounts, so no existing caller
sends an undeclared key to a control account. Non-control accounts are unaffected.

## Ownership map

- Items 1, 3(discovery/distinct): `Accounting101.Ledger.Mongo`
- Items 2, 3(typo), 4, 5(endpoint), 6, 7: `Accounting101.Ledger.Api`
- Item 5(service): `Accounting101.Ledger.Mongo` (`LedgerService`)
- Item 8: `Accounting101.Ledger.Api.Tests` + `UI/Angular`
- Item 9: `Accounting101.Receivables.Api`, `Accounting101.Payables.Api`
