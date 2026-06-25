# Idempotent entry post — Design

**Date:** 2026-06-25
**Status:** Spec for review
**Context:** The dog-food runs repeatedly produced duplicate journal entries (e.g. `TAXPAY-02` posted twice) when a clerk/module re-ran a partially-failed batch. The engine has no operation-identity on create, so a retry mints a second entry. This adds idempotent retry to `POST /entries` so a re-post of the *same* operation returns the *existing* entry instead of creating a duplicate.

## Principle

Idempotency is **caller-declared, never engine-inferred.** The engine must not look at an entry's content and guess "this looks like a duplicate" — that is duplicate-*detection*, which false-positives on legitimately-identical entries (two payroll runs the same day) and is explicitly out of scope. Instead, eligibility is binary and owned by the caller:

> **An entry is idempotency-eligible iff the caller supplied an explicit `Id`.**

- Caller supplies `Id` → "I am asserting this is operation X." A re-post with the same `Id` is a retry → return the existing entry.
- Caller omits `Id` → the engine generates a fresh `Guid` (`MapEntry`: `request.Id ?? Guid.NewGuid()`) → always unique, no dedup. **This is today's behavior, preserved** — idempotency is strictly opt-in.

The engine honors the identity the caller declares; it never determines eligibility from the entry's lines, amounts, or dates.

## What already exists (so this is one branch, not a subsystem)

- `PostEntryRequest.Id` is a nullable client-supplied `Guid`.
- `JournalEntryDocument.Id` is `[BsonId]` — the entry `Id` **is** the Mongo `_id`, unique by construction.
- `MongoJournalStore.AppendAsync` is a plain `InsertOneAsync` → a duplicate `_id` raises `MongoWriteException` (DuplicateKey / E11000). `WithTransactionAsync` does **not** retry it (not a transient label), so it propagates out of `PostAsync` to the endpoint.
- `PostEntry` (`LedgerEndpoints.cs:78`) already catches it: `catch (MongoWriteException … DuplicateKey) → Conflict("An entry with this id or sequence number already exists.")`.
- `MongoJournalStore.GetAsync(Guid id)` fetches an entry by id.
- The sequence `$inc` happens inside the post transaction *before* the insert, so a colliding insert aborts the transaction and **rolls back the `$inc`** — no wasted sequence number, sequence stays gapless.

This design replaces only the body of that one catch.

## Approach

In `PostEntry`, when a post fails with a DuplicateKey, resolve it by the supplied id rather than blanket-409:

```csharp
catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
{
    // Idempotency is opt-in: only an explicit, caller-supplied id can collide on _id.
    if (request.Id is { } suppliedId
        && await ctx.Ledger!.Service.GetEntryAsync(clientId, suppliedId, cancellationToken) is { } existing)
    {
        // Same client + same financial content => idempotent replay of the same operation.
        if (IsSameEntry(existing, entry!))
            return Results.Ok(new PostEntryResponse(existing.Id, existing.Status.ToString(), existing.Posting.ToString()));

        // Same id, different content => the caller reused an operation id for a different entry. A bug, surfaced.
        return Unprocessable("An entry with this id already exists with different content.");
    }

    // No entry for this id under this client => a sequence-number collision (a real, rare conflict).
    return Conflict("An entry with this sequence number already exists.");
}
```

### Eligibility & resolution rules (the decision table)

On a DuplicateKey, given the request's `clientId` and supplied `Id`:

| Existing entry for `Id` (in this client's store)? | Belongs to this client? | Financial content matches? | Result |
|---|---|---|---|
| no | — | — | `409` sequence-number collision (real conflict, unchanged) |
| yes | **no** (different client) | — | `409` "an entry with this id already exists" — **never return another client's entry** |
| yes | yes | **yes** | `200` idempotent replay → return the existing entry |
| yes | yes | **no** | `422` "id reused with different content" |

**Tenancy correction (verified during implementation):** Accounting101 uses a **per-tenant database** — each client's journal lives in its own MongoDB database, so `_id` uniqueness is **per-client, not global**, and a cross-client `_id` collision *cannot structurally occur* (client B inserting a `Guid` that client A used just creates B's own entry in B's database — no DuplicateKey). The "different client → 409" row above is therefore unreachable in this architecture. The `existing.ClientId == clientId` check is **still retained as defense-in-depth** — it is the one guard standing between a future shared-store refactor and a cross-tenant leak, so it must not be removed as "dead code." Accordingly the cross-client *test* proves **tenant isolation** (B's replay resolves to B's own entry, never A's) rather than a 409 the architecture prevents.

### `GetEntryAsync` on the service

`LedgerService` exposes a thin `GetEntryAsync(Guid clientId, Guid entryId, CancellationToken)` that returns the entry only when it belongs to the client (wraps `_journal.GetAsync`, returns null on a client mismatch), so the endpoint never has to reach past the service or risk a cross-client read. (The existing `GetEntry` query endpoint already client-scopes its read; reuse that scoping.)

