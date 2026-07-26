# Retry keys on money-moving POSTs (sub-project 2)

**Date:** 2026-07-25
**Repo:** `Accounting101`
**Follows:** `2026-07-25-duplicate-handling-policy-design.md` (sub-project 1)

## Why

`docs/design-principles.md` §3 says a repeat that moves money must be absorbed by a caller-supplied
key: *"same id and same content replays the existing document; same id and different content is a
422."* The ledger implements exactly that (`LedgerEndpoints.cs:76-128`, `EntryComparison.cs`).
Nothing above the ledger does.

The sub-project 1 scope note motivated this work with "a double-click on Record Payment pays the
vendor twice." **That is not true and the record should say so.** Every editor disables its submit
button while the request is in flight — `bill-payment-editor.ts:86`, `payment-editor.ts:86`,
`run-editor.ts:61`, `cash-voucher-editor.ts:76`. A rapid double-click cannot post twice today.

What is genuinely unprotected:

- **Failed-but-succeeded.** The POST times out or the connection drops *after* the server committed.
  The editor shows the error and re-enables the button (`bill-payment-editor.ts:157`); the operator
  clicks again and pays twice. Nothing prevents this, and it is the case caller-supplied keys exist
  for.
- **Orphan documents.** Every one of these services writes and finalizes its document and *then*
  posts to the ledger, with no compensation if that post throws
  (`BillPaymentService.cs:26-29` is the pattern). A failed ledger post leaves a finalized payment
  with no entry; the operator's retry adds a *second* orphan plus one entry. Only `BillService` and
  `InvoiceService` dry-run first (`BillService.cs:33`, `InvoiceService.cs:66`) — and those two are
  not in this set.
- Browser back and resubmit, two tabs, two operators on the same task.

A survey for "POSTs that compose a ledger entry and carry no duplicate guard of any kind" also found
two the sub-project 1 note never mentioned: `POST /payroll-runs` and `POST /tax-remittances`. Payroll
is the largest single money-mover in the application.

## The governing standard

Unchanged from sub-project 1: **don't hand-hold more than necessary.** A retry key earns its place
only where a repeat moves money. This spec adds no business-duplicate detection, no cross-module
consistency checks, and nothing for vendors, customers, items or AR invoices.

## Scope

Thirteen endpoints — every POST that composes a ledger entry and has no duplicate guard today.

| Module | Endpoint | Request record |
|---|---|---|
| Payables | `POST /bill-payments` | `RecordBillPaymentRequest` |
| Payables | `POST /vendor-credit-applications` | `VendorCreditApplicationRequest` |
| Receivables | `POST /payments` | `RecordPaymentRequest` |
| Receivables | `POST /credit-applications` | `CreditApplicationRequest` |
| Receivables | `POST /write-offs` | `WriteOffRequest` |
| Receivables | `POST /credit-notes` | `CreditNoteRequest` |
| Receivables | `POST /refunds` | `RefundRequest` |
| Banking.Cash | `POST /cash-disbursements` | `RecordCashDisbursementRequest` |
| Banking.Cash | `POST /cash-deposits` | `RecordCashDepositRequest` |
| Payroll | `POST /payroll-runs` | `RecordPayrollRunRequest` |
| Payroll | `POST /tax-remittances` | `RecordTaxRemittanceRequest` |
| Banking.Reconciliation | `POST /reconciliations/{id}/adjustments` | `RecordAdjustmentRequest` |
| Inventory | `POST /movements` | `RecordMovementRequest` |

Write-offs and credit notes are in even though they move no cash: a repeat doubles bad-debt expense
or contra-revenue and drives an invoice balance negative. That is money moving in the books, which is
what the standard is about.

**Deliberately out, because each is already guarded on its own terms:**

- `POST /depreciation-runs` — period-scoped singleton (`FixedAssetsRunService.cs:24-25`).
- `POST /assets/{id}/dispose` — refuses when the asset is not Active or an active disposal exists
  (`FixedAssetsDisposalService.cs:26-31`).
- `POST /bills/{id}/enter`, `POST /invoices/{id}/issue` — addressed by the draft's own id, and the
  draft is consumed by the transition.
- `POST /entries` on the ledger — already has this, and is the mechanism everything below reuses.

