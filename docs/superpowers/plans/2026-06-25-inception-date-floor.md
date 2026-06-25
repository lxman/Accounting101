# Inception date floor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Reject any post dated before the client's opening-balance date, by seeding the closed-period freeze at onboarding (pre-inception = a closed period). Kills the `effectiveDate = 0001-01-01` class.

**Architecture:** No new rule — reuse `EnsureOpenForPostAsync` (rejects `effectiveDate <= closedThrough`). `OpenAsync`, after approving the opening entry, writes the checkpoint with `closedThrough = asOf − 1 day` (empty balances).

**Tech Stack:** C#/.NET 10, MongoDB, xUnit + EphemeralMongo.

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- Reuse the existing closed-period guard; add NO parallel validation rule.
- The opening entry (dated `asOf`) must remain on the books — seed the checkpoint AFTER it is posted+approved, so its own freeze never blocks it.
- Off-by-one: `closedThrough = asOf.AddDays(-1)` so `>= asOf` is allowed and `< asOf` (incl. `0001-01-01`) is rejected.
- No future/upper bound. No reporting change (`GetOpeningBalancesAsync` stays empty for a fresh client).
- Tests use EphemeralMongo; run the test class on its own when verifying.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit file lists; check for stray churn.

---

## Task 1: Seed the inception freeze in `OpenAsync`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/LedgerService.cs` (`OpenAsync`)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/OnboardingTests.cs` (add cases; create if absent — mirror a sibling test's EphemeralMongo harness)

**Interfaces:**
- Consumes: existing `_checkpoints.SaveAsync(Guid, DateOnly, IReadOnlyDictionary<Guid,decimal>, Guid, DateTimeOffset, IClientSessionHandle?, CancellationToken)` and `EnsureOpenForPostAsync`.
- Behavior change: after onboarding, `GetClosedThroughAsync(clientId)` returns `asOf − 1`.

- [ ] **Step 1: Write the failing tests**

In `OnboardingTests.cs` (read the file first; reuse its harness for constructing `LedgerService`, onboarding a client via `OpenAsync`, and posting entries):

```csharp
[Fact]
public async Task Onboarding_seeds_the_inception_freeze_at_the_day_before_opening()
{
    DateOnly opening = new(2024, 1, 1);
    await Service.OpenAsync(clientId, opening, OpeningLines(), Actor);   // balanced opening lines
    Assert.Equal(new DateOnly(2023, 12, 31), await Checkpoints.GetClosedThroughAsync(clientId));
}

[Fact]
public async Task Post_dated_before_opening_is_rejected()
{
    DateOnly opening = new(2024, 1, 1);
    await Service.OpenAsync(clientId, opening, OpeningLines(), Actor);
    InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
        () => Service.PostAsync(EntryDated(new DateOnly(1, 1, 1)), Actor));        // the 0001-01-01 footgun
    Assert.Contains("closed", ex.Message, StringComparison.OrdinalIgnoreCase);
    // also reject the day before opening
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => Service.PostAsync(EntryDated(new DateOnly(2023, 12, 31)), Actor));
}

[Fact]
public async Task Post_on_or_after_opening_is_allowed()
{
    DateOnly opening = new(2024, 1, 1);
    await Service.OpenAsync(clientId, opening, OpeningLines(), Actor);
    await Service.PostAsync(EntryDated(new DateOnly(2024, 1, 1)), Actor);   // on the opening date
    await Service.PostAsync(EntryDated(new DateOnly(2024, 1, 15)), Actor);  // after
    // no throw
}

[Fact]
public async Task Opening_entry_is_on_the_books_after_onboarding()
{
    DateOnly opening = new(2024, 1, 1);
    JournalEntry openingEntry = await Service.OpenAsync(clientId, opening, OpeningLines(), Actor);
    JournalEntry? read = await Service.GetEntryAsync(clientId, openingEntry.Id);
    Assert.NotNull(read);
    Assert.Equal(PostingState.Posted, read!.Posting);   // posted+approved, not blocked by its own inception freeze
}

[Fact]
public async Task First_real_close_still_works_after_inception_seed()
{
    DateOnly opening = new(2024, 1, 1);
    await Service.OpenAsync(clientId, opening, OpeningLines(), Actor);
    await Service.PostAsync(EntryDated(new DateOnly(2024, 1, 15)), Actor); // (approve if the close gate requires it)
    // approve any pending in-period entries so the close gate doesn't block, then:
    await Service.CloseAsync(clientId, new DateOnly(2024, 1, 31), Actor);   // must NOT be "already closed"
    Assert.Equal(new DateOnly(2024, 1, 31), await Checkpoints.GetClosedThroughAsync(clientId));
}

[Fact]
public async Task Opening_balances_remain_empty_after_onboarding()
{
    DateOnly opening = new(2024, 1, 1);
    await Service.OpenAsync(clientId, opening, OpeningLines(), Actor);
    Assert.Empty(await Checkpoints.GetOpeningBalancesAsync(clientId));
}
```

