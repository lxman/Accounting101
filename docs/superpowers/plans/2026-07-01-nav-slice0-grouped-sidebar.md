# Slice 0 — Grouped, Collapsible Sidebar — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the flat 13-item sidebar with the five-section, collapsible north-star tree (full tree; unbuilt destinations route to `Placeholder`), and move the dead header Firm/Client buttons into an Administration section.

**Architecture:** A grouped `NavSection[]`/`NavLink` model in `nav.ts` with a `navLeafPaths()` flattener. `app.routes.ts` keeps the built route trees and derives `Placeholder` routes for every nav leaf not already served. `shell.ts` renders sections + nested children with in-memory collapse state and auto-expand-of-active.

**Tech Stack:** Angular 22 (standalone, zoneless, OnPush), Spartan NG helm, vitest.

## Global Constraints

- Angular 22, standalone, zoneless, OnPush; Spartan NG helm components.
- Tests via `cd UI/Angular && npx ng test --watch=false`.
- No backend changes. No capability/role filtering (that is Slice B) — everyone sees everything.
- `environment.ts` (devClientId) and IDE csproj/slnx churn stay UNCOMMITTED — stage explicit paths only, never `git add -A`.
- Keep existing conventions: `RouterLink`, longest-prefix active highlighting, persistent shell hosting `<router-outlet/>`.
- Paths are fixed by the spec tree — use them verbatim.

---

### Task 1: Grouped nav model + `navLeafPaths()`

**Files:**
- Modify: `UI/Angular/src/app/layout/nav.ts` (full rewrite)
- Test: `UI/Angular/src/app/layout/nav.spec.ts` (new)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `export interface NavLink { label: string; path: string; children?: NavLink[]; }`
  - `export interface NavSection { label: string; items: NavLink[]; }`
  - `export const NAV: NavSection[]`
  - `export function navLeafPaths(): string[]` — every `path` in the tree (each parent contributes its own path AND recurses into `children`), in depth-first order, no duplicates.

- [ ] **Step 1: Write the failing test**

Create `UI/Angular/src/app/layout/nav.spec.ts`:

```ts
import { NAV, navLeafPaths, NavSection } from './nav';

describe('nav', () => {
  it('has the five north-star sections in order', () => {
    expect(NAV.map((s: NavSection) => s.label)).toEqual([
      'Overview', 'General Ledger', 'Subledgers', 'Assurance', 'Administration',
    ]);
  });

  it('navLeafPaths returns every path (parents + children), no duplicates', () => {
    const paths = navLeafPaths();
    expect(new Set(paths).size).toBe(paths.length); // no dupes
    expect(paths).toEqual(expect.arrayContaining([
      '/dashboard',
      '/journal', '/journal/approvals', '/accounts', '/trial-balance', '/statements', '/periods',
      '/receivables', '/payables', '/payroll',
      '/cash', '/cash/reconciliation', '/fixed-assets',
      '/audit', '/audit/trail', '/audit/verify', '/audit/reconciliations',
      '/reports', '/reports/budgets',
      '/admin/users', '/admin/firm', '/admin/client', '/admin/fiscal', '/admin/posting-accounts',
    ]));
    expect(paths.length).toBe(24);
  });

  it('nests Bank Reconciliation under Cash & Banking', () => {
    const subledgers = NAV.find((s) => s.label === 'Subledgers')!;
    const cash = subledgers.items.find((i) => i.path === '/cash')!;
    expect(cash.label).toBe('Cash & Banking');
    expect(cash.children?.map((c) => c.path)).toEqual(['/cash/reconciliation']);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `navLeafPaths` not exported / NAV shape mismatch.

- [ ] **Step 3: Rewrite `nav.ts`**

Replace the entire file with:

```ts
export interface NavLink { label: string; path: string; children?: NavLink[]; }
export interface NavSection { label: string; items: NavLink[]; }

