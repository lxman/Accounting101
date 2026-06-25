# Audit-chain tail-truncation detection — Design

**Date:** 2026-06-25
**Status:** Spec for review
**Context:** `MongoAuditLog.VerifyAsync` walks the hash chain forward from an empty `PreviousHash` to the end of whatever records exist. Deleting the **newest N** records leaves a shorter chain `1..(k−N)` that still links and re-hashes cleanly, so **tail truncation is undetectable** — exactly the records a fraud (or a bad restore) would drop. Mid-chain and head deletions ARE caught (the next record's `PreviousHash` won't match). The walk also never asserts **sequence contiguity**. Reclassified group-1 item from the adversarial review.

## Principle

A tamper-evident log must commit to its own **length**, not just its internal links. `VerifyAsync` today verifies "these records form a valid chain" but not "these are *all* the records." We add a per-client **guarded chain-head** — the expected latest `(Sequence, Hash)` — that `VerifyAsync` must reach, plus a contiguity assertion. Truncating the tail then leaves the walk ending before the head ⇒ detected.

**Honest limit:** the head lives in the same per-client database, so an adversary with full write access to that database could truncate the records *and* rewrite the head consistently. This is not cryptographic tamper-proofing against that adversary — that needs an external/signed anchor (out of scope). What it does close: accidental truncation, a lossy backup/restore, and the *naive* tail-delete — and it raises the bar from "delete records" to "delete records AND forge a consistent head." That is the documented intent of this slice.

## Approach

### Component 1 — the guarded head document

A new `AuditHeadDocument` (collection `audit-head`, one per client) holds the chain's latest position:

```csharp
public sealed class AuditHeadDocument
{
    [BsonId] public Guid ClientId { get; set; }   // one head per client; ClientId is the _id
    public long Sequence { get; set; }            // sequence of the latest appended record
    public string Hash { get; set; } = string.Empty; // that record's hash
}
```

`ClientId` as `_id` gives a natural one-per-client uniqueness.

### Component 2 — `AppendAsync` advances the head atomically with the record

After a record is successfully inserted, the head is updated to that record's `(Sequence, Hash)`. The update must:
- **Join the caller's transaction when a session is present** (the common path: `AppendAsync` is called inside `InTransactionAsync`), so record + head commit together — a crash never leaves the head ahead of or behind the records.
- Be **monotonic**: never regress to a lower sequence. Use a guarded update — replace the head where `ClientId == clientId AND Sequence < newSequence` (upsert), so a late/retried append carrying an older sequence cannot pull the head backward. (On the very first append the upsert creates it.)
- In the standalone (`session is null`) retry path, the head update happens after the winning insert, by the same guarded-monotonic update.

Because appends for a client are already serialized into a linear chain (the unique `(ClientId, Sequence)` index + the re-chain retry), the head tracks the true tail.

### Component 3 — `VerifyAsync` checks length + contiguity, not just links

```
records = all records for client, sorted by Sequence
expectedSeq = 1
previousHash = ""
for record in records:
    if record.Sequence != expectedSeq: return false        // NEW: contiguity (no gaps, starts at 1)
    if record.PreviousHash != previousHash: return false    // linkage (existing)
    if record.Hash != ComputeHash(record): return false     // integrity (existing)
    previousHash = record.Hash
    expectedSeq++

head = head doc for client
// NEW: reconcile against the guarded head — catches tail truncation
if records is empty:
    return head is absent (or head.Sequence == 0)
return head is present
    && head.Sequence == records[^1].Sequence
    && head.Hash == records[^1].Hash
```

- **Tail truncated** (delete newest N): the walk ends at record `k−N`; `head.Sequence` still says `k` ⇒ mismatch ⇒ `false`. ✔ (the gap this fixes)
- **Mid/head deletion:** still caught by linkage/contiguity (unchanged behavior). ✔
- **Sequence gap:** caught by the new contiguity check. ✔
- **Clean chain:** walk reaches the last record, which equals the head ⇒ `true`. ✔

### Component 4 — index/bootstrap

`EnsureIndexesAsync` needs nothing extra for the head (the `_id` on `ClientId` is unique by construction). The audit-record index is unchanged.

## Edge cases

- **First-ever append:** head absent → guarded upsert creates `(1, h1)`. Verify of a one-record chain reconciles. ✔
- **Crash between record insert and head update (non-transactional path only):** the head could lag by one. Mitigation: the head update is part of the same transaction whenever a session is passed (all engine mutation paths use `InTransactionAsync`). For any genuinely standalone append, a lagging head makes Verify return `false` (conservative — it flags a real inconsistency rather than hiding one). Document this; do not paper over it.
- **Reopen/period operations** also append audit records (e.g. `PeriodClosed`); they advance the head like any append — no special-casing.

## Testing (`Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs`, real EphemeralMongo)

- **Tail truncation is now detected (the headline):** append K records (Verify `true`, head == last). Delete the newest 1 and 2 records directly from the `audit` collection. Verify → `false`. (Before this change it returned `true` — the bug.)
- **Clean chain verifies + head matches:** after K appends, Verify `true`, and the head doc's `(Sequence, Hash)` equals the last record's.
- **Mid-deletion still detected** (regression): delete a middle record → `false`.
- **Head/first-record deletion still detected** (regression): delete record #1 → `false`.
- **Sequence gap detected** (contiguity): manually perturb a record's `Sequence` to create a gap → `false`.
- **Head advances monotonically:** after N sequential appends the head is `(N, hash_N)`; a contrived stale head update (Sequence < current) does not regress it.
- **Empty chain:** no records and no head → `true`; no records but a stray head → `false`.
- **Head moves inside a transaction:** an append via a session commits record + head together (assert both present post-commit; abort leaves neither).

## Scope

**In scope:** `AuditHeadDocument`, head maintenance in `AppendAsync` (transactional + monotonic), the contiguity + head reconciliation in `VerifyAsync`, and the tests above. The `GET /audit/verify` endpoint is unchanged (it already calls `VerifyAsync`).

**Out of scope (documented):**
- **Cryptographic tamper-proofing against a full-DB-write adversary** — needs an external/signed head anchor (a control-plane store or a signature over the head). This slice closes accidental/naive truncation and raises the bar; it does not claim unforgeability.
- Repair/forensics of an already-truncated chain.

## Global constraints

- .NET 10; tests use EphemeralMongo (real transactions); build 0 warnings; commit per slice; TDD.
- The head update must be transactional with the record append whenever a session is supplied — record and head commit atomically or not at all.
- Domain-agnostic engine integrity; no policy.
