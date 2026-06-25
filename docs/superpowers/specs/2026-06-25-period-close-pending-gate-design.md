# Period-close gate: refuse to close while in-period work is unapproved — Design

**Date:** 2026-06-25
**Status:** Spec for review
**Context:** Surfaced by the second dog-food run (2026-06-25). In month 6, June closed while three
Controller entries dated 2024-06-30 sat `PendingApproval`. The engine correctly refuses to approve
*or* void an entry dated in a closed period, so those three became **permanently stranded** — never
booked, unfixable in-engine — and a July reversal of one of them (`ACCRREV-05`) then reversed an entry
that was never posted, driving Accrued Expenses negative. No engine defect; the close operation simply
let legitimate in-flight work be abandoned.

## Problem

`LedgerService.CloseAsync` snapshots balances and freezes the period without checking whether any entry
*dated in that period* is still awaiting approval. A `PendingApproval` entry is durable and audited but
**not on the books**. Once the period closes:

- The entry can never be approved — `ApproveAsync` calls `EnsureOpenForPostAsync`, which rejects an
  effective date `<= closedThrough` (`LedgerService.cs:89`).
- The entry can never be voided either — `VoidAsync` calls the same guard (`LedgerService.cs:128`).

So the entry is stuck forever, and the only escape is `ReopenAsync` (admin + step-up re-auth — the most
privileged operation in the ledger). The close silently destroyed legitimate work and lied about being
"final and complete."

## Principle

This is not a defense against clerks doing something dumb — it is the **missing half of an invariant the
engine already enforces**. The engine says: *you cannot post into a closed period*
(`EnsureOpenForPostAsync`). The corollary it currently omits: *therefore you cannot close a period while
in-period work is still un-booked* — because closing doesn't defer that work, it annihilates it. A close
that abandons pending in-period entries is exactly as wrong as the journal holding an unbalanced entry:
the degradation is silent and after-the-fact irreparable. The fix is the same shape as every other core
guard — **reject proactively, before the damage, with the reason surfaced** — not detect-and-repair
afterward.

Scope discipline: this guards the *integrity of the close operation*, a core ledger invariant. It does
**not** try to judge entry content (duplicates, "looks wrong" amounts) — that would require guessing
intent and is explicitly out of scope.

## Approach

Add one read to `CloseAsync`, symmetric to the already-closed check it already does: before freezing,
find every entry that the close would strand, and if any exist, **refuse the close** and return the list
so the caller can resolve them. The period stays open, so the caller's resolution paths still work.

### What counts as a blocker

An entry is a blocker for `CloseAsync(clientId, asOf)` iff **all** hold:

- `ClientId == clientId`
- `Status == LifecycleStatus.Active` (ignore `Superseded` / `Voided` — already resolved)
- `Posting == PostingState.PendingApproval` (a `Posted` entry is on the books; not a problem)
- `EffectiveDate <= asOf` (an entry dated in a *future* open period is fine to leave pending)

This is the exact inverse of `EnsureOpenForPostAsync`'s predicate: the close refuses to freeze through
`asOf` precisely when a pending entry dated `<= asOf` would be orphaned by that freeze.

### Component 1 — Journal store: query the blockers

Add to `IJournalStore` (impl `MongoJournalStore`):

```csharp
/// Active, PendingApproval entries dated on or before asOf — the entries a close through
/// asOf would strand. Empty when the period is clean to close. Reads through the supplied
/// session so the close's gate runs on the same transactional snapshot as its checkpoint write.
Task<IReadOnlyList<JournalEntry>> GetPendingThroughAsync(
    Guid clientId, DateOnly asOf, IClientSessionHandle session, CancellationToken cancellationToken = default);
```

The `client_status_posting` index `(ClientId, Status, Posting)` already serves the equality predicate;
the `EffectiveDate <= asOf` range filters the (small) pending subset. No new index required. (If the
pending set ever grows large, extend that index with `EffectiveDate` — noted, not needed now.)

### Component 2 — Domain exception carrying the blockers

