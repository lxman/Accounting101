# Credit Detail Screen — Design (Slice 2b-2)

**Status:** Approved for planning
**Date:** 2026-07-16
**Predecessor:** Slice 2b-1 (refund detail, merged `d8d6ec2`). Companion to [[accounting101-drilldown-slices]].

## Goal

Add a credit-detail screen reachable by whole-row drill-in from the Credits list, backed by a new type-qualified `GET /clients/{id}/credits/{type}/{creditId}` returning the credit, the invoices it was applied to (allocations resolved to invoice numbers), and its posted journal entry id. A `gl.read`-gated "View journal entry" link drills to the GL entry.

This is the richer sibling of 2b-1: refunds carry no allocations, so refund detail is thin; credits ARE allocation-based dispositions, so the allocations table (which invoices, how much) is the reason to drill in.

## Context: what exists today

- **Credits are a unified read view over three separate collections** — `CreditNote`, `WriteOff`, `CreditApplication` — assembled date-descending by `PaymentService.GetCreditsByCustomerAsync` into `CreditDocument(string Type, Guid Id, Guid CustomerId, DateOnly Date, decimal Amount, string? Memo, bool Voided)`. `Type` ∈ `"credit-note" | "write-off" | "credit-application"`. `Memo` is null for credit-application.
- **Amount is folded from the GL entry**, not stored: `SettlementRelief.ForSourceAsync(ledger, clientId, {id}, posting.ReceivableAccountId, ct, postedOnly: true)`. The credit document stores no allocation array.
- **Allocations live in the GL posting.** `PaymentPosting.Compose{CreditNote,WriteOff,CreditApplication}` tag each allocation line with `Dimensions["Invoice"] = allocation.TargetId` (invoice id) and set the line `Amount` to the allocated portion. So the invoices a credit was applied to are recoverable from the credit's GL entry lines.
- **The GL entry is reachable by source ref.** `ILedgerClient.GetEntriesBySourceRefAsync(clientId, creditId, ct)` returns `EntryResponse[]`; each `EntryResponse` carries `IReadOnlyList<EntryLineResponse> Lines`, and each `EntryLineResponse` carries `IReadOnlyDictionary<string, Guid> Dimensions` and `decimal Amount`. The original posting is the `{ Status: "Active", ReversalOf: null }` entry (same pick 2b-1 and `VoidLedgerEntryAsync` use).
- **Invoice number resolution exists.** `invoices.GetAsync(clientId, invoiceId, ct)` returns `Invoice` with `string? Number`. (`PaymentService` already holds the `invoices` port — used by `GetInvoiceViewAsync`.)
- **Per-type by-id store getters:** `GetCreditNoteAsync` and `GetWriteOffAsync` exist on `IPaymentStore`; **`GetCreditApplicationAsync` does NOT** (only `GetCreditApplicationsByCustomerAsync`). This is the one backend gap to close.
- **No GET-by-id endpoint** exists for any credit type (`GET /credits` is list-by-customer only).
- **Void** applies to credit-note and write-off only; credit-application has no void.

## Decisions (settled during brainstorming)

- **Rich detail** — header + status + a `gl.read`-gated journal-entry drill PLUS an allocations table (which invoices, resolved to invoice numbers, with per-invoice amounts and a total).
- **Type-qualified endpoint** — `GET /clients/{id}/credits/{type}/{creditId}`; FE route `/receivables/credits/:type/:id`. The list row already knows its `type` (`CreditDocument.type`), so it builds the URL directly; the backend does one direct store lookup by type — no multi-collection scanning.
- **Allocation ordering** — preserve posting-line order (reflects entry intent); no re-sort.
- **Unknown `type` string → 404** (consistent with unknown-resource; the route is only ever built from a known list-row type).
- **`gl.read` gate baked into the FE task from the start** — the "View journal entry" link is a cross-area AR→GL affordance, exactly the case Slice-2a gated and 2b-1 fixed as a fast-follow. Apply the lesson up front here.

## Architecture

### Backend (Receivables module)