### `IsSameEntry` — the financial fingerprint

Compares the just-mapped `entry` against the stored `existing` on the fields that define the operation's financial substance, ignoring engine-assigned and cosmetic fields:

- **Compared:** `EffectiveDate`, `EntryType`, `SourceRef`, `SourceType`, and the **ordered** `Lines` (each: `AccountId`, `Direction`, `Amount`, and the `Dimensions` map).
- **Ignored:** `Id` (equal by definition), `SequenceNumber`/`PostedAt`/audit stamps (engine-assigned), `Status`/`Posting` (lifecycle — a replay after approval still matches), `Reference`/`Memo` (descriptive, not financial).

A pure function on `JournalEntry`, unit-testable without Mongo.

## Status code & client contract

- Fresh create → `201 Created` (unchanged).
- Idempotent replay → `200 OK` with the **same `PostEntryResponse` shape** (id, status, posting), reflecting the existing entry's current lifecycle state.

A `200`-vs-`201` split lets a caller tell a replay from a create, but both are success. `HttpLedgerClient` already treats any 2xx as success (`EnsureSuccessAsync`), so module callers need no change to *consume* idempotent replays.

## Edge interactions (all fall out cleanly)

- **Gapless sequence:** the colliding insert's `$inc` rolls back with the aborted transaction — no gap, no wasted number.
- **Freeze-safe:** the replay performs **no write**, so retrying a post whose effective date is now in a closed period returns the existing entry (`200`), never a freeze violation.
- **Approval-safe:** the replay returns the entry in whatever lifecycle state it reached (PendingApproval or Posted); it never creates a second entry to approve.
- **Concurrent identical retries:** both run the post; one wins the insert, the other gets DuplicateKey → resolves to the idempotent `200`. (The sequence-counter fence already serializes the two transactions per client.)

## The caller contract (what realizes the benefit — see Scope)

The engine primitive is inert unless callers derive `Id` **deterministically from the operation**, so a retry re-produces the same `Id`. A caller that does `Guid.NewGuid()` per attempt has, by its own declaration, created a different operation — nothing can dedup it.

**The eligibility trap:** the key is **operation-scoped, not document-scoped.** You cannot key on `(SourceType, SourceRef)` alone — that back-link is not unique: a revise leaves the superseded original *plus* the replacement, and a reversal shares the same `SourceRef`. Those are different operations on one document. So a module derives, e.g., `Id = UUIDv5(clientId, sourceType, sourceRef, purpose)` where `purpose` ∈ {original-post, revise, reverse}; a batch importer uses its stable per-line key.

## Testing

Engine (`Accounting101.Ledger.Api.Tests` for the endpoint behavior; pure-function tests for `IsSameEntry`):

- **Replay returns existing, creates nothing:** POST a balanced entry with explicit `Id` → `201`. POST the identical request again → `200`, same entry id; assert the journal holds exactly one entry and the sequence counter did not advance (gapless).
- **Replay after approval:** post with `Id`, approve it, re-post the same request → `200` returning the *Posted* entry; still one entry.
- **Replay after the period closed:** post with `Id` (open period), close the period, re-post the same request → `200` (no freeze 409, no second entry).
- **Same id, different content → 422:** post with `Id`, then post the same `Id` with a changed amount/line → `422` "different content"; the original is untouched.
- **Cross-client id collision → 409, no leak:** client A posts with `Id` X; client B posts with `Id` X (B authorized on its own client) → `409`; B does **not** receive A's entry.
- **Opt-out preserved:** posting the same body twice **without** an `Id` → two distinct entries (no dedup).
- **Sequence-collision path still 409:** (unit/targeted) a DuplicateKey with no entry for the supplied id maps to the sequence-collision `409`.
- `IsSameEntry`: matches on reordered-but-equal? No — lines are compared in order (document order is stable per construction); matches identical, differs on any financial field; ignores Reference/Memo/lifecycle.

## Scope

**In scope:** the `PostEntry` idempotent-resolution branch, `LedgerService.GetEntryAsync` (client-scoped), the `IsSameEntry` fingerprint, the `200`-on-replay response, and the tests above. Domain-agnostic; no module changes.

**Out of scope (explicit follow-ups, each its own slice):**
- **Module/importer `Id` derivation** — Receivables (invoice-issue post), Payables (bill-payment post), and any batch importer deriving stable operation-scoped ids. *This is what actually prevents the dog-food duplicates;* the engine primitive is the foundation it builds on.
- **Bill-creation idempotency** in the Payables document store (`POST /bills`) — the duplicate-bill path behind the month-8 "double-pay." Mirrors this design at the module's document store.
- The entries-list pending-only filter / the dead `?reference=` filter (separate read-side slice).

## Global constraints

- .NET 10; tests use EphemeralMongo (real transactions); build 0 warnings; commit per slice; TDD.
- The engine enforces only irreducible invariants — this exposes the existing `_id` uniqueness as a safe-retry contract; it adds no new policy and no content-based "duplicate detection." It is domain-agnostic and host-policy free.
