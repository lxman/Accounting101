# Receivables Hub + Sub-Navigation (Slice A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Receivables a hub with Invoices/Payments/Customers tabs so each document type has one home; add a Payments list home; share one customer selector; and remove the two scattered Record-payment buttons (strict one place).

**Architecture:** A `ReceivablesShell` parent component (tab nav + `<router-outlet/>`, mirroring `Statements`) with the Receivables routes re-nested under it. A shared `<app-customer-select>` (bound to the service's persisted selection) replaces the inline customer select in the invoice list and powers the new `PaymentList`. No backend changes.

**Tech Stack:** Angular 22 (zoneless, signals, OnPush standalone components), Spartan helm UI, Vitest (`ng test`).

**Spec:** `docs/superpowers/specs/2026-06-30-receivables-hub-ia-design.md`

## Global Constraints

- Angular components: standalone, `ChangeDetectionStrategy.OnPush`, signals (no NgModules/Zone).
- Every customer `hlmSelect` needs `*hlmSelectPortal` on its content AND `[itemToString]` mapping id→name (else it never closes / shows the raw GUID).
- Reuse `money`/`displayDate` from `core/format/display`, `extractProblem` from `core/api/problem-details`, and the persisted selection in `ReceivablesService` (`selectedCustomerId` / `setSelectedCustomer`).
- Run frontend tests: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false [--include='**/<file>.spec.ts']`.
- Commit trailer on every commit, exactly: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit files only — never `environment.ts` or `.claude/`.

## File Structure

- `UI/Angular/src/app/shared/customer-select.ts` (+ `.spec.ts`) — shared customer selector bound to the service selection.
- `UI/Angular/src/app/features/receivables/payment-list.ts` (+ `.spec.ts`) — the Payments home.
- `UI/Angular/src/app/features/receivables/receivables-shell.ts` (+ `.spec.ts`) — hub with the tab nav.
- `UI/Angular/src/app/app.routes.ts` — re-nest Receivables routes under the shell.
- `UI/Angular/src/app/features/receivables/invoice-list.ts` (+ `.spec.ts`) — adopt the shared select, remove Record-payment, skip-reset effect.
- `UI/Angular/src/app/features/receivables/invoice-detail.ts` (+ `.spec.ts`) — remove Record-payment link.

---

### Task 1: Shared `<app-customer-select>`

**Files:**
- Create: `UI/Angular/src/app/shared/customer-select.ts`
- Test: `UI/Angular/src/app/shared/customer-select.spec.ts`

**Interfaces:**
- Consumes: `ReceivablesService` (`customers()`, `selectedCustomerId()`, `setSelectedCustomer(id)`, `customerName(id)`).
- Produces: component `CustomerSelect`, selector `app-customer-select`, public field `toName(id: string): string`.

- [ ] **Step 1: Write the failing test**

Create `customer-select.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CustomerSelect } from './customer-select';
import { ReceivablesService } from '../core/receivables/receivables.service';
import { ClientContextService } from '../core/client/client-context.service';

