# Refund Detail Implementation Plan (Slice 2b-1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a refund detail screen reachable by whole-row click, backed by a new `GET /refunds/{id}` returning the refund plus its posted journal entry id, with a "View journal entry" drill.

**Architecture:** Backend (Receivables module): a new `RefundView` read-model record, a `PaymentService.GetRefundViewAsync` (wraps the existing `GetRefundAsync` store method + finds the posting via the ledger client it already holds), and a `GET /clients/{id}/refunds/{refundId}` endpoint mirroring `GetInvoice`. Frontend: a `refund-detail` screen + `refunds/:id` route + a `getRefund` service method, and the refund-list rows made whole-row clickable (Void button insulated, memo truncated). No new capability wiring — the new GET is `ar.read`-gated by the engine's scoped document store like every Receivables read.

**Tech Stack:** .NET 10 minimal APIs + MongoDB (EphemeralMongo in tests); Angular 22 (standalone, OnPush, zoneless), Tailwind v4, Spartan Helm; xUnit (backend), Jasmine + TestBed / Vitest runner (frontend).

## Global Constraints

- **Backend:** namespaces follow folder structure (`Accounting101.Receivables`). New endpoint returns `RefundView` and follows the exact shape of `GetInvoice` (`ReceivablesEndpoints.cs:128-133`): inject `PaymentService`, `return view is null ? Results.NotFound() : Results.Ok(view)`. The endpoint group already carries `.RequireAuthorization()` (`ReceivablesEndpoints.cs:14`). Rider auto-converts explicit types to `var` — **stage explicit file lists and check for stray churn before each commit.**
- **Frontend:** standalone components, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. New detail route is ungated like every other detail route. The refund-list drill-in is same-area (a Refunds-list viewer already holds `ar.read`), so rows are unconditionally clickable. FE test runner is **Vitest** — use `vi.spyOn`, not Jasmine `spyOn`; nav spies chain `.mockResolvedValue(true)`.
- `RefundView` wire shape is identical backend↔frontend: `{ refund: Refund, journalEntryId: string | null }` (`Guid? JournalEntryId` serializes camelCase to `string | null`).
- Truncation uses the existing shared `TruncateDirective` (`[appTruncate]`, `src/app/shared/truncate.directive.ts`), imported from `'../../shared/truncate.directive'`.
- Only touch the files named per task. Do NOT touch credit-list/credit anything (that is 2b-2), the customer/vendor statement lists (2c), or any other module.
- Backend test run (focused): `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetRefundsEndpointTests"`. FE unit test: `npx ng test --include='<glob>' --watch=false` from `UI/Angular`. FE compile gate: `npx ng build --configuration development` from `UI/Angular`.
- Branch `feat/refund-detail`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: Backend — RefundView + GET /refunds/{id}

**Files:**
- Create: `Modules/Receivables/Accounting101.Receivables/RefundView.cs`
- Modify: `Modules/Receivables/Accounting101.Receivables/PaymentService.cs` (add `GetRefundViewAsync`)
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` (add `GetRefund` handler + route)
- Test (extend): `Modules/Receivables/Accounting101.Receivables.Tests/GetRefundsEndpointTests.cs`

**Interfaces:**
- Consumes: existing `IPaymentStore.GetRefundAsync`, `ILedgerClient.GetEntriesBySourceRefAsync`, `EntryResponse`.
- Produces: `RefundView(Refund Refund, Guid? JournalEntryId)`; `PaymentService.GetRefundViewAsync(Guid, Guid, CancellationToken) → RefundView?`; route `GET /clients/{clientId}/refunds/{refundId:guid}`.

- [ ] **Step 1: Write the failing tests**

Add two test methods to `GetRefundsEndpointTests.cs`, inside the existing `GetRefundsEndpointTests` class (it already has `SeedSodClientAsync`, `SetUpChartAsync`, `IssueInvoiceAsync`, `ApproveBySourceRefAsync` helpers). Reference the new `RefundView` type (it will not exist yet — that is the RED; the test project will fail to compile):

```csharp
    [Fact]
    public async Task GET_refund_by_id_returns_the_refund_and_its_journal_entry_id()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        // $100 of unapplied credit: issue a $100 invoice, pay $200 allocating $100.
        Guid invoiceId = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);
        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 2), 200m, "check",
                    [new Allocation(invoiceId, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        Refund refund = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer.Id, new DateOnly(2026, 3, 5), 30m, "partial")))
            .Content.ReadFromJsonAsync<Refund>())!;

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={refund.Id}"))!;
        Guid expectedEntryId = entries.Single(e => e is { Status: "Active", ReversalOf: null }).Id;

        RefundView view = (await clerk.GetFromJsonAsync<RefundView>(
            $"/clients/{clientId}/refunds/{refund.Id}"))!;

        Assert.Equal(refund.Id, view.Refund.Id);
        Assert.Equal(30m, view.Refund.Amount);
        Assert.Equal("partial", view.Refund.Memo);
        Assert.False(view.Refund.Voided);
        Assert.Equal(expectedEntryId, view.JournalEntryId);
    }

    [Fact]
    public async Task GET_refund_by_unknown_id_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/refunds/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