**New records** (`CreditView.cs`):
```csharp
namespace Accounting101.Receivables;

/// <summary>One invoice a credit was applied to: the invoice's id, its number (null if unnumbered),
/// and the amount of this credit applied to it. Recovered from the credit's GL entry lines
/// (each allocation line carries an "Invoice" dimension and the allocated amount).</summary>
public sealed record CreditAllocationLine(Guid InvoiceId, string? InvoiceNumber, decimal Amount);

/// <summary>A credit plus the invoices it was applied to and the id of its posted journal entry —
/// what the credit detail endpoint returns. Credit reuses the unified CreditDocument shape so the
/// detail header matches the list row; Allocations are folded from the GL posting; JournalEntryId
/// lets the UI drill from the credit to the GL entry that recorded it (null if none is found).</summary>
public sealed record CreditView(
    CreditDocument Credit,
    IReadOnlyList<CreditAllocationLine> Allocations,
    Guid? JournalEntryId);
```

**`PaymentService.GetCreditViewAsync(Guid clientId, string type, Guid creditId, CancellationToken ct)` → `CreditView?`:**
1. Load the per-type document by id:
   - `"credit-note"` → `payments.GetCreditNoteAsync`
   - `"write-off"` → `payments.GetWriteOffAsync`
   - `"credit-application"` → `payments.GetCreditApplicationAsync` (**new** getter)
   - any other `type` → return null (→ 404).
   - document null → return null (→ 404).
2. Build the `CreditDocument` exactly as `GetCreditsByCustomerAsync` does for that type: `Amount = SettlementRelief.ForSourceAsync(ledger, clientId, creditId, accounts.ReceivableAccountId, ct, postedOnly: true)` — where `accounts` is the resolved posting-accounts config (the same value `GetCreditsByCustomerAsync` uses; its local there is named `posting`, but this spec calls it `accounts` to avoid collision with the GL entry below). `Memo` = the document's memo (null for credit-application), `Voided` = the document's voided flag.
3. Fetch the GL entry: `spawned = GetEntriesBySourceRefAsync(clientId, creditId, ct)`; `postingEntry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })`; `JournalEntryId = postingEntry?.Id`.
4. Build allocations from `postingEntry?.Lines`: for each line whose `Dimensions` contains the `"Invoice"` key, take `invoiceId = Dimensions["Invoice"]` and `amount = line.Amount` (a positive magnitude — only the per-invoice AR-relief lines carry the `"Invoice"` dimension), group by `invoiceId` and sum amounts (defensive — normally one line per invoice), resolve each to a number via `invoices.GetAsync(clientId, invoiceId, ct)`. Preserve first-seen posting-line order. If `postingEntry` is null, allocations is empty.
5. Return `new CreditView(creditDocument, allocations, JournalEntryId)`.

The `"Invoice"` dimension key is the same constant `PaymentPosting` writes (`InvoiceDimension = "Invoice"`); the service references it (expose it or reuse the literal per the plan).

**New store getter** (`GetCreditApplicationAsync(Guid clientId, Guid creditApplicationId, CancellationToken ct) → CreditApplication?`) on `IPaymentStore` (`PaymentPorts.cs`) + `DocumentPaymentStore.cs`, mirroring `GetCreditNoteAsync`/`GetWriteOffAsync`.

**Endpoint** (`ReceivablesEndpoints.cs`): register `clients.MapGet("/credits/{type}/{creditId:guid}", GetCredit)` near the other credit routes. Handler mirrors `GetInvoice`/`GetRefund`:
```csharp
private static async Task<IResult> GetCredit(
    Guid clientId, string type, Guid creditId, PaymentService service, CancellationToken cancellationToken)
{
    CreditView? view = await service.GetCreditViewAsync(clientId, type, creditId, cancellationToken);
    return view is null ? Results.NotFound() : Results.Ok(view);
}
```
`ar.read`-gated automatically via the endpoint group's `.RequireAuthorization()` + the engine's scoped document store. No new capability wiring.

### Frontend (Angular 22, standalone, OnPush, zoneless)

**Interfaces** (`core/receivables/receivables.ts`), wire shapes identical to the backend records (host `JsonNamingPolicy.CamelCase`):
```ts
export interface CreditAllocationLine { invoiceId: string; invoiceNumber: string | null; amount: number; }
export interface CreditView { credit: CreditDocument; allocations: CreditAllocationLine[]; journalEntryId: string | null; }
```

