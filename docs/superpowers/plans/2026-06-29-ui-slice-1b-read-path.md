# UI Slice 1b — Read Path (live data: entries · trial balance · statements) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the Angular app (`UI/Angular`, the Slice-1a foundation) to the **real engine API** and ship the read screens of the demo critical path — **entry list, trial balance, balance sheet, income statement** — rendering live data through the Format Profile, authenticated with the engine's DevToken scheme.

**Architecture:** Typed Angular services wrap the engine's HTTP endpoints (one service per resource). A functional interceptor mints the engine's **unsigned DevToken** (`Authorization: DevToken <base64url(json)>`) from a configured dev user; the active `clientId` comes from config (no per-user "my clients" endpoint exists yet) via the Slice-1a `ClientContextService`. Screens are standalone, zoneless, OnPush components using the real Spartan hlm components (table/card/badge/select/pagination) and the Slice-1a `formatMoney`/`formatProfileDate` (with `DEFAULT_FORMAT_PROFILE` — no per-client profile endpoint yet). Unit tests mock HTTP via `HttpTestingController` — **no running backend is required to build or test**; a live engine is only needed to *demo*.

**Tech Stack:** Angular 22 (standalone, signals, zoneless, OnPush), Tailwind v4, Spartan UI, Vitest.

## Global Constraints

- All Slice-1a conventions hold: **zoneless + OnPush on every component**, standalone with `standalone: true` **omitted**, signals + `input()`/`output()`/signal queries, `@if`/`@for` (no `*ngIf`/`*ngFor`), `inject()` DI, functional interceptors, **behaviors as attribute directives** (`hostDirectives`) not in components/services.
- **Money/dates render ONLY through the Slice-1a formatter** (`core/format/`): `formatMoney(amount, 'USD', DEFAULT_FORMAT_PROFILE, {symbol})`, `formatProfileDate(...)`. Decimal-aligned, tabular numerals; negatives in accounting parens. Never hand-format money.
- **The API returns raw `decimal` + ISO dates** (camelCase JSON). Services return typed DTOs; components format. No client-side recomputation of server aggregates.
- **DevToken scheme** (NOT Bearer): `Authorization: DevToken <base64url(utf8(json({sub,name,claims})))>` — unsigned, the running host accepts it. `sub` = the dev user Guid (must have a control-DB membership for the client to get real data; absent membership → 403, which the UI surfaces).
- Env: `nvm use 24.18.0` before npm/ng (in Bash subshells `export PATH="/c/nvm4w/nodejs:$PATH"`). Test: `ng test --watch=false` (Vitest, no `--browsers`). Build: `npm run build`. Work from `UI/Angular`.
- Commit trailer verbatim on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Exact engine contracts (camelCase JSON — from the backend recon)

- `GET /clients/{clientId}/entries?posting=&account=&reference=&skip=&limit=` — **dual-shape**: bare `EntryResponse[]` for filtered (`account`/`reference`/`sourceRef`/`dimension`) paths OR when no `skip`/`limit`; `PagedResponse<EntryResponse>` on the unfiltered/posting-only path when `skip` or `limit` is present. `posting` ∈ `"PendingApproval"|"Posted"` (else 400). **No `order` param.** `limit` default 200, clamp 1–1000.
- `EntryResponse`: `{ id, sequenceNumber, effectiveDate, type, status, posting, lineCount, supersedes, supersededBy, reversalOf, reversedBy, lines: EntryLineResponse[], sourceRef, sourceType, reference, memo, viaModule }`. `EntryLineResponse`: `{ accountId, direction: "Debit"|"Credit", amount, dimensions: Record<string,string>, lineMemo }`.
- `PagedResponse<T>`: `{ items: T[], total, skip, limit }` (Slice-1a already has this interface).
- `GET /clients/{clientId}/trial-balance?asOf=` → `TrialBalanceResponse { asOf, accounts: { accountId, balance }[] }`. **balance is debit-positive signed; NO number/name** → join with accounts.
- `GET /clients/{clientId}/statements/balance-sheet?asOf=` → `BalanceSheetResponse { asOf, assets: Section, liabilities: Section, equity: Section, totalAssets, totalLiabilitiesAndEquity, isBalanced }` where `Section = { title, lines: { accountId, number, name, amount }[], total }`.
- `GET /clients/{clientId}/statements/income-statement?from=&to=` → `IncomeStatementResponse { from, to, revenue: Section, expenses: Section, netIncome }`. **`from` and `to` REQUIRED** (missing/invalid → 422).
- `GET /clients/{clientId}/accounts` → `AccountResponse[] { id, number, name, type, parentId, postable, requiredDimension, cashFlowActivity, isRetainedEarnings, active, normalSide: "Debit"|"Credit", isTemporary }`.