```csharp
// Accounting101.Ledger.Core.Journal
public sealed class PeriodCloseBlockedException(IReadOnlyList<JournalEntry> blockers)
    : InvalidOperationException(
        $"Cannot close: {blockers.Count} entr{(blockers.Count == 1 ? "y is" : "ies are")} " +
        "dated in this period and still awaiting approval. Approve or void them, then close.")
{
    public IReadOnlyList<JournalEntry> Blockers { get; } = blockers;
}
```

### Component 3 — `CloseAsync` gate, inside the transaction, fenced against concurrent posts

The gate is only sound if it is **airtight against a concurrent post** — otherwise a post committing a
new in-period pending entry alongside the close re-creates the exact stranding we are preventing. MongoDB
gives snapshot isolation, not serializability: two transactions abort each other **only when they write
the same document**. So an in-transaction read alone is not enough — close and post must contend on a
shared per-client document.

**That document already exists.** Gapless per-client sequencing means every `PostAsync` does a
`findAndModify $inc` on the counter doc `journal:{clientId}` **inside its transaction**
(`MongoSequenceStore.NextJournalAsync`) — so per-client posts already serialize on it. The fence is to
have `CloseAsync` **touch that same document inside its transaction**, forcing a write-conflict with any
concurrent post. `WithTransactionAsync` already retries the loser, which then re-reads fresh state and
does the right thing. The cost is *zero beyond what posting already pays* — posts already contend here.

`CloseAsync` runs all of its checks-and-writes in **one transaction**, reading through the session so the
snapshot is consistent and a retry re-reads committed state:

```csharp
await InTransactionAsync(async session =>
{
    // 1. already-closed guard (re-read via session)
    DateOnly? through = await _checkpoints.GetClosedThroughAsync(clientId, session, cancellationToken);
    if (through is { } t && asOf <= t)
        throw new InvalidOperationException($"Period is already closed through {t:yyyy-MM-dd}.");

    // 2. pending-blocker gate (read via session)
    IReadOnlyList<JournalEntry> blockers = await _journal.GetPendingThroughAsync(clientId, asOf, session, cancellationToken);
    if (blockers.Count > 0)
        throw new PeriodCloseBlockedException(blockers);

    // 3. fence: touch journal:{clientId} so a concurrent post write-conflicts with this close
    await _sequences.TouchJournalAsync(clientId, session, cancellationToken);

    // 4. snapshot opening balances (via session) and freeze
    IReadOnlyDictionary<Guid, decimal> balances = await _journal.AggregateBalancesAsync(clientId, asOf, session, cancellationToken);
    await _checkpoints.SaveAsync(clientId, asOf, balances, actor.UserId, now, session, cancellationToken);
    await _audit.AppendAsync(clientId, null, 0, AuditAction.PeriodClosed, actor, $"closed through {asOf:yyyy-MM-dd}", now, session, cancellationToken);
}, cancellationToken);
```

`TouchJournalAsync` bumps a separate `guard` field on the counter doc (it does **not** consume a journal
sequence number — that would gap the entry sequence); the write-conflict is document-level, so touching
any field suffices:

```csharp
// MongoSequenceStore
public Task TouchJournalAsync(Guid clientId, IClientSessionHandle session, CancellationToken ct) =>
    _counters.UpdateOneAsync(session,
        Builders<BsonDocument>.Filter.Eq("_id", "journal:" + clientId),
        Builders<BsonDocument>.Update.Inc("guard", 1L),
        new UpdateOptions { IsUpsert = true }, ct);
```

**Why this is airtight, both orderings:**
- *Post commits first* → close's `TouchJournalAsync` conflicts → close aborts & retries → on the fresh
  snapshot its `GetPendingThroughAsync` now sees the just-committed pending entry → blocks (no stranding).
- *Close commits first* (fence + freeze) → the concurrent post's `NextJournalAsync $inc` conflicts → post
  aborts & retries → on the fresh snapshot its in-transaction freeze re-check (Component 3b) sees the
  freeze → post rejected `409` (no entry in a frozen period). This is the backlog's **post-side freeze
  TOCTOU** — closed by the same fence.

#### Component 3b — `PostAsync` re-checks the freeze inside its transaction

