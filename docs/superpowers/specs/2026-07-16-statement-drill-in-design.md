# Statement & Credit-Activity Drill-In (Slice 2c-3b) — Design

**Status:** Approved for planning
**Date:** 2026-07-16
**Slice:** 2c-3b — the final slice of the "drill-down-where-a-row-is-an-entity" arc ([[accounting101-drilldown-slices]]).

## Goal

Make the **statement-of-account** and **credit-activity** rows on the Customer 360 (AR) and Vendor 360 (AP) screens drill into the underlying document's detail screen on whole-row click/Enter — the same way every list row in the app now does. This completes the arc: every drill target already exists (2a–2c-3a built them all), and each of these rows corresponds to exactly one real document that already has a detail screen.

## Background & Current State

The two account-view read models each expose a statement and a credit-activity list:

- **AR** — `Modules/Receivables/Accounting101.Receivables/CustomerAccountBuilder.cs` builds `CustomerAccountView` (`.../CustomerAccountView.cs`).
- **AP** — `Modules/Payables/Accounting101.Payables/VendorAccountBuilder.cs` builds `VendorAccountView` (`.../VendorAccountView.cs`).

The `StatementLine` and `CreditActivityLine` records are **structurally identical across AR and AP** (distinct record types in distinct namespaces, same fields):

```csharp
public sealed record StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance);
public sealed record CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance);
```

**The blocker is purely surfacing, not data.** At every row-construction site the source document's `.Id` (a `Guid`) and its kind are in scope — the builder iterates real documents — but they are dropped before the record is emitted:

- `StatementLine`: has **no id field at all**; the source id is used only inline as a relief-dictionary key and never captured.
- `CreditActivityLine`: the builder's intermediate `raw` tuple **already carries `Guid Id`** (it is used for a `ThenBy(r => r.Id)` sort tiebreak) but drops it at the final `Select(...)` projection.

