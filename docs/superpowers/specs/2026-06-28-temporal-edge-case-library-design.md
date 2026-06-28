# Temporal / Period-&-Fiscal-Year Edge-Case Library — Design

**Date:** 2026-06-28
**Status:** Approved (design)

## Problem

The engine's temporal guards — the closed-period freeze, the inception floor,
the monotonic close pointer, reopen, and the fiscal-year-end close guards — are
real and mostly correct, but several key paths are exercised only at the
**service/unit** level (or via the authz layer), not pinned at the **HTTP
boundary** with exact-outcome assertions. This is the same gap the settlement
edge-case library closed for money/settlement: unit tests prove the domain
throws, but the end-to-end status/message/balance contract a consumer (and the
future UI) depends on is unverified.

Concretely, at the **E2E** level today: the FY-end close guards are covered for
**December and June** clients (`FiscalYearCloseGuardTests`), close-with-pending
blockers (`PeriodCloseApiTests`), post-into-a-closed-period (`CommandQueryTests`),
reverse-into-a-closed-period (`ReverseTests`), and closing-entry-zeroes-
temporaries (`AccountTests`). But the **inception floor**, **reopen's behavioral
effect**, the **leap-aware February FY-end path**, the **monotonic close
pointer**, and **`close-year` with no retained-earnings account** are unit-only
or untested end-to-end.

## Goal

A deterministic, CI-runnable HTTP-level scenario library that pins the temporal
boundary contract — exact status + message on rejections, exact balances /
`closedThrough` / statement effects on accepted sequences — for the gaps above.
Doubles as regression armor for the period/fiscal guards. Test-only; no product
change unless a scenario surfaces a real bug.

## Scope

**In scope (genuine gaps):**
1. **Inception floor (E2E).**
2. **Reopen behavioral effect (E2E)** — post into the reopened window; partial
   (`ReopenThrough`) vs full (`null`) reopen; the "must move earlier" guard.
3. **February / leap-year FY-end (E2E)** — a `FiscalYearEndMonth = 2` client.
4. **Monotonic close pointer (E2E)** — out-of-order / repeated closes.
5. **`close-year` with no retained-earnings account → 409.**
6. **Two-year fiscal-cycle integrity sweep** — close FY1 → activity in FY2 →
   close FY2; temporaries re-zero, retained earnings accumulates.

**Out of scope (already covered E2E — do NOT duplicate):** the Dec/June FY-end
guards (`FiscalYearCloseGuardTests`), close-with-pending blockers
(`PeriodCloseApiTests`), post-into-closed (`CommandQueryTests`), reverse-into-
closed (`ReverseTests`), closing-entry-zeroes-temporaries (`AccountTests` /
`YearEndCloseTests`). Also out: the engine itself, the reopen authz/step-up
policy (`ReopenTests` covers it), Payroll/Banking/Receivables/Payables modules.

## Decisions

- **Form:** HTTP E2E tests in `Backend/Accounting101.Ledger.Api.Tests/Temporal/`,
  driven through the real host via the existing `ApiFixture`
  (`WebApplicationFactory<Program>` + shared EphemeralMongo). No new runner.
- **Assertions:** rejections assert the exact `HttpStatusCode` AND a stable
  substring of the ProblemDetails `detail` (case-insensitive); accepted/integrity
  sequences assert exact `decimal` balances, the returned `closedThrough`/
  `OpeningBalances`, and `BalanceSheetResponse.IsBalanced` + income-statement
  totals. Never assert a full message verbatim.
- **Substrings** must match the live engine messages (e.g. `"closed through"`,
  `"is this client's fiscal year-end"`, `"is not this client's fiscal year-end"`,
  `"requires a designated retained-earnings account"`).
- **Discoveries are findings, not bent tests.** A scenario that reveals wrong
  behavior is surfaced and decided per-case; tests never lock in known-wrong
  behavior, and no scenario is silently skipped.
- **Reuse, don't re-derive:** lean on `ApiFixture` helpers and the close/
  close-year/reopen request records the existing tests already use.

## Architecture

A small shared helper plus five focused files.

### Shared helper (`Temporal/TemporalScenario.cs`)
- `SeedFyeClientAsync(fixture, fiscalYearEndMonth)` — create a client with a
  given FY-end month via the `AdminClient` + `CreateClientRequest`, add a
  Controller member, return `(clientId, http)`. (Generalizes the helper currently
  inlined in `FiscalYearCloseGuardTests`.)
