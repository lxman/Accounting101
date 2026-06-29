# UI Slice 1c — Journal Write Path (Post · Approve · Detail) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the Journal write path — post a balanced journal entry (Angular Signal Forms Dr/Cr grid), approve it through maker-checker, and inspect entry detail — wired to the real engine via the DevToken scheme, with a dev identity switcher so SoD is exercisable in one browser.

**Architecture:** A root `DevIdentityService` holds the active dev identity (Clerk or Approver) as a signal; the existing `authInterceptor` mints the DevToken from it per request, and a top-bar "Acting as" switcher flips it. The post-entry form uses experimental Signal Forms (`@angular/forms/signals`) — `form()` + `schema()` with `applyEach` (per-line) and `validateTree` (balance + ≥2 lines). The write methods extend the Slice-1b `EntriesService`; a new `AuditService` reads `GET /audit/{id}` for the author cue (D3) and audit stamps (D4). All screens are zoneless/OnPush standalone components; tests mock HTTP via `HttpTestingController` — no live backend.

**Tech Stack:** Angular 22 (standalone, signals, zoneless, OnPush), Signal Forms (`@angular/forms/signals`), Tailwind v4, Spartan UI (hlm), Vitest.

## Global Constraints

- **Zoneless + OnPush** on every component; standalone with `standalone: true` **omitted**; signals + `input()`/`output()`/signal queries; `@if`/`@for` (no `*ngIf`/`*ngFor`); `inject()` DI; functional interceptors.
- **Money/dates render ONLY through the formatter** (`core/format/`): `formatMoney(amount,'USD',DEFAULT_FORMAT_PROFILE,{symbol})`, `formatProfileDate(...)`. Decimal-aligned, tabular numerals, accounting parens for negatives. Never hand-format money.
- **API returns raw `decimal` + ISO dates** (camelCase JSON). Services return typed DTOs; components format. No client-side recomputation of server aggregates (the form footer totals are input echoes, not server figures).
- **DevToken scheme** (NOT Bearer): `Authorization: DevToken <base64url(utf8(json({sub,name,claims})))>` — minted by the existing `encodeDevToken`.
- Env: `nvm use 24.18.0` before npm/ng (in Bash subshells `export PATH="/c/nvm4w/nodejs:$PATH"`). Test: `ng test --watch=false` (Vitest, no `--browsers`). Build: `npm run build`. Work from `UI/Angular`.
- Commit trailer verbatim on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Exact engine contracts (from backend recon)

