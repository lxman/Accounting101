# Banking Area UI — Design Spec

**Date:** 2026-07-05
**Status:** Draft for review
**Scope:** Angular front-end for the existing **Cash** and **Bank Reconciliation** backend modules, delivered as one unified "Banking" area. Backends are complete and tested; this spec covers UI only.

---

## 1. Goal

Give the Cash and Bank Reconciliation modules a first-class UI so the `/cash` nav area stops routing to the placeholder. The area covers the full reconciliation loop — get a statement in (manual **or** file import), reconcile ledger cash entries against it, book bank-only adjustments, and complete — plus standalone cash disbursement/deposit vouchers.

Non-goals: no backend changes; no new domain logic. If a backend gap is discovered mid-build (e.g. a missing read field), it is flagged, not silently worked around.

## 2. Background — what the backends expose

### 2.1 Cash module (`/clients/{clientId}` routes)

- `POST /cash-disbursements` — `RecordCashDisbursementRequest(Lines[], Date, Reference?, Memo?)`. Clerk supplies the non-cash lines; the module derives the balancing **Cash credit**. Id/Number/Status server-assigned.
- `POST /cash-disbursements/{id}/void` — `VoidReasonRequest(Reason?)`.
- `GET /cash-disbursements/{id}` · `GET /cash-disbursements` (list).
- `POST /cash-deposits` — `RecordCashDepositRequest(Lines[], Date, Reference?, Memo?)`. Module derives the balancing **Cash debit**.
- `POST /cash-deposits/{id}/void` · `GET /cash-deposits/{id}` · `GET /cash-deposits`.
- Line shape: `CashLineRequest(Guid AccountId, decimal Amount)`.
- Read models: `CashDisbursementView(CashDisbursement)`, `CashDepositView(CashDeposit)`; status enum `Posted | Void` (already `[JsonStringEnumConverter]`).
- Posting accounts (the Cash account) come from a configured `CashPostingAccounts` provider — dev needs `Cash__Accounts__*` env wiring.

### 2.2 Bank Reconciliation module (`/clients/{clientId}` routes)

**Statements**
- `POST /bank-statements` — `RecordBankStatementRequest(CashAccountId, StatementDate, OpeningBalance, ClosingBalance, Lines[])`; line `BankStatementLineRequest(Date, Amount, Description, ExternalRef?)`. Must **foot** (`opening + Σlines == closing`) or `422`.
- `GET /bank-statements/{id}` · `GET /bank-statements?cashAccountId=…` (list; **cashAccountId required**, paged, `order=asc|desc`).
- `POST /bank-statements/import` (multipart: `file`, `format`, `mapping?`) — parses to a **preview**, creates nothing. Returns `ImportPreviewResponse(Statements[], Warnings[])`, each `StatementPreview(Lines[], DetectedOpeningBalance?, DetectedClosingBalance?, StatementDate?, AccountHint?)`. Client reviews, fills gaps, then POSTs to `/bank-statements`. CSV requires a `mapping` (JSON `CsvMapping`); OFX 1.x supported; OFX 2.x XML → `422 NotSupported`.
- Statement read model: `BankStatement(Id, Number "BST-#####", CashAccountId, StatementDate, OpeningBalance, ClosingBalance, Lines[], Status Posted|Void)`; line amount signed from the bank's perspective (+ in, − out).

**Reconciliation**
- `POST /reconciliations` — `StartReconciliationRequest(BankStatementId)` → `Reconciliation(Id, Number "REC-#####", CashAccountId, BankStatementId, StatementDate, Status InProgress|Completed, ClearedEntryIds[])`.
- `GET /reconciliations/{id}` → **`ReconciliationWorksheet`**:
  - `Reconciliation`, `BankStatement`,
  - `Entries: WorksheetEntry(EntryId, Date, Reference?, SourceType?, CashEffect, Cleared)[]` — ledger cash-account entries through the statement date, each with a cleared flag,
  - `BookBalance`, `ClearedTotal`, `ReconciledDifference`, `Balanced`.
- `POST /reconciliations/{id}/clear` · `/unclear` — `ClearRequest(EntryIds[])` → updated worksheet. (`422` bad id, `409` completed.)
- `POST /reconciliations/{id}/auto-match?apply=false` → **`AutoMatchProposal`** (read-only): `Matches[]`, `UnmatchedLines: UnmatchedLine(StatementLineIndex, Date, Amount, Description)[]`, `UnmatchedEntries: MatchableEntry(EntryId, Date, CashEffect)[]`, `MatchedEntryIds[]`. `?apply=true` → clears the matched ids and returns the worksheet.
- `POST /reconciliations/{id}/complete` → `409` unless `Balanced`.

