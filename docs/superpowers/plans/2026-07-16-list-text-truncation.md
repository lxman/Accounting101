# List Text Truncation Implementation Plan (Slice 1 of 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **⚠️ POST-EXECUTION CORRECTION (2026-07-16):** This plan's "no `max-width`" decision was **wrong** and was reversed during Task 5 visual verification. The shipped directive applies `block truncate min-w-0 **max-w-[28rem]**`. Without the cap, `truncate` does not engage in an auto-layout table (the column expands to full content width and the table overflows its container, pushing sibling columns off-screen). Wherever this plan says the directive is `block truncate min-w-0` with "no max-width" (Architecture, Global Constraints, Task 1 Step 3), read `block truncate min-w-0 max-w-[28rem]`. The corrected spec is authoritative. **Slice 2 must copy the cap.**

**Goal:** Add a shared `appTruncate` directive and apply it so long free-text list columns truncate (where the row drills into a detail) or wrap (true-leaf line-item tables), without tooltips.

**Architecture:** A thin standalone Angular directive applies the host classes `block truncate min-w-0` to a text element. List/table cells whose row already navigates to a detail wrap their text in `<span appTruncate>`; true-leaf line-item tables instead wrap their text in `<span class="whitespace-normal break-words">`. No new tooltips, no `max-width`. The already-shipped shell `min-w-0` fix guarantees the nav never scrolls off regardless.

**Tech Stack:** Angular 22 (standalone components, `ChangeDetectionStrategy.OnPush`, zoneless), Tailwind CSS v4 (CSS-first, no JS config), Spartan NG Helm directives, Jasmine + TestBed.

## Global Constraints

- Angular components are standalone with `ChangeDetectionStrategy.OnPush`; the app is zoneless.
- TS imports use single quotes; HTML template attributes use double quotes; templates use 2-space indentation. Match each file's existing indentation exactly (quoted per site below).
- The directive applies exactly these host classes: `block truncate min-w-0`. No `title`/tooltip attribute. No `max-width`.
- Wrap treatment uses `whitespace-normal break-words` on an inner element (never a `title`).
- New directive lives at `src/app/shared/truncate.directive.ts`, exported class `TruncateDirective`, selector `[appTruncate]`. From any `features/<area>/<file>.ts` it is imported as `'../../shared/truncate.directive'`.
- Adding the directive to a component = add the `import` line **and** add `TruncateDirective` to the component's `imports: [...]` array.
- Unit test runner: `npx ng test --include='<glob>' --watch=false` from `UI/Angular`. Compile gate: `npx ng build --configuration development` from `UI/Angular` (expected tail: `Application bundle generation complete`).
- Excluded from all tasks (bounded account labels): `entry-detail` (Account), `trial-balance` (Account), `statements/statement-section` (name).
- Deferred to Slice 2 (do NOT touch): credit-list, refund-list, reconciliation-worksheet, customer-account statement-of-account, vendor-account statement-of-account.
- All commands run from `C:\Users\jorda\RiderProjects\Accounting101\UI\Angular` unless noted. Work is on branch `feat/list-text-truncation`.

---

### Task 1: `appTruncate` directive

**Files:**
- Create: `UI/Angular/src/app/shared/truncate.directive.ts`
- Test: `UI/Angular/src/app/shared/truncate.directive.spec.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: `TruncateDirective` (standalone, `selector: '[appTruncate]'`) that applies host classes `block truncate min-w-0` and sets no `title`.

- [ ] **Step 1: Write the failing test**

Create `UI/Angular/src/app/shared/truncate.directive.spec.ts`:

```ts
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TruncateDirective } from './truncate.directive';

@Component({
  imports: [TruncateDirective],
  template: `<span appTruncate>hello world</span>`,
})
class Host {}