- `POST /clients/{clientId}/entries` — body `PostEntryRequest`; **201** `PostEntryResponse {id,status,posting}` (posting `"PendingApproval"`).
- `POST /clients/{clientId}/entries/validate` — same body; **200** `{valid:true}` or the same **409/422** ProblemDetails a real post returns (side-effect-free).
- `POST /clients/{clientId}/entries/{entryId}/approve` — **200** `EntryResponse`; **403** if SoD and author == approver; **409** lifecycle/period; **404** unknown.
- `POST /clients/{clientId}/entries/{entryId}/void` — body `VoidRequest {reason?:string}`; **200** `EntryResponse`; **409**/**404**.
- `GET /clients/{clientId}/entries/{entryId}` — `EntryResponse`.
- `GET /clients/{clientId}/audit/{entryId}` — `AuditRecordResponse[]` (each `{sequence,action,entryId,entryVersion,at,reason,actor:{userId,name,claims}}`). The author is the record with `action === "Created"`.
- `GET /clients/{clientId}/entries?posting=PendingApproval&skip=&limit=` — paged `PagedResponse<EntryResponse>` (reuse Slice-1b `EntriesService.listPaged`).

**Request DTOs (camelCase on the wire):**
```
PostEntryRequest { id?: string|null; effectiveDate: string; reference?: string|null; memo?: string|null;
                   lines: PostLineRequest[]; sourceRef?: string|null; sourceType?: string|null; type?: string|null; }
PostLineRequest  { accountId: string; direction: 'Debit'|'Credit'; amount: number; dimensions?: Record<string,string>|null; }
```
1c sends only `effectiveDate`, `reference`, `memo`, `lines` (no id/sourceRef/sourceType; `type` = `'Standard'|'Adjusting'`; no `dimensions`).

**Signal Forms API facts (verified against installed @angular/forms 22.0.4):**
- Imports from `@angular/forms/signals`: `form`, `schema`, `applyEach`, `validate`, `validateTree`, `required`, and the binding directive **`FormField`** (selector `[formField]`, required signal input; bind to native inputs/textarea).
- `form(model: WritableSignal<T>, schemaFn)` returns a `FieldTree<T>`. The tree is callable: `tree()` → `FieldState` with signals `value` (WritableSignal), `valid()`, `invalid()`, `errors()` (`{kind,message,...}[]`), `touched()`. Sub-fields by property: `tree.effectiveDate`; arrays by index: `tree.lines[i].accountId`.
- `validate(path, ({value}) => err | err[] | undefined)` attaches `{kind,message}` errors to `path`. `validateTree(path, ({value}) => …)` returns errors targeting the (array) field; read them at `tree.lines().errors()`.
- `applyEach(path.lines, (line) => { required(line.accountId); validate(line, …); })` runs the schema per item.
- Mutating the model signal updates the form (the model is the source of truth). Add/remove rows = `model.update(v => ({...v, lines:[...]}))`.
- **hlm-select interop:** bind selects manually to the field value (`(valueChange)` → `tree.lines[i].accountId().value.set($event)`) rather than `[formField]`, since spartan brn-select CVA↔signal-forms interop is unverified. Native inputs (date/text/number/textarea) use `[formField]`.

---

## Prerequisite (run once, before Task 1) — generate the hlm input + label

Interactive Spartan generators; run at the terminal (Node 24, from `UI/Angular`). `components.json` already exists, so these just copy source into `libs/ui`:
```
ng g @spartan-ng/cli:ui input
ng g @spartan-ng/cli:ui label
```
Commit:
```
git add UI/Angular/libs UI/Angular/components.json UI/Angular/package*.json UI/Angular/tsconfig*.json
git commit -m "feat(ui): generate hlm input + label for the write-path forms

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```
Import aliases: `@spartan-ng/helm/input`, `@spartan-ng/helm/label`. Task implementers assume these exist.

---

### Task 1: DevIdentityService + two configured identities + interceptor

**Files:**
- Modify: `src/app/core/api/environment.ts` (two identities)
- Create: `src/app/core/api/dev-identity.service.ts`, `dev-identity.service.spec.ts`
- Modify: `src/app/core/api/auth.interceptor.ts` (read active identity from the service)
- Modify: `src/app/core/api/auth.interceptor.spec.ts`

**Interfaces:**
- Consumes: `encodeDevToken(payload)`, `DevTokenPayload`, `DevClaim` (`core/api/dev-token.ts`, Slice 1a).
- Produces: `DevIdentity { sub: string; name: string; claims: DevClaim[] }`; `DevIdentityService.active` (signal), `.identities` (readonly list), `.use(sub: string)`. Consumed by the interceptor (Task 1), the shell switcher (Task 2), the queue + detail (Tasks 5–6, the SoD cue).

- [ ] **Step 1: environment — two identities**

Replace the single `devUserId/devUserName/devClaims` triple in `environment.ts` with a clerk + approver pair (keep `apiBaseUrl`, `devClientId`):
```ts
export interface DevIdentityConfig { sub: string; name: string; claims: { type: string; value: string }[]; }

export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5000',
  // Two dev identities so maker-checker (author ≠ approver) is exercisable in one browser.
  // Both must map to a control-DB membership for the demo client to get real data (not a build/test dependency).
  devClerk: {
    sub: '00000000-0000-0000-0000-000000000001',
    name: 'Dev Clerk',
    claims: [{ type: 'role', value: 'Controller' }],
  } as DevIdentityConfig,
  devApprover: {
    sub: '00000000-0000-0000-0000-000000000002',
    name: 'Dev Approver',
    claims: [{ type: 'role', value: 'Approver' }, { type: 'admin', value: 'true' }],
  } as DevIdentityConfig,
  devClientId: '' as string, // set to the seeded demo client's Guid before demoing
};
```

- [ ] **Step 2: Write the failing test** — `dev-identity.service.spec.ts`

```ts
import { TestBed } from '@angular/core/testing';
import { DevIdentityService } from './dev-identity.service';
import { environment } from './environment';

describe('DevIdentityService', () => {
  let svc: DevIdentityService;
  beforeEach(() => { TestBed.configureTestingModule({}); svc = TestBed.inject(DevIdentityService); });

  it('defaults to the clerk identity', () => {
    expect(svc.active().sub).toBe(environment.devClerk.sub);
    expect(svc.active().name).toBe('Dev Clerk');
  });

  it('lists both identities', () => {
    expect(svc.identities.map(i => i.sub)).toEqual([environment.devClerk.sub, environment.devApprover.sub]);
  });

  it('use(sub) switches the active identity', () => {
    svc.use(environment.devApprover.sub);
    expect(svc.active().sub).toBe(environment.devApprover.sub);
  });

  it('use(unknown) is a no-op', () => {
    svc.use('nope');
    expect(svc.active().sub).toBe(environment.devClerk.sub);
  });
});
```

- [ ] **Step 3: Run it — verify it fails**

Run: `export PATH="/c/nvm4w/nodejs:$PATH" && ng test --watch=false -- dev-identity.service.spec.ts` (or full run)
Expected: FAIL — `DevIdentityService` does not exist.

- [ ] **Step 4: Implement `dev-identity.service.ts`**

```ts
import { Injectable, signal } from '@angular/core';
import { environment, DevIdentityConfig } from './environment';

export interface DevIdentity { sub: string; name: string; claims: { type: string; value: string }[]; }

@Injectable({ providedIn: 'root' })
export class DevIdentityService {
  readonly identities: readonly DevIdentity[] = [environment.devClerk, environment.devApprover];
  private readonly _active = signal<DevIdentity>(this.identities[0]);
  readonly active = this._active.asReadonly();

  use(sub: string): void {
    const match = this.identities.find(i => i.sub === sub);
    if (match) this._active.set(match);
  }
}
```

- [ ] **Step 5: Run it — verify pass**

Run: `ng test --watch=false`
Expected: the 4 new tests PASS (auth.interceptor.spec will fail until Step 6 — that's next).

- [ ] **Step 6: Update the interceptor to read the active identity**

`auth.interceptor.ts`:
```ts
import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { encodeDevToken } from './dev-token';
import { DevIdentityService } from './dev-identity.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const id = inject(DevIdentityService).active();
  if (!id.sub) return next(req);
  const token = encodeDevToken({ sub: id.sub, name: id.name, claims: id.claims });
  return next(req.clone({ setHeaders: { Authorization: `DevToken ${token}` } }));
};
```

- [ ] **Step 7: Update `auth.interceptor.spec.ts`**

The interceptor now depends on `DevIdentityService`. Run it through `HttpClient` so `inject()` works (interceptors run in injection context). Replace the spec body:
```ts
import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor } from './auth.interceptor';
import { DevIdentityService } from './dev-identity.service';
import { encodeDevToken } from './dev-token';
import { environment } from './environment';

describe('authInterceptor', () => {
  let http: HttpClient; let ctrl: HttpTestingController; let ids: DevIdentityService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient); ctrl = TestBed.inject(HttpTestingController); ids = TestBed.inject(DevIdentityService);
  });
  afterEach(() => ctrl.verify());

  it('sets a DevToken for the active (clerk) identity', () => {
    http.get('/x').subscribe();
    const req = ctrl.expectOne('/x');
    const expected = encodeDevToken({ sub: environment.devClerk.sub, name: environment.devClerk.name, claims: environment.devClerk.claims });
    expect(req.request.headers.get('Authorization')).toBe(`DevToken ${expected}`);
    req.flush({});
  });

  it('re-mints the token after switching identity', () => {
    ids.use(environment.devApprover.sub);
    http.get('/y').subscribe();
    const req = ctrl.expectOne('/y');
    const expected = encodeDevToken({ sub: environment.devApprover.sub, name: environment.devApprover.name, claims: environment.devApprover.claims });
    expect(req.request.headers.get('Authorization')).toBe(`DevToken ${expected}`);
    req.flush({});
  });
});
```

- [ ] **Step 8: Run full tests — verify pass**

Run: `ng test --watch=false`
Expected: PASS. If any other spec referenced the removed `environment.devUserId`, update it to `environment.devClerk.sub` (grep first: `grep -rn "devUserId\|devUserName\|devClaims" src`).

- [ ] **Step 9: Build + commit**

Run: `npm run build` (expected: success).
```bash
git add src/app/core/api
git commit -m "feat(ui): dev identity service + switchable DevToken (maker-checker)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: "Acting as" switcher in the top-bar shell

**Files:**
- Modify: `src/app/layout/shell.ts` (add the switcher), `src/app/layout/shell.spec.ts`

**Interfaces:**
- Consumes: `DevIdentityService.active`, `.identities`, `.use(sub)` (Task 1); `HlmSelectImports` (`@spartan-ng/helm/select`).

- [ ] **Step 1: Write the failing test** — append to `shell.spec.ts`

```ts
it('switches the active dev identity from the top bar', () => {
  const fixture = TestBed.createComponent(Shell);
  fixture.detectChanges();
  const ids = TestBed.inject(DevIdentityService);
  ids.use(environment.devApprover.sub);
  fixture.detectChanges();
  expect(fixture.nativeElement.textContent).toContain('Dev Approver');
});
```
Add imports at the top of the spec: `import { DevIdentityService } from '../core/api/dev-identity.service';` and `import { environment } from '../core/api/environment';`. (Keep the existing shell test setup — it already provides zoneless CD + router.)

- [ ] **Step 2: Run it — verify it fails**

Run: `ng test --watch=false -- shell.spec.ts`
Expected: FAIL — "Dev Approver" not rendered (no switcher yet).

- [ ] **Step 3: Add the switcher to `shell.ts`**

Add to imports: `import { DevIdentityService } from '../core/api/dev-identity.service';` and `import { HlmSelectImports } from '@spartan-ng/helm/select';`. Add `...HlmSelectImports` to the component `imports`. Inject `protected readonly identity = inject(DevIdentityService);`. Replace the placeholder `Jordan ▾` span with an "Acting as" select:
```html
<div hlmSelect [value]="identity.active().sub" (valueChange)="identity.use($any($event))" class="w-44">
  <hlm-select-trigger class="w-44">
    <hlm-select-value placeholder="Acting as…">Acting as: {{ identity.active().name }}</hlm-select-value>
  </hlm-select-trigger>
  <hlm-select-content>
    @for (id of identity.identities; track id.sub) {
      <hlm-select-item [value]="id.sub">{{ id.name }}</hlm-select-item>
    }
  </hlm-select-content>
</div>
```
(Keep the existing client button, Edit Firm/Client, and theme switch. The select sits where the `Jordan ▾` span was.)

- [ ] **Step 4: Run it — verify pass**

Run: `ng test --watch=false -- shell.spec.ts`
Expected: PASS. If the hlm-select content is not rendered synchronously in the test, assert against the trigger text (`Acting as: Dev Approver`) which is bound directly to `identity.active().name` — adjust the expectation to `toContain('Dev Approver')` on the trigger, which holds regardless.

- [ ] **Step 5: Build + commit**

Run: `npm run build`.
```bash
git add src/app/layout
git commit -m "feat(ui): top-bar 'Acting as' dev identity switcher

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Write-path service methods + audit service + problem-details helper

**Files:**
- Modify: `src/app/core/entries/entry.ts` (add request DTOs)
- Modify: `src/app/core/entries/entries.service.ts` (add post/validate/approve/void/get), `entries.service.spec.ts`
- Create: `src/app/core/audit/audit.ts` (DTOs), `audit.service.ts`, `audit.service.spec.ts`
- Create: `src/app/core/api/problem-details.ts`, `problem-details.spec.ts`

**Interfaces:**
- Consumes: `ClientContextService.clientId()`, `PagedResponse`, `EntryResponse` (1a/1b).
- Produces:
  - `PostLineRequest`, `PostEntryRequest`, `PostEntryResponse`, `EntryValidationResponse` types.
  - `EntriesService.post(req)`, `.validate(req)`, `.approve(id)`, `.void(id, reason?)`, `.get(id)`.
  - `AuditService.entryAudit(id)` → `Observable<AuditRecordResponse[]>`; `AuditService.authorOf(records)` → `string | null`.
  - `extractProblem(err: unknown)` → `{ detail: string; fieldErrors: Record<string,string[]> }`.

- [ ] **Step 1: Add request DTOs to `entry.ts`**

Append:
```ts
export interface PostLineRequest { accountId: string; direction: Direction; amount: number; dimensions?: Record<string, string> | null; }
export interface PostEntryRequest {
  effectiveDate: string; reference?: string | null; memo?: string | null;
  lines: PostLineRequest[]; type?: 'Standard' | 'Adjusting' | null;
}
export interface PostEntryResponse { id: string; status: string; posting: Posting; }
export interface EntryValidationResponse { valid: boolean; }
```

- [ ] **Step 2: Write the failing test** — add to `entries.service.spec.ts`

```ts
it('post() POSTs to /entries and returns PostEntryResponse', () => {
  const client = TestBed.inject(ClientContextService); client.select('C1');
  const req: PostEntryRequest = { effectiveDate: '2026-06-29', reference: 'R', memo: 'M', type: 'Standard',
    lines: [{ accountId: 'A', direction: 'Debit', amount: 10 }, { accountId: 'B', direction: 'Credit', amount: 10 }] };
  let res: PostEntryResponse | undefined;
  svc.post(req).subscribe(r => (res = r));
  const http = ctrl.expectOne('http://localhost:5000/clients/C1/entries');
  expect(http.request.method).toBe('POST');
  expect(http.request.body).toEqual(req);
  http.flush({ id: 'E1', status: 'Active', posting: 'PendingApproval' });
  expect(res).toEqual({ id: 'E1', status: 'Active', posting: 'PendingApproval' });
});

it('validate() POSTs to /entries/validate', () => {
  TestBed.inject(ClientContextService).select('C1');
  svc.validate({ effectiveDate: '2026-06-29', lines: [] }).subscribe();
  const http = ctrl.expectOne('http://localhost:5000/clients/C1/entries/validate');
  expect(http.request.method).toBe('POST'); http.flush({ valid: true });
});

it('approve() POSTs to /entries/{id}/approve', () => {
  TestBed.inject(ClientContextService).select('C1');
  svc.approve('E1').subscribe();
  const http = ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1/approve');
  expect(http.request.method).toBe('POST'); http.flush({});
});

it('void() POSTs reason to /entries/{id}/void', () => {
  TestBed.inject(ClientContextService).select('C1');
  svc.void('E1', 'oops').subscribe();
  const http = ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1/void');
  expect(http.request.method).toBe('POST'); expect(http.request.body).toEqual({ reason: 'oops' }); http.flush({});
});

it('get() GETs a single entry', () => {
  TestBed.inject(ClientContextService).select('C1');
  svc.get('E1').subscribe();
  const http = ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1');
  expect(http.request.method).toBe('GET'); http.flush({});
});
```
Add imports to the spec: `PostEntryRequest, PostEntryResponse` from `./entry`, and `ClientContextService` (if not already imported). Use the existing spec's `svc`/`ctrl` setup.

- [ ] **Step 3: Run it — verify it fails**

Run: `ng test --watch=false -- entries.service.spec.ts`
Expected: FAIL — `svc.post`/`validate`/`approve`/`void`/`get` not functions.

- [ ] **Step 4: Implement the methods in `entries.service.ts`**

Add (keep existing `listPaged`):
```ts
private url(path = ''): string {
  return `${environment.apiBaseUrl}/clients/${this.client.clientId()}/entries${path}`;
}
post(req: PostEntryRequest) { return this.http.post<PostEntryResponse>(this.url(), req); }
validate(req: PostEntryRequest) { return this.http.post<EntryValidationResponse>(this.url('/validate'), req); }
approve(id: string) { return this.http.post<EntryResponse>(this.url(`/${id}/approve`), {}); }
void(id: string, reason?: string) { return this.http.post<EntryResponse>(this.url(`/${id}/void`), { reason: reason ?? null }); }
get(id: string) { return this.http.get<EntryResponse>(this.url(`/${id}`)); }
```
Import the new types (`PostEntryRequest, PostEntryResponse, EntryValidationResponse, EntryResponse`). (The void test expects `{reason:'oops'}`; when no reason, body is `{reason:null}` — adjust that test if you prefer; the contract accepts a nullable reason.)

- [ ] **Step 5: Run it — verify pass** — `ng test --watch=false -- entries.service.spec.ts` → PASS.

- [ ] **Step 6: Audit DTOs + service (TDD)**

`audit.ts`:
```ts
export interface ClaimResponse { type: string; value: string; }
export interface ActorResponse { userId: string; name: string | null; claims: ClaimResponse[]; }
export interface AuditRecordResponse {
  sequence: number; action: string; entryId: string | null; entryVersion: number;
  at: string; reason: string | null; actor: ActorResponse;
}
```
`audit.service.spec.ts`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuditService } from './audit.service';
import { AuditRecordResponse } from './audit';
import { ClientContextService } from '../client/client-context.service';

describe('AuditService', () => {
  let svc: AuditService; let ctrl: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    svc = TestBed.inject(AuditService); ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  });
  afterEach(() => ctrl.verify());

  it('GETs /audit/{id}', () => {
    svc.entryAudit('E1').subscribe();
    const http = ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1');
    expect(http.request.method).toBe('GET'); http.flush([]);
  });

  it('authorOf returns the userId of the Created record', () => {
    const recs: AuditRecordResponse[] = [
      { sequence: 2, action: 'Posted', entryId: 'E1', entryVersion: 1, at: '', reason: null, actor: { userId: 'U2', name: null, claims: [] } },
      { sequence: 1, action: 'Created', entryId: 'E1', entryVersion: 1, at: '', reason: null, actor: { userId: 'U1', name: 'Clerk', claims: [] } },
    ];
    expect(svc.authorOf(recs)).toBe('U1');
  });

  it('authorOf falls back to null when no Created record', () => {
    expect(svc.authorOf([])).toBeNull();
  });
});
```
`audit.service.ts`:
```ts
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { AuditRecordResponse } from './audit';

@Injectable({ providedIn: 'root' })
export class AuditService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  entryAudit(entryId: string) {
    return this.http.get<AuditRecordResponse[]>(`${environment.apiBaseUrl}/clients/${this.client.clientId()}/audit/${entryId}`);
  }
  authorOf(records: AuditRecordResponse[]): string | null {
    return records.find(r => r.action === 'Created')?.actor.userId ?? null;
  }
}
```

- [ ] **Step 7: problem-details helper (TDD)**

`problem-details.spec.ts`:
```ts
import { extractProblem } from './problem-details';
import { HttpErrorResponse } from '@angular/common/http';

describe('extractProblem', () => {
  it('reads ProblemDetails.detail', () => {
    const err = new HttpErrorResponse({ status: 409, error: { detail: 'Period is closed' } });
    expect(extractProblem(err).detail).toBe('Period is closed');
  });
  it('flattens ValidationProblemDetails.errors', () => {
    const err = new HttpErrorResponse({ status: 422, error: { errors: { 'lines[0].amount': ['must be > 0'] } } });
    const p = extractProblem(err);
    expect(p.fieldErrors['lines[0].amount']).toEqual(['must be > 0']);
    expect(p.detail).toContain('must be > 0');
  });
  it('falls back to a generic message', () => {
    expect(extractProblem(new HttpErrorResponse({ status: 500 })).detail).toBeTruthy();
  });
});
```
`problem-details.ts`:
```ts
import { HttpErrorResponse } from '@angular/common/http';

export interface Problem { detail: string; fieldErrors: Record<string, string[]>; }

export function extractProblem(err: unknown): Problem {
  const body = err instanceof HttpErrorResponse ? err.error : (err as { error?: unknown })?.error ?? err;
  const fieldErrors = (body && typeof body === 'object' && 'errors' in body
    ? (body as { errors: Record<string, string[]> }).errors : {}) ?? {};
  const flat = Object.values(fieldErrors).flat();
  const detail = (body && typeof body === 'object' && 'detail' in body && (body as { detail?: string }).detail)
    || (flat.length ? flat.join('; ') : null)
    || (err instanceof HttpErrorResponse ? `Request failed (${err.status})` : 'Request failed');
  return { detail, fieldErrors };
}
```

- [ ] **Step 8: Run full tests — verify pass** — `ng test --watch=false` → PASS.

- [ ] **Step 9: Build + commit**

```bash
git add src/app/core/entries src/app/core/audit src/app/core/api/problem-details.ts src/app/core/api/problem-details.spec.ts
git commit -m "feat(ui): write-path entry methods + audit service + problem-details

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: D1 — Post-entry form (Signal Forms Dr/Cr grid)

**Files:**
- Create: `src/app/features/journal/entry-form.ts`, `entry-form.spec.ts`
- Modify: `src/app/app.routes.ts` (journal children), `src/app/features/journal/entry-list.ts` (add "New entry" link)

**Interfaces:**
- Consumes: `form, schema, applyEach, validate, validateTree, required, FormField` (`@angular/forms/signals`); `EntriesService.post/validate` + `PostEntryRequest/PostLineRequest` (Task 3); `extractProblem` (Task 3); `AccountsService` (1b); `formatMoney`/`DEFAULT_FORMAT_PROFILE` (1a); `Router`; `HlmInputImports`/`HlmLabelImports`/`HlmButton`/`HlmSelectImports`.
- Produces: route `journal/new` → `EntryForm`.

- [ ] **Step 1: Write the failing test** — `entry-form.spec.ts`

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EntryForm } from './entry-form';
import { AccountsService } from '../../core/accounts/accounts.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { AccountResponse } from '../../core/accounts/account';

function seedAccounts(): AccountResponse[] {
  return [
    { id: 'A', number: '1000', name: 'Cash', type: 'Asset', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false },
    { id: 'B', number: '4000', name: 'Revenue', type: 'Revenue', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Credit', isTemporary: false },
  ];
}

describe('EntryForm', () => {
  let ctrl: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const accounts = TestBed.inject(AccountsService);
    (accounts as unknown as { _accounts: { set(v: AccountResponse[]): void } })._accounts.set(seedAccounts());
  });
  afterEach(() => ctrl.verify());

  it('disables Post until the entry is balanced with ≥2 lines', () => {
    const f = TestBed.createComponent(EntryForm); f.detectChanges();
    const cmp = f.componentInstance;
    // two empty lines, no amounts → invalid
    expect(cmp.canPost()).toBe(false);
    cmp.setAccount(0, 'A'); cmp.entryForm.lines[0].debit().value.set(100);
    cmp.setAccount(1, 'B'); cmp.entryForm.lines[1].credit().value.set(100);
    f.detectChanges();
    expect(cmp.canPost()).toBe(true);
  });

  it('flags unbalanced entries', () => {
    const f = TestBed.createComponent(EntryForm); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.setAccount(0, 'A'); cmp.entryForm.lines[0].debit().value.set(100);
    cmp.setAccount(1, 'B'); cmp.entryForm.lines[1].credit().value.set(90);
    f.detectChanges();
    expect(cmp.canPost()).toBe(false);
    expect(cmp.balanceError()).toContain('equal');
  });

  it('maps the two-column model to PostLineRequest and POSTs on submit', () => {
    const f = TestBed.createComponent(EntryForm); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.entryForm.effectiveDate().value.set('2026-06-29');
    cmp.setAccount(0, 'A'); cmp.entryForm.lines[0].debit().value.set(100);
    cmp.setAccount(1, 'B'); cmp.entryForm.lines[1].credit().value.set(100);
    f.detectChanges();
    cmp.post();
    const http = ctrl.expectOne('http://localhost:5000/clients/C1/entries');
    expect(http.request.body.lines).toEqual([
      { accountId: 'A', direction: 'Debit', amount: 100 },
      { accountId: 'B', direction: 'Credit', amount: 100 },
    ]);
    const nav = spyOn(TestBed.inject(Router), 'navigate');
    http.flush({ id: 'E1', status: 'Active', posting: 'PendingApproval' });
    expect(nav).toHaveBeenCalledWith(['/journal', 'E1']);
  });

  it('surfaces a server 422 from the Validate button', () => {
    const f = TestBed.createComponent(EntryForm); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.setAccount(0, 'A'); cmp.entryForm.lines[0].debit().value.set(100);
    cmp.setAccount(1, 'B'); cmp.entryForm.lines[1].credit().value.set(100);
    f.detectChanges();
    cmp.validate();
    const http = ctrl.expectOne('http://localhost:5000/clients/C1/entries/validate');
    http.flush({ detail: 'Account 1000 is not postable' }, { status: 422, statusText: 'Unprocessable' });
    f.detectChanges();
    expect(cmp.serverMessage()).toContain('not postable');
  });
});
```
> Note: the test reaches into `AccountsService._accounts` to seed without HTTP. If 1a named the private signal differently, adapt; alternatively call `accounts.load()` and flush the `/accounts` GET. The `navigate` spy is installed before `flush` so the post-success navigation is captured.

- [ ] **Step 2: Run it — verify it fails** — `ng test --watch=false -- entry-form.spec.ts` → FAIL (`EntryForm` undefined).

- [ ] **Step 3: Implement `entry-form.ts`**

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { form, schema, applyEach, validate, validateTree, required, FormField } from '@angular/forms/signals';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { EntriesService } from '../../core/entries/entries.service';
import { PostEntryRequest, PostLineRequest } from '../../core/entries/entry';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { formatMoney } from '../../core/format/money-formatter';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';

interface LineModel { accountId: string; debit: number | null; credit: number | null; }
interface EntryFormValue { effectiveDate: string; reference: string; memo: string; type: 'Standard' | 'Adjusting'; lines: LineModel[]; }

const emptyLine = (): LineModel => ({ accountId: '', debit: null, credit: null });

@Component({
  selector: 'app-entry-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormField, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-4xl">
      <h1 class="text-2xl font-bold">Post Journal Entry</h1>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Effective date</label>
          <input hlmInput type="date" [formField]="entryForm.effectiveDate" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Type</label>
          <div hlmSelect [value]="entryForm.type().value()" (valueChange)="entryForm.type().value.set($any($event))">
            <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
            <hlm-select-content>
              <hlm-select-item value="Standard">Standard</hlm-select-item>
              <hlm-select-item value="Adjusting">Adjusting</hlm-select-item>
            </hlm-select-content>
          </div>
        </div>
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Reference</label>
        <input hlmInput type="text" [formField]="entryForm.reference" />
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Memo</label>
        <input hlmInput type="text" [formField]="entryForm.memo" />
      </div>

      <table class="w-full text-sm">
        <thead>
          <tr class="text-left text-muted-foreground">
            <th class="py-1">Account</th><th class="text-right">Debit</th><th class="text-right">Credit</th><th></th>
          </tr>
        </thead>
        <tbody>
          @for (line of model().lines; track $index) {
            <tr>
              <td class="py-1 pr-2">
                <div hlmSelect [value]="line.accountId" (valueChange)="setAccount($index, $any($event))" class="w-full">
                  <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select account" /></hlm-select-trigger>
                  <hlm-select-content>
                    @for (a of postableAccounts(); track a.id) {
                      <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item>
                    }
                  </hlm-select-content>
                </div>
              </td>
              <td class="pr-2"><input hlmInput type="number" class="text-right tabular-nums" [formField]="entryForm.lines[$index].debit" /></td>
              <td class="pr-2"><input hlmInput type="number" class="text-right tabular-nums" [formField]="entryForm.lines[$index].credit" /></td>
              <td><button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine($index)" [disabled]="model().lines.length <= 2">✕</button></td>
            </tr>
          }
        </tbody>
        <tfoot>
          <tr class="font-semibold border-t border-border">
            <td class="py-1 text-right pr-2">Totals</td>
            <td class="text-right tabular-nums">{{ money(totalDebit()) }}</td>
            <td class="text-right tabular-nums">{{ money(totalCredit()) }}</td>
            <td></td>
          </tr>
        </tfoot>
      </table>

      <div class="flex items-center gap-3">
        <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>
        @if (balanceError()) { <span class="text-destructive text-sm">{{ balanceError() }}</span> }
      </div>

      @if (serverMessage()) {
        <p [class]="serverOk() ? 'text-sm text-[color:var(--brand-teal)]' : 'text-destructive text-sm'">{{ serverMessage() }}</p>
      }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" variant="outline" (click)="validate()" [disabled]="busy()">Validate</button>
        <button hlmBtn type="button" (click)="post()" [disabled]="!canPost() || busy()">Post</button>
      </div>
    </div>
  `,
})
export class EntryForm {
  private readonly entries = inject(EntriesService);
  private readonly accounts = inject(AccountsService);
  private readonly router = inject(Router);

  readonly model = signal<EntryFormValue>({
    effectiveDate: new Date().toISOString().slice(0, 10),
    reference: '', memo: '', type: 'Standard', lines: [emptyLine(), emptyLine()],
  });

  readonly entryForm = form(this.model, (p) => {
    required(p.effectiveDate);
    applyEach(p.lines, (line) => {
      required(line.accountId);
      validate(line, ({ value }) => {
        const l = value(); const d = (l.debit ?? 0) > 0; const c = (l.credit ?? 0) > 0;
        return d === c ? { kind: 'one-side', message: 'Enter a debit OR a credit' } : undefined;
      });
    });
    validateTree(p.lines, ({ value }) => {
      const lines = value();
      const filled = lines.filter(l => (l.debit ?? 0) > 0 || (l.credit ?? 0) > 0).length;
      const totD = lines.reduce((s, l) => s + (l.debit ?? 0), 0);
      const totC = lines.reduce((s, l) => s + (l.credit ?? 0), 0);
      const errs: { kind: string; message: string }[] = [];
      if (filled < 2) errs.push({ kind: 'min-lines', message: 'At least two lines are required' });
      if (Math.round((totD - totC) * 100) !== 0) errs.push({ kind: 'unbalanced', message: 'Debits must equal credits' });
      return errs.length ? errs : undefined;
    });
  });

  readonly busy = signal(false);
  readonly serverMessage = signal<string | null>(null);
  readonly serverOk = signal(false);

  readonly postableAccounts = computed(() => this.accounts.accounts().filter(a => a.postable));
  readonly totalDebit = computed(() => this.model().lines.reduce((s, l) => s + (l.debit ?? 0), 0));
  readonly totalCredit = computed(() => this.model().lines.reduce((s, l) => s + (l.credit ?? 0), 0));
  readonly canPost = computed(() => this.entryForm().valid());
  readonly balanceError = computed(() => this.entryForm.lines().errors().map(e => e.message).filter(Boolean).join('; ') || null);

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  setAccount(i: number, id: string): void { this.entryForm.lines[i].accountId().value.set(id); }
  addLine(): void { this.model.update(v => ({ ...v, lines: [...v.lines, emptyLine()] })); }
  removeLine(i: number): void { this.model.update(v => ({ ...v, lines: v.lines.filter((_, idx) => idx !== i) })); }
  money(n: number): string { return formatMoney(n, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: false }); }

  private toRequest(): PostEntryRequest {
    const v = this.model();
    const lines: PostLineRequest[] = v.lines
      .filter(l => (l.debit ?? 0) > 0 || (l.credit ?? 0) > 0)
      .map(l => ({ accountId: l.accountId, direction: (l.debit ?? 0) > 0 ? 'Debit' : 'Credit', amount: (l.debit ?? 0) > 0 ? l.debit! : l.credit! }));
    return { effectiveDate: v.effectiveDate, reference: v.reference || null, memo: v.memo || null, type: v.type, lines };
  }

  validate(): void {
    this.busy.set(true); this.serverMessage.set(null);
    this.entries.validate(this.toRequest()).subscribe({
      next: () => { this.serverOk.set(true); this.serverMessage.set('Entry is valid and would post.'); this.busy.set(false); },
      error: (e) => { this.serverOk.set(false); this.serverMessage.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  post(): void {
    if (!this.canPost()) return;
    this.busy.set(true); this.serverMessage.set(null);
    this.entries.post(this.toRequest()).subscribe({
      next: (r) => { this.router.navigate(['/journal', r.id]); },
      error: (e) => { this.serverOk.set(false); this.serverMessage.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
```
> If `entryForm.lines[i].debit().value.set(...)` typing complains in the spec, the field tree exposes the item field's value signal; the `setAccount`/value-set pattern is the supported path. Native number inputs bound with `[formField]` parse to `number | null`.

- [ ] **Step 4: Wire the route + the entry-list link**

`app.routes.ts` — replace the flat `journal` route with children (import `EntryForm`):
```ts
{
  path: 'journal',
  children: [
    { path: '', pathMatch: 'full', component: EntryList },
    { path: 'new', component: EntryForm },
    // approvals + :id added in Tasks 5–6
  ],
},
```
Keep the `NAV.filter` placeholder mapping but exclude anything under `/journal` (change the predicate to `!n.path.startsWith('/journal')` alongside the existing excludes) so a future "Approvals" nav item doesn't collide.
`entry-list.ts` — add a "New entry" button linking to `/journal/new` next to the `<h1>` (import `RouterLink` + `HlmButton`):
```html
<a hlmBtn size="sm" routerLink="/journal/new" class="ms-auto">New entry</a>
```
(Move the existing posting `select` so both fit; the select keeps `ms-auto` removed or wrapped in a flex row.)

- [ ] **Step 5: Run tests — verify pass** — `ng test --watch=false` → PASS (entry-form + entry-list specs green).

- [ ] **Step 6: Build + commit**

```bash
git add src/app/features/journal/entry-form.ts src/app/features/journal/entry-form.spec.ts src/app/app.routes.ts src/app/features/journal/entry-list.ts
git commit -m "feat(ui): D1 post-entry form (Signal Forms Dr/Cr grid + validate/post)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: D3 — Pending-approval queue (with SoD cue)

**Files:**
- Create: `src/app/features/journal/approval-queue.ts`, `approval-queue.spec.ts`
- Modify: `src/app/app.routes.ts` (add `journal/approvals`), `src/app/layout/nav.ts` (add Approvals)

**Interfaces:**
- Consumes: `EntriesService.listPaged` (1b); `AuditService.entryAudit/authorOf` + `DevIdentityService.active` (Tasks 3/1); formatter; `HlmTableImports`/`HlmBadgeImports`/`RouterLink`.
- Produces: route `journal/approvals` → `ApprovalQueue`.

- [ ] **Step 1: Write the failing test** — `approval-queue.spec.ts`

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApprovalQueue } from './approval-queue';
import { ClientContextService } from '../../core/client/client-context.service';
import { DevIdentityService } from '../../core/api/dev-identity.service';
import { environment } from '../../core/api/environment';

describe('ApprovalQueue', () => {
  let ctrl: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  });
  afterEach(() => ctrl.verify());

  it('lists pending entries and marks the active identity\\'s own entry as not approvable', () => {
    const f = TestBed.createComponent(ApprovalQueue); f.detectChanges();
    const page = ctrl.expectOne(r => r.url.includes('/clients/C1/entries') && r.params.get('posting') === 'PendingApproval');
    page.flush({ items: [
      { id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: 'mine', supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
      { id: 'E2', sequenceNumber: 2, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: 'theirs', supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
    ], total: 2, skip: 0, limit: 50 });
    f.detectChanges();
    // audit fetched per row to resolve the author
    const a1 = ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1');
    a1.flush([{ sequence: 1, action: 'Created', entryId: 'E1', entryVersion: 1, at: '', reason: null, actor: { userId: environment.devClerk.sub, name: 'Clerk', claims: [] } }]);
    const a2 = ctrl.expectOne('http://localhost:5000/clients/C1/audit/E2');
    a2.flush([{ sequence: 1, action: 'Created', entryId: 'E2', entryVersion: 1, at: '', reason: null, actor: { userId: environment.devApprover.sub, name: 'Other', claims: [] } }]);
    f.detectChanges();
    // active identity defaults to the clerk → E1 is theirs (not approvable), E2 is approvable
    expect(f.componentInstance.approvableById()['E1']).toBe(false);
    expect(f.componentInstance.approvableById()['E2']).toBe(true);
  });
});
```

- [ ] **Step 2: Run it — verify it fails** — `ng test --watch=false -- approval-queue.spec.ts` → FAIL.

- [ ] **Step 3: Implement `approval-queue.ts`**

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { EntriesService } from '../../core/entries/entries.service';
import { EntryResponse } from '../../core/entries/entry';
import { AuditService } from '../../core/audit/audit.service';
import { DevIdentityService } from '../../core/api/dev-identity.service';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';
import { formatProfileDate } from '../../core/format/date-formatter';

@Component({
  selector: 'app-approval-queue',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmTableImports, ...HlmBadgeImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <h1 class="text-2xl font-bold">Pending Approval</h1>
      @if (loading()) { <p class="text-muted-foreground text-sm">Loading…</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (!loading() && !error()) {
        @if (entries().length === 0) { <p class="text-muted-foreground text-sm">Nothing awaiting approval.</p> }
        @else {
          <div hlmTableContainer><table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>#</th><th hlmTh>Date</th><th hlmTh>Memo</th><th hlmTh>Lines</th><th hlmTh>Approvable</th></tr></thead>
            <tbody hlmTBody>
              @for (e of entries(); track e.id) {
                <tr hlmTr>
                  <td hlmTd><a class="underline" [routerLink]="['/journal', e.id]">{{ e.sequenceNumber }}</a></td>
                  <td hlmTd>{{ formatDate(e.effectiveDate) }}</td>
                  <td hlmTd>{{ e.memo ?? '—' }}</td>
                  <td hlmTd>{{ e.lineCount }}</td>
                  <td hlmTd>
                    @if (approvableById()[e.id]) { <span hlmBadge variant="secondary">Approvable</span> }
                    @else { <span hlmBadge class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]">Your entry — needs another approver</span> }
                  </td>
                </tr>
              }
            </tbody>
          </table></div>
        }
      }
    </div>
  `,
})
export class ApprovalQueue {
  private readonly entriesSvc = inject(EntriesService);
  private readonly audit = inject(AuditService);
  private readonly identity = inject(DevIdentityService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly entries = signal<EntryResponse[]>([]);
  private readonly authorById = signal<Record<string, string | null>>({});

  readonly approvableById = computed(() => {
    const me = this.identity.active().sub; const authors = this.authorById();
    return Object.fromEntries(this.entries().map(e => [e.id, authors[e.id] != null && authors[e.id] !== me]));
  });

  constructor() {
    this.entriesSvc.listPaged({ posting: 'PendingApproval', skip: 0, limit: 50 }).subscribe({
      next: (page) => {
        this.entries.set(page.items); this.loading.set(false);
        if (page.items.length === 0) return;
        forkJoin(Object.fromEntries(page.items.map(e => [e.id, this.audit.entryAudit(e.id)]))).subscribe({
          next: (map) => this.authorById.set(Object.fromEntries(Object.entries(map).map(([id, recs]) => [id, this.audit.authorOf(recs)]))),
          error: () => { /* cue only; leave authors empty → rows show not-approvable, safe default */ },
        });
      },
      error: (e) => { this.error.set((e as { message?: string })?.message ?? 'Error loading queue'); this.loading.set(false); },
    });
    void of(null); // keep rxjs of import if unused elsewhere; remove if lint complains
  }

  formatDate(d: string): string { return formatProfileDate(d, DEFAULT_FORMAT_PROFILE); }
}
```
> Remove the `of`/`void of(null)` line if the linter flags the unused import; it's only there to avoid an accidental missing-import during editing.

- [ ] **Step 4: Route + nav**

`app.routes.ts` — add under the `journal` children: `{ path: 'approvals', component: ApprovalQueue },` (import `ApprovalQueue`).
`nav.ts` — add `{ label: 'Approvals', path: '/journal/approvals' }` after the Journal entry. (The Task-4 predicate change `!n.path.startsWith('/journal')` already keeps it from the placeholder map.)

- [ ] **Step 5: Run tests — verify pass** — `ng test --watch=false` → PASS.

- [ ] **Step 6: Build + commit**

```bash
git add src/app/features/journal/approval-queue.ts src/app/features/journal/approval-queue.spec.ts src/app/app.routes.ts src/app/layout/nav.ts
git commit -m "feat(ui): D3 pending-approval queue with SoD cue

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: D4 — Entry detail (lines + audit stamps + approve/void)