describe('CustomerSelect', () => {
  it('renders and maps a customer id to its name (itemToString), with id fallback', () => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    const svc = TestBed.inject(ReceivablesService);
    svc.load();
    TestBed.inject(HttpTestingController).expectOne('http://localhost:5000/clients/C1/customers')
      .flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    const f = TestBed.createComponent(CustomerSelect); f.detectChanges();   // render smoke (template compiles)
    const cmp = f.componentInstance as CustomerSelect;
    expect(cmp.toName('cu1')).toBe('Acme Co');
    expect(cmp.toName('nope')).toBe('nope');                                // fallback to id
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/customer-select.spec.ts'`
Expected: FAIL — `CustomerSelect` does not exist.

- [ ] **Step 3: Create the component**

Create `customer-select.ts`:

```ts
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { ReceivablesService } from '../core/receivables/receivables.service';

/** The customer picker shared by the Receivables list tabs. Bound to the service's persisted
 *  per-client selection, so choosing a customer on one tab carries to the others. */
@Component({
  selector: 'app-customer-select',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmSelectImports],
  template: `
    <div hlmSelect [value]="svc.selectedCustomerId()" [itemToString]="toName"
         (valueChange)="svc.setSelectedCustomer($any($event) ?? '')">
      <hlm-select-trigger class="w-64">
        <hlm-select-value placeholder="Select a customer" />
      </hlm-select-trigger>
      <hlm-select-content *hlmSelectPortal>
        @for (c of svc.customers(); track c.id) {
          <hlm-select-item [value]="c.id">{{ c.name }}</hlm-select-item>
        }
      </hlm-select-content>
    </div>
  `,
})
export class CustomerSelect {
  readonly svc = inject(ReceivablesService);
  /** id→name for the trigger (so it shows the name, not the raw GUID); falls back to the id. */
  readonly toName = (id: string): string => this.svc.customerName(id);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/customer-select.spec.ts'`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/shared/customer-select.ts UI/Angular/src/app/shared/customer-select.spec.ts
git commit -m "feat(ui): shared customer-select bound to the persisted selection

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `PaymentList` (the Payments home)

**Files:**
- Create: `UI/Angular/src/app/features/receivables/payment-list.ts`
- Test: `UI/Angular/src/app/features/receivables/payment-list.spec.ts`

**Interfaces:**
- Consumes: `CustomerSelect` (Task 1); `ReceivablesService.listPayments(customerId): Observable<Payment[]>`, `.selectedCustomerId`, `.customers()`, `.load()`; `Payment` type; `money`/`displayDate`; `extractProblem`.
- Produces: component `PaymentList`, selector `app-payment-list`; public `customerId` signal, `payments` signal, `listError` signal, methods `allocated(p)`, `fmtMoney(n)`, `fmtDate(d)`.

- [ ] **Step 1: Write the failing test**

Create `payment-list.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PaymentList } from './payment-list';
import { ClientContextService } from '../../core/client/client-context.service';

function setup() {
  localStorage.clear();
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const payment = (id: string, amount: number, allocated: number) => ({
  id, customerId: 'cu1', date: '2026-06-30', amount, method: 'check',
  allocations: [{ targetId: 'inv1', amount: allocated }], voided: false,
});

describe('PaymentList', () => {
  it('loads the selected customer\'s payments and renders amount/method/allocated/unapplied', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(PaymentList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/payments') && r.params.get('customerId') === 'cu1')
      .flush([payment('p1', 100, 60)]);
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('100.00');   // amount
    expect(text).toContain('check');    // method
    expect(text).toContain('60.00');    // allocated
    expect(text).toContain('40.00');    // unapplied = 100 - 60
  });

  it('Record payment link targets the editor for the selected customer; disabled with none', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(PaymentList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    const link = () => [...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Record payment') as HTMLAnchorElement;
    expect(link().className).toContain('opacity-50');               // disabled-styled, no customer
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/payments') && r.params.get('customerId') === 'cu1').flush([]);
    f.detectChanges();
    expect(link().getAttribute('href')).toContain('/receivables/payments/new');
    expect(link().getAttribute('href')).toContain('customer=cu1');
  });

  it('shows the empty states', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(PaymentList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No customers yet');
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/payment-list.spec.ts'`
Expected: FAIL — `PaymentList` does not exist.

- [ ] **Step 3: Create the component**

Create `payment-list.ts`:

```ts
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { Payment } from '../../core/receivables/receivables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { CustomerSelect } from '../../shared/customer-select';

@Component({
  selector: 'app-payment-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, CustomerSelect],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Payments</h1>
        <app-customer-select />
        <a hlmBtn size="sm" class="ms-auto"
           routerLink="/receivables/payments/new"
           [queryParams]="{ customer: customerId() }"
           [class.pointer-events-none]="!customerId()"
           [class.opacity-50]="!customerId()">
          Record payment
        </a>
      </div>

      @if (svc.customers().length === 0) {
        <p class="text-muted-foreground text-sm">No customers yet — <a routerLink="/receivables/customers" class="underline">add one first</a>.</p>
      } @else if (!customerId()) {
        <p class="text-muted-foreground text-sm">Select a customer to view payments.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (payments().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No payments recorded.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>Date</th><th hlmTh>Amount</th><th hlmTh>Method</th>
                  <th hlmTh>Allocated</th><th hlmTh>Unapplied</th><th hlmTh>Status</th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (p of payments(); track p.id) {
                  <tr hlmTr [class.opacity-50]="p.voided">
                    <td hlmTd>{{ fmtDate(p.date) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(p.amount) }}</td>
                    <td hlmTd>{{ p.method ?? '—' }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(allocated(p)) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(p.amount - allocated(p)) }}</td>
                    <td hlmTd>{{ p.voided ? 'Voided' : 'Active' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      }
    </div>
  `,
})
export class PaymentList {
  readonly svc = inject(ReceivablesService);
  readonly customerId = this.svc.selectedCustomerId;
  readonly listError = signal<string | null>(null);

  readonly payments = toSignal(
    toObservable(this.customerId).pipe(
      switchMap(cid => {
        if (!cid) return of([] as Payment[]);
        this.listError.set(null);
        return this.svc.listPayments(cid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as Payment[]); }),
        );
      }),
    ),
    { initialValue: [] as Payment[] },
  );

  constructor() { this.svc.load(); }

  allocated(p: Payment): number { return p.allocations.reduce((s, a) => s + a.amount, 0); }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/payment-list.spec.ts'`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/receivables/payment-list.ts UI/Angular/src/app/features/receivables/payment-list.spec.ts
