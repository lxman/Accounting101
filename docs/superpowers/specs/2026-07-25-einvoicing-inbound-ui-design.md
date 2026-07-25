# EInvoicing inbound — Angular preview-and-confirm screen (sub-project 2)

**Date:** 2026-07-25
**Repo:** public `Accounting101` (this one). Nothing in `Modules.Private` or `EInvoice` is modified.
**Predecessor:** sub-project 1 — the backend — is shipped and specced at
`EInvoice/docs/superpowers/specs/2026-07-24-einvoicing-inbound-backend-design.md`.

## Goal

Give an operator a screen that turns a vendor's EN 16931 e-invoice — a Factur-X/ZUGFeRD hybrid PDF,
a raw CII XML, or a raw UBL 2.1 XML — into a reviewed draft vendor bill: upload, read the engine's
findings, confirm the vendor, code each line to an expense account, and post to Payables' existing
bill-drafting endpoint.

The backend module is **advisory and stateless** — it parses, validates, maps, and returns a
proposal, and writes nothing. Everything that turns that proposal into a bill happens here, in the
UI, against Payables' own public API. That is what keeps the production module free of any
cross-module reference.

## What the backend already gives us

`POST /clients/{clientId}/einvoicing/inbound/preview`, multipart, field `file`, 15 MB cap.
**200-with-findings is the success contract**; 4xx means the *request* failed, never that the
*document* did.

```
InboundPreviewResponse {
  syntax: "Cii" | "Ubl" | "Unknown"
  profile: string | null
  canImport: boolean
  findings: FindingDto[]          // { ruleId, severity, message, businessTerm?, location? }
  proposal: BillProposalDto | null
  vendorMatch: VendorMatchDto     // { vendorId, vendorName, sellerName }
}

BillProposalDto {
  suggestedVendorId, sellerName, billDate, dueDate,
  vendorReference, memo, lines: { description, amount }[],
  total, linesAreDetail
}
```

Four properties of that contract drive this design:

1. **Lines carry no expense account.** Chart coding is the caller's job — it is the single thing
   the operator *must* supply before a proposal can become a bill.
2. **`suggestedVendorId` is null on every real preview.** The endpoint passes an empty vendor list
   deliberately: the module references no other module, so it cannot read Payables' vendors.
   `vendorMatch.sellerName` is always populated, and matching seller → vendor is this screen's job.
3. **A PDF carrier is indistinguishable from raw CII on the wire.** `PreviewPdf` reports
   `syntax: "Cii"`. The UI knows it uploaded a PDF because it holds the file.
4. **`profile` has two shapes.** CII gives a `FacturXProfile` enum name (`"En16931"`,
   `"XRechnung"`, `"Extended"`); UBL gives the raw `cbc:CustomizationID` URN. The backend's own doc
   comment warns callers not to assume one shape.

And one property of Payables: `DraftBillRequest` requires `vendorId`, `billDate`, and lines that
each have a description, an `expenseAccountId`, and `amount > 0`. A bill has no tax concept — VAT
arrives as its own line — which is why the proposal's total is always BT-112 (tax-inclusive), never
BT-115.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Where the UI lives | Public repo, module-gated | Every other paid module's UI (receivables, payables, payroll) is already public and gated by `enabledModules`. The proprietary IP is the EInvoice engine and the backend module, not an upload form. Zero conditional build machinery. |
| Edit model | Header read-only, vendor selectable, lines fully editable | Splitting a single BT-112 fallback line across accounts is the common real case; header fields are the audit link back to the source document and editing them silently divorces the bill from its origin. |
| PDF pane | Collapsible, PDF uploads only | The human-readable half is the entire point of a hybrid format. Costs one blob URL client-side. XML uploads get no pane and no empty placeholder. |
| Unmatched vendor | Pick from existing, or operator-initiated inline create | Suggest-only ruled out the *backend* creating vendors silently. An explicit operator action is a different thing, and without it a first-time vendor is a dead end that forces abandoning the upload. |
| Duplicate check | Bounded scan, warn only | No server-side `vendorReference` filter exists, and `vendorId` is required. Warn rather than block: legitimate re-issues exist and a bounded check has no business hard-blocking anything. |
| Placement | `/payables/bills/import`, under the Payables shell | The output is a vendor bill. Mirrors `statement-import` at `/cash/statements/import`. |

## Architecture

### New files