---

## Prerequisite (run once, before Task 1) — generate the hlm components

These are interactive Spartan generators; run them at the terminal (components.json already exists from 1a, so no theme prompt — they just copy component source into `libs/ui`). From `UI/Angular` (with Node 24):
```
ng g @spartan-ng/cli:ui table
ng g @spartan-ng/cli:ui select
ng g @spartan-ng/cli:ui pagination
ng g @spartan-ng/cli:ui card
ng g @spartan-ng/cli:ui badge
```
Commit the generated `libs/ui/**` additions:
```
git add UI/Angular/libs UI/Angular/components.json UI/Angular/package*.json UI/Angular/tsconfig*.json
git commit -m "feat(ui): generate hlm table/select/pagination/card/badge for the read screens

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```
(If a generator prompts for anything beyond confirmation, accept defaults. The import alias is `@spartan-ng/helm/<name>`.) **Task implementers assume these components exist at `@spartan-ng/helm/{table,select,pagination,card,badge}`.**

---

### Task 1: DevToken auth + API client foundation + AccountsService

**Files:**
- Modify: `src/app/core/api/environment.ts` (dev user + client config), `src/app/core/api/auth.interceptor.ts` (DevToken scheme)
- Create: `src/app/core/api/dev-token.ts` (encoder), `src/app/core/api/api-base.ts` (client-scoped URL helper) — or fold the URL helper into each service; `src/app/core/accounts/account.ts` (AccountResponse + types), `src/app/core/accounts/accounts.service.ts`
- Modify: `src/app/app.ts` or an `APP_INITIALIZER`/root effect to seed `ClientContextService` with `environment.devClientId`
- Test: `dev-token.spec.ts`, `auth.interceptor.spec.ts` (update), `accounts.service.spec.ts`

**Interfaces:**
- Produces: `encodeDevToken(payload)`; the DevToken interceptor; `AccountResponse` type + `AccountsService.list()` (signal/Observable of accounts) + an `accountLabel(id)` lookup — consumed by every later task.

- [ ] **Step 1: environment — dev user + client**

`environment.ts`:
```ts
export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5000',
  // DevToken identity (must match a control-DB membership for real data; see plan)
  devUserId: '00000000-0000-0000-0000-000000000001',
  devUserName: 'Dev User',
  devClaims: [{ type: 'role', value: 'Controller' }, { type: 'admin', value: 'true' }] as { type: string; value: string }[],
  // Active client in dev (no per-user "my clients" endpoint yet)
  devClientId: '' as string, // set to the seeded demo client's Guid before demoing
};
```

- [ ] **Step 2: DevToken encoder (TDD)**

`dev-token.spec.ts`:
```ts
import { encodeDevToken, DevTokenPayload } from './dev-token';

describe('encodeDevToken', () => {
  it('produces a base64url(JSON) string that round-trips to the payload', () => {
    const payload: DevTokenPayload = { sub: 'abc', name: 'Dev', claims: [{ type: 'role', value: 'Controller' }] };
    const enc = encodeDevToken(payload);
    expect(enc).not.toContain('+'); expect(enc).not.toContain('/'); expect(enc).not.toContain('=');
    const json = JSON.parse(decodeURIComponent(escape(atob(enc.replace(/-/g, '+').replace(/_/g, '/')))));
    expect(json).toEqual(payload);
  });
});
```
`dev-token.ts`:
```ts
export interface DevClaim { type: string; value: string; }
export interface DevTokenPayload { sub: string; name?: string; claims: DevClaim[]; }

export function encodeDevToken(payload: DevTokenPayload): string {
  const json = JSON.stringify(payload);
  const b64 = btoa(unescape(encodeURIComponent(json)));   // utf8-safe base64
  return b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); // base64url, no padding
}
```

- [ ] **Step 3: DevToken interceptor (replace the Bearer stub)**

