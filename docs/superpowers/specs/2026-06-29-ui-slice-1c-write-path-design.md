# UI Slice 1c — Journal Write Path (Post · Approve · Detail) — Design

**Date:** 2026-06-29
**Status:** Approved (design); precedes the implementation plan
**Builds on:** Slice 1a (foundation) + Slice 1b (read path), both merged to master.

## Purpose

Ship the **write path** of the Journal critical path: post a balanced journal entry,
approve it through maker-checker, and inspect an entry's detail. This is the first
slice that **mutates** ledger state and the first to use Angular's **experimental
Signal Forms** (`@angular/forms/signals`, present in the installed Angular 22.0.4)
for the Dr/Cr entry grid.

Covers screen-map areas **D1** (post entry), **D3** (pending-approval queue), and
**D4** (entry detail). Revise/reverse (also D4) are deferred to a later slice.

## Stack & conventions

All Slice-1a/1b conventions hold verbatim:
- **Zoneless + OnPush** on every component; standalone (with `standalone: true` omitted);
  signals + `input()`/`output()`/signal queries; `@if`/`@for`; `inject()` DI;
  functional interceptors; behaviors as attribute directives where applicable.
- **Money/dates render ONLY through the formatter** (`core/format/`):
  `formatMoney(amount,'USD',DEFAULT_FORMAT_PROFILE,{symbol})`, `formatProfileDate(...)`.
  Decimal-aligned, tabular numerals, accounting parens for negatives. Never hand-format.
- **API returns raw `decimal` + ISO dates** (camelCase JSON). Services return typed DTOs;
  components format. No client-side recomputation of server aggregates (the live footer
  totals are *input echoes*, not server figures — see D1).
- **DevToken scheme** (not Bearer): `Authorization: DevToken <base64url(utf8(json({sub,name,claims})))>`.
- Env: `nvm use 24.18.0`. Test: `ng test --watch=false` (Vitest). Build: `npm run build`.
  Work from `UI/Angular`.
- Commit trailer verbatim: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Exact engine contracts (from backend recon — `LedgerEndpoints.cs`, `PostEntryRequest.cs`)

- `POST /clients/{clientId}/entries` — body `PostEntryRequest`; on success **201**
  `PostEntryResponse { id, status, posting }` (posting will be `"PendingApproval"`).
  Idempotency-by-id exists but is not used by 1c (no client-supplied id).
- `POST /clients/{clientId}/entries/validate` — same body; **200** `EntryValidationResponse { valid: true }`
  when it would post, else the **same 409/422 ProblemDetails** a real post returns
  (byte-for-byte). Side-effect-free dry run.
- `POST /clients/{clientId}/entries/{entryId}/approve` — **200** `EntryResponse`. Enforces
  **SoD**: if the client requires SoD and `entry.createdBy == actor`, returns **403** with a
  segregation-of-duties message. Closed-period/lifecycle issues → **409**.
- `POST /clients/{clientId}/entries/{entryId}/void` — body `VoidRequest { reason?: string }`;
  **200** `EntryResponse`. Lifecycle issues → **409**; unknown id → **404**.
- `GET /clients/{clientId}/entries/{entryId}` — `EntryResponse` (full lines + stamps).
- `GET /clients/{clientId}/audit/{entryId}` — entry audit (creator/approver/timestamps chain).
- `GET /clients/{clientId}/entries?posting=PendingApproval&skip=&limit=` — paged
  `PagedResponse<EntryResponse>` (reuses Slice-1b `EntriesService.listPaged`).

**Request DTOs:**
```
PostEntryRequest {
  id?: string|null;           // unused in 1c
  effectiveDate: string;      // ISO date (DateOnly)
  reference?: string|null;
  memo?: string|null;
  lines: PostLineRequest[];
  sourceRef?: string|null;    // unused (module-only)
  sourceType?: string|null;   // unused (module-only)
  type?: string|null;         // "Standard" (default) | "Adjusting"
}
PostLineRequest {
  accountId: string;
  direction: "Debit"|"Credit";
  amount: number;             // decimal, positive
  dimensions?: Record<string,string>|null;  // out of 1c scope (see boundary)
}
```

## Architecture

### Component 1 — `DevIdentityService` + identity switcher

