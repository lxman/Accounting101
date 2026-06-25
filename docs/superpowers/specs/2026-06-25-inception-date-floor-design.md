# Inception date floor — Design

**Date:** 2026-06-25
**Status:** Spec for review
**Context:** Run-3 of the dog-food sim surfaced a real statement-integrity gap: an uncoached clerk fat-fingered an invoice's date field, so the entry posted with `effectiveDate = 0001-01-01`. In month 1 (before any period close) the pre-flight let it through — it rejects *closed* periods, but there is **no lower bound** on the effective date. The balance-based reconciler passed it, but the auditor found the income statement (period-windowed) and balance sheet (as-of) diverge by exactly the mis-dated revenue: the books don't tie. Root cause: **a post can be dated arbitrarily far in the past.**

## Principle

The user's framing: *anything before the client's opening-balance date is, by definition, a closed period* — there is no "open" time before the business started. So we don't add a new validation rule; we **reuse the existing closed-period freeze**. The engine already rejects `effectiveDate <= closedThrough` (`LedgerService.EnsureOpenForPostAsync`). We make pre-inception fall under that same guard by **seeding the freeze at onboarding**.

## Approach

When a client is onboarded, `LedgerService.OpenAsync(clientId, asOf, lines, actor)` establishes the opening balances as a single `Opening` entry dated `asOf` (the cutover/opening date), posts it, and approves it. After that, **write the period checkpoint with `closedThrough = asOf − 1 day`** (and empty balances). From then on the existing freeze guard rejects anything dated before the opening date.

### The off-by-one (the one detail to get right)

The opening entry and the first real transactions are dated **on** `asOf`. We must **allow `>= asOf`** and **reject `< asOf`**. The guard rejects `effectiveDate <= closedThrough`, so:

- `closedThrough = asOf.AddDays(-1)` → rejects `<= (asOf − 1)`, i.e. strictly **before** `asOf` (including `0001-01-01`); allows `asOf` and later. ✔
- The opening entry itself (dated `asOf`) is on the books — it is posted+approved **before** the checkpoint is written, so its own freeze never blocks it. ✔
- The first real monthly close still works: `CloseAsync(asOf = 2024-01-31)` checks `asOf <= closedThrough` → `2024-01-31 <= 2023-12-31` is false → not "already closed". ✔

### Component — `OpenAsync` seeds the inception checkpoint

After the opening entry is approved, before returning:

```csharp
// Pre-inception is a closed period: seed the freeze one day before the opening date so any post dated
// before the client started is rejected by the same closed-period guard as a normal close.
await _checkpoints.SaveAsync(
    clientId,
    asOf.AddDays(-1),
    new Dictionary<Guid, decimal>(),   // no balances before inception
    actor.UserId,
    DateTimeOffset.UtcNow,
    session: null,
    cancellationToken);
```

(Records an inception freeze for the client; idempotent via the one-checkpoint-per-client upsert. No audit `PeriodClosed` action is logged — this is inception, not a close; if a marker is wanted later, that's additive.)

### Why nothing else breaks

- **`GetClosedThroughAsync`**: was `null` for a fresh client, now `asOf − 1`. That is the *desired* freeze; consumers are `EnsureOpenForPostAsync` (now enforces the floor — the point), `CloseAsync`'s already-closed check (still passes for the first real close, shown above), and `ReopenAsync` (a fresh client could now be "reopened" past inception — an admin+step-up operation, vanishingly rare, acceptable; out of scope to special-case).
- **`GetOpeningBalancesAsync`**: returned an **empty** dict for a fresh client (no checkpoint) and still returns empty (the inception checkpoint carries empty balances). The real opening position lives in the opening *entry*, unchanged. **No reporting change.**
- **The close gate / audit chain** (recent merges) are unaffected — they key off the same checkpoint/append paths, which behave normally.

## Effect on the bug

A `0001-01-01` post (the run-3 footgun) is now rejected **from day one** — even in month 1, before any real close — with the existing closed-period 409/`InvalidOperationException`. The invoice pre-flight (`/entries/validate`) inherits it for free (it calls the same guard), so a fat-fingered invoice date is caught at *issue* while the invoice is still a Draft, exactly like a closed-period typo. The statement divergence cannot arise because the mis-dated entry never posts.

## Error message (small nicety)

The guard's message is "Period is closed through `2023-12-31`; entry dated `0001-01-01` is in a closed period." That is accurate and tells the clerk the floor. A friendlier inception-specific wording ("… is before the client's opening date `2024-01-01`") would require distinguishing the inception floor from a real close (e.g. storing the opening date) — **deferred as out-of-scope polish**; the generic message is correct and actionable.

## Testing

Engine (`Accounting101.Ledger.Mongo.Tests` — `OnboardingTests` is the natural home; real EphemeralMongo):

- **Inception freeze is set:** after `OpenAsync(asOf = 2024-01-01, …)`, `GetClosedThroughAsync` returns `2023-12-31`.
- **Pre-inception post rejected (the headline):** posting an entry dated `0001-01-01` (and one dated `2023-12-31`) → `InvalidOperationException` mentioning "closed". This FAILS before the fix (it currently succeeds).
- **On-the-opening-date post allowed:** an entry dated `2024-01-01` posts fine.
- **After-opening post allowed:** an entry dated `2024-01-15` posts fine.
- **Opening entry is on the books:** the opening entry (dated `2024-01-01`) is `Active`/`Posted` after onboarding (the inception checkpoint did not block it).
- **First real close still works:** `CloseAsync(2024-01-31)` succeeds (not "already closed"); a `2024-02` post afterward still works, a `2024-01` post afterward is now closed.
- **Opening balances unchanged:** `GetOpeningBalancesAsync` returns empty after onboarding (as before).

API (`Accounting101.Ledger.Api.Tests`, if the onboarding harness is readily available):
- Onboard a client at `2024-01-01`, then `POST /entries` (or `/entries/validate`) dated `0001-01-01` → `409` closed-period. (If the onboarding API test setup is heavy, the engine-level coverage above is sufficient; note the decision.)

## Scope

**In scope:** seeding the inception checkpoint in `OpenAsync`; the tests above. No new field, no new endpoint, no message-distinguishing state.

**Out of scope (documented):**
- A friendlier inception-specific rejection message (needs the opening date stored to distinguish floor vs close).
- An **upper / future** date bound — deliberately not added; post-dated accruals can be legitimate.
- Repairing the already-mis-dated month-1 entries in any historical run data (those runs are throwaway sim state).

## Global constraints

- .NET 10; tests use EphemeralMongo; build 0 warnings; commit per slice; TDD.
- Reuse the existing closed-period guard — add no parallel validation rule. The opening entry must remain postable (checkpoint seeded after it is approved).
- Domain-agnostic engine integrity; no policy.
