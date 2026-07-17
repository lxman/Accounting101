# Block AutoApprove While Pending — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent switching a client to AutoApprove while it has entries awaiting approval — enforced on the backend (422), surfaced on the Approval policy screen (note + disabled AutoApprove option).

**Architecture:** The approval-policy endpoints (already gated by `admin.approvalPolicy`) count `PendingApproval` entries via `ClientLedgerFactory` directly (authorization already done), block the AutoApprove switch when > 0, and return the count on GET so the screen renders the note.

**Tech Stack:** ASP.NET Core minimal APIs + xUnit (backend); Angular (zoneless, standalone, OnPush signals) + Vitest/TestBed (frontend).

**Spec:** `docs/superpowers/specs/2026-07-17-block-autoapprove-while-pending-design.md`

## Global Constraints

- Count via `ClientLedgerFactory.CreateAsync(clientId)` then `ledger.Journal.CountByPostingAsync(clientId, PostingState.PendingApproval, ct)` — a `Task<long>`. NOT via `gateway.ResolveAsync(Permission.Read)` (no `gl.read` coupling); the endpoint's `admin.approvalPolicy` gate is the authorization boundary.
- Only switching TO `AutoApprove` is blocked (when pending > 0). Transitions to/from TwoPerson and SelfApprove are never blocked.
- `PostingState` is in namespace `Accounting101.Ledger.Core.Journal`; `ClientLedgerFactory`/`ClientLedger` in `Accounting101.Ledger.Api.Tenancy`. Add whatever `using`s are needed to compile.
- Block response: `422` with detail `"Cannot enable auto-approve while N entr(y awaits|ies await) approval. Clear the approval queue first."` (singular "entry awaits" when N==1).
- FE `ApprovalPolicy.pendingApprovalCount: number`; component reads it defensively (`?? 0`) so pre-existing spec flushes without the field still work.
- Test runner: `npx ng test --include=<path> --watch=false` (NOT vitest run); prod build gate `npx ng build --configuration production` (< 2MB).
- TDD: failing test first; commit after each green task. Do NOT push. Do NOT stage `UI/Angular/src/app/core/api/environment.ts`.

---

### Task 1: Backend — count pending + guard the AutoApprove switch

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` (`ApprovalPolicyResponse`, ~line 62)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/ApprovalPolicyEndpoints.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyEndpointTests.cs`

**Interfaces:**
- Consumes: `ClientLedgerFactory.CreateAsync`, `ClientLedger.Journal.CountByPostingAsync`, `PostingState.PendingApproval`, `ApprovalPolicy.ModeOf`, `AdminAuthorization.MayAsync`, `ControlStore.GetClientAsync`/`RegisterClientAsync` (all existing).
- Produces: `ApprovalPolicyResponse(ApprovalMode Mode, long PendingApprovalCount)`; PUT returns 422 when AutoApprove requested with pending > 0.

- [ ] **Step 1: Write the failing tests**

Add to `ApprovalPolicyEndpointTests.cs`. It has `using System.Net;`, `using System.Net.Http.Json;`, `using Accounting101.Ledger.Api.Control;`, `using Accounting101.Ledger.Contracts;`. Add a local posting helper (copied from `ApprovalModeEnforcementTests`) and the tests:

```csharp
    private static PostEntryRequest Balanced(Guid? id, string date, Guid debit, Guid credit, decimal amount = 100m) =>
        new(id, DateOnly.Parse(date), null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

    [Fact]
    public async Task Cannot_switch_to_auto_approve_while_entries_await_approval()
    {
        // SelfApprove leaves a post PendingApproval (see ApprovalModeEnforcementTests).
        SeededClient c = await fixture.SeedClientAsync("BlockAuto", approvalMode: ApprovalMode.SelfApprove);
        HttpResponseMessage post = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries",
            Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        post.EnsureSuccessStatusCode();

        HttpResponseMessage put = await fixture.AdminClient().PutAsJsonAsync(
            $"/clients/{c.ClientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.AutoApprove));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);

        // Mode not persisted; GET still reports SelfApprove and the pending count.
        ApprovalPolicyResponse got = (await fixture.AdminClient().GetFromJsonAsync<ApprovalPolicyResponse>(
            $"/clients/{c.ClientId}/approval-policy"))!;
        Assert.Equal(ApprovalMode.SelfApprove, got.Mode);
        Assert.Equal(1, got.PendingApprovalCount);
    }

    [Fact]
    public async Task Can_switch_to_auto_approve_when_nothing_is_pending()
    {
        SeededClient c = await fixture.SeedClientAsync("BlockAutoOk", approvalMode: ApprovalMode.SelfApprove);
        HttpResponseMessage put = await fixture.AdminClient().PutAsJsonAsync(
            $"/clients/{c.ClientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.AutoApprove));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    [Fact]
    public async Task Switching_to_two_person_is_not_blocked_by_pending_entries()
    {
        SeededClient c = await fixture.SeedClientAsync("PendingTwoPerson", approvalMode: ApprovalMode.SelfApprove);
        HttpResponseMessage post = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries",
            Balanced(Guid.NewGuid(), "2026-03-31", Guid.NewGuid(), Guid.NewGuid()));
        post.EnsureSuccessStatusCode();

        HttpResponseMessage put = await fixture.AdminClient().PutAsJsonAsync(
            $"/clients/{c.ClientId}/approval-policy", new SetApprovalPolicyRequest(ApprovalMode.TwoPerson));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~ApprovalPolicyEndpointTests"`
Expected: FAIL — `ApprovalPolicyResponse` has no `PendingApprovalCount` (compile error) and the AutoApprove-with-pending switch currently returns 200.

- [ ] **Step 3: Add the count to the contract**

In `AdminContracts.cs`, change `ApprovalPolicyResponse` (keep the doc comment):

```csharp
/// <summary>A client's current approval mode, plus how many entries are awaiting approval
/// (drives the "clear the queue before enabling auto-approve" guard).</summary>
public sealed record ApprovalPolicyResponse(ApprovalMode Mode, long PendingApprovalCount);
```

Grep for other construction sites so none break:
Run: `grep -rn "new ApprovalPolicyResponse(" Backend/`
Expected: only `ApprovalPolicyEndpoints.cs` (updated next). Update any other site with the trailing arg.

- [ ] **Step 4: Count pending + guard in the endpoints**

In `ApprovalPolicyEndpoints.cs`, add `using`s for `Accounting101.Ledger.Api.Tenancy` and `Accounting101.Ledger.Core.Journal` (add whatever the compiler needs). Add a count helper, inject `ClientLedgerFactory` into both handlers, add the GET field and the PUT guard:

```csharp
    private static async Task<long> CountPendingAsync(
        ClientLedgerFactory ledgers, Guid clientId, CancellationToken ct)
    {
        ClientLedger? ledger = await ledgers.CreateAsync(clientId, ct);
        return ledger is null ? 0 : await ledger.Journal.CountByPostingAsync(clientId, PostingState.PendingApproval, ct);
    }
```

GET — add `ClientLedgerFactory ledgers` to the parameter list and return the count:

```csharp
    private static async Task<IResult> GetApprovalPolicy(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        ClientLedgerFactory ledgers, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminApprovalPolicy, actorFactory, control, ct))
            return Results.Forbid();

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();

        long pending = await CountPendingAsync(ledgers, clientId, ct);
        return Results.Ok(new ApprovalPolicyResponse(ApprovalPolicy.ModeOf(client), pending));
    }
```

PUT — add `ClientLedgerFactory ledgers`, count once, block AutoApprove when pending > 0, return the count:

```csharp
    private static async Task<IResult> SetApprovalPolicy(
        Guid clientId, SetApprovalPolicyRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, ClientLedgerFactory ledgers, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminApprovalPolicy, actorFactory, control, ct))
            return Results.Forbid();

        if (request.Mode == ApprovalMode.Unspecified)
            return Results.Problem("Mode must be TwoPerson, SelfApprove, or AutoApprove.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();

        long pending = await CountPendingAsync(ledgers, clientId, ct);
        if (request.Mode == ApprovalMode.AutoApprove && pending > 0)
            return Results.Problem(
                $"Cannot enable auto-approve while {pending} {(pending == 1 ? "entry awaits" : "entries await")} approval. Clear the approval queue first.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        client.ApprovalMode = request.Mode;
        await control.RegisterClientAsync(client, ct);
        return Results.Ok(new ApprovalPolicyResponse(request.Mode, pending));
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~ApprovalPolicyEndpointTests"`
Expected: PASS — existing tests (they read only `.Mode`; the extra JSON field is tolerated) plus the 3 new ones.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/AdminContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/ApprovalPolicyEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/ApprovalPolicyEndpointTests.cs
git commit -m "feat(approval): block AutoApprove while entries await approval"
```

---

### Task 2: Frontend — note + disabled AutoApprove option

**Files:**
- Modify: `UI/Angular/src/app/core/approval-policy/approval-policy.ts` (model)
- Modify: `UI/Angular/src/app/features/admin/approval-policy.ts`
- Test: `UI/Angular/src/app/features/admin/approval-policy.spec.ts`

**Interfaces:**
- Consumes: `GET /clients/{id}/approval-policy` now returns `{ mode, pendingApprovalCount }` (Task 1).
- Produces: screen disables AutoApprove + shows a note (with a `/journal/approvals` link) when `pendingApprovalCount > 0`.

- [ ] **Step 1: Write the failing tests**

In `approval-policy.spec.ts`, add tests. The file already has `seed()` (with `provideRouter([]))`, `HttpTestingController`, `environment`. Add:

```ts
  it('disables Auto-approve and shows a note when entries are pending', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'TwoPerson', pendingApprovalCount: 2 });
    f.detectChanges();

    const el = f.nativeElement as HTMLElement;
    const autoRadio = Array.from(el.querySelectorAll('input[type=radio]'))
      .find((r) => (r as HTMLInputElement).value === 'AutoApprove') as HTMLInputElement;
    expect(autoRadio.disabled).toBe(true);
    const note = el.querySelector('[data-testid=pending-note]');
    expect(note?.textContent).toContain('2 entries are');
    expect(el.querySelector('[data-testid=pending-note] a[href="/journal/approvals"]')).not.toBeNull();
  });

  it('enables Auto-approve and shows no note when nothing is pending', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'TwoPerson', pendingApprovalCount: 0 });
    f.detectChanges();

    const el = f.nativeElement as HTMLElement;
    const autoRadio = Array.from(el.querySelectorAll('input[type=radio]'))
      .find((r) => (r as HTMLInputElement).value === 'AutoApprove') as HTMLInputElement;
    expect(autoRadio.disabled).toBe(false);
    expect(el.querySelector('[data-testid=pending-note]')).toBeNull();
  });

  it('surfaces a 422 detail from a failed save', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'SelfApprove', pendingApprovalCount: 0 });
    f.detectChanges();

    const c = f.componentInstance as ApprovalPolicyScreen;
    c.select('AutoApprove');   // allowed here: count is 0
    c.save();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush(
      { detail: 'Cannot enable auto-approve while 1 entry awaits approval. Clear the approval queue first.' },
      { status: 422, statusText: 'Unprocessable Entity' });
    f.detectChanges();

    expect((f.nativeElement as HTMLElement).textContent).toContain('Cannot enable auto-approve');
  });
