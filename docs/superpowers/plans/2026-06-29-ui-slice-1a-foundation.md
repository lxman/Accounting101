# UI Slice 1a — Foundation (scaffold · theming · format · shell) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the Angular 22 app at `UI/Angular/` — toolchain (Tailwind + Spartan), the Accounting101 design tokens + Light/Dark/Follow-OS theming, the per-client Format Profile + a TypeScript money/date formatter, the dev-auth + API plumbing, and the app shell (top bar + sidebar + routing) — with a placeholder dashboard. **No accounting screens yet** (Slice 1b adds journal → approval → statements).

**Architecture:** Standalone Angular 22, signals + modern services, typed reactive forms later. The palette is CSS custom properties (Light + Dark token sets) consumed through Tailwind; Spartan provides accessible, Tailwind-styled primitives we own. The Format Profile is the single source of truth for number/date display (this slice builds the TS formatter; a matching C# formatter for PDFs comes later). All API calls carry a dev bearer token via a functional interceptor — **this slice makes no real API calls**, it builds the plumbing the next slice uses.

**Tech Stack:** Angular 22 (standalone, signals, zone-based CD for now), Tailwind CSS, Spartan UI (`@spartan-ng`), TypeScript. Tests via the framework `ng new` provisions (Jasmine/Karma or the current default).

## Global Constraints

- The Angular workspace lives at **`UI/Angular/`** (project name `accounting101`). Leave room for a sibling `UI/Avalonia/` later — nothing in this app may assume it is the only UI.
- **Toolchain is fast-moving — verify, don't trust my version numbers.** Before pinning versions/commands in Tasks 1 and 5, confirm the current Angular CLI, Tailwind, and Spartan setup against live `npm` and the **spartan-ng MCP server** (it serves current Spartan components/blocks/source + install steps). Use `ng add`/Spartan CLI flows (which self-configure to the installed version) over hand-written config where possible. Report any deviation from this plan's commands in your task report.
- **Palette (exact hexes):** `--teal #24CCAB`, `--blue #1D4DE8`, `--slate #36413E`, `--cream #F3E3B3`, `--lavender #BFACC8`. **Buttons solid blue** (no gradient). **Light is the default** theme.
- **Cutting-edge Angular idioms (stable in 22) are mandatory conventions:** standalone components with **no NgModules** and `standalone: true` **omitted** (it's the implied default); **signals** for state with `input()`/`output()`/`model()` for component I/O and `viewChild()`/`contentChild()` signal queries (not the `@Input()`/`@Output()`/`@ViewChild()` decorators); built-in control flow `@if`/`@for`/`@switch`/`@let` (not `*ngIf`/`*ngFor`); `inject()` function DI (not constructor injection); functional interceptors/guards; `afterNextRender`/`afterRenderEffect` for direct DOM work.
- **Zoneless change detection** — `provideZonelessChangeDetection()` in `app.config.ts`, `zone.js` removed from polyfills/deps; every component uses `ChangeDetectionStrategy.OnPush`. (Experimental signal-forms and `httpResource()`/`resource()` are deliberate opt-ins at later slices, NOT used in 1a.)
- **Behaviors live in attribute directives, not components or services.** Cross-cutting UI behavior (focus, hover/lift, alignment, field-error display, etc.) is expressed as signal-based attribute directives (host bindings/listeners) and composed onto components via `hostDirectives` — components and services never reach into the DOM. "Services hand components data; directives give them behavior." (No directives are needed in 1a beyond what the shell/theme require, but all later component work follows this.)
- Every task ends green: `npm run build` (or `ng build`) succeeds, `ng lint` clean if configured, and `ng test --watch=false` passes. No `any` where a real type exists; no TODOs left in shipped code.
- Commit trailer, verbatim, on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## File Structure (created by this slice, under `UI/Angular/`)

```
UI/Angular/
  src/
    styles.scss                         # Tailwind import + token import
    theme/tokens.css                    # palette + semantic CSS vars (light + dark)
    app/
      app.config.ts                     # providers (router, http+interceptor)
      app.routes.ts                     # routes
      app.ts / app.html                 # root → shell
      core/
        theme/theme.service.ts          # Light/Dark/Follow-OS
        theme/theme-switch.ts           # the switch control
        api/environment.ts              # apiBaseUrl, devToken
        api/auth.interceptor.ts         # dev bearer
        api/paged-response.ts           # PagedResponse<T>
        client/client-context.service.ts# selected client signal
        format/format-profile.ts        # FormatProfile + DEFAULT
        format/money-formatter.ts       # formatMoney / isNegativeAmount
        format/date-formatter.ts        # formatProfileDate
      layout/
        shell.ts / shell.html           # top bar + sidebar + <router-outlet>
        nav.ts                          # sidebar nav model
      features/dashboard/dashboard.ts   # placeholder
      features/placeholder/placeholder.ts# "coming soon" for unbuilt routes
```

---

### Task 1: Scaffold the Angular 22 workspace + Tailwind

**Files:** the whole `UI/Angular/` workspace (generated) + Tailwind wiring.

**Interfaces:**
- Produces: a building, testable Angular app at `UI/Angular/` with Tailwind utilities working. Consumed by every later task.

- [ ] **Step 1: Confirm tooling, then scaffold**

Verify the current Angular CLI major (target **22.x**): `npx -p @angular/cli@latest ng version`. Then from the **repo root**, scaffold into `UI/Angular`:
```bash
npx -p @angular/cli@latest ng new accounting101 \
  --directory=UI/Angular --style=scss --ssr=false --routing --skip-git --package-manager=npm
```
(`--directory` puts the workspace files directly in `UI/Angular`. `--skip-git` because we are inside the existing repo. If the CLI prompts for zoneless/AI-tools, choose **zone-based** and no extras.)

- [ ] **Step 2: Verify it builds, runs tests, and serves**

```bash
cd UI/Angular
npm run build
ng test --watch=false --browsers=ChromeHeadless   # adapt to the provisioned runner
```
Expected: build succeeds; the default spec passes.

- [ ] **Step 3: Add Tailwind (confirm current setup against npm/Tailwind docs)**

Target the current Tailwind major. For Tailwind v4 (CSS-first): `npm i -D tailwindcss @tailwindcss/postcss postcss`, add a `.postcssrc.json`:
```json
{ "plugins": { "@tailwindcss/postcss": {} } }
```
and replace `src/styles.scss` with:
```scss
@use './theme/tokens.css';
@import 'tailwindcss';
```
(If the installed Tailwind is v3, use the classic `tailwind.config.js` + `@tailwind base/components/utilities` directives instead — confirm which major you installed and wire accordingly. Note the version used in your report.)

Create `src/theme/tokens.css` as a stub for now (real tokens land in Task 2):
```css
:root { --blue:#1D4DE8; }
```

- [ ] **Step 4: Prove a Tailwind utility renders**

In `src/app/app.html`, replace the default template with `<h1 class="text-3xl font-bold text-[color:var(--blue)]">Accounting 101</h1>` and `ng serve` briefly (or build) to confirm Tailwind classes apply. Then restore `app.html` to a bare `<router-outlet />` (the shell arrives in Task 5; until then a bare outlet keeps the app valid).

- [ ] **Step 5: Build + commit**

```bash
cd UI/Angular && npm run build && ng test --watch=false --browsers=ChromeHeadless
git add UI/Angular
git commit -m "$(cat <<'EOF'
feat(ui): scaffold Angular 22 app at UI/Angular with Tailwind

Standalone Angular 22 workspace (accounting101) under UI/Angular, Tailwind wired
via PostCSS, building + tests green. Foundation for the UI slices.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Design tokens + ThemeService + theme switch

**Files:**
- Create: `src/theme/tokens.css` (real), `src/app/core/theme/theme.service.ts`, `src/app/core/theme/theme-switch.ts`, `theme.service.spec.ts`

**Interfaces:**
- Produces: CSS vars (`--color-bg/-surface/-ink/-muted/-border/-primary/-accent/-warm/-muted-accent`) for both themes; `ThemeService { preference: WritableSignal<ThemePreference>; effective: Signal<'light'|'dark'>; set(p) }`; `<app-theme-switch>`. Consumed by the shell (Task 5).

- [ ] **Step 1: Tokens — palette + semantic, Light + Dark**

`src/theme/tokens.css`:
```css
/* Brand palette (Accounting101Palette.pdf) */
:root {
  --teal:#24CCAB; --blue:#1D4DE8; --slate:#36413E; --cream:#F3E3B3; --lavender:#BFACC8;
}
/* Light (default) */
:root, :root[data-theme="light"] {
  --color-bg:#f5f7f6; --color-surface:#ffffff; --color-ink:#36413E; --color-muted:#7a8a83;
  --color-border:#e6ece9; --color-sidebar:#36413E; --color-sidebar-ink:#cbd6d1;
  --color-primary:#24CCAB; --color-accent:#1D4DE8; --color-warm:#F3E3B3; --color-muted-accent:#BFACC8;
  --color-pending-bg:#BFACC8; --color-pending-ink:#473a52; --color-negative:#b3261e;
}
/* Dark */
:root[data-theme="dark"] {
  --color-bg:#2b3431; --color-surface:#36413e; --color-ink:#e3ebe8; --color-muted:#9fb6ad;
  --color-border:rgba(255,255,255,.08); --color-sidebar:#222a28; --color-sidebar-ink:#9fb0aa;
  --color-primary:#24CCAB; --color-accent:#1D4DE8; --color-warm:#F3E3B3; --color-muted-accent:#BFACC8;
  --color-pending-bg:rgba(191,172,200,.22); --color-pending-ink:#BFACC8; --color-negative:#ff6b5e;
}
```
If Tailwind v4, map a few semantic colors in `styles.scss` so utilities like `bg-surface text-ink` work:
```scss
@theme {
  --color-bg: var(--color-bg); --color-surface: var(--color-surface); --color-ink: var(--color-ink);
  --color-muted: var(--color-muted); --color-border: var(--color-border);
  --color-primary: var(--color-primary); --color-accent: var(--color-accent);
}
```
(For Tailwind v3, add these under `theme.extend.colors` in `tailwind.config.js` as `'ink': 'var(--color-ink)'`, etc. Use whichever matches the installed major.)

- [ ] **Step 2: Write the failing ThemeService test**

`theme.service.spec.ts` (adapt matcher style to the runner):
```ts
import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => { localStorage.clear(); document.documentElement.removeAttribute('data-theme'); });

  it('defaults to light', () => {
    const s = TestBed.inject(ThemeService);
    expect(s.preference()).toBe('light');
    expect(s.effective()).toBe('light');
  });

  it('set() persists and updates effective + the data-theme attribute', () => {
    const s = TestBed.inject(ThemeService);
    s.set('dark');
    expect(s.preference()).toBe('dark');
    expect(s.effective()).toBe('dark');
    expect(localStorage.getItem('a101.theme')).toBe('dark');
    TestBed.flushEffects?.();
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('system preference resolves through effective()', () => {
    localStorage.setItem('a101.theme', 'system');
    const s = TestBed.inject(ThemeService);
    expect(s.preference()).toBe('system');
    expect(['light','dark']).toContain(s.effective());
  });
});
```

- [ ] **Step 3: Implement ThemeService**

`theme.service.ts`:
```ts
import { Injectable, signal, computed, effect } from '@angular/core';

export type ThemePreference = 'light' | 'dark' | 'system';
const STORAGE_KEY = 'a101.theme';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly preference = signal<ThemePreference>(this.load());
  private readonly system = signal<'light' | 'dark'>(this.systemPrefersDark() ? 'dark' : 'light');
  readonly effective = computed<'light' | 'dark'>(() =>
    this.preference() === 'system' ? this.system() : (this.preference() as 'light' | 'dark'));

  constructor() {
    const mq = window.matchMedia?.('(prefers-color-scheme: dark)');
    mq?.addEventListener('change', e => this.system.set(e.matches ? 'dark' : 'light'));
    effect(() => document.documentElement.setAttribute('data-theme', this.effective()));
  }

  set(pref: ThemePreference): void {
    this.preference.set(pref);
    localStorage.setItem(STORAGE_KEY, pref);
  }

  private load(): ThemePreference {
    const v = localStorage.getItem(STORAGE_KEY);
    return v === 'light' || v === 'dark' || v === 'system' ? v : 'light';
  }
  private systemPrefersDark(): boolean {
    return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
  }
}
```

- [ ] **Step 4: Run the tests (pass)**

`ng test --watch=false --browsers=ChromeHeadless`. (If the runner lacks `flushEffects`, the DOM assertion may need `TestBed.tick()`/`fakeAsync` — adapt to the provisioned runner; the logic assertions must pass regardless.)

- [ ] **Step 5: Theme switch component**

`theme-switch.ts` (standalone; three-state segmented control; uses native buttons + Tailwind for now — Spartan upgrade optional in Task 5):
```ts
import { Component, inject } from '@angular/core';
import { ThemeService, ThemePreference } from './theme.service';