The fence only helps if, after a conflict-retry, the post actually *re-reads* the freeze. Today
`PostAsync` checks `EnsureOpenAsync` **before** the transaction (`LedgerService.cs:69`), so a retry reuses
the stale pre-txn result. Keep that pre-txn check as a cheap fast-fail, and add an authoritative re-check
**inside** the transaction, reading the checkpoint via the session:

```csharp
await InTransactionAsync(async session =>
{
    await EnsureOpenForPostAsync(entry.ClientId, entry.EffectiveDate, session, cancellationToken); // in-txn, via session
    JournalEntry posted = await AppendSequencedAsync(entry, session, cancellationToken); // $inc journal:{clientId} = the fence
    await _audit.AppendAsync(...);
}, cancellationToken);
```

#### Component 3c — extend the fence to `ApproveAsync` / `VoidAsync` / `ReviseAsync`

These also mutate on-books state for an entry dated in the period and also call `EnsureOpenAsync`
pre-transaction (`LedgerService.cs:89`, `:128`). To make the freeze invariant *uniformly* airtight (not
just post-vs-close), give each the same treatment: move the freeze check inside the transaction (via
session) **and** call `TouchJournalAsync` inside the transaction so it fences against a concurrent close.
They already run in a transaction touching the projection; this is one extra guarded write each. Low-
frequency operations, so the added per-client contention is immaterial.

`CloseYearAsync` calls `CloseAsync` after it posts and approves the year-end `Closing` entry, so it
inherits the gate and the fence unchanged: the closing entry is `Posted` by then (not a blocker), but any
*other* pending entry dated `<= fiscalYearEnd` correctly blocks year-end close too.

### Component 4 — Surface it at the endpoint

`ClosePeriod` (`LedgerEndpoints.cs:254`) today catches `InvalidOperationException` → `Conflict` (409) for
"already closed." Add a more specific catch **before** it so the blocker list reaches the caller:

```csharp
catch (PeriodCloseBlockedException ex)
{
    return Results.Problem(
        detail: ex.Message,
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?>
        {
            ["blockers"] = ex.Blockers
                .Select(b => new PendingEntryRef(b.Id, b.Reference, b.EffectiveDate, b.Type.ToString()))
                .ToList(),
        });
}
catch (InvalidOperationException ex) // already closed through >= AsOf
{
    return Conflict(ex.Message);
}
```

New wire contract:

```csharp
// Accounting101.Ledger.Contracts
public sealed record PendingEntryRef(Guid EntryId, string? Reference, DateOnly EffectiveDate, string Type);
```

`CloseYear` (`LedgerEndpoints.cs:591`) gets the same `catch (PeriodCloseBlockedException ...)` so year-end
close surfaces blockers identically.

Status code: **409 Conflict** — the resource is in a conflicting state (unresolved in-period work),
parallel to the existing already-closed 409. The machine-readable `blockers` array lets a UI list exactly
what to fix, mirroring how `/entries/validate` surfaces a rejection reason.

## Resolution path (no new capability needed)

Because the gate refuses the close, the period stays open, so both existing remedies work on each blocker:

- **Approve it** — `POST /entries/{id}/approve`. `EnsureOpenForPostAsync` passes (period still open); the
  entry goes on the books. Then re-close.
- **Void it** — `POST /entries/{id}/void`. Same guard passes; `VoidAsync` marks a not-yet-posted entry
  `Voided` without touching the projection. Then re-close.

The controller chooses per entry; nothing new to build. (Contrast the stranded-after-close state, where
*neither* works — which is the whole reason to gate.)

## Data flow (clean close vs. blocked close)

```
Close June, all in-period entries Posted        Close June, BANK-05/ACCR-05/TAX-05 still PendingApproval
  └ already-closed? no                             └ already-closed? no
  └ GetPendingThrough(2024-06-30) -> []            └ GetPendingThrough(2024-06-30) -> [3 entries]
  └ aggregate balances, save checkpoint            └ throw PeriodCloseBlocked -> 409 + blockers[3]
  └ 200 + opening balances                         └ period NOT frozen; controller approves or voids the 3
                                                    └ re-close -> 200
```

## Error handling

- Blockers present → `409` with `blockers[]`; **no checkpoint written**, period stays open (assert a
  subsequent in-period post still succeeds).
