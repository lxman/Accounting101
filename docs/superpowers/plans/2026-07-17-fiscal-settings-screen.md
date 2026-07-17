# Fiscal Settings Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the placeholder at the `/admin/fiscal` nav leaf with a real screen that loads and saves the current client's fiscal-year-end month, gated by the `admin.fiscal` capability.

**Architecture:** The write endpoint (`PUT /admin/clients/{clientId}/fiscal-year-end`) and its `admin.fiscal` gate already exist. Add a symmetric `GET` on the same route, then build an Angular screen mirroring the existing Approval Policy screen (load-on-init → edit → save, three signals, `*appCan`-gated Save button).

**Tech Stack:** ASP.NET Core minimal APIs + xUnit (backend); Angular (zoneless, standalone, OnPush signals) + Spartan helm + Vitest/TestBed (frontend).

**Spec:** `docs/superpowers/specs/2026-07-17-fiscal-settings-screen-design.md`

## Global Constraints

- Backend read normalizes legacy `0` months to December via `FiscalYear.MonthOf(registration)`.
- The existing `PUT` handler is **unchanged** — it keeps returning `ClientRegistrationResponse`.
- All wire fields are camelCase JSON (`fiscalYearEndMonth`), number not string.
- Frontend components are `standalone`, `ChangeDetectionStrategy.OnPush`, and use Angular signals.
- Save action is gated in the template by `*appCan="'admin.fiscal'"` and at the route by `canWrite` + `requiredCapability: 'admin.fiscal'`.
- TDD: write the failing test first; commit after each green task.
- A dev-stack SMOKE against real serialization is required before calling the feature done (self-consistent mocks miss wire-format bugs).

---

### Task 1: Backend — symmetric GET fiscal-year-end endpoint

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` (add response record near `SetFiscalYearEndRequest`, line ~19)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` (register + handler)
- Test: `Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs` (add GET trio)

**Interfaces:**
- Consumes: `AdminAuthorization.MayAsync`, `ControlStore.GetClientAsync`, `FiscalYear.MonthOf`, `Capabilities.AdminFiscal` (all existing).
- Produces: `FiscalYearEndResponse(int FiscalYearEndMonth)`; route `GET /admin/clients/{clientId:guid}/fiscal-year-end`.

- [ ] **Step 1: Write the failing tests**

Add to `AdminCapabilityTests.cs` (uses existing `MemberWithAsync`, `fixture`, and `using System.Net.Http.Json;` already present):

```csharp
    [Fact]
    public async Task Member_with_admin_fiscal_may_read_fiscal_year_end()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.AdminFiscal);
        await http.PutAsJsonAsync($"/admin/clients/{clientId}/fiscal-year-end", new SetFiscalYearEndRequest(6));

        HttpResponseMessage resp = await http.GetAsync($"/admin/clients/{clientId}/fiscal-year-end");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        FiscalYearEndResponse? body = await resp.Content.ReadFromJsonAsync<FiscalYearEndResponse>();
        Assert.Equal(6, body!.FiscalYearEndMonth);
    }

    [Fact]
    public async Task Member_without_admin_fiscal_is_forbidden_from_reading_fiscal_year_end()
    {
        (Guid clientId, HttpClient http) = await MemberWithAsync(Capabilities.GlRead);
        HttpResponseMessage resp = await http.GetAsync($"/admin/clients/{clientId}/fiscal-year-end");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Deployment_admin_may_read_fiscal_year_end()
    {
        SeededClient c = await fixture.SeedClientAsync("AdminCapsFiscalRead");
        HttpResponseMessage resp = await fixture.AdminClient().GetAsync($"/admin/clients/{c.ClientId}/fiscal-year-end");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AdminCapabilityTests&FullyQualifiedName~fiscal_year_end"`
Expected: FAIL — `FiscalYearEndResponse` does not exist / GET returns 404 (route unmapped).

- [ ] **Step 3: Add the response contract**

In `AdminContracts.cs`, immediately after the `SetFiscalYearEndRequest` record (line ~19):

