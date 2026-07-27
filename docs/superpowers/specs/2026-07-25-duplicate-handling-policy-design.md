# Duplicate handling — policy, and the bill duplicate lookup (sub-project 1)

**Date:** 2026-07-25
**Repo:** `Accounting101`

## Why

A survey of the codebase found six different behaviours for what turned out to be two different
problems:

| Surface | Behaviour today |
|---|---|
| Ledger post | Caller-declared idempotency key: same id + same financial content → replay; different content → 422 (`LedgerEndpoints.cs:76-128`, `EntryComparison.cs`) |
| Inventory SKU | Natural-key uniqueness, hard 409 (`InventoryService.cs:18`, `DocumentItemStore.cs:36`) |
| Depreciation runs | Period-scoped singleton, hard failure (`FixedAssetsRunService.cs:25`) |
| Platform/control registrations | Idempotent upsert — repeat silently overwrites |
| Vendors, customers, AR invoices, bank statement import | Nothing |
| AP bills (e-invoicing import) | Advisory client-side warning, bounded, and blind to drafts |

`docs/design-principles.md` — the canonical rulebook every spec defers to — says nothing about
duplicates. The inconsistency is downstream of never having decided.

The trigger was a live defect: the e-invoicing import screen's duplicate warning never fires for a
bill that is still a draft, because it scans `GET /bills`, and `BillPaymentService.ListBillViewsAsync:224`
filters to `BillStatus.Entered`. Importing the same invoice twice before entering the first — the
likeliest real sequence — produces no warning.

## The governing standard

**Don't hand-hold more than necessary.** A duplicate check earns its place only where duplication
moves money. Duplicate vendor records, duplicate customers, a customer invoiced twice — the cost is
embarrassment, not money, and detecting those is out of scope by decision, not oversight.

## The principle

Added to `docs/design-principles.md` as a new section:

> ### Duplicate submissions
>
> Two different questions live here, and conflating them is why our answers diverged. They get
> different mechanisms and neither borrows the other's.
>
> **A retry is invisible.** The same operation delivered twice — a network retry, a double-click, a
> redelivery — must produce one result. Where a repeat moves money, the write accepts a
> caller-supplied id: same id and same content replays the existing document; same id and different
> content is a 422. With no key there is no protection, and that is the caller's choice, not a defect.
>
> **A business duplicate is a human decision.** A genuinely new submission that *resembles* something
> already recorded — the same invoice number from the same vendor — is never auto-resolved. Re-issues,
> corrections and annual repeats are all legitimate. The system reports what it found, links to it,
> and lets the operator decide. It does not block.
>
> **The module that owns the documents answers the question.** Duplicate lookup is a server-side query
> on the owning module, never a scan assembled by a caller from a list endpoint. A caller cannot know
> what a list is not showing it, and a bounded scan that reports "none found" is a lie of omission.
> The lookup states its own completeness.
>
> **We do not police untidiness.** Duplicate vendor records, duplicate customers, a customer invoiced
> twice — the cost is embarrassment, not money. Detecting those is hand-holding and we don't do it.

## Scope of this sub-project

**In:** the principle above, Payables' bill duplicate lookup, and switching the e-invoicing import
screen onto it (deleting the client-side scan it replaces).

**Out, each its own sub-project:**

- **Retry keys on money-moving POSTs** (sub-project 2) — an optional caller-supplied `Id` on
  `RecordBillPaymentRequest`, the AR equivalent, cash vouchers, refunds and the two credit-application
  endpoints; same id + same content → 200 replay, different content → 422, absent → today's behaviour.
  Because the document id feeds `EntryIdentity.ForSource`, the ledger's existing replay path engages
  unchanged. The UI editors mint a `crypto.randomUUID()` per form and reuse it across retries.
- **Bank statement duplicate lookup** (sub-project 3) — `GET /bank-statements/duplicates`, matching on
  cash account + statement date, called from the import screen. Deliberately not line-level
  `ExternalRef` reconciliation: partial overlaps and incremental statements make that a feature, not a
  rule. **SHIPPED 2026-07-26** (`master` `e1abc8c`) — see
  [its design](2026-07-26-bank-statement-duplicates-design.md). It warns on the manual editor as well
  as the import screen; the two carry the identical field pair and failure mode.
- **Everything the standard excludes** — AR invoices, vendor and customer master records, and general
  cross-module consistency for its own sake.

## The lookup

### Endpoint

`GET /clients/{clientId}/bills/duplicates?vendorId={guid}&reference={string}`

Registered in `PayablesEndpoints.MapPayablesEndpoints` beside the other bill reads. It takes no new
authorization gate: like every other Payables read it reaches documents through `IBillStore`, whose
`ScopedDocumentStore` is the chokepoint where `ModuleAccess.AuthorizeAsync` already enforces
entitlement and capability.

`vendorId` and `reference` are both required; a missing or blank either way is a 400 stating which,
matching `ListBills`' existing treatment of a missing `vendorId`.

### Response

