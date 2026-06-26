# Structured validation errors — actionable field-level 422s — Design

**Date:** 2026-06-25
**Status:** Spec for review

## Context & principle

Posting an entry that fails validation returns a single flat string in `ProblemDetails.detail`. The painful cases the dog-food agents hit repeatedly:
- a bad line direction surfaces the raw .NET message **"Requested value 'dr' was not found."** (no field, no line index, no valid values) — from `Enum.Parse<Direction>` in `MapLines`;
- a missing direction surfaces **"Value cannot be null (Parameter 'value')."**;
- chart violations are collected as a list but `string.Join(" ", …)`-flattened into one blob;
- none of it is machine-readable, so a module/front end can't map an error to the offending field.

**Principle:** a validation failure should name the field (and line) at fault and state the valid values. Adopt the standard ASP.NET **`ValidationProblemDetails`** shape — `{ "errors": { "<field-path>": ["message", …] }, "status": 422 }` — for the entry-post field-validation path, keeping the existing **422** status so existing status-code assertions and the pre-flight contract hold.

Scope is the engine entry-post validation path (`POST /entries`, `POST /entries/validate`). Semantic conflicts (closed-period **409**, idempotency "already exists with different content" **422**) are NOT field validation and stay plain `ProblemDetails`. Statement/query 422s are already clear and are out of scope.

## Design

### 1. Field-keyed validation, collected (not first-throw) — `LedgerEndpoints`

Today `MapEntry`→`MapLines` calls `Enum.Parse<Direction>` which **throws on the first bad line**, so only one error ever surfaces. Restructure so parse errors are **accumulated** across all lines, then returned together.

- **`TryMapEntry(clientId, request, actor, out JournalEntry? entry, out Dictionary<string, string[]> errors)`** → bool. It:
  - parses each line's `Direction` with `Enum.TryParse`; on failure adds `errors["lines[{i}].direction"]`:
    - empty/null → `"A direction is required; expected 'Debit' or 'Credit'."`
    - unrecognized → `"'{raw}' is not a valid direction; expected 'Debit' or 'Credit'."`
  - parses the entry `type` via the existing `ParsePostableType` rules; on failure adds `errors["type"] = ["EntryType must be 'Standard' or 'Adjusting'; '{type}' cannot be posted directly."]`
  - if any parse errors so far → return `false` (cannot construct the entry).
  - else construct via `JournalEntry.Create`; if it throws `UnbalancedEntryException` → add `errors["balance"] = ["The entry does not balance: debits minus credits = {imbalance}."]` and return `false`.
  - else `entry` is set, `errors` empty, return `true`.
- **`ChartFieldViolationsAsync(...)`** replaces `ChartViolationsAsync`: same checks, but returns `Dictionary<string, string[]>` keyed by `lines[{i}].accountId` (each existing violation message kept verbatim), empty when conforming. (Skipped when no chart is set up, as today.)
- **Response helper:**
  ```csharp
  private static IResult ValidationProblem(IDictionary<string, string[]> errors) =>
      Results.ValidationProblem(errors,
          detail: "One or more fields are invalid.",
          statusCode: StatusCodes.Status422UnprocessableEntity);
  ```
  (The `detail` summary keeps any consumer that only reads `detail` informative; the `errors` map carries the field-level facts.)

### 2. Wire into the three call sites

- `ValidateForPostAsync`: `if (!TryMapEntry(...)) return (ValidationProblem(errors), null);` then `var chart = await ChartFieldViolationsAsync(...); if (chart.Count > 0) return (ValidationProblem(chart), null);` then the period-freeze **409** (unchanged). (Parse errors gate chart-checking — unparseable lines can't be chart-checked; within each stage ALL errors are collected.)
- `PostEntry` early idempotent-replay path: replace the `try { MapEntry } catch (ArgumentException…) { Unprocessable }` with `TryMapEntry` → `ValidationProblem(errors)` on failure.
- `ValidateEntry` (pre-flight) already delegates to `ValidateForPostAsync`, so it inherits the structured body unchanged — `200 {valid:true}` on success, the same structured 422 on failure (the side-effect-free contract is preserved).
- The idempotency "already exists with different content" stays `Unprocessable(detail)` (plain); the closed-period path stays `Conflict` (409).

### 3. Module pre-flight surfaces the field errors — `HttpLedgerClient.ReasonFrom`

`HttpLedgerClient.EnsureSuccessAsync` builds a `LedgerClientException` reason via `ReasonFrom`, which currently reads only ProblemDetails `detail`. Update `ReasonFrom` to **prefer the `errors` map when present** — flatten to a readable `"<field>: <message>; …"` — else fall back to `detail`, else the raw body/status. So the Receivables/Payables issue/post pre-flight surfaces the field-level reason (e.g. `lines[0].accountId: Account 1100 "A/R" requires a Customer on the posting line`) instead of degrading to the summary.

## Out of scope

- Module-endpoint request validation (invoice draft / bill) — those throw `InvalidOperationException` with already-reasonable messages; the engine entry path is where the cryptic enum/chart errors live. (The global strict-binding 400 already handles unknown fields everywhere.)
- Statement/query 422s (already clear).
- A localized/i18n error catalog.

## Testing

Engine (`Accounting101.Ledger.Api.Tests`):
- **Bad direction names the line + valid values:** post a line with `direction:"dr"` → 422 whose body `errors["lines[0].direction"]` contains `"Debit"` and `"Credit"` (or "dr").
- **Multiple bad directions collected:** two bad lines → `errors` has BOTH `lines[0].direction` and `lines[1].direction` (proves collect-not-first-throw).
- **Missing direction → required message** keyed to the line.
- **Bad entry type → `errors["type"]`.**
- **Unbalanced → `errors["balance"]`** with the imbalance.
- **Chart violation → `errors["lines[{i}].accountId"]`** (missing-dimension / non-postable / not-in-chart), each line keyed independently; multiple violations all present.
- **Valid post still 201; valid `/entries/validate` still 200 `{valid:true}`** (no false positives).
- **Closed-period stays 409; idempotency clash stays 422 plain `detail`** (not restructured).
- Update existing `PostingValidationTests`/others that asserted on the old flat `detail` for these cases (status assertions are unchanged; switch any body-`detail` assertion to the `errors` map — the message text survives there).

Module (`Accounting101.Receivables.Tests`):
- **Pre-flight failure reason carries the field detail:** an issue whose A/R entry violates the chart throws `LedgerClientException` whose reason includes the field-level message (proves `ReasonFrom` flattens `errors`).

## Global constraints

- .NET 10; build 0 warnings; commit per task; TDD.
- Keep **422** status for field validation; keep **409** freeze and the idempotency-conflict **422** as plain ProblemDetails.
- Collect ALL errors within a stage (don't stop at the first bad line).
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