```

- [ ] **Step 2: Run the spec to verify it fails**

Run: `cd UI/Angular && npx ng test --include=src/app/features/admin/approval-policy.spec.ts --watch=false`
Expected: FAIL — no `pending-note`, AutoApprove not disabled (model/component not updated yet).

- [ ] **Step 3: Add the model field**

In `core/approval-policy/approval-policy.ts`:

```ts
export type ApprovalMode = 'TwoPerson' | 'SelfApprove' | 'AutoApprove';

export interface ApprovalPolicy {
  mode: ApprovalMode;
  pendingApprovalCount: number;
}
```

- [ ] **Step 4: Update the component**

In `features/admin/approval-policy.ts`:

Add `RouterLink` to the imports:

```ts
import { RouterLink } from '@angular/router';
```

Add it to the component `imports` array: `imports: [HlmButton, CanDirective, RouterLink],`.

Add the count signal + helpers to the class (after `saved`):

```ts
  readonly pendingApprovalCount = signal(0);

  isAutoApproveBlocked(mode: ApprovalMode): boolean {
    return mode === 'AutoApprove' && this.pendingApprovalCount() > 0;
  }

  pendingCountText(): string {
    const n = this.pendingApprovalCount();
    return n === 1 ? '1 entry is' : `${n} entries are`;
  }
```

Set the count on load — update the constructor's `next`:

```ts
    this.service.get().subscribe({
      next: (p) => { this.selected.set(p.mode); this.pendingApprovalCount.set(p.pendingApprovalCount ?? 0); },
      error: () => this.error.set('Could not load the approval policy.'),
    });
```

Guard `select()` against picking a blocked option:

```ts
  select(mode: ApprovalMode): void {
    if (this.isAutoApproveBlocked(mode)) return;
    this.selected.set(mode);
    this.saved.set(false);
  }
```

Update the template's radio block to disable the blocked option and render the note. Replace the `@for` option `<label>` body with:

```html
      @for (o of options; track o.value) {
        <label class="flex items-start gap-3" [class.opacity-60]="isAutoApproveBlocked(o.value)">
          <input type="radio" name="mode" [value]="o.value" [checked]="selected() === o.value"
                 [disabled]="isAutoApproveBlocked(o.value)"
                 (change)="select(o.value)" class="mt-1" />
          <span>
            <span class="font-medium">{{ o.label }}</span>
            @if (o.lowControl) {
              <span class="ms-2 text-xs rounded bg-amber-100 text-amber-800 px-1.5 py-0.5">removes a review step</span>
            }
            <span class="block text-sm text-muted-foreground">{{ o.description }}</span>
            @if (o.value === 'AutoApprove' && pendingApprovalCount() > 0) {
              <span class="block text-sm text-amber-700 mt-1" data-testid="pending-note">
                {{ pendingCountText() }} awaiting approval. Clear the
                <a routerLink="/journal/approvals" class="underline">approval queue</a>
                before enabling auto-approve.
              </span>
            }
          </span>
        </label>
      }
```

- [ ] **Step 5: Run the spec to verify it passes**

Run: `cd UI/Angular && npx ng test --include=src/app/features/admin/approval-policy.spec.ts --watch=false`
Expected: PASS — the 3 new tests plus the pre-existing ones (which flush without `pendingApprovalCount`; the component defaults it to 0 via `?? 0`).

- [ ] **Step 6: Production build gate**

Run: `cd UI/Angular && npx ng build --configuration production`
Expected: succeeds within budgets (< 2MB error gate).

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/approval-policy/approval-policy.ts UI/Angular/src/app/features/admin/approval-policy.ts UI/Angular/src/app/features/admin/approval-policy.spec.ts
git commit -m "feat(ui): note + disabled AutoApprove while approvals pending"
```

---

### Task 3: Dev-stack SMOKE (live, JordanSoft)

**Files:** none (verification only).

**Interfaces:** Consumes the full stack from Tasks 1–2.

- [ ] **Step 1: Deploy the branch**

Run `C:\Users\jorda\OneDrive\Documents\JordanSoft\deploy\update.ps1` (backup first; mongo data untouched).