- Already closed through `>= asOf` → `409` (unchanged).
- No blockers → closes exactly as today (regression).

## Testing

Engine (`Accounting101.Ledger.Mongo.Tests/PeriodCloseTests.cs`, real EphemeralMongo):

- Close blocked when an Active+PendingApproval entry dated `<= asOf` exists → `PeriodCloseBlockedException`
  whose `Blockers` contains that entry; **checkpoint not saved** — a fresh in-period post afterward still
  succeeds (proves the period is still open).
- Close **succeeds** when the only pending entry is dated *after* `asOf` (future period) — not a blocker.
- Close **succeeds** when every in-period entry is `Posted`.
- An entry that is `Voided` or `Superseded` but dated `<= asOf` does **not** block (Status filter).
- Resolution — approve: blocked → approve the blocker → re-close succeeds, entry is on the books.
- Resolution — void: blocked → void the blocker → re-close succeeds, entry excluded.
- Year-end (`CloseYearAsync`): an unrelated in-period pending entry blocks year-end close too; the
  engine-posted `Closing` entry itself never blocks (it is approved before `CloseAsync`).

Concurrency (`Accounting101.Ledger.Mongo.Tests/ConcurrencyTests.cs`, real EphemeralMongo — the fence):

- **Close vs. back-dated post, post wins the race:** start a post of an in-period pending entry and a
  `CloseAsync` concurrently; when the post commits first, the close must end in
  `PeriodCloseBlockedException` (it saw the entry) — never a successful close that strands it.
- **Close vs. back-dated post, close wins the race:** when the close commits first, the concurrent post
  must end rejected (`InvalidOperationException` "period is closed") — never a committed entry dated in
  the now-frozen period. (This is the backlog post-side freeze TOCTOU.)
- Run each ordering enough times (e.g. 20 interleavings) that a missing fence would flake. Exactly one of
  {close succeeds, post succeeds} resolves cleanly each run; the loser fails closed.
- `ApproveAsync` / `VoidAsync` vs. concurrent close: the mutation never lands on the books in a period the
  close froze (same fence).

API (`Accounting101.Ledger.Api.Tests`):

- `POST /periods/close` with a pending in-period entry → `409` ProblemDetails carrying a `blockers` array
  with `{entryId, reference, effectiveDate, type}` for the offending entry.
- `POST /periods/close-year` surfaces blockers the same way.

## Scope

**In scope:** the `GetPendingThroughAsync` query (session-aware), the session-aware
`GetClosedThroughAsync` / `AggregateBalancesAsync` overloads, `MongoSequenceStore.TouchJournalAsync` (the
fence), the session-aware `EnsureOpenForPostAsync` overload, the in-transaction freeze re-check + fence in
`PostAsync`/`ApproveAsync`/`VoidAsync`/`ReviseAsync`, the `CloseAsync` in-transaction gate + fence
(inherited by `CloseYearAsync`), the `PeriodCloseBlockedException`, the `PendingEntryRef` contract, and
surfacing on both close endpoints + tests. **This slice closes the backlog's reclassified group-1
"closed-period freeze TOCTOU" item** as a direct consequence of the fence.

**Out of scope (separate, deliberately):**
- **Catch-up posting / the stranded-entry remedy.** This gate *prevents new* strandings; it does not
  repair the three already stranded in historical data. That repair (post a current-period catch-up, or
  reopen) is its own slice.
- **The pending-only entries-list filter / ordering fix.** Complementary read-side usability (so an
  approver can *find* pending entries) — the gate makes missing them non-catastrophic, but the filter is
  a separate change. (Read side primary, command guard backstop.)
- **Batch idempotency key** on bill/JE creation — unrelated transport-hygiene item from the same run.
- Duplicate-detection of content — explicitly rejected (would require guessing intent).

## Global constraints

- .NET 10; tests use EphemeralMongo (real transactions); build with 0 warnings; commit per slice; TDD.
- Engine enforces only irreducible invariants — this **completes** the existing closed-period invariant,
  it does not add a new policy. It is domain-agnostic (knows nothing of invoices/bills) and host-policy
  free (no role/SoD logic; the host already gates `Close` permission upstream).
