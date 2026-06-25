# Period-close pending-gate + freeze fence — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refuse to close a period while any entry dated in it is still awaiting approval, and make the closed-period freeze airtight against concurrent posts/approvals/voids/revisions.

**Architecture:** The engine already serializes per-client posts on the journal-counter document `journal:{clientId}` (gapless sequencing `$inc`s it inside the post transaction). We reuse that document as a per-client **fence**: `CloseAsync` touches it inside its transaction, so a concurrent post write-conflicts and one side retries onto a fresh snapshot. The close's already-closed guard, pending-blocker gate, balance snapshot, and freeze all move **into one transaction** reading via the session; each freeze-checked mutation re-reads the freeze inside its transaction. A pending entry dated `<= asOf` blocks the close with a `409` listing the blockers.

**Tech Stack:** C#/.NET 10, MongoDB (replica-set transactions via `IClientSessionHandle` + `WithTransactionAsync`), xUnit + EphemeralMongo (real transactions).

## Global Constraints

- .NET 10; build with **0 warnings**; commit per task; TDD (red → green → commit).
- Tests use EphemeralMongo (real replica-set transactions). Host-boot/Mongo test classes can flake when run together (`replSetInitiate already initialized`) — run a single test class at a time when verifying.
- The engine enforces only irreducible invariants. This **completes** the closed-period freeze invariant; it adds no policy, no domain knowledge (nothing about invoices/bills), and no auth (the host already gates the `Close` permission).
- The fence must **not** consume a journal sequence number (that would gap the entry sequence). It bumps a separate `guard` field on `journal:{clientId}`.
- Commit trailer (required): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit file lists per commit (the IDE linter may rewrite types to `var` / touch unrelated files); check for stray churn before each commit.

---

## Task 1: Store plumbing — session-aware reads, pending query, and the fence touch

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoCheckpointStore.cs`
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs`
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoSequenceStore.cs`
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/JournalStoreQueryTests.cs` (add cases; create if absent)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/SequenceStoreTests.cs` (add a case; create if absent)

**Interfaces:**
- Produces (consumed by Tasks 2/3/5):
  - `MongoCheckpointStore.GetClosedThroughAsync(Guid clientId, IClientSessionHandle session, CancellationToken)` → `Task<DateOnly?>`
  - `MongoJournalStore.GetPendingThroughAsync(Guid clientId, DateOnly asOf, IClientSessionHandle session, CancellationToken)` → `Task<IReadOnlyList<JournalEntry>>`
  - `MongoJournalStore.AggregateBalancesAsync(Guid clientId, DateOnly? asOf, IClientSessionHandle session, CancellationToken)` → `Task<IReadOnlyDictionary<Guid, decimal>>` (session overload)
  - `MongoSequenceStore.TouchJournalAsync(Guid clientId, IClientSessionHandle session, CancellationToken)` → `Task`

- [ ] **Step 1: Write failing tests**

In `JournalStoreQueryTests.cs` (uses the existing EphemeralMongo fixture pattern — copy the harness/setup from a sibling test in `Accounting101.Ledger.Mongo.Tests`). Seed via the store under a session.

```csharp
[Fact]
public async Task GetPendingThrough_returns_active_pending_entries_on_or_before_asOf_only()
{
    // Arrange: one Active+PendingApproval @ 2024-06-30 (R1), one Posted @ 2024-06-30 (R2),
    // one Active+PendingApproval @ 2024-07-15 (future, R3), one Voided pending-shaped @ 2024-06-10 (R4).
    // (Build via JournalEntry.Create + .Approve()/.Void() as the other store tests do.)
    using IClientSessionHandle session = await Client.StartSessionAsync();

    IReadOnlyList<JournalEntry> blockers =
        await Journal.GetPendingThroughAsync(ClientId, new DateOnly(2024, 6, 30), session, CancellationToken.None);

    Assert.Equal(new[] { R1.Id }, blockers.Select(b => b.Id).ToArray());
}

