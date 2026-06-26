# Structured Validation Errors — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Turn the engine's flat-string entry-post validation 422s into standard `ValidationProblemDetails` with field-keyed, valid-values messages collected across all lines; teach the module pre-flight client to surface them.

**Architecture:** Restructure `LedgerEndpoints` so line/type parsing and chart checks **accumulate** field-keyed errors (`lines[i].direction`, `type`, `balance`, `lines[i].accountId`) instead of throwing on the first; return `Results.ValidationProblem(errors, statusCode: 422)`. Update `HttpLedgerClient.ReasonFrom` to flatten the `errors` map. Status codes are unchanged (422 field-validation, 409 freeze, 422 plain for idempotency conflict).

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs, xUnit + WebApplicationFactory + EphemeralMongo.

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- Keep **422** for field validation; **409** freeze and the idempotency-conflict **422** stay plain `ProblemDetails`.
- Collect ALL errors within a stage (don't stop at the first bad line); parse errors gate the chart stage.
- Spec: `docs/superpowers/specs/2026-06-25-structured-validation-errors-design.md`.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit file lists; do NOT commit in a worktree.

---

## Task 1: Engine — structured field-validation 422s

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`TryMapEntry`, `ChartFieldViolationsAsync`, `ValidationProblem` helper; wire into `PostEntry` early-replay + `ValidateForPostAsync`)
- Test: `Backend/Accounting101.Ledger.Api.Tests/StructuredValidationErrorsTests.cs` (create)
- Modify: any existing test asserting on the old flat `detail` for these cases (status assertions unchanged)

**Interfaces:**
- Produces: 422 bodies in `ValidationProblemDetails` shape with keys `lines[{i}].direction`, `type`, `balance`, `lines[{i}].accountId`.

- [ ] **Step 1: Write the failing tests** — `StructuredValidationErrorsTests` (reuse the existing host/auth/seed harness). Cover the spec's cases:
```csharp
// bad direction "dr"      -> 422, errors["lines[0].direction"] contains "Debit" & "Credit"
// two bad directions      -> 422, errors has lines[0].direction AND lines[1].direction (collect, not first-throw)
// missing/empty direction -> 422, errors["lines[0].direction"] = required message
// bad type "Closing"      -> 422, errors["type"] present
// unbalanced entry        -> 422, errors["balance"] present (mentions the imbalance)
// chart: missing required dimension on a control account -> 422, errors["lines[{i}].accountId"] present
// chart: two violations on two lines -> both line keys present
// valid post -> 201 ; valid /entries/validate -> 200 {valid:true}
// closed period -> 409 (unchanged) ; idempotency same-id-different-content -> 422 plain detail (no errors map)
```
Read the 422 body as `HttpValidationProblemDetails` (or parse the `errors` object) and assert on keys + substrings.

- [ ] **Step 2: Run, confirm fail** — today these return a flat `detail` (no `errors` map) and only the first bad line surfaces. `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "StructuredValidationErrorsTests"`.

- [ ] **Step 3: Implement** in `LedgerEndpoints.cs`:
- Add `TryMapEntry(Guid clientId, PostEntryRequest request, Actor actor, out JournalEntry? entry, out Dictionary<string, string[]> errors)`:
  - iterate `request.Lines` with index; `Enum.TryParse<Direction>(l.Direction, ignoreCase:true, out var dir)`; on empty/null → `lines[{i}].direction` = `"A direction is required; expected 'Debit' or 'Credit'."`; on unparseable → `"'{l.Direction}' is not a valid direction; expected 'Debit' or 'Credit'."` Collect mapped `Line`s when valid.
  - parse `type` via the existing `ParsePostableType` rules but capture its `ArgumentException` message into `errors["type"]` rather than throwing.
  - if `errors.Count > 0` → `entry = null; return false`.
  - else `try { entry = JournalEntry.Create(... mapped lines ...); return true; } catch (UnbalancedEntryException ex) { errors["balance"] = ["The entry does not balance: debits minus credits = " + <imbalance> + "."]; entry = null; return false; }` (reuse the exception's imbalance; keep `MapEntry` for the non-collecting callers like `MapReplacement`, or refactor `MapEntry` to call the line-mapping core — implementer's call, but `MapReplacement`/revise behavior must be unchanged).
