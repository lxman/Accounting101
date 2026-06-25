# Issue Pre-flight via Engine Validate-Entry — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement
> this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate an invoice's would-be A/R entry against the engine's own post rules *before*
finalizing the invoice, so a closed-period (or otherwise un-postable) date is caught while the invoice
is still a draft — no orphan, no escalation for a typo.

**Architecture:** Add a side-effect-free `POST /clients/{clientId}/entries/validate` to the engine that
runs the *same* pre-write validation as a real post (map+balance, chart, period freeze) and writes
nothing. The receivables module calls it from `IssueAsync` before `FinalizeAsync`. No-drift is the
load-bearing constraint: `PostEntry` and `ValidateEntry` share one validation routine.

**Tech Stack:** C# / .NET 10, ASP.NET minimal APIs, MongoDB (replica-set transactions), xUnit +
EphemeralMongo. Full design: `docs/superpowers/specs/2026-06-25-issue-preflight-validate-entry-design.md`.

## Global Constraints

- .NET 10; build with **0 warnings**; **commit per task**; **TDD** (red → green).
- Tests use EphemeralMongo (real transactions, single-node replica set). Host-boot test classes are run
  individually when verifying (EphemeralMongo replica-set-init flakiness across classes is environmental).
- The `validate` primitive is **engine-owned and domain-agnostic** — it must not reference any module
  type. It adds no new rule: it exposes the existing post validation as a side-effect-free read.
- Engine error body shape is ProblemDetails (`Results.Problem(detail, statusCode)`), `detail` carries the
  reason. `validate` rejections must be byte-for-byte the same status + detail a real post returns.

---

## File Structure

- `Backend/Accounting101.Ledger.Contracts/EntryValidationResponse.cs` — Create: `record EntryValidationResponse(bool Valid)`.
- `Backend/Accounting101.Ledger.Mongo/LedgerService.cs` — Modify: expose the period check as a public
  method (`EnsureOpenForPostAsync`) that `PostAsync` also calls.
- `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` — Modify: extract the shared
  pre-write validation; add the `ValidateEntry` handler + route map.
- `Backend/Accounting101.Ledger.Api.Tests/…` — Test: new `ValidateEntryTests` (or add to the endpoint test class).
- `Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs` — Modify: add `ValidateAsync`.
- `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs` — Modify: implement `ValidateAsync`.
- `Modules/Receivables/Accounting101.Receivables/InvoiceService.cs` — Modify: reorder `IssueAsync` to pre-flight.
- `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs` — Modify: configurable validation outcome.
- `Modules/Receivables/Accounting101.Receivables.Tests/HttpLedgerClientTests.cs` — Test: `ValidateAsync`.
- `Modules/Receivables/Accounting101.Receivables.Tests/InvoiceServiceTests.cs` — Test: pre-flight leaves Draft.
- `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesIssueTests.cs` — Test: strengthen the e2e.

---

