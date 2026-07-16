# List text truncation — shared `appTruncate` directive

**Date:** 2026-07-16
**Status:** Design, pending implementation

## Problem

Data tables and list rows across the app render user-entered free text (memo,
reference, description, entity names) in cells with no width management. A long
value stretches the column until the table exceeds its content area. Concretely:
JordanSoft's real journal now carries long asset-contribution memos (e.g. the
GIGABYTE atom-box entries), and on the Journal list this pushed the Lines and
Status columns off the right edge.

Two distinct defects were originally in play. The **containment** defect — a wide
table scrolling the whole page and carrying the sidebar/nav off-screen — was the
flexbox `min-width: auto` trap on the shell's `<main>`, and is **already fixed**
globally (`shell.ts`: `<main class="flex-1 min-w-0 p-6">`, commit-pending). With
that in place, no screen can scroll its nav off anymore; the worst a wide table
can do is scroll horizontally *inside* the content area.

This spec addresses the remaining, lower-severity defect: **long free-text columns
make tables wider than they should be**, so the user scrolls internally to reach
right-hand columns. The Journal list was fixed inline as a proof
(`entry-list.ts`: memo wrapped in `<span class="block truncate">` with a `title`);
this spec generalizes that into one reusable pattern and sweeps the rest of the app.

## Goals

- One shared directive that truncates a text element with an ellipsis and exposes
  the full value on hover, applied consistently across all affected screens.
- No per-cell copy-paste of the truncation incantation; future lists inherit the
  pattern by using the directive.
- Retrofit the Journal list to the shared pattern so there is a single idiom.

## Non-goals

- No responsive/mobile layout work. The app targets desktop (fixed 224px sidebar,
  no breakpoints). A separate, pre-existing narrow-window overflow from the Journal
  header controls (the `w-48` posting filter + "New entry" button below ~700px) is
  **out of scope** here.
- No change to the already-shipped shell containment fix.
- Account-label columns are **excluded**: `entry-detail` (Account), `trial-balance`
  (Account), and the statement section tables pull from the client's chart of
  accounts (15 accounts for JordanSoft) and are bounded in practice.

## Design

### The directive

`src/app/shared/truncate.directive.ts`, selector `[appTruncate]`, standalone,
alongside the existing `CanDirective` (`appCan`) and `Paginator`.

Behavior:

1. **Host classes** — applies `block truncate min-w-0`. `truncate` is Tailwind's
   `overflow: hidden; text-overflow: ellipsis; white-space: nowrap`. `block` makes
   the span take the cell/flex content-box width so the clip has a bound to work
   against. `min-w-0` lets it also shrink when it is itself a flex child.
2. **Auto tooltip** — reads the element's rendered `textContent` and sets the
   `title` attribute to the full value, so hover reveals untruncated text with no
   extra markup at the call site. Uses `afterRenderEffect` (or equivalent
   post-render hook) so the title reflects the projected text and updates when the
   bound value changes. When `textContent` is empty/whitespace, no `title` is set.

**No `max-width`.** Verified during the Journal fix: `max-w-*` on a `<td>` is
ignored by auto table-layout (the memo cell rendered at ~630px despite
`max-w-[28rem]`), and `truncate` clips to whatever width the column receives
anyway. Omitting a cap lets each column use its fair share of available width and
truncate only genuinely-overlong values — strictly better than an arbitrary cap,
and one less magic number. The Journal cell's now-pointless `max-w-[28rem]` is
removed as part of this work so there is one consistent idiom.

### Applying it

**Table cells** — wrap the cell's text in a span carrying the directive; the `<td>`
itself is unchanged:

```html
<td hlmTd><span appTruncate>{{ entry.memo }}</span></td>
```

**Flex / tree rows** (customer-list, vendor-list, chart-of-accounts) — add the
directive to the existing text element, and additionally add `min-w-0` to the row's
flex container so the truncating child is allowed to shrink. This container tweak is
per-site and cannot be done by the directive (it targets the child, not the parent).

### Scope — sites to treat

Tier 1 — unbounded free text (memo / reference / description):
1. `features/journal/approval-queue.ts` — Memo
2. `features/receivables/invoice-detail.ts` — Description (line item)
3. `features/payables/bill-detail.ts` — Description, Account
4. `features/payables/bill-list.ts` — Vendor ref
5. `features/banking/statement-detail.ts` — Description, Ref
6. `features/banking/reconciliation-worksheet.ts` — Reference
7. `features/banking/cash-list.ts` — Memo
8. `features/receivables/credit-list.ts` — Memo
9. `features/receivables/refund-list.ts` — Memo
10. `features/banking/statement-import.ts` — Description (read-only preview)
11. `features/receivables/customer-account.ts` — Ref (Statement of account table)
12. `features/payables/vendor-account.ts` — Ref (Statement of account table)

Tier 2 — entity-name columns:
13. `features/fixed-assets/asset-list.ts` — Description
14. `features/inventory/item-list.ts` — Name
15. `features/admin/capability-set-list.ts` — Name
16. `features/admin/member-list.ts` — Member, Roles

Non-table rows (directive on the text element **plus** `min-w-0` on the flex/tree
container):
17. `features/receivables/customer-list.ts` — name + email
18. `features/payables/vendor-list.ts` — name + email
19. `features/accounts/chart-of-accounts.ts` — account name (CDK tree row)

Retrofit:
20. `features/journal/entry-list.ts` — replace the inline `max-w-[28rem]` + span with
    the shared directive.

Excluded (bounded account labels): `entry-detail`, `trial-balance`,
`statements/statement-section`.

## Testing

- **Unit** (`truncate.directive.spec.ts`): a host component asserts the directive
  applies `block truncate min-w-0`, derives `title` from projected text content, and
  sets no `title` when content is empty. Follow the existing zoneless test setup
  (`provideZonelessChangeDetection()`; `provideRouter([])` only where a rendered
  component needs it).
- **Visual spot-check**: via the same dev-serve-on-4200 flow used for the Journal
  fix (temporarily point `environment.ts` at the JordanSoft client, act as Dev
  Admin, restore after), confirm truncation + tooltip on two or three real screens
  the client uses (cash-list, reconciliation-worksheet, statement-detail) and that
  no page-level horizontal overflow is introduced.

## Rollout

Source-only; not deployed to the JordanSoft container by this work. Promotion to the
running stack is a separate `update.ps1` step at the user's discretion.
