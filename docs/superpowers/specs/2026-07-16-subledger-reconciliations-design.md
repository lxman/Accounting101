# Subledger Reconciliations Screen — Design

**Status:** Approved for planning
**Date:** 2026-07-16
**Branch:** `feat/subledger-reconciliations`

## Goal

Build the third "Assurance ▸ Audit" screen the nav promises — `/audit/reconciliations`, currently the generic "Coming soon" placeholder. It shows, for every dimensioned control account in the client's chart, whether its subsidiary ledger **ties out** to the general-ledger control balance (control vs subledger vs variance), and lets a row expand into the per-dimension-value breakdown to locate drift. This completes the Assurance ▸ Audit trio (Audit Trail + Verify Integrity + Subledger Reconciliations).

## Background & Current State

The "ledger-first subledger invariant" epic established that a subledger balance is a *fold of the ledger*, never stored — so it must tie out to the GL control account. The engine already exposes the per-pair math:

- `GET /clients/{id}/subledger/reconciliation?account=&dimension=&asOf=` → `SubledgerReconciliationResponse(Guid Account, string Dimension, DateOnly? AsOf, decimal ControlBalance, decimal SubledgerTotal, decimal Variance, bool TiesOut)`. Ties out **one (control account, dimension) pair** per call. Gated `gl.read` (`Permission.Read`).
- `GET /clients/{id}/subledger?account=&dimension=&asOf=` → `SubledgerResponse(string Dimension, DateOnly? AsOf, IReadOnlyList<SubledgerLineResponse> Lines)` where `SubledgerLineResponse(Guid AccountId, Guid DimensionValue, decimal Balance, string? Number, string? Name)` — the per-dimension-value breakdown. Gated `gl.read`.

Both live in `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (routes at lines 48–49); response records in `Backend/Accounting101.Ledger.Contracts/SubledgerContracts.cs`. The folds are on the journal: `AggregateBalancesAsync(clientId, asOf, ct) → IReadOnlyDictionary<Guid,decimal>` (control balances = the trial-balance fold) and `AggregateSubledgerAsync(clientId, dimension, account, asOf, includePending?, ct) → IReadOnlyList<SubledgerBalance>` where `SubledgerBalance(Guid AccountId, Guid DimensionValue, decimal Balance)`. `Variance = control − Σsubledger` = the untagged remainder (lines that hit the account without the dimension tag).

**Control accounts are self-identifying from the chart.** A control account is any account with `RequiredDimensions.Count > 0`; each required dimension is an axis to reconcile. `ChartOfAccounts.Accounts` (`IReadOnlyCollection<Account>`) enumerates all accounts; `Account.RequiredDimensions` (`IReadOnlyCollection<string>`) is the discriminator. So the engine can discover everything from the chart — no module coupling. The current dimensioned control accounts are: **AR** (`Customer`, `Invoice`), **AP** (`Vendor`, `Bill`), **Fixed Assets** (`Asset`), **Inventory** (`Item`). **Cash and Payroll have no `RequiredDimensions`** (single control-account fold, nothing to split), so they never appear — correct, there is nothing to reconcile there.

**Constraint that shaped the design — no "reconcile everything" endpoint exists**, and the FE has no way to discover each module's configured control-account id (the per-pair endpoint requires the caller to already know account+dimension). Rather than a fragile FE two-hop through chart-readiness, the chart itself is the discovery source.

**Gating context.** The nav leaf `/audit/reconciliations` is `area: 'audit'` (nav visible to `audit.read` holders via `hasArea`). The existing subledger endpoints are `gl.read`. The existing `/subledger*` endpoints have **many consumers** — AR/AP/Inventory ledger-first proof tests, `SubledgerTests`, `AccountLabelingTests`, `RequiredDimensionSetTests` — some of which call them with **module-clerk callers** (`ArClerk`/`ApClerk`, whose presets are `{gl.read, …}` *without* `audit.read`). Flipping those endpoints to `audit.read` would break those tests. So this design **does not touch the existing endpoints**: it adds one new `audit.read`-gated aggregating endpoint, and the breakdown drill reuses the existing `gl.read` `/subledger`. This is safe because every Reads role preset that grants `gl.read` also grants `audit.read` (verified during the audit-screens work), so every real viewer of this screen holds both — the breakdown drill works for all of them.

**No existing FE** for subledger reconciliation (the `reconcil*` files under `UI/Angular` are the unrelated Bank Reconciliation area at `/cash/reconciliation`).

## Design

### Backend — one new aggregating endpoint

`GET /clients/{id}/subledger/reconciliations?asOf=` (plural; distinct from the existing singular `/subledger/reconciliation`). Chart-driven and `audit.read`-gated:

- Resolve via `gateway.ResolveCapabilityAsync(user, clientId, Capabilities.AuditRead, ct)` — the same capability-string gateway method the audit endpoints use (leaves the `Permission`↔cap maps untouched).
- Load the chart + all control balances once (`AggregateBalancesAsync`). For every account with `RequiredDimensions.Count > 0` (ordered by account `Number`, ordinal), and for each of its `RequiredDimensions` (in declared order), fold the subledger (`AggregateSubledgerAsync`), compute `variance = control − Σsubledger`, and emit a labeled line.
- Returns:
```csharp
public sealed record SubledgerReconciliationsResponse(
    DateOnly? AsOf, IReadOnlyList<SubledgerReconciliationLine> Lines);