| File | Purpose |
|---|---|
| `core/einvoicing/einvoicing.ts` | Wire models mirroring the DTOs, plus two pure functions: `matchVendor(sellerName, vendors)` and `findDuplicates(reference, bills)`. |
| `core/einvoicing/einvoicing.service.ts` | `previewInbound(file): Observable<InboundPreviewResponse>` — one `FormData` POST, mirroring `BankingService.importStatements`. |
| `core/capabilities/module.guard.ts` | `moduleEnabled` — a `CanActivateFn` reading `route.data['requiredModule']`, structurally identical to `canWrite`. None exists today. |
| `features/einvoicing/einvoice-import.ts` | The screen. One component, `upload → preview` stage signal. |
| Four `.spec.ts` files alongside | Per repo convention. |

Module code stays under one `features/einvoicing/` folder even though the route nests under
Payables, so it could be lifted into a private overlay later without archaeology.

### Edited files

- **`app.routes.ts`** — one child of the Payables shell:
  `{ path: 'bills/import', component: EInvoiceImport, canActivate: [canWrite, moduleEnabled],
  data: { requiredCapability: 'ap.write', requiredModule: 'einvoicing', fallback: '/payables/bills' } }`
- **`layout/nav.ts`** — a child link under Payables:
  `{ label: 'Import e-invoice', path: '/payables/bills/import', area: 'ap', moduleKey: 'einvoicing' }`.
  The existing `visibleSections` filter hides it automatically; no nav code changes.
- **`features/payables/bill-list.ts`** — an "Import e-invoice" button beside "New bill", inside
  `@if (caps.moduleEnabled('einvoicing'))`. `moduleEnabled` reads a computed signal, so a plain
  `@if` is already reactive — no new structural directive.
- **`core/capabilities/route-sentinel.service.ts`** — a symmetric `requiredModule` check beside the
  existing `requiredCapability` one, so a client whose entitlement lapses mid-session is bounced off
  the screen. Without it the guard protects entry but not occupancy.
- **`core/accounts/accounts.service.ts`** — the expense-account filter
  (`type === 'Expense' && postable && active`), currently inline in `bill-editor.ts:136-137`, becomes
  an `expenseAccounts` computed on `AccountsService`; `bill-editor.ts` and this screen both read it.
  A targeted cleanup in code this work touches, not a general refactor.
No test-support changes are needed: `provideCapabilities(...)` only takes capability strings, but
specs that need to set enabled modules construct `StubCapabilityService` directly and provide it —
which `route-sentinel.service.spec.ts:13-25` already does. `capability.testing.ts` is untouched.

## The screen

### Stage `upload`

A file input accepting `.pdf,.xml`, a client-side 15 MB check mirroring the endpoint's
`MaxUploadBytes`, and an "Upload & preview" button.

The client-side size check matters because the endpoint's own friendly 413 is unreachable for an
oversized body — `WithFormOptions` caps the multipart reader, so Kestrel rejects mid-stream with a
bare 400 before the handler runs. Checking in the browser is the only way the operator sees a
readable message.

### Stage `preview`

Top to bottom:

1. **Document strip** — file name, syntax badge, profile badge. The profile formatter is
   shape-tolerant and never branches on syntax: it maps the handful of values it recognises — the
   `FacturXProfile` enum names and the EN 16931 / XRechnung / Peppol customisation URNs — to short
   labels, and renders anything else as the raw string it was given. An unrecognised profile shows
   up honestly rather than as "Unknown".
2. **PDF pane** — only when the picked file is a PDF. An `<object>` on a blob URL, collapsible, open
   by default, `URL.revokeObjectURL` on teardown and on reset.
3. **Findings** — grouped Error → Warning → Information. Each row shows the rule id in mono, the
   message, and `businessTerm`/`location` when present. Errors are always expanded; warnings and
   information collapse behind a count when `canImport` is true. Rule ids pass through verbatim,
   official (`BR-*`, `PEPPOL-*`, `BR-DE-*`) and module (`einvoicing-*`) alike — they are what an
   operator takes to their vendor.
4. **Blocked state** — `canImport: false` means `proposal` is null, so there is no form at all:
   findings plus "Choose another file". No half-filled form to mislead anyone.
