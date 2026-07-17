# Fiscal Settings screen (`/admin/fiscal`) — design

**Date:** 2026-07-17
**Status:** Approved (design)
**Area:** Admin
**Capability:** `admin.fiscal`

## Goal

Replace the placeholder at the `/admin/fiscal` nav leaf ("Fiscal settings") with a
real screen that loads and saves the current client's fiscal-year-end month. The
screen mirrors the existing **Approval Policy** screen's shape: load-on-init →
edit → save, gated by a single per-client admin capability.

The write endpoint (`PUT /admin/clients/{clientId}/fiscal-year-end`, gated by
`admin.fiscal`) and its capability already exist and are tested. This work adds a
symmetric read endpoint plus the frontend screen.

## Scope

**In scope:** the fiscal-year-end month (1–12) only — a single selector, load and
save.

**Out of scope (confirmed):** posting accounts and approval policy keep their own
separate nav leaves; no display / number-formatting knobs live here.

## Backend

### New read endpoint

`GET /admin/clients/{clientId:guid}/fiscal-year-end`, registered next to the
existing `PUT` in `AdminEndpoints.cs` (the `perClient` group with
`RequireAuthorization()`).

- In-handler gate: `AdminAuthorization.MayAsync(user, clientId,
  Capabilities.AdminFiscal, actorFactory, control, ct)` → `Results.Forbid()` (403)
  if not held. Same gate as the `PUT`.
- Load `ClientRegistration` via `control.GetClientAsync`; `Results.NotFound()`
  (404) if missing.
- Otherwise return the month via `FiscalYear.MonthOf(registration)` so legacy `0`
  values normalize to December — identical normalization to the `/admin/clients`
  list and `/periods/status` read paths.

### New response contract

`FiscalYearEndResponse(int FiscalYearEndMonth)` in `AdminContracts.cs`. A focused
record mirroring the `ApprovalPolicyResponse(ApprovalMode Mode)` idiom; the
existing `ClientRegistrationResponse` carries extra fields the screen does not
need.

The `PUT` handler is **unchanged** — it keeps returning `ClientRegistrationResponse`,
so no existing endpoint test is touched.

## Frontend

### Model — `core/fiscal/fiscal.ts`

```ts
export interface FiscalSettings { fiscalYearEndMonth: number; }
```

### Service — `core/fiscal/fiscal.service.ts`

`root`-provided; injects `HttpClient` + `ClientContextService`. Copied straight
from `ApprovalPolicyService`:

- base: `${environment.apiBaseUrl}/admin/clients/${this.client.clientId()}/fiscal-year-end`
- `get(): Observable<FiscalSettings>` — guards on `clientId()`, returns `EMPTY` when absent.
- `set(month: number): Observable<FiscalSettings>` — `PUT` with body
  `{ fiscalYearEndMonth: month }`, same guard.

### Component — `features/admin/fiscal-settings.ts`

`app-fiscal-settings`, standalone, `OnPush`. Structure copied from
`approval-policy.ts`:

- Heading "Fiscal settings" + a short explainer line.
- A **native `<select>`** listing the 12 months, `value` 1–12 (native select over
  Spartan `hlm-select` to sidestep the portal / `itemToString` gotchas for a
  trivial 12-item list; radios like Approval Policy would be too many at 12).
- A forward-only note under the control (static copy): changing this affects future
  closes only; already-closed years are immutable.
- A **Save** button wrapped in `*appCan="'admin.fiscal'"`.
- Three signals — `selected: number | null`, `error: string | null`,
  `saved: boolean` — same pattern as `approval-policy.ts`. Load in the constructor;
  on save error surface `e?.error?.detail`.

### Route — `app.routes.ts`

```ts
{ path: 'admin/fiscal', component: FiscalSettings, canActivate: [canWrite],
  data: { requiredCapability: 'admin.fiscal', fallback: '/admin/users' } }
```

Same guard shape as the `admin/approval-policy` route. Add `/admin/fiscal` to the
`built` array so it stops falling through to `Placeholder`.

## Testing

### Backend — `AdminCapabilityTests.cs`

Add a GET trio mirroring the existing PUT trio:

- Member with `admin.fiscal` → `200` and the correct month.
- Member with only `gl.read` → `403`.
- Deployment admin → `200`.

### Frontend — `fiscal-settings.spec.ts`

Mirroring the `approval-policy` specs:

- Loads and shows the current month.
- Save calls the service with the selected month and shows "Saved."
- Save button hidden without `admin.fiscal`.

### Dev-stack smoke (non-optional before "done")

Per the self-consistent-mock lesson, run the real dev-stack smoke so actual wire
serialization (camelCase `fiscalYearEndMonth`, number not string) is exercised —
the spec mocks alone won't catch a casing/number-vs-string mismatch.

## Files touched

**Backend**
- `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` — new GET handler + registration.
- `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` — `FiscalYearEndResponse`.
- `Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs` — GET trio.

**Frontend**
- `UI/Angular/src/app/core/fiscal/fiscal.ts` — model (new).
- `UI/Angular/src/app/core/fiscal/fiscal.service.ts` — service (new).
- `UI/Angular/src/app/features/admin/fiscal-settings.ts` — component (new).
- `UI/Angular/src/app/features/admin/fiscal-settings.spec.ts` — specs (new).
- `UI/Angular/src/app/app.routes.ts` — route + `built` array entry.
