# Reconciliation Worksheet Drill-in Implementation Plan (Slice 2a)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make bank-reconciliation worksheet rows drill into the journal entry at `/journal/:entryId`, gated on `gl.read`, keep the clearing checkbox working, and truncate the Reference column.

**Architecture:** A single frontend component change (`reconciliation-worksheet.ts`) plus its spec. Whole-row navigation mirrors `bill-list.ts`; the affordance (role/tabindex/cursor/handlers) is shown only when the user holds `gl.read`, so a user who cannot open the entry never gets a clickable row (403-unreachable-via-UI, matching the nav gate). The clearing checkbox stops click propagation so toggling never navigates. The Reference cell gains the shared `appTruncate` directive. No backend, no route change — `/journal/:id` (`EntryDetail`) already exists, is ungated, and takes a plain entry id.

**Tech Stack:** Angular 22 (standalone, `ChangeDetectionStrategy.OnPush`, zoneless), Tailwind v4, Spartan Helm, Jasmine + TestBed (Vitest runner).

## Global Constraints

- Standalone component, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent.
- The drill affordance is gated on `caps.has('gl.read')` via a `computed()` (`CapabilityService.has` reads the `capabilities()` signal, so a `computed` reacts to load). When absent: no `role`/`tabindex`/`cursor`/hover/navigation on the row.
- Detail route stays ungated (no new guard) — consistent with every existing detail route. Read access is governed by nav gate + this affordance gate + backend 403.
- Reference truncation uses the existing shared directive `TruncateDirective` (`[appTruncate]`, at `src/app/shared/truncate.directive.ts`), imported as `'../../shared/truncate.directive'`.
- Only touch `UI/Angular/src/app/features/banking/reconciliation-worksheet.ts` and its `.spec.ts`. Do NOT touch any other file, the deferred Slice-2b/2c lists, or the backend.
- Commands from `UI/Angular`. Unit test: `npx ng test --include='**/reconciliation-worksheet.spec.ts' --watch=false`. Compile gate: `npx ng build --configuration development` (tail: `Application bundle generation complete`).
- Work is on branch `feat/worksheet-drill-in`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: Worksheet row drill-in + Reference truncation

**Files:**
- Modify: `UI/Angular/src/app/features/banking/reconciliation-worksheet.ts`
- Modify (extend existing): `UI/Angular/src/app/features/banking/reconciliation-worksheet.spec.ts`

**Interfaces:**
- Consumes: `TruncateDirective` (Slice 1), `CapabilityService.has(cap): boolean`, `Router.navigate`, the existing `WorksheetEntry { entryId, date, reference, sourceType, cashEffect, cleared }`.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing tests**

Extend `reconciliation-worksheet.spec.ts`. First, parameterize the existing `boot()` to accept capabilities (default preserves the current test):

Change the `boot()` signature line from:
```ts
function boot() {
```
to:
```ts
function boot(caps: string[] = ['bankrec.write']) {
```
and change its provider line from:
```ts
      provideCapabilities('bankrec.write'),
```
to:
```ts
      provideCapabilities(...caps),
```

Add `Router` to the router import at the top of the file:
```ts
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
```

Then add these three specs inside the `describe('ReconciliationWorksheet', ...)` block, after the existing `it(...)`:

```ts
  it('navigates to the journal entry when a drillable row is clicked', () => {
    const { fixture } = boot(['bankrec.write', 'gl.read']);
    const nav = spyOn(TestBed.inject(Router), 'navigate');
    const row = (fixture.nativeElement as HTMLElement).querySelector('tbody tr')!;
    expect(row.getAttribute('role')).toBe('button');
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).toHaveBeenCalledWith(['/journal', 'e1']);
  });

  it('does not navigate when the clearing checkbox is clicked (stopPropagation)', () => {
    const { fixture } = boot(['bankrec.write', 'gl.read']);
    const nav = spyOn(TestBed.inject(Router), 'navigate');
    const checkbox = (fixture.nativeElement as HTMLElement).querySelector('tbody tr input[type=checkbox]')!;
    checkbox.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).not.toHaveBeenCalled();
  });

  it('shows no drill affordance without gl.read', () => {
    const { fixture } = boot(['bankrec.write']);
    const nav = spyOn(TestBed.inject(Router), 'navigate');
    const row = (fixture.nativeElement as HTMLElement).querySelector('tbody tr')!;
    expect(row.getAttribute('role')).toBeNull();
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).not.toHaveBeenCalled();
  });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `npx ng test --include='**/reconciliation-worksheet.spec.ts' --watch=false`
Expected: the 3 new specs FAIL (row has no `role="button"`, no click navigation, no `open` wiring). The pre-existing "clears an entry" spec still passes.

- [ ] **Step 3: Implement the component change**

In `UI/Angular/src/app/features/banking/reconciliation-worksheet.ts`:

**3a.** Update imports. Change the `@angular/core` import to add `computed`:
```ts
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
```
Add `NgClass` and the `@angular/router` `Router`, and the two new imports (place the core/shared imports next to the existing ones):
```ts
import { NgClass } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
```
and, alongside the existing `CanDirective` import:
```ts
import { CapabilityService } from '../../core/capabilities/capability.service';
import { TruncateDirective } from '../../shared/truncate.directive';
```

**3b.** Add `NgClass` and `TruncateDirective` to the `imports` array:
```ts
  imports: [RouterLink, HlmButton, CanDirective, AdjustmentsPanel, ...HlmTableImports, NgClass, TruncateDirective],