[Fact]
public async Task TouchJournal_does_not_consume_a_sequence_number()
{
    using IClientSessionHandle session = await Client.StartSessionAsync();
    long before = await Sequences.NextJournalAsync(ClientId, session, CancellationToken.None); // e.g. 1

    await Sequences.TouchJournalAsync(ClientId, session, CancellationToken.None);

    long after = await Sequences.NextJournalAsync(ClientId, session, CancellationToken.None);
    Assert.Equal(before + 1, after); // touch bumped `guard`, NOT `seq` — next seq is contiguous
}
```

- [ ] **Step 2: Run the tests, confirm they fail** (method/overload not defined)

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "GetPendingThrough_returns_active_pending_entries_on_or_before_asOf_only|TouchJournal_does_not_consume_a_sequence_number"`
Expected: FAIL (does not compile / method missing).

- [ ] **Step 3: Implement**

`MongoSequenceStore.cs` — add (uses the existing `_counters` collection and `journal:` key):

```csharp
/// <summary>
/// Touch the per-client journal counter document WITHOUT consuming a sequence number — bumps a separate
/// <c>guard</c> field. The period close calls this inside its transaction so it write-conflicts with any
/// concurrent post (which <c>$inc</c>s <c>seq</c> on the same document): one side retries onto a fresh
/// snapshot. Reuses the document posts already serialize on, so it adds no new contention point.
/// </summary>
public Task TouchJournalAsync(Guid clientId, IClientSessionHandle session, CancellationToken cancellationToken = default) =>
    _counters.UpdateOneAsync(
        session,
        Builders<BsonDocument>.Filter.Eq("_id", "journal:" + clientId),
        Builders<BsonDocument>.Update.Inc("guard", 1L),
        new UpdateOptions { IsUpsert = true },
        cancellationToken);
```

`MongoCheckpointStore.cs` — add a session overload that reads through the session, and have the existing parameterless method delegate without a session (keep current signature for non-transactional callers):

```csharp
/// <summary>The client's close date read through <paramref name="session"/> (the transactional snapshot).</summary>
public async Task<DateOnly?> GetClosedThroughAsync(Guid clientId, IClientSessionHandle session, CancellationToken cancellationToken = default)
{
    CheckpointDocument? checkpoint = await _checkpoints
        .Find(session, c => c.ClientId == clientId)
        .FirstOrDefaultAsync(cancellationToken);

    return checkpoint is null ? null : DateOnly.ParseExact(checkpoint.AsOf, DateFormat, CultureInfo.InvariantCulture);
}
```

`MongoJournalStore.cs` — add the pending query and a session overload of the balance fold:

```csharp
/// <summary>
/// Active, PendingApproval entries dated on or before <paramref name="asOf"/> — exactly the entries a
/// close through asOf would strand. Read through the session so the close's gate runs on the same
/// transactional snapshot as its freeze write. Empty when the period is clean to close.
/// </summary>
public async Task<IReadOnlyList<JournalEntry>> GetPendingThroughAsync(
    Guid clientId, DateOnly asOf, IClientSessionHandle session, CancellationToken cancellationToken = default)
{
    FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
    FilterDefinition<JournalEntryDocument> filter = f.And(
        f.Eq(e => e.ClientId, clientId),
        f.Eq(e => e.Status, nameof(LifecycleStatus.Active)),
        f.Eq(e => e.Posting, nameof(PostingState.PendingApproval)),
        f.Lte(e => e.EffectiveDate, Iso(asOf)));   // reuse the same date encoding AggregateBalances uses

    List<JournalEntryDocument> docs = await _entries.Find(session, filter).ToListAsync(cancellationToken);
    return docs.Select(d => d.ToDomain()).ToList();
}
```

