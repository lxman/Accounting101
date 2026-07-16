# AP Bill-Payment Detail Screen — Design (Slice 2c-2)

**Status:** Approved for planning
**Date:** 2026-07-16
**Predecessor / template:** Slice 2c-1 (AR payment detail, `b4eab8d`) — this is its symmetric Payables mirror. Part of the 2c arc; companion to [[accounting101-drilldown-slices]].

## Goal

Add an AP bill-payment-detail screen reachable by whole-row drill-in from the Bill Payments list, backed by a new `GET /clients/{id}/bill-payments/{paymentId}` returning the payment, the bills it was applied to (allocations resolved to bill numbers), the unapplied remainder (held as vendor credit), and its posted journal entry id, with a `gl.read`-gated "View journal entry" drill — and fix the pre-existing bill-payments-list bug (the list + bill-detail read per-payment allocations the backend never folds).

This is the exact Payables twin of AR payment detail (2c-1). It is the AP half of the 2c statement drill-in (`VendorAccountBuilder` statement "Payment" lines will need it in 2c-3).

## Context: what exists today (all mirrors AR)

- **`BillPayment(Guid Id, Guid VendorId, DateOnly Date, decimal Amount, string? Method, bool Voided)`** — a stored document; the per-bill split is NOT stored, only as ledger dimensions on the payment's GL entry (`BillPayment.cs`).
- **`IBillPaymentStore.GetPaymentAsync(clientId, paymentId)`** already exists (returns `BillPayment?`).
- **The bill-payment GL entry** (`BillPosting.ComposePayment`): one Debit on the Payable account per allocation, tagged `Dimensions["Bill"] = allocation.TargetId` with the allocated amount; and, when there is an overpayment, one Debit on the VendorCredits account for the remainder (`Amount − Σallocations`), Vendor-dimensioned only. `BillPosting.BillDimension = "Bill"` is a `public const` (analogous to `PaymentPosting.InvoiceDimension`). So allocations are recoverable from the posting, and the unapplied remainder equals `Amount − Σ(allocation amounts)`.
- **Allocations are recovered** from the posting via `ILedgerClient.GetEntriesBySourceRefAsync(clientId, paymentId)` → the `{Status:"Active", Posting:"Posted", ReversalOf:null}` entry (the 2b-2/2c-1 Posted predicate) → its `Bill`-dimensioned lines, grouped and resolved to bill numbers via `bills.GetAsync(...).Number` (`Bill` has `string? Number`; `IBillStore.GetAsync` exists).
- **No `GET /bill-payments/{id}` endpoint** exists (only POST-create, POST-void, and list-by-vendor `GET /bill-payments?vendorId=`).
- **`BillPaymentList`** (`features/payables/bill-payment-list.ts`, route `/payables/payments`) renders Date / Amount / Method / Allocated / Unapplied / Status with **no in-row action buttons** and no memo column. `BillPaymentService(IBillPaymentStore payments, IBillStore bills, IBillAccountsProvider accounts, ILedgerClient ledger)`.
- **The pre-existing bug (confirmed present — same as AR):** `GET /bill-payments?vendorId=` returns raw `BillPayment[]` with no allocations, but **both** `BillPaymentList` (Allocated/Unapplied columns, `bill-payment-list.ts:88`) and `BillDetail` ("Applied payments" section, `bill-detail.ts:122` — `p.allocations.filter(a => a.targetId === billId)`) read `p.allocations` from that single endpoint. On real data `p.allocations` is `undefined` → crash. The FE interfaces + mocks already expect `allocations: {targetId, amount}[]`; the backend simply never folded them. `ListBillPayments` currently injects `IBillPaymentStore` directly.

## Decisions (settled during brainstorming — mirror 2c-1)

