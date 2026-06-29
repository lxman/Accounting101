# UI Slice — 1c Hardening + Chart of Accounts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the deferred 1c hardening fixes, then build the Chart of Accounts screen — a hierarchical, balance-annotated tree with drag-to-reparent and a Signal-Forms account editor (create/edit/renumber/reparent).

**Architecture:** Pure helpers (`buildTree`/`canDrop`/`isDescendant`) keep the tree + drag rules testable in isolation; the tree component renders type-grouped hierarchy joined with trial-balance amounts and wires CDK drag-drop (one drop zone per account + a per-type root zone, gated by an enter-predicate that enforces same-type/non-descendant). The editor reuses Signal Forms (as in the 1c entry form). `AccountsService.upsert` is the single write path (`PUT /accounts/{id}`, upsert-by-id).

**Tech Stack:** Angular 22 (standalone, signals, zoneless, OnPush), Signal Forms (`@angular/forms/signals`), `@angular/cdk/drag-drop`, Tailwind v4, Spartan UI (hlm), Vitest.

## Global Constraints

- Zoneless + OnPush on every component; standalone with `standalone: true` **omitted**; signals + `input()`/`output()`/signal queries; `@if`/`@for`; `inject()` DI.
- Money/dates render **only** through the formatter: `formatMoney(amount,'USD',DEFAULT_FORMAT_PROFILE,{symbol})`, `formatProfileDate(...)`. Decimal-aligned, tabular numerals, accounting parens for negatives. Never hand-format.
- API returns raw `decimal` + ISO dates (camelCase). Services return typed DTOs; URLs client-scoped via `ClientContextService.clientId()`.
- **Account identity is the GUID `id`; `number` is a renumberable label and the canonical sort key.** No separate display-order field. **Sort by `number` ordinally** (string compare) to match the engine's statement ordering.
- **Chart invariants (engine-enforced on every PUT, 422 on violation):** parent exists; child shares parent's `type`; no cycles; numbers unique; ≤1 retained-earnings. Normal side derived from `type` (Asset/Expense → Debit; Liability/Equity/Revenue → Credit) — read-only in the UI.
- `PUT /clients/{clientId}/accounts/{accountId}` body = `AccountRequest { number, name, type, parentId?, postable=true, requiredDimension?, cashFlowActivity?, isRetainedEarnings=false, active=true }` (NO `normalSide`); returns **200 `AccountResponse`**.
- Env: `nvm use 24.18.0` (Bash subshells: `export PATH="/c/nvm4w/nodejs:$PATH"`). Test: `ng test --watch=false` (Vitest). Build: `npm run build`. Work from `UI/Angular`. Use `vi.spyOn` (not Jasmine `spyOn`).
- Commit trailer verbatim: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Existing types (already in the codebase)

- `AccountResponse` (`core/accounts/account.ts`): `{ id, number, name, type: AccountType, parentId: string|null, postable, requiredDimension: string|null, cashFlowActivity: string|null, isRetainedEarnings, active, normalSide: 'Debit'|'Credit', isTemporary }`. `AccountType = 'Asset'|'Liability'|'Equity'|'Revenue'|'Expense'`.
- `Posting = 'PendingApproval'|'Posted'` (`core/entries/entry.ts`).
- `TrialBalanceResponse { asOf, accounts: { accountId, balance }[] }` (`core/trial-balance/trial-balance.ts`); `TrialBalanceService.get(asOf?)`.
- `AccountsService` (`core/accounts/accounts.service.ts`): `accounts()` signal, `byId` computed, `load()`, `label(id)`, private `_accounts` signal.

---

### Task 1: 1c behavior hardening (entry-form, approval-queue, entry-detail)

**Files:**
- Modify: `src/app/features/journal/entry-form.ts`, `entry-form.spec.ts`
- Modify: `src/app/features/journal/approval-queue.ts`, `approval-queue.spec.ts`
- Modify: `src/app/features/journal/entry-detail.ts`

**Interfaces:** No new exports. Behavior-only fixes.

- [ ] **Step 1: entry-form — scope `balanceError` to tree-level errors**

In `entry-form.ts`, replace the `balanceError` computed (currently line ~143):
```ts
readonly balanceError = computed(() => this.entryForm.lines().errors().map(e => e.message).filter(Boolean).join('; ') || null);
```
with (only the cross-line kinds — drops the per-line `one-side` noise from the shared strip):
```ts
readonly balanceError = computed(() =>
  this.entryForm.lines().errors()
    .filter(e => e.kind === 'unbalanced' || e.kind === 'min-lines')
    .map(e => e.message).join('; ') || null);

// Per-line error, shown on the row once that line is touched (keeps a fresh form quiet).
lineError(i: number): string | null {
  const f = this.entryForm.lines[i]();
  return f.touched() ? (f.errors().find(e => e.kind === 'one-side')?.message ?? null) : null;
}
```

- [ ] **Step 2: entry-form — surface the per-line error on its row**

In the line `@for` block, after the credit `<td>` (line ~73) and before the remove-button `<td>`, the per-line error already has a column; instead add it under the account cell. Change the account `<td>` (line ~62-71) to append the error below the select:
```html
<td class="py-1 pr-2">
  <div hlmSelect [value]="line.accountId" [itemToString]="accountItemToString" (valueChange)="setAccount($index, $any($event))" class="w-full">
    <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select account" /></hlm-select-trigger>
    <hlm-select-content *hlmSelectPortal>
      @for (a of postableAccounts(); track a.id) {
        <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item>
      }
    </hlm-select-content>
  </div>
  @if (lineError($index)) { <span class="text-destructive text-xs">{{ lineError($index) }}</span> }
</td>
```

- [ ] **Step 3: entry-form — stable `lineId` track key**