- Add `ChartFieldViolationsAsync(MongoAccountStore accounts, Guid clientId, IReadOnlyList<Line> lines, CancellationToken ct)` returning `Dictionary<string,string[]>` keyed `lines[{i}].accountId` (same messages as `ChartViolationsAsync`, but per-line keyed; empty when conforming; still short-circuits to empty when the chart is unset).
- Add helper:
```csharp
private static IResult ValidationProblem(IDictionary<string, string[]> errors) =>
    Results.ValidationProblem(errors, detail: "One or more fields are invalid.",
        statusCode: StatusCodes.Status422UnprocessableEntity);
```
- `ValidateForPostAsync`: `if (!TryMapEntry(clientId, request, ctx.Actor!, out JournalEntry? entry, out var errors)) return (ValidationProblem(errors), null);` then `Dictionary<string,string[]> chart = await ChartFieldViolationsAsync(...); if (chart.Count > 0) return (ValidationProblem(chart), null);` then the existing freeze 409. Return `(null, entry!)`.
- `PostEntry` early idempotent-replay path: replace the `try { MapEntry } catch { Unprocessable }` with `TryMapEntry` → `ValidationProblem(errors)` on failure (then the existing `SameFinancialContent` comparison).
- Keep `ChartViolationsAsync`'s removal clean (no dead code); keep the idempotency "already exists with different content" as `Unprocessable(...)` and the freeze as `Conflict(...)`.

- [ ] **Step 4: Run, confirm pass; sweep** — `StructuredValidationErrorsTests` green. Then run `PostingValidationTests`, `IdempotentPostTests`, `PeriodCloseApiTests`, `CommandQueryTests`, `EntriesListFilterTests`, `SourceLinkTests` individually; fix any that asserted on the OLD flat `detail` for a now-structured case (switch to the `errors` map — the message text survives there; status assertions are unchanged). Record each test touched.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs \
        Backend/Accounting101.Ledger.Api.Tests/StructuredValidationErrorsTests.cs <any updated tests>
git commit -m "feat(ledger): structured field-level validation errors (ValidationProblemDetails)"
```

---

## Task 2: Module pre-flight — surface field errors in the client reason

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs` (`ReasonFrom`)
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/` (add/extend a pre-flight failure-reason test)

**Interfaces:**
- Consumes: Task 1's `errors`-map 422 body from `/entries/validate`.

- [ ] **Step 1: Write the failing test** — drive an issue whose A/R entry violates the chart (e.g. a control account missing its required dimension) so the pre-flight `ValidateAsync` returns the structured 422; assert the thrown `LedgerClientException.Reason` (or message) contains the field-level text (e.g. the account number/dimension), not just "One or more fields are invalid." Reuse the existing Receivables host harness; mirror however the current tests provoke a pre-flight rejection.

- [ ] **Step 2: Run, confirm fail** — `ReasonFrom` reads only `detail`, so the reason is the summary, missing the field text.

- [ ] **Step 3: Implement** — in `HttpLedgerClient.ReasonFrom`, when the body is ProblemDetails JSON: if an `errors` object is present, flatten its members to `"<field>: <message>; …"` (join each field's messages) and return that; else the existing `detail`; else raw body/status. Keep the existing `detail`/raw/status fallbacks intact for non-validation errors (409 freeze, etc.).

- [ ] **Step 4: Run, confirm pass** — the new test green; re-run `ReceivablesIssueTests` (and any test asserting on a pre-flight rejection reason) to confirm no regression in the reason text they expect.

- [ ] **Step 5: Build clean, commit**
```bash
git add Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs <test file>
git commit -m "feat(receivables): pre-flight client surfaces field-level validation errors"
```

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run individually: `StructuredValidationErrorsTests`, `PostingValidationTests`, `IdempotentPostTests`, `PeriodCloseApiTests`, and the Receivables pre-flight test — all green.
- [ ] Confirm: bad direction/type/balance/chart → 422 with field-keyed `errors` + valid values; multiple errors collected; valid post 201, validate 200; freeze 409 and idempotency clash 422-plain unchanged; module pre-flight reason carries the field text.
- [ ] Whole-branch review on the most capable model, then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- Spec coverage: collect-not-first-throw (Task 1 `TryMapEntry` + multi-error tests), field keys + valid values (Task 1), 422/409 status preserved (Task 1), module reason flatten (Task 2).
- Type consistency: `Dictionary<string,string[]>` throughout; `Results.ValidationProblem(errors, detail, statusCode)`; `TryMapEntry` out-params.
- Open implementer checks: (a) keep `MapReplacement`/revise behavior unchanged when refactoring the shared line-mapping; (b) confirm `Results.ValidationProblem` emits 422 (not its 400 default) with the `errors` map; (c) which existing tests asserted on the flat `detail` (Task 1 Step 4 sweep).
