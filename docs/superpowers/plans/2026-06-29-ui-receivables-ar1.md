# Receivables AR-1 (Customers + Invoices) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the first Receivables UI — manage customers and the invoice lifecycle (draft → issue → void) — plus the one backend endpoint the UI needs.

**Architecture:** Reuse the Journal document pattern: an invoice **list** screen with the **customer as the lead filter** + settlement filter + paging, routed **detail** and **editor** screens, a thin **CustomerList**, and a `ReceivablesService` that mirrors `EntriesService`/`AccountsService`. One backend prerequisite: `GET /clients/{clientId}/customers`. Design spec: `docs/superpowers/specs/2026-06-29-ui-receivables-ar1-design.md`.

**Tech Stack:** .NET 10 minimal API + EphemeralMongo (backend, Task 1); Angular 22 (standalone, signals, zoneless, OnPush), Signal Forms (`@angular/forms/signals`), Tailwind v4, Spartan UI (hlm), Vitest (Tasks 2–7).

## Global Constraints

- **Angular:** zoneless + OnPush on every component; `standalone: true` **omitted**; signals + `input()`/`output()`; `@if`/`@for`; `inject()` DI.
- **Money/dates render only through** `money(n)` / `displayDate(d)` from `core/format/display`. Never hand-format. Decimal-aligned, tabular numerals, accounting parens for negatives.
- API returns raw `decimal` (→ TS `number`) + ISO date strings (camelCase JSON). Services return typed DTOs; URLs client-scoped via `ClientContextService.clientId()` with a `if (!id) return EMPTY;` guard (import `EMPTY` from `rxjs`).
- Reactive lists use `toObservable(query) → switchMap → toSignal` (cancels in-flight requests), consuming `PagedResponse<T> { items, total, skip, limit }` and doing page math from the echoed `skip`/`limit`.
- Spartan select rule: **every** `hlmSelect` content needs `*hlmSelectPortal`; add `[itemToString]` whenever the bound value differs from the visible label (e.g. customer GUID → name).
- **Env:** Bash subshells `export PATH="/c/nvm4w/nodejs:$PATH"` (nvm 24.18.0). Angular tests: `ng test --watch=false` (Vitest, use `vi.spyOn`, not Jasmine). Build: `npm run build`. Work from `UI/Angular`. Backend tests: `dotnet test` from repo root for the Receivables test project.
- **Backend:** .NET 10; namespaces follow folders; stage explicit file lists (Rider rewrites types to `var` — verify the staged set, no stray churn).
- Commit trailer verbatim: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Existing code to mirror (read before implementing)

- Backend: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (route group, `POST /customers` → mirror for the new GET), the customer service/store it calls, and `Modules/Receivables/Accounting101.Receivables.Tests/` (EphemeralMongo fixture; an existing endpoint/E2E test to copy structure from).
- Angular: `core/entries/entries.service.ts` (client-scoped service, paged list), `core/accounts/accounts.service.ts` (signal cache + `tap` + `label`), `features/journal/entry-list.ts` (query→switchMap→toSignal paging + filters), `features/journal/entry-form.ts` (Signal Forms, line array, server-error surfacing), `features/journal/entry-detail.ts` (footed lines, state actions), `features/accounts/account-editor.ts` (Signal Forms editor + routed new/edit), `shared/posting-badge.ts` (badge), `core/format/display.ts`, `core/api/problem-details.ts` (`extractProblem`), `app.routes.ts`.

## Existing types (already in the codebase)

- `PagedResponse<T> { items: T[]; total: number; skip: number; limit: number }` (`core/...`; confirm exact path via entries.service import).
- `ClientContextService` with `clientId()` signal; `extractProblem(e).detail`.
- `money(n: number): string`, `displayDate(d: string): string` (`core/format/display`).

---

### Task 1: Backend — `GET /clients/{clientId}/customers`

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (add the GET route next to `POST /customers`)
- Modify: the customer service + store the endpoints use (add a list method) — discover the exact types by reading the `POST /customers` handler (`CreateCustomer`) and following its service/store calls.
- Test: add to the Receivables test project (`Modules/Receivables/Accounting101.Receivables.Tests/`), mirroring an existing endpoint/E2E test that uses the shared EphemeralMongo fixture.

**Interfaces:**
- Produces: `GET /clients/{clientId}/customers` → **200** `Customer[]` (JSON array of `{ id, name, email }`), ordered by `Name` ascending, client-scoped, `RequireAuthorization()`. Consumed by Task 2's `ReceivablesService.load()`.

- [ ] **Step 1: Read the mirror.** Open `ReceivablesEndpoints.cs`; locate the `POST .../customers` mapping and the customer service/store method it calls (e.g. `ICustomerStore`/`CustomerService`). Note the method names, the store's collection access, and how the route group injects `clientId`. Open one existing test (e.g. a `*E2eTests.cs` or endpoint test) to copy the fixture/host setup.

- [ ] **Step 2: Write the failing integration test.**