> Implementer: read `OnboardingTests.cs` (and `LedgerServiceTests.cs`) first and reuse their exact harness — the `LedgerService` construction, the `MongoCheckpointStore` accessor, the `OpenAsync` opening-lines builder, and an `EntryDated(date)` balanced-entry helper (one likely already exists; if not, build a minimal balanced two-line entry). For `First_real_close_still_works…`, satisfy the period-close gate (approve any in-period pending entry first) so the test exercises the inception interaction, not the gate.

- [ ] **Step 2: Run, confirm fail** — `Post_dated_before_opening_is_rejected` and `Onboarding_seeds_the_inception_freeze…` FAIL (today no checkpoint is seeded, so a `0001-01-01` post succeeds and `GetClosedThroughAsync` is null).
Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "OnboardingTests"`.

- [ ] **Step 3: Implement** — in `LedgerService.OpenAsync`, after the opening entry is posted and approved (just before `return`), seed the inception checkpoint:

```csharp
JournalEntry approved = await ApproveAsync(opening.Id, actor, cancellationToken);

// Pre-inception is a closed period: seed the freeze one day before the opening date so any post dated
// before the client started is rejected by the same closed-period guard as a normal close. Seeded after
// the opening entry is approved, so the opening entry (dated asOf) is never blocked by its own freeze.
await _checkpoints.SaveAsync(
    clientId, asOf.AddDays(-1), new Dictionary<Guid, decimal>(),
    actor.UserId, DateTimeOffset.UtcNow, session: null, cancellationToken);

return approved;
```

> Confirm `OpenAsync` currently ends with `await PostAsync(opening, …); return await ApproveAsync(opening.Id, …);` and restructure to capture `approved`, seed the checkpoint, then return it. Do not change the opening entry construction, the post, or the approve.

- [ ] **Step 4: Run, confirm pass** — all `OnboardingTests` green.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Mongo/LedgerService.cs Backend/Accounting101.Ledger.Mongo.Tests/OnboardingTests.cs
git commit -m "feat(ledger): seed an inception freeze at onboarding (reject posts dated before the opening date)"
```

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run individually: `OnboardingTests`, plus `PeriodCloseTests` and `LedgerServiceTests` (confirm the new inception checkpoint doesn't regress close/post behavior for clients those tests onboard — they may now have a seeded freeze; adjust only if a test posted a pre-opening date intentionally).
- [ ] Confirm the invoice pre-flight inherits it (no code change needed — `/entries/validate` calls the same guard): a quick check that a Receivables issue with a pre-opening date would now 409 at validate (covered transitively; note it).
- [ ] (small single-method change) the task review is the gate; controller spot-checks before finishing.

## Self-review (author)
- Spec coverage: inception seed (Step 3 + test), pre-opening reject incl. 0001-01-01 (test), on/after-opening allowed (test), opening entry on books (test), first-close still works (test), opening balances unchanged (test).
- Type consistency: `SaveAsync(Guid, DateOnly, IReadOnlyDictionary<Guid,decimal>, Guid, DateTimeOffset, IClientSessionHandle?, CancellationToken)` matches the existing signature; `asOf.AddDays(-1)` is `DateOnly`.
- Open implementer check: whether existing `PeriodCloseTests`/`LedgerServiceTests` onboard via `OpenAsync` and then post pre-opening dates (now rejected) — fix any such test data; flagged in Final verification.