**What it does:** Holds the **active dev identity** as a signal so maker-checker can be
exercised with a single browser. **Why it's needed:** posting lands `PendingApproval`,
and `/approve` returns **403** when the approver equals the author. One fixed DevToken
identity can never approve its own entries.

- `environment` gains two identities: `devClerk` and `devApprover`
  (`{ sub, name, claims }` each; the clerk has a Clerk/Controller role able to post, the
  approver has an Approve-capable role). Both must map to control-DB memberships for the
  demo client to get real data (documented; not a build/test dependency).
- `DevIdentityService` (root): `readonly active = signal<DevIdentity>(devClerk)`; `use(id)`.
- `authInterceptor` reads `inject(DevIdentityService).active()` and mints the token from it
  per request (replaces the direct `environment` read). Switching identity changes the
  token on the next request — no reload.
- **Switcher UI:** an "Acting as ▾" hlm `select` in the **top-bar shell** (dev-only,
  visible everywhere). Changing it calls `DevIdentityService.use(...)`. The queue/detail
  re-evaluate approvability reactively.

**Interface:** `DevIdentityService.active()` (signal), `.use(id)`. Consumed by the
interceptor (token) and the queue/detail (SoD cue + approve gating).

### Component 2 — D1 Post-entry form (`features/journal/entry-form.ts`)

The Signal Forms showcase. **What it does:** lets a clerk compose a balanced entry,
validate it client-side and via the server dry run, then post it.

**Form model** (the accountant-facing shape; mapped to the wire DTO on submit):
```
EntryFormValue {
  effectiveDate: string;       // ISO; default today
  reference: string;
  memo: string;
  type: 'Standard' | 'Adjusting';
  lines: LineModel[];          // starts with 2 empty rows
}
LineModel { accountId: string; debit: number | null; credit: number | null; }
```
- `form()` over a `signal<EntryFormValue>` with a `schema()`:
  - `required` on `effectiveDate`; `type` constrained to the two values (hlm select).
  - `applyEach(lines, ...)`: per line — `accountId` required; **exactly one** of
    `debit`/`credit` is a positive number (a `validate` on the line subtree).
  - **`validateTree(lines, ...)`**: cross-line rules — (a) **Σdebit == Σcredit**
    (balanced), (b) **≥ 2 lines** with a value. Emits a tree-level error surfaced in the
    footer.
- **Account picker:** per-line **plain hlm `select`** over `AccountsService` filtered to
  `postable === true` (label `"<number> <name>"`). (Searchable combobox is a later upgrade.)
- **Live footer:** Σ debits, Σ credits, and difference — derived from the form value signal,
  decimal-aligned tabular, parens negatives. These are input echoes, not server numbers.
- **Add/remove line** rows mutate the `lines` array in the form value.
- **Submit mapping:** each `LineModel` → `PostLineRequest { accountId, direction:
  debit>0?'Debit':'Credit', amount: debit ?? credit }`. Empty trailing rows dropped.
- **Validate UX (decided):**
  - Client Signal Forms gates **Post** (disabled until the form is valid: balanced, ≥2
    lines, every line well-formed).
  - A **"Validate"** button calls `POST /entries/validate` and renders the result inline:
    `{valid:true}` → a success note; a 409/422 ProblemDetails → its `detail`/field errors
    inline (catches **chart validity** — account postable / required-dimension — and
    **closed-period** rejections the client cannot know).
  - **Post** calls `POST /entries`; on 201 → success toast + navigate to D4 detail for the
    new entry. A server 409/422 on Post is surfaced the same way as Validate.
- **Boundary (explicit):** required-dimension accounts (subledger entities) are **out of
  1c scope** for the manual journal form. If one is selected, the server dry-run/Post 422
  surfaces inline directing the user to the relevant module. Entity pickers (Customer/
  Vendor) arrive with the subledger slice. The manual form sends **no** `dimensions`.
- Route: `journal/new` → `EntryForm`. A "New entry" button on the entry list (D2) links here.

### Component 3 — D3 Pending-approval queue (`features/journal/approval-queue.ts`)

**What it does:** the maker-checker worklist. Reuses `EntriesService.listPaged({ posting:
'PendingApproval', skip, limit })` (Slice 1b). Paged table; columns: #, date, memo, lines,
creator. Each row links to the D4 detail. **SoD cue:** rows whose `createdBy` equals the
**current** `DevIdentityService.active()` sub are marked *"your entry — needs another
approver"* (approve disabled there); others are approvable. Empty/loading/error states.
Route: `journal/approvals` (nav entry).

