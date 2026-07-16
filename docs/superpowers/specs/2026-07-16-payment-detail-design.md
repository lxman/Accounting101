# AR Payment Detail Screen — Design (Slice 2c-1)

**Status:** Approved for planning
**Date:** 2026-07-16
**Predecessors:** Slice 2b-1 (refund detail, `d8d6ec2`), 2b-2 (credit detail, `ee103dd`). Part of the 2c arc (statement drill-in); companion to [[accounting101-drilldown-slices]].

## Goal

Add an AR payment-detail screen reachable by whole-row drill-in from the Payments list, backed by a new `GET /clients/{id}/payments/{paymentId}` returning the payment, the invoices it was applied to (allocations resolved to invoice numbers), the unapplied overpayment remainder (held as customer credit), and its posted journal entry id, with a `gl.read`-gated "View journal entry" drill.

Payment detail is the richest of the AR detail screens: like credit detail it resolves allocations from the payment's GL entry, and it adds an **unapplied remainder** line (the overpayment held as customer credit). It is the last missing drill target for the 2c statement drill-in (invoice/credit/refund detail already exist; only payment did not).

This slice also introduces a shared `InvoiceAllocationLine` (renamed from `CreditAllocationLine`) used by both `CreditView` and the new `PaymentView` — one allocation-line type across the module rather than parallel duplicates.

## Context: what exists today

- **`Payment(Guid Id, Guid CustomerId, DateOnly Date, decimal Amount, string? Method, bool Voided)`** — a stored document. The per-invoice split is NOT stored; it lives only as ledger dimensions on the payment's GL entry (`Payment.cs`).
- **`IPaymentStore.GetPaymentAsync(clientId, paymentId)`** already exists (returns `Payment?`).
- **The payment's GL entry** (`PaymentPosting.ComposePayment`): Debit Cash (full amount); one Credit on the Receivable account per allocation, tagged `Dimensions["Invoice"] = allocation.TargetId` with the allocated amount; and, when there is an overpayment, one Credit on the CustomerCredits account for the remainder (`Amount − Σallocations`), Customer-dimensioned only (no `Invoice` dimension). So allocations are recoverable exactly as in credit detail, and the unapplied remainder equals `Amount − Σ(allocation amounts)`.
- **Allocations are recovered** from the posting via `ILedgerClient.GetEntriesBySourceRefAsync(clientId, paymentId)` → the `{Status:"Active", Posting:"Posted", ReversalOf:null}` entry (the Posted predicate the 2b-2 fix settled on) → its `Invoice`-dimensioned lines, grouped and resolved to invoice numbers via `invoices.GetAsync(...).Number`. `PaymentPosting.InvoiceDimension` is already `public` (made so in 2b-2).
- **No `GET /payments/{id}` endpoint** exists (only POST-create, POST-void, and list-by-customer).
- **`PaymentList`** (`features/receivables/payment-list.ts`, route `payments`) renders Date / Amount / Method / Allocated / Unapplied / Status with **no in-row action buttons** and no memo column. Rows are `@for (p of payments(); track p.id)`.
- **`CreditAllocationLine(Guid InvoiceId, string? InvoiceNumber, decimal Amount)`** (shipped in 2b-2) is structurally exactly what a payment allocation line needs.

## Decisions (settled during brainstorming)

- **Rich detail** — header + status + a `gl.read`-gated journal-entry drill + an allocations table + a distinct unapplied-remainder line.
- **Shared allocation-line type** — rename the shipped `CreditAllocationLine` → `InvoiceAllocationLine` (a neutral name fitting both credits and payments) and use it in both `CreditView` and the new `PaymentView`. Wire field names (`invoiceId`, `invoiceNumber`, `amount`) are unchanged, so the rename is a pure type-name refactor that does not affect serialization; backend and FE renames are each independently safe.
- **Posted predicate** — the posting pick uses `{Status:"Active", Posting:"Posted", ReversalOf:null}` (the 2b-2 lesson). Because `Payment.Amount` is a stored field (not folded), a not-yet-approved payment reads consistently as Amount = full / allocations empty / unapplied = full / no journal link — no self-contradiction (unlike the folded-amount case 2b-2 had to fix).
- **`gl.read` gate** on the "View journal entry" link (cross-area AR→GL drill), baked in from the start.
- **Endpoint** is a simple `GET /clients/{id}/payments/{paymentId}` — payments are one collection, so no type qualifier (unlike credits).
- **`Unapplied = Payment.Amount − Σ(allocation amounts)`** — equals the CustomerCredits remainder line; computed from the resolved allocations rather than read as a separate GL line, so it stays consistent with the allocations shown.

