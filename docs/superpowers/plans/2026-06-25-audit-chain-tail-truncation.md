# Audit-chain tail-truncation detection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make `VerifyAsync` detect tail truncation of a client's audit chain (and sequence gaps), by maintaining a per-client guarded chain-head and reconciling the walked chain against it.

**Architecture:** A `audit-head` collection (one `AuditHeadDocument` per client, `_id = ClientId`) holds the latest `(Sequence, Hash)`. `AppendAsync` advances it monotonically, transactionally with the record insert when a session is present. `VerifyAsync` adds a contiguity check and a final reconciliation against the head.

**Tech Stack:** C#/.NET 10, MongoDB (replica-set transactions), xUnit + EphemeralMongo.

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- The head update must JOIN the caller's transaction when a session is supplied (record + head commit atomically). It must be MONOTONIC (never regress to a lower sequence).
- Tests use EphemeralMongo (real transactions); run the `AuditLogTests` class on its own when verifying.
- Do not change the `GET /audit/verify` endpoint (it already calls `VerifyAsync`) or the `ComputeHash` content (changing it would invalidate existing chains).
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit file lists; check for stray churn.

---

## Task 1: `AuditHeadDocument` + `AppendAsync` maintains the head

**Files:**
- Create: `Backend/Accounting101.Ledger.Mongo/Documents/AuditHeadDocument.cs`
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs` (constructor gets the `audit-head` collection; `AppendAsync` advances the head)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs` (add head-maintenance cases)

**Interfaces:**
- Produces (consumed by Task 2): a per-client head doc keyed by `ClientId`, updated to the latest `(Sequence, Hash)` on every append; a private `Task<AuditHeadDocument?> FindHeadAsync(Guid clientId, IClientSessionHandle? session, CancellationToken)` (or equivalent read) Task 2 uses in `VerifyAsync`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Append_advances_the_head_to_the_latest_record()
{
    // append 3 records for a client via AppendAsync (use the same call shape existing AuditLogTests use,
    // wrapping in a transaction/session if the existing tests do)
    // read the audit-head doc for the client; assert Sequence == 3 and Hash == the 3rd record's Hash
}

[Fact]
public async Task Head_does_not_regress_on_a_stale_update()
{
    // after N appends (head at N), directly invoking the guarded head update with a lower sequence
    // (or a contrived stale path) must NOT lower the head below N.
}

[Fact]
public async Task Append_and_head_commit_together_in_a_transaction()
{
    // an append performed inside a session/transaction: after commit, both the record AND the head exist;
    // if the transaction is aborted, NEITHER exists. (Mirror how existing tests drive a session.)
}
```

> Implementer: match how the EXISTING `AuditLogTests` construct `MongoAuditLog`, call `AppendAsync`, and drive sessions/transactions (read the file first). Reuse that harness.

- [ ] **Step 2: Run, confirm fail** (`dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "AuditLogTests"` — new tests fail; the head doesn't exist yet).

- [ ] **Step 3: Implement**

`AuditHeadDocument.cs`:
```csharp
using MongoDB.Bson.Serialization.Attributes;
namespace Accounting101.Ledger.Mongo.Documents;