Build the Owner DevToken as in prior smokes: base64url of `{"sub":"00000000-0000-0000-0000-000000000005","name":"Owner","claims":[{"type":"role","value":"Admin"},{"type":"admin","value":"true"}]}`. Client `761f80b1-f0b5-4927-b8de-dedf84477e59`.

- [ ] **Step 2: Arrange a pending entry under a non-AutoApprove mode**

JordanSoft is AutoApprove (nothing pends). Switch it to SelfApprove, then post a balanced entry (it will stay PendingApproval):

```bash
curl -s -X PUT -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"mode":"SelfApprove"}' http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/approval-policy
curl -s -X POST -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"effectiveDate":"2026-03-31","lines":[{"accountId":"'"$(uuidgen)"'","side":"Debit","amount":10},{"accountId":"'"$(uuidgen)"'","side":"Credit","amount":10}]}' \
  http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/entries
```
Expected: the POST returns `"posting":"PendingApproval"`. (If `uuidgen` is unavailable, substitute any two fresh GUIDs. Note the returned entry `id` for cleanup.)

- [ ] **Step 3: Confirm the block (API + GET count)**

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X PUT -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"mode":"AutoApprove"}' http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/approval-policy
curl -s -H "Authorization: DevToken <token>" http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/approval-policy
```
Expected: the PUT → `422`; the GET → `{"mode":"SelfApprove","pendingApprovalCount":1}` (or higher if other pending exist).

- [ ] **Step 4: Confirm the screen (browser)**

Open `http://localhost:4200/admin/approval-policy`. The **Auto-approve** radio is disabled with the amber note "1 entry is awaiting approval. Clear the approval queue …" and the "approval queue" link points to `/journal/approvals`.

- [ ] **Step 5: Clear the pending entry, confirm the switch succeeds, restore**

Approve the pending entry (its author may self-approve under SelfApprove), then switch to AutoApprove:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST -H "Authorization: DevToken <token>" \
  http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/entries/<entryId>/approve
curl -s -o /dev/null -w '%{http_code}\n' -X PUT -H "Authorization: DevToken <token>" -H "Content-Type: application/json" \
  -d '{"mode":"AutoApprove"}' http://localhost:5000/clients/761f80b1-f0b5-4927-b8de-dedf84477e59/approval-policy
```
Expected: approve → `200`; the AutoApprove PUT → `200`. Final `GET /me/capabilities` (or `/approval-policy`) reports AutoApprove — **JordanSoft restored**. If the posted entry cannot be cleanly approved, void it instead, then restore AutoApprove; the goal is to leave JordanSoft on AutoApprove with no residual pending test entry.

---

## Self-Review

**1. Spec coverage:**
- Backend count via `ClientLedgerFactory` + PUT 422 guard + GET count field → Task 1. ✓
- Contract `ApprovalPolicyResponse(Mode, PendingApprovalCount)` → Task 1. ✓
- Non-AutoApprove transitions never blocked → Task 1 (test `Switching_to_two_person_...`). ✓
- FE model field, disabled AutoApprove, note + queue link, 422 surfaced → Task 2. ✓
- Backend tests (block / allow-when-clear / other-transition-ok / GET count) → Task 1. ✓
- FE tests (disabled+note / enabled+no-note / 422 surfaced) → Task 2. ✓
- Dev-stack smoke → Task 3. ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows complete code.

**3. Type consistency:** `ApprovalPolicyResponse(ApprovalMode Mode, long PendingApprovalCount)` (backend) ↔ `ApprovalPolicy { mode; pendingApprovalCount: number }` (FE) match by JSON name (`mode`, `pendingApprovalCount`). `pending` counted once in PUT, used for both guard and response. `isAutoApproveBlocked`/`pendingCountText`/`pendingApprovalCount` names consistent across component and template. The 422 detail string format matches between the backend (`entry awaits`/`entries await`) and is surfaced verbatim by the FE error banner.