## Architecture

### Backend (Receivables module)

**Rename** `CreditAllocationLine` → `InvoiceAllocationLine` across the shipped 2b-2 code (`CreditView.cs`, `PaymentService.cs`, `GetCreditsEndpointTests.cs`). The record moves to its own file or stays in `CreditView.cs`; the plan will place it. `CreditView`'s `Allocations` becomes `IReadOnlyList<InvoiceAllocationLine>`.

**New records** (`PaymentView.cs`):
```csharp
namespace Accounting101.Receivables;

/// <summary>A customer payment plus the invoices it was applied to, the unapplied remainder held as
/// customer credit, and the id of its posted journal entry — what the payment detail endpoint returns.
/// Allocations are folded from the GL posting; Unapplied = Amount − Σallocations (the overpayment held
/// as credit); JournalEntryId lets the UI drill to the GL entry (null if none is found).</summary>
public sealed record PaymentView(
    Payment Payment,
    IReadOnlyList<InvoiceAllocationLine> Allocations,
    decimal Unapplied,
    Guid? JournalEntryId);
```
(`InvoiceAllocationLine` is the renamed shared record, reused here.)

**`PaymentService.GetPaymentViewAsync(Guid clientId, Guid paymentId, CancellationToken ct)` → `PaymentView?`:**
1. `Payment? payment = await payments.GetPaymentAsync(clientId, paymentId, ct);` — null → return null (→404).
2. Fetch the GL entry: `spawned = GetEntriesBySourceRefAsync(clientId, paymentId, ct)`; `postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null })`.
3. Allocations from `postingEntry?.Lines`: the `Invoice`-dimensioned lines, grouped by invoice id (GroupBy preserves posting-line order), each resolved to a number via `invoices.GetAsync`, amount = `Σ line.Amount` — identical to `GetCreditViewAsync`.
4. `Unapplied = payment.Amount − allocations.Sum(a => a.Amount)`.
5. Return `new PaymentView(payment, allocations, Unapplied, postingEntry?.Id)`.

**Endpoint** (`ReceivablesEndpoints.cs`): register `clients.MapGet("/payments/{paymentId:guid}", GetPayment)` near the other payment routes. Handler mirrors `GetRefund`:
```csharp
private static async Task<IResult> GetPayment(
    Guid clientId, Guid paymentId, PaymentService service, CancellationToken cancellationToken)
{
    PaymentView? view = await service.GetPaymentViewAsync(clientId, paymentId, cancellationToken);
    return view is null ? Results.NotFound() : Results.Ok(view);
}
```
`ar.read`-gated automatically via the endpoint group's `.RequireAuthorization()` + scoped store. No new capability wiring.

### Frontend (Angular 22, standalone, OnPush, zoneless)

**Rename** the FE `CreditAllocationLine` interface → `InvoiceAllocationLine` (`core/receivables/receivables.ts`), updating `CreditView.allocations` and `credit-detail.ts`'s import + `sum()` parameter type.

**New interfaces** (`receivables.ts`), wire shapes identical to the backend records:
```ts
export interface PaymentView { payment: Payment; allocations: InvoiceAllocationLine[]; unapplied: number; journalEntryId: string | null; }
```
(`Payment` interface already exists.)

**Service** (`receivables.service.ts`): `getPayment(id: string): Observable<PaymentView>` → `GET /payments/{id}` via `base()`.

