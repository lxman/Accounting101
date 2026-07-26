# Bank statement duplicate lookup — design

**Date:** 2026-07-26
**Sub-project:** 3 of 3 in the duplicate-handling arc
**Policy:** [`docs/design-principles.md` §3](../../design-principles.md) — "Duplicate submissions are two
different questions"
**Predecessors:** [sub-project 1](2026-07-25-duplicate-handling-policy-design.md) (Payables bill lookup),
[sub-project 2](2026-07-25-retry-keys-design.md) (retry keys on money-moving POSTs)

## What this closes

Sub-project 1's spec enumerated this one and left it for its own slice:

> **Bank statement duplicate lookup** (sub-project 3) — `GET /bank-statements/duplicates`, matching on
> cash account + statement date, called from the import screen. Deliberately not line-level
> `ExternalRef` reconciliation: partial overlaps and incremental statements make that a feature, not a
> rule.

That direction is ratified and this design does not revisit it. What it settles is everything the one
paragraph left open: which screens ask, when they ask, and what the lookup promises about completeness.

## Why a statement duplicate earns a check

§3's standard is that a duplicate check earns its place **only where duplication moves money**, and a
bank statement is evidentiary — recording one posts nothing to the ledger. (The module does post, but
only for reconciliation adjustments, through its `HttpLedgerClient`.)

The check earns its place one step downstream, and this was verified rather than assumed:
`ReconciliationService.ClearAsync` validates only that an entry is eligible for **this** reconciliation
— posted, on this cash account, dated on or before the statement date — and `ClearedEntryIds` lives on
the individual `Reconciliation` document. **Nothing checks whether another reconciliation already
cleared the same entry.** So two statements for the same account and date support two reconciliations
that both clear the same ledger entries, and a cleared entry is what a reconciliation's outstanding
items, its adjustments and its completed balance are computed from. The duplicate does not move money;
the reconciliation built on it does.

This is still a **business duplicate**, never a machine retry. It is advisory, it never blocks, and
nothing in the module consults it — re-issued and corrected statements are legitimate and only a human
can tell.

## Scope

**In:** the service method, the endpoint, one Angular client method, and the warning on both screens
that create a statement.

**Out:** line-level `ExternalRef` matching (ratified out above); anything that blocks recording; any
change to how statements are stored, numbered or reconciled.

## The lookup

### Where it lives

`ReconciliationService`, beside `ListStatementsAsync`. `IBankStatementStore` needs no change:
`GetByAccountAsync` already reads every statement for an account with no paging and no cap, so the
lookup is **complete by construction**. This is the property that matters — sub-project 1 exists
because a caller assembled a scan over a list endpoint, and a bounded scan reporting "none found" is a
lie of omission.

```csharp
public async Task<IReadOnlyList<BankStatement>> FindDuplicateStatementsAsync(
    Guid clientId, Guid cashAccountId, DateOnly statementDate, CancellationToken ct = default)
```

Returns statements on that cash account bearing that exact statement date, where
`Status != BankStatementStatus.Void`, ordered by `Number` descending.

**The Void filter is belt-and-braces, not the load-bearing exclusion.** This was checked rather than
assumed, and the first draft of this spec had it wrong. Two facts already prevent a voided statement
reaching the lookup:

- `DocumentBankStatementStore.GetByAccountAsync` calls `QueryAsync` without `includeVoided`, which
  defaults to `false`, and `MongoDocumentStore.MatchFilter` then adds `Nin(State, HiddenStates)` —
  Voided, Superseded and Inactive.
- **Nothing voids a bank statement in the first place.** `IBankStatementStore` exposes no void method,
  no module code path calls one, and the collection registers as `manifest.Evidentiary("bank-statements")`
  — recorded, finalized, immutable. Cash's deposits and disbursements have void paths; statements do not.

The filter stays, because it keeps this read correct if either fact changes, and because `Map` already
models the state. But the spec must not pretend it is doing the work, and — see Testing — it must not
be covered by a test that cannot fail.

**Ordering is by `Number`, not by date.** Every hit shares the same statement date by construction, so
date ordering would be meaningless. `Number` descending puts the most recently recorded first, matching
`ListStatements`' default order.

### Endpoint

```
GET /clients/{clientId}/bank-statements/duplicates?cashAccountId={guid}&statementDate={date}
```

Registered in `ReconciliationEndpoints.MapReconciliationEndpoints` beside the other statement reads.
No route conflict with `/bank-statements/{id:guid}` — that route is guid-constrained, so `duplicates`
cannot match it.

Both parameters are required. A missing, empty or unparseable either way is a 400 stating which,
matching `ListStatements`' existing treatment of a missing `cashAccountId`.

Returns a bare array of the domain `BankStatement`, per this class's convention (the same choice the
bill lookup made).

**No new authorization gate.** Like every other read here it reaches documents through
`IBankStatementStore`, whose `ScopedDocumentStore` is the chokepoint where `ModuleAccess.AuthorizeAsync`
already enforces entitlement and capability. The route group is already `.RequireAuthorization()`.

## The callers