git commit -m "feat(ui): payment list (the Payments tab home)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `ReceivablesShell` + route restructure

**Files:**
- Create: `UI/Angular/src/app/features/receivables/receivables-shell.ts`
- Test: `UI/Angular/src/app/features/receivables/receivables-shell.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `PaymentList` (Task 2), existing `InvoiceList`/`InvoiceEditor`/`InvoiceDetail`/`PaymentEditor`/`CustomerList`.
- Produces: component `ReceivablesShell` (selector `app-receivables-shell`); the `receivables` route subtree with children `invoices`, `invoices/new`, `invoices/:id/edit`, `invoices/:id`, `payments`, `payments/new`, `customers`, and `'' → invoices` redirect.

- [ ] **Step 1: Write the failing test**

Create `receivables-shell.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { ReceivablesShell } from './receivables-shell';

describe('ReceivablesShell', () => {
  it('renders Invoices / Payments / Customers tabs with routerLinks', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([])] });
    const f = TestBed.createComponent(ReceivablesShell); f.detectChanges();
    const el = f.nativeElement;
    for (const [tid, label, seg] of [
      ['tab-invoices', 'Invoices', 'invoices'],
      ['tab-payments', 'Payments', 'payments'],
      ['tab-customers', 'Customers', 'customers'],
    ] as const) {
      const a = el.querySelector(`[data-testid=${tid}]`) as HTMLAnchorElement;
      expect(a).toBeTruthy();
      expect(a.textContent.trim()).toBe(label);
      expect(a.getAttribute('href')).toContain(seg);
    }
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/receivables-shell.spec.ts'`
Expected: FAIL — `ReceivablesShell` does not exist.

- [ ] **Step 3: Create the shell**

Create `receivables-shell.ts`:

```ts
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-receivables-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="flex flex-col gap-0">
      <nav class="flex gap-1 border-b border-border px-4 pt-4">
        <a routerLink="invoices"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-invoices">Invoices</a>
        <a routerLink="payments"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-payments">Payments</a>
        <a routerLink="customers"
           routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
           [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
           data-testid="tab-customers">Customers</a>
      </nav>
      <router-outlet />
    </div>
  `,
})
export class ReceivablesShell {}
```

- [ ] **Step 4: Re-nest the routes**

In `app.routes.ts`, add imports next to the other receivables imports:

```ts
import { ReceivablesShell } from './features/receivables/receivables-shell';
import { PaymentList } from './features/receivables/payment-list';
```

Replace the existing `{ path: 'receivables', children: [ … ] }` block entirely with:

```ts
  { path: 'receivables', component: ReceivablesShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'invoices' },
    { path: 'invoices', component: InvoiceList },
    { path: 'invoices/new', component: InvoiceEditor },
    { path: 'invoices/:id/edit', component: InvoiceEditor },
    { path: 'invoices/:id', component: InvoiceDetail },
    { path: 'payments', component: PaymentList },
    { path: 'payments/new', component: PaymentEditor },
    { path: 'customers', component: CustomerList },
  ] },