```csharp
/// <summary>A client's current fiscal-year-end month (1-12).</summary>
public sealed record FiscalYearEndResponse(int FiscalYearEndMonth);
```

- [ ] **Step 4: Register and implement the GET handler**

In `AdminEndpoints.cs`, register in the `perClient` group next to the existing PUT (after line 26):

```csharp
        perClient.MapGet("/clients/{clientId:guid}/fiscal-year-end", GetFiscalYearEnd);
```

Add the handler (place it just above `SetFiscalYearEnd`, ~line 63):

```csharp
    private static async Task<IResult> GetFiscalYearEnd(
        Guid clientId, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminFiscal, actorFactory, control, cancellationToken))
            return Results.Forbid();

        ClientRegistration? registration = await control.GetClientAsync(clientId, cancellationToken);
        if (registration is null)
            return Results.NotFound();

        return Results.Ok(new FiscalYearEndResponse(FiscalYear.MonthOf(registration)));
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AdminCapabilityTests"`
Expected: PASS (existing PUT trio + new GET trio).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/AdminContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/AdminCapabilityTests.cs
git commit -m "feat(admin): GET fiscal-year-end endpoint (admin.fiscal)"
```

---

### Task 2: Frontend — model, service, component, spec

**Files:**
- Create: `UI/Angular/src/app/core/fiscal/fiscal.ts`
- Create: `UI/Angular/src/app/core/fiscal/fiscal.service.ts`
- Create: `UI/Angular/src/app/features/admin/fiscal-settings.ts`
- Test: `UI/Angular/src/app/features/admin/fiscal-settings.spec.ts`

**Interfaces:**
- Consumes: `GET/PUT ${apiBaseUrl}/admin/clients/${clientId}/fiscal-year-end` returning `{ fiscalYearEndMonth: number }` (Task 1); `ClientContextService`, `CanDirective`, `HlmButton`, `provideCapabilities` (all existing).
- Produces: `FiscalSettings` interface; `FiscalService` (`get()`, `set(month)`); `FiscalSettings` component (selector `app-fiscal-settings`) — consumed by Task 3.

- [ ] **Step 1: Write the failing spec**

Create `UI/Angular/src/app/features/admin/fiscal-settings.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FiscalSettings } from './fiscal-settings';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function seed(...caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities(...caps)],
  });
  TestBed.inject(ClientContextService).select('c1');
}

const url = `${environment.apiBaseUrl}/admin/clients/c1/fiscal-year-end`;