Change `LineModel` + `emptyLine` (lines 15, 18):
```ts
interface LineModel { lineId: string; accountId: string; debit: number | null; credit: number | null; }
...
const emptyLine = (): LineModel => ({ lineId: crypto.randomUUID(), accountId: '', debit: null, credit: null });
```
Change the line `@for` (line 60) from `track $index` to `track line.lineId`:
```html
@for (line of model().lines; track line.lineId; let i = $index) {
```
and replace every `$index` used for form indexing inside that block with `i` (the account select `setAccount($index,…)` → `setAccount(i,…)`, `entryForm.lines[$index]` → `entryForm.lines[i]`, `lineError($index)` → `lineError(i)`, `removeLine($index)` → `removeLine(i)`). In `toRequest()`, the `lines` map already ignores `lineId` (it only reads accountId/debit/credit), so no change there.

- [ ] **Step 4: entry-form — run the spec**

Run: `export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false -- entry-form.spec.ts`
Expected: PASS. If the spec set line values via `cmp.entryForm.lines[0].debit().value.set(...)` it still works (form tree indexing unchanged). If any assertion read `balanceError()` expecting a one-side message on a fresh form, update it to expect `null` on a pristine form and the `unbalanced` message only after values make it unbalanced.

- [ ] **Step 5: approval-queue — resilient per-row audit + three-state cue**

In `approval-queue.ts`: import `catchError, of` and the audit type:
```ts
import { catchError, forkJoin, of } from 'rxjs';
import { AuditRecordResponse } from '../../core/audit/audit';
```
Replace the `approvableById` computed (lines 58-61) with a three-state cue:
```ts
readonly cueById = computed<Record<string, 'approvable' | 'own' | 'unknown'>>(() => {
  const me = this.identity.active().sub;
  const authors = this.authorById();
  return Object.fromEntries(this.entries().map(e => {
    const a = authors[e.id];
    return [e.id, a == null ? 'unknown' : a === me ? 'own' : 'approvable'];
  }));
});
```
Wrap each audit fetch so one failure degrades only that row (lines 68-71):
```ts
forkJoin(Object.fromEntries(page.items.map(e =>
  [e.id, this.audit.entryAudit(e.id).pipe(catchError(() => of([] as AuditRecordResponse[])))]))).subscribe({
    next: (map) => this.authorById.set(Object.fromEntries(
      Object.entries(map).map(([id, recs]) => [id, this.audit.authorOf(recs)]))),
  });
```
Update the cue cell (lines 35-38):
```html
<td hlmTd>
  @switch (cueById()[e.id]) {
    @case ('approvable') { <span hlmBadge variant="secondary">Approvable</span> }
    @case ('own') { <span hlmBadge class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]">Your entry — needs another approver</span> }
    @default { <span hlmBadge variant="outline">Author unknown — cannot approve</span> }
  }
</td>
```

- [ ] **Step 6: approval-queue — spec updates + identity-switch test**

In `approval-queue.spec.ts`: change assertions from `approvableById()` to `cueById()` (`['E1']` → `'own'`, `['E2']` → `'approvable'`). Add a case: after both audits flush, switch identity and assert the cue flips:
```ts
it('flips the cue when the active identity changes', () => {
  const f = TestBed.createComponent(ApprovalQueue); f.detectChanges();
  ctrl.expectOne(r => r.params.get('posting') === 'PendingApproval').flush({ items: [
    { id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: null, supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
  ], total: 1, skip: 0, limit: 50 });
  f.detectChanges();
  ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1').flush([
    { sequence: 1, action: 'Created', entryId: 'E1', entryVersion: 1, at: '', reason: null, actor: { userId: environment.devClerk.sub, name: 'C', claims: [] } }]);
  f.detectChanges();
  expect(f.componentInstance.cueById()['E1']).toBe('own');     // active = clerk = author
  TestBed.inject(DevIdentityService).use(environment.devApprover.sub);
  f.detectChanges();
  expect(f.componentInstance.cueById()['E1']).toBe('approvable');
});
```
Add a case proving a single audit 404 degrades only that row (flush one audit with `flush([])` and another with an error, assert the failed one is `'unknown'` and the other resolves). Imports: `DevIdentityService`, `environment` (already imported in the spec).

- [ ] **Step 7: entry-detail — void-reason a11y label**

In `entry-detail.ts`, the void input (line ~67): add an accessible name.
```html
<input hlmInput type="text" aria-label="Void reason" placeholder="Void reason" [value]="voidReason()" (input)="voidReason.set($any($event.target).value)" />
```

- [ ] **Step 8: full suite + build + commit**

Run: `ng test --watch=false` (expected: all pass) and `npm run build` (expected: success).
```bash
git add src/app/features/journal/entry-form.ts src/app/features/journal/entry-form.spec.ts src/app/features/journal/approval-queue.ts src/app/features/journal/approval-queue.spec.ts src/app/features/journal/entry-detail.ts
git commit -m "fix(ui): 1c hardening — balanceError scope, stable line keys, resilient queue cue, void a11y

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Shared posting-badge + display-format helpers (DRY)

**Files:**
- Create: `src/app/core/format/display.ts`, `src/app/shared/posting-badge.ts`, `posting-badge.spec.ts`
- Modify: `src/app/features/journal/entry-list.ts`, `entry-detail.ts` (use the badge + helpers)

**Interfaces:**
- Produces: `money(n: number): string`, `displayDate(d: string): string` (`core/format/display.ts`); `PostingBadge` component `<app-posting-badge [posting]="Posting">` (`shared/posting-badge.ts`). Consumed by journal screens and by the COA screen (Task 4/5 use `money`/`displayDate`).

- [ ] **Step 1: display helpers**

`core/format/display.ts`:
```ts
import { formatMoney } from './money-formatter';
import { formatProfileDate } from './date-formatter';
import { DEFAULT_FORMAT_PROFILE } from './format-profile';

/** Money for display: USD, no symbol (symbol shows on totals only, per the profile), parens negatives. */
export const money = (n: number): string => formatMoney(n, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: false });

