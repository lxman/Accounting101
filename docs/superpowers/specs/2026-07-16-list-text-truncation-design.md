# List text truncation — shared `appTruncate` directive (Slice 1 of 2)

**Date:** 2026-07-16
**Status:** Design, pending implementation
**Related:** Slice 2 — "drill-down where a row is an entity" + capability gating
(separate spec, not yet written). This spec is Slice 1 and ships independently.

## Problem

Data tables and list rows across the app render user-entered free text (memo,
reference, description, entity names) with no width management. A long value
stretches the column until the table exceeds its content area. Concretely:
JordanSoft's real journal now carries long asset-contribution memos (the GIGABYTE
atom-box entries), and on the Journal list this pushed the Lines and Status
columns off the right edge.

The **containment** defect — a wide table scrolling the whole page and carrying the
nav off-screen — was the flexbox `min-width: auto` trap on the shell's `<main>` and
is **already fixed** globally (`shell.ts`: `<main class="flex-1 min-w-0 p-6">`).
With that in place no screen can scroll its nav off; the worst a wide table does is
scroll horizontally *inside* the content area. This spec addresses the remaining
defect: long free-text columns making tables wider than they should be.

## Two treatments, chosen by whether the full text is reachable

A `title` tooltip would only ever reveal the characters hidden by the ellipsis, and
on most screens that reveal is already one click away — so instead of truncating
everywhere and papering over it with hover text, each site gets one of two
treatments based on whether the full value is reachable:

- **Truncate** — for list/table rows whose row **already navigates to a detail
  screen that displays the full value**. The list stays tidy; the full text lives
  one click away on the detail. **No tooltip.**
- **Wrap** — for **leaf tables with no underlying entity to drill into**: invoice
  and bill **line-item descriptions**, and the statement-import preview. Truncating
  here would hide text with no recourse, so the cell wraps and stays fully visible.
  These are short tables (invoices/bills have few lines), so variable row height is
  a non-issue.

### Explicitly deferred to Slice 2 (do NOT touch in this slice)

Five leaf-today tables whose rows **are entities with ids** and therefore *should*
gain a capability-gated drill-down rather than being wrapped. Slice 2 adds their
drill-down and truncates them in the same change, so this slice leaves them
untouched to avoid wrap-then-unwrap churn:

- `receivables/credit-list.ts` (Memo) — needs a new credit-detail screen
- `receivables/refund-list.ts` (Memo) — needs a new refund-detail screen
- `banking/reconciliation-worksheet.ts` (Reference) — wire row → `/journal/:id`
- `receivables/customer-account.ts` statement-of-account (Ref) — wire → doc detail
- `payables/vendor-account.ts` statement-of-account (Ref) — wire → doc detail

## Goals

- One shared directive that truncates a text element, applied consistently across
  the drill-in list screens; future lists inherit the pattern by using it.
- Wrap the true-leaf line-item/preview tables so nothing is hidden.
- Retrofit the Journal list (fixed inline as the original proof) onto the shared
  directive so there is a single idiom.

## Non-goals

- No tooltips.
- No responsive/mobile work. The app targets desktop (fixed 224px sidebar). The
  pre-existing narrow-window overflow from the Journal header controls (the `w-48`
  posting filter + "New entry" button below ~700px) is out of scope.
- No drill-down navigation and no new detail screens — those are Slice 2.
- No change to the already-shipped shell containment fix.
- Account-label columns are excluded (bounded by the client's chart): `entry-detail`
  (Account), `trial-balance` (Account), `statements/statement-section` (name).

## Design

### The directive — `appTruncate`

`src/app/shared/truncate.directive.ts`, selector `[appTruncate]`, standalone,
alongside `CanDirective` (`appCan`) and `Paginator`.

- **Host classes only**: applies `block truncate min-w-0`.
  - `truncate` = `overflow: hidden; text-overflow: ellipsis; white-space: nowrap`.
  - `block` makes the element take the cell/flex content-box width so the clip has a
    bound to work against.
  - `min-w-0` lets it also shrink when it is itself a flex child.
- **No `title`/tooltip.** The reveal is the row's drill-in (guaranteed for every
  site this directive is applied to in this slice).