describe('TruncateDirective', () => {
  it('applies block truncate min-w-0 and sets no title', () => {
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideZonelessChangeDetection()],
    });
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    const span: HTMLElement = f.nativeElement.querySelector('span');
    expect(span.classList.contains('block')).toBe(true);
    expect(span.classList.contains('truncate')).toBe(true);
    expect(span.classList.contains('min-w-0')).toBe(true);
    expect(span.hasAttribute('title')).toBe(false);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx ng test --include='**/truncate.directive.spec.ts' --watch=false`
Expected: FAIL — cannot resolve `./truncate.directive` (module does not exist yet).

- [ ] **Step 3: Write minimal implementation**

Create `UI/Angular/src/app/shared/truncate.directive.ts`:

```ts
import { Directive } from '@angular/core';

/** Truncates a text element to one line with an ellipsis. Apply to an inner element
 * (e.g. `<span appTruncate>`) inside a table cell or flex row; the element takes the
 * cell/flex width and clips overflow. No tooltip — every site this is applied to has
 * a row that drills into a detail showing the full value. */
@Directive({
  selector: '[appTruncate]',
  host: { class: 'block truncate min-w-0' },
})
export class TruncateDirective {}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx ng test --include='**/truncate.directive.spec.ts' --watch=false`
Expected: PASS (1 spec).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/shared/truncate.directive.ts UI/Angular/src/app/shared/truncate.directive.spec.ts
git commit -m "feat(ui): add appTruncate directive for list free-text columns"
```

---

### Task 2: Apply truncate to simple table cells

Seven files, eight cells — each is a solo free-text cell whose row already drills into a detail. Pattern: wrap the cell's expression in `<span appTruncate>…</span>`, add the import, add `TruncateDirective` to `imports`.

**Files (all under `UI/Angular/src/app/`):**
- Modify: `features/journal/entry-list.ts` (retrofit), `features/journal/approval-queue.ts`, `features/payables/bill-list.ts`, `features/banking/cash-list.ts`, `features/admin/member-list.ts`, `features/fixed-assets/asset-list.ts`, `features/inventory/item-list.ts`

**Interfaces:**
- Consumes: `TruncateDirective` from Task 1.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: `entry-list.ts` — retrofit the memo cell**

Replace lines 83-85 (20-space indent):

```html
                    <td hlmTd class="max-w-[28rem]">
                      <span class="block truncate" [title]="entry.memo ?? ''">{{ entry.memo ?? '—' }}</span>
                    </td>
```

with:

```html
                    <td hlmTd><span appTruncate>{{ entry.memo ?? '—' }}</span></td>
```

Add the import near the other `../../shared/` imports (after line 21):

```ts
import { TruncateDirective } from '../../shared/truncate.directive';
```

Add `TruncateDirective` to the `imports` array (lines 26-34), e.g. after `Paginator,`.

- [ ] **Step 2: `approval-queue.ts` — memo cell**

Replace line 37:

```html
                  <td hlmTd>{{ e.memo ?? '—' }}</td>
```

with:

```html
                  <td hlmTd><span appTruncate>{{ e.memo ?? '—' }}</span></td>
```

Add import (this file has no `../../shared/` import yet):

```ts
import { TruncateDirective } from '../../shared/truncate.directive';
```

Change `imports` (line 18) from `imports: [...HlmTableImports, ...HlmBadgeImports],` to `imports: [...HlmTableImports, ...HlmBadgeImports, TruncateDirective],`.

- [ ] **Step 3: `bill-list.ts` — vendor ref cell**

Replace line 60:

```html
                  <td hlmTd>{{ v.bill.vendorReference ?? '—' }}</td>
```

with:

```html
                  <td hlmTd><span appTruncate>{{ v.bill.vendorReference ?? '—' }}</span></td>
```

Add import (place next to `import { Paginator } from '../../shared/paginator';`):

```ts
import { TruncateDirective } from '../../shared/truncate.directive';
```

Add `TruncateDirective` to the `imports` array (line 21).

- [ ] **Step 4: `cash-list.ts` — memo cell**

Replace line 49:

```html
                  <td hlmTd>{{ r.memo ?? '' }}</td>
```

with:

```html
                  <td hlmTd><span appTruncate>{{ r.memo ?? '' }}</span></td>
```

Add import (next to `import { Paginator } from '../../shared/paginator';`) and add `TruncateDirective` to `imports` (line 18).

- [ ] **Step 5: `member-list.ts` — Member and Roles cells**

Replace lines 35-36:

```html
                  <td hlmTd>{{ displayName(m.userId) }}</td>
                  <td hlmTd>{{ m.roles.join(', ') }}</td>
```

with:

```html
                  <td hlmTd><span appTruncate>{{ displayName(m.userId) }}</span></td>
                  <td hlmTd><span appTruncate>{{ m.roles.join(', ') }}</span></td>
```

Add import (no `../../shared/` import yet) and change `imports` (line 13) from `imports: [...HlmTableImports],` to `imports: [...HlmTableImports, TruncateDirective],`.

- [ ] **Step 6: `asset-list.ts` — description cell**

Replace line 43:

```html
                  <td hlmTd>{{ v.asset.description }}</td>
```

with:

```html
                  <td hlmTd><span appTruncate>{{ v.asset.description }}</span></td>
```

Add import (next to `import { Paginator } from '../../shared/paginator';`) and add `TruncateDirective` to `imports` (line 18).

- [ ] **Step 7: `item-list.ts` — name cell**

Replace line 44:

```html
                  <td hlmTd>{{ v.item.name }}</td>
```

with:

```html
                  <td hlmTd><span appTruncate>{{ v.item.name }}</span></td>
```

Add import (next to `import { Paginator } from '../../shared/paginator';`) and add `TruncateDirective` to `imports` (line 18).

- [ ] **Step 8: Compile gate**

Run: `npx ng build --configuration development`
Expected: succeeds, tail `Application bundle generation complete`. No template errors.

- [ ] **Step 9: Commit**

```bash
git add UI/Angular/src/app/features/journal/entry-list.ts UI/Angular/src/app/features/journal/approval-queue.ts UI/Angular/src/app/features/payables/bill-list.ts UI/Angular/src/app/features/banking/cash-list.ts UI/Angular/src/app/features/admin/member-list.ts UI/Angular/src/app/features/fixed-assets/asset-list.ts UI/Angular/src/app/features/inventory/item-list.ts
git commit -m "feat(ui): truncate free-text columns on drill-in list tables"
```

---

### Task 3: Apply truncate to multi-element cell and flex/tree rows

Four files. These differ from Task 2 because the text element has an inline sibling (a badge) or is a flex/tree item, so `block` alone would misplace siblings — the name gets `appTruncate` while siblings stay put.

**Files:**
- Modify: `features/admin/capability-set-list.ts`, `features/receivables/customer-list.ts`, `features/payables/vendor-list.ts`, `features/accounts/chart-of-accounts.ts`

**Interfaces:**
- Consumes: `TruncateDirective` from Task 1.
- Produces: nothing.

- [ ] **Step 1: `capability-set-list.ts` — name cell (keep the built-in badge inline)**

Replace line 27:

```html
              <td hlmTd>{{ s.name }} @if (s.builtin) { <span class="text-xs text-muted-foreground">(built-in)</span> }</td>
```

with:

```html
              <td hlmTd><div class="flex items-center gap-1 min-w-0"><span appTruncate>{{ s.name }}</span>@if (s.builtin) { <span class="text-xs text-muted-foreground shrink-0">(built-in)</span> }</div></td>
```

Add import (no `../../shared/` import yet) and change `imports` (line 12) from `imports: [HlmButton, ...HlmTableImports],` to `imports: [HlmButton, ...HlmTableImports, TruncateDirective],`.

- [ ] **Step 2: `customer-list.ts` — name + email spans**

Replace line 32:

```html
          <span>{{ c.name }}</span><span class="text-muted-foreground">{{ c.email }}</span>
```

with:

```html
          <span appTruncate>{{ c.name }}</span><span class="text-muted-foreground" appTruncate>{{ c.email }}</span>
```

Add import (no `../../shared/` import yet) and change `imports` (line 13) from `imports: [...HlmInputImports, HlmButton, CanDirective],` to `imports: [...HlmInputImports, HlmButton, CanDirective, TruncateDirective],`.

- [ ] **Step 3: `vendor-list.ts` — name + email spans**

Replace line 32:

```html
          <span>{{ v.name }}</span><span class="text-muted-foreground">{{ v.email }}</span>
```

with:

```html
          <span appTruncate>{{ v.name }}</span><span class="text-muted-foreground" appTruncate>{{ v.email }}</span>
```

Add import (no `../../shared/` import yet) and change `imports` (line 13) from `imports: [...HlmInputImports, HlmButton, CanDirective],` to `imports: [...HlmInputImports, HlmButton, CanDirective, TruncateDirective],`.

- [ ] **Step 4: `chart-of-accounts.ts` — account name span (tree row)**

Replace line 61 (note the leading space inside the span, kept):

```html
            <span> {{ node.account.name }}</span>
```

with:

```html
            <span appTruncate> {{ node.account.name }}</span>
```

Add import (no `../../shared/` import yet) and change `imports` (line 18) from `imports: [NgTemplateOutlet, RouterLink, CdkDropListGroup, CdkDropList, CdkDrag, HlmButton, CanDirective],` to `imports: [NgTemplateOutlet, RouterLink, CdkDropListGroup, CdkDropList, CdkDrag, HlmButton, CanDirective, TruncateDirective],`.

- [ ] **Step 5: Compile gate**

Run: `npx ng build --configuration development`
Expected: succeeds, tail `Application bundle generation complete`.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/admin/capability-set-list.ts UI/Angular/src/app/features/receivables/customer-list.ts UI/Angular/src/app/features/payables/vendor-list.ts UI/Angular/src/app/features/accounts/chart-of-accounts.ts
git commit -m "feat(ui): truncate name columns in flex/tree list rows"
```

---

### Task 4: Wrap true-leaf line-item cells

Four files, five cells. These are line-item/preview tables with no underlying entity to drill into, so their free text **wraps** (stays fully visible) rather than truncating. Pattern: wrap the expression in `<span class="whitespace-normal break-words">…</span>` (overrides `hlmTd`'s forced `whitespace-nowrap`). No directive, no imports.

**Files:**
- Modify: `features/receivables/invoice-detail.ts`, `features/payables/bill-detail.ts`, `features/banking/statement-detail.ts`, `features/banking/statement-import.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing.

- [ ] **Step 1: `invoice-detail.ts` — description cell**

Replace line 45:

```html
                <td hlmTd>{{ l.description }}</td>
```

with:

```html
                <td hlmTd><span class="whitespace-normal break-words">{{ l.description }}</span></td>
```

- [ ] **Step 2: `bill-detail.ts` — description cell**

Replace line 41:

```html
                <td hlmTd>{{ l.description }}</td>
```

with:

```html
                <td hlmTd><span class="whitespace-normal break-words">{{ l.description }}</span></td>
```

(Leave the Account column on line 42 unchanged — bounded by the chart.)

- [ ] **Step 3: `statement-detail.ts` — description and ref cells**

On line 43 replace the description cell `<td hlmTd>{{ l.description }}</td>` with:

```html
<td hlmTd><span class="whitespace-normal break-words">{{ l.description }}</span></td>
```

On line 45 replace the ref cell `<td hlmTd>{{ l.externalRef ?? '' }}</td>` with:

```html
<td hlmTd><span class="whitespace-normal break-words">{{ l.externalRef ?? '' }}</span></td>
```

Leave the surrounding cells on those lines (date, amount) unchanged.

- [ ] **Step 4: `statement-import.ts` — description cell (plain `<td>`)**

On line 115 replace the description cell `<td>{{ l.description }}</td>` with:

```html
<td class="break-words">{{ l.description }}</td>
```

(This `<td>` has no `hlmTd`, so it already wraps by default; `break-words` only adds breaking of very long unbroken tokens.)

- [ ] **Step 5: Compile gate**

Run: `npx ng build --configuration development`
Expected: succeeds, tail `Application bundle generation complete`.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/invoice-detail.ts UI/Angular/src/app/features/payables/bill-detail.ts UI/Angular/src/app/features/banking/statement-detail.ts UI/Angular/src/app/features/banking/statement-import.ts
git commit -m "feat(ui): wrap description columns on leaf line-item tables"
```

---

### Task 5: Visual verification against real data

Verify the truncate and wrap behaviors in a real browser using the same dev-serve-on-4200 flow used for the original Journal fix. Only screens JordanSoft can reach have data (core GL + cash + reconciliation); the rest are covered by the Task 1 unit test + the Task 2-4 compile gates + the identical shared directive.

**Files:**
- Temporarily modify then restore: `UI/Angular/src/app/core/api/environment.ts`

**Interfaces:**
- Consumes: the running JordanSoft stack (Docker: `jordansoft-books-api-1` on :5000, `jordansoft-books-web-1` on :4200). Docker Desktop must be running.
- Produces: nothing.

- [ ] **Step 1: Free port 4200 and point dev config at JordanSoft**

```bash
docker stop jordansoft-books-web-1
```

In `UI/Angular/src/app/core/api/environment.ts`, temporarily change the `devClientId` line to `'761f80b1-f0b5-4927-b8de-dedf84477e59'` (JordanSoft). Record the original value `'55f47a46-6b2a-4767-a713-bcd0c7965a93'` to restore in Step 5.

- [ ] **Step 2: Start the dev server**

Run (background): `npx ng serve --port 4200`
Wait until `http://localhost:4200` returns HTTP 200 (the CORS allow-list only permits origin `http://localhost:4200`, so the port must be 4200).

- [ ] **Step 3: Drive the browser (act as Dev Admin)**

Load `http://localhost:4200/journal`, switch the "Acting as" identity to **Dev Admin** (identity `…0005`, the only JordanSoft member), then verify each screen. Because the entries query is keyed on client/filter/page (not identity), after switching identity trigger a re-fetch (e.g. change the posting filter) rather than expecting an automatic reload.

Verify (viewport 1280×800):
- **Truncate — `/cash/cash`** (cash-list): a long memo shows an ellipsis; Type/Amount/Memo/Status columns all remain visible; `document.documentElement.scrollWidth - clientWidth === 0`.
- **Truncate — `/accounts`** (chart-of-accounts): account names render on one line (truncating only if a row is width-constrained); no page horizontal overflow.
- **Truncate — `/journal`** (retrofit): long atom-box memos ellipsize; Lines/Status visible; no page horizontal overflow.
- **Wrap — a reachable leaf table** if data exists (`/cash` → Statements → a statement's detail, or the import preview): a long description **wraps** to multiple lines and stays fully visible (no ellipsis). If no statement data exists in the container, note that wrap screens were covered by compile + unit test only.

Expected: truncate screens ellipsize with all columns visible and zero page horizontal overflow; wrap screen (if reachable) shows full multi-line text.

- [ ] **Step 4: Stop the dev server**

Stop the background `ng serve`. If anything still holds port 4200, free it.

- [ ] **Step 5: Restore environment and container**

Restore the `devClientId` line in `environment.ts` to `'55f47a46-6b2a-4767-a713-bcd0c7965a93'` (verify no `761f80b1` or TEMP marker remains). Then:

```bash
docker start jordansoft-books-web-1
```

Confirm `http://localhost:4200` returns 200 (JordanSoft web restored). Confirm `git status --short -- UI/Angular` shows only the intended source changes plus the pre-existing `environment.ts` local diff (no truncation-related change to `environment.ts`).

- [ ] **Step 6: Commit (only if Step 3 required any fix)**

If a screen needed a correction (e.g. adding `min-w-0` to a flex container that did not shrink), apply it, re-run the relevant compile gate, and commit:

```bash
git add -A -- UI/Angular/src/app/features
git commit -m "fix(ui): container min-w-0 so flex-row truncation engages"
```

If no fix was needed, skip this step (verification is observation-only).

---

## Self-Review

**Spec coverage:**
- Directive (`block truncate min-w-0`, no title, no max-w) → Task 1. ✓
- Truncate drill-in list cells (entry-list retrofit, approval-queue, bill-list, cash-list, member-list, asset-list, item-list) → Task 2. ✓
- Truncate multi-element / flex / tree (capability-set-list, customer-list, vendor-list, chart-of-accounts) → Task 3. ✓
- Wrap true-leaf tables (invoice-detail, bill-detail, statement-detail, statement-import) → Task 4. ✓
- Deferred-to-Slice-2 set untouched; excluded account-label tables untouched → honored (not in any task). ✓
- Visual verification via dev-serve → Task 5. ✓

**Placeholder scan:** No TBD/TODO; every edit shows exact old→new markup; test and directive code are complete.

**Type/name consistency:** `TruncateDirective` / selector `[appTruncate]` used identically in Tasks 1-3; import path `'../../shared/truncate.directive'` consistent; wrap class string `whitespace-normal break-words` consistent across Task 4.

## Execution Handoff

Two execution options:

1. **Subagent-Driven (recommended)** — a fresh subagent per task with review between tasks.
2. **Inline Execution** — execute tasks in this session with checkpoints.