/** A date string rendered through the active Format Profile. */
export const displayDate = (d: string): string => formatProfileDate(d, DEFAULT_FORMAT_PROFILE);
```

- [ ] **Step 2: posting-badge (TDD)**

`shared/posting-badge.spec.ts`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { PostingBadge } from './posting-badge';

describe('PostingBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] }));
  it('renders Pending for PendingApproval', () => {
    const f = TestBed.createComponent(PostingBadge);
    f.componentRef.setInput('posting', 'PendingApproval'); f.detectChanges();
    expect(f.nativeElement.querySelector('[data-testid=badge-pending]')).toBeTruthy();
    expect(f.nativeElement.textContent).toContain('Pending');
  });
  it('renders Posted otherwise', () => {
    const f = TestBed.createComponent(PostingBadge);
    f.componentRef.setInput('posting', 'Posted'); f.detectChanges();
    expect(f.nativeElement.querySelector('[data-testid=badge-posted]')).toBeTruthy();
  });
});
```
`shared/posting-badge.ts`:
```ts
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { Posting } from '../core/entries/entry';

@Component({
  selector: 'app-posting-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmBadgeImports],
  template: `
    @if (posting() === 'PendingApproval') {
      <span hlmBadge class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]" data-testid="badge-pending">Pending</span>
    } @else {
      <span hlmBadge variant="secondary" data-testid="badge-posted">Posted</span>
    }
  `,
})
export class PostingBadge { readonly posting = input.required<Posting>(); }
```

- [ ] **Step 3: adopt in entry-list**

In `entry-list.ts`: add `import { PostingBadge } from '../../shared/posting-badge';` and `import { money as _money, displayDate } from '../../core/format/display';` (or just `displayDate`). Add `PostingBadge` to `imports`. Replace the status `<td>` inline badge markup with `<td hlmTd><app-posting-badge [posting]="entry.posting" /></td>`. Replace the `formatDate` method body with `return displayDate(date);` (drop the now-unused `formatProfileDate`/`DEFAULT_FORMAT_PROFILE` imports if nothing else uses them). Keep the `data-testid` assertions in `entry-list.spec.ts` green (the badge preserves them).

- [ ] **Step 4: adopt in entry-detail**

In `entry-detail.ts`: add `PostingBadge` to imports; replace the header pending/posted `@if/@else` (lines ~27-28) with `<app-posting-badge [posting]="e.posting" />`. Replace `money`/`formatDate` method bodies to delegate to the shared `money`/`displayDate` (import them; rename to avoid clash, e.g. `import { money as fmtMoney, displayDate } from '../../core/format/display'` and `money(n){ return fmtMoney(n); } formatDate(d){ return displayDate(d); }`).

- [ ] **Step 5: full suite + build + commit**

Run: `ng test --watch=false` + `npm run build` (expected: pass).
```bash
git add src/app/core/format/display.ts src/app/shared/posting-badge.ts src/app/shared/posting-badge.spec.ts src/app/features/journal/entry-list.ts src/app/features/journal/entry-detail.ts
git commit -m "refactor(ui): shared <app-posting-badge> + display-format helpers (DRY journal screens)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Accounts service `upsert` + pure tree/drag logic

**Files:**
- Modify: `src/app/core/accounts/account.ts` (add `AccountUpsert`), `accounts.service.ts` (add `upsert`, `newId`), `accounts.service.spec.ts`
- Create: `src/app/core/accounts/account-tree.ts`, `account-tree.spec.ts`

**Interfaces:**
- Produces: `AccountUpsert` type; `AccountsService.upsert(a: AccountUpsert): Observable<AccountResponse>`, `AccountsService.newId(): string`; pure `buildTree(accounts, balancesById, showInactive): TypeSection[]`, `isDescendant(accounts, ancestorId, candidateId): boolean`, `canDrop(accounts, draggedId, targetId, sectionType): boolean`; types `AccountNode`, `TypeSection`, `TYPE_ORDER`. Consumed by Tasks 4 (tree) and 5 (editor).

- [ ] **Step 1: AccountUpsert type**

Append to `core/accounts/account.ts`:
```ts
export interface AccountUpsert {
  id: string; number: string; name: string; type: AccountType;
  parentId: string | null; postable: boolean; requiredDimension: string | null;
  cashFlowActivity: string | null; isRetainedEarnings: boolean; active: boolean;
}
```

- [ ] **Step 2: service `upsert`/`newId` (TDD)**

Add to `accounts.service.spec.ts` (create the spec if absent, mirroring other service specs — `provideHttpClient`, `provideHttpClientTesting`, `ClientContextService.select('C1')`, `afterEach ctrl.verify()`):
```ts
it('upsert PUTs the account and refreshes the cache', () => {
  const svc = TestBed.inject(AccountsService);
  const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  const a = { id: 'A1', number: '1000', name: 'Cash', type: 'Asset' as const, parentId: null,
    postable: true, requiredDimension: null, cashFlowActivity: 'Operating', isRetainedEarnings: false, active: true };
  let saved: AccountResponse | undefined;
  svc.upsert(a).subscribe(r => (saved = r));
  const req = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/A1');
  expect(req.request.method).toBe('PUT');
  expect(req.request.body).toEqual({ number: '1000', name: 'Cash', type: 'Asset', parentId: null,
    postable: true, requiredDimension: null, cashFlowActivity: 'Operating', isRetainedEarnings: false, active: true });
  const resp = { ...a, normalSide: 'Debit', isTemporary: false } as AccountResponse;
  req.flush(resp);
  expect(saved).toEqual(resp);
  expect(svc.accounts().find(x => x.id === 'A1')).toEqual(resp);
});
```
Implement in `accounts.service.ts` (add imports `tap` from `rxjs`, `AccountUpsert` from `./account`):
```ts
upsert(a: AccountUpsert) {
  const id = this.client.clientId();
  const body = { number: a.number, name: a.name, type: a.type, parentId: a.parentId,
    postable: a.postable, requiredDimension: a.requiredDimension, cashFlowActivity: a.cashFlowActivity,
    isRetainedEarnings: a.isRetainedEarnings, active: a.active };
  return this.http.put<AccountResponse>(`${environment.apiBaseUrl}/clients/${id}/accounts/${a.id}`, body)
    .pipe(tap(saved => this._accounts.update(list => {
      const i = list.findIndex(x => x.id === saved.id);
      return i >= 0 ? list.map(x => (x.id === saved.id ? saved : x)) : [...list, saved];
    })));
}
newId(): string { return crypto.randomUUID(); }
```

- [ ] **Step 3: pure tree/drag logic (TDD)**

`account-tree.spec.ts` — cover grouping, nesting, ordinal child sort, balance rollup, and the drag predicates:
```ts
import { buildTree, isDescendant, canDrop } from './account-tree';
import { AccountResponse, AccountType } from './account';