- **No `max-width`.** Verified during the Journal fix: `max-w-*` on a `<td>` is
  ignored by auto table-layout (the memo cell rendered ~630px despite
  `max-w-[28rem]`), and `truncate` clips to whatever width the column receives.
  Omitting a cap lets each column use its fair share and truncate only genuinely
  overlong values. The Journal cell's now-pointless `max-w-[28rem]` is removed as
  part of the retrofit so there is one idiom.

The directive is deliberately thin; its value is a single named, discoverable source
of truth for the `block truncate min-w-0` trio so call sites can't forget `min-w-0`,
plus a place to hang future behavior.

### Applying it

**Table cells** — wrap the cell's text in a span carrying the directive; the `<td>`
is otherwise unchanged:

```html
<td hlmTd><span appTruncate>{{ entry.memo }}</span></td>
```

**Flex / tree rows** — add the directive to the existing text element **and** add
`min-w-0` to the row's flex/tree container so the truncating child may shrink (the
directive targets the child, not the parent, so the container tweak is per-site).

### Wrap treatment (leaf tables)

`hlmTd` forces `whitespace-nowrap`, so wrapping requires an override. Wrap the leaf
cell's text in `<span class="whitespace-normal break-words">` (an inner element, for
reliable override of the directive-set nowrap and to break very long unbroken
tokens). No directive involved.

## Scope — sites in this slice

### Truncate (rows already drill into a detail showing the full value)

Table cells (inner `<span appTruncate>`):
1. `features/journal/entry-list.ts` — Memo (**retrofit**: replace inline
   `max-w-[28rem]` + span with the directive)
2. `features/journal/approval-queue.ts` — Memo (→ `/journal/:id`, EntryDetail shows memo)
3. `features/payables/bill-list.ts` — Vendor ref (→ `/payables/bills/:id`)
4. `features/banking/cash-list.ts` — Memo (→ `/cash/cash/:id`, CashVoucherDetail shows memo)
5. `features/admin/capability-set-list.ts` — Name (→ `/admin/access/sets/:id`)
6. `features/admin/member-list.ts` — Member, Roles (→ `/admin/users/:id`)
7. `features/fixed-assets/asset-list.ts` — Description (→ `/fixed-assets/assets/:id`)
8. `features/inventory/item-list.ts` — Name (→ `/inventory/items/:id`)

Flex / tree rows (directive on text element + `min-w-0` on container):
9. `features/receivables/customer-list.ts` — name + email (→ `/receivables/customers/:id`)
10. `features/payables/vendor-list.ts` — name + email (→ `/payables/vendors/:id`)
11. `features/accounts/chart-of-accounts.ts` — account name (tree row; full name also
    shown inline in the tree and in the gated edit form)

### Wrap (true leaf, no underlying entity)

12. `features/receivables/invoice-detail.ts` — Description (line item)
13. `features/payables/bill-detail.ts` — Description (line item). The Account column
    is left as-is (bounded by the chart, like the excluded account-label tables); only
    Description wraps.
14. `features/banking/statement-detail.ts` — Description, Ref (imported bank lines)
15. `features/banking/statement-import.ts` — Description (read-only preview)

### Deferred to Slice 2

credit-list, refund-list, reconciliation-worksheet, customer-account
(statement-of-account), vendor-account (statement-of-account) — see above.

### Excluded (bounded account labels)

entry-detail (Account), trial-balance (Account), statements/statement-section (name).

## Testing

- **Unit** (`truncate.directive.spec.ts`): a host component asserts the directive
  applies `block`, `truncate`, and `min-w-0` to its host and sets no `title`. Use the
  existing zoneless setup (`provideZonelessChangeDetection()`; `provideRouter([])`
  only where a rendered component needs it).
- **Visual spot-check**: via the dev-serve-on-4200 flow used for the Journal fix
  (temporarily point `environment.ts` at the JordanSoft client, act as Dev Admin,
  restore after). Confirm on a truncate screen the client uses (cash-list) that long
  memos ellipsize and Lines/Status stay visible, and on a wrap screen
  (invoice-detail or statement-detail) that long descriptions wrap and stay fully
  visible. Confirm no page-level horizontal overflow is introduced.

## Rollout

Source-only; not deployed to the JordanSoft container by this work. Promotion is a
separate `update.ps1` step at the user's discretion.
