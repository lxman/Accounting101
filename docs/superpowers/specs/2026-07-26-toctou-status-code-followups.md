# TOCTOU status codes — follow-ups left open at merge

**Date:** 2026-07-26
**Branch:** `feat/toctou-status-codes`
**Spec:** [2026-07-26-toctou-status-code-design.md](2026-07-26-toctou-status-code-design.md)

All thirteen retry-key endpoints now trust the store's atomic-create bool, each with a race test that
was watched failing first. These are items surfaced during review that were deliberately not fixed
here, recorded so they are not rediscovered from scratch.

## 1. Two plan claims that turned out to be false — corrected in code, noted here

Both were caught by implementers or reviewers, not by the plan author. Recording them because the same
mistakes are easy to repeat.

- **"Both consumers already filter `!Voided` themselves, so this is dead code today."** False for one
  consumer in each module. `BillPaymentService.GetCreditApplicationsWithAllocationsByVendorAsync` and
  `PaymentService.GetPaymentsWithAllocationsByCustomerAsync` both pass `Voided` straight into their
  DTOs and document *"Voided … are included (greyed in the UI)"*. The `includeVoided: true` change is
  therefore a real, user-visible change to the Payables Credits list and the Receivables Payments
  list — and the correct one, since those two stores were silently violating a contract their
  consumers already documented. Now proven by engine-backed tests that actually void.
- **"`FakeLedgerClient` dedupes unconditionally."** False in all six modules — none had any
  `SourceRef` dedupe at all, so the work was to *add* replay logic, not guard it.

## 2. Call-recording collections in the fakes are written under lock but read unguarded

Cash and Payroll's `SingularCalls`/`BatchCalls`, and Inventory's `GetSubledgerCallCount` /
`GetEntriesBySourceRefsCallCount`, are now written inside `_postLock` but their public getters and
fields are still read without it.

Harmless today: every read happens after `Task.WhenAll` completes, so there is a memory barrier and no
live enumeration. But a future test that enumerates `SingularCalls` mid-race would hit exactly the
`Collection was modified` class of bug the final fix wave closed for `_entries`/`_linesById`/`_posted`.
Worth extending the same guard if anyone writes such a test.

## 3. `SeedEntry` bypasses the new dedupe

`SeedEntry` (Payables, Receivables) writes directly into `_entries` without registering in
`_bySourceRef`, so a seeded entry is invisible to the divergence guard added in Task 9.

Harmless today — seeds use SourceTypes the code under test never posts (`"Bill"`, `"Invoice"`,
`"CustomerCredit"`) — but it is a fidelity seam that will surprise whoever first seeds an entry the
service also posts.

## 4. FixedAssets' ledger fake was left on the old behaviour

`Modules/FixedAssets/Accounting101.FixedAssets.Tests/Fakes.cs` is the seventh `ILedgerClient` fake in
the repo and the only one still doing an unconditional add. FixedAssets has no retry-key endpoint, so
nothing is broken — but the same fake now behaves differently depending on which module you are
testing in. Either bring it along or note the exclusion where the six are documented.

## 5. `FakePostingLedger.StubEntry()` can NRE

It dereferences `Posted!` and will throw if `ReverseAsync`/`VoidAsync` is called before any post.
Pre-existing, untouched by this branch.

## 6. Plan and spec docs still embed a superseded recipe

`docs/superpowers/plans/2026-07-26-toctou-status-code.md` (five sites) and the design spec (one) still
carry the *"revert to `replayed = known is not null;`"* re-verification recipe in their planned-code
snippets. The shipped race-test comments were corrected to say **delete** the line — which is what
actually produces the documented "sees two and fails" symptom — but the planning records were left as
the historical artefacts they are. Only matters if plan docs are ever treated as live guidance.

## 7. One comment's line anchors are wrong

`Modules/Inventory/Accounting101.Inventory.Tests/Fakes.cs` cites `ItemValuationService.cs:52,67` as the
racing ledger reads. Those lines are movement-store reads; the actual ledger reads are `:46`
(`GetSubledgerAsync`) and `:54` (`ResolveBySourceAsync`). The comment's substance is right, the
anchors are not.

## Still out of scope, per the spec

- ~~**Inventory's weighted-average fold race.**~~ **CLOSED 2026-07-27** —
  [2026-07-27-inventory-fold-race-design.md](2026-07-27-inventory-fold-race-design.md). Investigation found
  a second, unrecorded and more serious face: two concurrent movements on the same item with DIFFERENT ids
  both passed the block-negative guard, driving on-hand negative permanently. Both faces are closed by a
  per-item write lock; single-instance only, by ratified decision.
- **Retry-keys follow-up #3** — validation running before the atomic create. Bites only on genuine
  concurrency, not on the sequential failed-but-succeeded retry the feature exists for, and
  self-heals.
- **Retry-keys follow-up #4** — the ledger's `sourceRef` query not being scoped by source type.
  Engine-level, and no longer UI-reachable.
