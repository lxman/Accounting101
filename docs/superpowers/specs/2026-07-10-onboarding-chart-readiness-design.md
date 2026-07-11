# Onboarding Chart Readiness — Design

**Date:** 2026-07-10
**Status:** Approved (design)

## Problem

A module's ledger-first folds and posts depend on the client's chart being set up a
specific way: the folded control accounts must carry exact `RequiredDimensions`
(FA Accumulated Depreciation → `["Asset"]`, Inventory 1400 → `["Item"]`, A/R →
`["Customer","Invoice"]`, Customer Credits → `["Customer"]`, A/P →
`["Vendor","Bill"]`, Vendor Credits → `["Vendor"]`), and every posting account the
module uses must exist at its configured id. When the chart is wrong, the failure
surfaces only at runtime — a fold `422` (now relayed as a clean 4xx by the ModuleKit
middleware, `16687ba`) or a post that the engine refuses. Both were hit in dev
smokes: FA/Inventory folded accounts missing their dimension, and Vendor Credits
(1300) absent from a seeded chart entirely.

The runtime relay is the *reactive* backstop. This work is the *proactive*
complement: let an accountant (or a UI) ask, before posting, "is this client's chart
ready for this module?" — and get a precise, actionable answer. The dimension
requirement is currently **implicit** (hardcoded at each fold call site), so nothing
can validate it; step one is making each module *declare* its chart contract.

## Goal

Add an on-demand, advisory **chart-readiness** check per module:
`GET /clients/{id}/<module>/chart-readiness` returns a 200 report of whether the
client's chart satisfies that module's account requirements (existence, type, and —
for folded accounts — the required dimensions), naming exactly what's wrong. Surface
the fault proactively; do not gate any workflow. The runtime relay stays the
backstop for anyone who skips the check.

## Principle & non-goals

Consistent with the fold-on-read + ModuleKit work: **surface the fault, never
tolerate or auto-correct it, and never block the workflow.** The check is a pure,
read-only comparison.

- **Advisory only.** Always returns `200` with a report; an unready chart is
  `ready:false`, not a request error. It does not block onboarding, module-enable,
  posting, or reading.
- **No engine change.** The engine already exposes `GET /clients/{id}/accounts`
  (returning `Type` + `RequiredDimensions`) and the check is a pure comparison over
  data both sides already expose. Nothing under `Backend/` changes.
- **No degrade-to-0, no runtime guard** — those were rejected in the fold-on-read
  work and remain rejected.
- **UI is a separate follow-on.** A "chart health" panel that renders these reports
  across a client's enabled modules is out of scope; this ships the backend
  endpoints only.

## Scope decisions (and the reasoning)

**Per-module endpoints, brick-isolated (Approach A).** Each module owns its own chart
contract and exposes its own readiness endpoint; the engine is not taught to
enumerate module requirements. This keeps the engine boundary thin (the discipline
the whole ledger-first epic held), gives ModuleKit a clean second use, and matches
the modules-as-movable-units model the tenancy architecture relies on. The one
cost — no single "whole chart" call — is acceptable: a readiness UI (or caller)
queries the modules it has enabled and merges. (Approach B, an engine-hosted
aggregated endpoint enumerating an `IModuleChartRequirements` extension point, was
rejected for the added engine coupling. Approach C, validating inside
`POST /onboarding`, was rejected — onboarding is opening-balances and runs before the
chart is fully built.)

**Full account readiness, not dimensions-only.** The check covers every account a
module needs: it must exist at the configured id, be Active, have the expected type,
and — for folded ones — carry the required dimensions. This catches both real
failure modes seen in dev (missing dimension *and* absent account), for barely more
work since the module already resolves all its account ids via its `AccountsProvider`.

**Six modules (Reconciliation excluded).** The four with dimensioned folds
(AR/AP/FA/Inventory) carry the load-bearing checks; Cash/Payroll get existence+type
only (catching the "Cash account not configured" class). The shared checker makes
each module's addition tiny, so full coverage is cheap and gives a complete answer.
**Reconciliation is deliberately excluded:** it has no config-fixed account
contract — the bank/cash account it reconciles is carried on each statement record
at runtime and the adjustment offset account is caller-supplied per adjustment, and
it does no dimensioned folds. There is nothing static a readiness endpoint could
check; the only account it touches (the bank/cash account) is already covered by the
**Cash** module's endpoint. Adding a Reconciliation endpoint would duplicate Cash's
check with no new signal.

## Architecture

### ModuleKit additions (the shared machinery — its second reason to exist)

- **`ModuleLedgerClient.GetAccountsAsync(Guid clientId, CancellationToken ct = default)`**
  (in `Accounting101.ModuleKit.Api`) — a new read on the shared base hitting the
  engine's existing `GET clients/{clientId}/accounts`, returning
  `IReadOnlyList<AccountResponse>`. Forwards the bearer like every other read (no
  module credential). All modules inherit it.