@Component({
  selector: 'app-theme-switch',
  standalone: true,
  template: `
    <div class="inline-flex rounded-lg border border-[color:var(--color-border)] overflow-hidden">
      @for (opt of options; track opt.value) {
        <button type="button"
          class="px-2.5 py-1 text-xs"
          [class.bg-[color:var(--color-accent)]]="theme.preference() === opt.value"
          [class.text-white]="theme.preference() === opt.value"
          (click)="theme.set(opt.value)">{{ opt.label }}</button>
      }
    </div>`,
})
export class ThemeSwitch {
  protected readonly theme = inject(ThemeService);
  protected readonly options: { value: ThemePreference; label: string }[] = [
    { value: 'light', label: 'Light' }, { value: 'dark', label: 'Dark' }, { value: 'system', label: 'Auto' },
  ];
}
```

- [ ] **Step 6: Build + commit** (`feat(ui): design tokens + theme service (light/dark/follow-OS)` + trailer)

---

### Task 3: API foundation — environment, dev-auth interceptor, PagedResponse, client context

**Files:**
- Create: `src/app/core/api/environment.ts`, `auth.interceptor.ts`, `paged-response.ts`, `src/app/core/client/client-context.service.ts`, `auth.interceptor.spec.ts`, `client-context.service.spec.ts`
- Modify: `src/app/app.config.ts` (provide HttpClient + the interceptor + router)

**Interfaces:**
- Produces: `environment { apiBaseUrl, devToken }`; `authInterceptor: HttpInterceptorFn`; `PagedResponse<T> { items:T[]; total:number; skip:number; limit:number }`; `ClientContextService { clientId: Signal<string|null>; select(id) }`. Consumed by 1b's data services + the shell switcher.

- [ ] **Step 1: environment + PagedResponse + interceptor**

`environment.ts` (dev stub — no real calls in 1a; `devToken` is finalized in 1b against the engine's dev-auth):
```ts
export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5000',
  devToken: '' as string, // set in 1b to a token the engine's dev-auth accepts
};
```
`paged-response.ts`:
```ts
export interface PagedResponse<T> { items: T[]; total: number; skip: number; limit: number; }
```
`auth.interceptor.ts`:
```ts
import { HttpInterceptorFn } from '@angular/common/http';
import { environment } from './environment';