- **Rich detail** — header + status + `gl.read`-gated journal drill + allocations table + a distinct unapplied-remainder line.
- **`BillAllocationLine` is the general Payables allocation-line type** (target-named, mirroring the shared AR `InvoiceAllocationLine`): "one bill an allocation-based AP document (bill payment, and any future vendor-credit detail) was applied to." No rename needed — there is no shipped AP allocation-line type to rename; a future AP credit-detail reuses this rather than spawning a parallel.
- **Posted predicate** `{Status:"Active", Posting:"Posted", ReversalOf:null}` for allocations/journal id. `BillPayment.Amount` is stored (not folded), so a pending payment reads consistently (Amount full / allocations empty / unapplied full / no link) — no divergence.
- **`gl.read` gate** on the "View journal entry" link (cross-area AP→GL drill), baked in.
- **Endpoint** `GET /clients/{id}/bill-payments/{paymentId}` (bill-payments are one collection — no type qualifier). `ap.read`-gated automatically.
- **`Unapplied = Amount − Σ(allocation amounts)`** — equals the VendorCredits remainder line; computed from the resolved allocations for internal consistency.
- **List-fix is backend-only** — the list folds each payment's allocations into a flat DTO serializing to the shape the FE already expects; zero FE consumer changes; fixes both `BillPaymentList` and `BillDetail`.

## Architecture

### Backend (Payables module)

**New records** (`BillAllocationLine.cs`, `BillPaymentView.cs`):
```csharp
public sealed record BillAllocationLine(Guid BillId, string? BillNumber, decimal Amount);

public sealed record BillPaymentView(
    BillPayment Payment,
    IReadOnlyList<BillAllocationLine> Allocations,
    decimal Unapplied,
    Guid? JournalEntryId);
```

**`BillPaymentService.GetBillPaymentViewAsync(Guid clientId, Guid paymentId, CancellationToken ct)` → `BillPaymentView?`** — mirror of `GetPaymentViewAsync`:
1. `BillPayment? payment = await payments.GetPaymentAsync(clientId, paymentId, ct);` — null → null (→404).
2. `postingEntry = (await ledger.GetEntriesBySourceRefAsync(clientId, paymentId, ct)).FirstOrDefault(e => e is { Status:"Active", Posting:"Posted", ReversalOf:null });`
3. Allocations from `postingEntry?.Lines`: the `Bill`-dimensioned lines (`BillPosting.BillDimension`), grouped (GroupBy preserves posting-line order), each resolved via `bills.GetAsync`, amount = `Σ line.Amount`.
4. `Unapplied = payment.Amount − allocations.Sum(a => a.Amount)`.
5. Return `new BillPaymentView(payment, allocations, Unapplied, postingEntry?.Id)`.

**Endpoint** (`PayablesEndpoints.cs`): `clients.MapGet("/bill-payments/{paymentId:guid}", GetBillPayment)` near the other bill-payment routes. Handler mirrors `GetBill`:
```csharp
private static async Task<IResult> GetBillPayment(
    Guid clientId, Guid paymentId, BillPaymentService payments, CancellationToken cancellationToken)
{
    BillPaymentView? view = await payments.GetBillPaymentViewAsync(clientId, paymentId, cancellationToken);
    return view is null ? Results.NotFound() : Results.Ok(view);
}
```
`ap.read`-gated automatically via the endpoint group's `.RequireAuthorization()` + scoped store. No new capability wiring.

**List-fold fix** (`BillPaymentWithAllocations.cs` + service method + endpoint change):
```csharp
public sealed record BillPaymentWithAllocations(
    Guid Id, Guid VendorId, DateOnly Date, decimal Amount, string? Method, bool Voided,
    IReadOnlyList<Allocation> Allocations);
```
- `BillPaymentService.GetPaymentsWithAllocationsByVendorAsync` folds each payment's Posted GL `Bill`-dimensioned lines into `Allocation(billId, amount)` pairs (reusing `Accounting101.Settlement.Allocation`; no bill-number resolution needed for the list).
- `ListBillPayments` changes to inject `BillPaymentService` and return `BillPaymentWithAllocations[]` (the 400-on-missing-vendorId behavior unchanged). Serializes to the FE `BillPayment` shape `{id, vendorId, date, amount, method, voided, allocations:[{targetId, amount}]}` → no FE consumer change.

### Frontend (Angular 22, standalone, OnPush, zoneless)