> Note: `EntryResponse` already exposes lifecycle/identity fields used by the read screens;
> the creator identity for the SoD cue comes from the entry audit. If `EntryResponse` does
> not already carry `createdBy`, the queue derives the cue lazily (on the detail it is
> always available via `GET /audit/{id}`); the plan resolves this against the live response
> shape and falls back to "fetch audit on demand" rather than guessing.

### Component 4 — D4 Entry detail (`features/journal/entry-detail.ts`)

**What it does:** the canonical single-entry view + state actions.
- Loads `GET /entries/{id}` (+ `GET /audit/{id}` for stamps). Renders all lines with the
  account-label join, **Debit/Credit columns footed** (Σ equal, double-rule), the header
  (date, type, reference, memo, posting badge), and **creator/approver audit stamps**
  (who/when), plus the **source back-link** when `sourceRef`/`sourceType` are present.
- **Actions** by state: **Approve** (`…/approve`) — shown when posting is
  `PendingApproval`; surfaces the **403 SoD** inline when the current identity authored it.
  **Void** (`…/void`, optional reason via a small dialog/textarea) — surfaces 409 inline.
  On success, re-fetch the entry to reflect the new state. **Revise/Reverse deferred.**
- Route: `journal/:id` → `EntryDetail`. Reached from the entry list (D2), the queue (D3),
  and post-success.

## Data flow

```
identity switcher → DevIdentityService.active() → authInterceptor mints DevToken (per request)
entry-form → POST /entries/validate (dry run, inline result) and POST /entries (201 → /journal/:id)
approval-queue → GET /entries?posting=PendingApproval (paged) → row → /journal/:id
entry-detail → GET /entries/{id} + /audit/{id};  Approve/Void → re-fetch entry
```

## Error handling

- Client form invalid → Post disabled; the footer shows the unbalanced/too-few-lines
  tree error and per-line errors appear on the offending rows.
- Server 422 (unbalanced caught server-side, chart violation, required-dimension missing)
  and 409 (closed period) from **Validate** or **Post** → rendered inline from
  ProblemDetails (`detail` + per-field `errors`), reusing the engine's own messages.
- Approve 403 (SoD) → inline on the detail, explaining a different approver is required.
- Void/approve 409 / 404 → inline.
- Every data view has empty/loading/error states (matching 1b).

## Testing (Vitest + HttpTestingController — no live backend)

- `dev-identity.service.spec` — default identity is the clerk; `use(approver)` flips
  `active()`. `auth.interceptor.spec` (update) — the minted token reflects the **active**
  identity, and re-mints after `use(...)`.
- `entry-form.spec` — unbalanced lines block Post (tree validator); a balanced ≥2-line form
  enables it; the two-column `LineModel` → `PostLineRequest` mapping is correct (direction
  + amount); the **Validate** button surfaces a stubbed 422 inline; **Post** issues
  `POST /entries` and navigates on 201.
- `approval-queue.spec` — renders pending rows; an entry authored by the active identity
  shows the SoD cue / disabled approve; switching identity flips it.
- `entry-detail.spec` — lines + audit stamps render; Approve issues the call and a stubbed
  403 is surfaced inline; Void issues `…/void` and re-fetches.

## Scope boundary (YAGNI)

**In:** D1 post + client/server validate, D3 queue with SoD cue, D4 detail with
approve/void + stamps, the dev identity switcher.
**Out (deferred):** revise/reverse actions, dimension/entity pickers (subledger slice),
searchable account combobox, per-client Format Profile fetch (uses `DEFAULT_FORMAT_PROFILE`),
real auth/user/client switching (front-door slice). The manual journal sends no `dimensions`.

## Open (resolve during the plan / before demo)

- Confirm `EntryResponse` carries the creator identity needed for the D3 SoD cue; if not,
  the queue fetches `GET /audit/{id}` on demand for the cue (the detail always has it).
- Seed the demo client + two memberships (clerk, approver) and set `environment.devClerk`/
  `devApprover` to matching pairs before the live demo — not a build/test dependency.
- Confirm Spartan has a dialog/textarea pairing for the Void reason, else a plain inline
  textarea + confirm (resolved at generation time alongside any needed hlm components).