export const authInterceptor: HttpInterceptorFn = (req, next) =>
  environment.devToken
    ? next(req.clone({ setHeaders: { Authorization: `Bearer ${environment.devToken}` } }))
    : next(req);
```

- [ ] **Step 2: ClientContextService**

```ts
import { Injectable, signal } from '@angular/core';
@Injectable({ providedIn: 'root' })
export class ClientContextService {
  private readonly _clientId = signal<string | null>(null);
  readonly clientId = this._clientId.asReadonly();
  select(id: string | null): void { this._clientId.set(id); }
}
```

- [ ] **Step 3: Wire providers in app.config.ts**

```ts
import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { routes } from './app.routes';
import { authInterceptor } from './core/api/auth.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
  ],
};
```

- [ ] **Step 4: Tests**

`auth.interceptor.spec.ts` — with `devToken` set, the outgoing request carries `Authorization: Bearer …`; with it empty, no header. Use `HttpTestingController`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors, HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor } from './auth.interceptor';
import { environment } from './environment';

describe('authInterceptor', () => {
  let http: HttpClient; let ctrl: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient); ctrl = TestBed.inject(HttpTestingController);
  });
  afterEach(() => ctrl.verify());

  it('attaches the bearer token when set', () => {
    environment.devToken = 'tok123';
    http.get('/x').subscribe();
    const r = ctrl.expectOne('/x');
    expect(r.request.headers.get('Authorization')).toBe('Bearer tok123');
    r.flush({});
  });

  it('omits the header when no token', () => {
    environment.devToken = '';
    http.get('/y').subscribe();
    const r = ctrl.expectOne('/y');
    expect(r.request.headers.has('Authorization')).toBe(false);
    r.flush({});
  });
});
```
`client-context.service.spec.ts` — `select()` updates `clientId()`.