const acc = (id: string, number: string, type: AccountType, parentId: string | null = null, active = true): AccountResponse =>
  ({ id, number, name: 'n' + number, type, parentId, postable: true, requiredDimension: null,
     cashFlowActivity: null, isRetainedEarnings: false, active, normalSide: 'Debit', isTemporary: false });

describe('buildTree', () => {
  it('groups by type in chart order, nests by parentId, sorts children ordinally, rolls up balances', () => {
    const accounts = [acc('p', '1000', 'Asset'), acc('c2', '1200', 'Asset', 'p'), acc('c1', '1100', 'Asset', 'p'), acc('r', '4000', 'Revenue')];
    const bal = new Map([['p', 0], ['c1', 30], ['c2', 70], ['r', -100]]);
    const sections = buildTree(accounts, bal, false);
    expect(sections.map(s => s.type)).toEqual(['Asset', 'Liability', 'Equity', 'Revenue', 'Expense']);
    const asset = sections.find(s => s.type === 'Asset')!;
    expect(asset.nodes.length).toBe(1);                         // single root 'p'
    expect(asset.nodes[0].children.map(n => n.account.id)).toEqual(['c1', 'c2']); // 1100 before 1200 (ordinal)
    expect(asset.nodes[0].balance).toBe(100);                   // 0 + 30 + 70 rolled up
  });
  it('hides inactive unless showInactive', () => {
    const accounts = [acc('a', '1000', 'Asset'), acc('b', '1100', 'Asset', null, false)];
    expect(buildTree(accounts, new Map(), false).find(s => s.type === 'Asset')!.nodes.length).toBe(1);
    expect(buildTree(accounts, new Map(), true).find(s => s.type === 'Asset')!.nodes.length).toBe(2);
  });
});

describe('isDescendant / canDrop', () => {
  const accounts = [acc('p', '1000', 'Asset'), acc('c', '1100', 'Asset', 'p'), acc('g', '1110', 'Asset', 'c'), acc('rev', '4000', 'Revenue')];
  it('isDescendant walks the parent chain', () => {
    expect(isDescendant(accounts, 'p', 'g')).toBe(true);
    expect(isDescendant(accounts, 'c', 'p')).toBe(false);
  });
  it('canDrop: same type, not self, not own descendant', () => {
    expect(canDrop(accounts, 'c', 'rev', 'Revenue')).toBe(false);  // cross-type
    expect(canDrop(accounts, 'p', 'c', 'Asset')).toBe(false);      // c is p's descendant → cycle
    expect(canDrop(accounts, 'g', 'p', 'Asset')).toBe(true);       // reparent g under p (valid)
    expect(canDrop(accounts, 'g', null, 'Asset')).toBe(true);      // drop to Asset root
    expect(canDrop(accounts, 'g', null, 'Revenue')).toBe(false);   // wrong section root
    expect(canDrop(accounts, 'g', 'g', 'Asset')).toBe(false);      // onto self
  });
});
```
`account-tree.ts`:
```ts
import { AccountResponse, AccountType } from './account';

export interface AccountNode { account: AccountResponse; balance: number; children: AccountNode[]; }
export interface TypeSection { type: AccountType; nodes: AccountNode[]; }
export const TYPE_ORDER: AccountType[] = ['Asset', 'Liability', 'Equity', 'Revenue', 'Expense'];

const byNumber = (a: AccountNode, b: AccountNode): number =>
  a.account.number < b.account.number ? -1 : a.account.number > b.account.number ? 1 : 0; // ordinal

export function buildTree(
  accounts: readonly AccountResponse[], balancesById: ReadonlyMap<string, number>, showInactive: boolean,
): TypeSection[] {
  const visible = accounts.filter(a => showInactive || a.active);
  const inType = (type: AccountType) => visible.filter(a => a.type === type);
  return TYPE_ORDER.map(type => {
    const here = inType(type);
    const ids = new Set(here.map(a => a.id));
    const node = (a: AccountResponse): AccountNode => {
      const children = here.filter(c => c.parentId === a.id).map(node).sort(byNumber);
      const own = balancesById.get(a.id) ?? 0;
      return { account: a, balance: own + children.reduce((s, c) => s + c.balance, 0), children };
    };
    // Root within a type = no parent, or parent not present in this type section.
    const roots = here.filter(a => a.parentId === null || !ids.has(a.parentId)).map(node).sort(byNumber);
    return { type, nodes: roots };
  });
}

/** True if candidateId is within ancestorId's subtree (walks candidate's parent chain up to ancestor). */
export function isDescendant(accounts: readonly AccountResponse[], ancestorId: string, candidateId: string): boolean {
  const byId = new Map(accounts.map(a => [a.id, a]));
  let cur = byId.get(candidateId)?.parentId ?? null;
  while (cur !== null) {
    if (cur === ancestorId) return true;
    cur = byId.get(cur)?.parentId ?? null;
  }
  return false;
}

/** Valid drop: same type; if onto an account, not self and not the dragged node's own descendant; if onto a
 *  section root (targetId null), the section's type must equal the dragged account's type. */