`auth.interceptor.ts`:
```ts
import { HttpInterceptorFn } from '@angular/common/http';
import { environment } from './environment';
import { encodeDevToken } from './dev-token';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!environment.devUserId) return next(req);
  const token = encodeDevToken({ sub: environment.devUserId, name: environment.devUserName, claims: environment.devClaims });
  return next(req.clone({ setHeaders: { Authorization: `DevToken ${token}` } }));
};
```
Update `auth.interceptor.spec.ts`: assert the header equals `DevToken ` + `encodeDevToken({sub:devUserId,...})`; and that with `devUserId` empty no header is set.

- [ ] **Step 4: Seed the active client**

In the root `App` (or an `provideAppInitializer`/root `effect`), set the client context from config so all services have a client to scope to:
```ts
// in App constructor (OnPush, zoneless):
constructor() { const c = inject(ClientContextService); if (environment.devClientId) c.select(environment.devClientId); }
```
(Keep `App` OnPush; this is a one-time seed.)

- [ ] **Step 5: AccountsService + the label lookup**

`account.ts`:
```ts
export type AccountType = 'Asset' | 'Liability' | 'Equity' | 'Revenue' | 'Expense';
export interface AccountResponse {
  id: string; number: string; name: string; type: AccountType; parentId: string | null;
  postable: boolean; requiredDimension: string | null; cashFlowActivity: string | null;
  isRetainedEarnings: boolean; active: boolean; normalSide: 'Debit' | 'Credit'; isTemporary: boolean;
}
```
`accounts.service.ts` — fetch once, cache as a signal; expose a `byId` map for label joins:
```ts
@Injectable({ providedIn: 'root' })
export class AccountsService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);
  private readonly _accounts = signal<AccountResponse[]>([]);
  readonly accounts = this._accounts.asReadonly();
  readonly byId = computed(() => new Map(this._accounts().map(a => [a.id, a])));

  load(): void {
    const id = this.client.clientId(); if (!id) return;
    this.http.get<AccountResponse[]>(`${environment.apiBaseUrl}/clients/${id}/accounts`)
      .subscribe(a => this._accounts.set(a));
  }
  label(accountId: string): string {
    const a = this.byId().get(accountId); return a ? `${a.number} ${a.name}` : accountId;
  }
}
```
`accounts.service.spec.ts`: with a seeded client, `load()` issues `GET …/clients/{id}/accounts`; flushing a list populates `accounts()` and `label(id)` returns `"<number> <name>"`, falling back to the id when unknown.

- [ ] **Step 6: build + test + commit** (`feat(ui): DevToken auth + API foundation + AccountsService` + trailer)

---

### Task 2: Entries API + entry-list screen

**Files:**
- Create: `src/app/core/entries/entry.ts` (DTOs), `entries.service.ts`, `src/app/features/journal/entry-list.ts`, `entries.service.spec.ts`, `entry-list.spec.ts`
- Modify: `src/app/app.routes.ts` (point `journal` → `EntryList`)

**Interfaces:**
- Consumes: `PagedResponse<T>` (1a), the formatter, `AccountsService` (Task 1).
- Produces: `EntriesService.listPaged(...)`.

- [ ] **Step 1: Entry DTOs**

`entry.ts`:
```ts
export type Direction = 'Debit' | 'Credit';
export type Posting = 'PendingApproval' | 'Posted';
export interface EntryLineResponse { accountId: string; direction: Direction; amount: number; dimensions: Record<string, string>; lineMemo: string | null; }
export interface EntryResponse {
  id: string; sequenceNumber: number; effectiveDate: string; type: string; status: string; posting: Posting;
  lineCount: number; supersedes: string | null; supersededBy: string | null; reversalOf: string | null;
  reversedBy: string | null; lines: EntryLineResponse[]; sourceRef: string | null; sourceType: string | null;
  reference: string | null; memo: string | null; viaModule: string | null;
}
```

- [ ] **Step 2: EntriesService (TDD)**

`entries.service.spec.ts` — `listPaged({posting, skip, limit})` calls `GET /clients/{id}/entries?posting=Posted&skip=0&limit=50` and returns the `PagedResponse<EntryResponse>` (always pass `limit`, so the engine returns the paged envelope). Assert the URL + that the envelope passes through. Mock with `HttpTestingController` + a seeded client.
`entries.service.ts`:
```ts
@Injectable({ providedIn: 'root' })
export class EntriesService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);
  listPaged(opts: { posting?: Posting; skip: number; limit: number }) {
    const id = this.client.clientId();
    let params = new HttpParams().set('skip', opts.skip).set('limit', opts.limit);
    if (opts.posting) params = params.set('posting', opts.posting);
    return this.http.get<PagedResponse<EntryResponse>>(`${environment.apiBaseUrl}/clients/${id}/entries`, { params });
  }
}
```