/// <summary>Per-client guarded chain-head: the expected latest (Sequence, Hash) of the audit chain.
/// VerifyAsync reconciles the walked chain against this so a truncated tail is detected.</summary>
public sealed class AuditHeadDocument
{
    [BsonId] public Guid ClientId { get; set; }
    public long Sequence { get; set; }
    public string Hash { get; set; } = string.Empty;
}
```

`MongoAuditLog`:
- Constructor: `_head = database.GetCollection<AuditHeadDocument>("audit-head");`
- After a successful record insert in `AppendAsync` (both the `session` and standalone branches), advance the head with a guarded-monotonic upsert. Implement an `AdvanceHeadAsync(Guid clientId, long sequence, string hash, IClientSessionHandle? session, CancellationToken)`:
  ```csharp
  FilterDefinition<AuditHeadDocument> filter = Builders<AuditHeadDocument>.Filter.And(
      Builders<AuditHeadDocument>.Filter.Eq(h => h.ClientId, clientId),
      Builders<AuditHeadDocument>.Filter.Lt(h => h.Sequence, sequence)); // only advance
  UpdateDefinition<AuditHeadDocument> update = Builders<AuditHeadDocument>.Update
      .SetOnInsert(h => h.ClientId, clientId)
      .Set(h => h.Sequence, sequence)
      .Set(h => h.Hash, hash);
  var opts = new UpdateOptions { IsUpsert = true };
  // session-aware: pass session when non-null, same pattern as the record insert
  ```
  > Subtlety: an upsert whose filter includes `Sequence < N` will, when a head with `Sequence >= N` already exists, fail to match and ATTEMPT AN INSERT (which collides on the `_id = ClientId` unique key → DuplicateKey). Handle that: catch the DuplicateKey from the head upsert and treat it as "head already at/ahead of N" (a no-op) — the monotonic guarantee holds. Verify this interaction in the `Head_does_not_regress_on_a_stale_update` test. (Alternatively, structure the update so a no-advance is a clean no-op without an exception — implementer's choice, but it must be monotonic and must not throw on the normal path.)
- Call `AdvanceHeadAsync` after each successful insert, using the SAME `session` so it joins the transaction when present.

- [ ] **Step 4: Run, confirm pass** → PASS.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Mongo/Documents/AuditHeadDocument.cs Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs Backend/Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs
git commit -m "feat(ledger): maintain a per-client guarded audit chain-head on every append"
```

---

## Task 2: `VerifyAsync` — contiguity + head reconciliation (detects truncation)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs` (`VerifyAsync`)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs` (the truncation/contiguity cases)

**Interfaces:**
- Consumes: the head maintained in Task 1.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Tail_truncation_is_detected()
{
    // append K (>=3) records -> VerifyAsync true.
    // delete the newest record directly from the audit collection (NOT via AppendAsync) -> VerifyAsync false.
    // (also test deleting the newest 2.)
}

[Fact]
public async Task Clean_chain_verifies_true() { /* K appends -> true */ }

[Fact]
public async Task Mid_deletion_is_detected() { /* delete a middle record -> false (regression) */ }

[Fact]
public async Task First_record_deletion_is_detected() { /* delete record #1 -> false */ }

[Fact]
public async Task Sequence_gap_is_detected() { /* perturb a record's Sequence to create a gap -> false */ }

[Fact]
public async Task Empty_chain_with_no_head_verifies_true() { /* no records, no head -> true */ }
```

> Implementer: to simulate tampering, delete/modify documents directly via the raw `audit` collection (the test already has the `IMongoDatabase`), NOT via `AppendAsync` — you are emulating an out-of-band edit. The head doc is left untouched (that's the point — the head still says length K).

- [ ] **Step 2: Run, confirm fail** — `Tail_truncation_is_detected` FAILS against the current `VerifyAsync` (it returns true for a truncated chain); contiguity/empty tests may also fail.

- [ ] **Step 3: Implement** — rewrite `VerifyAsync` per the spec:
  - Walk records sorted by `Sequence`, asserting: `record.Sequence == expectedSeq` (start 1, increment), `record.PreviousHash == previousHash`, `record.Hash == ComputeHash(record)`.
  - After the walk, read the head and reconcile: empty records ⇒ head absent (or `Sequence == 0`) ⇒ true; non-empty ⇒ head present AND `head.Sequence == records[^1].Sequence` AND `head.Hash == records[^1].Hash`.
  - Return false on any mismatch.

- [ ] **Step 4: Run, confirm pass** → all AuditLogTests green (including the regressions and the new truncation detection).

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs Backend/Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs
git commit -m "feat(ledger): VerifyAsync detects tail truncation and sequence gaps via the guarded head"
```

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run `AuditLogTests` individually → all green (esp. `Tail_truncation_is_detected`).
- [ ] Confirm `ComputeHash` content string is unchanged (no existing-chain invalidation) and `GET /audit/verify` is untouched.
- [ ] Whole-branch review on the most capable model (this is an integrity-critical change), then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- Spec coverage: head doc + monotonic transactional maintenance (Task 1); contiguity + head reconciliation + truncation detection (Task 2); regressions for mid/head deletion preserved.
- Type consistency: `AuditHeadDocument{ClientId,Sequence,Hash}`; `AdvanceHeadAsync(Guid,long,string,session,ct)`.
- Open implementer check (flagged): the guarded-upsert DuplicateKey interaction when the head is already at/ahead of N (Task 1 Step 3) — must be monotonic and must not throw on the normal path.