export function canDrop(
  accounts: readonly AccountResponse[], draggedId: string, targetId: string | null, sectionType: AccountType,
): boolean {
  const byId = new Map(accounts.map(a => [a.id, a]));
  const dragged = byId.get(draggedId);
  if (!dragged || dragged.type !== sectionType) return false;
  if (targetId === null) return true;                         // drop to this section's root
  if (targetId === draggedId) return false;
  const target = byId.get(targetId);
  if (!target || target.type !== dragged.type) return false;
  return !isDescendant(accounts, draggedId, targetId);        // no cycle
}
```

- [ ] **Step 4: run specs + build + commit**

Run: `ng test --watch=false -- account-tree.spec.ts accounts.service.spec.ts` then full `ng test --watch=false` + `npm run build`.
```bash
git add src/app/core/accounts/account.ts src/app/core/accounts/accounts.service.ts src/app/core/accounts/accounts.service.spec.ts src/app/core/accounts/account-tree.ts src/app/core/accounts/account-tree.spec.ts
git commit -m "feat(ui): accounts upsert + pure chart tree/drag logic (buildTree/canDrop/isDescendant)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Chart of Accounts tree screen (E1) + drag-to-reparent

**Files:**
- Create: `src/app/features/accounts/chart-of-accounts.ts`, `chart-of-accounts.spec.ts`
- Modify: `src/app/app.routes.ts` (add `accounts` route + exclude from placeholder)

**Interfaces:**
- Consumes: `AccountsService` (`accounts`, `load`, `upsert`), `TrialBalanceService.get`, `buildTree`/`canDrop`/`TypeSection`/`AccountNode`/`TYPE_ORDER` (Task 3), `money` (Task 2), `RouterLink`; CDK `@angular/cdk/drag-drop` (`CdkDrag`, `CdkDropList`, `CdkDropListGroup`, `CdkDragDrop`).
- Produces: route `accounts` → `ChartOfAccounts`.

- [ ] **Step 1: Write the failing test** — `chart-of-accounts.spec.ts`

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChartOfAccounts } from './chart-of-accounts';
import { ClientContextService } from '../../core/client/client-context.service';
import { AccountResponse } from '../../core/accounts/account';

function seed(): AccountResponse[] {
  const a = (id: string, number: string, type: AccountResponse['type'], parentId: string | null = null, active = true): AccountResponse =>
    ({ id, number, name: 'n' + number, type, parentId, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active, normalSide: 'Debit', isTemporary: false });
  return [a('cash', '1000', 'Asset'), a('ar', '1200', 'Asset'), a('rev', '4000', 'Revenue'), a('old', '1900', 'Asset', null, false)];
}

describe('ChartOfAccounts', () => {
  let ctrl: HttpTestingController;
  function setup() {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  }
  function flushData() {
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush(seed());
    ctrl.expectOne(r => r.url.includes('/clients/C1/trial-balance')).flush({ asOf: null, accounts: [{ accountId: 'cash', balance: 500 }, { accountId: 'rev', balance: -500 }] });
  }
  afterEach(() => ctrl.verify());

  it('renders type sections with balances and hides inactive by default', () => {
    setup(); const f = TestBed.createComponent(ChartOfAccounts); f.detectChanges(); flushData(); f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('Asset'); expect(text).toContain('1000 n1000');
    expect(text).not.toContain('1900');                 // inactive hidden
    f.componentInstance.showInactive.set(true); f.detectChanges();
    expect(f.nativeElement.textContent).toContain('1900'); // shown when toggled
  });

  it('a valid drop reparents via upsert; an invalid (cross-type) drop does not', () => {
    setup(); const f = TestBed.createComponent(ChartOfAccounts); f.detectChanges(); flushData(); f.detectChanges();
    const cmp = f.componentInstance;
    // valid: AR (asset) under Cash (asset)
    cmp.onDrop('ar', 'cash', 'Asset');
    const put = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/ar');
    expect(put.request.method).toBe('PUT'); expect(put.request.body.parentId).toBe('cash');
    put.flush({ ...seed().find(x => x.id === 'ar')!, parentId: 'cash' });
    // invalid: Cash (asset) under Revenue → no call
    cmp.onDrop('cash', 'rev', 'Asset');                 // canDrop false (cross-type at section level)
    ctrl.expectNone('http://localhost:5000/clients/C1/accounts/cash');
  });
});
```

- [ ] **Step 2: Run it — verify fail** — `ng test --watch=false -- chart-of-accounts.spec.ts` → FAIL (`ChartOfAccounts` undefined).

- [ ] **Step 3: Implement `chart-of-accounts.ts`**

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CdkDrag, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { HlmButton } from '@spartan-ng/helm/button';
import { AccountsService } from '../../core/accounts/accounts.service';
import { AccountResponse } from '../../core/accounts/account';
import { TrialBalanceService } from '../../core/trial-balance/trial-balance.service';
import { buildTree, canDrop, AccountNode, TypeSection } from '../../core/accounts/account-tree';
import { extractProblem } from '../../core/api/problem-details';
import { money } from '../../core/format/display';

@Component({
  selector: 'app-chart-of-accounts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CdkDropListGroup, CdkDropList, CdkDrag, HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Chart of Accounts</h1>
        <a hlmBtn size="sm" routerLink="/accounts/new" class="ms-auto">New account</a>
        <label class="text-sm text-muted-foreground flex items-center gap-1">
          <input type="checkbox" [checked]="showInactive()" (change)="showInactive.set($any($event.target).checked)" /> Show inactive
        </label>
      </div>
      <p class="text-xs text-muted-foreground">Drag an account onto another (same type) to re-parent it, or onto a section header to make it a root. Order follows the account number — edit a number to reorder.</p>
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      <div cdkDropListGroup class="flex flex-col gap-4">
        @for (section of sections(); track section.type) {
          <section>
            <h2 class="font-semibold text-sm uppercase text-muted-foreground border-b border-border pb-1 mb-1"
                cdkDropList [cdkDropListData]="{ type: section.type, parentId: null }"
                [cdkDropListEnterPredicate]="enterPredicate(section.type, null)"
                (cdkDropListDropped)="dropped($event, section.type, null)">{{ section.type }}</h2>
            @for (node of section.nodes; track node.account.id) {
              <ng-container [ngTemplateOutlet]="row" [ngTemplateOutletContext]="{ node, type: section.type, depth: 0 }" />
            }
            @if (section.nodes.length === 0) { <p class="text-xs text-muted-foreground italic">No accounts.</p> }
          </section>
        }
      </div>

      <ng-template #row let-node="node" let-type="type" let-depth="depth">
        <div class="flex items-center gap-2 py-1 border-b border-border/50 text-sm"
             [style.padding-left.rem]="depth"
             cdkDropList [cdkDropListData]="{ type, parentId: node.account.id }"
             [cdkDropListEnterPredicate]="enterPredicate(type, node.account.id)"
             (cdkDropListDropped)="dropped($event, type, node.account.id)"
             cdkDrag [cdkDragData]="node.account.id"
             [class.opacity-50]="!node.account.active">
          <span class="font-mono">{{ node.account.number }}</span>
          <span>{{ node.account.name }}</span>
          @if (!node.account.postable) { <span class="text-xs px-1 rounded bg-muted text-muted-foreground">header</span> }
          @if (!node.account.active) { <span class="text-xs px-1 rounded bg-muted text-muted-foreground">inactive</span> }
          <span class="ms-auto tabular-nums">{{ money(node.balance) }}</span>
          <a class="underline text-xs" [routerLink]="['/accounts', node.account.id, 'edit']">Edit</a>
        </div>
        @for (child of node.children; track child.account.id) {
          <ng-container [ngTemplateOutlet]="row" [ngTemplateOutletContext]="{ node: child, type, depth: depth + 1 }" />
        }
      </ng-template>
    </div>
  `,
})
export class ChartOfAccounts {
  private readonly accountsSvc = inject(AccountsService);
  private readonly trialBalance = inject(TrialBalanceService);