Create `Modules/Receivables/Accounting101.Receivables.Tests/CustomerListEndpointTests.cs` mirroring an existing endpoint test's setup (shared EphemeralMongo, host/client). Assert:
```csharp
// Arrange: POST two customers (e.g. "Beta LLC", then "Acme Co") for client C.
// Act: GET /clients/{C}/customers
// Assert: 200; body is a 2-item array; ordered by Name ascending → ["Acme Co","Beta LLC"];
//         each item has id/name; a different clientId returns an empty array (client isolation).
```
Use the existing test's HTTP helper / `WebApplicationFactory` pattern verbatim; only the assertions above are new.

- [ ] **Step 3: Run it — verify it fails.**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter CustomerListEndpointTests`
Expected: FAIL (route not mapped → 404, or method missing → compile error).

- [ ] **Step 4: Add the store + service list method.**

In the customer store, add a method returning all customers for the scoped client ordered by `Name` (mirror the store's existing find/insert; e.g. `Find(_ => true).SortBy(c => c.Name).ToListAsync()`). In the customer service, add a pass-through `ListCustomersAsync()` returning `IReadOnlyList<Customer>`. Match the existing async signatures and cancellation-token usage in those files.

- [ ] **Step 5: Map the endpoint.**

In `ReceivablesEndpoints.cs`, beside `POST .../customers`, add:
```csharp
group.MapGet("/customers", async (CustomerService customers, CancellationToken ct) =>
        Results.Ok(await customers.ListCustomersAsync(ct)))
    .RequireAuthorization();
```
Adapt the handler's parameter types/names to the file's actual DI shape (service type, how `clientId` is bound in the group — copy from the `POST /customers` handler).

- [ ] **Step 6: Run the test — verify it passes.**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter CustomerListEndpointTests`
Expected: PASS.

- [ ] **Step 7: Full backend build + commit.**

Run: `dotnet build` (expected: 0 warnings). Then commit only the touched files:
```bash
git add Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs <changed service/store files> Modules/Receivables/Accounting101.Receivables.Tests/CustomerListEndpointTests.cs
git commit -m "feat(receivables): GET /clients/{id}/customers (list customers, name-ordered)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Core types + `ReceivablesService`

**Files:**
- Create: `UI/Angular/src/app/core/receivables/receivables.ts` (types)
- Create: `UI/Angular/src/app/core/receivables/receivables.service.ts`
- Create: `UI/Angular/src/app/core/receivables/receivables.service.spec.ts`

**Interfaces:**
- Produces (types): `Customer`, `InvoiceLine`, `Invoice`, `InvoiceView`, `DraftInvoiceRequest`, `VoidInvoiceRequest`, `InvoiceStatus`, `SettlementStatus`, `SettlementFilter`, `InvoiceListQuery`.
- Produces (`ReceivablesService`): `customers: Signal<Customer[]>`, `load(): void`, `create(name, email?): Observable<Customer>`, `customerName(id): string`, `listInvoices(q: InvoiceListQuery): Observable<PagedResponse<InvoiceView>>`, `getInvoice(id): Observable<InvoiceView>`, `draft(req): Observable<Invoice>`, `updateDraft(id, req): Observable<Invoice>`, `deleteDraft(id): Observable<void>`, `issue(id): Observable<Invoice>`, `void(id, reason?): Observable<Invoice>`. Consumed by Tasks 4–7.

- [ ] **Step 1: Types** — `core/receivables/receivables.ts`:
```ts
export type InvoiceStatus = 'Draft' | 'Issued' | 'Void';
export type SettlementStatus = 'Open' | 'PartiallyPaid' | 'Paid';
export type SettlementFilter = 'open' | 'paid';

export interface Customer { id: string; name: string; email: string | null; }

export interface InvoiceLine {
  description: string; quantity: number; unitPrice: number;
  taxable: boolean; revenueCategory: string | null;
}
export interface Invoice {
  id: string; customerId: string; number: string | null;
  issueDate: string; dueDate: string | null; status: InvoiceStatus;
  taxRate: number; memo: string | null; lines: InvoiceLine[];
}
export interface InvoiceView { invoice: Invoice; openBalance: number; settlementStatus: SettlementStatus; }

export interface DraftInvoiceRequest {
  customerId: string; lines: InvoiceLine[]; taxRate: number;
  issueDate: string; dueDate: string | null; memo: string | null;
}
export interface VoidInvoiceRequest { reason: string | null; }

export interface InvoiceListQuery {
  customerId: string; settlement?: SettlementFilter; skip: number; limit: number; order?: 'asc' | 'desc';
}

/** Pure money math mirroring the backend Invoice computed fields. */
export const lineAmount = (l: Pick<InvoiceLine, 'quantity' | 'unitPrice'>): number => l.quantity * l.unitPrice;
export function invoiceTotals(lines: readonly InvoiceLine[], taxRate: number): { subtotal: number; tax: number; total: number } {
  const subtotal = lines.reduce((s, l) => s + lineAmount(l), 0);
  const taxableBase = lines.filter(l => l.taxable).reduce((s, l) => s + lineAmount(l), 0);
  const tax = Math.round(taxRate * taxableBase * 100) / 100;   // 2dp, half-away-from-zero for non-negative inputs
  return { subtotal, tax, total: subtotal + tax };
}
```

- [ ] **Step 2: Service spec (TDD)** — `core/receivables/receivables.service.spec.ts`. Mirror `accounts.service.spec.ts` setup (`provideZonelessChangeDetection`, `provideHttpClient`, `provideHttpClientTesting`, `ClientContextService.select('C1')`, `afterEach ctrl.verify()`). Tests:
```ts
it('load() GETs customers and caches them; customerName resolves with id fallback', () => {
  const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  svc.load();
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush(
    [{ id: 'cu1', name: 'Acme Co', email: null }] as Customer[]);
  expect(svc.customers().length).toBe(1);
  expect(svc.customerName('cu1')).toBe('Acme Co');
  expect(svc.customerName('nope')).toBe('nope');           // fallback
});