**Adjustments** (bank-only entries booked during reconciliation)
- `POST /reconciliations/{id}/adjustments` — `RecordAdjustmentRequest(OffsetAccountId, Amount, Kind, Date?, Memo?)`; `AdjustmentKind` = `Charge` (bank fee, reduces cash) | `Credit` (interest, increases cash). Posts a `PendingApproval` GL entry. `422` (amount≤0, offset==cash), `409` (rec not found/completed), engine 4xx relayed.
- `GET /reconciliations/{id}/adjustments` (list, paged) · `GET …/adjustments/{adjId}` · `POST …/adjustments/{adjId}/void`.
- Read model: `BankAdjustment(Id, Number "ADJ-#####", ReconciliationId, CashAccountId, OffsetAccountId, Kind, Amount, Date, Memo?, Status Posted|Void)`.

## 3. Architecture

### 3.1 Area shape & routing

A tabbed shell at `/cash`, mirroring the Receivables/Payables/Payroll/FixedAssets shell pattern (tab nav + `<router-outlet>`, `data-testid="tab-*"`, `routerLinkActive` underline).

**Three tabs:** `Cash` · `Statements` · `Reconcile`

```
/cash                          → BankingShell
  ''            → redirect to  cash
  cash                         → CashList (disbursements + deposits)
  cash/disbursements/new       → DisbursementEditor      (canWrite)
  cash/deposits/new            → DepositEditor           (canWrite)
  cash/:id                     → CashVoucherDetail        (disbursement or deposit)
  statements                   → StatementList
  statements/new               → StatementEditor (manual) (canWrite)
  statements/import            → StatementImport          (canWrite)
  statements/:id               → StatementDetail
  reconciliation               → ReconciliationList (+ start)
  reconciliation/:id           → ReconciliationWorksheet  (clear/unclear/auto-match/adjust/complete)
```

- Add these paths to the `built` array in `app.routes.ts` so they leave the placeholder fallback.
- Keep the existing sidebar entry ("Cash & Banking" → `/cash`, child "Bank Reconciliation" → `/cash/reconciliation`). The child deep-links into the Reconcile tab. Add a "Statements" affordance if it reads better during build (nav tweak, not a route change).
- Write actions gated by `canWrite` + `data.requiredCapability`. The exact capability key(s) (`cash.write` / `bankrec.write` vs a shared `banking.write`) are verified against the server-enforcement vocabulary during planning; fallbacks point back to the owning list.

### 3.2 Core layer — `core/banking`

- `banking.service.ts` — one injectable service wrapping all Cash + Reconciliation endpoints (mirrors `core/fixed-assets/fixed-assets.service.ts`: `httpResource`/`HttpClient` + client-id from the active-client signal, `load()`/`done` idioms).
- `banking.ts` — TypeScript models for every contract in §2. Enums are string unions matching the C# `[JsonStringEnumConverter]` output (`'Posted' | 'Void'`, `'Charge' | 'Credit'`, `'InProgress' | 'Completed'`). A serialization-key guard test protects against the digit/PascalCase→camelCase trap that bit the AR aging work.
- `banking.service.spec.ts` — service-level tests (URL shape, param passing, unwrap of wrapped responses).

### 3.3 Component conventions

OnPush + signals; `svc.load()` then a `done` observer; Spartan (`@spartan-ng/helm/*`) imports; whole-row-click lists (cursor/hover/tabindex/click+enter → `router.navigate`); `hlm-select` with `*hlmSelectPortal` + `[itemToString]` where value≠label. One `.spec.ts` per component (Vitest), authored RED→GREEN.

## 4. Slice breakdown

Each slice is an independent, shippable increment reviewed via subagent-driven development (per-task review + whole-branch review), like FA-1..FA-4.

### BK-1 — Core + Cash tab
- `core/banking` service + models + shell + tab/route wiring + `built`-array + nav.
- **Cash tab:** `CashList` (combined disbursements + deposits, or two sub-lists — decided in the plan), `DisbursementEditor`, `DepositEditor` (each: date, reference, memo, N non-cash lines `{account, amount}`; the balancing cash line is server-derived and shown read-only in a running total), `CashVoucherDetail` with **void**.
- Establishes the scaffolding every later slice builds on.

