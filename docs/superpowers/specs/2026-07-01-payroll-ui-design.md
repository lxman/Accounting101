# Payroll Module UI — Design

**Date:** 2026-07-01
**Status:** Approved (design)
**Module:** Payroll (`Modules/Payroll/*` backend already complete; this is the Angular UI)

## Goal

Build the Angular UI for the Payroll module: list, record, and view/void the two
Payroll document types (payroll runs and tax remittances), replacing the current
`/payroll` placeholder route.

## Backend reality (what we build against)

The Payroll backend is simpler than AR/AP — no drafts, no allocation, no
customer/vendor link. Two evidentiary document types, each **record-then-void**
(one-step lifecycle `Posted → Void`); the module does no withholding-table math
(all amounts are clerk-supplied). Endpoints (all under `/clients/{clientId}`):

- `POST /payroll-runs` → 201, body `RecordPayrollRunRequest(decimal Gross, decimal EmployeeFica, decimal EmployerFica, decimal Deductions, decimal IncomeTaxWithheld, DateOnly PayDate, string? Memo)` → returns `PayrollRun`. 422 if net pay negative.
- `POST /payroll-runs/{runId}/void` → 200, body `VoidReasonRequest(string? Reason)`. 409 if not voidable.
- `GET /payroll-runs/{runId}` → 200 `PayrollRunView(PayrollRun Run)`; 404 unknown.
- `GET /payroll-runs?skip&limit&order&includeVoided` → 200 `PagedResponse<PayrollRunView>`.
- `POST /tax-remittances` → 201, body `RecordTaxRemittanceRequest(decimal WithholdingsAmount, decimal TaxesAmount, DateOnly PayDate, string? Memo)` → returns `TaxRemittance`.
- `POST /tax-remittances/{remittanceId}/void` → 200, body `VoidReasonRequest`.
- `GET /tax-remittances/{remittanceId}` → 200 `TaxRemittanceView(TaxRemittance Remittance)`; 404 unknown.
- `GET /tax-remittances?skip&limit&order&includeVoided` → 200 `PagedResponse<TaxRemittanceView>`.

`PayrollRun`: `{ Guid Id, string? Number, decimal Gross, decimal EmployeeFica, decimal EmployerFica, decimal Deductions, decimal IncomeTaxWithheld, DateOnly PayDate, string? Memo, PayrollRunStatus Status }` (`Posted | Void`).
`TaxRemittance`: `{ Guid Id, string? Number, decimal WithholdingsAmount, decimal TaxesAmount, DateOnly PayDate, string? Memo, TaxRemittanceStatus Status }` (`Posted | Void`).

List query params: `skip` (default 0, min 0), `limit` (default 50, clamp [1,200]),
`order` (`asc|desc`, default `desc`, invalid → 400), `includeVoided` (default false).

Net pay (derived, not stored) = `Gross − EmployeeFica − IncomeTaxWithheld − Deductions`.
Remittance total (derived) = `WithholdingsAmount + TaxesAmount`.

## Global Constraints

- USD-only; camelCase JSON on the wire.
- Angular 22 + Signal Forms; zoneless; OnPush; Spartan NG helm components; vitest via `ng test`.
- Follow the established module-UI conventions: shell + tabs (`ReceivablesShell`/`PayablesShell`), whole-row-click-to-detail on list rows, void-on-detail (not on the list), `PagedResponse` envelope, `extractProblem(e).detail` on error paths, `takeUntilDestroyed(this.destroyRef)` on every inline subscribe, root-singleton core service with `client.clientId()` base URL.
- No backend changes — the Payroll backend is complete and already tested.
- Money formatting via the existing `money`/`displayDate` display helpers.

## Components & structure

Under `UI/Angular/src/app/`:

- `core/payroll/payroll.ts` — types: `PayrollRun`, `TaxRemittance`, `PayrollRunStatus`, `TaxRemittanceStatus`, `RecordPayrollRunRequest`, `RecordTaxRemittanceRequest`, list-query type, plus small derived helpers `netPay(run)` and `remittanceTotal(r)`.
- `core/payroll/payroll.service.ts` — root singleton `PayrollService`: `listRuns(q)`, `getRun(id)`, `recordRun(req)`, `voidRun(id, reason?)`, `listRemittances(q)`, `getRemittance(id)`, `recordRemittance(req)`, `voidRemittance(id, reason?)`, and `entriesForSource(sourceRef)` (GET `/entries?sourceRef=` for the posted-entry link). Base URL `${apiBaseUrl}/clients/${clientId()}`.
- `features/payroll/payroll-shell.ts` — tabs **Runs | Remittances** + `<router-outlet/>`.
- `features/payroll/run-list.ts`, `run-editor.ts`, `run-detail.ts`.
- `features/payroll/remittance-list.ts`, `remittance-editor.ts`, `remittance-detail.ts`.
- `app.routes.ts` — replace the `/payroll` placeholder with a shell route: `runs`, `runs/new`, `runs/:id`, `remittances`, `remittances/new`, `remittances/:id`, default redirect to `runs`.

## Screens

**Run list** — paged table: Number · Pay date · Gross · Net pay · Status. Whole-row → `runs/:id`. "Record payroll run" button → `runs/new`. `includeVoided` toggle; desc default. Voided rows show a badge.

**Remittance list** — paged table: Number · Pay date · Withholdings · Taxes · Total · Status. Same conventions. "Record remittance" → `remittances/new`.

**Run editor** — signal-form inputs: gross, employee FICA, employer FICA, deductions, income tax withheld, pay date (default today), memo. Live **Net pay** figure. A computed `netPayWarning: string | null` returns a red message when net pay < 0 ("Net pay is negative ({net}) — gross must cover FICA, withholding, and deductions.") shown next to Save with Save disabled (reuses the over-allocation warning pattern). On save POSTs and navigates to `runs/:id`.

**Remittance editor** — inputs: withholdings amount, taxes amount, pay date, memo. Live **Total**. Posts → `remittances/:id`.

**Run detail** — shows Number, Status, Pay date, Memo, the five amount fields, computed Net pay, and a **Void** action (confirm + optional reason) present only while `Posted`; void → reload. Plus a **"Posted journal entry →"** link: on load, `entriesForSource(run.id)` fetches the posted entry and links to the existing Journal `EntryDetail` at `/journal/{entryId}` (decoupled — reads the real posted entry, does not recompute the recipe). If no entry is found (edge), the link is omitted.

**Remittance detail** — same layout: fields, computed Total, Void action while `Posted`, and the posted-journal-entry link.

## Dev-stack prerequisite (not app code, but required to run)

`.localdev/start.ps1` (gitignored) needs a `Payroll__Accounts__*` block mapped to
seeded Demo Co GUIDs: `SalariesExpense`, `PayrollTaxExpense`, `Cash` (1000, already
seeded), `WithholdingsPayable`, `PayrollTaxesPayable`. Seed the four missing accounts
(Cash reused) with non-colliding numbers (payroll payables 2200/2300 must not clash
with existing seeded accounts — verify and adjust numbers if needed). Without this
block, recording a run fails with "posting account not configured."

## Out of scope

- No draft/edit for payroll documents (backend has none).
- No withholding-table computation (clerk supplies all amounts).
- No employee-level breakdown (a run is a single aggregate — the backend has no employee entity).
- No backend changes.

## Testing & verification

Vitest specs per component (mirror AR/AP specs): each list renders rows + paging +
includeVoided; each editor posts the correct request body and (run) shows the
net-pay figure + negative warning + disabled Save, then clears; each detail renders
fields + computed derived value, performs void, and renders the posted-entry link
from a stubbed `entries?sourceRef=` response. Run: `cd UI/Angular && npx ng test --watch=false`.
Execution: one branch, subagent-driven.