  readonly showInactive = signal(false);
  readonly error = signal<string | null>(null);
  private readonly balances = signal<ReadonlyMap<string, number>>(new Map());

  readonly sections = computed<TypeSection[]>(() =>
    buildTree(this.accountsSvc.accounts(), this.balances(), this.showInactive()));

  constructor() {
    this.accountsSvc.load();
    this.trialBalance.get().subscribe({
      next: (tb) => this.balances.set(new Map(tb.accounts.map(a => [a.accountId, a.balance]))),
      error: () => { /* balances are annotation only; tree still renders without them */ },
    });
  }

  money(n: number): string { return money(n); }

  enterPredicate(type: AccountResponse['type'], parentId: string | null) {
    return (drag: { data: string }) => canDrop(this.accountsSvc.accounts(), drag.data, parentId, type);
  }

  dropped(event: { item: { data: string } }, type: AccountResponse['type'], parentId: string | null): void {
    this.onDrop(event.item.data, parentId, type);
  }

  // Pulled out for direct unit testing (the CDK event is awkward to synthesize).
  onDrop(draggedId: string, newParentId: string | null, sectionType: AccountResponse['type']): void {
    if (!canDrop(this.accountsSvc.accounts(), draggedId, newParentId, sectionType)) return;
    const a = this.accountsSvc.byId().get(draggedId);
    if (!a || a.parentId === newParentId) return;
    this.error.set(null);
    this.accountsSvc.upsert({
      id: a.id, number: a.number, name: a.name, type: a.type, parentId: newParentId,
      postable: a.postable, requiredDimension: a.requiredDimension, cashFlowActivity: a.cashFlowActivity,
      isRetainedEarnings: a.isRetainedEarnings, active: a.active,
    }).subscribe({ error: (e) => this.error.set(extractProblem(e).detail) });
  }
}
```
> `NgTemplateOutlet` is needed for the recursive `#row`. Add `import { NgTemplateOutlet } from '@angular/common';` and include it in `imports`. The drop zones use `{type, parentId}` data; `enterPredicate`/`dropped` read the dragged id from `cdkDragData`. `onDrop` is the unit-tested seam; `dropped` is the thin CDK adapter.

- [ ] **Step 4: Route + placeholder exclusion**

`app.routes.ts` — add (import `ChartOfAccounts`):
```ts
{ path: 'accounts', children: [
  { path: '', pathMatch: 'full', component: ChartOfAccounts },
  // 'new' + ':id/edit' added in Task 5
] },
```
Update the placeholder predicate so `/accounts*` is not also mapped to `Placeholder`: change it to `&& !n.path.startsWith('/journal') && !n.path.startsWith('/accounts')` (it currently excludes a hardcoded list + `/journal`). Add `/accounts` to the hardcoded exclude list if that's the existing shape.

- [ ] **Step 5: run spec + full suite + build + commit**

