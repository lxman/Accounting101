# Fiscal-Year-End Correction — Design

**Date:** 2026-06-28
**Status:** Approved (design)

## Context

A client's `FiscalYearEndMonth` (a scalar `int`, default 12, on
`ClientRegistration`) is set **only at client creation** via the admin
`POST /admin/clients` endpoint. There is no way to change it afterward — a
typo (or a deliberate change) currently requires recreating the client.

A full **effective-dated** FY-end history (a `{effectiveFrom, month}` timeline,
short-year handling, close-year picking the right FY-end per period) is
deliberately deferred — it addresses a rare corporate event and is speculative
with no customers yet. This slice is the **minimal, non-speculative** version:
let an admin correct/change the scalar **forward-only**.

This is safe by construction: **already-closed fiscal years are immutable facts
in the ledger** (the journal records what happened; a closed year is sealed).
Changing the config only affects the validation of *future* closes. So a plain
scalar update needs no timeline — it cannot retroactively alter history.

## Goal

An admin endpoint to update a client's `FiscalYearEndMonth` after creation, with
the same 1–12 validation as create, so a wrong-at-creation month can be corrected
(or changed going forward) without recreating the client.

## Scope

**In scope:** a `PUT /admin/clients/{clientId}/fiscal-year-end` admin endpoint;
its request DTO; tests.

**Out of scope (still deferred / not built):** effective-dated FY-end history;
per-period FY-end selection; short-year handling; any change to how `close-year`
validates (it keeps reading the single current scalar via
`FiscalYear.MonthOf`); retroactively altering closed years (impossible by design).

## Architecture

Mirrors the existing `AdminEndpoints` patterns exactly. No new `ControlStore`
method — `RegisterClientAsync` already does a `ReplaceOneAsync` by id, so an
update is `GetClientAsync` → mutate the scalar → `RegisterClientAsync`.

### Endpoint (`AdminEndpoints`, behind the `DeploymentAdmin` policy)

`PUT /admin/clients/{clientId:guid}/fiscal-year-end`, body
`SetFiscalYearEndRequest(int FiscalYearEndMonth)`:
1. Validate `FiscalYearEndMonth` ∈ 1..12, else 400 (same message/shape as
   `CreateClient`).
2. `control.GetClientAsync(clientId)`; null → 404.
3. Set `registration.FiscalYearEndMonth = request.FiscalYearEndMonth`;
   `control.RegisterClientAsync(registration)` (replace by id).
4. Return 200 `ClientRegistrationResponse(Id, Name, DatabaseName,
   RequireSegregationOfDuties, FiscalYear.MonthOf(registration))` — the same
   response shape `CreateClient`/`ListClients` return.

### Contract (`AdminContracts.cs`)

```csharp
/// <summary>Change a client's fiscal-year-end month (1-12), forward-only. Already-closed years are
/// immutable; this affects only future closes.</summary>
public sealed record SetFiscalYearEndRequest(int FiscalYearEndMonth);
```

## Data flow

```
PUT /admin/clients/{id}/fiscal-year-end { FiscalYearEndMonth }   [DeploymentAdmin]
  → validate 1..12 (else 400)
  → GetClientAsync (null → 404)
  → registration.FiscalYearEndMonth = month → RegisterClientAsync (ReplaceOne by id)
  → 200 ClientRegistrationResponse (FiscalYear.MonthOf)
```

## Error handling

- `FiscalYearEndMonth` out of 1..12 → 400 ("FiscalYearEndMonth must be between 1
  and 12.").
- Unknown `clientId` → 404.
- Not a deployment admin → the `DeploymentAdmin` policy returns 401/403 (existing
  group behavior; no per-endpoint handling).

## Testing

(`AdminTests` in `Accounting101.Ledger.Api.Tests`, alongside the existing
create/list/member admin tests.)
- Update succeeds: create a client (FY-end 12) → `PUT` FY-end 6 → 200, response
  `FiscalYearEndMonth == 6`; a follow-up `GET /admin/clients` (or the create
  response shape) reflects 6.
- The new month drives validation: after changing to a June FY-end, a
  `close-year` on 2024-06-30 is accepted and a `close-year` on 2024-12-31 is
  rejected (the FY-end guard now reads the updated scalar) — a focused E2E using
  the existing temporal/close-year helpers, OR a narrower assertion that
  `FiscalYear.MonthOf` reflects the update (keep it proportional to the slice).
- Out-of-range month (0 or 13) → 400.
- Unknown client id → 404.
- (Documented, not a test of retroactivity: a year closed before the change
  remains closed/immutable — the change is forward-only by nature.)

## Success criteria

- An admin can change a client's `FiscalYearEndMonth` after creation; the value
  persists and drives subsequent close-year validation.
- Validation (1–12) and not-found (404) behave like the sibling admin endpoints.
- No effective-dated machinery added; `close-year` validation is unchanged (still
  reads the single scalar); closed years remain immutable.
- New tests green; existing suites stay green.
