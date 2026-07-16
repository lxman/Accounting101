# Vendor-Credit-Application Detail Screen — Design (Slice 2c-3a)

**Status:** Approved for planning
**Date:** 2026-07-16
**Predecessor / template:** Slice 2c-2 (AP bill-payment detail, `ab948fa`). First half of 2c-3 (the missing primitive so 2c-3b statement drill-in can make AP fully drillable). Companion to [[accounting101-drilldown-slices]].

## Goal

Add a vendor-credit-application detail screen reachable by whole-row drill-in from the Vendor Credits list, backed by a new `GET /clients/{id}/vendor-credit-applications/{id}` returning the credit application, the bills it was applied to (allocations resolved to bill numbers), and its posted journal entry id, with a `gl.read`-gated "View journal entry" drill — and fix the pre-existing vendor-credits-list bug (the list reads per-application allocations the backend never folds).

This is the last missing AP detail primitive. It closes the one gap the 2c statement drill-in needs on the Payables side (statement "Credit applied" lines are vendor credit applications, which had no detail screen). It is a thinner sibling of 2c-2: a vendor credit application **applies fully**, so there is no unapplied remainder and no payment method.

## Context: what exists today

- **`VendorCreditApplication(Guid Id, Guid VendorId, DateOnly Date, bool Voided)`** — a stored document; the per-bill split is NOT stored, only as ledger dimensions on its GL entry (`BillPayment.cs`). No memo, no method, no stored amount.
- **The GL entry** (`BillPosting.ComposeCreditApplication`): one Debit on the Payable account per allocation, tagged `Dimensions["Bill"] = allocation.TargetId` (and `Vendor`) with the allocated amount; one Credit on the VendorCredits account for the total applied. So allocations are recoverable exactly as in bill-payment detail; the total applied = Σ(allocation amounts) (there is no remainder — a credit application applies existing credit fully).
- **`BillPosting.BillDimension = "Bill"`** is a `public const`. Bills carry a `Number` (`BILL-0000N`, gapless at enter); `IBillStore.GetAsync` resolves id → `Bill.Number`.
- **`BillPaymentService`** handles vendor credit applications (`RecordCreditApplicationAsync`) and already holds `payments` (`IBillPaymentStore`), `bills` (`IBillStore`), `ledger` (`ILedgerClient`) in its primary constructor.
- **Store getters:** `IBillPaymentStore.RecordCreditApplicationAsync` and `GetCreditApplicationsByVendorAsync` (by-vendor) exist; **`GetCreditApplicationAsync` (by-id) does NOT** — the same gap AR closed in 2b-2. Impl lives in `DocumentBillPaymentStore.cs`.
- **No `GET /vendor-credit-applications/{id}`** exists (only POST `ApplyCredit` and list-by-vendor `GET /vendor-credit-applications?vendorId=`).
- **The pre-existing bug (third instance of the [[accounting101-ui-mock-casing-trap]] pattern):** `GET /vendor-credit-applications?vendorId=` returns raw `VendorCreditApplication[]` with no allocations, but `VendorCreditList` reads `c.allocations` — `vendor-credit-list.ts:54` renders `{{ c.allocations.length }}` and `:93` sums `c.allocations.reduce(...)`. On real data `c.allocations` is `undefined` → crash. The FE `VendorCreditApplication` interface already declares `allocations: PaymentAllocation[]`; the backend simply never folded them. `ListCreditApplications` currently injects the store directly.

## Decisions (settled during brainstorming)