Run: `ng test --watch=false -- chart-of-accounts.spec.ts`, then `ng test --watch=false` + `npm run build`.
```bash
git add src/app/features/accounts/chart-of-accounts.ts src/app/features/accounts/chart-of-accounts.spec.ts src/app/app.routes.ts
git commit -m "feat(ui): chart-of-accounts tree (grouped/hierarchical + balances + drag-to-reparent)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Account editor (E2, Signal Forms) — create/edit/renumber/reparent

**Files:**
- Create: `src/app/features/accounts/account-editor.ts`, `account-editor.spec.ts`
- Modify: `src/app/app.routes.ts` (add `accounts/new`, `accounts/:id/edit`)

**Interfaces:**
- Consumes: `form`/`schema`/`required`/`FormField` (`@angular/forms/signals`), `AccountsService` (`accounts`, `byId`, `load`, `upsert`, `newId`), `AccountResponse`/`AccountType`/`AccountUpsert`, `isDescendant` (Task 3), `extractProblem`, hlm input/label/select/button, `Router`/`ActivatedRoute`.
- Produces: routes `accounts/new` + `accounts/:id/edit` → `AccountEditor`.

- [ ] **Step 1: Write the failing test** — `account-editor.spec.ts`

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { AccountEditor } from './account-editor';
import { AccountsService } from '../../core/accounts/accounts.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { AccountResponse } from '../../core/accounts/account';

function route(id: string | null) {
  return { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } };
}
function seedAccounts(svc: AccountsService) {
  const a = (id: string, number: string, type: AccountResponse['type']): AccountResponse =>
    ({ id, number, name: 'n' + number, type, parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false });
  (svc as unknown as { _accounts: { set(v: AccountResponse[]): void } })._accounts.set([a('cash', '1000', 'Asset'), a('rev', '4000', 'Revenue')]);
}

describe('AccountEditor', () => {
  let ctrl: HttpTestingController;
  function setup(id: string | null) {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), route(id)] });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    seedAccounts(TestBed.inject(AccountsService));
  }
  afterEach(() => ctrl.verify());

  it('create: required validation blocks save until number/name/type set, then PUTs a new id', () => {
    setup(null); const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.canSave()).toBe(false);
    cmp.accountForm.number().value.set('1100'); cmp.accountForm.name().value.set('Petty Cash'); cmp.accountForm.type().value.set('Asset');
    f.detectChanges();
    expect(cmp.canSave()).toBe(true);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate');
    cmp.save();
    const put = ctrl.expectOne(r => r.method === 'PUT' && /\/clients\/C1\/accounts\/.+/.test(r.url));
    expect(put.request.body.number).toBe('1100'); expect(put.request.body.type).toBe('Asset');
    put.flush({ id: 'x', number: '1100', name: 'Petty Cash', type: 'Asset', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false });
    expect(nav).toHaveBeenCalledWith(['/accounts']);
  });

  it('edit: loads the account and PUTs the same id on save (renumber)', () => {
    setup('cash'); const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.accountForm.number().value()).toBe('1000');
    cmp.accountForm.number().value.set('1001');
    f.detectChanges(); cmp.save();
    const put = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/cash');
    expect(put.request.body.number).toBe('1001');
    put.flush({ id: 'cash', number: '1001', name: 'n1000', type: 'Asset', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false });
  });

  it('surfaces a server 422 (duplicate number)', () => {
    setup(null); const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.accountForm.number().value.set('4000'); cmp.accountForm.name().value.set('Dup'); cmp.accountForm.type().value.set('Asset');
    f.detectChanges(); cmp.save();
    ctrl.expectOne(r => r.method === 'PUT').flush({ detail: 'Account number 4000 already exists' }, { status: 422, statusText: 'Unprocessable' });
    f.detectChanges();
    expect(cmp.message()).toContain('already exists');
  });
});
```

- [ ] **Step 2: Run it — verify fail** — `ng test --watch=false -- account-editor.spec.ts` → FAIL.

- [ ] **Step 3: Implement `account-editor.ts`**

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { form, required, FormField } from '@angular/forms/signals';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { AccountsService } from '../../core/accounts/accounts.service';
import { AccountResponse, AccountType } from '../../core/accounts/account';
import { isDescendant } from '../../core/accounts/account-tree';
import { extractProblem } from '../../core/api/problem-details';

interface EditorValue {
  number: string; name: string; type: AccountType; parentId: string | null;
  cashFlowActivity: string; postable: boolean; isRetainedEarnings: boolean; active: boolean;
}
const TYPES: AccountType[] = ['Asset', 'Liability', 'Equity', 'Revenue', 'Expense'];
const DEBIT_TYPES = new Set<AccountType>(['Asset', 'Expense']);