- **`AccountRequirement`** (in domain-safe `Accounting101.ModuleKit`, refs only
  `Ledger.Contracts`) — a pure record a module declares per account:
  ```csharp
  public sealed record AccountRequirement(
      Guid AccountId,
      string Label,                         // human text, e.g. "Accumulated Depreciation"
      string? ExpectedType,                 // e.g. "Asset"; null = don't check type
      IReadOnlyList<string> RequiredDimensions); // empty = only needs to exist
  ```
- **`ChartReadinessChecker.Check(IReadOnlyList<AccountRequirement> requirements, IReadOnlyList<AccountResponse> chart, string moduleKey)`
  → `ChartReadinessReport`** (domain-safe `ModuleKit`) — a pure function. Per
  requirement it matches the account by id in `chart` and emits an
  `AccountReadinessResult` with a `status`:
  - `Missing` — no account with that id in the chart.
  - `Inactive` — found but `Active == false`.
  - `WrongType` — found, `ExpectedType` given and `Type != ExpectedType`.
  - `MissingDimensions` — found, but `RequiredDimensions` (the declared set) is not a
    subset of the account's `RequiredDimensions`.
  - `Ok` — none of the above.
  The precedence is Missing → Inactive → WrongType → MissingDimensions → Ok (report
  the most fundamental problem first). The result carries the declared expectation,
  the actual (`actualType`, `actualRequiredDimensions`), and a human `detail` string.
  `ChartReadinessReport { string ModuleKey, bool Ready, IReadOnlyList<AccountReadinessResult> Accounts }`
  with `Ready == Accounts.All(a => a.Status == Ok)`.

### Per-module additions (thin)

- **`<Module>ChartRequirements`** — a small class that builds `AccountRequirement[]`
  from the module's existing `AccountsProvider` (the account ids, resolved from
  config) plus the type + required-dimension knowledge that is currently hardcoded at
  the fold sites, now made explicit in one place. Lives in the module's `.Api` (where
  the configured provider lives).
- **Endpoint** `GET /clients/{id}/<module>/chart-readiness` in the module's `.Api`:
  ```csharp
  var reqs = requirements.For(clientId);                    // AccountRequirement[]
  var chart = await ledger.GetAccountsAsync(clientId, ct);  // AccountResponse[]
  return Results.Ok(ChartReadinessChecker.Check(reqs, chart, "<module>"));
  ```
  Requires authentication and engine **read** permission on the client's chart —
  enforced when `GetAccountsAsync` hits the engine (`GET /accounts` resolves
  `Permission.Read`); a caller without it gets the engine's relayed 403. Note: the
  endpoint reads only the chart and does **not** touch the module doc-store, so it
  does not pass through the `ModuleAccess` entitlement chokepoint — module
  entitlement is not separately enforced here (an authenticated client user who can
  read the chart can retrieve any module's readiness report; the data is advisory,
  read-only chart-config metadata). Adding strict per-module entitlement parity is a
  documented fast-follow if wanted. Always `200` on success (the report itself carries
  `ready`).

### Data flow

```
GET /clients/{id}/inventory/chart-readiness
  → InventoryChartRequirements.For(clientId)
        → [{1400,"Inventory Asset","Asset",["Item"]}, {5000,"COGS","Expense",[]}, {2100,"GRNI","Liability",[]}, {5100,"Adjustment","Expense",[]}]
  → ModuleLedgerClient.GetAccountsAsync(clientId) → engine GET /accounts → AccountResponse[]
  → ChartReadinessChecker.Check(reqs, chart, "inventory")
        → { inventory, ready:false, [ 1400: MissingDimensions("Item"), 5000: Ok, ... ] }
  → 200
```

## Module coverage

Exact config keys + types (ground-truth from each `AccountsProvider`, posting recipe,
and the fixtures that set `RequiredDimensions`):