**`payment-detail` component** (`features/receivables/payment-detail.ts` + `.spec.ts`): OnPush, reads `:id` from the route snapshot, calls `getPayment(id)`, renders:
- Header: "Payment" title + status chip (Active/Voided) + Date + Amount + Method (dash `—` if null).
- **Allocations table:** one row per `InvoiceAllocationLine` — invoice number (fallback `—`) → amount; plus a Total row summing the allocation amounts. If allocations is empty, a muted "No allocations" line.
- **Unapplied line:** a distinct "Unapplied (held as customer credit)" row showing `unapplied` (always shown; may be 0).
- **`gl.read`-gated journal link:** `@if (v.journalEntryId) { <a *appCan="'gl.read'" [routerLink]="['/journal', v.journalEntryId]">View journal entry →</a> }`.
- Back link to `/receivables/payments`; loading/error states mirror `refund-detail`/`credit-detail`.

**Route** (`app.routes.ts`): `{ path: 'payments/:id', component: PaymentDetail }`, ordered AFTER `payments/new`, ungated like every detail route.

**PaymentList drill-in** (`payment-list.ts` + `.spec.ts`):
- Rows become `role="button"` / `tabindex="0"` / `cursor-pointer hover:bg-muted/50`, with `(click)` and `(keydown.enter)` → `open(p.id)` where `open` navigates `['/receivables/payments', p.id]`. **Unconditional** (same-area — a Payments-list viewer already holds `ar.read`), no capability gate on the row.
- No in-row button to insulate and no memo cell to truncate (the list has neither) — this is purely additive row wiring.
- Inject `Router`.

## Testing

**Backend** (`GetPaymentsEndpointTests.cs` — new file, or extend an existing payments test file; the plan decides):
- `GET payment by id returns allocations resolved to invoice numbers, unapplied remainder, and journal entry id` — issue two invoices, record a payment that over-pays (allocates across both invoices and leaves a remainder), approve, assert `view.Payment` fields, `view.Allocations` (two lines with the right numbers + amounts), `view.Unapplied` = the remainder, `view.JournalEntryId` = the Active/Posted/non-reversal entry.
- `GET fully-allocated payment has zero unapplied` — a payment allocating its full amount → `Unapplied == 0`.
- `GET payment by unknown id is 404`.

**Backend regression** (rename): the existing `GetCreditsEndpointTests` (renamed `InvoiceAllocationLine`) must still pass — the rename is name-only.

**Frontend:**
- `payment-detail.spec.ts`: renders header (incl. method) + allocation rows + total + the unapplied line; journal link present when `journalEntryId` set AND `gl.read` granted; absent when `journalEntryId` null; absent when `gl.read` not granted.
- `payment-list.spec.ts` (extend): row click navigates to `['/receivables/payments', id]`. FE runner is **Vitest** — `vi.spyOn(...).mockResolvedValue(true)` on nav spies.
- Existing `credit-detail.spec.ts` (rename): unaffected (uses inline literals, not the type name) — must still pass.

## Constraints (carry into the plan)

- Backend namespaces follow folder structure (`Accounting101.Receivables`). Rider auto-converts explicit types to `var` — stage explicit file lists, check for stray churn before each commit.
- `environment.ts` stays modified/uncommitted (local dev config, never commit).
- FE: single-quoted TS imports, double-quoted HTML attrs, 2-space template indent. FE runner Vitest. Compile gate: `npx ng build --configuration development`.
- Only touch files named per task. Do NOT touch payables (2c-2), the statement builders / customer-account / vendor-account (2c-3), refund-* (done), or other modules.
- The rename must be complete within its module side (no lingering `CreditAllocationLine` references) so the solution compiles at each commit.
- Branch `feat/payment-detail`. Commit trailer `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## Out of scope

- 2c-2 (AP bill-payment detail) and 2c-3 (statement/credit-activity drill-in wiring) — separate slices.
- Any change to how payments are recorded, allocated, voided, or folded — read-path only.
- Editing allocations or issuing refunds/credit from the detail screen — display only.
- Historical 2b-2 spec/plan docs are not updated for the rename (they record the state as shipped).
