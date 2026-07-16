# Refund detail screen — design (Slice 2b-1 of the drill-down work)

**Date:** 2026-07-16
**Status:** Design, pending implementation
**Parent:** Slice 2 drill-down (see [[accounting101-drilldown-slices]]). This is **2b-1**
— the refund half of 2b, the easy case. **2b-2** (credit detail — three collections,
amount fold, allocation reconstruction) is a separate later slice. **2c**
(statement-of-account) remains deferred/blocked.

## Problem

The Receivables Refunds list (`features/receivables/refund-list.ts`) has no drill-in:
rows are refund entities with a stable id, but there is no detail screen and no
`GET` for a single refund (only a list-by-customer). A refund's row was also left out
of Slice 1's truncation sweep because it had no drill-in. Give refunds a detail
screen, reachable by whole-row click, whose value over the list is a deep-linkable
view plus a link to the refund's posted journal entry.

## Goals

- New backend `GET /clients/{id}/refunds/{refundId}` returning the refund plus the id
  of its posted journal entry (`RefundView`).
- New `refund-detail` screen: the refund's fields (date, amount, memo, voided status)
  and a "View journal entry →" link to `/journal/:journalEntryId` when present.
- Refund-list rows drill into the detail on whole-row click; the in-row Void button is
  insulated from navigation; the memo column gains `appTruncate`.
- Establish the detail + journal-entry-link pattern that 2b-2 (credit detail) reuses.

## Non-goals

- No credit detail, no allocation reconstruction — those are 2b-2.
- No void-from-detail (Void stays on the list). No batch invoice resolution.
- No new capability wiring — the new GET is `ar.read`-gated automatically by the
  engine's scoped document store, like every existing Receivables read.
- No new route guard — the detail route is ungated like every other detail route;
  the drill-in is same-area (a user who sees the Refunds list already holds `ar.read`),
  so rows are unconditionally clickable (unlike the cross-area worksheet drill in 2a).

## Design

### Backend

The single API host (`Accounting101.Host`) exposes both engine and module endpoints,
so the FE calls the module's `/refunds/{id}` directly and the module reads through the
engine's scoped document store (as it already does for the list).

- **`RefundView`** — a new read-model record in the Receivables module (alongside
  `CreditDocument` / the existing views): `RefundView(Refund Refund, Guid? JournalEntryId)`.
  Mirrors the nesting of the existing `InvoiceView`.
- **`PaymentService.GetRefundViewAsync(Guid clientId, Guid refundId, CancellationToken)`
  → `RefundView?`**:
  1. `refund = await payments.GetRefundAsync(clientId, refundId, ct)` (existing store
     method); if null, return null.
  2. Find the posted entry via the ledger client the service already holds:
     `entries = await ledger.GetEntriesBySourceRefAsync(clientId, refundId, ct)`, pick
     the original posting `e is { Status: "Active", ReversalOf: null }` (the same
     predicate the void path uses); `journalEntryId = posting?.Id`.
  3. Return `new RefundView(refund, journalEntryId)`.
  Enriching a read model with ledger data is an established module pattern (the credit
  read model folds ledger amounts via `SettlementRelief`).
- **Endpoint** `GET /clients/{id}/refunds/{refundId:guid}` → `GetRefund` handler,
  mirroring `GetInvoice` (`ReceivablesEndpoints.cs:128-133`):
  `var view = await service.GetRefundViewAsync(clientId, refundId, ct);
  return view is null ? Results.NotFound() : Results.Ok(view);`
  Registered next to the existing refunds routes; the group already carries
  `.RequireAuthorization()`. Place the literal `/refunds/{refundId:guid}` GET so it does
  not shadow the existing `/refunds` list GET (distinct path, no conflict).

### Frontend

- **Wire type** (`core/receivables/receivables.ts`):
  `interface RefundView { refund: Refund; journalEntryId: string | null; }`
  (`journalEntryId` is `Guid?` on the wire → `string | null`, camelCase).
- **Service** (`core/receivables/receivables.service.ts`): add
  `getRefund(id: string): Observable<RefundView>` → `this.http.get<RefundView>(this.base(\`/refunds/${id}\`))`,
  next to the existing `getInvoice` (`:71`).
- **`refund-detail.ts`** — standalone, OnPush. Reads `id` from the route
  (`route.snapshot.paramMap.get('id')!`), calls `getRefund(id)` into a
  `signal<RefundView | null>`, renders:
  - a back link to `/receivables/refunds`;
  - the refund's date, amount, memo, and voided status (a small key/value block, not a
    table — a refund has no line items);
  - **"View journal entry →"** linking to `['/journal', view.journalEntryId]`
    — rendered only when `journalEntryId` is non-null;
  - loading fallback and an error line via `extractProblem`.
  Mirrors `invoice-detail.ts` structure minus the line-item/payments tables.
- **Route** (`app.routes.ts`): add `{ path: 'refunds/:id', component: RefundDetail }`
  after the `refunds/new` entry (`:117`), ungated, mirroring `invoices/:id` (`:109`).
  Literal `refunds/new` already precedes `refunds/:id`.
- **List drill-in** (`refund-list.ts`): mirror the `bill-list` whole-row pattern —
  `role="button"`, `tabindex="0"`, `cursor-pointer hover:bg-muted/50`,
  `(click)`/`(keydown.enter)` → `router.navigate(['/receivables/refunds', r.id])`
  (inject `Router`). The rows are unconditionally clickable (same-area drill). The
  in-row **Void** button gains `(click)="$event.stopPropagation()"` (and
  `(keydown.enter)="$event.stopPropagation()"`) so voiding never navigates. The memo
  cell is wrapped in `<span appTruncate>` (`TruncateDirective` imported + added to
  `imports`). The existing `[class.opacity-50]` voided styling is preserved.

## Testing

- **Backend endpoint test** (Receivables module test project, mirroring existing
  refund/invoice endpoint tests): after posting a refund, `GET /refunds/{refundId}`
  returns 200 with the refund fields **and** `journalEntryId` equal to the posted
  entry's id; `GET /refunds/{unknownId}` returns 404. If feasible in the harness,
  assert a voided refund still reports the original posting's id (the
  `Active`/`ReversalOf == null` pick).
- **FE `refund-detail.spec.ts`** (Jasmine + TestBed, Vitest runner; `provideRouter([])`,
  HTTP testing): flush a `RefundView` and assert the fields render and the journal link
  points to `/journal/:journalEntryId`; flush a `RefundView` with `journalEntryId: null`
  and assert no journal link renders.
- **FE `refund-list` drill-in** (extend/attach to its spec if present, else a focused
  spec): clicking a row navigates to `/receivables/refunds/:id`; clicking the Void
  button does not navigate (`vi.spyOn(Router,'navigate')` — this repo uses Vitest
  `vi.spyOn`, not Jasmine `spyOn`).
- Compile gate: `npx ng build --configuration development`. Backend: the module test
  project's suite. Visual verification is best-effort — JordanSoft has no Receivables
  data (module disabled), so this is covered by the endpoint + component specs, like
  Slice 1's unreachable screens.

## Rollout

Source-only; not promoted to the JordanSoft container by this work.
