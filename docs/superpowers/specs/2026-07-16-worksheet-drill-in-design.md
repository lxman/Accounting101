# Reconciliation worksheet drill-in — design (Slice 2a of the drill-down work)

**Date:** 2026-07-16
**Status:** Design, pending implementation
**Parent:** Slice 2 (drill-down where a row is an entity + capability-gated). This is
sub-slice **2a** — the smallest, frontend-only piece. Sub-slices 2b (credit/refund
detail screens + backend GET endpoints) and 2c (statement-of-account line drill-in +
backend DTO enrichment) are separate and come later.

## Problem

The bank-reconciliation worksheet (`features/banking/reconciliation-worksheet.ts`)
lists ledger entries touching the cash account. Each row **is** a journal entry and
already carries its `entryId`, but the row is not clickable — there is no way to open
the underlying entry from the worksheet. Separately, the worksheet's Reference column
was deferred from Slice 1's truncation sweep precisely because it had no drill-in;
with a drill-in added, truncation becomes the correct treatment.

## Goals

- Make each worksheet row navigate to the entry's detail at `/journal/:entryId`.
- Gate the drill-in affordance on read access to where the detail lives: a row is
  clickable only if the user holds `gl.read`; otherwise it is a plain,
  non-interactive row. This mirrors the nav principle — the UI never offers access
  the user does not have, so a 403 is unreachable through normal navigation.
- Keep the clearing checkbox fully functional (toggling cleared/uncleared must not
  also navigate).
- Truncate the Reference column with the shared `appTruncate` directive, now that the
  row drills into a detail showing the full reference.

## Non-goals

- No backend work. `/journal/:id` (`EntryDetail`) already exists, is ungated, and
  takes a plain entry id via `entries.get(id)`; the backend enforces `gl.read` on that
  GET. No new endpoint, DTO, or route.
- No new route guard. Detail routes stay ungated like every other detail route in the
  app; read access is governed by the nav gate plus the in-component affordance gate
  plus the backend 403. (Confirmed: no `canRead` guard exists in the codebase; adding
  one would be a new, inconsistent pattern.)
- Credit/refund detail (2b) and statement-of-account drill-in (2c) are out of scope.

## Design

All changes are in `features/banking/reconciliation-worksheet.ts` (one component + its
spec). Pattern mirrors the existing whole-row navigation in
`features/payables/bill-list.ts`.

### Affordance gate

Expose a read flag from the injected `CapabilityService`:

```ts
readonly canDrill = computed(() => this.caps.has('gl.read'));
```

(`CapabilityService.has(...)` is the established primitive; `gl.read` is the confirmed
read capability for journal entries. Exact reactive wiring — signal vs. method — is an
implementation detail resolved in the plan; the component already injects
`CapabilityService` for its existing `bankrec.write` gating.)

### Row markup

Each `<tr>` gains a conditional drill affordance driven by `canDrill()`:

- When `canDrill()` is true: `role="button"`, `tabindex="0"`,
  `cursor-pointer hover:bg-muted/50`, and `(click)`/`(keydown.enter)` →
  `open(e.entryId)`.
- When false: none of the above — a plain row with no pointer, role, tabindex, or
  handlers.

`open(entryId)` navigates: `this.router.navigate(['/journal', entryId])` (inject
`Router`). Because the affordance itself is gated, `open` is only reachable with
`gl.read`.

### Checkbox segregation

The clearing checkbox keeps its `(change)="toggle(e)"` and additionally stops click
propagation so a checkbox click never bubbles to the row's navigation:

```html
<input type="checkbox" [checked]="e.cleared"
       (change)="toggle(e)" (click)="$event.stopPropagation()" />
```

This is independent of `canDrill()` — clearing works for any user with `bankrec.write`
regardless of `gl.read`.

### Reference truncation

Wrap the Reference cell's text in the shared directive and register it:

```html
<td hlmTd><span appTruncate>{{ e.reference ?? '—' }}</span></td>
```

Add `import { TruncateDirective } from '../../shared/truncate.directive';` and
`TruncateDirective` to the component's `imports` array.

## Testing

Component spec (`reconciliation-worksheet.spec.ts`, Jasmine + TestBed + zoneless,
`provideRouter([])` since navigation is exercised):

- **Row click navigates** — with `gl.read` present, clicking a row calls
  `router.navigate(['/journal', <entryId>])` (spy on `Router.navigate`).
- **Checkbox does not navigate** — clicking the checkbox toggles clearing (its handler
  runs) and does **not** call `router.navigate` (the `stopPropagation` guard).
- **No affordance without `gl.read`** — with `gl.read` absent, the row carries no
  `role="button"`/click handler and clicking it does not navigate.

Compile gate: `npx ng build --configuration development`. Visual verification is
best-effort — JordanSoft has no reconciliation set up (no imported statement), so the
worksheet is likely not data-reachable; the spec plus compile gate are the primary
gates, consistent with Slice 1's unreachable screens.

## Rollout

Source-only; not promoted to the JordanSoft container by this work.