```

If `HttpStatusCode` / `EntryResponse` are not already imported in this file, add the usings (`System.Net`, and the Ledger contracts namespace — check the file's existing usings; `ApproveBySourceRefAsync` already deserializes `EntryResponse[]`, so that using is present).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetRefundsEndpointTests"`
Expected: BUILD FAILURE — `RefundView` does not exist (the RED for a typed test).

- [ ] **Step 3: Create the `RefundView` record**

Create `Modules/Receivables/Accounting101.Receivables/RefundView.cs`:

```csharp
namespace Accounting101.Receivables;

/// <summary>A refund plus the id of its posted journal entry — what the refund detail endpoint returns.
/// The entry id lets the UI drill from the refund to the GL entry that recorded it.</summary>
public sealed record RefundView(Refund Refund, Guid? JournalEntryId);
```

- [ ] **Step 4: Add `GetRefundViewAsync` to `PaymentService`**

In `Modules/Receivables/Accounting101.Receivables/PaymentService.cs`, add this method (place it just after `GetRefundsByCustomerAsync`, ~line 101). It uses the primary-constructor `payments` and `ledger` parameters already in scope, and the same `Active`/`ReversalOf == null` pick used by `VoidLedgerEntryAsync` (line 276):

```csharp
    /// <summary>A single refund plus its posted journal entry id (for the detail screen's drill-in).
    /// The entry is the original posting sourced from the refund; null if none is found.</summary>
    public async Task<RefundView?> GetRefundViewAsync(Guid clientId, Guid refundId, CancellationToken ct = default)
    {
        Refund? refund = await payments.GetRefundAsync(clientId, refundId, ct);
        if (refund is null) return null;
        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, refundId, ct);
        EntryResponse? posting = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null });
        return new RefundView(refund, posting?.Id);
    }
```

- [ ] **Step 5: Add the `GetRefund` endpoint + route**

In `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs`:

Register the route immediately after the `ListRefunds` map (line 37) — add:
```csharp
        clients.MapGet("/refunds/{refundId:guid}", GetRefund);
```

Add the handler (place it next to `ListRefunds`, ~after line 208), mirroring `GetInvoice`:
```csharp
    private static async Task<IResult> GetRefund(
        Guid clientId, Guid refundId, PaymentService service, CancellationToken cancellationToken)
    {
        RefundView? view = await service.GetRefundViewAsync(clientId, refundId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter "FullyQualifiedName~GetRefundsEndpointTests"`
Expected: PASS (the two new tests + the pre-existing ones).

- [ ] **Step 7: Commit**

Stage the explicit file list (guard against Rider `var` churn):
```bash
git add Modules/Receivables/Accounting101.Receivables/RefundView.cs Modules/Receivables/Accounting101.Receivables/PaymentService.cs Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs Modules/Receivables/Accounting101.Receivables.Tests/GetRefundsEndpointTests.cs
git commit -m "feat(receivables): GET /refunds/{id} returning refund + journal entry id"
```

---

### Task 2: Frontend — refund-detail screen + route

**Files:**
- Modify: `UI/Angular/src/app/core/receivables/receivables.ts` (add `RefundView` interface)
- Modify: `UI/Angular/src/app/core/receivables/receivables.service.ts` (add `getRefund`)
- Create: `UI/Angular/src/app/features/receivables/refund-detail.ts`
- Create: `UI/Angular/src/app/features/receivables/refund-detail.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (add `refunds/:id` route)

**Interfaces:**
- Consumes: Task 1's `RefundView` wire shape; `ReceivablesService`, `ClientContextService`.
- Produces: `RefundView` TS interface; `ReceivablesService.getRefund(id): Observable<RefundView>`; `RefundDetail` component; route `refunds/:id`.

- [ ] **Step 1: Write the failing component spec**

Create `UI/Angular/src/app/features/receivables/refund-detail.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RefundDetail } from './refund-detail';
import { ClientContextService } from '../../core/client/client-context.service';