5. **Proposal form**, when `canImport: true`:
   - **Vendor** — dropdown prefilled by `matchVendor`. On no match, the seller name is shown with a
     `Create vendor "<SellerName>"` button.
   - **Document header, read-only** — bill date, due date, vendor reference, memo, as a definition
     list rather than disabled inputs.
   - **Duplicate warning** — appears once vendor and reference are both known.
   - **Lines table** — description input, `app-currency-input` for amount, expense-account
     `hlmSelect`, remove button, and "+ Add line". Splitting a fallback line is: edit the amount
     down, add a line, type the remainder. No bespoke split control.
   - **Totals footer** — line sum against the document's `total` (BT-112). Drift shows an amber
     "Lines total X, document says Y" line. **Warning, not a block**: the backend guarantees detail
     lines sum exactly to BT-112 and a fallback line *is* BT-112, so drift only ever comes from a
     deliberate operator edit, and there are legitimate reasons for one.
   - **Confirm** — `Create draft bill`, `*appCan="'ap.write'"`, enabled only when a vendor is set,
     there is at least one line, and every line has a description, an account, and `amount > 0` —
     the same predicate as `BillEditor.canSave`, because it is the same server contract.
6. **Success** — the form is replaced by a link to the created draft at `/payables/bills/{id}` and
   an "Import another" action resetting to `upload`. This follows `statement-import`'s stay-put
   pattern rather than `bill-editor`'s navigate-away, because importing a folder of invoices is the
   realistic workflow.

## Data flow

**Upload → preview.** `previewInbound(file)` posts the `FormData`. The component keeps the raw
`InboundPreviewResponse` — findings, syntax, profile and the read-only header fields are read
straight off it — and derives one editable model:

```ts
interface EditableLine { lineId: string; description: string; amount: number; expenseAccountId: string | null; }
interface ImportModel { vendorId: string; lines: EditableLine[]; }
```

Only vendor and lines are mutable. Header fields live on the response object, which makes
"read-only from the document" structural rather than a `disabled` attribute someone can delete
later.

**Vendor resolution**, in order: `proposal.suggestedVendorId` if non-null, else
`matchVendor(vendorMatch.sellerName, vendors())?.id`, else empty. The first branch is always null
against today's backend, but honouring it costs one `??` and means the screen still works if the
module ever gains a vendor list.

`matchVendor` is a verbatim TypeScript mirror of the backend's `VendorMatcher.Match`: trim,
case-insensitive equality, suggest only on a unique hit, stay silent when two vendors share a name.
Mirroring it exactly is deliberate — the backend version is built and tested but unused, and the two
must not drift.

**Inline vendor create** calls `PayablesService.create(sellerName, null)`, which already `tap`s the
new vendor into the shared `vendors` signal, then selects the returned id. No local cache to keep in
sync.

**Duplicate check** runs whenever `vendorId` and `proposal.vendorReference` are both present — so it
re-runs on a vendor change and after an inline create. One call to
`listBills({ vendorId, skip: 0, limit: 200, order: 'desc' })`, then over `page.items.map(v => v.bill)`:
drop `status === 'Void'`, keep case-insensitive trimmed matches on `vendorReference`. Hits render as
an amber strip with a `routerLink` each.

The strip states what was actually checked: when `page.total > 200` the wording becomes "no
duplicate among this vendor's 200 most recent bills" rather than "no duplicate found", so a bounded
miss never reads as a clean result.

**Confirm** assembles a `DraftBillRequest` from the read-only header plus the edited lines and posts
through `PayablesService.draftBill`. Success stores the returned bill id; failure surfaces
`extractProblem(e).detail` in place, leaving the form intact so the operator can fix and retry.

**Preview errors** land in the same message signal — 400 for a missing or empty file, 403 when the
module is not enabled, 413 for oversize, 5xx otherwise — all via `extractProblem`, all leaving the
operator on the upload stage with their file still picked.

## Gating

Four layers, one authority. The nav link (`moduleKey`) and the bill-list button (`@if`) control
*discovery*; the route guard controls *entry*; the sentinel controls *occupancy*. None of them is
security — the endpoint's own `EnabledModules` check is the authority, and the UI treats 403 as a
reachable response rather than an impossible one.

Capability and module are checked independently: `ap.write` gates writing a bill, `einvoicing` gates
the module, and a user can plausibly hold either without the other.

## Edge cases

- **Credit notes.** Blocked by the backend on a type-code family, returning `canImport: false` with
  an `einvoicing-*` finding. The blocked state covers it with no special case, but this is the most
  likely "why won't it import?" — the finding message carries the explanation and is shown verbatim.