it('create() POSTs and appends to the cache', () => {
  const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  let made: Customer | undefined; svc.create('Beta LLC', 'b@x.com').subscribe(c => (made = c));
  const req = ctrl.expectOne('http://localhost:5000/clients/C1/customers');
  expect(req.request.method).toBe('POST');
  expect(req.request.body).toEqual({ name: 'Beta LLC', email: 'b@x.com' });
  req.flush({ id: 'cu2', name: 'Beta LLC', email: 'b@x.com' } as Customer);
  expect(made!.id).toBe('cu2'); expect(svc.customers().some(c => c.id === 'cu2')).toBe(true);
});

it('listInvoices() builds the query string', () => {
  const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  svc.listInvoices({ customerId: 'cu1', settlement: 'open', skip: 0, limit: 50, order: 'desc' }).subscribe();
  const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/invoices'
    && r.params.get('customerId') === 'cu1' && r.params.get('settlement') === 'open'
    && r.params.get('skip') === '0' && r.params.get('limit') === '50' && r.params.get('order') === 'desc');
  req.flush({ items: [], total: 0, skip: 0, limit: 50 });
  expect(req.request.method).toBe('GET');
});

it('draft/issue/void hit the right method, URL and body', () => {
  const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  const req: DraftInvoiceRequest = { customerId: 'cu1', lines: [{ description: 'Work', quantity: 1, unitPrice: 100, taxable: true, revenueCategory: null }], taxRate: 0.07, issueDate: '2026-06-29', dueDate: null, memo: null };
  svc.draft(req).subscribe();
  const post = ctrl.expectOne('http://localhost:5000/clients/C1/invoices');
  expect(post.request.method).toBe('POST'); expect(post.request.body).toEqual(req);
  post.flush({ id: 'inv1', customerId: 'cu1', number: null, issueDate: '2026-06-29', dueDate: null, status: 'Draft', taxRate: 0.07, memo: null, lines: req.lines });

  svc.issue('inv1').subscribe();
  const issue = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1/issue');
  expect(issue.request.method).toBe('POST'); issue.flush({ id: 'inv1', customerId: 'cu1', number: '1001', issueDate: '2026-06-29', dueDate: null, status: 'Issued', taxRate: 0.07, memo: null, lines: req.lines });

  svc.void('inv1', 'mistake').subscribe();
  const v = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1/void');
  expect(v.request.method).toBe('POST'); expect(v.request.body).toEqual({ reason: 'mistake' });
  v.flush({ id: 'inv1', customerId: 'cu1', number: '1001', issueDate: '2026-06-29', dueDate: null, status: 'Void', taxRate: 0.07, memo: null, lines: req.lines });
});
```

- [ ] **Step 3: Run the spec — verify it fails.** `export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false` → FAIL (`ReceivablesService` undefined).

- [ ] **Step 4: Implement the service** — `core/receivables/receivables.service.ts`:
```ts
import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, tap } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../<paged-response-path>';   // copy the import path from entries.service.ts
import { Customer, DraftInvoiceRequest, Invoice, InvoiceListQuery, InvoiceView } from './receivables';

