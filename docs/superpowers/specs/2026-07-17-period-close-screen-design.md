# Period Close screen (General Ledger) — Design

**Date:** 2026-07-17
**Status:** Spec for review
**Area:** General Ledger ▸ Period Close (`/periods`)

## Context

`/periods` ("Period Close") is a nav leaf that has always fallen through to the shared `Placeholder`
("Coming soon."). The backend close machinery is complete and battle-tested — monthly close, year-end
close, the pending-entry blocker gate, the fiscal-year-end guard, and monotonic-close enforcement all
exist. This slice builds the **frontend screen** that drives them, plus **one small backend read
endpoint** so the screen can show the current period state (there is currently no way to read the
closed-through date).

**Reopen is deliberately excluded.** A closed period is final; corrections flow through an adjusting
entry posted in the current open period (exactly how the engine's closed-period repair already works).
Excluding reopen keeps the control model clean and removes the step-up/`auth_time` re-auth machinery
entirely.

## Scope

**In scope:**
- Backend: `GET /clients/{id}/periods/status` (gl.read) returning `{ closedThrough, fiscalYearEndMonth }`,
  backed by a one-line pass-through on `LedgerService`. New `PeriodStatusResponse` contract.
- Frontend: `PeriodsService`, the `PeriodClose` screen, its route + `built`-array entry, and specs.

**Out of scope (deliberately):**
- **Reopen** — closed periods are immutable; correct via an adjusting entry in the open period.
- Any change to the existing `close` / `close-year` / `reopen` endpoints, the engine, or the blocker/
  fence logic. This slice only *reads* status and *calls* the existing write endpoints.
- Inline approve/void of blockers on this screen — blockers link out to entry detail, where
  approve/void already live.

## Global Constraints

- **Backend:** namespaces follow folder structure. Additive only — the existing `/periods/close`,
  `/periods/close-year`, `/periods/reopen` routes and handlers are NOT touched. `gl.read` via
  `gateway.ResolveAsync(user, clientId, Permission.Read, ct)` (the gate every GL read endpoint uses).
  **Rider auto-converts explicit types to `var`** — stage the explicit file list per task and check
  `git diff --cached --stat` for stray churn before each commit.
- **Frontend:** standalone, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted;
  2-space template indent. FE test runner is **Vitest** (`vi.fn`/`vi.spyOn` global). Conditional
  Tailwind classes with special chars (`hover:bg-muted/50`) use `[class]="cond ? '…' : ''"`, never
  `[class.hover:bg-muted/50]`. Capability gating via the existing `*appCan` directive / `CapabilityService`.
- **Wire shapes** identical backend record ↔ FE interface (host `JsonNamingPolicy.CamelCase`).
- The `built` array in `app.routes.ts` gets EXACTLY `'/periods'` added.
- The `PeriodClose` screen is a normal content page (not a list) — it does NOT use the full-height list
  frame; it renders as top-aligned cards inside `<main>`.
- `environment.ts` stays modified/uncommitted (never commit).
- Branch `feat/period-close-screen`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## Existing backend contracts (referenced, unchanged)

```csharp
public sealed record ClosePeriodRequest(DateOnly AsOf);
public sealed record CloseResponse(DateOnly AsOf, IReadOnlyList<AccountBalanceResponse> OpeningBalances);
public sealed record CloseYearRequest(DateOnly FiscalYearEnd);
public sealed record CloseYearResponse(EntryResponse? ClosingEntry);
public sealed record PendingEntryRef(Guid EntryId, string? Reference, DateOnly EffectiveDate, string Type);
public sealed record AccountBalanceResponse(Guid AccountId, decimal Balance, string? Number = null, string? Name = null);
```

**`POST /clients/{id}/periods/close` `{ asOf }`** returns one of:
- `200` `CloseResponse` — closed; opening balances snapshot.
- `409` blockers — `{ detail, extensions: { blockers: PendingEntryRef[] } }` — pending in-period entries.
- `409` FY-end guard — `{ detail, extensions: { useEndpoint: "periods/close-year", fiscalYearEnd: "YYYY-MM-DD" } }`.
- `409` already-closed — plain `{ detail }`.

**`POST /clients/{id}/periods/close-year` `{ fiscalYearEnd }`** returns `200` `CloseYearResponse`
(closing entry, or `null` if the year had nothing to close) or the same `409` blocker shape.

## Backend design

### B1. Read pass-through on `LedgerService`

`closedThrough` is already read internally via `_checkpoints.GetClosedThroughAsync(clientId, ct)`
(`MongoCheckpointStore`). Expose it (concrete `LedgerService`, no interface to update):

```csharp
/// <summary>The date the client's ledger is frozen through, or null if no period has been closed.</summary>
public Task<DateOnly?> GetClosedThroughAsync(Guid clientId, CancellationToken cancellationToken = default)
    => _checkpoints.GetClosedThroughAsync(clientId, cancellationToken);
```

### B2. Contract

Append to the contracts (same file as the other period responses, e.g. `EntryResponses.cs`):

```csharp
/// <summary>Current period state: the date the ledger is closed through (null if never closed) and the
/// client's fiscal-year-end month (1–12), so the UI can identify the year-end close date.</summary>
public sealed record PeriodStatusResponse(DateOnly? ClosedThrough, int FiscalYearEndMonth);
```

### B3. Endpoint

Register next to the other period routes (`clients.MapGet("/periods/status", GetPeriodStatus);`) and add:

```csharp
private static async Task<IResult> GetPeriodStatus(
    Guid clientId, LedgerGateway gateway, ControlStore control, ClaimsPrincipal user, CancellationToken cancellationToken)
{
    LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
    if (ctx.Failed) return ctx.Error;

    DateOnly? closedThrough = await ctx.Ledger.Service.GetClosedThroughAsync(clientId, cancellationToken);
    ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
    int fiscalMonth = client is null ? 12 : FiscalYear.MonthOf(client);
    return Results.Ok(new PeriodStatusResponse(closedThrough, fiscalMonth));
}
```

## Frontend design

### F1. Interfaces (`core/periods/periods.ts`)

```ts
export interface PeriodStatus { closedThrough: string | null; fiscalYearEndMonth: number; }
export interface PendingEntryRef { entryId: string; reference: string | null; effectiveDate: string; type: string; }
export interface CloseResponse { asOf: string; openingBalances: { accountId: string; balance: number; number: string | null; name: string | null; }[]; }
export interface CloseYearResponse { closingEntry: { id: string } | null; }
```

### F2. Service (`core/periods/periods.service.ts`)

Root-provided; base-URLs via `ClientContextService` + `environment` (mirrors `SubledgerService`):

```ts
status(): Observable<PeriodStatus>                         // GET  /periods/status
close(asOf: string): Observable<CloseResponse>             // POST /periods/close        { asOf }
closeYear(fiscalYearEnd: string): Observable<CloseYearResponse> // POST /periods/close-year { fiscalYearEnd }
```

Errors are surfaced to the caller as the raw `HttpErrorResponse`; the screen uses the existing
`extractProblem` helper to read `detail` and the `blockers` / `useEndpoint` / `fiscalYearEnd`
extensions off the 409 `ProblemDetails`.

### F3. `PeriodClose` screen (`features/periods/period-close.ts`)

Standalone, `OnPush`, signals. Loads `status()` on construction.

**Status card** — "Closed through {closedThrough}" (formatted) or "No periods have been closed yet.";
plus the fiscal year-end (month name).

**Principle note** (static): *"Closed periods are final. To correct a closed period, post an adjusting
entry in the current period."*

**Next period to close** (guided) — compute from `status`:
- `nextMonthEnd` = last day of the month *after* `closedThrough`; if `closedThrough` is null, default to
  the last day of the **previous calendar month** relative to today (a sensible "close last month").
- If `nextMonthEnd` **is** the fiscal year-end (its month === `fiscalYearEndMonth` and it is the month's
  last day), render the **year-end** affordance instead of the monthly one (proactive FY-end steer).
- Otherwise a primary button **"Close {Month YYYY}"** → `close(nextMonthEnd)`.
- A secondary **month-picker** (month + year selects; closes through that month's last day) for any other
  period.

**Year-end close** — button **"Run year-end close ({FY-end date})"** → `closeYear(fyEndDate)`, where
`fyEndDate` is the fiscal-year-end date **on or after `nextMonthEnd`** — deterministic from
`fiscalYearEndMonth` (e.g. month 12 → Dec 31 of `nextMonthEnd`'s year; a mid-year FY-end rolls to the
next occurrence). On `200`: refresh status; if a closing entry was returned, show a `gl.read`-gated link
to `/journal/{closingEntry.id}`.

**Close/close-year error handling** (`extractProblem`):
- `blockers` present → render a "Can't close — N entr(y/ies) still awaiting approval" panel listing each
  `PendingEntryRef` (`effectiveDate` · `reference` · `type`), each row a `gl.read`-gated link to
  `/journal/{entryId}`; a **"Retry close"** button re-issues the same close.
- `useEndpoint`/`fiscalYearEnd` present → message steering to year-end, with the year-end button.
- otherwise → show `detail` (already-closed, validation, etc.).

**Gating** — the "Close …" and "Run year-end close" buttons are shown only with `gl.close`
(`*appCan="['gl.close']"`); blocker/closing-entry journal links only with `gl.read`. The screen itself is
reachable at `gl.read` via the existing nav gate (`area: 'gl'`); **no new route guard**.

**Success feedback** — after any successful close/year-end, re-fetch `status()` so the card and the
"next period" advance; clear any prior error/blocker panel.

### F4. Route

`app.routes.ts`: import `PeriodClose`; add `{ path: 'periods', component: PeriodClose }`; add `'/periods'`
to the `built` array.

## Wire shapes

`PeriodStatusResponse { closedThrough: string|null, fiscalYearEndMonth: number }` (host CamelCase) ==
FE `PeriodStatus`. No enums on the wire → no casing trap. `PendingEntryRef` fields
(`entryId/reference/effectiveDate/type`) identical backend ↔ FE.

## Error handling

- `GET /periods/status` — always `200` for a gl.read caller (null `closedThrough` before any close);
  `403` without gl.read.
- Close/close-year `409`s handled per F3; the screen never leaves the user without a next action (retry,
  resolve blockers, or switch to year-end).
- A missing/failed status load shows an inline error and disables the close actions.

## Testing

**Backend** (`Accounting101.Ledger.Api.Tests/PeriodStatusTests.cs`, `ApiFixture`):
- Before any close → `GET /periods/status` returns `closedThrough: null` and the seeded
  `fiscalYearEndMonth`.
- After `POST /periods/close` through a month-end → status returns that `closedThrough`.
- gl.read gating: a caller without gl.read (e.g. a preset lacking it) gets `403`; a gl.read caller `200`.

**Frontend** (`features/periods/period-close.spec.ts`, Vitest + TestBed, service stubbed):
- Status card renders "Closed through …" and the fiscal year-end; empty-state when `closedThrough` null.
- Guided next-month button calls `close(nextMonthEnd)` and re-fetches status on success.
- Month-picker close calls `close(chosenMonthEnd)`.
- Blocked `409` renders the blocker rows as `/journal/{id}` links + a "Retry close" that re-calls `close`.
- FY-end steer: when the next month-end is the fiscal year-end, the year-end affordance is shown (not the
  monthly button); the year-end button calls `closeYear(fyEnd)`.
- Capability gating: without `gl.close`, the close/year buttons are absent; with it, present.

## Self-review

- New read endpoint + `PeriodStatusResponse` + `LedgerService` pass-through → backend task. ✓
- Existing close/year/reopen endpoints untouched (only a new GET + a new public read method). ✓
- `PeriodsService` + `PeriodClose` screen (status, guided close, month-picker, blocker links, FY-end
  steer, year-end, principle note, gating) + route/`built` → frontend tasks. ✓
- No reopen anywhere; no `auth_time`/step-up change. ✓
- Gating: gl.read (screen + status), gl.close (close/year actions), gl.read (journal links). ✓
