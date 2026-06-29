# UI Slice — 1c Hardening + Chart of Accounts — Design

**Date:** 2026-06-29
**Status:** Approved (design); precedes the implementation plan
**Builds on:** Slices 1a/1b/1c (merged + pushed, `origin/master`). This is the first slice of screen-map area **E (Chart of Accounts)**; Cash Flow (G3), Periods (H), and the real Dashboard (C) are deferred to subsequent slices.

## Purpose

Two things in one slice:
1. **1c hardening** — the deferred polish/robustness fixes surfaced by the 1c reviews (no new design, known fixes).
2. **Chart of Accounts (E1 + E2)** — a hierarchical, balance-annotated chart view with **drag-to-reparent**, and a **Signal-Forms account editor** to create/edit accounts (including renumbering and reparenting).

## Stack & conventions

All slice-1 conventions hold verbatim: zoneless + OnPush; standalone (`standalone: true` omitted); signals + `input()`/`output()`/signal queries; `@if`/`@for`; `inject()` DI; functional interceptors. Money/dates render **only** through the formatter (`formatMoney`/`formatProfileDate`); decimal-aligned tabular numerals; accounting parens for negatives. DevToken auth; client-scoped URLs via `ClientContextService.clientId()`. Tests: Vitest + `HttpTestingController`, no live backend, pristine output. Work from `UI/Angular`. Commit trailer verbatim: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

## Domain facts that shape this design (from backend recon)

- **Account identity is the GUID `Id`; `Number` is a renumberable label** (the engine doc-comment says so). Every journal `Line.AccountId` is a Guid; balances/statements/subledgers all key off the GUID. **Renumbering an account — even wholesale — breaks nothing**, it only changes the display label and the sort position.
- **`Number` is the canonical sort order.** Trial balance, statements, and exports all order by number. There is **no separate display-order field**, and we deliberately do not add one (it would desync the screen from every report and cuts against "engine enforces only irreducible invariants"). So: **order = number**, edited via the account editor.
- **Sorting is ordinal (lexicographic), not numeric.** The engine orders accounts with `StringComparer.Ordinal` (`Statement.cs`, `CashFlowStatement.cs`) — the conventional treatment of an account *code* as a string (matches QuickBooks/Xero/Sage/NetSuite, and keeps segmented/alphanumeric codes viable). **The COA tree sorts children by `Number` using the same ordinal comparison** so the screen order matches the trial balance and statements exactly; sorting numerically would desync them. Ordinal == numeric as long as codes are uniform width (the demo chart is all 4-digit); mixed widths sort lexicographically (e.g. `"900"` after `"1000"`). Uniform-width/zero-padded codes are assumed; natural-sort or a width nudge is a future engine/UX consideration, out of scope here.
- **Chart structural invariants** (enforced by `ChartOfAccounts`, validated on every `PUT`): parents exist; **a child shares its parent's type**; **no cycles**; account **numbers are unique**; at most one retained-earnings account. An invalid edit returns **422** (`InvalidChartOfAccountsException` message).
- **Normal side and temporary/permanent are derived from `Type`, never stored** → shown read-only in the UI.
- **`Postable`** distinguishes leaves (postable) from summary/header accounts; postings land on leaves.

## Engine contracts (camelCase JSON)

- `GET /clients/{clientId}/accounts` → `AccountResponse[]` `{ id, number, name, type, parentId, postable, requiredDimension, cashFlowActivity, isRetainedEarnings, active, normalSide, isTemporary }`. (Slice-1b `AccountsService` already consumes this.)
- `PUT /clients/{clientId}/accounts/{accountId}` — body `AccountRequest` `{ number, name, type, parentId?, postable=true, requiredDimension?, cashFlowActivity?, isRetainedEarnings=false, active=true }`; **200 `AccountResponse`** on success; **422** ProblemDetails on a chart-invariant violation; requires `Permission.ManageAccounts` (Controller/Admin). **Upsert by id**: a new account uses a client-generated GUID; an edit reuses the existing id. The request has **no `normalSide`** (derived).
- `GET /clients/{clientId}/trial-balance?asOf=` → `TrialBalanceResponse { asOf, accounts: { accountId, balance }[] }` (balance **debit-positive signed**). Used to annotate the tree with balances in one call.

## A. 1c hardening pass (known fixes — no new design)

1. **`entry-form.ts` `balanceError` scope.** Filter `entryForm.lines().errors()` to the tree-level kinds (`unbalanced`, `min-lines`) for the footer strip; surface each line's `one-side` error on its own row instead. Removes the fresh-form "Enter a debit OR a credit" noise.
2. **`entry-form.ts` stable track key.** Add a generated `lineId` to `LineModel`; `@for (line of model().lines; track line.lineId)`. Fixes index-reuse on mid-row removal.
3. **`approval-queue.ts` resilient audit fetch.** Wrap each per-row `entryAudit(id)` in `catchError(() => of([]))` so one failed audit doesn't blank the whole queue; render an **"author unknown"** cue (distinct from "your entry — needs another approver") when the author can't be resolved.
4. **`approval-queue.spec.ts` identity-switch test.** Add a case: after audits resolve, switching `DevIdentityService` flips `approvableById` for the affected rows.
5. **`entry-detail.ts` void-reason a11y.** Give the void-reason input an associated `hlmLabel` (or `aria-label`).
6. **DRY shared journal view helpers.** Extract a `formatMoney`-wrapping `money()` + `formatDate()` (a small `core/format` helper or a shared base) and a presentational **`<app-posting-badge [posting]="...">`** used by entry-list, approval-queue, and entry-detail (replacing the duplicated pending/posted badge markup).

These ship as the first task block; each keeps the full suite green.

## B. Chart of Accounts

### B1. Service additions (`core/accounts/`)