- [ ] **Step 3: entry-list component**

`entry-list.ts` — OnPush, signals. A posting filter (hlm `select`: All / Posted / PendingApproval), an hlm `table` of entries (columns: `#` sequenceNumber, Date `formatProfileDate(effectiveDate)`, Memo, Lines `lineCount`, Status `posting` as an hlm `badge` — Pending uses `bg-[color:var(--pending)] text-[color:var(--pending-foreground)]`), and hlm `pagination` driven by the `PagedResponse` (`total`/`skip`/`limit` → page count). Use signals for `posting`, `skip`, `limit`; load via an `effect`/explicit call to `EntriesService.listPaged`. Money: an entry's total isn't in the response — show `lineCount` (the per-line amounts are in `lines`; a "total debits" display is optional and, if shown, summed from `lines` where `direction==='Debit'` and rendered with `formatMoney`). Header shows "Page N of M" from the envelope. Empty/loading/error states.
- Route: `journal` → `EntryList` (replace the placeholder in `app.routes.ts`).

- [ ] **Step 4: entry-list test**

`entry-list.spec.ts` — render with a stubbed `EntriesService` returning a `PagedResponse` of 3 entries (total 3, limit 2); assert the rows render (date via the formatter, the Pending badge present), the pager shows 2 pages, and changing the posting filter re-queries. Mirror Slice-1a component test setup (provideZonelessChangeDetection, provideRouter).

- [ ] **Step 5: build + test + commit** (`feat(ui): entries service + paged entry-list screen` + trailer)

---

### Task 3: Trial balance screen (with account join)

**Files:**
- Create: `src/app/core/trial-balance/trial-balance.ts` (DTO), `trial-balance.service.ts`, `src/app/features/trial-balance/trial-balance.ts` (component), specs
- Modify: `app.routes.ts` (`trial-balance` → component)

**Interfaces:**
- Consumes: `AccountsService` (label + normalSide join), the formatter.

- [ ] **Step 1: DTO + service**

`trial-balance.ts`:
```ts
export interface TrialBalanceRow { accountId: string; balance: number; }
export interface TrialBalanceResponse { asOf: string | null; accounts: TrialBalanceRow[]; }
```
`trial-balance.service.ts` — `get(asOf?: string)` → `GET /clients/{id}/trial-balance` (optional `asOf` param).

- [ ] **Step 2: trial-balance component**

OnPush. On load, fetch the trial balance AND ensure accounts are loaded (`AccountsService.load()` if empty), then render a table joined by `accountId`:
- columns: Account (`accounts.byId().get(id)` → `"<number> <name>"`), Debit, Credit.
- **Column placement (debit-positive signed balance):** an account's `balance > 0` → render `formatMoney(balance,'USD',D)` in the **Debit** column; `balance < 0` → render `formatMoney(-balance,...)` in the **Credit** column; zero → blank/dash. (This is the standard debit-positive trial-balance presentation.)
- Foot the columns: sum of debit-column = sum of credit-column; show both totals with the double-rule style; if they differ, show an "out of balance" indicator. Decimal-aligned tabular numerals throughout; `asOf` date selector (an `<input type="date">` bound to a signal; default = today).

- [ ] **Step 3: tests**

Service spec (URL + asOf param). Component spec: stub the service with 3 rows (two positive, one negative) + a stubbed AccountsService; assert positive balances land in the Debit column, the negative in the Credit column, labels are joined, and the two column totals foot equal.

- [ ] **Step 4: build + test + commit** (`feat(ui): trial-balance screen with account join` + trailer)

---

### Task 4: Statements — Balance Sheet + Income Statement

**Files:**
- Create: `src/app/core/statements/statement.ts` (DTOs), `statements.service.ts`, `src/app/features/statements/balance-sheet.ts`, `income-statement.ts`, a small shared `statement-section.ts` (presentational), specs
- Modify: `app.routes.ts` (`statements` → a parent with child routes `balance-sheet` / `income-statement`, default balance-sheet)

**Interfaces:**
- Consumes: the formatter. (Statement lines already include `number`+`name` — no account join needed.)