### Task 1: Engine — validate-entry endpoint (dry-run), sharing post's validation

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/EntryValidationResponse.cs`
- Modify: `Backend/Accounting101.Ledger.Mongo/LedgerService.cs` (expose period check)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (extract validation, add endpoint)
- Test: `Backend/Accounting101.Ledger.Api.Tests/ValidateEntryTests.cs`

**Interfaces:**
- Produces: `POST /clients/{clientId}/entries/validate` taking `PostEntryRequest`, requiring `Permission.Post`;
  returns `200 EntryValidationResponse(true)` when postable, else the same `409`/`422` ProblemDetails a real
  post returns. `EntryValidationResponse(bool Valid)`. `LedgerService.EnsureOpenForPostAsync(Guid clientId, DateOnly effectiveDate, CancellationToken)` (public; `PostAsync` calls the same method).

- [ ] **Step 1: Write failing endpoint tests.** In `ValidateEntryTests` (mirror the existing endpoint test
  fixture that boots the engine on EphemeralMongo): (a) a balanced, open-period, chart-valid entry →
  `200` with `Valid == true` AND the journal is unchanged (assert `GET /entries` count unchanged / no new
  sequence); (b) effective date in a period closed via `POST /periods/close` → `409` whose detail contains
  "closed", no write; (c) an unbalanced entry → `422`, no write; (d) an entry referencing a missing/
  non-postable account or omitting a required dimension → `422`, no write; (e) **parity**: post and
  validate the same closed-period request and assert identical status + detail.
- [ ] **Step 2: Run the tests, confirm they fail** (the route 404s / behavior absent).
- [ ] **Step 3: Add the contract.** `EntryValidationResponse.cs`: `public sealed record EntryValidationResponse(bool Valid);`
- [ ] **Step 4: Expose the period check.** In `LedgerService`, rename the private `EnsureOpenAsync` to a
  public `EnsureOpenForPostAsync(Guid clientId, DateOnly effectiveDate, CancellationToken cancellationToken)`
  (same body — throws `InvalidOperationException` on a closed period); `PostAsync` calls it. No behavior change.
- [ ] **Step 5: Extract the shared validation + add the endpoint.** In `LedgerEndpoints`, extract the
  pre-write checks from `PostEntry` into a helper that returns either a rejection or the mapped entry, e.g.
  `private static async Task<(IResult? Rejection, JournalEntry? Entry)> ValidateForPostAsync(Guid clientId, PostEntryRequest request, LedgerContext ctx, CancellationToken ct)`
  performing, in order: `MapEntry` (catch `ArgumentException`/`UnbalancedEntryException` → `Unprocessable`),
  `ChartViolationsAsync` (→ its violation result), then `ctx.Ledger.Service.EnsureOpenForPostAsync(...)`
  (catch `InvalidOperationException` → `Conflict`). `PostEntry` becomes: resolve ctx (Post) → `ValidateForPostAsync`
  → if `Rejection` return it → `await ctx.Ledger.Service.PostAsync(Entry!, ctx.Actor, ct)` (which keeps its
  own internal period check as the authoritative transactional-time guard) → `Created`. Add `ValidateEntry`:
  resolve ctx (Post) → `ValidateForPostAsync` → `return Rejection ?? Results.Ok(new EntryValidationResponse(true));`
  Map `clients/{clientId}/entries/validate` → `ValidateEntry`.
- [ ] **Step 6: Run new tests + the full existing engine suite (`PostEntry` tests must stay green).** 0 warnings.
- [ ] **Step 7: Commit** — `feat(engine): side-effect-free POST /entries/validate (dry-run of a post)`.

---

### Task 2: Module — `ILedgerClient.ValidateAsync` + client + fake

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/ILedgerClient.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/HttpLedgerClient.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables.Tests/Fakes.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/HttpLedgerClientTests.cs`

**Interfaces:**
- Consumes: `POST clients/{clientId}/entries/validate` (Task 1); the existing `EnsureSuccessAsync` +
  `LedgerClientException` in `HttpLedgerClient`.
- Produces: `Task ILedgerClient.ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken ct)` —
  returns on `valid`, throws `LedgerClientException(status, reason)` on rejection.

- [ ] **Step 1: Write the failing client test.** In `HttpLedgerClientTests`, using the existing
  `CapturingHandler`: a `409` ProblemDetails (`{detail:"Period is closed through 2024-03-31."}`) response →
  `ValidateAsync` throws `LedgerClientException` with `StatusCode == 409` and `Reason` containing "closed";
  and a `200 {valid:true}` response → `ValidateAsync` returns without throwing and targets
  `…/clients/{id}/entries/validate`.
- [ ] **Step 2: Run, confirm it fails** (method absent).
- [ ] **Step 3: Add to the interface.** `ILedgerClient`: `Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default);`
- [ ] **Step 4: Implement in `HttpLedgerClient`.** Mirror `PostAsync`: `Forwarded(HttpMethod.Post, $"clients/{clientId}/entries/validate")`,
  attach `JsonContent.Create(entry)`, `SendAsync`, `await EnsureSuccessAsync(response, cancellationToken)`,
  return. (No body needed; success = valid.)