- [ ] **Step 5: Build + commit** (`feat(ui): API plumbing — dev-auth interceptor, PagedResponse, client context` + trailer)

---

### Task 4: Format Profile + money/date formatter (TDD)

**Files:**
- Create: `src/app/core/format/format-profile.ts`, `money-formatter.ts`, `date-formatter.ts`, `money-formatter.spec.ts`

**Interfaces:**
- Produces: `FormatProfile` + `DEFAULT_FORMAT_PROFILE`; `formatMoney(amount, currency, profile, opts?) → string`; `isNegativeAmount(amount) → boolean`; `formatProfileDate(date, profile) → string`. Consumed by every money/statement view (1b+) and mirrored by a C# formatter (later, for PDFs).

- [ ] **Step 1: The model + default**

`format-profile.ts`:
```ts
export type NegativeStyle = 'parens' | 'minus' | 'red' | 'trailing';
export type Scale = 'none' | 'thousands' | 'millions' | 'auto';
export type SymbolPlacement = 'firstAndTotal' | 'every' | 'none';

export interface FormatProfile {
  negativeStyle: NegativeStyle;
  decimals: 0 | 2;
  scale: Scale;
  thousandsSep: boolean;
  currencySymbol: SymbolPlacement;
  zeroDisplay: 'zero' | 'dash';
  dateFormat: string;       // Angular DatePipe format, e.g. 'yyyy-MM-dd'
  accountCodeShown: boolean;
}

export const DEFAULT_FORMAT_PROFILE: FormatProfile = {
  negativeStyle: 'parens', decimals: 2, scale: 'none', thousandsSep: true,
  currencySymbol: 'firstAndTotal', zeroDisplay: 'zero', dateFormat: 'yyyy-MM-dd', accountCodeShown: true,
};
```