- `OnboardAsync(http, clientId, asOf, lines)` — open the client via the
  onboarding path so the inception freeze is seeded at `asOf − 1`.
- `PostAndApproveAsync(http, clientId, date, debit, credit, amount)` — the
  post-then-approve pattern used throughout.
- `CloseAsync` / `CloseYearAsync` / `ReopenWithFreshAuthAsync` — thin wrappers
  over the period endpoints (the reopen one mints a fresh `auth_time` client so it
  passes step-up).
- `AssertProblemAsync(resp, status, substring)` and `AssertBalancedAsync(http,
  clientId, asOf)` — same shape as the settlement library's helpers.

### Files
- **`InceptionFloorE2eTests.cs`** — onboard at 2024-01-01; a post dated
  2023-12-31 (or earlier) → **409/closed**; a post dated 2024-01-01 (and later) →
  accepted; the opening entry itself is not blocked by its own freeze.
- **`ReopenEffectE2eTests.cs`** — close through a date; a backdated post → 409;
  **reopen (full clear)** → the same post now succeeds and shows in balances;
  separately, **reopen-through an earlier date** → a post after the new freeze but
  before the old close succeeds while a post still before the new freeze is
  rejected; and **reopen not-earlier-than-current** → rejected.
- **`FiscalYearBoundaryE2eTests.cs`** — a `FiscalYearEndMonth = 2` client:
  `close-year` on **2024-02-29** (leap) → 200; on **2025-02-28** (non-leap, after
  reopening/continuing) → 200; **monthly** close on the Feb FY-end → **409**
  (use close-year); `close-year` on a non-Feb-end date → **409** naming the real
  FY-end. Exercises the leap-aware `EndDateFor` end-to-end.
- **`PeriodCloseSequenceE2eTests.cs`** — the monotonic pointer: close through
  Mar-31; then close Feb-28 → **409** (≤ current `closedThrough`); then close
  Apr-30 → 200. And `close-year` for a client whose chart has **no
  retained-earnings account**, with temporary-account activity → **409**
  `"requires a designated retained-earnings account"`.
- **`FiscalCycleIntegrityE2eTests.cs`** — open 2024-01-01; post revenue + expense
  in FY2024; `close-year` 2024-12-31 → assert temporaries are 0 and retained
  earnings equals FY2024 net income, balance sheet balanced; post revenue +
  expense in FY2025; `close-year` 2025-12-31 → assert temporaries are 0 again and
  retained earnings equals **net income #1 + net income #2** (accumulates),
  balance sheet balanced at each year-end.

## Data flow

```
ApiFixture.SeedClient/SeedFyeClient → seed chart → onboard (sets inception freeze)
   → post+approve activity → close / close-year / reopen via /periods/* endpoints
   → rejection: AssertProblem(status, substring)
   → accepted: read CloseResponse/CloseYearResponse + balance-sheet/income-statement
               → assert exact balances, closedThrough, temporaries=0, RE accumulation
```

## Implementation notes (to confirm during planning)
- The onboarding route + request body (the host path that calls
  `LedgerService.OpenAsync` and seeds the inception freeze) — confirm against
  `OnboardingTests` (Api.Tests).
- Exact ProblemDetails `detail` strings + status for: post-into-closed,
  FY-end monthly-close guard, close-year-wrong-date, no-RE close-year, reopen
  guards — confirm against the endpoint source and the existing tests.
- The reopen step-up `auth_time` claim mechanics — confirm against `ReopenTests`
  (the fresh-auth client construction).
- The balance-sheet / income-statement routes + DTOs for the integrity sweep —
  confirm against `FinancialStatementTests` (`BalanceSheetResponse`,
  `IncomeStatementResponse`, `StatementSectionResponse`).
- Whether seeding a non-Dec `FiscalYearEndMonth` requires the admin
  `CreateClientRequest` path (it does — confirm the field name + admin claim).

## Success criteria
- `dotnet test Accounting101.Ledger.Api.Tests` is green.
- Every rejection scenario pins HTTP status **and** message substring.
- Every accepted/integrity scenario pins exact balances + `closedThrough` /
  statement effects; the two-year sweep proves RE accumulation and temporary
  re-zeroing across two year-ends.
- No product behavior changes; any real bug a scenario surfaces is reported and
  decided, not silently skipped.