describe('FiscalSettings', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('loads the current month and PUTs the chosen one', () => {
    seed('admin.fiscal'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(FiscalSettings);
    f.detectChanges();
    http.expectOne(url).flush({ fiscalYearEndMonth: 12 });
    f.detectChanges();

    const c = f.componentInstance as FiscalSettings;
    expect(c.selected()).toBe(12);
    expect(c.months.length).toBe(12);

    c.selected.set(6);
    c.save();
    const req = http.expectOne(url);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ fiscalYearEndMonth: 6 });
    req.flush({ fiscalYearEndMonth: 6 });
    expect(c.saved()).toBe(true);
  });

  it('hides Save without admin.fiscal', () => {
    seed('gl.read'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(FiscalSettings);
    f.detectChanges();
    http.expectOne(url).flush({ fiscalYearEndMonth: 12 });
    f.detectChanges();
    const btn = (f.nativeElement as HTMLElement).querySelector('button');
    expect(btn).toBeNull();
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run: `cd UI/Angular && npx vitest run src/app/features/admin/fiscal-settings.spec.ts`
Expected: FAIL — cannot resolve `./fiscal-settings`.

- [ ] **Step 3: Create the model**

Create `UI/Angular/src/app/core/fiscal/fiscal.ts`:

```ts
export interface FiscalSettings {
  fiscalYearEndMonth: number;
}
```

- [ ] **Step 4: Create the service**

Create `UI/Angular/src/app/core/fiscal/fiscal.service.ts`:

```ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { FiscalSettings } from './fiscal';

@Injectable({ providedIn: 'root' })
export class FiscalService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(): string {
    return `${environment.apiBaseUrl}/admin/clients/${this.client.clientId()}/fiscal-year-end`;
  }

  get(): Observable<FiscalSettings> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<FiscalSettings>(this.base());
  }

  set(month: number): Observable<FiscalSettings> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<FiscalSettings>(this.base(), { fiscalYearEndMonth: month });
  }
}
```

- [ ] **Step 5: Create the component**

Create `UI/Angular/src/app/features/admin/fiscal-settings.ts`:

```ts
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { CanDirective } from '../../core/capabilities/can.directive';
import { FiscalService } from '../../core/fiscal/fiscal.service';

interface MonthOption { value: number; label: string; }

@Component({
  selector: 'app-fiscal-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective],
  template: `
    <h1 class="text-xl font-semibold mb-4">Fiscal settings</h1>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }
    @if (saved()) { <p class="text-green-600 mb-2">Saved.</p> }
    <p class="text-sm text-muted-foreground mb-3">The month this client's fiscal year ends.</p>

    <label class="block">
      <span class="text-sm font-medium">Fiscal year-end month</span>
      <select class="mt-1 block w-64 rounded border border-border bg-background px-3 py-2 text-sm"
              [value]="selected()" (change)="select($event)" data-testid="fye-select">
        @for (m of months; track m.value) {
          <option [value]="m.value">{{ m.label }}</option>
        }
      </select>
    </label>

    <p class="text-xs text-muted-foreground mt-2 max-w-prose">Changing this affects future closes only.
       Already-closed years are immutable.</p>

    <div class="flex gap-2 mt-4">
      <button *appCan="'admin.fiscal'" hlmBtn [disabled]="selected() === null" (click)="save()">Save</button>
    </div>
  `,
})
export class FiscalSettings {
  private readonly service = inject(FiscalService);

  readonly months: MonthOption[] = [
    { value: 1, label: 'January' }, { value: 2, label: 'February' }, { value: 3, label: 'March' },
    { value: 4, label: 'April' }, { value: 5, label: 'May' }, { value: 6, label: 'June' },
    { value: 7, label: 'July' }, { value: 8, label: 'August' }, { value: 9, label: 'September' },
    { value: 10, label: 'October' }, { value: 11, label: 'November' }, { value: 12, label: 'December' },
  ];

  readonly selected = signal<number | null>(null);
  readonly error = signal<string | null>(null);
  readonly saved = signal(false);

  constructor() {
    this.service.get().subscribe({
      next: (s) => this.selected.set(s.fiscalYearEndMonth),
      error: () => this.error.set('Could not load fiscal settings.'),
    });
  }

  select(event: Event): void {
    this.selected.set(Number((event.target as HTMLSelectElement).value));
    this.saved.set(false);
  }

  save(): void {
    const month = this.selected();
    if (month === null) return;
    this.error.set(null);
    this.service.set(month).subscribe({
      next: () => this.saved.set(true),
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }
}
```

- [ ] **Step 6: Run the spec to verify it passes**

Run: `cd UI/Angular && npx vitest run src/app/features/admin/fiscal-settings.spec.ts`
Expected: PASS (both tests).

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/fiscal/ UI/Angular/src/app/features/admin/fiscal-settings.ts UI/Angular/src/app/features/admin/fiscal-settings.spec.ts
git commit -m "feat(ui): fiscal settings screen — model, service, component"
```

---

### Task 3: Frontend — route wiring + build verification

**Files:**
- Modify: `UI/Angular/src/app/app.routes.ts` (import, route, `built` array)

**Interfaces:**
- Consumes: `FiscalSettings` component (Task 2); existing `canWrite` guard.
- Produces: navigable `/admin/fiscal` route resolving to the real screen instead of `Placeholder`.

- [ ] **Step 1: Add the component import**

In `app.routes.ts`, after the `ApprovalPolicyScreen` import (line 63):

```ts
import { FiscalSettings } from './features/admin/fiscal-settings';
```

- [ ] **Step 2: Add the route**

Immediately after the `admin/approval-policy` route (line 199):

```ts
  { path: 'admin/fiscal', component: FiscalSettings, canActivate: [canWrite], data: { requiredCapability: 'admin.fiscal', fallback: '/admin/users' } },
```

- [ ] **Step 3: Add `/admin/fiscal` to the `built` array**

In the `built` array (line 206), add `'/admin/fiscal'` (e.g. after `'/admin/approval-policy'`) so the leaf stops falling through to `Placeholder`:

```ts
    const built = ['/dashboard', '/journal', '/trial-balance', '/periods', '/statements', '/accounts', '/receivables', '/payables', '/payroll', '/fixed-assets', '/cash', '/inventory', '/admin/users', '/admin/access/sets', '/admin/access/sets/new', '/admin/approval-policy', '/admin/fiscal', '/audit/trail', '/audit/verify', '/audit/reconciliations'];
```

- [ ] **Step 4: Run the full frontend suite + production build**

Run: `cd UI/Angular && npx vitest run src/app/features/admin && npx ng build --configuration production`
Expected: admin specs PASS; PROD build succeeds within budgets (bundle gate).

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): wire /admin/fiscal route to Fiscal settings screen"
```

---

### Task 4: Dev-stack SMOKE (wire-format verification)

**Files:** none (verification only).

**Interfaces:** Consumes the full stack from Tasks 1–3.

- [ ] **Step 1: Start the dev stack**

Bring up the local Dockerized monolith + Angular per the project's `.localdev/start.ps1` (monolith on :5000; `Authorization: DevToken`). Confirm the acting identity holds `admin.fiscal`.

- [ ] **Step 2: Smoke the read path**

```bash
curl -s -H "Authorization: DevToken" http://localhost:5000/admin/clients/<clientId>/fiscal-year-end
```
Expected: `{"fiscalYearEndMonth":12}` — camelCase key, numeric value (not a string, not PascalCase). This is the check self-consistent unit mocks cannot make.

- [ ] **Step 3: Smoke the write path and re-read**

```bash
curl -s -X PUT -H "Authorization: DevToken" -H "Content-Type: application/json" \
  -d '{"fiscalYearEndMonth":6}' http://localhost:5000/admin/clients/<clientId>/fiscal-year-end
curl -s -H "Authorization: DevToken" http://localhost:5000/admin/clients/<clientId>/fiscal-year-end
```
Expected: PUT returns the `ClientRegistrationResponse` (with `fiscalYearEndMonth:6`); the re-read GET returns `{"fiscalYearEndMonth":6}`. Restore to the original month afterward.

- [ ] **Step 4: Smoke the screen**

Navigate to `/admin/fiscal` in the running UI. Confirm the select shows the current month, changing it and clicking Save shows "Saved.", and a reload reflects the saved value. Restore the original month.

---

## Self-Review

**1. Spec coverage:**
- Backend GET endpoint + `admin.fiscal` gate + `FiscalYear.MonthOf` normalization → Task 1. ✓
- `FiscalYearEndResponse` contract; PUT unchanged → Task 1. ✓
- Model / service / component (native `<select>`, three signals, forward-only note, `*appCan` Save) → Task 2. ✓
- Route + `built` array entry → Task 3. ✓
- Backend GET trio + frontend load/save/hidden-Save specs → Tasks 1–2. ✓
- Dev-stack smoke → Task 4. ✓

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"; every code step shows complete code. ✓

**3. Type consistency:** `FiscalSettings.fiscalYearEndMonth: number` and wire `{ fiscalYearEndMonth }` match across service, component, spec, and backend `FiscalYearEndResponse(int FiscalYearEndMonth)`. `FiscalService.get()/set(month)` names match the component's calls. Component class name `FiscalSettings` matches the route import and spec import. ✓