**New interfaces** (`core/payables/payables.ts`), wire shapes identical to the backend records:
```ts
export interface BillAllocationLine { billId: string; billNumber: string | null; amount: number; }
export interface BillPaymentView { payment: BillPayment; allocations: BillAllocationLine[]; unapplied: number; journalEntryId: string | null; }
```
(`BillPayment` interface already exists.)

**Service** (`core/payables/payables.service.ts`): `getBillPayment(id: string): Observable<BillPaymentView>` → `GET /bill-payments/{id}` via `base()`.

**`bill-payment-detail` component** (`features/payables/bill-payment-detail.ts` + `.spec.ts`): OnPush, reads `:id` from the route snapshot, calls `getBillPayment(id)`, renders:
- Header: "Bill payment" title + status chip + Date + Amount + Method (dash `—` if null).
- **Allocations table:** one row per `BillAllocationLine` — bill number (fallback `—`) → amount; plus a Total row. "No allocations" when empty.
- **Unapplied line:** a distinct "Unapplied (held as vendor credit)" row showing `unapplied`.
- **`gl.read`-gated journal link:** `@if (v.journalEntryId) { <a *appCan="'gl.read'" [routerLink]="['/journal', v.journalEntryId]">View journal entry →</a> }`.
- Back link to `/payables/payments`; loading/error states mirror AR payment-detail.

**Route** (`app.routes.ts`): `{ path: 'payments/:id', component: BillPaymentDetail }` in the PAYABLES block, after `payments/new`, ungated.

**BillPaymentList drill-in** (`bill-payment-list.ts` + `.spec.ts`): rows become `role="button"` / `tabindex="0"` / `cursor-pointer hover:bg-muted/50`, `(click)`/`(keydown.enter)` → `open(p.id)` navigating `['/payables/payments', p.id]`. Unconditional (same-area — a Bill-Payments viewer already holds `ap.read`). No in-row button / memo to insulate. Inject `Router`. The Allocated/Unapplied columns become correct automatically from the list-fold fix.

## Testing

**Backend** (`GetBillPaymentsEndpointTests.cs` — extend the existing bill-payments test file if present, or new; the plan decides; reuse its SoD-seed / chart / enter-bill / approve helpers):
- `GET bill-payment by id returns allocations resolved to bill numbers, unapplied remainder, and journal entry id` — enter two bills, record a payment over-paying (allocates across both, leaves a remainder), approve, assert `Payment` fields, `Allocations` (two lines with bill numbers + amounts), `Unapplied` = remainder, `JournalEntryId`.
- `GET fully-allocated bill-payment has zero unapplied`.
- `GET bill-payment by unknown id is 404`.
- List-fold: the existing list test updated to assert each payment carries its folded allocations (`{targetId, amount}`).

**Frontend:**
- `bill-payment-detail.spec.ts`: renders header (incl. method) + allocation rows + total + unapplied line; journal link present when `journalEntryId` set AND `gl.read` granted; absent when null; absent when `gl.read` not granted.
- `bill-payment-list.spec.ts` (extend): row click navigates to `['/payables/payments', id]`. FE runner **Vitest** (`vi.spyOn(...).mockResolvedValue(true)`).

## Constraints (carry into the plan)

- Backend namespaces follow folder structure (`Accounting101.Payables`). Rider auto-converts explicit types to `var` — stage explicit file lists, check for stray churn before each commit.
- `environment.ts` stays modified/uncommitted (never commit).
- FE: single-quoted TS imports, double-quoted HTML attrs, 2-space template indent. FE runner Vitest. Compile gate: `npx ng build --configuration development`.
- Only touch files named per task. Do NOT touch receivables (2c-1, done), the statement builders / customer-account / vendor-account (2c-3), or other modules.
- Branch `feat/bill-payment-detail`. Commit trailer `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## Out of scope

- 2c-3 (statement/credit-activity drill-in wiring, AR + AP) — the next slice.
- Any change to how bill payments are recorded, allocated, voided, or folded — read-path only.
- A vendor-credit detail screen (would reuse `BillAllocationLine` when built) — not part of 2c.