```

**3c.** Replace the row `<tr>` block. Change:
```html
              @for (e of w.entries; track e.entryId) {
                <tr hlmTr>
                  <td hlmTd><input type="checkbox" [checked]="e.cleared" [disabled]="w.reconciliation.status !== 'InProgress' || busy()" (change)="toggle(e)" /></td>
                  <td hlmTd>{{ date(e.date) }}</td>
                  <td hlmTd>{{ e.reference ?? '—' }}</td>
                  <td hlmTd>{{ e.sourceType ?? '—' }}</td>
                  <td hlmTd class="text-right tabular-nums" [class.text-destructive]="e.cashEffect < 0">{{ money(e.cashEffect) }}</td>
                </tr>
              }
```
to:
```html
              @for (e of w.entries; track e.entryId) {
                <tr hlmTr
                    [ngClass]="canDrill() ? 'cursor-pointer hover:bg-muted/50' : ''"
                    [attr.role]="canDrill() ? 'button' : null"
                    [attr.tabindex]="canDrill() ? 0 : null"
                    (click)="canDrill() && open(e.entryId)"
                    (keydown.enter)="canDrill() && open(e.entryId)">
                  <td hlmTd><input type="checkbox" [checked]="e.cleared" [disabled]="w.reconciliation.status !== 'InProgress' || busy()" (change)="toggle(e)" (click)="$event.stopPropagation()" /></td>
                  <td hlmTd>{{ date(e.date) }}</td>
                  <td hlmTd><span appTruncate>{{ e.reference ?? '—' }}</span></td>
                  <td hlmTd>{{ e.sourceType ?? '—' }}</td>
                  <td hlmTd class="text-right tabular-nums" [class.text-destructive]="e.cashEffect < 0">{{ money(e.cashEffect) }}</td>
                </tr>
              }
```
(`NgClass` is used for the conditional class string because `hover:bg-muted/50` contains `:`/`/`, which are awkward in a `[class.<name>]` binding.)

**3d.** Add the injections, the `canDrill` computed, and the `open` method. After the existing `private readonly destroyRef = inject(DestroyRef);` line, add:
```ts
  private readonly router = inject(Router);
  private readonly caps = inject(CapabilityService);
```
and after the `readonly message = signal<string | null>(null);` line, add:
```ts
  readonly canDrill = computed(() => this.caps.has('gl.read'));
```
and add the method (e.g. just after `toggle(...)`):
```ts
  open(entryId: string): void { void this.router.navigate(['/journal', entryId]); }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `npx ng test --include='**/reconciliation-worksheet.spec.ts' --watch=false`
Expected: all specs PASS (the original + the 3 new), output pristine.

- [ ] **Step 5: Compile gate**

Run: `npx ng build --configuration development`
Expected: success, tail `Application bundle generation complete`.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/banking/reconciliation-worksheet.ts UI/Angular/src/app/features/banking/reconciliation-worksheet.spec.ts
git commit -m "feat(ui): drill from reconciliation worksheet into the journal entry"
```

---

## Self-Review

**Spec coverage:** whole-row nav to `/journal/:entryId` (Step 3c/3d); `gl.read` affordance gate (`canDrill`, conditional row attrs); checkbox `stopPropagation` (Step 3c); Reference truncation via `appTruncate` (Step 3c); no backend/route/guard change; tests for nav / no-nav-on-checkbox / no-affordance-without-gl.read (Step 1). ✓

**Placeholder scan:** every edit shows exact old→new markup and complete test code. No TBD.

**Type/name consistency:** `canDrill`, `open(entryId)`, `caps`, `router` used identically in component and referenced correctly in tests; `TruncateDirective`/`[appTruncate]` and `NgClass` imports match usage; entry id `'e1'` matches the existing `worksheet()` fixture.

## Execution Handoff

Two execution options:

1. **Subagent-Driven (recommended)** — a fresh subagent for the task with review, then final review.
2. **Inline Execution** — execute in this session with a checkpoint.