@Injectable({ providedIn: 'root' })
export class ReceivablesService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);
  private readonly _customers = signal<Customer[]>([]);
  readonly customers = this._customers.asReadonly();
  private readonly byId = computed(() => new Map(this._customers().map(c => [c.id, c])));

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  load(): void {
    const id = this.client.clientId(); if (!id) return;
    this.http.get<Customer[]>(this.base('/customers')).subscribe(cs => this._customers.set(cs));
  }
  create(name: string, email?: string | null): Observable<Customer> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<Customer>(this.base('/customers'), { name, email: email ?? null })
      .pipe(tap(c => this._customers.update(list => [...list, c])));
  }
  customerName(id: string): string { return this.byId().get(id)?.name ?? id; }

  listInvoices(q: InvoiceListQuery): Observable<PagedResponse<InvoiceView>> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    let params = new HttpParams().set('customerId', q.customerId).set('skip', q.skip).set('limit', q.limit);
    if (q.settlement) params = params.set('settlement', q.settlement);
    if (q.order) params = params.set('order', q.order);
    return this.http.get<PagedResponse<InvoiceView>>(this.base('/invoices'), { params });
  }
  getInvoice(id: string): Observable<InvoiceView> { return this.http.get<InvoiceView>(this.base(`/invoices/${id}`)); }
  draft(req: DraftInvoiceRequest): Observable<Invoice> { return this.http.post<Invoice>(this.base('/invoices'), req); }
  updateDraft(id: string, req: DraftInvoiceRequest): Observable<Invoice> { return this.http.put<Invoice>(this.base(`/invoices/${id}`), req); }
  deleteDraft(id: string): Observable<void> { return this.http.delete<void>(this.base(`/invoices/${id}`)); }
  issue(id: string): Observable<Invoice> { return this.http.post<Invoice>(this.base(`/invoices/${id}/issue`), {}); }
  void(id: string, reason?: string | null): Observable<Invoice> { return this.http.post<Invoice>(this.base(`/invoices/${id}/void`), { reason: reason ?? null }); }
}
```
> Confirm the `PagedResponse` import path and `environment.apiBaseUrl` usage by copying from `entries.service.ts`. Guards return `EMPTY` for the write/list paths; `load()` early-returns (it sets a signal, returns void).

- [ ] **Step 5: Run the spec — verify it passes.** `ng test --watch=false` → the 4 new tests PASS.

- [ ] **Step 6: Build + commit.**
```bash
git add UI/Angular/src/app/core/receivables/receivables.ts UI/Angular/src/app/core/receivables/receivables.service.ts UI/Angular/src/app/core/receivables/receivables.service.spec.ts
git commit -m "feat(ui): receivables core types + ReceivablesService (customers + invoices)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `InvoiceStatusBadge` + `SettlementBadge` (shared, TDD)

**Files:**
- Create: `UI/Angular/src/app/shared/invoice-status-badge.ts`, `invoice-status-badge.spec.ts`
- Create: `UI/Angular/src/app/shared/settlement-badge.ts`, `settlement-badge.spec.ts`

**Interfaces:**
- Produces: `<app-invoice-status-badge [status]="InvoiceStatus">`, `<app-settlement-badge [status]="SettlementStatus">`. Consumed by Tasks 5–7.

- [ ] **Step 1: Specs (TDD).** Mirror `shared/posting-badge.spec.ts`. For `invoice-status-badge.spec.ts`:
```ts
it('renders Draft / Issued / Void with matching testids', () => {
  for (const [s, tid] of [['Draft','badge-draft'],['Issued','badge-issued'],['Void','badge-void']] as const) {
    const f = TestBed.createComponent(InvoiceStatusBadge);
    f.componentRef.setInput('status', s); f.detectChanges();
    expect(f.nativeElement.querySelector(`[data-testid=${tid}]`)).toBeTruthy();
    expect(f.nativeElement.textContent).toContain(s);
  }
});
```
For `settlement-badge.spec.ts`: same shape for `Open`/`PartiallyPaid`/`Paid` → `badge-open`/`badge-partial`/`badge-paid`, text contains "Open"/"Partial"/"Paid".

- [ ] **Step 2: Run — verify fail.** `ng test --watch=false` → FAIL (components undefined).

- [ ] **Step 3: Implement.** Mirror `shared/posting-badge.ts` (OnPush, `imports: [...HlmBadgeImports]`, `input.required<...>()`). `invoice-status-badge.ts`:
```ts
@Component({
  selector: 'app-invoice-status-badge', changeDetection: ChangeDetectionStrategy.OnPush, imports: [...HlmBadgeImports],
  template: `
    @switch (status()) {
      @case ('Draft')  { <span hlmBadge variant="outline" data-testid="badge-draft">Draft</span> }
      @case ('Issued') { <span hlmBadge variant="secondary" data-testid="badge-issued">Issued</span> }
      @case ('Void')   { <span hlmBadge class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]" data-testid="badge-void">Void</span> }
    }`,
})
export class InvoiceStatusBadge { readonly status = input.required<InvoiceStatus>(); }
```
`settlement-badge.ts`: same pattern; `Open` → outline "Open"; `PartiallyPaid` → secondary "Partial"; `Paid` → secondary "Paid" (testids `badge-open`/`badge-partial`/`badge-paid`). Import the enums from `../core/receivables/receivables`.

- [ ] **Step 4: Run — verify pass.** `ng test --watch=false` → PASS.

- [ ] **Step 5: Commit.**
```bash
git add UI/Angular/src/app/shared/invoice-status-badge.ts UI/Angular/src/app/shared/invoice-status-badge.spec.ts UI/Angular/src/app/shared/settlement-badge.ts UI/Angular/src/app/shared/settlement-badge.spec.ts
git commit -m "feat(ui): shared invoice-status + settlement badges

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `CustomerList` screen (list + inline create) + route

**Files:**
- Create: `UI/Angular/src/app/features/receivables/customer-list.ts`, `customer-list.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `ReceivablesService` (`customers`, `load`, `create`), `extractProblem`.
- Produces: route `receivables/customers` → `CustomerList`.

