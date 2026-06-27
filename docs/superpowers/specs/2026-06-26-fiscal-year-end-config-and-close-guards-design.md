# Per-Client Fiscal Year-End + Close Guards — Design

**Date:** 2026-06-26
**Status:** Spec for review

## Why

The 24-month dog-food surfaced that the monthly-close-vs-year-end-close distinction was **tribal runbook knowledge**: at a fiscal year-end you must run `close-year` (which posts the closing entry on the open period and closes it), not a monthly `close`. Get the order wrong and you get an opaque downstream failure. The product should **know each client's fiscal year-end and enforce the right close** — surfaced inline with the fix path, the same way the closed-period and pending-entry guards already work.

This slice: make the fiscal year-end a **per-client configuration**, and add two guards so the engine steers the correct close. (Changing a FY-end after the fact — with its transition/stub-period and effective-dating concerns — is a deliberate **follow-up slice**, not this one. See Out of scope.)

## Where this lives — policy, not engine invariant

Fiscal-year-end is a per-client **policy**, exactly like `RequireSegregationOfDuties`. It therefore lives in `ClientRegistration` (the control DB) and is enforced in the **API/host layer** (the endpoint handlers), read via `ControlStore.GetClientAsync(clientId)` — the identical path `PostEntry` already uses to enforce SoD (`LedgerEndpoints.cs:181`). The **engine (`LedgerService`) stays policy-light** — it keeps enforcing only irreducible invariants (balance, freeze, concurrency). No engine change.

## The config

`ClientRegistration` (`Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs`) gains:
```csharp
/// <summary>The month (1-12) the client's fiscal year ends; the fiscal year ends on the LAST day of
/// that month. 12 (December / calendar year) by default. A per-client policy, like SoD.</summary>
public int FiscalYearEndMonth { get; set; } = 12;
```
- **Why a month, not a full date:** a fiscal year ends on the last day of a month; the month captures it and generalizes to any non-calendar fiscal year (June-FY → month 6 → June 30). 52/53-week retail calendars that end mid-month are out of scope (YAGNI).
- Set at client creation: `CreateClientRequest` (`Backend/Accounting101.Ledger.Contracts/AdminContracts.cs`) gains `int FiscalYearEndMonth = 12` (optional; defaults to 12 so existing callers are unaffected). `AdminEndpoints.CreateClient` copies it onto the registration and **validates 1–12** (reject out-of-range with 400). Existing registrations with no stored value deserialize to 0 → treat `0` as the default 12 at read time (a tiny normalization), or backfill is unnecessary since the field defaults to 12 on the model.
- Surfaced on read: `ClientRegistrationResponse` gains `int FiscalYearEndMonth`; the admin list/get + create response include it.

### The fiscal-year-end helper
A small shared static (e.g. `FiscalYear.EndDateFor(int fiscalYearEndMonth, int year)`), used by both guards:
```csharp
// Last calendar day of the fiscal-year-end month in the given year.
public static DateOnly EndDateFor(int fiscalYearEndMonth, int year) =>
    new(year, fiscalYearEndMonth, DateTime.DaysInMonth(year, fiscalYearEndMonth));
```
Lives in the Api `Control` namespace (host-layer policy helper).

## Guard 1 — monthly close refuses the fiscal year-end (hard)

`ClosePeriod` (`LedgerEndpoints.cs:280`) — inject `ControlStore control`. After resolving the context (Close permission), before calling `CloseAsync`:
```
client = control.GetClientAsync(clientId)
fye = FiscalYear.EndDateFor(client.FiscalYearEndMonth (or 12 if 0), request.AsOf.Year)
if request.AsOf == fye:
    return 409 Conflict, detail:
      "{AsOf:yyyy-MM-dd} is this client's fiscal year-end. Run the year-end close
       (POST /clients/{clientId}/periods/close-year) instead of a monthly close."
```
Every other `AsOf` proceeds to the normal monthly close unchanged. A `409` matches the existing close-family error shape (closed-period, pending-blockers). Include a machine-readable hint extension (e.g. `["useEndpoint"] = "periods/close-year"`, `["fiscalYearEnd"] = fye`) so a UI can offer the right action.