```

- [ ] **Step 5: Run shell test + full UI suite**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/receivables-shell.spec.ts'`
Expected: PASS.
Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false`
Expected: PASS (the route change does not touch component-level specs).

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/receivables-shell.ts UI/Angular/src/app/features/receivables/receivables-shell.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): Receivables hub shell + re-nested routes (invoices/payments/customers tabs)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Invoice list — adopt shared select, remove Record-payment, skip-reset effect

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/invoice-list.ts`
- Test: `UI/Angular/src/app/features/receivables/invoice-list.spec.ts`

**Interfaces:**
- Consumes: `CustomerSelect` (Task 1).
- Produces: `InvoiceList` no longer renders a customer `hlmSelect` inline or any "Record payment" control; switching customer resets `skip` to 0 via an effect.

**Note:** The invoice list keeps a SECOND `hlmSelect` (the settlement filter), so do NOT remove `HlmSelectImports` — only the customer select moves to `<app-customer-select>`.

- [ ] **Step 1: Update the spec (remove the Record-payment test; assert no Record-payment control)**

In `invoice-list.spec.ts`, DELETE the test titled `'Record payment link targets the payment editor for the selected customer'` in its entirety. Then add this test inside the same `describe`:

```ts
it('has no Record-payment control in the invoice-list header (moved to the Payments tab)', () => {
  const ctrl = TestBed.inject(HttpTestingController);
  const f = TestBed.createComponent(InvoiceList); f.detectChanges();
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  f.detectChanges();
  const recordLink = [...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Record payment');
  expect(recordLink).toBeFalsy();
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/invoice-list.spec.ts'`
Expected: FAIL — the "Record payment" link still exists, so the new assertion fails.

- [ ] **Step 3: Edit the component**

In `invoice-list.ts`:

(a) Add `effect` to the `@angular/core` import and add the `CustomerSelect` import:

```ts
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
```
```ts
import { CustomerSelect } from '../../shared/customer-select';
```

(b) Add `CustomerSelect` to the component `imports` array (keep `...HlmSelectImports` — the settlement select still uses it).

(c) Replace the customer-select block in the template — this entire element:

```html
        <div hlmSelect [value]="customerId()" (valueChange)="onCustomerChange($event)" [itemToString]="toCustomerName">
          <hlm-select-trigger class="w-64">
            <hlm-select-value placeholder="Select a customer" />
          </hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            @for (c of svc.customers(); track c.id) {
              <hlm-select-item [value]="c.id">{{ c.name }}</hlm-select-item>
            }
          </hlm-select-content>
        </div>
```

with:

```html
        <app-customer-select />
```

(d) Replace the two-button `ms-auto` wrapper — this element:

```html
        <div class="ms-auto flex items-center gap-2">
          <a hlmBtn size="sm" variant="outline"
             routerLink="/receivables/payments/new"
             [queryParams]="{ customer: customerId() }"
             [class.pointer-events-none]="!customerId()"
             [class.opacity-50]="!customerId()">
            Record payment
          </a>
          <a hlmBtn size="sm"
             routerLink="/receivables/invoices/new"
             [queryParams]="{ customer: customerId() }"
             [class.pointer-events-none]="!customerId()"
             [class.opacity-50]="!customerId()">
            New invoice
          </a>
        </div>
```

with the single New-invoice anchor:

```html
        <a hlmBtn size="sm" class="ms-auto"
           routerLink="/receivables/invoices/new"
           [queryParams]="{ customer: customerId() }"
           [class.pointer-events-none]="!customerId()"
           [class.opacity-50]="!customerId()">
          New invoice
        </a>
```