function boot(id: string) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(RefundDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('RefundDetail', () => {
  it('renders refund fields and links to the journal entry', () => {
    const { fixture, ctrl } = boot('rf1');
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf1').flush(
      { refund: { id: 'rf1', customerId: 'cu1', date: '2026-06-30', amount: 50, memo: 'overpayment', voided: false }, journalEntryId: 'e9' });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('50.00');
    expect(text).toContain('overpayment');
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('omits the journal link when journalEntryId is null', () => {
    const { fixture, ctrl } = boot('rf2');
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf2').flush(
      { refund: { id: 'rf2', customerId: 'cu1', date: '2026-06-30', amount: 25, memo: null, voided: false }, journalEntryId: null });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run: `npx ng test --include='**/refund-detail.spec.ts' --watch=false`
Expected: FAIL — cannot resolve `./refund-detail` (component does not exist).

- [ ] **Step 3: Add the `RefundView` interface**

In `UI/Angular/src/app/core/receivables/receivables.ts`, add after the `Refund` interface (line 56):
```ts
export interface RefundView { refund: Refund; journalEntryId: string | null; }
```

- [ ] **Step 4: Add the `getRefund` service method**

In `UI/Angular/src/app/core/receivables/receivables.service.ts`:

Add `RefundView` to the import from `'./receivables'` (line 7 — append `, RefundView` to the destructured list).

Add the method next to `getInvoice` (line 71):
```ts
  getRefund(id: string): Observable<RefundView> { return this.http.get<RefundView>(this.base(`/refunds/${id}`)); }
```

- [ ] **Step 5: Create the `refund-detail` component**

Create `UI/Angular/src/app/features/receivables/refund-detail.ts`:

```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { RefundView } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';

@Component({
  selector: 'app-refund-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables/refunds" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Refunds</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Refund</h1>
          <span class="text-sm px-2 py-0.5 rounded border border-border">{{ v.refund.voided ? 'Voided' : 'Active' }}</span>
        </div>
        <div class="text-sm flex flex-col gap-1">
          <div><span class="text-muted-foreground">Date</span> · {{ formatDate(v.refund.date) }}</div>
          <div><span class="text-muted-foreground">Amount</span> · <span class="tabular-nums">{{ money(v.refund.amount) }}</span></div>
          <div><span class="text-muted-foreground">Memo</span> · {{ v.refund.memo ?? '—' }}</div>
        </div>
        @if (v.journalEntryId) {
          <a [routerLink]="['/journal', v.journalEntryId]" class="text-sm text-primary hover:underline w-fit">View journal entry →</a>
        }
      } @else if (loadError()) {
        <p class="text-destructive text-sm">{{ loadError() }}</p>
      } @else {
        <p class="text-muted-foreground text-sm">Loading…</p>
      }
    </div>
  `,
})
export class RefundDetail {
  private readonly svc = inject(ReceivablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<RefundView | null>(null);
  readonly loadError = signal<string | null>(null);

  constructor() {
    this.svc.getRefund(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.view.set(v),
      error: (e) => this.loadError.set(extractProblem(e).detail),
    });
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
```

- [ ] **Step 6: Add the route**

In `UI/Angular/src/app/app.routes.ts`:

Add the import near the other receivables feature imports (next to line 24 `import { RefundEditor } ...`):
```ts
import { RefundDetail } from './features/receivables/refund-detail';
```

Add the route after the `refunds/new` entry (line 117), mirroring `invoices/:id` (line 109):
```ts
    { path: 'refunds/:id', component: RefundDetail },
```

- [ ] **Step 7: Run the spec + compile gate**

Run: `npx ng test --include='**/refund-detail.spec.ts' --watch=false` → both specs PASS.
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/receivables/receivables.ts UI/Angular/src/app/core/receivables/receivables.service.ts UI/Angular/src/app/features/receivables/refund-detail.ts UI/Angular/src/app/features/receivables/refund-detail.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): refund detail screen with journal-entry drill"
```

---

### Task 3: Frontend — refund-list drill-in + memo truncation

**Files:**
- Modify: `UI/Angular/src/app/features/receivables/refund-list.ts`
- Modify (extend): `UI/Angular/src/app/features/receivables/refund-list.spec.ts`

**Interfaces:**
- Consumes: `Router`, `TruncateDirective`, the `refunds/:id` route (Task 2).
- Produces: nothing.

- [ ] **Step 1: Write the failing tests**

Add `Router` to the router import at the top of `refund-list.spec.ts`:
```ts
import { provideRouter, Router } from '@angular/router';
```
Add these two specs inside `describe('RefundList', ...)`:

```ts
  it('navigates to the refund detail when a row is clicked', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'x')]);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).toHaveBeenCalledWith(['/receivables/refunds', 'rf1']);
  });

  it('does not navigate when the Void button is clicked', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'x')]);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void') as HTMLButtonElement;
    voidBtn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).not.toHaveBeenCalled();
    // flush the void POST + reload the void triggers, so HttpTestingController stays clean
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf1/void').flush({});
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/refunds')).flush([refund('rf1', 50, 'x', true)]);
  });
```

- [ ] **Step 2: Run to verify they fail**

Run: `npx ng test --include='**/refund-list.spec.ts' --watch=false`
Expected: the 2 new specs FAIL (row not clickable → `navigate` not called; Void click currently has no `stopPropagation` but also no row nav wired, so the first fails on the missing nav). Pre-existing RefundList specs still pass.

- [ ] **Step 3: Wire the drill-in + truncation in `refund-list.ts`**

**3a.** Update imports. Change line 2 from `import { RouterLink } from '@angular/router';` to:
```ts
import { Router, RouterLink } from '@angular/router';
```
Add the directive import (next to the other feature imports, after line 12):
```ts
import { TruncateDirective } from '../../shared/truncate.directive';
```

**3b.** Add `TruncateDirective` to the `imports` array (line 17):
```ts
  imports: [RouterLink, HlmButton, ...HlmTableImports, CustomerSelect, CanDirective, TruncateDirective],
```

**3c.** Replace the row `<tr>` block (lines 49-60). Change:
```html
                @for (r of refunds(); track r.id) {
                  <tr hlmTr [class.opacity-50]="r.voided">
                    <td hlmTd>{{ fmtDate(r.date) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(r.amount) }}</td>
                    <td hlmTd>{{ r.memo ?? '—' }}</td>
                    <td hlmTd>{{ r.voided ? 'Voided' : 'Active' }}</td>
                    <td hlmTd>
                      @if (!r.voided) {
                        <button *appCan="'ar.write'" hlmBtn size="sm" variant="outline" (click)="doVoid(r)" [disabled]="busy()">Void</button>
                      } @else { <span class="text-muted-foreground">—</span> }
                    </td>
                  </tr>
                }
```
to:
```html
                @for (r of refunds(); track r.id) {
                  <tr hlmTr role="button" tabindex="0"
                      class="cursor-pointer hover:bg-muted/50"
                      [class.opacity-50]="r.voided"
                      (click)="open(r.id)"
                      (keydown.enter)="open(r.id)">
                    <td hlmTd>{{ fmtDate(r.date) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(r.amount) }}</td>
                    <td hlmTd><span appTruncate>{{ r.memo ?? '—' }}</span></td>
                    <td hlmTd>{{ r.voided ? 'Voided' : 'Active' }}</td>
                    <td hlmTd>
                      @if (!r.voided) {
                        <button *appCan="'ar.write'" hlmBtn size="sm" variant="outline"
                                (click)="$event.stopPropagation(); doVoid(r)"
                                (keydown.enter)="$event.stopPropagation()"
                                [disabled]="busy()">Void</button>
                      } @else { <span class="text-muted-foreground">—</span> }
                    </td>
                  </tr>
                }
```

**3d.** Inject `Router` and add `open`. After `private readonly destroyRef = inject(DestroyRef);` (line 72), add:
```ts
  private readonly router = inject(Router);
```
Add the method (e.g. after `doVoid`, ~line 99):
```ts
  open(id: string): void { void this.router.navigate(['/receivables/refunds', id]); }
```

- [ ] **Step 4: Run the specs to verify they pass**

Run: `npx ng test --include='**/refund-list.spec.ts' --watch=false`
Expected: all specs PASS (pre-existing + 2 new), output pristine.

- [ ] **Step 5: Compile gate**

Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 6: Commit**

```bash
git add UI/Angular/src/app/features/receivables/refund-list.ts UI/Angular/src/app/features/receivables/refund-list.spec.ts
git commit -m "feat(ui): refund-list whole-row drill-in + memo truncation"
```

---

## Self-Review

**Spec coverage:**
- Backend `GET /refunds/{id}` returning `RefundView{refund, journalEntryId}` via `GetRefundViewAsync` (posting found by `GetEntriesBySourceRefAsync`, `Active`/`ReversalOf==null` pick) → Task 1. ✓
- `ar.read` gating automatic (no code) — no task needed. ✓
- refund-detail screen (fields + conditional journal link) + `refunds/:id` ungated route + `getRefund` service + `RefundView` type → Task 2. ✓
- refund-list whole-row drill-in (unconditional, same-area), Void `stopPropagation` (click + Enter), memo `appTruncate` → Task 3. ✓
- Tests: backend GET returns fields + entry id, 404 unknown (Task 1); FE detail renders + journal link present/absent (Task 2); FE list row-nav + Void no-nav (Task 3). ✓

**Placeholder scan:** every step has complete code; no TBD.

**Type/name consistency:** `RefundView{refund, journalEntryId}` identical backend record ↔ FE interface; `getRefund`/`GetRefundViewAsync`/`GetRefund` names consistent; route `/receivables/refunds/:id` matches `open(...)` navigation and the spec's `navigate` assertion; fixture ids (`rf1`, `e9`) consistent within each spec.

## Execution Handoff

Two execution options:
1. **Subagent-Driven (recommended)** — fresh implementer per task, per-task review, final whole-branch review.
2. **Inline Execution** — execute in this session with checkpoints.