## Guard 2 — close-year refuses a non-fiscal-year-end date (symmetric)

`CloseYear` (`LedgerEndpoints.cs:669`) — inject `ControlStore control`. After resolving the context, before `CloseYearAsync`:
```
client = control.GetClientAsync(clientId)
fye = FiscalYear.EndDateFor(client.FiscalYearEndMonth (or 12), request.FiscalYearEnd.Year)
if request.FiscalYearEnd != fye:
    return 409 Conflict, detail:
      "{FiscalYearEnd:yyyy-MM-dd} is not this client's fiscal year-end ({fye:yyyy-MM-dd}).
       close-year closes the fiscal year; use the monthly close for an ordinary period."
```
Then proceed to `CloseYearAsync` as today (its existing 409s for pending-blockers / already-closed / no-RE-account are unchanged). Together the two guards make the endpoints un-confusable: ordinary month-ends → monthly close; the fiscal year-end → year-end close — enforced by the product, not a runbook.

## Data flow
- Create client → `FiscalYearEndMonth` stored on `ClientRegistration` (control DB).
- Close request → handler reads the registration (control DB) → computes the FY-end for the request's year → guards.
- No engine change; no new stored ledger state; balances/statements still wholly derived by replay.

## Error handling
- Out-of-range `FiscalYearEndMonth` at create → `400`.
- Monthly close on the FY-end date → `409` + guidance + hint extensions.
- close-year on a non-FY-end date → `409` + guidance.
- All other behavior (normal monthly close, normal year-end close, pending-blocker 409s) unchanged.

## Testing
- **Config:** `CreateClient` with `FiscalYearEndMonth = 6` stores + returns 6; omitted → defaults to 12; `0`/`13` → `400`.
- **Guard 1 (default Dec client):** monthly close on `2024-11-30` succeeds; on `2024-12-31` → `409` naming close-year; the closing still works via `close-year` on `2024-12-31`.
- **Guard 1 (June-FY client):** monthly close on `2024-06-30` → `409`; on `2024-12-31` succeeds (Dec 31 is an ordinary month-end for them).
- **Guard 2 (default Dec client):** `close-year` on `2024-12-31` succeeds; on `2024-06-30` → `409` naming the real FY-end.
- **Guard 2 (June-FY client):** `close-year` on `2024-06-30` succeeds; on `2024-12-31` → `409`.
- **Engine untouched:** existing close/close-year/SoD tests stay green.

## Out of scope (deliberate follow-ups)
- **Changing a client's fiscal year-end after creation.** This is its own slice — it requires the FY-end stored as an **effective-dated history** (so historical fiscal-year *reporting* across the change derives the correct range), a **forward-only** guard (a new FY-end can't precede the client's `closed-through`), and the transition **stub period** (handled naturally by the next `close-year`, which never assumes 12 months). The scalar `FiscalYearEndMonth` added here is exactly what that slice will evolve into a dated history — nothing is thrown away. Our journal-as-only-state model makes that change cheap (no period buckets / materialized rolls to migrate; the historical `Closing` entries are immutable journal facts).
- **Auto-deriving close-year's date from config** (dropping the request param). Keeping the explicit param + the symmetric guard is safer and clearer.
- UI affordances (the hint extensions are provided for a future UI to consume).

## Global constraints
- .NET 10; build 0 warnings; TDD.
- Engine (`LedgerService`) stays policy-light — the guard is host-layer, mirroring the SoD enforcement path (`ControlStore.GetClientAsync`).
- Backward-compatible: `FiscalYearEndMonth` defaults to 12, so existing clients and callers behave exactly as before (a Dec-FY client's Dec-31 monthly close now correctly routes to close-year — the intended behavior change, and the only one).
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