- **Currency mismatch.** BT-5 against the client's base currency is a blocking finding. Same path,
  no special case. (This is what the per-client base-currency work was the prerequisite for.)
- **No usable expense accounts.** An empty filtered list means the confirm can never enable, which
  reads as a broken button. That state gets an explicit hint linking to the chart of accounts
  instead of a silently dead control.
- **File changed after a preview.** Picking a new file clears findings and proposal and revokes the
  blob URL, so stage-2 state cannot outlive the document it describes.
- **Client switched mid-flow.** An effect on `ClientContextService.clientId()` resets to the upload
  stage. Posting would fail safely anyway — the old `vendorId` does not exist under the new client —
  but a stale proposal on screen under another client's name is worth preventing outright.

## Testing

Repo idiom throughout: `TestBed` with `provideZonelessChangeDetection`, `provideRouter([])`,
`provideHttpClient` + `provideHttpClientTesting`, `provideCapabilities(...)`, and
`HttpTestingController.verify()` — the shape of `statement-import.spec.ts`.

| Spec | Covers |
|---|---|
| `einvoicing.spec.ts` | Pure functions, no TestBed. `matchVendor`: exact hit, case/whitespace tolerance, no match, two same-named vendors → null, null/blank seller → null. `findDuplicates`: hit, case-insensitive hit, voided bill excluded, absent reference → no check. |
| `einvoicing.service.spec.ts` | Posts multipart to `/clients/{id}/einvoicing/inbound/preview` with the file under `file`; returns `EMPTY` with no client selected. |
| `module.guard.spec.ts` | Allows when enabled; redirects to `fallback` when not; waits for `caps.loaded` before deciding. |
| `einvoice-import.spec.ts` | Upload posts and advances to `preview`. `canImport: false` renders findings and no form. Vendor prefills from `sellerName`. Duplicate strip appears after `listBills` and links the hit. Confirm posts a `DraftBillRequest` carrying the document's header plus the edited lines. Total drift warns but does not disable confirm. Inline create posts `/vendors` and selects the result. A 403 from preview surfaces as a message on the upload stage. |
| `route-sentinel.spec.ts` (extend) | Redirects when `requiredModule` leaves `enabledModules`, alongside the existing capability case. |
| `bill-list.spec.ts` (extend) | Import button absent when the module is disabled, present when enabled. |

**Setting enabled modules in specs.** `provideCapabilities(...caps)` takes only capability strings,
so module-aware specs construct `StubCapabilityService` directly, call `setEnabledModules([...])`,
and provide it for `CapabilityService` — the pattern `route-sentinel.service.spec.ts:13-25` already
uses. No test-support change is needed.

**Not tested:** the PDF pane's rendering. It is a browser `<object>` on a blob URL; the spec asserts
the URL is created for a PDF, not created for XML, and revoked on reset. The rendering is the
browser's job.

## Scope

**In scope:** the new and edited files listed above — four production files plus their four specs,
five edited files, and two extended specs; the upload/preview/confirm flow; vendor
matching and inline create; per-line expense-account coding and line editing; findings display;
duplicate warning; the module route guard and sentinel extension; the tests above.

**Out of scope (documented, not dropped):**

- **Import history and stored source documents.** The module is stateless and so is this screen. If
  the operator navigates away mid-review the upload is gone and they re-pick the file. Retaining
  source evidence is a reasonable later feature.
- **Batch upload.** One file at a time. "Import another" resets the screen; it does not queue.
- **Vendor-credit documents.** Payables has no vendor-credit *document* (`VendorCreditApplication`
  applies an existing credit balance, it does not create one), which is why the backend blocks
  credit notes. Unblocking them is a Payables change, not a UI one.
- **Retiring the temporary `money-formatter.ts` SYMBOLS mirror** and the nine `formatMoney` call
  sites still passing `'USD'`. That is the deferred base-currency UI slice; this screen uses the
  existing `money()` helper as its neighbours do.
- **Outbound (Arc C)** and **PDF generation (Arc B)**.

## Global constraints

- Angular signals + `@angular/forms/signals`, `ChangeDetectionStrategy.OnPush`, standalone
  components, inline templates, spartan-ng helm imports — the idiom of every neighbouring feature.
- TDD; commit per slice; stage explicit file lists.
- No new npm dependency. The PDF pane is a browser `<object>`, not a viewer library.
- The public build stays green for a clone with no `Modules.Private` overlay and no e-invoicing
  entitlement — the screen simply never becomes reachable.