Both screens that create a bank statement warn, not just the import screen the scope note named. They
carry the identical cash-account-plus-statement-date pair and the identical failure mode; wiring only
one leaves a hand-typed duplicate undetected, which is the same lie of omission this arc exists to
close.

One client method, mirroring `PayablesService.billDuplicates`:

```ts
statementDuplicates(cashAccountId: string, statementDate: string): Observable<BankStatement[]>
```

### `StatementImport` — `/cash/statements/import`

A file may carry several statements, so `previews` is an array and each entry has its own editable
statement date; the cash account is chosen once at the upload stage and shared.

`EditablePreview` gains `duplicates: BankStatement[]`. Each preview is scanned when the preview stage
opens and re-scanned when **that preview's** statement date changes. Scanning at preview time rather
than at confirm time is deliberate: the warning exists to inform the review, not to interrupt the
commitment.

**Both defects the e-invoicing import review caught are guarded up front**, because this screen has the
same shape that produced them:

- A `generation` counter incremented **only** in `reset()`; every state-writing subscription captures
  it and returns early on mismatch.
- A per-scan key of the date being scanned, so a response for an abandoned date cannot land on a
  preview that has moved on. The generation guard and the key guard protect different things and both
  are needed.
- Duplicates clear before each scan **and** in the error path — a stale hit surviving a failed re-scan
  is how the e-invoicing screen left a previous vendor's duplicate on screen with a live link.

The warning never disables **Record statement**.

### `StatementEditor` — `/cash/statements/new`

Single statement, both fields already signals. A `duplicates` signal, scanned once both cash account
and statement date are set and re-scanned when either changes, with the same guards minus the preview
index. Never blocks **Record statement**.

### Defaulted dates are scanned like any other

Both screens default an undetected statement date to today —
`statement-import.ts` `s.statementDate ?? new Date().toISOString().slice(0, 10)` and
`statement-editor.ts` `signal(new Date().toISOString().slice(0, 10))`. A defaulted date is scanned
identically to a parser-detected one. Two unrelated same-day imports will warn about each other; that
is accepted. The warning never blocks and always names the statements it found, so a false positive
costs one glance at a link — cheaper than the state needed to track detected-ness through preview
edits, and cheaper than staying silent in the common case where the statement really is today's.

### Rendering

Both screens render the same block: each hit as number, statement date and closing balance, linking to
`/cash/statements/{id}`.

## Error handling

A failed lookup is not a failed import. The scan's error path clears the duplicates for that preview
and leaves the screen otherwise untouched — no message, no disabled button. A warning that could not be
computed must not present as "no duplicates found", but neither should it obstruct a recording the
operator is entitled to make.

## Testing

### Service — mirroring `BillDuplicateTests`

Finds a statement on the same account and date; excludes other accounts; excludes other dates; empty
when there are none; `Number`-descending ordering.

**The Void case is tested here and only here**, against `InMemoryBankStatementStore` — which must gain
a seeding method that can place a statement in a chosen status, because its `RecordAsync` cannot
produce one. That is the only way to write a Void test that *can fail*: delete the service's filter and
the fake hands the voided statement back. Testing it through the real store would be a test that passes
whether or not the filter exists, which is worth nothing.

**Plus the test that carries this arc's process lesson:** a duplicate on an account with **more than
200 statements is still found**. That is the statement-side analogue of sub-project 1's drafts test —
it pins that this read is complete where a caller-assembled scan over the paged list (`limit` clamped
to 200) would silently miss. It sits at the service level against a fake store; driving 201 statements
through the real document store in an endpoint test is slow for what it proves.

### Endpoint — mirroring `BillDuplicateEndpointTests`

400 for a missing `cashAccountId`; 400 for a missing `statementDate`; 400 for an unparseable
`statementDate`; 200 with an empty array when nothing matches.

**Plus the test the endpoint actually exists for:** a statement recorded over HTTP is then found by the
lookup over HTTP. This is the analogue of sub-project 1's drafts test — nothing else in the file records
a statement, so a transposed argument or a mis-bound query key would otherwise leave every test green
while every import silently reported "no duplicates found". That is the defect class this arc keeps
producing.

No Void test at this level, for the reason given above.

`statementDate` binds as `string?` and is parsed in the handler rather than bound as `DateOnly?`. A
malformed value bound directly would produce the framework's own empty-bodied 400, and these tests
assert a `problem+json` body like every other 400 in the module.

### Angular — both screens

The warning renders on a hit and is absent on a miss; it never disables the submit button; the
stale-response guard actually holds (a response for a superseded date does not land); a failed scan
leaves no stale hit on screen.

## Deliberately not built

- **Re-scanning the other previews after one is recorded.** A file carrying two statements with the
  same date is odd enough that the fan-out is not worth it. Recorded here so it is not rediscovered as
  an oversight.
- **Line-level `ExternalRef` reconciliation.** Ratified out by sub-project 1: partial overlaps and
  incremental statements make that a feature, not a rule.
- **Anything §3's standard excludes** — duplicate vendors, customers, AR invoices, and cross-module
  consistency for its own sake.