**Files:**
- Create: `src/app/features/journal/entry-detail.ts`, `entry-detail.spec.ts`
- Modify: `src/app/app.routes.ts` (add `journal/:id`)

**Interfaces:**
- Consumes: `EntriesService.get/approve/void` (Task 3); `AuditService.entryAudit/authorOf` (Task 3); `AccountsService.label` (1b); `DevIdentityService.active` (Task 1); `extractProblem` (Task 3); formatter; `HlmTableImports`/`HlmBadgeImports`/`HlmButton`/`HlmInputImports`; `ActivatedRoute`.
- Produces: route `journal/:id` → `EntryDetail`.

- [ ] **Step 1: Write the failing test** — `entry-detail.spec.ts`

```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { EntryDetail } from './entry-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function provideRouteId(id: string) {
  return { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } }, paramMap: of({ get: () => id }) } };
}

describe('EntryDetail', () => {
  let ctrl: HttpTestingController;
  function setup(id = 'E1') {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideRouteId(id)],
    });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  }
  function flushEntryAndAudit(authorSub: string) {
    const e = ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1');
    e.flush({ id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2,
      lines: [{ accountId: 'A', direction: 'Debit', amount: 100, dimensions: {}, lineMemo: null }, { accountId: 'B', direction: 'Credit', amount: 100, dimensions: {}, lineMemo: null }],
      supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null, memo: 'm', reference: 'r', sourceRef: null, sourceType: null });
    const a = ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1');
    a.flush([{ sequence: 1, action: 'Created', entryId: 'E1', entryVersion: 1, at: '2026-06-29T00:00:00Z', reason: null, actor: { userId: authorSub, name: 'Author', claims: [] } }]);
  }

  afterEach(() => ctrl.verify());

  it('renders lines and the creator stamp', () => {
    setup(); const f = TestBed.createComponent(EntryDetail); f.detectChanges();
    flushEntryAndAudit(environment.devApprover.sub); f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Author');
    expect(f.componentInstance.entry()?.lines.length).toBe(2);
  });

  it('approve() surfaces a 403 SoD inline', () => {
    setup(); const f = TestBed.createComponent(EntryDetail); f.detectChanges();
    flushEntryAndAudit(environment.devClerk.sub); f.detectChanges(); // active = clerk = author
    f.componentInstance.approve();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1/approve');
    req.flush({ detail: 'Segregation of duties: must be approved by someone else.' }, { status: 403, statusText: 'Forbidden' });
    f.detectChanges();
    expect(f.componentInstance.message()).toContain('Segregation of duties');
  });

  it('void() posts the reason and re-fetches', () => {
    setup(); const f = TestBed.createComponent(EntryDetail); f.detectChanges();
    flushEntryAndAudit(environment.devApprover.sub); f.detectChanges();
    f.componentInstance.voidReason.set('mistake');
    f.componentInstance.voidEntry();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1/void');
    expect(req.request.body).toEqual({ reason: 'mistake' });
    req.flush({ id: 'E1', posting: 'Posted', status: 'Voided', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', lineCount: 2, lines: [], supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null });
    // re-fetch
    ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1').flush({ id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Voided', posting: 'Posted', lineCount: 0, lines: [], supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null });
    ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1').flush([]);
  });
});
```