The human `Type` string **cannot** serve as the routing key:
- `"Overpayment"` (credit-activity) is derived from a **Payment**/**BillPayment** document — it must route to the payment detail, not any "overpayment" entity.
- `"Credit applied"` appears in **both** the statement (AR-relief side of a `CreditApplication`) and the credit-activity list (credit-ledger debit side of the same `CreditApplication`) — same label, and the AR statement route needs a distinct `credits/:type/:id` slug anyway.

Therefore a separate **machine `Kind` slug** is required alongside the unchanged display `Type`.

Both FE screens (`UI/Angular/src/app/features/receivables/customer-account.ts`, `.../features/payables/vendor-account.ts`) currently render both tables with plain `<table>` markup, `@for (... ; track $index)`, and **no row interactivity**. The FE interfaces (`core/receivables/receivables.ts`, `core/payables/payables.ts`) mirror the backend records exactly (no id/kind).

## Design

### 1. Backend — enrich the four line records with `Id` + `Kind`

Add two fields to each record (AR and AP), appended so the addition is purely additive on the wire:

```csharp
public sealed record StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance, Guid Id, string Kind);
public sealed record CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance, Guid Id, string Kind);
```

`Id` = the source document's `Guid`. `Kind` = a **route-aligned lowercase slug** set at each construction site from the in-scope loop variable. The display `Type` string is **unchanged** — no UI label changes.

**`Kind` vocabulary — the complete, exhaustive mapping:**

| Area | Kind slug | Source document | Emitted in | Display `Type` | FE route |
|---|---|---|---|---|---|
| AR | `invoice` | `Invoice` | Statement | `"Invoice"` | `/receivables/invoices/:id` |
| AR | `payment` | `Payment` | Statement | `"Payment"` | `/receivables/payments/:id` |
| AR | `payment` | `Payment` (unapplied remainder) | CreditActivity | `"Overpayment"` | `/receivables/payments/:id` |
| AR | `credit-note` | `CreditNote` | Statement | `"Credit note"` | `/receivables/credits/credit-note/:id` |
| AR | `write-off` | `WriteOff` | Statement | `"Write-off"` | `/receivables/credits/write-off/:id` |
| AR | `credit-application` | `CreditApplication` | Statement | `"Credit applied"` | `/receivables/credits/credit-application/:id` |
| AR | `credit-application` | `CreditApplication` | CreditActivity | `"Credit applied"` | `/receivables/credits/credit-application/:id` |
| AR | `refund` | `Refund` | CreditActivity | `"Refund"` | `/receivables/refunds/:id` |
| AP | `bill` | `Bill` | Statement | `"Bill"` | `/payables/bills/:id` |
| AP | `payment` | `BillPayment` | Statement | `"Payment"` | `/payables/payments/:id` |
| AP | `payment` | `BillPayment` (unapplied remainder) | CreditActivity | `"Overpayment"` | `/payables/payments/:id` |
| AP | `credit-application` | `VendorCreditApplication` | Statement | `"Credit applied"` | `/payables/credits/:id` |
| AP | `credit-application` | `VendorCreditApplication` | CreditActivity | `"Credit applied"` | `/payables/credits/:id` |

Notes:
- For AR credit-ledger rows the `Kind` slug **is** the `:type` segment of the existing `credits/:type/:id` route — no separate credit-type field is needed. These slugs match the existing FE `CreditType = 'credit-note' | 'write-off' | 'credit-application'` (`core/receivables/receivables.ts`).
- AP has no `CreditNote`/`WriteOff`/`Refund` document types; its `credits/:id` route is un-typed (single credit-application document kind).
- **Threading:** for `StatementLine`, add `Guid Id, string Kind` to the builder's `raw` tuple (currently absent) and thread through to the projection. For `CreditActivityLine`, the `raw` tuple already carries `Guid Id`; add `string Kind` alongside it and pass both into the final record. The date/order sort and the running balance/relief math are **unchanged**.

This is a read-only projection change: no amounts, relief math, posting picks, or GL reads are touched, so there is **no self-consistent-fold divergence risk** (unlike 2b-2). The account-view endpoints are unchanged — the new fields serialize additively (host `JsonNamingPolicy.CamelCase` → `id`, `kind`).

### 2. Frontend — whole-row drill-in on both tables, both screens

For each of `customer-account.ts` (AR) and `vendor-account.ts` (AP):

- Add `id: string; kind: string;` to the FE `StatementLine` and `CreditActivityLine` interfaces (`core/receivables/receivables.ts`, `core/payables/payables.ts`).
- Add an `open(line)` method that switches on `line.kind` to the route table above and calls `void this.router.navigate([...])`. The AR and AP switches differ, so each component owns its own `open()` inline (mirroring how each list component owns its own `open()`); no shared helper.
- On **both** the statement `<tr>` and the credit-activity `<tr>`: add `role="button" tabindex="0"`, `class="cursor-pointer hover:bg-muted/50"`, `(click)="open(row)"`, `(keydown.enter)="open(row)"`.
- Change `track $index` → `track row.id` on both loops (id is now present and unique within each list).
- Keep the existing plain `<table>` markup (no migration to `hlmTable`).

**Gating:** unconditional, same-area. The account screen is only reachable by a holder of the area's read cap (`ar.read` / `ap.read`), and every drill target is in the same area, so rows are unconditionally clickable — no `*appCan` affordance gate (consistent with 2b-1/2b-2/2c-1/2c-2/2c-3a same-area list drill-ins). The cross-area `gl.read`-gated journal link pattern does not apply here (these rows drill to same-area document details, not to the GL).

### 3. Wire shapes (identical AR ↔ AP, backend record ↔ FE interface)

```
StatementLine      { date, type, reference, charge, payment, balance, id: string, kind: string }
CreditActivityLine { date, type, reference, amount, creditBalance, id: string, kind: string }
```

## Testing

**Backend (xUnit):** extend the existing `CustomerAccountBuilder` / `VendorAccountBuilder` tests to assert, for a fixture exercising each document kind, that every emitted `StatementLine` and `CreditActivityLine` carries (a) the correct source-document `Id` and (b) the correct `Kind` slug per the table above. Explicitly cover that an **Overpayment** credit-activity row carries `Kind == "payment"` and the originating payment's `Id`, and that an AR **"Credit applied"** row carries `Kind == "credit-application"`. If the builders lack direct unit tests, add focused ones (the builders are pure functions over in-memory document lists).

**Frontend (Vitest + TestBed):** extend `customer-account.spec.ts` / `vendor-account.spec.ts` with row-click navigation tests. For a mocked account view containing a representative row of each `kind`, dispatch a `click` on the row and assert `router.navigate` was called with the correct route array — including an Overpayment row → `['/receivables/payments', id]` (AR) / `['/payables/payments', id]` (AP), and each AR credit kind → `['/receivables/credits', kind, id]`. Nav spies use the Vitest idiom `vi.spyOn(router, 'navigate').mockResolvedValue(true)`.

## Task Decomposition (4 tasks)

1. **AR backend** — enrich `StatementLine`/`CreditActivityLine` in `CustomerAccountView.cs`; thread `Id` + `Kind` through both `CustomerAccountBuilder.Statement(...)` and `CreditActivity(...)`; extend builder tests.
2. **AP backend** — the same for `VendorAccountView.cs` + `VendorAccountBuilder`; extend builder tests.
3. **FE AR** — `StatementLine`/`CreditActivityLine` interface fields + `customer-account.ts` `open()` + both-table drill-in + `track row.id`; extend `customer-account.spec.ts`.
4. **FE AP** — the same for `payables.ts` interfaces + `vendor-account.ts`; extend `vendor-account.spec.ts`.

Each task is independent given the shared vocabulary in this spec. Backend tasks are pure record + pure-function changes; FE tasks are pure component + interface changes.

## Global Constraints

- **Backend:** namespaces follow folder structure. New record fields are appended (additive wire). Display `Type` strings are unchanged. No endpoint signature changes. Rider auto-converts explicit types to `var` — stage explicit file lists and check for stray churn before each commit.
- **Frontend:** standalone, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. Rows unconditionally clickable (same-area). FE test runner is **Vitest** (`vi.spyOn` global; nav spies `.mockResolvedValue(true)`).
- **Wire shapes** identical backend ↔ frontend (host camelCase). `Kind` slugs are exactly those in the vocabulary table; the AR credit slugs match the existing `CreditType` union and `credits/:type/:id` route.
- Only touch the files named per task. Do NOT touch other modules, the detail screens themselves (2a–2c-3a, done), or unrelated builders.
- `environment.ts` stays modified/uncommitted (local dev config, never commit).
- Branch `feat/statement-drill-in`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## Out of Scope / Non-Goals

- No new endpoints, caps, routes, or detail screens (all targets exist).
- No migration to `hlmTable` on the account screens.
- No changes to display labels, amounts, relief math, sort order, or the open-invoices/open-bills tables.
- No cross-area (GL) drill from these rows.