public sealed record SubledgerReconciliationLine(
    Guid Account, string? Number, string? Name, string Dimension,
    decimal ControlBalance, decimal SubledgerTotal, decimal Variance, bool TiesOut);
```
Added to `SubledgerContracts.cs`. AR/AP each contribute two lines (one per dimension) for the same account; Cash/Payroll contribute none (no dimensions). A client with no dimensioned control accounts returns an empty `Lines` list (not an error).

The existing per-pair `/subledger/reconciliation` and breakdown `/subledger` are **unchanged** (routes, gating, behavior).

### Frontend — one screen with expandable breakdown

`features/audit/subledger-reconciliations.ts` at route `/audit/reconciliations`. Mirrors the simplicity of `verify-integrity.ts` (single load, signals, OnPush) with a table like `audit-trail.ts`; the multi-item shape echoes the Chart-Health widget.

- **Service** `core/subledger/subledger.service.ts` (+ interfaces in `core/subledger/subledger.ts`):
  - `reconciliations(asOf?): Observable<SubledgerReconciliationsResponse>` → GET `/clients/{id}/subledger/reconciliations`.
  - `breakdown(account, dimension, asOf?): Observable<SubledgerResponse>` → GET `/clients/{id}/subledger?account=&dimension=` (reuses the existing endpoint).
  - FE interfaces mirror the wire: `SubledgerReconciliationLine`, `SubledgerReconciliationsResponse`, `SubledgerResponse`, `SubledgerLineResponse`.
- **Summary:** on load, call `reconciliations()`. Render one row per line: **Account** (`number` + `name`), **Dimension**, **Control** (`money(controlBalance)`), **Subledger** (`money(subledgerTotal)`), **Variance** (`money(variance)`), **Status** — a green "Ties out" badge when `tiesOut`, else a red "Variance" badge. Empty state: "No dimensioned control accounts to reconcile." (covers a core-only client like Cash/Payroll-only).
- **Breakdown drill:** each row is expandable (a caret / click on the row toggles an `expanded` set keyed by `account|dimension`). On first expand, lazy-call `breakdown(account, dimension)` and cache it (a signal map keyed by `account|dimension`). The expanded panel lists each `SubledgerLineResponse` — `number ?? name ?? dimensionValue` and `money(balance)` — followed, when `variance !== 0`, by an explicit **"Untagged remainder (not attributed to any {dimension}): {money(variance)}"** line, since the variance is composed of lines lacking the dimension and therefore does not appear among the tagged breakdown values. A breakdown load error (e.g. a bespoke `audit.read`-without-`gl.read` set 403s the `/subledger` call) is surfaced inline in the panel, not fatal to the screen.
- **Gating:** no new route guard — access = nav-gate (`area: 'audit'` → `audit.read`) + backend 403 (the aggregating endpoint enforces `audit.read`). Route added; removed from the Placeholder fallback by adding `'/audit/reconciliations'` to the `built` array in `app.routes.ts` (the singular `/audit` redirect and `/audit/trail`/`/audit/verify` from the audit-screens slice are unaffected).

### Wire shapes (backend record ↔ FE interface, host camelCase)
- `SubledgerReconciliationsResponse { asOf: string | null; lines: SubledgerReconciliationLine[] }`
- `SubledgerReconciliationLine { account: string; number: string | null; name: string | null; dimension: string; controlBalance: number; subledgerTotal: number; variance: number; tiesOut: boolean }`
- `SubledgerResponse { dimension: string; asOf: string | null; lines: SubledgerLineResponse[] }`; `SubledgerLineResponse { accountId: string; dimensionValue: string; balance: number; number: string | null; name: string | null }`

## Testing

**Backend (xUnit, host — mirror `SubledgerTests`/`RequiredDimensionSetTests` for seeding a control account with `RequiredDimensions` and posting dimensioned lines):**
- **Ties out:** a control account with a required dimension, all lines tagged → the reconciliations list contains a line for it with `Variance == 0`, `TiesOut == true`, correct `Number`/`Name`/`Dimension`, and `ControlBalance == SubledgerTotal`.
- **Variance:** post a line to the same control account WITHOUT the dimension tag → its reconciliation line has `Variance == thatAmount`, `TiesOut == false`.
- **Two-dimension account:** a control account requiring two dimensions yields two lines (one per dimension).
- **Dimensionless absence:** a plain account (no `RequiredDimensions`) does NOT appear; a client with no dimensioned control accounts returns an empty `Lines`.
- **Gating:** a member with `audit.read` (default `SeedClientAsync` Controller) gets 200; a member holding `gl.read` but not `audit.read` (`AddMemberAsync(clientId, LedgerRole.ArClerk, …)`) gets 403 — proving the new endpoint is `audit.read`-gated and distinct from the `gl.read` `/subledger*` endpoints.

**Frontend (Vitest + TestBed, stub `SubledgerReconciliationsService`):**
- Summary renders a row per line with the money values and the correct Ties-out/Variance badge (a tying line → green "Ties out"; a variance line → red "Variance").
- Expanding a row lazy-calls `breakdown(account, dimension)` once (a second expand does not re-call), renders the per-value lines, and shows the "Untagged remainder … {variance}" line for a variance row.
- Empty `lines` → the empty-state message.

## Task Decomposition (3 tasks)

1. **Backend — aggregating endpoint.** `SubledgerReconciliationsResponse` + `SubledgerReconciliationLine` in `SubledgerContracts.cs`; `GetSubledgerReconciliations` handler (chart-driven, `audit.read`-gated) + route `GET /subledger/reconciliations`; host tests (ties-out, variance, two-dimension, dimensionless-absence, empty, `audit.read` gating).
2. **Frontend — service + summary screen.** `subledger.service.ts` (`reconciliations` + `breakdown`) + `subledger.ts` interfaces; `subledger-reconciliations.ts` summary table + empty state + route/`built`-array + spec (render + badges + empty state).
3. **Frontend — expandable breakdown drill.** Row expand/collapse + lazy `breakdown` cache + per-value panel + untagged-remainder line + inline error; extend the spec (expand lazy-loads once, renders values + remainder).

## Global Constraints

- **Backend:** namespaces follow folder structure. The new endpoint is **additive** — existing `/subledger` and `/subledger/reconciliation` (routes, `gl.read` gating, behavior) are NOT touched. `audit.read` via `ResolveCapabilityAsync` (no `Permission`-map changes). Chart-driven discovery (`Account.RequiredDimensions`), no module coupling. Rider auto-converts explicit types to `var` — stage explicit file lists and check for stray churn before each commit.
- **Frontend:** standalone, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. No new route guard (nav-gate `area:'audit'` + backend 403). FE test runner is **Vitest** (`vi.fn`/`vi.spyOn` global). Conditional Tailwind classes with special chars (`hover:bg-muted/50`) use a `[class]="cond ? '…' : ''"` string binding, never `[class.hover:bg-muted/50]`.
- **Wire shapes** identical backend ↔ frontend (host `JsonNamingPolicy.CamelCase`).
- Only touch files named per task. Do NOT change the existing subledger/audit endpoints, the audit-trail/verify screens, the Bank Reconciliation area, or unrelated modules.
- `environment.ts` stays modified/uncommitted (never commit).
- Branch `feat/subledger-reconciliations`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## Out of Scope / Non-Goals

- Flipping the existing `/subledger`/`/subledger/reconciliation` endpoints to `audit.read` (would break module ledger-first proof tests; the breakdown drill practically requires `gl.read`, which every real viewer holds).
- Reconciling Cash/Payroll (no dimensioned control account — nothing to tie out).
- A per-value drill *beyond* the one breakdown level (e.g. into a single customer's transactions) — the breakdown lists the dimension values and their balances only.
- Any write/adjustment action to resolve a variance — this screen is read-only assurance.