export const NAV: NavSection[] = [
  { label: 'Overview', items: [
    { label: 'Dashboard', path: '/dashboard' },
  ] },
  { label: 'General Ledger', items: [
    { label: 'Journal', path: '/journal' },
    { label: 'Approvals', path: '/journal/approvals' },
    { label: 'Chart of Accounts', path: '/accounts' },
    { label: 'Trial Balance', path: '/trial-balance' },
    { label: 'Financial Statements', path: '/statements' },
    { label: 'Period Close', path: '/periods' },
  ] },
  { label: 'Subledgers', items: [
    { label: 'Receivables', path: '/receivables' },
    { label: 'Payables', path: '/payables' },
    { label: 'Payroll', path: '/payroll' },
    { label: 'Cash & Banking', path: '/cash', children: [
      { label: 'Bank Reconciliation', path: '/cash/reconciliation' },
    ] },
    { label: 'Fixed Assets', path: '/fixed-assets' },
  ] },
  { label: 'Assurance', items: [
    { label: 'Audit', path: '/audit', children: [
      { label: 'Audit Trail', path: '/audit/trail' },
      { label: 'Verify Integrity', path: '/audit/verify' },
      { label: 'Subledger Reconciliations', path: '/audit/reconciliations' },
    ] },
    { label: 'Reports', path: '/reports', children: [
      { label: 'Budgets', path: '/reports/budgets' },
    ] },
  ] },
  { label: 'Administration', items: [
    { label: 'Users & Roles', path: '/admin/users' },
    { label: 'Firm', path: '/admin/firm' },
    { label: 'Client', path: '/admin/client' },
    { label: 'Fiscal settings', path: '/admin/fiscal' },
    { label: 'Posting accounts', path: '/admin/posting-accounts' },
  ] },
];