**Out by the standard:** vendor, customer and item creation, AR invoice drafting, and bank-statement
import (that one is sub-project 3's business-duplicate lookup, a different question entirely).

## The mechanism

Every one of the thirteen already composes its entry with
`EntryIdentity.ForSource(sourceType, documentId)`. The ledger's atomic replay is therefore already
sitting underneath all of them, unreachable only because the document id is minted server-side by
`ScopedDocumentStore.CreateAsync:110` (`Guid.NewGuid()`). Making the caller's key *be* the document
id engages it.

**No new content comparison is written anywhere.** `EntryComparison.SameFinancialContent` decides,
and it is the only comparer that can: allocations exist solely in the entry lines and are invisible
to any document-body comparison, so a retry that changed them would replay as identical under a
per-module comparer.

### On the wire

Each of the thirteen request records gains a trailing `Guid? Id = null`. Optional, so every existing
caller, test and screen compiles and behaves exactly as today. Absent key means no protection, and
per the principle that is the caller's choice, not a defect.

One naming trap to be explicit about: `POST /reconciliations/{id}/adjustments` already has an `id`
route parameter, and it is the *reconciliation's*. The new body field is the adjustment's own id and
is unrelated to it. The handler signature must not shadow one with the other.

### In the document store

`IDocumentStore` gains one method beside `CreateAsync`:

```csharp
Task<bool> CreateWithIdAsync<T>(Guid clientId, string collection, Guid id, T body,
    IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);
```

A separate method rather than an optional parameter on `CreateAsync`, because the contract differs:
this one reports whether it wrote. It returns `false` when the id is taken, leaving the existing
document untouched — it never overwrites. `ScopedDocumentStore.CreateAsync` writes through
`PutAsync`, which is an upsert; an id-addressable upsert would let any caller with write capability
clobber a finalized payment by supplying its id. `CreateAsync` itself is untouched, so callers outside
these thirteen are unaffected.

**A bool rather than an exception**, for two reasons. The id being taken is the *expected* outcome of
a retry, not an error, and control flow through exceptions would be a smell. More concretely,
`ModuleDocumentException` lives in `Accounting101.Ledger.Api.Documents` while modules reference only
`Accounting101.Ledger.Contracts` — a module store could not catch it if we threw one.

Underneath, `MongoDocumentStore` gains `InsertIfAbsentAsync`, which uses `InsertOneAsync` and converts
the duplicate-key write error into `false`. `ModuleDocument.Id` is `[BsonId]`, so the unique index on
`_id` is the arbiter and the operation is genuinely atomic: two concurrent callers cannot both receive
`true`. This is the same `ServerErrorCategory.DuplicateKey` check the ledger already uses
(`LedgerEndpoints.cs:108`).

Each module store's record method takes the id, and **only the caller that actually created the
document finalizes it**:

```csharp
if (await documents.CreateWithIdAsync(clientId, BillPayments, id, body, Tags(body.VendorId), ct))
    await documents.FinalizeAsync(clientId, BillPayments, id, ct);
DocumentResult<BillPaymentBody>? result = await documents.GetAsync<BillPaymentBody>(clientId, BillPayments, id, ct);
return MapPayment(result!);
```

Gating the finalize on the create is what keeps two concurrent same-key callers from both finalizing:
`ScopedDocumentStore.FinalizeAsync` throws on a document that is no longer `Draft`, that throw is a
deliberately pinned invariant (`DocumentStoreEvidentiaryTests.cs:101`), and the exception type is
again one modules cannot catch. It also means **no sequence number is burned on a replay**, since the
replaying caller never reaches the finalize.

**Known limit, accepted rather than papered over.** If a process dies between the create and the
finalize, the document is left `Draft` with no sequence number, and a later retry — which gets `false`
from the create — will not finalize it either. Repairing that would mean finalizing a `Draft` we did
not create, which reintroduces exactly the double-finalize race the gate removes. The window is the
few milliseconds between two calls, and the consequence is a document missing its gapless number
rather than wrong money: the ledger entry still posts, because the service posts on both branches. We
take the race-free version and log the limit here instead of claiming a repair the code does not
perform.

### In each service

```csharp
Guid id = command.Id ?? Guid.NewGuid();
BillPayment? existing = command.Id is not null
    ? await payments.GetPaymentAsync(clientId, id, ct) : null;

if (existing is null)
{
    await ValidateAllocationsAsync(clientId, command.VendorId, command.Allocations, ct);
    existing = await payments.RecordPaymentAsync(clientId, id, body, ct);
}

PostEntryRequest entry = BillPosting.ComposeBillPayment(id, body, command.Allocations, posting);
await ledger.PostAsync(clientId, entry, ct);
```

Three properties fall out of that shape, and each is why it is written that way:

- **Validation is skipped on the replay branch, deliberately.** `ValidateAllocationsAsync` checks
  allocations against each bill's *remaining* balance — a balance the first call already reduced.
  Re-running it would reject the legitimate retry it exists to permit. This mirrors
  `LedgerEndpoints.PostEntry:76-93`, which short-circuits before freeze validation for the same
  reason.
- **The ledger post runs on both branches.** That is what repairs an orphan: the retry finds the
  document, skips the write, and completes the post that never landed.
- **The service never compares content.** It only decides whether this id is already known.

### The race

Two genuinely concurrent requests carrying the same key resolve without a lock. Both may miss the
`GetAsync` and both call `CreateWithIdAsync`; one wins and the loser treats the refusal as "already
exists". Even if both wrote, they would write byte-identical bodies under the same id. The money
movement is the ledger entry, and `_id` on the entries collection is a unique index — the arbiter
that matters is already atomic.

## Semantics

| Request | Response |
|---|---|
| No `Id` | Exactly today's behaviour — `201 Created`, no protection |
| `Id`, unseen | `201 Created`, document written under that id |
| `Id`, seen, same financial content | `200 OK` with the **stored** document |
| `Id`, seen, different financial content | `422` carrying the ledger's message |

201-vs-200 mirrors `LedgerEndpoints.PostEntry`, so a caller reading status can tell a fresh write from
a replay without a second round trip.

**"Financial content" is the ledger's definition** — effective date, type, source ref and type, and
the line array in order (`EntryComparison.cs`). A retry differing *only* in `Method`, `Memo` or
`Reference` is therefore a replay: it returns 200 and the stored document keeps the original wording.
That is the same stance the ledger already takes on `reference`/`memo`, and it is the right one — the
key identifies the operation and descriptive text does not redefine it. It also means a divergent
retry is caught on the things that move money, allocations included.

**A retry repairs; it does not merely refuse.** The replay branch re-runs the ledger post, so both of
these converge on one correct outcome instead of an orphan:

- document written, ledger post failed → the retry posts the entry, and there is one document and one
  entry at the end
- both succeeded, response lost → the retry changes nothing and returns 200

The retry does **not** repair a document abandoned in `Draft` — see the known limit above. No sequence
number is burned on a replay, since the replaying caller never reaches the finalize.

**A replayed key on a voided document returns the voided document and does not resurrect it.** The
operation happened and was reversed; re-sending the key must be invisible, which means reporting what
is there. Booking it again is a new operation and needs a new key. The ledger's early-replay path
claims to be "approval-safe (returns the entry in its current lifecycle state)"
(`LedgerEndpoints.cs:78-79`); the tests below pin that rather than taking the comment's word for it.

**Nothing new is disclosed.** Keys are meaningful only inside one client (documents live in per-client
databases), the caller needs the module's write capability to reach the endpoint at all, and a replay
returns a document that caller could already `GET` by id.

**No new error-mapping code.** The 422 arrives because `ModuleLedgerClient` throws
`LedgerClientException` and `UseModuleLedgerExceptionRelay` translates it to ProblemDetails carrying
the engine's status. That is one app-wide middleware registered once
(`Accounting101.Host/Program.cs:75`), not per-module wiring — and every module's e2e fixture boots
that same `Program` through `WebApplicationFactory<Program>`, so it is in the test path too.

## The screens

Ten editors, reaching the thirteen endpoints through five Angular services (`payables.service.ts`,
`receivables.service.ts`, `banking.service.ts` — cash vouchers *and* reconciliation adjustments —
`payroll.service.ts`, `inventory.service.ts`). Each service's request interface gains `id?: string`.

| Editor | Endpoints |
|---|---|
| `features/payables/bill-payment-editor.ts` | bill payment |
| `features/payables/vendor-credit-apply-editor.ts` | vendor credit application |
| `features/receivables/payment-editor.ts` | AR payment |
| `features/receivables/adjustment-editor.ts` | credit note, write-off, credit application |
| `features/receivables/refund-editor.ts` | refund |
| `features/banking/cash-voucher-editor.ts` | disbursement, deposit |
| `features/banking/adjustments-panel.ts` | reconciliation adjustment |
| `features/payroll/run-editor.ts` | payroll run |
| `features/payroll/remittance-editor.ts` | tax remittance |
| `features/inventory/movement-editor.ts` | stock movement |

Each mints `crypto.randomUUID()` on construction and sends it in the request body.

**The key is re-minted after each successful submit, not only on init.** Most of these navigate away
on success so the component dies and the key with it — but `adjustments-panel.ts` stays put and can
record several adjustments in a row, and reusing the key would make the second one replay the first
and silently return the first adjustment. One uniform rule is safe everywhere and removes the need to
reason about which editors navigate. The key deliberately survives an *error*, which is the point.

`adjustment-editor.ts` posts to one of three endpoints depending on mode, and re-mints the key on
every mode switch, not just on init and on success: the ledger's `sourceRef` lookup is not scoped
by source type, so a Guid shared across a mode switch would produce two entries with the same
`SourceRef` and the void/relief paths (`FirstOrDefault` by `SourceRef`) could resolve to the wrong
one.

`crypto.randomUUID()` requires a secure context — https in production, localhost in dev. Both hold.

### The one new error an operator can hit

After a failed submit the operator may change the amount and retry. If the first attempt actually
landed, that now returns 422 — correct, and the mechanism doing its job. But the relayed message is
the ledger's: *"An entry with this id already exists with different content."* That is engine jargon
on an AP screen.

One shared helper recognises that detail and substitutes screen-appropriate wording — that the
submission was already recorded, differs from what was just sent, and the list should be checked
before retrying — used by all ten editors. It belongs in `core/api/problem-details.ts` beside
`extractProblem`, which every one of these editors already calls to render an error. It string-matches
a message from our own repo, which is brittle unless it is a contract, so a backend test asserts that
exact detail is what a divergent replay returns. That makes the coupling something a build catches
rather than something an operator discovers.

## Testing

**Per endpoint, all thirteen** — the three-case matrix, not a representative sample:

- absent key → 201, exactly today's behaviour
- replayed key → 200, the same document id, and no second document
- divergent content under the same key → 422

These are thirteen separately-wired paths and a mis-threaded `Id` is exactly the kind of omission
that ships quietly. Sub-project 1's lesson was that the missing assertion was the one nobody thought
worth writing.

**Once each, on the payables path** — the behaviours that are not per-endpoint:

- orphan repair: a fake ledger throws on the first post; the retry completes it, and there is one
  document and one entry at the end
- replay of a voided document returns the voided document and creates nothing
- two concurrent identical posts yield one document and one entry
- no sequence number is burned on a replay

**Unit tests on `CreateWithIdAsync`**: writes under the supplied id and returns `true`; returns
`false` when the id is taken; never overwrites the existing body; and the replay path leaves the
collection's gapless counter untouched.

**One contract test** asserting the exact 422 detail string the shared Angular helper matches on.

**Angular, per editor**: the request carries an `id`, and the `id` changes after a successful submit.
Plus one spec for the shared error helper.

## Global constraints

- .NET 10; Angular 22; build with 0 warnings; TDD; commit per slice; stage explicit file lists.
- `UI/Angular/src/app/core/api/environment.ts` carries a long-lived local dev-config edit that must
  stay uncommitted.
- No new npm or NuGet dependency.
- No module gains a reference to another module. The only shared change is
  `IDocumentStore.CreateWithIdAsync` in `Accounting101.Ledger.Contracts`, which every module already
  references.
- The `Id` stays optional at every layer. No existing caller changes behaviour.

## Shape of the work

Six backend modules, one engine contract change, ten components. The natural decomposition is one
slice per module — engine contract first, since everything depends on it, then Payables, Receivables,
Banking.Cash, Payroll, Banking.Reconciliation, Inventory, then the Angular pass — each slice
independently testable and reviewable. The implementation plan formalises that.