### BK-2 — Statements (manual)
- `StatementList` (requires a selected cash account; paged; whole-row click). Cash-account selector at the top.
- `StatementEditor` (manual entry): cash account, statement date, opening/closing balance, lines `{date, amount, description, externalRef?}`; a live **foot check** (opening + Σlines vs closing) with inline validation before submit; `422` surfaced.
- `StatementDetail`: header + lines, status, "Start reconciliation" action.

### BK-3 — Statements (import)
- `StatementImport`: file picker, **format** selector (CSV / OFX), and for CSV a **column-mapping builder** producing the `CsvMapping` JSON (has-header toggle, column→field assignment, amount handling). Upload → render `ImportPreviewResponse` (one or more `StatementPreview` cards + warnings) → let the user fill missing balances/date/account → confirm each preview by POSTing to `/bank-statements`. This is the one genuinely novel screen (no prior analog); its mapping-builder gets focused tests.

### BK-4 — Reconciliation worksheet
- `ReconciliationList`: in-progress + completed reconciliations; "Start reconciliation" (pick a posted statement) → `POST /reconciliations` → navigate to worksheet.
- `ReconciliationWorksheet`: the entry grid (date, reference, source type, cash effect, cleared checkbox) with **clear/unclear** (single + bulk via `ClearRequest`); an **Auto-match** action that first shows the read-only **proposal** (matched pairs, unmatched statement lines, unmatched entries) then **Apply**; a running summary panel (book balance, cleared total, **reconciled difference**, **Balanced** badge); **Complete** button enabled only when balanced (409 otherwise, surfaced).

### BK-5 — Adjustments
- Within the worksheet: an **adjustments** panel/section — record (`Charge` fee / `Credit` interest, offset account, amount, date, memo), list, and **void**. Recording posts a PendingApproval GL entry (relay engine 4xx). Adjustments move the reconciled difference toward zero.

## 5. Data flow

1. **Cash voucher:** editor collects non-cash lines → `POST` → module derives cash line + posts → list/detail reflect `Posted`; void → `Void`.
2. **Statement in:** manual editor (foot-checked) **or** import (upload → preview → confirm) → `POST /bank-statements` → `BST-#####`.
3. **Reconcile:** start off a statement → worksheet → clear/unclear or auto-match → record adjustments as needed → difference reaches 0 → **Complete**.

## 6. Error handling

- Foot failure (`422`), unbalanced complete (`409`), adjustment guards (`422`/`409`), engine relays (closed period, unknown account) → surfaced inline via the existing `ReasonFrom`/problem-details flattening pattern, never swallowed.
- `busy` signal cleared in **both** `next` and `error` observers on every mutate/reload (the known "stuck busy" trap).
- Import warnings shown non-fatally; OFX 2.x XML `422` shown as "not yet supported."

## 7. Testing

- One `.spec.ts` per component + `banking.service.spec.ts`, authored RED→GREEN (Vitest via `npm test`).
- Serialization-key guard test for the models (camelCase round-trip).
- Focused tests on the two novel behaviors: the CSV **mapping-builder** output and the auto-match **proposal→apply** two-step.
- Whole-branch build clean; Angular suite green.

## 8. Dev-stack wiring

- Add `Cash__Accounts__*` posting-account env vars to `.localdev/start.ps1` (mapped to seeded Demo Co chart GUIDs), same as the FixedAssets block. Bank statements/reconciliation take the cash account as a user selection (no config).
- `cash` and `reconciliation` modules are already registered and already enabled for Demo Co (enabled 2026-07-05 alongside `fixedassets`).

## 9. Known watch-outs

- **Auto-match two-step:** the proposal (`apply=false`) and apply (`apply=true`) are distinct calls; the UI must not clear until the user confirms Apply.
- **CSV mapping builder:** the only screen without a codebase analog — keep the `CsvMapping` shape authoritative (read the interchange contract, don't guess).
- **Reserved-word method names:** FA-4 hit NG5002 with `void()`; use `voidDisbursement()`/`voidDeposit()`/`voidAdjustment()`.
- **Statement list requires `cashAccountId`:** the list is per-account; the selector is mandatory, not optional.

## 10. Deliverable order

BK-1 → BK-2 → BK-3 → BK-4 → BK-5, each merged behind a per-slice review, then a whole-area review. Smoke-test in the dev stack before the final merge (standing convention).