- [ ] **Step 5: Update `FakeLedgerClient`.** Add a settable hook (default no-op) so tests drive rejection,
  e.g. `public Func<PostEntryRequest, Task>? OnValidate { get; set; }` and `ValidateAsync` invokes it then
  returns; tests set it to `_ => throw new LedgerClientException(409, "Period is closed …")`.
- [ ] **Step 6: Run the client tests + full receivables unit suite.** 0 warnings.
- [ ] **Step 7: Commit** — `feat(receivables): ILedgerClient.ValidateAsync (dry-run a post before committing)`.

---

### Task 3: Module — `IssueAsync` pre-flights before finalize

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables/InvoiceService.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/InvoiceServiceTests.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/ReceivablesIssueTests.cs`

**Interfaces:**
- Consumes: `ledger.ValidateAsync` (Task 2); existing `accounts.GetAsync`, `InvoicePosting.Compose`,
  `invoices.FinalizeAsync`, `ledger.PostAsync`.

- [ ] **Step 1: Write failing module tests.** In `InvoiceServiceTests` (uses `FakeLedgerClient`): (a)
  with the fake set to reject validation, `IssueAsync` on a draft **throws `LedgerClientException`**, the
  invoice is still `Draft` on read-back, and `FinalizeAsync` was never called (assert via the in-memory
  invoice store: no number assigned / still Draft, and no entry was posted — `Posted` is empty); (b) with
  the fake accepting (default), `IssueAsync` finalizes + posts exactly as today (existing assertions hold).
- [ ] **Step 2: Run, confirm (a) fails** (today it finalizes-then-posts, so the invoice would orphan / not stay Draft).
- [ ] **Step 3: Reorder `IssueAsync`.** After the `Status == Draft` and `Total > 0` guards: resolve
  `InvoicePostingAccounts` (move up), `PostEntryRequest preflight = InvoicePosting.Compose(draft, accounts)`
  (the draft already carries `IssueDate`, lines, tax, `CustomerId`; `Number` is null → `Reference` null,
  which validation ignores), `await ledger.ValidateAsync(clientId, preflight, cancellationToken)`. Only
  after it returns: `Invoice issued = await invoices.FinalizeAsync(...)`, recompose
  `InvoicePosting.Compose(issued, accounts)` (now with the assigned number in `Reference`), `await ledger.PostAsync(...)`.
- [ ] **Step 4: Run the module tests, confirm green.**
- [ ] **Step 5: Strengthen the e2e.** In `ReceivablesIssueTests.Issuing_into_a_closed_period_surfaces_the_engine_conflict_not_a_500`:
  after asserting `409` + "closed", also read the invoice back (`GET /clients/{id}/invoices/{draftId}`) and
  assert it is still **Draft** (no orphan); then issue a fresh invoice with an **open** date and assert it
  succeeds — proving fix-and-retry. Rename the test to reflect the stronger guarantee
  (`Issuing_into_a_closed_period_is_rejected_before_finalize_no_orphan`).
- [ ] **Step 6: Run the receivables suite (host classes individually).** 0 warnings.
- [ ] **Step 7: Commit** — `feat(receivables): pre-flight the A/R post before finalizing an invoice (no orphan on a bad date)`.

---

## Out of scope (separate work)

Orphan exception queue + period-close gate (the backstop for the residual TOCTOU race and genuinely-late
items); `VoidAsync` handling entry-less invoices; current-period catch-up posting; payables pre-flight
parity (fast-follow reusing the same engine primitive); real-time push notification; the AR brief's
example-date footgun (harness repo).

## Self-review notes

- Spec coverage: validate endpoint (Task 1), client method (Task 2), `IssueAsync` pre-flight + strengthened
  e2e (Task 3), parity test (Task 1 step 1e) — all spec sections covered.
- Type consistency: `EntryValidationResponse(bool Valid)`, `ValidateAsync(Guid, PostEntryRequest, CancellationToken)`,
  `EnsureOpenForPostAsync(Guid, DateOnly, CancellationToken)` used consistently across tasks.
- No-drift: enforced by `ValidateForPostAsync` being the single routine `PostEntry` and `ValidateEntry`
  both call, and `EnsureOpenForPostAsync` being the single period check `PostAsync` and the routine share.