- **Reuse the shared `BillAllocationLine`** (`BillAllocationLine(Guid BillId, string? BillNumber, decimal Amount)`, introduced general/target-named in 2c-2) for `VendorCreditView.Allocations` — this is exactly the future consumer it was generalized for. No new allocation-line type.
- **No unapplied line, no method** — a credit application applies fully; the allocations Total IS the credit amount. No separate header "Amount" line (it would duplicate the Total).
- **Posted predicate** `{Status:"Active", Posting:"Posted", ReversalOf:null}` for allocations/journal id (the 2b-2/2c-1/2c-2 lesson).
- **`gl.read` gate** on the "View journal entry" link (cross-area AP→GL drill), baked in.
- **Endpoint** `GET /clients/{id}/vendor-credit-applications/{id}`. `ap.read`-gated automatically.
- **List-fix is backend-only** — fold each application's allocations into a flat DTO serializing to the shape the FE already expects; zero FE consumer changes; fixes `VendorCreditList`.
- **Add the by-id store getter** `GetCreditApplicationAsync` (closes the gap, mirroring 2b-2).

## Architecture

### Backend (Payables module)

**New store getter** (`GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct) → VendorCreditApplication?`) on `IBillPaymentStore` (`PayablesPorts.cs`) + `DocumentBillPaymentStore.cs`, mirroring the existing by-vendor getter's mapping.

**New record** (`VendorCreditView.cs`):
```csharp
namespace Accounting101.Payables;

/// <summary>A vendor credit application plus the bills it was applied to and the id of its posted journal
/// entry — what the vendor-credit detail endpoint returns. Allocations are folded from the GL posting
/// (Posted-only) and reuse the shared BillAllocationLine; a credit application applies existing credit fully,
/// so the allocations' total IS the amount (no unapplied remainder). JournalEntryId drills to the GL entry.</summary>
public sealed record VendorCreditView(
    VendorCreditApplication Credit,
    IReadOnlyList<BillAllocationLine> Allocations,
    Guid? JournalEntryId);
```

**`BillPaymentService.GetVendorCreditViewAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct)` → `VendorCreditView?`:**
1. `VendorCreditApplication? credit = await payments.GetCreditApplicationAsync(clientId, creditApplicationId, ct);` — null → return null (→404).
2. `postingEntry = (await ledger.GetEntriesBySourceRefAsync(clientId, creditApplicationId, ct)).FirstOrDefault(e => e is { Status:"Active", Posting:"Posted", ReversalOf:null });`
3. Allocations from `postingEntry?.Lines`: the `Bill`-dimensioned lines (`BillPosting.BillDimension`), grouped (GroupBy preserves posting-line order), each resolved via `bills.GetAsync`, amount = `Σ line.Amount` — identical to `GetBillPaymentViewAsync` minus the unapplied computation.
4. Return `new VendorCreditView(credit, allocations, postingEntry?.Id)`.

**Endpoint** (`PayablesEndpoints.cs`): `clients.MapGet("/vendor-credit-applications/{creditApplicationId:guid}", GetVendorCredit)` near the other vendor-credit-application routes. Handler mirrors `GetBill`:
```csharp
private static async Task<IResult> GetVendorCredit(
    Guid clientId, Guid creditApplicationId, BillPaymentService payments, CancellationToken cancellationToken)
{
    VendorCreditView? view = await payments.GetVendorCreditViewAsync(clientId, creditApplicationId, cancellationToken);
    return view is null ? Results.NotFound() : Results.Ok(view);
}
```
`ap.read`-gated automatically via the endpoint group's `.RequireAuthorization()` + scoped store. No new capability wiring.

**List-fold fix** (`VendorCreditApplicationWithAllocations.cs` + service method + endpoint change):
```csharp
public sealed record VendorCreditApplicationWithAllocations(
    Guid Id, Guid VendorId, DateOnly Date, bool Voided, IReadOnlyList<Allocation> Allocations);
```
- `BillPaymentService.GetCreditApplicationsWithAllocationsByVendorAsync` folds each application's Posted GL `Bill`-dimensioned lines into `Allocation(billId, amount)` pairs (reusing `Accounting101.Settlement.Allocation`; no bill-number resolution needed for the list).
- `ListCreditApplications` changes to inject `BillPaymentService` and return `VendorCreditApplicationWithAllocations[]` (the 400-on-missing-vendorId behavior unchanged). Serializes to the FE `VendorCreditApplication` shape `{id, vendorId, date, voided, allocations:[{targetId, amount}]}` → no FE consumer change.