A bare `IReadOnlyList<Bill>` — the domain type, per the module's stated convention: *"Responses are the
domain types; only the request bodies are DTOs"* (`PayablesEndpoints.cs:8-9`). No new DTO, and the
Angular `Bill` interface already exists, so the duplicate strip keeps rendering `number`, `billDate`
and `status` unchanged.

Bare array, matching `ListVendors` and `ListBillPayments` rather than the paged `ListBills`. No wrapper
and no completeness flag: the principle's "states its own completeness" clause binds a lookup that *is*
bounded, and this one never is. A wrapper whose sole field is the list, with completeness expressed
only in a comment, would state nothing at runtime either — it would just be machinery. A future
bounded lookup introduces its own response type carrying the disclosure.

Matches carry their `Lines`, which the caller does not need. That is the cost of returning the domain
type, and it is not worth a projection: matches number in the low single digits.

### The rule

`BillService.FindDuplicatesAsync(clientId, vendorId, reference, ct)`:

1. `bills.GetByVendorAsync(clientId, vendorId, ct)` — which already concatenates drafts and entered
   (`DocumentBillStore.cs:68-73`). **This is the fix**: the current screen's source, `ListBillViews`,
   filters to `Entered`.
2. Keep a bill when its `VendorReference`, trimmed and compared case-insensitively, equals the trimmed
   `reference`, and its `Status` is not `Void`.
3. Return the matches ordered by `BillDate` descending.

Blank or whitespace `reference` never matches anything — the caller gets a 400 before it reaches here,
but the service is defensive because it is also unit-tested directly.

**Void is excluded** because a voided bill is precisely when re-entering the invoice is correct.
**Draft and Entered are both included** — that is the whole point.

**No dedup between a draft and its entered successor is needed.** `EnterAsync` promotes a draft onto a
new evidentiary id and deletes the draft (`BillService.cs:19-20`), so the two never coexist.

### Cost

The query reads all of a vendor's bills, the same read `GetVendorAccount` already performs on every
vendor-account view. No new index, no new access pattern, and no optimisation until something shows it
is needed.

## The screen

`features/einvoicing/einvoice-import.ts` swaps its client-side scan for one call to the new endpoint.
Behaviour the operator sees is unchanged except that it now works: the amber strip still lists matches
with a link each, still says re-issues are legitimate, and still never blocks the confirm.

**Deleted** — the policy retires code rather than only adding it:

- `findDuplicates` and its unit tests leave `core/einvoicing/einvoicing.ts`. The rule now lives on the
  server, in the module that owns bills.
- `DUPLICATE_SCAN_LIMIT`, `duplicateScanTotal`, `duplicateScanBounded`, and the "no duplicate among
  this vendor's 200 most recent bills" wording all go. A complete lookup has nothing to disclaim, so
  the honest message is simply that none was found.
- The `listBills` call and its query construction collapse to one call to a new
  `PayablesService.billDuplicates(vendorId, reference)`. It belongs on `PayablesService`, not on
  `EInvoicingService`: Payables owns the endpoint, and the screen already depends on `PayablesService`
  for vendors and bill drafting.

**Kept:** the `generation` guard on that subscription. It still writes component state after an
await, and the parked same-key re-issue race is unaffected by moving the query server-side.

`duplicateChecked` is removed. It was written for a template branch — `@else if (duplicateChecked() &&
duplicateScanBounded())` — that the bounded-scan deletion above already removes; with that branch gone,
the template renders nothing for any of "not asked", "asked, found none", or "lookup failed", so the
signal has no reader left to justify keeping it.

## Testing

**`BillService.FindDuplicatesAsync`** — unit tests over a fake store:

- A **Draft** bill with a matching reference is returned. *This is the assertion whose absence let the
  defect ship*: every spec in the e-invoicing arc mocked `listBills` responses, so nothing ever asked
  what the real source returns.
- An Entered bill with a matching reference is returned.
- A Void bill with a matching reference is not.
- Matching ignores surrounding whitespace and case on both sides.
- A bill with a null `VendorReference` never matches.
- No matches → empty list, not null.

**Endpoint tests** — `GET /bills/duplicates` returns the matches for a vendor; missing `vendorId` and
missing `reference` are each a 400 naming the offender; another vendor's bill with the same reference
is not returned.

**A pinning test on the difference** — `ListBillViewsAsync` returns only Entered bills. The two reads
legitimately differ, and writing that down stops the next person "fixing" one to match the other. It
belongs beside the existing Payables list tests.

**Screen tests** — the strip renders from the endpoint's response and links each match; a draft match
renders (the end-to-end form of the regression above); the confirm stays enabled with matches present.
The bounded-scan test is deleted along with the bounded scan.

## Global constraints

- .NET 10; Angular 22; build with 0 warnings; TDD; commit per slice; stage explicit file lists.
- `UI/Angular/src/app/core/api/environment.ts` carries a long-lived local dev-config edit that must
  stay uncommitted.
- No new npm or NuGet dependency.
- Payables gains no reference to another module; the e-invoicing module gains no reference to anything
  — the Angular screen calls Payables directly, exactly as it already does to draft the bill.