(e) Remove the now-unused `toCustomerName` field and the `onCustomerChange` method; add the skip-reset effect in the constructor. Replace:

```ts
  /** Passed to [itemToString] so the trigger shows the customer name rather than the raw GUID. */
  readonly toCustomerName = (id: string): string => this.svc.customerName(id);
  readonly settlementToLabel = (v: string): string => v === 'open' ? 'Open' : v === 'paid' ? 'Paid' : 'All';

  constructor() { this.svc.load(); }

  onCustomerChange(value: unknown): void {
    this.svc.setSelectedCustomer((value as string) ?? '');
    this.skip.set(0);
  }
```

with:

```ts
  readonly settlementToLabel = (v: string): string => v === 'open' ? 'Open' : v === 'paid' ? 'Paid' : 'All';

  constructor() {
    this.svc.load();
    // The shared <app-customer-select> writes the selection directly; reset paging to page 1
    // whenever the selected customer changes (a no-op when skip is already 0).
    effect(() => { this.customerId(); this.skip.set(0); });
  }
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/invoice-list.spec.ts'`
Expected: PASS (the existing selection/persistence/list tests drive `svc.setSelectedCustomer` directly, so they are unaffected; the new no-control test passes).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/features/receivables/invoice-list.ts UI/Angular/src/app/features/receivables/invoice-list.spec.ts
git commit -m "refactor(ui): invoice list uses shared customer-select; drop Record-payment button

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Invoice detail — remove the Record-payment link

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/invoice-detail.ts`
- Test: `UI/Angular/src/app/features/receivables/invoice-detail.spec.ts`

**Interfaces:**
- Produces: the Issued invoice detail no longer shows a "Record payment" link (void controls + applied-payments list remain).

- [ ] **Step 1: Add the guard assertion to the existing payments test**

In `invoice-detail.spec.ts`, in the test titled `'lists payments applied to this invoice and voids one, reloading after'`, immediately after the existing line `expect(f.nativeElement.textContent).toContain('110.00');`, add:

```ts
    expect([...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Record payment')).toBeFalsy();
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/invoice-detail.spec.ts'`
Expected: FAIL — the "Record payment" link is still rendered for an Issued invoice.

- [ ] **Step 3: Remove the link from the template**

In `invoice-detail.ts`, in the `@case ('Issued')` block, DELETE this anchor (the void input + Void button stay):

```html
              <a hlmBtn variant="outline" routerLink="/receivables/payments/new"
                 [queryParams]="{ customer: v.invoice.customerId, invoice: id }">Record payment</a>
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false --include='**/invoice-detail.spec.ts'`
Expected: PASS.

- [ ] **Step 5: Run the full UI suite**

Run: `cd UI/Angular && export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false`
Expected: PASS (all spec files).

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/invoice-detail.ts UI/Angular/src/app/features/receivables/invoice-detail.spec.ts
git commit -m "refactor(ui): drop Record-payment link from invoice detail (one place: Payments tab)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Manual verification (after all tasks, against the running dev stack)

1. `pwsh .localdev/start.ps1`.
2. Sidebar **Receivables** → lands on the **Invoices** tab (URL `/receivables/invoices`), Receivables lit.
3. Tabs switch Invoices / Payments / Customers; the selected customer persists across tabs.
4. **Payments** tab → pick a customer → their payments list; **Record payment** is here (and only here) → records and returns.
5. Invoice list header has **no** Record-payment button; an Issued invoice detail has **no** Record-payment link (void + applied-payments still present).

## Self-review notes

- Spec coverage: shell+tabs (T3), re-nest+redirect (T3), PaymentList home (T2), shared customer-select (T1), dedupe list (T4) + detail (T5), tests in every task, manual steps cover the IA + dedupe.
- Sidebar highlight: unchanged — `shell.ts` already longest-prefix matches, so `/receivables/invoices` keeps Receivables lit.
- Deferred per spec: Credits area (Slice B), payment detail/void-from-payments, any contextual deep-link. `PaymentEditor`'s `&invoice=` support stays but is unused.