Extend the existing `AccountsService`:
- `upsert(account: AccountUpsert): Observable<AccountResponse>` → `PUT …/accounts/{id}`; on success, refresh the cached `accounts()` (re-`load()` or splice the returned account in). `AccountUpsert = { id: string; number; name; type; parentId: string | null; postable; requiredDimension: string | null; cashFlowActivity: string | null; isRetainedEarnings; active }`.
- `newId(): string` — `crypto.randomUUID()` for create.
- A small **tree builder** (pure function, unit-tested): `buildTree(accounts, balancesById)` → per-type sections, each a forest of nodes `{ account, balance, children }`, children sorted by `number`, roots = accounts whose `parentId` is null **or** points outside the type. Parent (rollup) balance = sum of its own + descendants' trial-balance amounts.

### B2. Chart of Accounts tree screen (E1) — `features/accounts/chart-of-accounts.ts`

- On load: `AccountsService.load()` (if empty) + `TrialBalanceService.get()` (as-of today) → join into the tree via `buildTree`.
- **Layout:** five type sections (Asset, Liability, Equity, Revenue, Expense) in chart order. Each row (indented by depth): account **number**, **name**, **balance** (right-aligned tabular; debit-positive shown in the natural column per the account's normal side, parens for contra), a **"header"** chip when `!postable`, an **inactive** dimming/badge when `!active`. A **"Show inactive"** toggle (default off). Empty/loading/error states. A **"New account"** button and a per-row **Edit** affordance open the editor (B3).
- **Drag-to-reparent** (`@angular/cdk/drag-drop`): drag a row onto another account to set its `parentId` to that target, or onto a type-section's **root drop zone** to clear `parentId` (make it a root). **Valid drop targets are restricted client-side** to: same `type`, not the dragged node itself, and not one of its descendants (mirrors the engine invariants: child shares parent's type, no cycles). An invalid target is not a drop candidate (no call); a server **422** is still surfaced inline as a backstop. A successful drop issues a single `upsert` carrying the **unchanged fields + new `parentId`**, then the tree re-renders from the refreshed cache. **Sibling order is not draggable** — within a parent, rows are ordered by `number` (renumber via the editor to reorder). A short helper line states this.

### B3. Account editor (E2, Signal Forms) — `features/accounts/account-editor.ts`

- A Signal-Forms form to **create** or **edit** an account. Fields: `number` (text, required), `name` (text, required), `type` (select: Asset/Liability/Equity/Revenue/Expense, required), `parentId` (select — **filtered to same-type accounts**, excluding self + descendants when editing; "— none (root) —" option), `cashFlowActivity` (select: none/Operating/Investing/Financing), and flags `postable`, `isRetainedEarnings`, `active` (checkboxes). **`normalSide` is derived from `type` and shown read-only** (Asset/Expense → Debit; Liability/Equity/Revenue → Credit).
- `schema()`: `required` on number/name/type. Type change re-derives the read-only normal side and re-filters the parent options.
- **Save:** map to `AccountUpsert` (create → `newId()`; edit → existing id) → `upsert`. On success, close + refresh the tree. Server **422** (duplicate number, >1 retained-earnings, cross-type parent, cycle) rendered inline via `extractProblem`.
- **Renumbering** is just editing `number` on an existing account — fully supported; uniqueness enforced by the engine (422 on collision). (Bulk/guided renumber with transient collisions is out of scope — noted as future.)
- Presentation: a routed panel or dialog under `accounts`. Route: `accounts` → tree (E1); `accounts/new` and `accounts/:id/edit` → editor (E2). (If a dialog is used instead of routes, it opens over the tree; the plan picks one — routed panel preferred for deep-linkability and simplicity.)

### B4. Testing (Vitest + HttpTestingController)

- **Service:** `upsert` PUTs to `…/accounts/{id}` with the mapped body (no `normalSide`); cache refreshes. `buildTree` (pure): groups by type, nests by `parentId`, sorts children by number, rolls up parent balances, treats out-of-type/null parent as root.
- **Tree screen:** renders the five sections + hierarchy + balances from stubbed accounts + trial-balance; "show inactive" toggles inactive rows; a drop onto a same-type account computes the right `parentId` and PUTs; a drop target that is cross-type or a descendant is rejected (no PUT); a stubbed 422 surfaces inline.
- **Editor:** required validation blocks save; type drives the read-only normal side and filters parent options; create PUTs with a fresh GUID; edit PUTs with the existing id; renumber edit PUTs the new number; a stubbed 422 (duplicate number) surfaces.
- **Hardening regressions:** fresh entry-form shows no per-line noise in the balance strip; line removal keeps row/value alignment (stable key); approval-queue survives a single audit 404 (other rows still resolve) and shows "author unknown"; identity-switch flips approvability; `<app-posting-badge>` renders pending vs posted.

## Scope boundary (YAGNI)

**In:** the six hardening fixes; COA tree (grouped, hierarchical, balances, show-inactive, drag-to-reparent); account create/edit/renumber/reparent via the editor.
**Out (deferred):** Cash Flow statement (G3), Periods (H), real Dashboard (C); sibling display-order persistence / engine order field; bulk-renumber tooling; account deletion (accounts are deactivated, not deleted — `active` flag); per-account drill-to-entries from the tree.

## Open (resolve during the plan)
- Editor as **routed panel** (`accounts/new`, `accounts/:id/edit`) vs dialog — plan picks routed panel unless an hlm dialog is trivially available and preferred.
- Whether the tree needs `hlm` components beyond what exists (table/card/badge/button/select/input/label all present) — likely only CDK drag-drop (no generation). The plan confirms and adds a prerequisite only if a new hlm component is required.
- Parent-balance rollup uses the trial-balance amounts already fetched; no extra per-account balance calls.