| Module (key) | Account (config key) | Type | Required dims |
|---|---|---|---|
| **Receivables** (`receivables`) | `Receivables:Accounts:Receivable` | Asset | `["Customer","Invoice"]` |
| | `Receivables:Accounts:CustomerCredits` | Liability | `["Customer"]` |
| | `Receivables:Accounts:Revenue` | Revenue | — |
| | `Receivables:Accounts:SalesTaxPayable` | Liability | — |
| | `Receivables:Accounts:Cash` | Asset | — |
| | `Receivables:Accounts:BadDebtExpense` | Expense | — |
| | `Receivables:Accounts:SalesReturns` | Revenue | — |
| **Payables** (`payables`) | `Payables:Accounts:Payable` | Liability | `["Vendor","Bill"]` |
| | `Payables:Accounts:VendorCredits` | **Asset** (debit-normal — deliberate, not symmetric with Customer Credits) | `["Vendor"]` |
| | `Payables:Accounts:Cash` | Asset | — |
| **Fixed Assets** (`fixedassets`) | `FixedAssets:Accounts:AccumulatedDepreciation` | Asset | `["Asset"]` |
| | `FixedAssets:Accounts:DepreciationExpense` | Expense | — |
| | `FixedAssets:Accounts:AssetCost` | Asset | — |
| | `FixedAssets:Accounts:DisposalProceeds` | Asset | — |
| | `FixedAssets:Accounts:GainOnDisposal` | Revenue | — |
| | `FixedAssets:Accounts:LossOnDisposal` | Expense | — |
| **Inventory** (`inventory`) | `Inventory:Accounts:InventoryAsset` | Asset | `["Item"]` |
| | `Inventory:Accounts:Cogs` | Expense | — |
| | `Inventory:Accounts:GrniClearing` | Liability | — |
| | `Inventory:Accounts:InventoryAdjustment` | Expense | — |
| **Cash** (`cash`) | `Cash:Accounts:Cash` | Asset | — |
| **Payroll** (`payroll`) | `Payroll:Accounts:SalariesExpense` | Expense | — |
| | `Payroll:Accounts:PayrollTaxExpense` | Expense | — |
| | `Payroll:Accounts:WithholdingsPayable` | Liability | — |
| | `Payroll:Accounts:PayrollTaxesPayable` | Liability | — |
| | `Payroll:Accounts:Cash` | Asset | — |

The declared `RequiredDimensions` mirror each account's **posting contract** — the
dimensions the module's recipes stamp on that account's lines — which the chart
account must `RequiredDimensions`-require for the posts to be accepted. This is a
superset of what the *fold* reads: e.g. A/R lines are stamped with both `Customer`
and `Invoice` (`InvoicePosting`), while the balance fold reads only `Invoice`; both
must be required on the account, so the declaration lists both. Same for A/P
(`Vendor`+`Bill` stamped, `Bill` folded). **Not covered** (caller-supplied per
transaction, not a
config-fixed account): AR's `RevenueByCategory` map (per-category revenue accounts,
only the default `Revenue` is a fixed target), AP's per-bill-line expense accounts,
Reconciliation's per-adjustment offset accounts.

## Report contract (example)

```json
GET /clients/{id}/inventory/chart-readiness  →  200
{
  "moduleKey": "inventory",
  "ready": false,
  "accounts": [
    { "accountId": "…1400", "label": "Inventory Asset", "expectedType": "Asset",
      "requiredDimensions": ["Item"], "status": "MissingDimensions",
      "actualType": "Asset", "actualRequiredDimensions": [],
      "detail": "Account exists but does not require the 'Item' dimension its value fold needs." },
    { "accountId": "…5000", "label": "Cost of Goods Sold", "expectedType": "Expense",
      "requiredDimensions": [], "status": "Ok",
      "actualType": "Expense", "actualRequiredDimensions": [], "detail": "OK." }
  ]
}
```

## Error handling

- The endpoint returns `200` with the report for any reachable chart (ready or not).
- If the underlying `GetAccountsAsync` itself fails (engine auth/period/etc.), that is
  a real `LedgerClientException` and relays as a 4xx via the ModuleKit middleware —
  the same behavior as every other module read. The readiness endpoint adds no new
  error surface.

## Testing

- **ModuleKit unit tests for `ChartReadinessChecker`** (primary coverage — pure
  function): the full status truth table (Ok / Missing / Inactive / WrongType /
  MissingDimensions, incl. the subset semantics for dimensions), precedence
  (Missing wins over MissingDimensions), and report aggregation (`Ready` true iff all
  Ok). Plus a `GetAccountsAsync` client unit test pinning it targets
  `GET clients/{id}/accounts` (mirrors the existing ported `ModuleLedgerClientTests`).
- **Per-module E2E** through each existing host fixture (which already build correct
  and misconfigured charts, as the `LedgerErrorRelayE2eTests` fixtures do). For each
  of the 4 dimensioned modules: correct chart → `200 ready:true`; folded account
  missing its dimension → `ready:false` with `MissingDimensions`; a required account
  absent → `ready:false` with `Missing`. For Cash/Payroll: correct →
  ready; a posting account absent → `Missing`. (Reconciliation excluded — no static
  contract.)

## Success criteria

- Each enabled module answers `GET /clients/{id}/<module>/chart-readiness` with a
  `200` report; a misconfigured chart yields `ready:false` naming the exact account
  and the fix; a correct chart yields `ready:true`.
- The declared requirements match what each fold site actually needs (the dimension
  strings are identical to the `GetSubledgerAsync` call sites).
- Engine untouched; the runtime relay remains the backstop; whole solution green.

## Future Work (deferred)

- **Chart-health UI panel** — render the reports across a client's enabled modules
  (one card per module, red/green per account with the `detail` fix text). A natural
  next slice; not in this backend work.
- **Onboarding hint** — optionally surface a summary readiness banner right after
  `POST /onboarding` completes. Deferred; the standalone endpoints cover the need.