> Implementer note: confirm how `Status`/`Posting`/`EffectiveDate` are stored on `JournalEntryDocument` (string enum names vs int; `EffectiveDate` via the `Iso(...)` helper already used by `AggregateBalancesAsync`). Match the existing serialization exactly — use the same `Iso(asOf)`/comparison the aggregation match uses so the index `client_status_posting` and the date filter behave identically. If the doc stores enums as something other than `nameof(...)`, filter on the actual stored representation.

For `AggregateBalancesAsync`, thread an optional session through to the fold so the close reads balances in-transaction. Add an overload (keep the existing one delegating with `session: null`):

```csharp
public Task<IReadOnlyDictionary<Guid, decimal>> AggregateBalancesAsync(
    Guid clientId, DateOnly? asOf, IClientSessionHandle session, CancellationToken cancellationToken = default)
{
    BsonDocument match = OnBooks(clientId);
    if (asOf is { } asOfDate) match.Add("EffectiveDate", new BsonDocument("$lte", Iso(asOfDate)));
    return FoldAsync(match, session, cancellationToken);
}
```

…and give `FoldAsync` an `IClientSessionHandle? session = null` parameter, calling `_entries.Aggregate(session, pipeline, ...)` when non-null (mirror the existing non-session aggregate call). The existing `AggregateBalancesAsync(clientId, asOf, ct)` stays and calls the new fold with `session: null`.

- [ ] **Step 4: Run the tests, confirm they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "GetPendingThrough_returns_active_pending_entries_on_or_before_asOf_only|TouchJournal_does_not_consume_a_sequence_number"`
Expected: PASS.

- [ ] **Step 5: Build clean, commit**

Run: `dotnet build Backend/Accounting101.Ledger.Mongo` → 0 warnings.
```bash
git add Backend/Accounting101.Ledger.Mongo/MongoCheckpointStore.cs Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs Backend/Accounting101.Ledger.Mongo/MongoSequenceStore.cs Backend/Accounting101.Ledger.Mongo.Tests/JournalStoreQueryTests.cs Backend/Accounting101.Ledger.Mongo.Tests/SequenceStoreTests.cs
git commit -m "feat(ledger): session-aware pending-through query, balance fold, and journal-counter fence touch"
```

---

## Task 2: In-transaction freeze re-check for `PostAsync`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/LedgerService.cs`
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/LedgerServiceTests.cs` (add a regression case)

**Interfaces:**
- Consumes: `MongoCheckpointStore.GetClosedThroughAsync(clientId, session, ct)` (Task 1).
- Produces (consumed by Tasks 3/5): `LedgerService.EnsureOpenForPostAsync(Guid clientId, DateOnly effectiveDate, IClientSessionHandle session, CancellationToken)` — session overload throwing the same `InvalidOperationException` message as the parameterless one.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Post_into_a_closed_period_is_rejected()
{
    // Arrange: open a client, close through 2024-06-30.
    // Act + Assert: posting an entry dated 2024-06-15 throws InvalidOperationException mentioning "closed".
    InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
        () => Service.PostAsync(EntryDated(new DateOnly(2024, 6, 15)), Actor, CancellationToken.None));
    Assert.Contains("closed", ex.Message, StringComparison.OrdinalIgnoreCase);
}
```

(If an equivalent regression already exists, keep it — the point is it must still pass after the freeze check moves into the transaction.)