- [ ] **Step 2: Run it — verify it fails** — `ng test --watch=false -- entry-detail.spec.ts` → FAIL.

- [ ] **Step 3: Implement `entry-detail.ts`**

```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { EntriesService } from '../../core/entries/entries.service';
import { EntryResponse } from '../../core/entries/entry';
import { AuditService } from '../../core/audit/audit.service';
import { AuditRecordResponse } from '../../core/audit/audit';
import { AccountsService } from '../../core/accounts/accounts.service';
import { DevIdentityService } from '../../core/api/dev-identity.service';
import { extractProblem } from '../../core/api/problem-details';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';
import { formatMoney } from '../../core/format/money-formatter';
import { formatProfileDate } from '../../core/format/date-formatter';

@Component({
  selector: 'app-entry-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmTableImports, ...HlmBadgeImports, HlmButton, ...HlmInputImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      @if (entry(); as e) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Entry #{{ e.sequenceNumber }}</h1>
          @if (e.posting === 'PendingApproval') { <span hlmBadge class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]">Pending</span> }
          @else { <span hlmBadge variant="secondary">{{ e.posting }}</span> }
        </div>
        <div class="text-sm text-muted-foreground">
          {{ formatDate(e.effectiveDate) }} · {{ e.type }} · {{ e.reference ?? '—' }} · {{ e.memo ?? '' }}
        </div>

        <div hlmTableContainer><table hlmTable>
          <thead hlmTHead><tr hlmTr><th hlmTh>Account</th><th hlmTh class="text-right">Debit</th><th hlmTh class="text-right">Credit</th></tr></thead>
          <tbody hlmTBody>
            @for (l of e.lines; track $index) {
              <tr hlmTr>
                <td hlmTd>{{ accountLabel(l.accountId) }}</td>
                <td hlmTd class="text-right tabular-nums">{{ l.direction === 'Debit' ? money(l.amount) : '' }}</td>
                <td hlmTd class="text-right tabular-nums">{{ l.direction === 'Credit' ? money(l.amount) : '' }}</td>
              </tr>
            }
          </tbody>
          <tfoot><tr hlmTr class="font-semibold border-t-2 border-border">
            <td hlmTd class="text-right">Totals</td>
            <td hlmTd class="text-right tabular-nums">{{ money(totalDebit()) }}</td>
            <td hlmTd class="text-right tabular-nums">{{ money(totalCredit()) }}</td>
          </tr></tfoot>
        </table></div>

        <div class="text-sm">
          <h2 class="font-semibold">Audit</h2>
          @for (r of audit(); track r.sequence) {
            <div class="text-muted-foreground">{{ r.action }} by {{ r.actor.name ?? r.actor.userId }} at {{ r.at }}</div>
          }
        </div>

        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

        @if (e.posting === 'PendingApproval') {
          <div class="flex items-center gap-2">
            <button hlmBtn type="button" (click)="approve()" [disabled]="busy()">Approve</button>
            <input hlmInput type="text" placeholder="Void reason" [value]="voidReason()" (input)="voidReason.set($any($event.target).value)" />
            <button hlmBtn type="button" variant="outline" (click)="voidEntry()" [disabled]="busy()">Void</button>
          </div>
        }
      } @else if (loadError()) { <p class="text-destructive text-sm">{{ loadError() }}</p> }
      @else { <p class="text-muted-foreground text-sm">Loading…</p> }
    </div>
  `,
})
export class EntryDetail {
  private readonly entries = inject(EntriesService);
  private readonly auditSvc = inject(AuditService);
  private readonly accounts = inject(AccountsService);
  private readonly route = inject(ActivatedRoute);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly entry = signal<EntryResponse | null>(null);
  readonly audit = signal<AuditRecordResponse[]>([]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly loadError = signal<string | null>(null);

  readonly totalDebit = computed(() => (this.entry()?.lines ?? []).filter(l => l.direction === 'Debit').reduce((s, l) => s + l.amount, 0));
  readonly totalCredit = computed(() => (this.entry()?.lines ?? []).filter(l => l.direction === 'Credit').reduce((s, l) => s + l.amount, 0));

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); this.load(); }

  private load(): void {
    this.entries.get(this.id).subscribe({ next: (e) => this.entry.set(e), error: (e) => this.loadError.set(extractProblem(e).detail) });
    this.auditSvc.entryAudit(this.id).subscribe({ next: (a) => this.audit.set(a) });
  }

  accountLabel(id: string): string { return this.accounts.label(id); }
  money(n: number): string { return formatMoney(n, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: false }); }
  formatDate(d: string): string { return formatProfileDate(d, DEFAULT_FORMAT_PROFILE); }

  readonly voidReason = signal('');

  approve(): void {
    this.busy.set(true); this.message.set(null);
    this.entries.approve(this.id).subscribe({
      next: (e) => { this.entry.set(e); this.busy.set(false); this.load(); },
      error: (err) => { this.message.set(extractProblem(err).detail); this.busy.set(false); },
    });
  }

  voidEntry(): void {
    this.busy.set(true); this.message.set(null);
    this.entries.void(this.id, this.voidReason() || undefined).subscribe({
      next: () => { this.busy.set(false); this.load(); },
      error: (err) => { this.message.set(extractProblem(err).detail); this.busy.set(false); },
    });
  }
}
```
> The void test expects `{reason:'mistake'}`; `EntriesService.void` sends `{reason: reason ?? null}`, so passing `voidReason()` (a non-empty string) yields `{reason:'mistake'}`. Matches.

- [ ] **Step 4: Add the route**

`app.routes.ts` — add under `journal` children **after** `new`/`approvals` (so it doesn't shadow them): `{ path: ':id', component: EntryDetail },` (import `EntryDetail`).

- [ ] **Step 5: Run tests — verify pass** — `ng test --watch=false` → PASS.

- [ ] **Step 6: Build + whole-app test + commit**

Run: `npm run build` (expected: success) and `ng test --watch=false` (expected: all green).
```bash
git add src/app/features/journal/entry-detail.ts src/app/features/journal/entry-detail.spec.ts src/app/app.routes.ts
git commit -m "feat(ui): D4 entry detail with audit stamps + approve/void

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**1. Spec coverage (vs the 1c design):**
- DevIdentityService + switchable DevToken (maker-checker enabler) → Task 1. ✓
- Top-bar "Acting as" switcher → Task 2. ✓
- Write-path service methods (post/validate/approve/void/get) + audit read + ProblemDetails surfacing → Task 3. ✓
- D1 post-entry form: Signal Forms `form`/`schema`/`applyEach`/`validateTree`, two-column Dr/Cr grid → `PostLineRequest`, live footer, plain hlm-select account picker, client-gates-Post + server Validate button surfacing 409/422, navigate-on-201 → Task 4. ✓
- D3 pending-approval queue + SoD cue (author from `/audit`, compared to active identity) → Task 5. ✓
- D4 entry detail: lines footed, audit stamps, approve (403 SoD inline) + void (reason, re-fetch) → Task 6. ✓
- Dimension boundary (manual journal sends no `dimensions`; server 422 surfaces) → enforced by `toRequest()` omitting dimensions + the Validate/Post error surfacing. ✓
- Money/dates via the formatter; parens negatives; tabular alignment → every render uses `formatMoney`/`formatProfileDate`. ✓
- Revise/reverse deferred; searchable combobox deferred; Format Profile fetch deferred → out of scope, not built. ✓