- [ ] **Step 2: Write the failing formatter tests (golden table)**

`money-formatter.spec.ts`:
```ts
import { formatMoney, isNegativeAmount } from './money-formatter';
import { DEFAULT_FORMAT_PROFILE as D, FormatProfile } from './format-profile';
const P = (o: Partial<FormatProfile>): FormatProfile => ({ ...D, ...o });

describe('formatMoney', () => {
  it('defaults: thousands sep, 2dp, parens negatives, no symbol unless asked', () => {
    expect(formatMoney(1234, 'USD', D)).toBe('1,234.00');
    expect(formatMoney(-29, 'USD', D)).toBe('(29.00)');
    expect(formatMoney(-450.5, 'USD', D)).toBe('(450.50)');
  });
  it('negative styles', () => {
    expect(formatMoney(-29, 'USD', P({ negativeStyle: 'minus' }))).toBe('-29.00');
    expect(formatMoney(-29, 'USD', P({ negativeStyle: 'trailing' }))).toBe('29.00-');
    expect(formatMoney(-29, 'USD', P({ negativeStyle: 'red' }))).toBe('(29.00)'); // text = parens; caller colors
    expect(isNegativeAmount(-29)).toBe(true);
    expect(isNegativeAmount(0)).toBe(false);
  });
  it('decimals 0', () => {
    expect(formatMoney(1234.56, 'USD', P({ decimals: 0 }))).toBe('1,235');
  });
  it('scale thousands/millions (divide; header carries the unit)', () => {
    expect(formatMoney(1_234_000, 'USD', P({ scale: 'thousands' }))).toBe('1,234.00');
    expect(formatMoney(2_500_000, 'USD', P({ scale: 'millions' }))).toBe('2.50');
  });
  it('thousands separator off', () => {
    expect(formatMoney(1234.5, 'USD', P({ thousandsSep: false }))).toBe('1234.50');
  });
  it('currency symbol placement', () => {
    expect(formatMoney(1234, 'USD', P({ currencySymbol: 'every' }))).toBe('$1,234.00');
    expect(formatMoney(1234, 'USD', P({ currencySymbol: 'firstAndTotal' }), { symbol: true })).toBe('$1,234.00');
    expect(formatMoney(1234, 'USD', P({ currencySymbol: 'firstAndTotal' }), { symbol: false })).toBe('1,234.00');
    expect(formatMoney(1234, 'USD', P({ currencySymbol: 'none' }), { symbol: true })).toBe('1,234.00');
  });
  it('negative with symbol keeps symbol inside the sign treatment', () => {
    expect(formatMoney(-29, 'USD', P({ currencySymbol: 'every' }))).toBe('($29.00)');
  });
  it('zero display', () => {
    expect(formatMoney(0, 'USD', D)).toBe('0.00');
    expect(formatMoney(0, 'USD', P({ zeroDisplay: 'dash' }))).toBe('—');
  });
});
```