- [ ] **Step 2: Run, confirm current behavior** — this should already PASS via the pre-transaction check. Run it to capture the green baseline before refactoring:
Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "Post_into_a_closed_period_is_rejected"` → PASS.

- [ ] **Step 3: Implement**

Add the session overload (place beside the existing `EnsureOpenForPostAsync` near `LedgerService.cs:425`):

```csharp
/// <summary>
/// Freeze guard read through <paramref name="session"/> — the authoritative in-transaction check. After a
/// write-conflict retry (against a concurrent close), this re-reads the now-current freeze on a fresh
/// snapshot, so a back-dated mutation cannot slip into a period the close just froze.
/// </summary>
public async Task EnsureOpenForPostAsync(
    Guid clientId, DateOnly effectiveDate, IClientSessionHandle session, CancellationToken cancellationToken)
{
    DateOnly? closedThrough = await _checkpoints.GetClosedThroughAsync(clientId, session, cancellationToken);
    if (closedThrough is { } through && effectiveDate <= through)
        throw new InvalidOperationException(
            $"Period is closed through {through:yyyy-MM-dd}; entry dated {effectiveDate:yyyy-MM-dd} is in a closed period.");
}
```

In `PostAsync` (`LedgerService.cs:65`), keep the pre-transaction `EnsureOpenAsync` as a cheap fast-fail, and add the authoritative re-check inside the transaction before the append:

```csharp
await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken); // fast-fail (unchanged)

DateTimeOffset now = DateTimeOffset.UtcNow;
await InTransactionAsync(async session =>
{
    await EnsureOpenForPostAsync(entry.ClientId, entry.EffectiveDate, session, cancellationToken); // authoritative, via session
    JournalEntry posted = await AppendSequencedAsync(entry, session, cancellationToken); // $inc journal:{clientId} = the fence
    await _audit.AppendAsync(posted.ClientId, posted.Id, posted.Version, AuditAction.Created, actor, null, now, session, cancellationToken);
}, cancellationToken);
```

- [ ] **Step 4: Run the test, confirm it still passes**
Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "Post_into_a_closed_period_is_rejected"` → PASS.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Mongo/LedgerService.cs Backend/Accounting101.Ledger.Mongo.Tests/LedgerServiceTests.cs
git commit -m "feat(ledger): re-check the closed-period freeze inside the post transaction (fence half)"
```

---

## Task 3: `CloseAsync` in-transaction gate + fence + `PeriodCloseBlockedException`

**Files:**
- Create: `Backend/Accounting101.Ledger.Core/Journal/PeriodCloseBlockedException.cs`
- Modify: `Backend/Accounting101.Ledger.Mongo/LedgerService.cs` (`CloseAsync`)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/PeriodCloseTests.cs`

**Interfaces:**
- Consumes: `GetPendingThroughAsync`, `GetClosedThroughAsync(session)`, `AggregateBalancesAsync(session)`, `TouchJournalAsync` (Task 1).
- Produces (consumed by Task 4): `PeriodCloseBlockedException { IReadOnlyList<JournalEntry> Blockers }`.

- [ ] **Step 1: Write failing tests**

In `PeriodCloseTests.cs`:

```csharp
[Fact]
public async Task Close_is_blocked_by_an_in_period_pending_entry_and_does_not_freeze()
{
    // Arrange: open client; post (but DO NOT approve) an entry dated 2024-06-30.
    PeriodCloseBlockedException ex = await Assert.ThrowsAsync<PeriodCloseBlockedException>(
        () => Service.CloseAsync(ClientId, new DateOnly(2024, 6, 30), Actor, CancellationToken.None));
    Assert.Contains(ex.Blockers, b => b.EffectiveDate == new DateOnly(2024, 6, 30));

    // Not frozen: a fresh in-period post still succeeds (period stayed open).
    await Service.PostAsync(EntryDated(new DateOnly(2024, 6, 20)), Actor, CancellationToken.None);
}

[Fact]
public async Task Close_succeeds_when_only_pending_entry_is_in_a_future_period()
{
    // pending entry dated 2024-07-10; closing through 2024-06-30 must succeed.
    await Service.CloseAsync(ClientId, new DateOnly(2024, 6, 30), Actor, CancellationToken.None);
}

[Fact]
public async Task Close_succeeds_when_all_in_period_entries_are_posted() { /* approve then close -> ok */ }

[Fact]
public async Task Voided_or_superseded_in_period_entry_does_not_block_close() { /* status filter */ }

[Fact]
public async Task Blocked_close_is_resolved_by_approving_the_blocker_then_reclosing()
{
    // post (no approve) @ 2024-06-30 -> close throws -> approve it -> close succeeds; entry on the books.
}

[Fact]
public async Task Blocked_close_is_resolved_by_voiding_the_blocker_then_reclosing()
{
    // post (no approve) @ 2024-06-30 -> close throws -> void it -> close succeeds; entry excluded.
}

[Fact]
public async Task Year_end_close_is_blocked_by_an_unrelated_in_period_pending_entry()
{
    // CloseYearAsync with a stray pending entry dated <= fiscalYearEnd throws PeriodCloseBlockedException.
}
```

