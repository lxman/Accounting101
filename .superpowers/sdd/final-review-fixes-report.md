# Final Review Fixes Report — feat/period-close-pending-gate

**Date:** 2026-06-25
**Branch:** feat/period-close-pending-gate
**Commit:** 32733fa
**Status:** All three findings fixed; build 0 warnings; all tests green.

---

## Finding I-1 — Pre-seeded counter path in ConcurrencyTests.cs

**File:** `Backend/Accounting101.Ledger.Mongo.Tests/ConcurrencyTests.cs`

**Problem:** The existing close-vs-backdated-post race test used a brand-new client on every iteration. A fresh client hits the INSERT path in the sequence store's counter document. The production-common case — an UPDATE on an already-existing counter document — was never exercised.

**Fix:** Added `Concurrent_close_and_backdated_post_never_strand_with_preseeded_counter`. Before the race, the test posts AND approves an out-of-period entry dated 2024-08-01 (future, open month) so the `journal:{clientId}` counter document exists. The 20-iteration race then follows the same exactly-one-winner invariant as the sister test.

**Outcome:** New test passed on both required runs (6/6 ConcurrencyTests green each time). No real failures detected; the existing-document UPDATE path is safe.

---

## Finding T3 — Tightened snapshot assertion in PeriodCloseTests.cs

**File:** `Backend/Accounting101.Ledger.Mongo.Tests/PeriodCloseTests.cs`
**Test:** `Blocked_close_is_resolved_by_approving_the_blocker_then_reclosing`

**Problem:** The assertion `snapshot.ContainsKey(a) || snapshot.ContainsKey(b)` would pass even if only one account appeared in the snapshot, masking a half-balanced entry.

**Fix:** Changed `||` to `&&`. A balanced double-entry must touch both accounts; both must be present in the period-end snapshot.

**Outcome:** 11/11 PeriodCloseTests green.

---

## Finding T4 — Named arguments in PeriodCloseApiTests.cs

**File:** `Backend/Accounting101.Ledger.Api.Tests/PeriodCloseApiTests.cs`

**Problem:** `PostEntryRequest` was constructed with positional `null`s. If the record's parameter order changes, the test silently shifts meaning with no compile error.

**Fix:** Switched to named arguments — `Id: null`, `EffectiveDate: new DateOnly(2024, 6, 30)`, `Reference: null`, `Memo: null`, `Lines: [...]`. Same runtime values; order-shift-safe.

**Outcome:** 1/1 PeriodCloseApiTests green.

---

## Build

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

`-warnaserror` confirmed: no suppressed warnings.

---

# Audit-Chain Tail-Truncation Fixes — feat/audit-chain-tail-truncation

**Date:** 2026-06-25
**Branch:** feat/audit-chain-tail-truncation
**Status:** All three fixes applied; build 0 warnings; all tests green.

---

## Fix 1 — Narrow the head-advance API surface

**Files changed:**
- `Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs`
- `Backend/Accounting101.Ledger.Mongo/Accounting101.Ledger.Mongo.csproj`

`AdvanceHeadAsync` and `FindHeadAsync` changed from `public` to `internal`. These are implementation details of the audit log machinery — `AdvanceHeadAsync` is called only from within `AppendAsync`, and `FindHeadAsync` is called only from `VerifyAsync`. Neither belongs on the public surface of the class.

**InternalsVisibleTo mechanism:** Added `<InternalsVisibleTo Include="Accounting101.Ledger.Mongo.Tests" />` as an `<ItemGroup>` element in the `.csproj` file. No existing `InternalsVisibleTo` usage was found in the repo, so either approach was valid; the csproj element was chosen to keep assembly attributes out of the source files.

The test in `AuditHeadTests.Head_does_not_regress_on_a_stale_update` calls `AdvanceHeadAsync` directly. With `InternalsVisibleTo` wired, that call compiles and passes as before.

---

## Fix 2 — Pin hash-linkage mid-deletion guarantee with a dedicated test

**File:** `Backend/Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs`
**Test added:** `Mid_deletion_is_detected_by_hash_linkage_even_when_contiguous` (in `AuditLogTests`)

**Approach:**
1. Append 3+ records via `service.PostAsync` / `ApproveAsync` so the chain has a contiguous seq 1..N.
2. Delete the middle record (index 1 of the sorted list, seq 2) directly from the raw `audit` collection.
3. Renumber the surviving later records' `Sequence` values to fill the gap — each survivor after the deleted middle gets its sequence decremented by 1 so the remaining chain reads 1..N-1, making it appear contiguous.
4. Assert `VerifyAsync` returns `false`.

**Why this test would be RED without the hash check:** After renumbering, the sequence values are consecutive (no gap), so the contiguity check (`record.Sequence != expectedSeq`) passes for every surviving record. The only thing that catches the tamper is the `PreviousHash` chain: seq 3's `PreviousHash` points to the deleted record's hash, not to seq 1's hash. Because seq 3 is now renumbered to seq 2, the check `record.PreviousHash != previousHash` fails immediately.

**Why it is GREEN as-is:** `VerifyAsync` verifies BOTH `record.Sequence == expectedSeq` AND `record.PreviousHash == previousHash` AND `record.Hash == ComputeHash(record)`. The hash linkage catches the renumbered seq-3 record because its stored `PreviousHash` is the deleted record's hash, which does not match the hash of the true seq-1 record.

---

## Fix 3 — Clarify in-transaction DuplicateKey comment

**File:** `Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs` — `AdvanceHeadAsync`'s DuplicateKey catch block.

Added two lines of comment explaining that inside a transaction the single-advance-per-append invariant means the insert-collision branch is not reached on the normal path, and that a future change causing two advances per transaction would turn the DuplicateKey into a transaction abort (not a silent no-op).

---

## Build Verification

```
dotnet build Backend/Accounting101.Ledger.Mongo/Accounting101.Ledger.Mongo.csproj -warnaserror
Build succeeded.
    0 Warning(s)
    0 Error(s)

dotnet build Backend/Accounting101.Ledger.Mongo.Tests/Accounting101.Ledger.Mongo.Tests.csproj -warnaserror
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Test Results

```
dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "AuditLogTests"
Passed!  - Failed: 0, Passed: 10, Skipped: 0, Total: 10

dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "AuditHeadTests"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3
```