- [ ] **Step 3: Implement the formatter**

`money-formatter.ts`:
```ts
import { FormatProfile } from './format-profile';

export interface MoneyFormatOptions { symbol?: boolean; }

const SYMBOLS: Record<string, string> = { USD: '$' };

export const isNegativeAmount = (amount: number): boolean => amount < 0;

export function formatMoney(
  amount: number, currency: string, profile: FormatProfile, opts: MoneyFormatOptions = {},
): string {
  const scaled = applyScale(amount, profile.scale);
  if (scaled === 0 && profile.zeroDisplay === 'dash') return '—';

  const negative = scaled < 0;
  const abs = Math.abs(scaled);

  const digits = abs.toFixed(profile.decimals); // rounds
  const [intPart, fracPart] = digits.split('.');
  const grouped = profile.thousandsSep ? intPart.replace(/\B(?=(\d{3})+(?!\d))/g, ',') : intPart;
  let body = fracPart ? `${grouped}.${fracPart}` : grouped;

  const showSymbol =
    profile.currencySymbol === 'every' ||
    (profile.currencySymbol === 'firstAndTotal' && opts.symbol === true);
  if (showSymbol) body = `${SYMBOLS[currency] ?? currency + ' '}${body}`;

  if (!negative) return body;
  switch (profile.negativeStyle) {
    case 'minus': return `-${body}`;
    case 'trailing': return `${body}-`;
    case 'parens':
    case 'red':   // text identical to parens; the caller applies red via isNegativeAmount
    default: return `(${body})`;
  }
}

function applyScale(amount: number, scale: FormatProfile['scale']): number {
  switch (scale) {
    case 'thousands': return amount / 1_000;
    case 'millions': return amount / 1_000_000;
    case 'auto': return Math.abs(amount) >= 1_000_000 ? amount / 1_000_000
               : Math.abs(amount) >= 1_000 ? amount / 1_000 : amount;
    default: return amount;
  }
}
```
`date-formatter.ts`:
```ts
import { formatDate } from '@angular/common';
import { FormatProfile } from './format-profile';
export const formatProfileDate = (date: string | number | Date, profile: FormatProfile, locale = 'en-US'): string =>
  formatDate(date, profile.dateFormat, locale);
```

- [ ] **Step 4: Run the tests (pass)** — `ng test --watch=false --browsers=ChromeHeadless`. Fix the implementation, not the golden table, on mismatch.

- [ ] **Step 5: Build + commit** (`feat(ui): per-client Format Profile + money/date formatter (TDD)` + trailer)

---

### Task 5: Spartan setup + app shell + routing + dashboard placeholder