- [ ] **Step 2: Run, confirm they fail**
Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "PeriodCloseTests"`
Expected: FAIL (type `PeriodCloseBlockedException` missing; close currently freezes regardless of pending).

- [ ] **Step 3: Implement**

Create the exception:

```csharp
// Backend/Accounting101.Ledger.Core/Journal/PeriodCloseBlockedException.cs
namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// A period close was refused because entries dated in that period are still awaiting approval — closing
/// would strand them (a closed period can no longer be posted to or voided). Carries the blockers so the
/// caller can list exactly what to approve or void before retrying.
/// </summary>
public sealed class PeriodCloseBlockedException(IReadOnlyList<JournalEntry> blockers)
    : InvalidOperationException(
        $"Cannot close: {blockers.Count} entr{(blockers.Count == 1 ? "y is" : "ies are")} " +
        "dated in this period and still awaiting approval. Approve or void them, then close.")
{
    public IReadOnlyList<JournalEntry> Blockers { get; } = blockers;
}
```

Rewrite `CloseAsync` (`LedgerService.cs:280`) so every check and write runs in one transaction reading via the session, with the fence touch:

```csharp
public async Task<IReadOnlyDictionary<Guid, decimal>> CloseAsync(
    Guid clientId, DateOnly asOf, Actor actor, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(actor);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    IReadOnlyDictionary<Guid, decimal> balances = new Dictionary<Guid, decimal>();

    await InTransactionAsync(async session =>
    {
        // already-closed guard (via session)
        DateOnly? through = await _checkpoints.GetClosedThroughAsync(clientId, session, cancellationToken);
        if (through is { } t && asOf <= t)
            throw new InvalidOperationException($"Period is already closed through {t:yyyy-MM-dd}.");

        // pending-blocker gate (via session)
        IReadOnlyList<JournalEntry> blockers = await _journal.GetPendingThroughAsync(clientId, asOf, session, cancellationToken);
        if (blockers.Count > 0)
            throw new PeriodCloseBlockedException(blockers);

        // fence: write-conflict against any concurrent post on journal:{clientId}
        await _sequences.TouchJournalAsync(clientId, session, cancellationToken);

        // snapshot opening balances (via session) and freeze
        balances = await _journal.AggregateBalancesAsync(clientId, asOf, session, cancellationToken);
        await _checkpoints.SaveAsync(clientId, asOf, balances, actor.UserId, now, session, cancellationToken);
        await _audit.AppendAsync(clientId, null, 0, AuditAction.PeriodClosed, actor, $"closed through {asOf:yyyy-MM-dd}", now, session, cancellationToken);
    }, cancellationToken);

    return balances;
}
```

> Notes for the implementer:
> - `WithTransactionAsync` retries only transient (write-conflict) errors; `PeriodCloseBlockedException` and the already-closed `InvalidOperationException` are non-transient, so they abort the transaction (nothing committed) and propagate. That is the desired "blocked → no freeze" behavior.
> - `CloseYearAsync` (`LedgerService.cs:345`) already calls `CloseAsync` at the end; it inherits the gate + fence with no change. Its own `Closing` entry is posted+approved before `CloseAsync`, so it is `Posted` (never a blocker).
> - Add `using Accounting101.Ledger.Core.Journal;` if not already present for the exception.

- [ ] **Step 4: Run the tests, confirm they pass**
Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "PeriodCloseTests"` → PASS.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Core/Journal/PeriodCloseBlockedException.cs Backend/Accounting101.Ledger.Mongo/LedgerService.cs Backend/Accounting101.Ledger.Mongo.Tests/PeriodCloseTests.cs
git commit -m "feat(ledger): gate period close on in-period pending entries, fenced inside the close transaction"
```

---

## Task 4: Surface the block at the close endpoints (`409` + `blockers[]`)

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/PendingEntryRef.cs`
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`ClosePeriod`, `CloseYear`)
- Test: `Backend/Accounting101.Ledger.Api.Tests/PeriodCloseApiTests.cs` (create if absent; otherwise add to the close test class)

