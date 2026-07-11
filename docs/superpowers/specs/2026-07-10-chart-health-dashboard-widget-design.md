# Chart-Health Dashboard Widget — Design

*2026-07-10. Frontend slice that surfaces the advisory chart-readiness endpoints (shipped `9857775`) in the UI, plus one essential backend enum-serialization fix and a small account-editor extension so flagged gaps are one-click fixable.*

## Goal

Give onboarding/admin a visible, at-a-glance answer to "is this client's chart set up for each module?" — and turn every gap into a direct fix. The 6 readiness endpoints (`GET /clients/{id}/{key}/chart-readiness`, modules `receivables/payables/payroll/cash/fixedassets/inventory`) already exist and return a `ChartReadinessReport { moduleKey, ready, accounts[] }`; nothing in the UI consumes them yet. This is the last mile of the fold-on-read "prevent" thread.

## Decisions (settled during brainstorming)

1. **Placement:** a **dashboard widget** (high visibility), not an admin screen or a Chart-of-Accounts tab.
2. **Drill-in:** the widget holds the per-module overview and per-gap rows inline; each gap **deep-links straight to Chart of Accounts** — there is no intermediate `/chart-health` detail route.
3. **Fix fidelity:** a `Missing` gap **prefills the New-Account form**; other statuses deep-link to the account's edit page.
4. **Dimensions:** the account editor is **extended to set required dimensions** so `MissingDimensions` gaps are truly fixable (backend already accepts `RequiredDimensions`).
5. **Module scope:** **all 6 modules, always** (onboarding framing; matches the endpoints' un-gated advisory stance; no new backend endpoint, no dependency on client `EnabledModules`).

## Architecture

A frontend dashboard slice consuming the 6 existing readiness endpoints, plus:
- a **one-line backend enum-serialization fix** (essential — see below), and
- a small **account-editor extension** (required-dimensions field + query-param prefill).

No engine changes. No new backend endpoints.

**Data flow:** widget → `ChartHealthService.readiness()` fans out `GET /clients/{id}/{key}/chart-readiness` across all 6 module hosts → combined, module-labeled report → per-module rows → each gap deep-links into Chart of Accounts to fix it.

## Global constraints

- **Angular:** standalone components, `ChangeDetectionStrategy.OnPush`, signal-based state, matching the existing `core/<module>/…service.ts` + `features/<area>/…` conventions.
- **Service pattern:** inject `HttpClient` + `ClientContextService`; build URLs off `environment.apiBaseUrl/clients/{clientId}`; guard on `clientId()` like the sibling module services.
- **No new libraries.** Comma-separated input for dimensions (v1); chips are out of scope.
- **Enum-as-string convention:** every domain enum in this repo carries `[JsonConverter(typeof(JsonStringEnumConverter))]` per-type (there is no global converter). New/consumed enums must follow it.

## Component 1 — Backend enum fix (essential, 1 line + 1 test)

`Modules/Shared/Accounting101.ModuleKit/AccountRequirement.cs`

- Add `[JsonConverter(typeof(JsonStringEnumConverter))]` to `AccountReadinessStatus`. Without it, `status` serializes as a **number** (0–4) and the widget cannot reliably branch on it. The existing E2E tests deserialize the report back into the C# enum, so numbers round-trip green — a latent wire-format bug that only a UI consumer or smoke test surfaces (`[[accounting101-ui-mock-casing-trap]]`).
- Add a serialization unit test pinning `AccountReadinessStatus.Missing → "Missing"` in JSON so this cannot silently regress.

## Component 2 — Data layer (`core/chart-health/`)

`chart-health.ts`
- TS mirrors of the wire contract:
  - `AccountReadinessStatus = 'Ok'|'Missing'|'Inactive'|'WrongType'|'MissingDimensions'`
  - `AccountReadinessResult { accountId, label, expectedType, requiredDimensions, status, actualType, actualRequiredDimensions, detail }`
  - `ChartReadinessReport { moduleKey, ready, accounts }`
- A `MODULES` constant: the 6 `{ key, label }` pairs (e.g. `fixedassets → "Fixed Assets"`).
- A view type carrying an `errored` marker for a module whose host call failed.

`chart-health.service.ts`
- Injects `HttpClient` + `ClientContextService`.
- `readiness(): Observable<ChartHealthView[]>` — `forkJoin` the 6 `GET .../{key}/chart-readiness` calls; each call `catchError` → an `errored` view (never blank the whole widget on one host failing). Returns reports tagged with key + display label, module order stable.
- Guard: if no `clientId()`, return `EMPTY` (sibling-service convention).

## Component 3 — Dashboard widget (`features/dashboard/chart-health-widget.ts`, OnPush)

- **Header:** `Chart Health · X / 6 ready` (X = count of `ready === true`).
- **Module rows:** ready → `✓`, collapsed; not-ready → `N gap(s)`, expandable; errored → a muted "couldn't check" row.
- **Gap rows** (inside an expanded not-ready module): account `label` + status badge + backend `detail` string + a fix link:
  - `Missing` → `/accounts/new?id={accountId}&type={expectedType}&name={label}&dims={requiredDimensions joined by ','}`
  - `Inactive` / `WrongType` / `MissingDimensions` → `/accounts/{accountId}/edit`
- Rendered as a card in `features/dashboard/dashboard.ts`.

**Correctness note — why `Missing` prefills the expected id.** A `Missing` account is not "any account of the right type" — the module's posting config points at a *specific* `accountId`. Account upsert is **PUT-by-id**, so a client-chosen id is honored. The New-Account deep-link therefore prefills that **expected `accountId`**; otherwise the user creates a correct-looking account with a fresh GUID and the module still reports Missing. This is what makes the fix actually close the gap.

## Component 4 — Account editor extension (`features/accounts/account-editor.ts`)

- Add a **required-dimensions** field: a comma-separated text input ↔ `string[]` (trim, drop empties). Wire `requiredDimensions` through:
  - `EditorValue`,
  - the `AccountUpsert` / `AccountResponse` TS interfaces (`core/accounts/account.ts`),
  - `fromAccount()` — so editing an existing account **preserves** its dimensions,
  - the `upsert` request body in `accounts.service.ts` (send `requiredDimensions`; empty array clears — backend: `RequiredDimensions` wins over legacy singular when present).
- Constructor reads new-account **prefill** query params: `id`, `type`, `name`, `dims`. When `id` is present, it overrides the generated `newId()` so the created account carries the module's expected id.

## Testing

- **Backend:** the `AccountReadinessStatus` string-serialization test (Component 1).
- **Unit specs:**
  - service — 6-call fan-out; one failing host → that module `errored`, others intact.
  - widget — summary count; ready vs not-ready vs errored rows; gap rows; correct deep-link hrefs per status (this pins the wire-format the UI depends on).
  - editor — dimensions round-trip: prefill from query params (`id/type/name/dims`), load-existing preserves dims, save sends `requiredDimensions`; `id` query param overrides `newId()`.
- **Dev-stack smoke test — NON-OPTIONAL before merge** (`[[accounting101-ui-mock-casing-trap]]`): against the seeded demo client, confirm `status` renders as **text not a number**, a `Missing` gap deep-links to a prefilled New-Account form, and saving it flips that module to `ready`. This is the only layer that exercises real cross-host serialization.

## Out of scope / follow-ups

- Filtering the widget to the client's actual `EnabledModules` (would need a new GET exposing entitlement to the UI). Deferred by decision — all 6 shown.
- Chips/tags UI for dimensions (comma-separated is v1).
- An advisory banner on the edit page echoing the expected type/dims for `Inactive/WrongType/MissingDimensions` (nice-to-have; the widget's `detail` text already states the fix).
- A standalone `/chart-health` detail route or admin screen (rejected in favor of the dashboard widget + COA deep-link).

## Execution

**REQUIRED SUB-SKILL:** Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans`. Fresh implementer per task, task review after each, final whole-branch review (opus). Sonnet throughout (mechanical; plan carries complete code).