- [ ] **Step 1: Spec (TDD).** Setup like `chart-of-accounts.spec.ts` (`provideZonelessChangeDetection`, `provideRouter([])`, `provideHttpClient`, `provideHttpClientTesting`, `ClientContextService.select('C1')`, `afterEach ctrl.verify()`).
```ts
it('lists customers and creates one inline', () => {
  const f = TestBed.createComponent(CustomerList); f.detectChanges();
  TestBed.inject(HttpTestingController).expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  f.detectChanges();
  expect(f.nativeElement.textContent).toContain('Acme Co');
  const cmp = f.componentInstance; cmp.newName.set('Beta LLC'); cmp.newEmail.set('b@x.com'); cmp.add();
  const ctrl = TestBed.inject(HttpTestingController);
  const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/customers');
  expect(post.request.body).toEqual({ name: 'Beta LLC', email: 'b@x.com' });
  post.flush({ id: 'cu2', name: 'Beta LLC', email: 'b@x.com' });
  f.detectChanges();
  expect(f.nativeElement.textContent).toContain('Beta LLC');
  expect(cmp.newName()).toBe('');                  // form cleared
});
```

- [ ] **Step 2: Run — verify fail.** `ng test --watch=false` → FAIL.

- [ ] **Step 3: Implement** `customer-list.ts`:
```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-customer-list', changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Customers</h1>
        <a routerLink="/receivables" class="ms-auto text-sm underline">Invoices →</a>
      </div>
      <div class="flex items-end gap-2">
        <div class="flex flex-col gap-1 flex-1"><label class="text-xs text-muted-foreground">Name</label>
          <input hlmInput [value]="newName()" (input)="newName.set($any($event.target).value)" /></div>
        <div class="flex flex-col gap-1 flex-1"><label class="text-xs text-muted-foreground">Email (optional)</label>
          <input hlmInput [value]="newEmail()" (input)="newEmail.set($any($event.target).value)" /></div>
        <button hlmBtn type="button" (click)="add()" [disabled]="!newName().trim() || busy()">Add</button>
      </div>
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (svc.customers().length === 0) { <p class="text-sm text-muted-foreground italic">No customers yet.</p> }
      @for (c of svc.customers(); track c.id) {
        <div class="flex items-center gap-3 py-1 border-b border-border/50 text-sm">
          <span>{{ c.name }}</span><span class="text-muted-foreground">{{ c.email }}</span>
        </div>
      }
    </div>`,
})
export class CustomerList {
  readonly svc = inject(ReceivablesService);
  readonly newName = signal(''); readonly newEmail = signal('');
  readonly busy = signal(false); readonly error = signal<string | null>(null);
  constructor() { this.svc.load(); }
  add(): void {
    const name = this.newName().trim(); if (!name) return;
    this.busy.set(true); this.error.set(null);
    this.svc.create(name, this.newEmail().trim() || null).subscribe({
      next: () => { this.newName.set(''); this.newEmail.set(''); this.busy.set(false); },
      error: (e) => { this.error.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
```

- [ ] **Step 4: Route.** In `app.routes.ts` add the `receivables` subtree (import `CustomerList`; other components added in later tasks — for now only `customers` + a temporary `''`). Add:
```ts
{ path: 'receivables', children: [
  { path: 'customers', component: CustomerList },
  // '' (InvoiceList) + invoices/* added in Tasks 5–7
] },
```
and remove `/receivables` from the placeholder filter (the `.filter(n => ![...].includes(n.path) ...)` array — add `'/receivables'`). Because the InvoiceList route (`''`) doesn't exist yet, temporarily point `{ path: '', pathMatch: 'full', redirectTo: 'customers' }` inside the subtree; Task 5 replaces it with the real component.

- [ ] **Step 5: Run spec + full suite + build + commit.** `ng test --watch=false`, `npm run build`.
```bash
git add UI/Angular/src/app/features/receivables/customer-list.ts UI/Angular/src/app/features/receivables/customer-list.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): receivables customer list + inline create

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: `InvoiceList` screen (customer filter + settlement + paging) + route

**Files:**
- Create: `UI/Angular/src/app/features/receivables/invoice-list.ts`, `invoice-list.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `ReceivablesService` (`customers`, `load`, `customerName`, `listInvoices`), badges (Task 3), `money`/`displayDate`, `RouterLink`.
- Produces: route `receivables` (`''`) → `InvoiceList`.

- [ ] **Step 1: Spec (TDD).** Mirror `entry-list.spec.ts` paging/query approach.
```ts
function inv(id: string, number: string | null, status: 'Draft'|'Issued', open = 0): InvoiceView {
  return { invoice: { id, customerId: 'cu1', number, issueDate: '2026-06-29', dueDate: null, status, taxRate: 0, memo: null, lines: [] }, openBalance: open, settlementStatus: open > 0 ? 'Open' : 'Paid' };
}
it('selecting a customer loads their invoices; row links to detail', () => {
  const f = TestBed.createComponent(InvoiceList); f.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  f.detectChanges();
  f.componentInstance.customerId.set('cu1'); f.detectChanges();
  const req = ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices') && r.params.get('customerId') === 'cu1');
  req.flush({ items: [inv('inv1','1001','Issued',500)], total: 1, skip: 0, limit: 50 });
  f.detectChanges();
  const text = f.nativeElement.textContent;
  expect(text).toContain('1001'); expect(text).toContain('500.00');
  expect(f.nativeElement.querySelector('a[href="/receivables/invoices/inv1"]')).toBeTruthy();
});
it('shows a prompt when no customer is selected', () => {
  const f = TestBed.createComponent(InvoiceList); f.detectChanges();
  TestBed.inject(HttpTestingController).expectOne('http://localhost:5000/clients/C1/customers').flush([]);
  f.detectChanges();
  expect(f.nativeElement.textContent).toContain('Select a customer');
});
```

- [ ] **Step 2: Run — verify fail.** `ng test --watch=false` → FAIL.

- [ ] **Step 3: Implement** `invoice-list.ts`. Mirror `entry-list.ts`: filter signals `customerId = signal('')`, `settlement = signal<SettlementFilter | ''>('')`, `skip`, `limit = 50`; a `query = computed(...)`; `page = toSignal(toObservable(query).pipe(switchMap(q => q.customerId ? this.svc.listInvoices(q) : of(null))))`; derived `invoices`, `total`, `pageCount`, `currentPage`. Template:
  - Header with a customer `hlmSelect` (`[itemToString]` → `customerName`, `*hlmSelectPortal`, options from `svc.customers()`), a settlement select (All / Open / Paid), and a **New invoice** `routerLink="/receivables/invoices/new"` (only enabled when a customer is selected; pass the customer via query param `[queryParams]="{ customer: customerId() }"`).
  - When `!customerId()` → `<p>Select a customer to view invoices.</p>`.
  - Else a table: **Number · Issue · Due · Total · Open · Status** — Number falls back to "—" when null (draft); Total via `money(...)` (compute from lines with `invoiceTotals` or show `invoice` total — use `invoiceTotals(v.invoice.lines, v.invoice.taxRate).total`); Open via `money(v.openBalance)`; Status via `<app-invoice-status-badge>` + `<app-settlement-badge>`. Each row is a `routerLink="/receivables/invoices/{{v.invoice.id}}"` (anchor with that href).
  - Prev/Next paging from echoed `skip`/`limit` (mirror entry-list), "Page N of M".
  - `constructor` calls `svc.load()`.
> Use `of(null)` from rxjs for the "no customer" branch so the list stays empty without an HTTP call. Reset `skip` to 0 when `customerId`/`settlement` change (mirror how entry-list resets).

- [ ] **Step 4: Route.** Replace the temporary redirect from Task 4: set `{ path: '', pathMatch: 'full', component: InvoiceList }` in the `receivables` subtree (import `InvoiceList`).

- [ ] **Step 5: Run spec + full suite + build + commit.**
```bash
git add UI/Angular/src/app/features/receivables/invoice-list.ts UI/Angular/src/app/features/receivables/invoice-list.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): receivables invoice list (customer filter + settlement + paging)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: `InvoiceEditor` (Signal Forms, new/edit draft) + routes

**Files:**
- Create: `UI/Angular/src/app/features/receivables/invoice-editor.ts`, `invoice-editor.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `form`/`required`/`FormField` (`@angular/forms/signals`), `ReceivablesService` (`customers`, `load`, `customerName`, `getInvoice`, `draft`, `updateDraft`), `invoiceTotals`/`lineAmount`, `money`/`displayDate`, `extractProblem`, hlm input/label/select/button, `Router`/`ActivatedRoute`.
- Produces: routes `receivables/invoices/new` + `receivables/invoices/:id/edit` → `InvoiceEditor`.

- [ ] **Step 1: Spec (TDD).** Mirror `account-editor.spec.ts` (route stub via `ActivatedRoute` with `paramMap.get` and `queryParamMap.get`; spy Router.navigate with `.mockResolvedValue(true)`).
```ts
it('new: validation gates save; live total; POSTs a draft then navigates', () => {
  // ActivatedRoute: paramMap.get('id')=null, queryParamMap.get('customer')='cu1'
  const f = TestBed.createComponent(InvoiceEditor); f.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  const cmp = f.componentInstance;
  expect(cmp.canSave()).toBe(false);                       // no lines / no amounts
  cmp.addLine(); cmp.form.lines[0].description().value.set('Work');
  cmp.form.lines[0].quantity().value.set(2); cmp.form.lines[0].unitPrice().value.set(100);
  cmp.form.taxRate().value.set(0.07); f.detectChanges();
  expect(cmp.totals().subtotal).toBe(200); expect(cmp.totals().tax).toBe(14); expect(cmp.totals().total).toBe(214);
  expect(cmp.canSave()).toBe(true);
  const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  cmp.save();
  const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/invoices');
  expect(post.request.body.customerId).toBe('cu1'); expect(post.request.body.lines.length).toBe(1);
  expect(post.request.body.taxRate).toBe(0.07);
  post.flush({ id: 'inv1', customerId: 'cu1', number: null, issueDate: cmp.form.issueDate().value(), dueDate: null, status: 'Draft', taxRate: 0.07, memo: null, lines: post.request.body.lines });
  expect(nav).toHaveBeenCalledWith(['/receivables/invoices', 'inv1']);
});
it('edit: loads the draft (cold cache) and PUTs the same id', () => {
  // paramMap.get('id')='inv1'
  const f = TestBed.createComponent(InvoiceEditor); f.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush({ invoice: { id: 'inv1', customerId: 'cu1', number: null, issueDate: '2026-06-29', dueDate: null, status: 'Draft', taxRate: 0.05, memo: null, lines: [{ description: 'A', quantity: 1, unitPrice: 50, taxable: true, revenueCategory: null }] }, openBalance: 0, settlementStatus: 'Open' });
  f.detectChanges();
  const cmp = f.componentInstance; expect(cmp.form.taxRate().value()).toBe(0.05);
  vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  cmp.save();
  const put = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1');
  expect(put.request.method).toBe('PUT');
});
```

- [ ] **Step 2: Run — verify fail.** `ng test --watch=false` → FAIL.

- [ ] **Step 3: Implement** `invoice-editor.ts`. Model interface `{ customerId, issueDate, dueDate, memo, taxRate, lines: LineModel[] }` where `LineModel { lineId, description, quantity, unitPrice, taxable, revenueCategory }` (stable `lineId` via `crypto.randomUUID()` for `@for` track, mirroring entry-form). `form(model, p => { required(p.customerId); required(p.issueDate); applyEach(p.lines, l => { required(l.description); }); })`. Computeds: `totals = computed(() => invoiceTotals(this.model().lines, this.model().taxRate))`; `canSave = computed(() => this.form().valid() && this.model().lines.length > 0 && this.totals().total > 0)`. `editId` from `route.snapshot.paramMap.get('id')`; on new, prefill `customerId` from `route.snapshot.queryParamMap.get('customer')`. Edit load: reactive `effect()` with one-shot `#loaded` guard calling `getInvoice(editId)` once customers are loaded → `model.set(fromInvoice(view.invoice))` (mirror account-editor's cold-cache effect). `addLine()`/`removeLine(i)` mutate the lines array via `model.update`. Template mirrors entry-form (line table with description/qty/unitPrice/taxable checkbox/revenueCategory + remove, an Add line button), a customer `hlmSelect` (read-only/disabled when `editId`), `issueDate`/`dueDate` date inputs, `taxRate` number input, `memo`, a live **Subtotal/Tax/Total** block via `money(...)`, **Save** (`[disabled]="!canSave() || busy()"`) and **Cancel** (`routerLink="/receivables"`). `save()` builds `DraftInvoiceRequest` from the model (map lines dropping `lineId`), calls `draft` (new) or `updateDraft` (edit), on success `busy.set(false)` then `router.navigate(['/receivables/invoices', saved.id])`, on error `message.set(extractProblem(e).detail)`.
> `toRequest()` maps `LineModel[] → InvoiceLine[]` (omit `lineId`). Dates are ISO `yyyy-mm-dd` strings; default `issueDate` to today is **not** available (no `Date.now` in pure code is fine here — components may use `new Date()`); set the default in the model initializer with `new Date().toISOString().slice(0,10)`.

- [ ] **Step 4: Routes.** Add to the `receivables/invoices` children: `{ path: 'new', component: InvoiceEditor }` and `{ path: ':id/edit', component: InvoiceEditor }` (import `InvoiceEditor`). Order: put `new` and `:id/edit` so they don't collide with `:id` (Task 7) — list `new` before `:id`, and `:id/edit` is distinct.

- [ ] **Step 5: Run spec + full suite + build + commit.**
```bash
git add UI/Angular/src/app/features/receivables/invoice-editor.ts UI/Angular/src/app/features/receivables/invoice-editor.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): receivables invoice editor (signal-forms draft create/edit, live totals)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: `InvoiceDetail` (state-driven actions) + route

**Files:**
- Create: `UI/Angular/src/app/features/receivables/invoice-detail.ts`, `invoice-detail.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts`

**Interfaces:**
- Consumes: `ReceivablesService` (`getInvoice`, `customerName`, `load`, `deleteDraft`, `issue`, `void`), badges, `invoiceTotals`, `money`/`displayDate`, `extractProblem`, `Router`/`ActivatedRoute`, hlm input/button.
- Produces: route `receivables/invoices/:id` → `InvoiceDetail`.

- [ ] **Step 1: Spec (TDD).** Route stub `paramMap.get('id')='inv1'`. Tests:
```ts
function view(status: 'Draft'|'Issued', number: string | null) {
  return { invoice: { id: 'inv1', customerId: 'cu1', number, issueDate: '2026-06-29', dueDate: null, status, taxRate: 0.1, memo: null, lines: [{ description: 'Work', quantity: 1, unitPrice: 100, taxable: true, revenueCategory: null }] }, openBalance: status === 'Issued' ? 110 : 0, settlementStatus: 'Open' as const };
}
it('draft: shows Edit/Delete/Issue; Issue POSTs and reloads', () => {
  const f = TestBed.createComponent(InvoiceDetail); f.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush(view('Draft', null));
  f.detectChanges();
  expect(f.nativeElement.textContent).toContain('110.00');           // footed total (100 + 10% tax)
  f.componentInstance.issue();
  const issue = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1/issue');
  expect(issue.request.method).toBe('POST'); issue.flush(view('Issued','1001').invoice);
  ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush(view('Issued','1001')); // reload
});
it('issued: void POSTs the reason', () => {
  const f = TestBed.createComponent(InvoiceDetail); f.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush(view('Issued','1001'));
  f.detectChanges();
  const cmp = f.componentInstance; cmp.voidReason.set('dup'); cmp.voidInvoice();
  const v = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1/void');
  expect(v.request.body).toEqual({ reason: 'dup' });
  v.flush(view('Issued','1001').invoice);
  ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush({ ...view('Issued','1001'), invoice: { ...view('Issued','1001').invoice, status: 'Void' } });
});
```

- [ ] **Step 2: Run — verify fail.** `ng test --watch=false` → FAIL.

- [ ] **Step 3: Implement** `invoice-detail.ts`. `id = route.snapshot.paramMap.get('id')`. A `view = signal<InvoiceView | null>(null)`; `reload()` calls `getInvoice(id).subscribe(v => view.set(v))`; `constructor` calls `svc.load()` + `reload()`. `totals = computed(() => view() ? invoiceTotals(view()!.invoice.lines, view()!.invoice.taxRate) : null)`. Template (mirror entry-detail): a back link `routerLink="/receivables"` is **omitted** per the prior editor decision — instead a "← Invoices" is fine here since detail is read-only navigation (use `routerLink="/receivables"` text "← Invoices"); header with number-or-"Draft", `customerName(view().invoice.customerId)`, issue/due dates, `<app-invoice-status-badge>` + `<app-settlement-badge>`; a footed line table (Description · Qty · Unit · Amount via `money(lineAmount(l))`) with Subtotal/Tax/Total + **Open balance** `money(view().openBalance)`. Actions by `view().invoice.status`:
  - `Draft`: `<a routerLink="/receivables/invoices/{{id}}/edit">Edit</a>`, **Delete** button (`deleteInvoice()` → `deleteDraft(id)` → `router.navigate(['/receivables'])`), **Issue** button (`issue()` → `svc.issue(id)` → on success `reload()`).
  - `Issued`: a void-reason `hlmInput` (`aria-label="Void reason"`) + **Void** button (`voidInvoice()` → `svc.void(id, voidReason())` → on success `reload()`).
  - `Void`: read-only (no actions).
  - Errors → `message` signal via `extractProblem`.
> `issue()`/`voidInvoice()`/`deleteInvoice()` set `busy` while in-flight; surface `extractProblem(e).detail` on error (covers 409/422/closed-period relays).

- [ ] **Step 4: Route.** Add `{ path: 'invoices/:id', component: InvoiceDetail }` to the `receivables` subtree **after** `invoices/new` and `invoices/:id/edit` so the static/`edit` paths win. Final subtree order: `''`(InvoiceList) · `customers` · `invoices/new` · `invoices/:id/edit` · `invoices/:id`.

- [ ] **Step 5: Run spec + full suite + build + commit.**
```bash
git add UI/Angular/src/app/features/receivables/invoice-detail.ts UI/Angular/src/app/features/receivables/invoice-detail.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): receivables invoice detail (state-driven edit/delete/issue/void)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**1. Spec coverage:**
- Backend `GET /customers` → Task 1. ✓
- Core types + service (customers + invoices, all lifecycle methods, `customerName`) → Task 2. ✓
- Badges (invoice status + settlement) → Task 3. ✓
- Customers screen (list + inline create) → Task 4. ✓
- Invoice list (customer lead-filter + settlement + paging + row→detail, empty states) → Task 5. ✓
- Invoice editor (Signal Forms new/edit draft, line array, live totals, validation, 422) → Task 6. ✓
- Invoice detail (footed lines, state-driven Edit/Delete/Issue/Void, open balance, error relay) → Task 7. ✓
- Routing subtree + placeholder exclusion → Tasks 4–7. ✓
- Out-of-scope (payments, credits, dispositions, subledger/aging, GL-approval UI) → not built. ✓

**2. Placeholder scan:** No TBD/TODO. Code shown for each new unit; structural boilerplate references a named mirror file with the specific delta. The `>` notes are adaptation guidance with concrete defaults.

**3. Type consistency:** `Customer`/`Invoice`/`InvoiceView`/`DraftInvoiceRequest`/`InvoiceListQuery`/`InvoiceStatus`/`SettlementStatus`/`SettlementFilter` defined in Task 2 and consumed unchanged in Tasks 3–7. Service method names (`load`/`create`/`customerName`/`listInvoices`/`getInvoice`/`draft`/`updateDraft`/`deleteDraft`/`issue`/`void`) are identical across the plan. Routes `['/receivables']`, `['/receivables/invoices', id]`, `/receivables/invoices/new`, `/receivables/invoices/:id/edit`, `:id` consistent. Badge selectors `app-invoice-status-badge`/`app-settlement-badge` consistent. `invoiceTotals`/`lineAmount` defined in Task 2, used in Tasks 5–7.

## Open (resolve during execution)
- The exact `PagedResponse` import path and `environment.apiBaseUrl` access: copy verbatim from `core/entries/entries.service.ts`.
- Task 1's customer store/service method names are discovered by reading the `POST /customers` handler; the endpoint contract (200, name-ordered array, client-scoped) is fixed.
- Date default in the editor uses `new Date().toISOString().slice(0,10)` in the component (allowed — only pure workflow scripts forbid `Date`).