export function navLeafPaths(): string[] {
  const out: string[] = [];
  const walk = (links: NavLink[]): void => {
    for (const l of links) {
      out.push(l.path);
      if (l.children) walk(l.children);
    }
  };
  for (const section of NAV) walk(section.items);
  return out;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS (nav.spec green). Note: `app.routes.ts` and `shell.ts` will now have TYPE errors against the new NAV shape — that is expected; Tasks 2 and 3 fix them. If the test runner fails to build the whole app due to those, that is acceptable at this step ONLY if nav.spec itself is green in isolation; prefer to proceed to Task 2 promptly. (If the runner cannot report nav.spec because the app won't compile, run just this spec file: `npx ng test --watch=false --include='**/nav.spec.ts'` — if unsupported, proceed and rely on Task 3 to restore a green full build.)

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/layout/nav.ts UI/Angular/src/app/layout/nav.spec.ts
git commit -m "feat(nav): grouped NavSection/NavLink model + navLeafPaths()"
```

---

### Task 2: Route rewiring (placeholders from `navLeafPaths()`)

**Files:**
- Modify: `UI/Angular/src/app/app.routes.ts:110` (the placeholder-derivation line) and its `NAV` import.
- Test: `UI/Angular/src/app/app.routes.spec.ts` (new)

**Interfaces:**
- Consumes: `navLeafPaths()` from Task 1.
- Produces: a `routes` array where every nav leaf path resolves to a component (built tree or `Placeholder`).

- [ ] **Step 1: Write the failing test**

Create `UI/Angular/src/app/app.routes.spec.ts`:

```ts
import { routes } from './app.routes';
import { navLeafPaths } from './layout/nav';

// Collect every concrete (non-param, non-wildcard) path the route table can match,
// expanding one level of children with their parent prefix.
function matchablePaths(): Set<string> {
  const set = new Set<string>();
  for (const r of routes) {
    if (!r.path || r.path === '**') continue;
    set.add('/' + r.path);
    for (const c of (r.children ?? [])) {
      if (c.path) set.add('/' + r.path + '/' + c.path);
    }
  }
  return set;
}

describe('app.routes', () => {
  it('resolves every nav leaf path to a route (no dead links)', () => {
    const matchable = matchablePaths();
    const missing = navLeafPaths().filter((p) => !matchable.has(p));
    expect(missing).toEqual([]);
  });

  it('registers placeholder routes for unbuilt destinations', () => {
    const matchable = matchablePaths();
    expect(matchable.has('/periods')).toBe(true);
    expect(matchable.has('/cash')).toBe(true);
    expect(matchable.has('/cash/reconciliation')).toBe(true);
    expect(matchable.has('/admin/users')).toBe(true);
    expect(matchable.has('/reports/budgets')).toBe(true);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — `/periods`, `/cash/reconciliation`, `/admin/*`, etc. not matchable (old filter only produced flat single-segment paths and referenced the old NAV shape).

- [ ] **Step 3: Rewrite the placeholder derivation**

In `app.routes.ts`, the import stays `import { NAV } from './layout/nav';` → change to `import { navLeafPaths } from './layout/nav';`.

Replace the line at `:109-110` (the `// remaining nav targets → placeholder` comment and its `...NAV.filter(...).map(...)`) with:

```ts
  // Every nav leaf not served by a built route tree above → Placeholder.
  ...(() => {
    const built = ['/dashboard', '/journal', '/trial-balance', '/statements', '/accounts', '/receivables', '/payables', '/payroll'];
    const isBuilt = (p: string) => built.some((b) => p === b || p.startsWith(b + '/'));
    return navLeafPaths()
      .filter((p) => !isBuilt(p))
      .map((p) => ({ path: p.slice(1), component: Placeholder }));
  })(),
  { path: '**', redirectTo: 'dashboard' },
```

(Keep all existing explicit route entries above unchanged. `Placeholder` is already imported.)

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS (app.routes.spec green; nav.spec still green). `shell.ts` may still have a type error against the new NAV shape — fixed in Task 3.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/app.routes.ts UI/Angular/src/app/app.routes.spec.ts
git commit -m "feat(nav): derive placeholder routes from navLeafPaths(), full-tree coverage"
```

---

### Task 3: Collapsible shell rendering + header change

**Files:**
- Modify: `UI/Angular/src/app/layout/shell.ts` (template + component logic)
- Test: `UI/Angular/src/app/layout/shell.spec.ts` (update)

**Interfaces:**
- Consumes: `NAV`, `NavSection`, `NavLink`, `navLeafPaths()` from Task 1.
- Produces: the rendered grouped sidebar; no exported API changes.

- [ ] **Step 1: Update the failing test**

Replace `UI/Angular/src/app/layout/shell.spec.ts` with:

```ts
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Shell } from './shell';
import { DevIdentityService } from '../core/api/dev-identity.service';
import { environment } from '../core/api/environment';

describe('Shell', () => {
  async function make() {
    await TestBed.configureTestingModule({
      imports: [Shell],
      providers: [provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  it('renders section headers and Dashboard', async () => {
    const el = (await make()).nativeElement as HTMLElement;
    expect(el.textContent).toContain('General Ledger');
    expect(el.textContent).toContain('Subledgers');
    expect(el.textContent).toContain('Administration');
    expect(el.textContent).toContain('Dashboard');
  });

  it('shows Administration Firm/Client links (moved out of the header)', async () => {
    const el = (await make()).nativeElement as HTMLElement;
    expect(el.textContent).toContain('Firm');
    expect(el.textContent).toContain('Client');
    expect(el.textContent).not.toContain('Edit Firm');
    expect(el.textContent).not.toContain('Edit Client');
  });

  it('shows a nested child under its parent (default expanded)', async () => {
    const el = (await make()).nativeElement as HTMLElement;
    expect(el.textContent).toContain('Bank Reconciliation');
  });

  it('collapsing a section hides its items', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    // find the Administration section header toggle and click it
    const header = Array.from(el.querySelectorAll('[data-testid="nav-section-header"]'))
      .find((h) => h.textContent?.includes('Administration')) as HTMLElement;
    header.click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Posting accounts');
  });

  it('switches the active dev identity from the top bar', async () => {
    const fixture = await make();
    const ids = TestBed.inject(DevIdentityService);
    ids.use(environment.devApprover.sub);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Dev Approver');
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: FAIL — shell still renders the flat nav / old header; `data-testid="nav-section-header"` absent; "Edit Firm" still present.

- [ ] **Step 3: Rewrite `shell.ts`**

Full replacement:

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { ClientContextService } from '../core/client/client-context.service';
import { ThemeSwitch } from '../core/theme/theme-switch';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { DevIdentityService } from '../core/api/dev-identity.service';
import { NAV, NavLink, navLeafPaths } from './nav';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, ThemeSwitch, HlmButton, ...HlmSelectImports],
  template: `
    <div class="min-h-screen bg-background text-foreground">
      <header class="flex items-center gap-3 px-4 h-14 bg-card border-b border-border">
        <button type="button" class="text-sm font-semibold px-3 py-1.5 rounded-lg border border-border">
          {{ client.clientId() ?? 'Select client' }} ▾
        </button>
        <div class="ml-auto flex items-center gap-2">
          <app-theme-switch />
          <div hlmSelect [value]="identity.active().sub" [itemToString]="identityItemToString" (valueChange)="identity.use($any($event))" class="w-44">
            <hlm-select-trigger class="w-44">
              <hlm-select-value placeholder="Acting as…" />
            </hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal>
              @for (id of identity.identities; track id.sub) {
                <hlm-select-item [value]="id.sub">{{ id.name }}</hlm-select-item>
              }
            </hlm-select-content>
          </div>
        </div>
      </header>
      <div class="flex">
        <aside class="w-56 min-h-[calc(100vh-3.5rem)] p-2 bg-sidebar text-sidebar-foreground">
          @for (section of nav; track section.label) {
            <div class="mt-3 first:mt-0">
              <button type="button" data-testid="nav-section-header"
                      (click)="toggle(section.label)"
                      class="w-full flex items-center justify-between px-3 py-1 text-xs uppercase tracking-wide text-muted-foreground">
                <span>{{ section.label }}</span>
                <span>{{ isOpen(section.label) ? '▾' : '▸' }}</span>
              </button>
              @if (isOpen(section.label)) {
                @for (item of section.items; track item.path) {
                  <a [routerLink]="item.path"
                     class="block px-3 py-2 rounded-lg text-sm"
                     [class.bg-sidebar-accent]="activePath() === item.path"
                     [class.text-sidebar-accent-foreground]="activePath() === item.path"
                     [class.font-semibold]="activePath() === item.path">{{ item.label }}</a>
                  @if (item.children && parentOpen(item)) {
                    @for (child of item.children; track child.path) {
                      <a [routerLink]="child.path"
                         class="block pl-6 pr-3 py-1.5 rounded-lg text-sm"
                         [class.bg-sidebar-accent]="activePath() === child.path"
                         [class.text-sidebar-accent-foreground]="activePath() === child.path"
                         [class.font-semibold]="activePath() === child.path">{{ child.label }}</a>
                    }
                  }
                }
              }
            </div>
          }
        </aside>
        <main class="flex-1 p-6"><router-outlet /></main>
      </div>
    </div>`,
})
export class Shell {
  protected readonly nav = NAV;
  protected readonly client = inject(ClientContextService);
  protected readonly identity = inject(DevIdentityService);
  private readonly router = inject(Router);

  private readonly leafPaths = navLeafPaths();
  private readonly collapsed = signal<Set<string>>(new Set());

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  // Highlighted item = longest nav leaf path that prefixes the current URL.
  protected readonly activePath = computed(() => {
    const u = this.url();
    return (
      this.leafPaths
        .filter((p) => u === p || u.startsWith(p + '/'))
        .sort((a, b) => b.length - a.length)[0] ?? null
    );
  });

  toggle(key: string): void {
    this.collapsed.update((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  }

  // A group is open when not explicitly collapsed OR when it contains the active path
  // (so navigating never hides the current page).
  isOpen(sectionLabel: string): boolean {
    if (!this.collapsed().has(sectionLabel)) return true;
    return this.sectionContainsActive(sectionLabel);
  }

  parentOpen(item: NavLink): boolean {
    if (!this.collapsed().has(item.path)) return true;
    const a = this.activePath();
    return a === item.path || (item.children?.some((c) => c.path === a) ?? false);
  }

  private sectionContainsActive(sectionLabel: string): boolean {
    const a = this.activePath();
    if (!a) return false;
    const section = NAV.find((s) => s.label === sectionLabel);
    if (!section) return false;
    return section.items.some((i) => i.path === a || (i.children?.some((c) => c.path === a) ?? false));
  }

  protected readonly identityItemToString = (sub: string): string =>
    `Acting as: ${this.identity.identities.find((i) => i.sub === sub)?.name ?? sub}`;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npx ng test --watch=false`
Expected: PASS — full suite green (nav.spec, app.routes.spec, shell.spec, and all pre-existing specs; total = prior 254 + new nav/routes specs, minus none).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/layout/shell.ts UI/Angular/src/app/layout/shell.spec.ts
git commit -m "feat(nav): collapsible grouped sidebar; move Firm/Client into Administration"
```

## Self-Review

- **Spec coverage:** Task 1 = grouped model + `navLeafPaths()`; Task 2 = full-tree placeholder routing; Task 3 = collapsible rendering + header change. All spec sections covered.
- **Type consistency:** `NavSection`/`NavLink`/`navLeafPaths` names identical across all three tasks and the shell import. `activePath()` returns `string | null`.
- **Placeholder scan:** none — all code shown in full.
- **Known cross-task interlock:** Task 1 intentionally leaves the app non-compiling (shell/routes reference old NAV shape) until Tasks 2–3 land. Task 1's gate is nav.spec green; the full green build is restored by Task 3 Step 4. Reviewers should not treat the interim red build after Task 1 as a Task 1 defect.

## Execution Handoff

Subagent-driven. Three tasks, sequential (Task 2 and 3 both depend on Task 1's exports; Task 3's shell build depends on Task 2's routes compiling).