**Service** (`core/receivables/receivables.service.ts`): `getCredit(type: string, id: string): Observable<CreditView>` → `GET /credits/{type}/{id}` via the existing `base()` helper.

**`credit-detail` component** (`features/receivables/credit-detail.ts` + `.spec.ts`): OnPush, reads `:type` and `:id` from the route snapshot, calls `getCredit(type, id)`, renders:
- Header: type label (Credit note / Write-off / Apply credit) + status chip (Active/Voided) + Date + Amount + Memo (dash `—` for credit-application / null memo).
- **Allocations table:** one row per `CreditAllocationLine` — invoice number (fallback `—` if null) → amount; plus a Total row summing the allocation amounts. If allocations is empty, show a muted "No allocations" line instead of an empty table.
- **`gl.read`-gated journal link:** `@if (v.journalEntryId) { <a *appCan="'gl.read'" [routerLink]="['/journal', v.journalEntryId]">View journal entry →</a> }` — both conditions required (present id AND `gl.read`). Uses the shared `CanDirective`.
- Back link to `/receivables/credits`; loading/error states mirror `refund-detail`.

**Route** (`app.routes.ts`): `{ path: 'credits/:type/:id', component: CreditDetail }`, ordered AFTER `credits/new`, ungated like every detail route.

**credit-list drill-in** (`features/receivables/credit-list.ts` + `.spec.ts`):
- Rows become `role="button"` / `tabindex="0"` / `cursor-pointer hover:bg-muted/50`, with `(click)` and `(keydown.enter)` → `open(c.type, c.id)` where `open` navigates `['/receivables/credits', type, id]`. **Unconditional** (same-area — a Credits-list viewer already holds `ar.read`), no capability gate on the row.
- Void button (`*appCan="'ar.write'"`, only for non-voided credit-note/write-off) gets `$event.stopPropagation()` on both `(click)` and `(keydown.enter)` so it never triggers row navigation.
- Memo cell wrapped in `<span appTruncate>` (the credit-list memo was in Slice-1's deferred truncation set).
- Inject `Router`; import `TruncateDirective`.

## Testing

**Backend** (`GetCreditsEndpointTests.cs`, extend):
- `GET credit-note by id returns folded amount, allocations resolved to invoice numbers, and journal entry id` — issue two invoices, record a credit note allocating across both, assert `view.Credit` fields, `view.Allocations` (two lines with the right invoice numbers + amounts), `view.JournalEntryId` = the Active/non-reversal entry sourced from the credit.
- `GET credit-application by id has null memo and resolved allocations` — seed unapplied credit, apply it across an invoice, assert `Memo` is null and allocations resolve.
- `GET credit by unknown id is 404`.
- `GET credit by unknown type is 404`.
  (Write-off shares the credit-note code path; covered structurally by the credit-note test.)

**Frontend:**
- `credit-detail.spec.ts`: renders header fields + allocation rows (invoice numbers + amounts) + total; journal link present when `journalEntryId` set AND `gl.read` granted; link absent when `journalEntryId` null; link absent when `gl.read` not granted; credit-application memo renders `—`.
- `credit-list.spec.ts` (extend): row click navigates to `['/receivables/credits', type, id]`; Void button click does not navigate (flush the void POST + reload). FE runner is **Vitest** — `vi.spyOn(...).mockResolvedValue(true)` on nav spies.

## Constraints (carry into the plan)

- Backend namespaces follow folder structure (`Accounting101.Receivables`). Rider auto-converts explicit types to `var` — stage explicit file lists, check for stray churn before each commit.
- `environment.ts` stays modified/uncommitted (local dev config, never commit).
- FE: single-quoted TS imports, double-quoted HTML attrs, 2-space template indent. FE runner Vitest. Compile gate: `npx ng build --configuration development`.
- Only touch files named per task; do NOT touch refund-* (2b-1, done), statement lists (2c), or other modules.
- Branch `feat/credit-detail`. Commit trailer `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## Out of scope

- 2c (statement-of-account line drill-in) — still blocked on the `StatementLine` DTO lacking id/discriminator + missing AR/AP payment-detail screens.
- Any change to how credits are recorded, voided, or folded — read-path only.
- Editing/redirecting allocations from the detail screen — display only.