- [ ] **Step 1: DTOs + service**

`statement.ts`:
```ts
export interface StatementLine { accountId: string | null; number: string | null; name: string; amount: number; }
export interface StatementSection { title: string; lines: StatementLine[]; total: number; }
export interface BalanceSheetResponse { asOf: string; assets: StatementSection; liabilities: StatementSection; equity: StatementSection; totalAssets: number; totalLiabilitiesAndEquity: number; isBalanced: boolean; }
export interface IncomeStatementResponse { from: string; to: string; revenue: StatementSection; expenses: StatementSection; netIncome: number; }
```
`statements.service.ts` — `balanceSheet(asOf?)` → `GET …/statements/balance-sheet?asOf=`; `incomeStatement(from, to)` → `GET …/statements/income-statement?from=&to=` (**both required** — the component must always supply them).

- [ ] **Step 2: shared section + statement components**

`statement-section.ts` (presentational, OnPush): `input()` a `StatementSection`; renders the section title, its lines (name left, `formatMoney(amount,'USD',D,{symbol:false})` right-aligned tabular), and a single-ruled section total. The `$` symbol shows on the section total row (`{symbol:true}`) and the grand-total rows, per the `firstAndTotal` profile.
`balance-sheet.ts` (OnPush): an `asOf` date selector; renders Assets / Liabilities / Equity via `statement-section`, the grand totals (`totalAssets`, `totalLiabilitiesAndEquity`) double-ruled, and an **`isBalanced`** hlm `badge` (teal when balanced via `--brand-teal`, destructive when not).
`income-statement.ts` (OnPush): a `from`/`to` date-range selector (default = current month start..today; both always sent); Revenue and Expenses via `statement-section`, then `netIncome` as the double-ruled grand total.
- Routes: `statements` parent → children `balance-sheet` (default) + `income-statement`; a small tab/segmented nav between them (hlm or plain).

- [ ] **Step 3: tests**

Service spec: the two URLs + that income-statement always sends `from`+`to`. Component specs: stub the service; balance-sheet renders the three sections + the isBalanced badge (assert balanced vs not flips the badge); income-statement renders revenue/expenses + netIncome; money is rendered via the formatter (assert a parenthesized negative appears for a negative line).

- [ ] **Step 4: build + whole-app test + commit** (`feat(ui): balance sheet + income statement screens` + trailer)

---

## Self-Review

**1. Spec coverage (vs the screen map D2/F/G + the 1b read-path scope):**
- Entry list (D2, paged, posting filter) → Task 2. ✓
- Trial balance (F, as-of, account join, debit/credit columns foot equal) → Task 3. ✓
- Balance sheet (G1, sections, isBalanced) + Income statement (G2, from/to, net income) → Task 4. ✓
- DevToken auth (the real scheme, not Bearer) + per-config clientId + AccountsService → Task 1. ✓
- Money/dates via the Slice-1a formatter everywhere; aligned decimals; parens negatives. ✓
- The hlm components the screens need → the Prerequisite generation step. ✓
- Write path (post/validate/approve) → deferred to Slice 1c. ✓ (intentional.)

**2. Placeholder scan:** No TBD/TODO. `devClientId` is intentionally empty until a demo client is seeded (documented at its definition) — tests mock HTTP and don't depend on it; the screens show an empty/error state until it's set, which is correct 1b behavior. No live backend is a test dependency.

**3. Type consistency:** Every DTO interface matches the engine's camelCase JSON from the recon (EntryResponse/EntryLineResponse, PagedResponse [reused from 1a], TrialBalanceResponse, BalanceSheet/IncomeStatement + StatementSection/StatementLine, AccountResponse). `encodeDevToken(DevTokenPayload)` (Task 1) feeds the interceptor. `AccountsService.byId/label` (Task 1) consumed by Tasks 2–3. `StatementSection` (Task 4) shared by both statement components. Services scope every URL to `ClientContextService.clientId()`.

## Open (resolve during execution / before demo)
- **Seed a demo client + membership** (control DB) and set `environment.devClientId` + `devUserId` to a matching pair before the live demo — not needed for build/test.
- A real **"my clients" endpoint** + the client switcher wiring is deferred (front-door slice); 1b uses the configured clientId.
- Per-client **Format Profile fetch** is deferred (no backend endpoint yet); 1b uses `DEFAULT_FORMAT_PROFILE`.
