# Slice 0 — Grouped, Collapsible Sidebar (visual only) — Design

**Date:** 2026-07-01
**Status:** Approved (design)
**Parent:** `2026-07-01-role-based-nav-rbac-design.md` (umbrella). This is
Sub-project 0 of that decomposition: the permanent grouped IA, with **no
capability/role filtering** (that is Slice B). Everyone sees everything.

## Goal

Replace the flat 13-item sidebar with the umbrella's five-section, collapsible
north-star tree — rendering **every** destination (unbuilt ones route to the
existing "Coming soon" `Placeholder`) — and move the dead header Firm/Client
buttons into a new Administration section. Pure navigation restructure; no
behavior beyond navigation changes.

## Scope decisions (from brainstorming)

- **Full north-star tree:** render all destinations now; unbuilt ones →
  `Placeholder`.
- **Move settings now:** Firm/Client/Fiscal/Users&Roles/Posting-accounts become
  Administration nav items; remove the header Edit Firm / Edit Client buttons.
- **Collapsible sections:** section headers and parent items with children
  expand/collapse.

## Global Constraints

- Angular 22, standalone, zoneless, OnPush; Spartan NG helm components; vitest
  via `npx ng test --watch=false` (run from `UI/Angular`).
- No backend changes. No capability/role filtering (Slice B).
- `environment.ts` (devClientId) and IDE csproj/slnx churn stay UNCOMMITTED —
  stage explicit paths only, never `git add -A`.
- Keep existing conventions: `RouterLink`, longest-prefix active highlighting,
  the persistent shell hosting `<router-outlet/>`.

## Data model — `UI/Angular/src/app/layout/nav.ts`

Replace the flat `NavItem[]` with a grouped model:

```ts
export interface NavLink { label: string; path: string; children?: NavLink[]; }
export interface NavSection { label: string; items: NavLink[]; }
export const NAV: NavSection[] = [ /* see tree below */ ];

// Flattens every leaf path (parents with children contribute their own path AND
// recurse into children). Used by app.routes.ts (placeholder derivation) and by
// the shell's active-path computation.
export function navLeafPaths(): string[];
```

A "leaf path" here means *every* `path` in the tree — parents (e.g. `/cash`,
`/audit`) have their own landing route too, and their children add more paths.

## The tree (labels → paths)

```
Overview
  Dashboard                    /dashboard

General Ledger
  Journal                      /journal
  Approvals                    /journal/approvals
  Chart of Accounts            /accounts
  Trial Balance                /trial-balance
  Financial Statements         /statements
  Period Close                 /periods

Subledgers
  Receivables                  /receivables
  Payables                     /payables
  Payroll                      /payroll
  Cash & Banking               /cash
    Bank Reconciliation        /cash/reconciliation
  Fixed Assets                 /fixed-assets

Assurance
  Audit                        /audit
    Audit Trail                /audit/trail
    Verify Integrity           /audit/verify
    Subledger Reconciliations  /audit/reconciliations
  Reports                      /reports
    Budgets                    /reports/budgets

Administration
  Users & Roles                /admin/users
  Firm                         /admin/firm
  Client                       /admin/client
  Fiscal settings              /admin/fiscal
  Posting accounts             /admin/posting-accounts
```

## Routing — `UI/Angular/src/app/app.routes.ts`

- Keep the explicit built route trees unchanged: `dashboard`, `journal` (+ new,
  approvals, :id), `trial-balance`, `statements` (+ children), `accounts`,
  `receivables`, `payables`, `payroll`.
- Replace today's ad-hoc `NAV.filter(...).map(...)` placeholder derivation with:
  from `navLeafPaths()`, register a `Placeholder` route for every leaf path
  **not** already served by a built tree. Built-prefix set:
  `/dashboard`, `/journal`, `/trial-balance`, `/statements`, `/accounts`,
  `/receivables`, `/payables`, `/payroll` (match by exact or `startsWith(prefix + '/')`).
- New placeholder routes therefore cover: `/periods`, `/cash`,
  `/cash/reconciliation`, `/fixed-assets`, `/audit`, `/audit/trail`,
  `/audit/verify`, `/audit/reconciliations`, `/reports`, `/reports/budgets`,
  `/admin/users`, `/admin/firm`, `/admin/client`, `/admin/fiscal`,
  `/admin/posting-accounts`.
- Keep the `**` → dashboard fallback.

## Shell rendering — `UI/Angular/src/app/layout/shell.ts`

- Render `NAV` as sections: each section shows a header (its `label`) and its
  items; parent items with `children` render the parent link plus an indented
  child list.
- **Collapse state:** an in-memory `signal<Set<string>>` of collapsed keys
  (section labels and parent paths). Sections default **expanded** (empty set).
  Clicking a section header or a parent's chevron toggles its key.
- **Auto-expand active:** the section (and parent, if any) containing the
  active path is always shown expanded regardless of the collapse set, so
  navigating never hides the current page. (Simplest correct rule: when
  computing "is this group open", treat it as open if not in the collapsed set
  OR if it contains the active path.)
- **Chevron affordance** on collapsible headers/parents (▸ collapsed / ▾ open).
- **Active highlight:** unchanged longest-prefix logic, computed over
  `navLeafPaths()` instead of the old flat list.
- Widen the aside `w-44` → `w-56` to fit headers + indentation.
- **Header:** remove the Edit Firm / Edit Client buttons. Keep the client
  selector button, `<app-theme-switch>`, and the identity switcher exactly as-is.

## Testing

- `shell.spec.ts` (update):
  - renders section headers (e.g. "General Ledger", "Subledgers") and
    "Dashboard".
  - a nested child (e.g. "Bank Reconciliation") becomes visible after expanding
    its parent (Cash & Banking) — or assert it is present given default-expanded
    sections; assert a collapse toggle hides a section's items.
  - the identity-switch test is unchanged.
  - replace the "Edit Firm"/"Edit Client" assertions with Administration
    "Firm"/"Client" sidebar links.
- `nav.spec.ts` (new): `navLeafPaths()` returns the full expected path set
  (parents + children), no duplicates, correct count.
- Route coverage: assert every `navLeafPaths()` entry resolves to a component
  (built or `Placeholder`) — no dead links.
- Run: `cd UI/Angular && npx ng test --watch=false`.

## Out of scope

- Capability/role filtering and the identity→capability plumbing (Slice B).
- Real screens for any placeholder destination.
- Persisting collapse state to localStorage (in-memory only for this slice).
- Any backend change.

## Execution

One branch (`feature/nav-grouped-sidebar`), subagent-driven. Likely three
tasks: (1) `nav.ts` grouped model + `navLeafPaths()` + `nav.spec`; (2)
`app.routes.ts` placeholder rewiring + route-coverage test; (3) `shell.ts`
collapsible rendering + header change + `shell.spec` update.
