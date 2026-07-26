# Retry keys — follow-ups left open at merge

**Date:** 2026-07-26
**Branch:** `feat/retry-keys`
**Spec:** `2026-07-25-retry-keys-design.md`

Everything below was found during implementation or by the final whole-branch review, triaged as
non-blocking, and deliberately shipped. Recorded here because the working ledger is deleted at merge
and none of this lives in git history otherwise.

## Real defects, tracked

### 1. The TOCTOU status-code race is fixed on one endpoint of thirteen

`BillPaymentService.RecordPaymentAsync` decides `replayed` from a pre-check `GetAsync`, which is
time-of-check-to-time-of-use. Two same-key racers can both observe `known == null` and both report
`201 Created`. Task 10 fixed it there by having the store return `(BillPayment, bool Created)` and
having the service trust that over its own pre-check; the other twelve endpoints still carry the
un-fixed shape.

**Consequence for eleven of them: a wrong status code only.** One document (Mongo `_id` is the
arbiter), one entry (deterministic UUIDv5 plus the ledger's own duplicate-key catch), correct money.

**Inventory is worse and needs its own design, not a mechanical copy.**
`InventoryMovementService.RecordAsync` recomputes `extendedCost` from a pending-inclusive fold that
the race winner has already moved. In the window between a loser's probe and its fold, that fold
already contains the winner's movement, so an Issue or shrinkage Adjustment computes a different cost
and gets a ledger 422, or trips block-negative (409), or trips the `extendedCost <= 0` guard (422) —
all for a movement that actually landed. Returning `Created` does not help: the cost is computed
before the store call. Mitigating factor, verified by the final review: the loser's *next* retry
succeeds, because its probe now finds the movement, freezes the cost from the stored document and
replays. So this is a transient spurious error, not a money defect, and it self-heals.

### 2. `includeVoided` asymmetry in two list methods

Task 10 fixed `GetPaymentsByVendorAsync` (voided payments were silently vanishing from the vendor
list rather than appearing greyed). Two siblings still carry the same pre-existing bug:

- `Modules/Payables/Accounting101.Payables/DocumentBillPaymentStore.cs` — `GetCreditApplicationsByVendorAsync`,
  directly below the now-fixed method and its explanatory comment.
- `Modules/Receivables/Accounting101.Receivables/DocumentPaymentStore.cs` — `GetPaymentsByCustomerAsync`,
  the only list method in that file without it (credit applications, write-offs, credit notes and
  refunds all have it).

Both consumers already filter `!Voided` themselves, so the filters are dead code today. Two-line
change plus two assertions.

### 3. Validation runs before the atomic create

`ValidateAllocationsAsync` runs after the probe and before the create, and folds pending-inclusive.
A race loser can therefore still get 422 "exceeds its open balance". Applies to every
allocation/credit-balance endpoint.

**Scoping verified by the final review: this bites only on genuine concurrency, not on the sequential
failed-but-succeeded retry the feature exists for.** In that case the probe finds the document,
validation is skipped entirely, and the ledger post repairs the orphan. The headline promise holds.
Like the above, a loser's spurious 422 self-heals on the next retry.

### 4. The ledger's `sourceRef` query is not scoped by source type

This is the root cause of the one bug that blocked merge (fixed in `0efbf9a` at the UI layer).
`GetEntriesBySourceRefAsync` filters on `sourceRef` alone, so two documents in different collections
sharing a Guid produce two entries with the same `SourceRef`, and every void/relief path resolves
that with `FirstOrDefault(Active, non-reversal)` — picking arbitrarily.

The UI can no longer reach this (`adjustment-editor` re-mints on mode switch), but **any API client
that deliberately reuses one key across two endpoints still can.** The durable fix is type-scoping
the query in the engine. Well outside this plan's scope.

## Accepted narrowing, documented and shipped

**Inventory: a same-key retry differing only in quantity on an Issue or shrinkage Adjustment replays
(200) instead of returning 422.** Extended cost on those types is derived from a weighted-average
fold that already contains the movement on replay, so recomputing it yields a spurious 422 or 409 on
a legitimate retry — the freeze is correct. Receipt and overage Adjustment *do* recompute (their cost
is `Round(qty × unitCost)`, request-pure), so divergence detection is intact for the dominant case.

Closing the remainder would require putting the quantity on the ledger entry — a contract change
outside this plan. Documented at `InventoryRequests.cs`, in the service XML doc, in the plan, and
pinned by a test.

Cheap partial mitigation worth doing: have `movement-editor.ts` re-mint the key when the operator
edits quantity or unit cost after a failed submit, which closes the only UI-reachable route to it.

## Test gaps

- **The concurrency test does not force its interleaving.** `Task.WhenAll` on two real HTTP posts, but
  if they serialize the second one's pre-check finds the document and returns 200 anyway — so the
  assertions pass even with the TOCTOU fix reverted. **The only real production bug this branch fixed
  therefore has no regression test.** Highest-value item on this list.
- **`FakeLedgerClient` dedupes unconditionally** while production 422s on divergent content. A future
  divergent-retry unit test written against the fake would pass vacuously. Cheap to fix, and worth
  doing before anyone writes that test.
- **No test pins the zero-cost guard** on a replayed Receipt with `unitCost: 0`
  (`InventoryMovementService.cs`). A guarded branch with no coverage.

## Deliberately dropped

Raised during review and judged not worth acting on: a test for `CreateWithIdAsync` on a
declared-but-non-evidentiary collection (the fixture declares no such collection, and
`RequireEvidentiary` is unchanged and covered via `CreateAsync`); the `known!` null-forgiving in
`BillPaymentService` (safe by construction); missing XML-doc on the store-layer `id` parameters
(cosmetic); duplicated setup helpers between two Receivables test classes.