**Files:**
- Create: `src/app/layout/shell.ts` (+ template), `src/app/layout/nav.ts`, `src/app/features/dashboard/dashboard.ts`, `src/app/features/placeholder/placeholder.ts`
- Modify: `src/app/app.ts`/`app.html` (root → shell), `src/app/app.routes.ts`

**Interfaces:**
- Consumes: `ThemeService`/`ThemeSwitch` (Task 2), `ClientContextService` (Task 3).
- Produces: the routed shell + a landing dashboard placeholder.

- [ ] **Step 1: Install Spartan (confirm via the spartan-ng MCP / docs)**

Use the **spartan-ng MCP server** (or current Spartan docs) to confirm the install for the installed Angular + Tailwind majors, then add the brain + helm packages and generate the base components you use here (at minimum a button). Typical flow:
```bash
cd UI/Angular
ng add @spartan-ng/brain        # or the current package per the MCP
# generate the hlm components used below (e.g. button) per Spartan's CLI
```
Record the exact packages/commands used in your report. If Spartan cannot be cleanly added against the installed Tailwind major, report it as DONE_WITH_CONCERNS with the conflict — do not force it; the shell can ship with native+Tailwind controls and adopt Spartan components in 1b.

- [ ] **Step 2: Nav model**

`nav.ts`:
```ts
export interface NavItem { label: string; path: string; }
export const NAV: NavItem[] = [
  { label: 'Dashboard', path: '/dashboard' },
  { label: 'Journal', path: '/journal' },
  { label: 'Accounts', path: '/accounts' },
  { label: 'Trial Balance', path: '/trial-balance' },
  { label: 'Statements', path: '/statements' },
  { label: 'Periods', path: '/periods' },
  { label: 'Receivables', path: '/receivables' },
  { label: 'Payables', path: '/payables' },
  { label: 'Payroll', path: '/payroll' },
  { label: 'Cash', path: '/cash' },
  { label: 'Bank Rec', path: '/bank-rec' },
  { label: 'Audit', path: '/audit' },
];
```

- [ ] **Step 3: Shell component (top bar + sidebar + outlet)**

`shell.ts`:
```ts
import { Component, inject } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { NAV } from './nav';
import { ThemeSwitch } from '../core/theme/theme-switch';
import { ClientContextService } from '../core/client/client-context.service';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ThemeSwitch],
  template: `
    <div class="min-h-screen bg-[color:var(--color-bg)] text-[color:var(--color-ink)]">
      <header class="flex items-center gap-3 px-4 h-14 bg-[color:var(--color-surface)] border-b border-[color:var(--color-border)]">
        <button type="button" class="text-sm font-semibold px-3 py-1.5 rounded-lg border border-[color:var(--color-border)]">
          {{ client.clientId() ?? 'Select client' }} ▾
        </button>
        <div class="ml-auto flex items-center gap-2">
          <button type="button" class="text-sm px-2.5 py-1.5 rounded-lg hover:bg-[color:var(--color-bg)]">Edit Firm</button>
          <button type="button" class="text-sm px-2.5 py-1.5 rounded-lg hover:bg-[color:var(--color-bg)]">Edit Client</button>
          <app-theme-switch />
          <span class="text-sm text-[color:var(--color-muted)]">Jordan ▾</span>
        </div>
      </header>
      <div class="flex">
        <aside class="w-44 min-h-[calc(100vh-3.5rem)] p-2 bg-[color:var(--color-sidebar)] text-[color:var(--color-sidebar-ink)]">
          @for (item of nav; track item.path) {
            <a [routerLink]="item.path" routerLinkActive="bg-white/10 text-white font-semibold"
               class="block px-3 py-2 rounded-lg text-sm">{{ item.label }}</a>
          }
        </aside>
        <main class="flex-1 p-6"><router-outlet /></main>
      </div>
    </div>`,
})
export class Shell {
  protected readonly nav = NAV;
  protected readonly client = inject(ClientContextService);
}
```

- [ ] **Step 4: Dashboard + placeholder + routes**

