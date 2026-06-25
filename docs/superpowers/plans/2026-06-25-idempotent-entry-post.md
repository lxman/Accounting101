# Idempotent entry post — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `POST /entries` idempotent on a caller-supplied `Id` — a re-post of the same operation returns the existing entry (200) instead of creating a duplicate (or erroring 409).

**Architecture:** Idempotency is opt-in via the existing client-supplied `PostEntryRequest.Id` (which is the Mongo `[BsonId]` `_id`). A duplicate `_id` already raises DuplicateKey and propagates to the `PostEntry` endpoint; we replace that catch so it resolves the supplied id to the existing entry, returning `200` on a content match, `422` on an id reused with different content, and `409` only for a true sequence-number collision or a cross-client id clash. A pure `SameFinancialContent` fingerprint decides "same operation."

**Tech Stack:** C#/.NET 10, MongoDB, xUnit + EphemeralMongo (endpoint tests), pure unit tests (fingerprint).

## Global Constraints

- .NET 10; build **0 warnings**; commit per task; TDD (red → green → commit).
- Idempotency is **opt-in**: omitting `Id` must behave exactly as today (engine generates a fresh `Guid`, no dedup). Do not change the no-Id path.
- The engine must **never** infer eligibility from entry content — only a caller-supplied `Id` is eligible. No content-based "duplicate detection."
- **Cross-client safety:** `_id` uniqueness is global; the idempotent path must verify the existing entry belongs to the request's `clientId` before returning it — never return another client's entry.
- Gapless sequence is preserved (the colliding insert's `$inc` rolls back with the aborted transaction — do not add a pre-check that allocates a number).
- Tests use EphemeralMongo (real transactions); run a single test class at a time when verifying (host-boot/Mongo classes flake together).
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage an explicit file list per commit; check for stray churn (the IDE linter may rewrite types to `var`).

---

## Task 1: `SameFinancialContent` — the operation fingerprint

**Files:**
- Create: `Backend/Accounting101.Ledger.Core/Journal/EntryComparison.cs`
- Test: `Backend/Accounting101.Ledger.Core.Tests/EntryComparisonTests.cs`

**Interfaces:**
- Produces (consumed by Task 2): `public static bool Accounting101.Ledger.Core.Journal.EntryComparison.SameFinancialContent(JournalEntry a, JournalEntry b)`

**Background:** Compares two entries on financial substance only. Compared: `EffectiveDate`, `Type` (EntryType), `SourceRef`, `SourceType`, and the **ordered** `Lines` (each line's `AccountId`, `Direction`, `Amount`, and `Dimensions` map — compare the dimension maps key-for-key). Ignored: `Id`, `SequenceNumber`, `PostedAt`, audit stamps, `Status`, `Posting`, `Reference`, `Memo`. (Confirm `JournalEntry`/`Line` property names against `Backend/Accounting101.Ledger.Core/Journal/JournalEntry.cs` — `Line` has `AccountId`, `Direction`, `Amount`, `Dimensions`.)

- [ ] **Step 1: Write the failing tests**

```csharp
public class EntryComparisonTests
{
    // helper builds a JournalEntry via JournalEntry.Create with given date/lines/sourceRef/type
    [Fact]
    public void Identical_entries_match()
    {
        JournalEntry a = Build(date: new(2024,6,30), lines: Pair(Acct1, Acct2, 100m));
        JournalEntry b = Build(date: new(2024,6,30), lines: Pair(Acct1, Acct2, 100m));
        Assert.True(EntryComparison.SameFinancialContent(a, b));
    }

    [Fact]
    public void Differing_amount_does_not_match()
    {
        Assert.False(EntryComparison.SameFinancialContent(
            Build(lines: Pair(Acct1, Acct2, 100m)),
            Build(lines: Pair(Acct1, Acct2, 101m))));
    }

    [Fact]
    public void Differing_effective_date_does_not_match() { /* same lines, dates 6-30 vs 7-01 => false */ }

    [Fact]
    public void Differing_source_ref_does_not_match() { /* same lines, sourceRef X vs Y => false */ }

    [Fact]
    public void Differing_dimensions_do_not_match() { /* a line with {Customer:G1} vs {Customer:G2} => false */ }

    [Fact]
    public void Reference_and_memo_are_ignored()
    {
        JournalEntry a = Build(lines: Pair(Acct1, Acct2, 100m), reference: "R-1", memo: "first");
        JournalEntry b = Build(lines: Pair(Acct1, Acct2, 100m), reference: "R-2", memo: "second");
        Assert.True(EntryComparison.SameFinancialContent(a, b)); // descriptive fields don't define the operation
    }

    [Fact]
    public void Lifecycle_state_is_ignored()
    {
        JournalEntry pending = Build(lines: Pair(Acct1, Acct2, 100m));
        JournalEntry posted  = pending.Approve(SomeUserId); // same financial content, Posted
        Assert.True(EntryComparison.SameFinancialContent(pending, posted));
    }
}
```

- [ ] **Step 2: Run, confirm they fail** (type/method missing)
Run: `dotnet test Backend/Accounting101.Ledger.Core.Tests --filter "EntryComparisonTests"`
Expected: FAIL (does not compile).

- [ ] **Step 3: Implement**

```csharp
// Backend/Accounting101.Ledger.Core/Journal/EntryComparison.cs
namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// Compares two entries on financial substance — the fields that define "the same operation" for
/// idempotent retry. Ignores engine-assigned (sequence/posted-at/audit), lifecycle (status/posting),
/// and descriptive (reference/memo) fields. Used by idempotent post to decide whether a re-post of an
/// existing id is a true replay (return the existing entry) or an id reused for different content.
/// </summary>
public static class EntryComparison
{
    public static bool SameFinancialContent(JournalEntry a, JournalEntry b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.EffectiveDate != b.EffectiveDate) return false;
        if (a.Type != b.Type) return false;
        if (a.SourceRef != b.SourceRef) return false;
        if (!string.Equals(a.SourceType, b.SourceType, StringComparison.Ordinal)) return false;
        if (a.Lines.Count != b.Lines.Count) return false;

        for (int i = 0; i < a.Lines.Count; i++)
        {
            Line la = a.Lines[i], lb = b.Lines[i];
            if (la.AccountId != lb.AccountId) return false;
            if (la.Direction != lb.Direction) return false;
            if (la.Amount != lb.Amount) return false;
            if (!SameDimensions(la.Dimensions, lb.Dimensions)) return false;
        }
        return true;
    }

    private static bool SameDimensions(
        IReadOnlyDictionary<string, Guid>? x, IReadOnlyDictionary<string, Guid>? y)
    {
        int xc = x?.Count ?? 0, yc = y?.Count ?? 0;
        if (xc != yc) return false;
        if (xc == 0) return true;
        foreach (KeyValuePair<string, Guid> kv in x!)
            if (!y!.TryGetValue(kv.Key, out Guid v) || v != kv.Value) return false;
        return true;
    }
}
```

> Implementer: confirm the actual type of `Line.Dimensions` on `JournalEntry`/`Line` (it may be a list of dimension records rather than a `IReadOnlyDictionary<string,Guid>`). Match the real shape — the contract is "same set of (type → value) tags." Adjust `SameDimensions` to the real representation; keep the semantics.

- [ ] **Step 4: Run, confirm pass**
Run: `dotnet test Backend/Accounting101.Ledger.Core.Tests --filter "EntryComparisonTests"` → PASS.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Core/Journal/EntryComparison.cs Backend/Accounting101.Ledger.Core.Tests/EntryComparisonTests.cs
git commit -m "feat(ledger): SameFinancialContent fingerprint for idempotent entry replay"
```

---

## Task 2: Client-scoped `GetEntryAsync` + idempotent `PostEntry` branch

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/LedgerService.cs` (add `GetEntryAsync`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`PostEntry` catch)
- Test: `Backend/Accounting101.Ledger.Api.Tests/IdempotentPostTests.cs` (create)

**Interfaces:**
- Consumes: `EntryComparison.SameFinancialContent` (Task 1); existing `MongoJournalStore.GetAsync(Guid id)`.
- Produces: `LedgerService.GetEntryAsync(Guid clientId, Guid entryId, CancellationToken)` → `Task<JournalEntry?>` (null when missing **or** when the entry belongs to another client).

- [ ] **Step 1: Write the failing tests**

`IdempotentPostTests.cs` — reuse the existing Api.Tests host/auth harness (the helper that issues a `DevToken` with `Post` permission; copy the pattern from `PostingValidationTests` / `PeriodCloseApiTests`).

```csharp
[Fact]
public async Task Reposting_the_same_id_returns_the_existing_entry_and_creates_nothing()
{
    Guid id = Guid.NewGuid();
    object body = BalancedEntry(id, "2024-06-30"); // explicit Id, balanced lines
    HttpResponseMessage first  = await Client.PostAsJsonAsync($"/clients/{clientId}/entries", body);
    HttpResponseMessage second = await Client.PostAsJsonAsync($"/clients/{clientId}/entries", body);

    Assert.Equal(HttpStatusCode.Created, first.StatusCode);
    Assert.Equal(HttpStatusCode.OK, second.StatusCode);          // idempotent replay
    // same entry id back
    Assert.Equal(id, (await second.Content.ReadFromJsonAsync<PostEntryResponse>())!.Id);
    // exactly one entry on the books for this client
    int count = await CountEntries(clientId);
    Assert.Equal(1, count);
}

[Fact]
public async Task Same_id_with_different_content_returns_422()
{
    Guid id = Guid.NewGuid();
    await Client.PostAsJsonAsync($"/clients/{clientId}/entries", BalancedEntry(id, "2024-06-30", amount: 100m));
    HttpResponseMessage clash = await Client.PostAsJsonAsync($"/clients/{clientId}/entries", BalancedEntry(id, "2024-06-30", amount: 200m));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
}

[Fact]
public async Task Repost_after_approval_returns_the_posted_entry_no_duplicate()
{
    // post with id -> approve (distinct approver token) -> re-post same body -> 200, still one entry, posting Posted
}

[Fact]
public async Task Repost_after_period_close_returns_existing_not_409()
{
    // post with id (open) -> close the period -> re-post same body -> 200 (NOT a closed-period 409), still one entry
}

[Fact]
public async Task Posting_without_an_id_twice_creates_two_entries()
{
    object body = BalancedEntryNoId("2024-06-30");
    await Client.PostAsJsonAsync($"/clients/{clientId}/entries", body);
    await Client.PostAsJsonAsync($"/clients/{clientId}/entries", body);
    Assert.Equal(2, await CountEntries(clientId)); // opt-out preserved: no dedup without an id
}

[Fact]
public async Task A_clients_id_is_never_resolved_to_another_clients_entry()
{
    // client A posts id X; client B (authorized on B) posts id X -> 409, and B's response is NOT A's entry
}
```

- [ ] **Step 2: Run, confirm they fail**
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "IdempotentPostTests"`
Expected: FAIL (today the second identical post returns `409`, the no-id case may already pass, cross-client returns 409 but the repost-after-close returns 409 not 200).

- [ ] **Step 3: Implement**

`LedgerService.cs` — add (near the other read helpers; uses the existing `_journal`):

```csharp
/// <summary>
/// The entry with <paramref name="entryId"/> IFF it belongs to <paramref name="clientId"/>; otherwise null.
/// Client-scoped so an idempotent-post resolution can never surface another client's entry on a global
/// id collision.
/// </summary>
public async Task<JournalEntry?> GetEntryAsync(Guid clientId, Guid entryId, CancellationToken cancellationToken = default)
{
    JournalEntry? entry = await _journal.GetAsync(entryId, cancellationToken);
    return entry is not null && entry.ClientId == clientId ? entry : null;
}
```

`LedgerEndpoints.cs` — replace the `PostEntry` DuplicateKey catch (currently `LedgerEndpoints.cs:78-81`):

```csharp
catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
{
    // Idempotency is opt-in and caller-declared: only an explicit, caller-supplied id can collide on _id.
    if (request.Id is { } suppliedId
        && await ctx.Ledger!.Service.GetEntryAsync(clientId, suppliedId, cancellationToken) is { } existing)
    {
        // Same client + same financial content => idempotent replay of the same operation.
        if (EntryComparison.SameFinancialContent(existing, entry!))
            return Results.Ok(new PostEntryResponse(existing.Id, existing.Status.ToString(), existing.Posting.ToString()));

        // Same id, different content => the caller reused an operation id for a different entry.
        return Unprocessable("An entry with this id already exists with different content.");
    }

    // No entry for this id under this client => a real conflict (sequence-number collision, or an id
    // already used by a different client). Do not leak the other entry.
    return Conflict("An entry with this id or sequence number already exists.");
}
```

Add `using Accounting101.Ledger.Core.Journal;` to `LedgerEndpoints.cs` if not already present (for `EntryComparison`).

> Notes for the implementer:
> - `entry!` here is the just-mapped `JournalEntry` from `ValidateForPostAsync` (in scope in `PostEntry`).
> - Do **not** add a pre-insert existence check — let the insert collide and resolve in the catch, so the sequence `$inc` rolls back with the aborted transaction (gapless). The freeze re-check inside `PostAsync` is unchanged.
> - `Unprocessable`/`Conflict` are the existing local helpers used elsewhere in this file.

- [ ] **Step 4: Run, confirm pass**
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "IdempotentPostTests"` → PASS.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Mongo/LedgerService.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/IdempotentPostTests.cs
git commit -m "feat(ledger-api): idempotent POST /entries on a caller-supplied id (replay returns existing; mismatch 422; cross-client 409)"
```

---

## Final verification (after both tasks)

- [ ] `dotnet build` full solution → **0 warnings**.
- [ ] Run individually: `EntryComparisonTests`, `IdempotentPostTests`, and a regression pass of `PostingValidationTests` (the existing post path must be unchanged for the no-id and fresh-id cases).
- [ ] Confirm the no-id path is untouched (opt-out) and the cross-client case does not leak.
- [ ] Whole-branch review on the most capable model; then `superpowers:finishing-a-development-branch`.

## Self-review (author)

- **Spec coverage:** opt-in eligibility (no-id opt-out test), replay→200 (test), mismatch→422 (test), cross-client→409 no-leak (test + `GetEntryAsync` scoping), freeze-safe replay (test), approval-safe replay (test), gapless (count-stays-1 + no pre-check), fingerprint semantics (Task 1 tests) — all mapped.
- **Type consistency:** `SameFinancialContent(JournalEntry, JournalEntry)`, `GetEntryAsync(Guid, Guid, CancellationToken) → Task<JournalEntry?>`, `PostEntryResponse(Id, Status, Posting)` used identically across tasks.
- **Open implementer check (flagged):** the real shape of `Line.Dimensions` on the domain `Line` must be confirmed in Task 1 so `SameDimensions` compares the actual representation (dict vs list of tags). Called out in Task 1 Step 3.
