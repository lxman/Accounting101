# TOCTOU status codes — a deterministic race test, then twelve conversions

**Date:** 2026-07-26
**Repo:** `Accounting101`
**Follows:** [2026-07-25-retry-keys-followups.md](2026-07-25-retry-keys-followups.md) items 1 and 2, and the first two Test gaps

## Why

Every retry-key endpoint decides "created or replayed?" from a pre-check:

```csharp
BillPayment? known = command.Id is not null ? await payments.GetPaymentAsync(clientId, id, ct) : null;
bool replayed = known is not null;
```

That read is time-of-check-to-time-of-use. Two callers racing the same key can both observe
`known == null` and both report `201 Created`. The engine's insert-only `CreateWithIdAsync` is the
real arbiter — atomic on the Mongo `_id`, so exactly one caller gets `true` — and the retry-keys
branch fixed exactly one endpoint by having the store return that bool and having the service trust
it over its own pre-check. Twelve endpoints still carry the un-fixed shape.

**Be clear about the stakes.** For eleven of the twelve this is a wrong status code and nothing more:
one document (Mongo `_id` arbitrates), one entry (deterministic UUIDv5 plus the ledger's own
duplicate-key catch), correct money. Inventory is the exception and is discussed below. This is
durability-of-contract work, not a live money defect.

**The reason to do it now is the test, not the conversions.** The one endpoint that *is* fixed has no
regression test. `Two_concurrent_posts_with_the_same_key_yield_one_document_and_one_entry` fires two
real HTTP posts under `Task.WhenAll`, but if they serialize — which they usually do — the second
one's pre-check finds the document and returns 200, so the assertions pass *even with the fix
reverted*. The only real production bug that branch fixed is therefore unprotected, and every
conversion below would land equally unprotected without a test that can tell the two shapes apart.

## The conversion

Mechanically small, because every store already computes the bool and throws it away:

```csharp
// DocumentBillPaymentStore.RecordCreditApplicationAsync — the un-fixed shape
if (await documents.CreateWithIdAsync(clientId, VendorCreditApplications, id, body, Tags(body.VendorId), ct))
    await documents.FinalizeAsync(clientId, VendorCreditApplications, id, ct);
return MapCredit(result!);                       // bool discarded

// RecordPaymentAsync — the fixed shape, from the retry-keys branch
bool created = await documents.CreateWithIdAsync(clientId, BillPayments, id, body, Tags(body.VendorId), ct);
if (created)
    await documents.FinalizeAsync(clientId, BillPayments, id, ct);
return (MapPayment(result!), created);
```

Four edits per endpoint, plus a test:

1. Port interface: `Task<T>` → `Task<(T Doc, bool Created)>`.
2. Store: capture the bool it already computes; return the tuple. The loser still gets back the
   winner's persisted document — the re-read after the create is unchanged.
3. Service: `known = result.Doc; replayed = !result.Created;`.
4. In-memory fake: last-wins `dict[key] = value` → `TryAdd` plus return-stored. Without this the fake
   cannot report an honest `Created` and the race test is meaningless.

## Architectural constraint

**No module's replay or create logic moves into the engine or any shared shipped assembly.** The
uniform shape is a convention applied in place, twelve times — no base class, no shared generic,
nothing new in `Accounting101.Ledger.Api`. No module gains a reference to another module.

This is not tidiness. Engine code ships to every deployment unconditionally; module code is gated by
`installed ∩ entitled` (see
[2026-07-26-module-entitlement-alignment-design.md](2026-07-26-module-entitlement-alignment-design.md)).
A generic "replay helper" hoisted into the engine would put paid-module behaviour into the ungated
tier, where it could be discovered and wired up without entitlement. Twelve near-identical private
methods are the correct outcome here, and a reviewer should not read them as missed deduplication.

The only shared artifact this spec introduces is test-only and never shipped.

## Part 1 — the deterministic race test

The TOCTOU window lives entirely inside the service, between its probe and its store call. Forcing
two callers into that window needs a rendezvous, which two real HTTP posts cannot provide. The test
is therefore service-level, against the existing in-memory fakes: no Mongo, no HTTP, milliseconds.

### `Accounting101.TestSupport/RaceRendezvous.cs`

`Accounting101.TestSupport` is referenced by all nine module test projects and is never referenced by
the Host, so it is the right home and carries no shipping risk.

```csharp
/// <summary>Releases every participant once the Nth arrives, so concurrent callers can be forced
/// into a specific interleaving. Times out rather than hanging the suite when a test declares more
/// participants than actually arrive.</summary>
public sealed class RaceRendezvous(int participants)
{
    public Task ArriveAndWaitAsync(CancellationToken ct = default);
}
```

The timeout is load-bearing: a mis-wired test should fail in seconds with a clear message, not block
the suite until the CI runner gives up.

### Per-module rendezvous decorator

For Payables, a test-only `RendezvousBillPaymentStore : IBillPaymentStore` wrapping
`InMemoryBillPaymentStore`. It blocks in the **probe** — `GetPaymentAsync` — until both racers have
arrived, then delegates every method untouched. Both callers are thereby forced to observe
`known == null`, which is precisely the state the HTTP test cannot produce.

Each module gets its own decorator in its own test project. Yes, they are near-identical; the
constraint above applies to test code too, and a shared decorator would need a shared interface that
does not and should not exist.

### The test

Two concurrent `RecordPaymentAsync` calls sharing one `command.Id`. Assert **exactly one** result has
`Replayed == false`.

That assertion is the whole point, and the test says so in its own comment: revert
`replayed = !result.Created` to `replayed = known is not null` and both racers claim a create, so the
assertion sees two and fails. A concurrency test that passes against the broken code is worse than no
test, because it advertises protection it does not provide — which is exactly the trap the existing
one fell into.