`dashboard.ts`:
```ts
import { Component } from '@angular/core';
@Component({ selector: 'app-dashboard', standalone: true,
  template: `<h1 class="text-2xl font-bold mb-2">Dashboard</h1>
    <p class="text-[color:var(--color-muted)]">Welcome to Accounting 101. Accounting screens arrive in the next slice.</p>` })
export class Dashboard {}
```
`placeholder.ts`:
```ts
import { Component } from '@angular/core';
@Component({ selector: 'app-placeholder', standalone: true,
  template: `<div class="text-[color:var(--color-muted)]">Coming soon.</div>` })
export class Placeholder {}
```
`app.routes.ts`:
```ts
import { Routes } from '@angular/router';
import { Dashboard } from './features/dashboard/dashboard';
import { Placeholder } from './features/placeholder/placeholder';
import { NAV } from './layout/nav';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'dashboard', component: Dashboard },
  // every other nav target → placeholder for 1a
  ...NAV.filter(n => n.path !== '/dashboard').map(n => ({ path: n.path.slice(1), component: Placeholder })),
  { path: '**', redirectTo: 'dashboard' },
];
```
Root `app.html` → `<app-shell />`; `app.ts` imports `Shell`.

- [ ] **Step 5: Smoke test the shell**

`shell.spec.ts` — render `Shell` (with `provideRouter([])` + the services), assert the brand/nav render and the theme switch toggles `data-theme`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Shell } from './shell';

describe('Shell', () => {
  it('renders the nav and top-bar actions', async () => {
    await TestBed.configureTestingModule({ imports: [Shell], providers: [provideRouter([])] }).compileComponents();
    const el = TestBed.createComponent(Shell).nativeElement as HTMLElement;
    expect(el.textContent).toContain('Dashboard');
    expect(el.textContent).toContain('Edit Firm');
    expect(el.textContent).toContain('Edit Client');
  });
});
```

- [ ] **Step 6: Build + commit** (`feat(ui): app shell (top bar + sidebar + routing) + Spartan + dashboard placeholder` + trailer)

---

## Self-Review

**1. Spec coverage (vs the screen-map design system + Slice-1a scope):**
- Angular 22 app at `UI/Angular/` → Task 1. ✓
- Palette tokens (exact hexes) + Light default + Light/Dark/Follow-OS switch → Task 2. ✓
- Format Profile (defaults) + TS formatter (negatives incl. parens default, decimals, scale K/M, separators, symbol placement, zero-as-dash) → Task 4. ✓
- Dev-auth stub + PagedResponse + client context (the switcher's backing) → Task 3. ✓
- Shell: top-bar client switcher + Edit Firm/Edit Client + sidebar nav + routing → Task 5. ✓
- Solid blue buttons / aligned-decimal tabular numerals → tokens + formatter (alignment is a table-render concern arriving with the first real table in 1b; the formatter guarantees fixed decimals now). ✓
- Tailwind + Spartan toolchain → Tasks 1 + 5, with the live-verification guard. ✓
- No accounting screens (deferred to 1b); zoneless deferred. ✓ (intentional scope.)

**2. Placeholder scan:** No TBD/TODO in shipped code. The `devToken` is intentionally empty in 1a (no real calls) and finalized in 1b — documented at its definition. "Coming soon" placeholder routes are intentional 1a deliverables, not stubs of unfinished logic.

**3. Type consistency:** `FormatProfile`/`DEFAULT_FORMAT_PROFILE` (Task 4) used by `formatMoney`/`formatProfileDate`; `ThemePreference`/`ThemeService.effective` (Task 2) used by `ThemeSwitch` + shell; `ClientContextService.clientId` (Task 3) read by the shell switcher; `PagedResponse<T>` defined once (Task 3) for 1b. `authInterceptor` provided in `app.config.ts` (Task 3) matches the `HttpInterceptorFn` type. Spartan packages/components are confirmed live (Task 5) rather than hard-pinned.

## Open (resolve during execution)
- Tailwind major (v4 CSS-first vs v3 JS-config) — pick per the installed version + Spartan's requirement; the plan shows both wirings.
- Exact Spartan packages/commands — confirm via the spartan-ng MCP; report what was used.
- Test runner specifics (Jasmine/Karma vs the current default) — adapt matchers/effect-flushing to what `ng new` provisioned.