**2. Placeholder scan:** No TBD/TODO. Every code/test step contains complete content. The two "if your 1a private signal is named differently / if lint flags the unused import" notes are adaptation guidance with a concrete default, not placeholders. `devClientId` stays intentionally empty (documented; tests mock HTTP).

**3. Type consistency:** `DevIdentity{sub,name,claims}` (Task 1) consumed by interceptor/shell/queue/detail. `PostEntryRequest`/`PostLineRequest`/`PostEntryResponse`/`EntryValidationResponse` (Task 3) consumed by Task 4. `EntriesService.post/validate/approve/void/get` (Task 3) consumed by Tasks 4/6. `AuditService.entryAudit/authorOf` + `AuditRecordResponse{action,actor.userId}` (Task 3) consumed by Tasks 5/6. `extractProblem` (Task 3) consumed by Tasks 4/6. `EntryResponse`/`EntryLineResponse{direction,amount}` (1b) reused for detail/queue. Signal Forms symbols (`form`/`schema`/`applyEach`/`validate`/`validateTree`/`required`/`FormField`) verified present in `@angular/forms/signals` 22.0.4. Route shape: `['/journal', id]` (Task 4 navigate) matches `journal/:id` (Task 6). ✓

## Open (resolve during execution / before demo)
- If a spec referencing the removed `environment.devUserId` breaks, repoint it to `environment.devClerk.sub` (Task 1 Step 8 greps for it).
- hlm-select ↔ Signal Forms: selects use manual `value`/`valueChange` binding (not `[formField]`); if a cleaner CVA path is confirmed during Task 4, it may be simplified — not required.
- Seed the demo client + two memberships (clerk sub, approver sub) and set `environment.devClerk`/`devApprover`/`devClientId` to matching pairs before the live demo — not a build/test dependency.