Two details that would otherwise make it fail for an unrelated reason:

- **Allocation amounts must be non-binding** against the bill balance — a 1000 bill and two 100
  payments. Otherwise follow-up #3 (validation runs after the probe and before the create, folding
  pending-inclusive) hands the second racer a spurious 422 and the test fails for a defect this spec
  is not addressing.
- **Both racers post to the ledger.** That is by design — posting on both branches is what repairs an
  orphaned entry after a failed post — so the assertions are about `Replayed` and the document count,
  not about post counts.

### The existing HTTP test stays

`Two_concurrent_posts_with_the_same_key_yield_one_document_and_one_entry` proves one-document and
one-non-reversal-entry end to end through real HTTP and real Mongo, which the service-level test
cannot. It keeps its assertions and gains a comment recording that it does not force the interleaving
and naming the test that does. Deleting it would trade real end-to-end coverage for nothing.

## Part 2 — the twelve conversions

| Module | Endpoints | Stores |
|---|---|---|
| Receivables | 5 — payment, credit application, write-off, credit note, refund | `DocumentPaymentStore` |
| Payables | 1 — credit application | `DocumentBillPaymentStore` |
| Cash | 2 — deposit, disbursement | `DocumentCashDepositStore`, `DocumentCashDisbursementStore` |
| Payroll | 2 — run, remittance | `DocumentPayrollRunStore`, `DocumentTaxRemittanceStore` |
| Reconciliation | 1 — bank adjustment | `DocumentBankAdjustmentStore` |
| Inventory | 1 — movement | `DocumentStockMovementStore` |

Each endpoint follows the four edits above and gets its own race test, TDD-ordered: write the test,
watch it fail against the un-converted service, convert, watch it pass. Without that red phase a
conversion is just a signature change with a green suite proving nothing.

### Inventory is converted, and only half fixed

The conversion applies, but `InventoryMovementService` needs a comment recording exactly what it does
and does not buy:

- **Receipt and overage Adjustment** compute `extendedCost` as `Round(qty × unitCost)` — pure in the
  request, no fold. The conversion fixes them completely.
- **Issue and shrinkage Adjustment** derive cost from a pending-inclusive weighted-average fold that
  the race winner has already moved. A loser recomputes against the moved fold and can get a ledger
  422, trip block-negative (409), or trip the `extendedCost <= 0` guard — all for a movement that
  actually landed. `Created` cannot help: **the cost is computed before the store call.** The loser's
  next retry succeeds, because its probe now finds the movement and freezes the cost from the stored
  document, so this is a transient spurious error that self-heals, not a money defect.

Converting Inventory anyway is deliberate. It leaves the fold race as the *only* remaining problem
there, which makes its future spec sharper — "the weighted-average fold races" rather than "the fold
races and the status code is also wrong."

## Folded in

Two adjacent items from the same follow-ups doc, cheap enough that a separate branch would cost more
than the fix:

- **`includeVoided` asymmetry.** `GetCreditApplicationsByVendorAsync` (Payables, directly below the
  already-fixed sibling and its explanatory comment) and `GetPaymentsByCustomerAsync` (Receivables,
  the only list method in that file without it). Voided documents vanish from the list instead of
  appearing greyed. Both consumers already filter `!Voided` themselves, so this is dead code today —
  two lines and two assertions to stop it becoming live later.
- **`FakeLedgerClient` dedupes unconditionally** while production 422s on divergent content, so any
  future divergent-retry test written against the fake would pass vacuously. Worth fixing before
  someone writes that test and trusts it.

**Risk, stated rather than discovered:** existing tests may lean on the fake's vacuous dedupe. If
tightening it turns any test red, that is reported as a finding — not papered over by loosening the
fake again, and not fixed by editing the assertion until it passes.

## Testing

- One race test per endpoint, service-level, against fakes: one in Part 1 for the already-fixed
  `BillPaymentService.RecordPaymentAsync`, then one per conversion in Part 2. Thirteen by the end,
  each running in milliseconds.
- Every race test must be a genuine discriminator. The implementation is not done until the author
  has watched it fail against the un-converted service.
- The existing HTTP concurrency test stays green and gains its clarifying comment.
- Two assertions for the `includeVoided` fixes.
- Full solution green: `dotnet test Accounting101.Local.slnx` (1571 tests including the private
  overlay). The committed `Accounting101.slnx` alone is 1494.

## Out of scope

- **Inventory's weighted-average fold race** — its own spec, deliberately narrowed by this one.
- **Follow-up #3**, validation running before the atomic create. It bites only on genuine concurrency,
  not on the sequential failed-but-succeeded retry the feature exists for, and it self-heals.
- **Follow-up #4**, the ledger's `sourceRef` query not being scoped by source type. Engine-level, and
  no longer UI-reachable.
- **Relocating Inventory to a private overlay.** Its source is tracked in the public repo today; its
  paywall is runtime entitlement, not code secrecy. Changing that is a distribution question, not a
  code-shape one.

## Global constraints

- .NET 10; build with 0 new warnings; TDD; commit per slice; stage explicit file lists.
- `UI/Angular/src/app/core/api/environment.ts` carries a long-lived local dev-config edit that must
  stay uncommitted.
- No new npm or NuGet dependency.
- No module gains a reference to another module; no module logic moves into the engine.
- Pre-existing and out of scope: 4 NU1902/NU1903 advisories from `Accounting101.TestSupport`
  transitive deps; 13 NG04002 Angular console errors; one pre-existing Angular failure in
  `auth.interceptor.spec.ts`.