@Component({
  selector: 'app-account-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, FormField, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-xl">
      <a routerLink="/accounts" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Chart of Accounts</a>
      <h1 class="text-2xl font-bold">{{ editId ? 'Edit account' : 'New account' }}</h1>

      <div class="flex flex-col gap-1">
        <label hlmLabel>Number</label>
        <input hlmInput type="text" [formField]="accountForm.number" />
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Name</label>
        <input hlmInput type="text" [formField]="accountForm.name" />
      </div>
      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Type</label>
          <div hlmSelect [value]="accountForm.type().value()" (valueChange)="accountForm.type().value.set($any($event))">
            <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal>
              @for (t of types; track t) { <hlm-select-item [value]="t">{{ t }}</hlm-select-item> }
            </hlm-select-content>
          </div>
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Normal side</label>
          <input hlmInput type="text" [value]="normalSide()" readonly disabled />
        </div>
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Parent (same type)</label>
        <div hlmSelect [value]="accountForm.parentId().value() ?? ''" [itemToString]="parentLabel" (valueChange)="setParent($any($event))">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="— none (root) —" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            <hlm-select-item value="">— none (root) —</hlm-select-item>
            @for (p of parentOptions(); track p.id) { <hlm-select-item [value]="p.id">{{ p.number }} {{ p.name }}</hlm-select-item> }
          </hlm-select-content>
        </div>
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Cash-flow activity</label>
        <div hlmSelect [value]="accountForm.cashFlowActivity().value()" (valueChange)="accountForm.cashFlowActivity().value.set($any($event))">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="— none —" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            <hlm-select-item value="">— none —</hlm-select-item>
            <hlm-select-item value="Operating">Operating</hlm-select-item>
            <hlm-select-item value="Investing">Investing</hlm-select-item>
            <hlm-select-item value="Financing">Financing</hlm-select-item>
          </hlm-select-content>
        </div>
      </div>
      <div class="flex flex-col gap-2 text-sm">
        <label class="flex items-center gap-2"><input type="checkbox" [checked]="accountForm.postable().value()" (change)="accountForm.postable().value.set($any($event.target).checked)" /> Postable (leaf account)</label>
        <label class="flex items-center gap-2"><input type="checkbox" [checked]="accountForm.isRetainedEarnings().value()" (change)="accountForm.isRetainedEarnings().value.set($any($event.target).checked)" /> Retained-earnings account</label>
        <label class="flex items-center gap-2"><input type="checkbox" [checked]="accountForm.active().value()" (change)="accountForm.active().value.set($any($event.target).checked)" /> Active</label>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Save</button>
        <a hlmBtn variant="outline" routerLink="/accounts">Cancel</a>
      </div>
    </div>
  `,
})
export class AccountEditor {
  private readonly accounts = inject(AccountsService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly types = TYPES;
  readonly editId = this.route.snapshot.paramMap.get('id'); // null on /accounts/new
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly model = signal<EditorValue>(this.initialValue());
  readonly accountForm = form(this.model, (p) => { required(p.number); required(p.name); required(p.type); });

  readonly canSave = computed(() => this.accountForm().valid());
  readonly normalSide = computed(() => (DEBIT_TYPES.has(this.model().type) ? 'Debit' : 'Credit'));

  // Same-type accounts, excluding self and the edited account's own descendants (no cycles).
  readonly parentOptions = computed<AccountResponse[]>(() => {
    const all = this.accounts.accounts();
    const type = this.model().type;
    return all.filter(a => a.type === type && a.id !== this.editId
      && !(this.editId ? isDescendant(all, this.editId, a.id) : false));
  });

  constructor() {
    if (this.accounts.accounts().length === 0) this.accounts.load();
    if (this.editId) {
      const existing = this.accounts.byId().get(this.editId);
      if (existing) this.model.set(this.fromAccount(existing));
    }
  }

  readonly parentLabel = (id: string): string => {
    if (!id) return '— none (root) —';
    const a = this.accounts.byId().get(id); return a ? `${a.number} ${a.name}` : id;
  };
  setParent(v: string): void { this.accountForm.parentId().value.set(v === '' ? null : v); }

  private initialValue(): EditorValue {
    return { number: '', name: '', type: 'Asset', parentId: null, cashFlowActivity: '', postable: true, isRetainedEarnings: false, active: true };
  }
  private fromAccount(a: AccountResponse): EditorValue {
    return { number: a.number, name: a.name, type: a.type, parentId: a.parentId, cashFlowActivity: a.cashFlowActivity ?? '', postable: a.postable, isRetainedEarnings: a.isRetainedEarnings, active: a.active };
  }

  save(): void {
    if (!this.canSave()) return;
    const v = this.model();
    this.busy.set(true); this.message.set(null);
    this.accounts.upsert({
      id: this.editId ?? this.accounts.newId(), number: v.number, name: v.name, type: v.type, parentId: v.parentId,
      postable: v.postable, requiredDimension: null, cashFlowActivity: v.cashFlowActivity || null,
      isRetainedEarnings: v.isRetainedEarnings, active: v.active,
    }).subscribe({
      next: () => this.router.navigate(['/accounts']),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
```
> `requiredDimension` is not exposed in this editor (control-account dimensions are a subledger concern) — sent as `null`. The parent select uses `[itemToString]` (value=GUID → label) per the Spartan-select rule. Type drives `normalSide()` (read-only) and re-filters `parentOptions()`.

- [ ] **Step 4: routes**

`app.routes.ts` — add under the `accounts` children (after `''`): `{ path: 'new', component: AccountEditor }, { path: ':id/edit', component: AccountEditor },` (import `AccountEditor`).

- [ ] **Step 5: run spec + whole-app suite + build + commit**

Run: `ng test --watch=false -- account-editor.spec.ts`, then `ng test --watch=false` + `npm run build`.
```bash
git add src/app/features/accounts/account-editor.ts src/app/features/accounts/account-editor.spec.ts src/app/app.routes.ts
git commit -m "feat(ui): account editor (signal-forms create/edit/renumber/reparent, derived normal side)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**1. Spec coverage:**
- 1c hardening (all six) → Task 1 (balanceError scope, per-line error, stable lineId, queue catchError + three-state cue + identity-switch test, void a11y) + Task 2 (shared `<app-posting-badge>` + `money`/`displayDate`). ✓
- COA service `upsert` + `newId` + balances source + pure tree → Task 3. ✓
- E1 tree: grouped-by-type, hierarchical, balances rolled up, show-inactive, drag-to-reparent (same-type/non-descendant, 422 backstop), order-by-number ordinal → Task 4. ✓
- E2 editor: Signal Forms create/edit, fields, derived read-only normal side, same-type parent filter, renumber, 422 surfacing → Task 5. ✓
- Routes `accounts` / `accounts/new` / `accounts/:id/edit` + placeholder exclusion → Tasks 4–5. ✓
- Deferred (cash flow, periods, dashboard, sibling-order field, bulk-renumber, deletion) → not built. ✓

**2. Placeholder scan:** No TBD/TODO. Every step has concrete code/commands. The `>` notes are adaptation guidance with defaults, not placeholders.

**3. Type consistency:** `AccountUpsert` (Task 3) consumed by `upsert` (Task 3) and both COA screens (Tasks 4–5). `buildTree`/`canDrop`/`isDescendant`/`TypeSection`/`AccountNode` (Task 3) consumed by Task 4; `isDescendant` also by Task 5. `money`/`displayDate` (Task 2) consumed by Task 4 and the journal screens. `PostingBadge`/`Posting` (Task 2) consumed by entry-list/detail. `AccountResponse` fields match the engine contract (no `normalSide` in the PUT body; derived in the UI). Routes `['/accounts']`, `['/accounts', id, 'edit']`, `/accounts/new` consistent across Tasks 4–5. ✓

## Open (resolve during execution)
- The editor is a **routed panel** (`accounts/new`, `accounts/:id/edit`) per the spec's stated default — not a dialog.
- No new hlm component generation is required (table not used by the tree; input/label/select/button/badge all exist). CDK drag-drop ships with the installed `@angular/cdk`. If a generator turns out to be needed, add it as a prerequisite step before Task 4.
- Trial-balance may omit zero-balance accounts; `buildTree` defaults missing ids to 0, so the tree still lists every account.