**Interfaces:**
- Consumes: `PeriodCloseBlockedException` (Task 3).
- Produces: `PendingEntryRef(Guid EntryId, string? Reference, DateOnly EffectiveDate, string Type)` — the wire shape inside the ProblemDetails `blockers` extension.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Close_with_an_in_period_pending_entry_returns_409_with_blockers()
{
    // Arrange via the API test host: onboard, post (no approve) an entry dated 2024-06-30.
    HttpResponseMessage resp = await Client.PostAsJsonAsync(
        $"/clients/{clientId}/periods/close", new { asOf = "2024-06-30" });

    Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    using JsonDocument body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    JsonElement blockers = body.RootElement.GetProperty("blockers");
    Assert.Equal(1, blockers.GetArrayLength());
    Assert.Equal("2024-06-30", blockers[0].GetProperty("effectiveDate").GetString());
}
```

(Follow the existing Api.Tests host/auth setup — reuse the helper that issues a `DevToken` with the `Close` permission, as the other close/onboarding API tests do.)

- [ ] **Step 2: Run, confirm it fails**
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Close_with_an_in_period_pending_entry_returns_409_with_blockers"`
Expected: FAIL (today the pending entry doesn't block; close returns 200 — or 500 if the exception escaped unmapped).

- [ ] **Step 3: Implement**

Contract:

```csharp
// Backend/Accounting101.Ledger.Contracts/PendingEntryRef.cs
namespace Accounting101.Ledger.Contracts;

/// <summary>One entry blocking a period close: dated in the period, still awaiting approval.</summary>
public sealed record PendingEntryRef(Guid EntryId, string? Reference, DateOnly EffectiveDate, string Type);
```

In `ClosePeriod` (`LedgerEndpoints.cs:254`), add a catch **before** the existing `InvalidOperationException` catch (order matters — the blocked exception is a subclass):

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

Apply the same `catch (PeriodCloseBlockedException ex) { ... }` to `CloseYear` (`LedgerEndpoints.cs:591`) ahead of its error handling, so year-end close surfaces blockers identically. Add `using Accounting101.Ledger.Core.Journal;` for the exception type if needed.

- [ ] **Step 4: Run the test, confirm it passes**
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Close_with_an_in_period_pending_entry_returns_409_with_blockers"` → PASS.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Contracts/PendingEntryRef.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/PeriodCloseApiTests.cs
git commit -m "feat(ledger-api): surface period-close blockers as 409 with a blockers[] list on close and close-year"
```

---

## Task 5: Extend the fence to `ApproveAsync` / `VoidAsync` / `ReviseAsync` / `ReverseAsync`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/LedgerService.cs`
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/LedgerServiceTests.cs` (regressions)

**Interfaces:**
- Consumes: `EnsureOpenForPostAsync(…, session, …)` and `TouchJournalAsync` (Tasks 1–2).

**Background (which methods already fence):** `ReviseAsync` (`:150`) and `ReverseAsync` (`:187`) append a new entry via `AppendSequencedAsync`, which already `$inc`s `journal:{clientId}` inside their transaction — they are already fenced; they only need the freeze check moved inside the transaction. `ApproveAsync` (`:85`) and `VoidAsync` (`:124`) append **no** entry, so they need an explicit `TouchJournalAsync` **and** the in-transaction freeze check.

- [ ] **Step 1: Write failing/regression tests**

```csharp
[Fact]
public async Task Approve_into_a_closed_period_is_rejected() { /* close, then approving an entry dated in it throws "closed" */ }

[Fact]
public async Task Void_into_a_closed_period_is_rejected() { /* same shape */ }
```

(These mirror behavior the pre-transaction checks already give; they must still hold after the checks move inside the transaction.)

- [ ] **Step 2: Run, confirm green baseline**
Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "Approve_into_a_closed_period_is_rejected|Void_into_a_closed_period_is_rejected"` → PASS (baseline).

- [ ] **Step 3: Implement** — for each method, replace the pre-transaction `EnsureOpenAsync(...)` with an in-transaction `EnsureOpenForPostAsync(..., session, ...)` (keep an optional pre-txn fast-fail), and for the two non-appending methods add `TouchJournalAsync`:

`ApproveAsync` (`:85`) — move the freeze check inside the transaction and add the fence touch:

```csharp
// remove the pre-transaction: await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);
await InTransactionAsync(async session =>
{
    await EnsureOpenForPostAsync(approved.ClientId, approved.EffectiveDate, session, cancellationToken);
    await _sequences.TouchJournalAsync(approved.ClientId, session, cancellationToken); // fence (approve appends no entry)
    await _journal.ReplaceAsync(approved, session, cancellationToken);
    await _projection.ApplyAsync(approved, session, cancellationToken);
    await _audit.AppendAsync(...);
    if (original is not null) { /* unchanged supersede swap */ }
}, cancellationToken);
```

> Keep the existing pre-transaction read of `original`/revision validation as-is; only the freeze check and fence move/append. Verify the revision branch still reads `original` correctly (it does so before the transaction today).

`VoidAsync` (`:124`) — same shape: drop the pre-transaction `EnsureOpenAsync`, add inside the transaction `await EnsureOpenForPostAsync(voided.ClientId, voided.EffectiveDate, session, cancellationToken);` then `await _sequences.TouchJournalAsync(voided.ClientId, session, cancellationToken);` before the `ReplaceAsync`/`ReverseAsync`/audit writes.

`ReviseAsync` (`:150`) and `ReverseAsync` (`:187`) — move their `EnsureOpenAsync(...)` calls (`:164–165`, `:204`) to inside their existing `InTransactionAsync` body as `EnsureOpenForPostAsync(..., session, ...)` (for `ReviseAsync`, check **both** the original's and the replacement's effective dates). No explicit `TouchJournalAsync` needed — `AppendSequencedAsync` already fences them.

- [ ] **Step 4: Run the tests, confirm they pass**
Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "Approve_into_a_closed_period_is_rejected|Void_into_a_closed_period_is_rejected"` → PASS.
Then run the full class once to catch regressions: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "LedgerServiceTests"` → PASS.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Mongo/LedgerService.cs Backend/Accounting101.Ledger.Mongo.Tests/LedgerServiceTests.cs
git commit -m "feat(ledger): fence approve/void/revise/reverse against concurrent close inside their transactions"
```

---

## Task 6: Concurrency tests — prove the fence (both race orderings)

**Files:**
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/ConcurrencyTests.cs` (add to the existing file)

**Interfaces:**
- Consumes: the full `LedgerService` (Tasks 2–5).

- [ ] **Step 1: Write the tests**

```csharp
[Fact]
public async Task Concurrent_close_and_backdated_post_never_strand_and_never_post_into_a_frozen_period()
{
    for (int i = 0; i < 20; i++)
    {
        // Fresh client per iteration (or fresh in-period date) so iterations are independent.
        Guid clientId = await NewOpenClientAsync();
        DateOnly asOf = new(2024, 6, 30);

        Task close = Service.CloseAsync(clientId, asOf, Actor, CancellationToken.None);
        Task post  = Service.PostAsync(EntryDated(clientId, asOf), Actor, CancellationToken.None); // pending, in-period

        Exception? closeEx = await Record.ExceptionAsync(() => close);
        Exception? postEx  = await Record.ExceptionAsync(() => post);

        bool closed = closeEx is null;
        if (closed)
        {
            // Close won: the post MUST have been rejected (period frozen) — no entry in the closed period.
            Assert.IsType<InvalidOperationException>(postEx);
            Assert.Contains("closed", postEx!.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Close lost: it MUST be the pending-block (it saw the entry), never any other failure.
            Assert.IsType<PeriodCloseBlockedException>(closeEx);
            Assert.Null(postEx); // the post committed; close correctly refused to strand it
        }
    }
}

[Fact]
public async Task Concurrent_void_and_close_never_void_into_a_frozen_period()
{
    // Post+approve an entry dated 2024-06-30, then race VoidAsync(it) against CloseAsync(2024-06-30).
    // Assert exactly one wins: either the void commits and the close blocks/loses, or the close commits
    // and the void is rejected ("closed"). Never both; never a void applied to a frozen-period entry.
}
```

> Implementer notes:
> - Launch the two tasks without awaiting between them so they genuinely interleave; rely on `WithTransactionAsync`'s write-conflict retry. 20 iterations gives interleavings; a missing fence will flake here (an iteration where both "succeed", or a stranded entry after a successful close).
> - After a "close won" iteration, additionally assert `GetPendingThroughAsync`/balances show no in-period pending entry left behind; after a "post won" iteration, assert the period is **not** frozen (a later in-period post still works) OR the close raised the block — exactly one.
> - These touch real transactions — run this class on its own (EphemeralMongo single-class guidance).

- [ ] **Step 2: Run, expect PASS** (the fence from Tasks 2–5 is already in place)
Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "ConcurrencyTests"` → PASS (run a few times to be sure it is not flaky).

- [ ] **Step 3: Commit**
```bash
git add Backend/Accounting101.Ledger.Mongo.Tests/ConcurrencyTests.cs
git commit -m "test(ledger): prove the close/post freeze fence under concurrency (both race orderings)"
```

---

## Final verification (after all tasks)

- [ ] `dotnet build` the full solution → **0 warnings**.
- [ ] Run each touched Mongo/Api test class individually (EphemeralMongo single-class guidance):
  `PeriodCloseTests`, `YearEndCloseTests`, `LedgerServiceTests`, `ConcurrencyTests`, `JournalStoreQueryTests`, `SequenceStoreTests`, the close Api test class.
- [ ] Confirm the backlog's reclassified "closed-period freeze TOCTOU" item is satisfied by Task 6 (note it in the whole-branch review).
- [ ] Whole-branch review on the most capable model (per subagent-driven-development), then `superpowers:finishing-a-development-branch`.

## Self-review notes (author)

- **Spec coverage:** gate (T3), 409+blockers (T4), in-txn fence both orderings (T2+T3+T6), approve/void/revise/reverse parity (T5), session-aware stores (T1) — all mapped.
- **Type consistency:** `EnsureOpenForPostAsync(Guid, DateOnly, IClientSessionHandle, CancellationToken)`, `GetPendingThroughAsync(Guid, DateOnly, IClientSessionHandle, CancellationToken)`, `TouchJournalAsync(Guid, IClientSessionHandle, CancellationToken)`, `PeriodCloseBlockedException.Blockers`, `PendingEntryRef(Guid, string?, DateOnly, string)` — used identically across tasks.
- **Open implementer check (flagged, not a placeholder):** the exact stored representation of `Status`/`Posting`/`EffectiveDate` on `JournalEntryDocument` must be confirmed in Task 1 so `GetPendingThroughAsync`'s filter matches the existing aggregation encoding (`Iso(...)`, enum-as-string vs int). Called out in Task 1 Step 3.