### Frontend (Angular 22, standalone, OnPush, zoneless)

**New interface** (`core/payables/payables.ts`), wire shape identical to the backend record:
```ts
export interface VendorCreditView { credit: VendorCreditApplication; allocations: BillAllocationLine[]; journalEntryId: string | null; }
```
(`VendorCreditApplication` interface already exists; `BillAllocationLine` was added FE-side in 2c-2.)

**Service** (`core/payables/payables.service.ts`): `getVendorCredit(id: string): Observable<VendorCreditView>` → `GET /vendor-credit-applications/{id}` via `base()`.

**`vendor-credit-detail` component** (`features/payables/vendor-credit-detail.ts` + `.spec.ts`): OnPush, reads `:id` from the route snapshot, calls `getVendorCredit(id)`, renders:
- Header: "Credit applied" title + status chip (Active/Voided) + Date.
- **Allocations table:** one row per `BillAllocationLine` — bill number (fallback `—`) → amount; plus a Total row (= the credit amount). "No allocations" when empty.
- **`gl.read`-gated journal link:** `@if (v.journalEntryId) { <a *appCan="'gl.read'" [routerLink]="['/journal', v.journalEntryId]">View journal entry →</a> }`.
- Back link to `/payables/credits`; loading/error states mirror bill-payment-detail.

**Route** (`app.routes.ts`): `{ path: 'credits/:id', component: VendorCreditDetail }` in the PAYABLES block, after `credits/new`, ungated.

**VendorCreditList drill-in** (`vendor-credit-list.ts` + `.spec.ts`): rows become `role="button"` / `tabindex="0"` / `cursor-pointer hover:bg-muted/50`, `(click)`/`(keydown.enter)` → `open(c.id)` navigating `['/payables/credits', c.id]`. Unconditional (same-area — a Vendor-Credits viewer already holds `ap.read`). Inspect the row for in-row buttons (e.g. a Void button) needing `stopPropagation`; the list's Count/Applied columns become correct automatically from the list-fold fix. Inject `Router`.

## Testing

**Backend** (extend the existing vendor-credit-application endpoint test file, or new; the plan decides; reuse SoD-seed / chart / enter-bill / approve helpers — a credit application needs existing vendor credit, so a bill + overpaying bill-payment is set up first, then credit applied to another bill):
- `GET vendor-credit by id returns allocations resolved to bill numbers and journal entry id` — set up vendor credit (over-pay a bill), apply it across a bill, approve, assert `Credit` fields, `Allocations` (bill number + amount), `JournalEntryId`.
- `GET vendor-credit by unknown id is 404`.
- List-fold: a list test asserting each application carries its folded allocations (`{targetId, amount}`).

**Frontend:**
- `vendor-credit-detail.spec.ts`: renders header + allocation rows + total; journal link present when `journalEntryId` set AND `gl.read` granted; absent when null; absent when `gl.read` not granted.
- `vendor-credit-list.spec.ts` (extend): row click navigates to `['/payables/credits', id]`. FE runner **Vitest** (`vi.spyOn(...).mockResolvedValue(true)`).

## Constraints (carry into the plan)

- Backend namespaces follow folder structure (`Accounting101.Payables`). Rider auto-converts explicit types to `var` — stage explicit file lists, check for stray churn before each commit.
- `environment.ts` stays modified/uncommitted (never commit).
- FE: single-quoted TS imports, double-quoted HTML attrs, 2-space template indent. FE runner Vitest. Compile gate: `npx ng build --configuration development`.
- Only touch files named per task. Do NOT touch receivables, the statement builders / customer-account / vendor-account (that is 2c-3b), bill-payment-* (2c-2, done), or other modules.
- Branch `feat/vendor-credit-detail`. Commit trailer `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## Out of scope

- 2c-3b (statement / credit-activity drill-in wiring, AR + AP) — the next slice, which consumes this detail screen.
- Any change to how vendor credit applications are recorded, allocated, voided, or folded — read-path only.
- An unapplied/remainder concept — a credit application applies fully by construction.
