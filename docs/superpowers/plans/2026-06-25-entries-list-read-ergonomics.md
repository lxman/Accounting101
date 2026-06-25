# Entries-list read ergonomics — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Add a real `posting` filter and a real `reference` filter to `GET /clients/{id}/entries`, and make an invalid `posting` value a `400` (never silently ignored).

**Architecture:** Two DB-level store queries (`GetByReferenceAsync`, `GetByPostingAsync`) plus two new optional params on `ListEntries` with validation and composition (`reference` joins the precedence chain head; `posting` alone uses the dedicated DB-filtered+paged query, otherwise refines a bounded base result).

**Tech Stack:** C#/.NET 10, MongoDB, xUnit + EphemeralMongo.

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- A filter param must work or be rejected — never silently ignored (invalid `posting` ⇒ 400).
- `posting` alone must DB-filter+page (the approver path must not paginate the whole journal); a non-matching `reference` returns `[]`, not the full list.
- Do not change existing `account`/`sourceRef`/`dimension`/unfiltered behavior.
- Tests use EphemeralMongo; run a single test class at a time when verifying.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit file lists; check for stray churn.

---

## Task 1: store queries `GetByReferenceAsync` + `GetByPostingAsync`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs`
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/JournalStoreQueryTests.cs` (add cases)

**Interfaces:**
- Produces (consumed by Task 2):
  - `GetByReferenceAsync(Guid clientId, string reference, CancellationToken) → Task<IReadOnlyList<JournalEntry>>`
  - `GetByPostingAsync(Guid clientId, PostingState posting, int skip, int limit, CancellationToken) → Task<IReadOnlyList<JournalEntry>>`

- [ ] **Step 1: Write the failing tests** (mirror the existing query tests' construction; seed via the store)

```csharp
[Fact]
public async Task GetByReference_returns_only_matching_reference()
{
    // seed entries with references "R1","R1","R2" -> GetByReferenceAsync(client,"R1") returns the two R1s; "RX" returns empty
}

[Fact]
public async Task GetByPosting_returns_only_that_posting_state_paged()
{
    // seed some PendingApproval and some Posted (approve a couple) -> GetByPostingAsync(client, PendingApproval, 0, 200)
    // returns only the pending ones, sequence-ordered
}
```

- [ ] **Step 2: Run, confirm fail** (`dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "JournalStoreQueryTests"` — new methods missing).

- [ ] **Step 3: Implement** in `MongoJournalStore.cs` (mirror the existing `GetBySourceRefAsync` / `GetByClientAsync` shape; reuse the `client_status_posting` index for posting):

```csharp
public async Task<IReadOnlyList<JournalEntry>> GetByReferenceAsync(
    Guid clientId, string reference, CancellationToken cancellationToken = default)
{
    FilterDefinitionBuilder<JournalEntryDocument> f = Builders<JournalEntryDocument>.Filter;
    FilterDefinition<JournalEntryDocument> filter = f.And(
        f.Eq(e => e.ClientId, clientId),
        f.Eq(e => e.Reference, reference));
    List<JournalEntryDocument> docs = await _entries.Find(filter).SortBy(e => e.SequenceNumber).ToListAsync(cancellationToken);
    return docs.Select(d => d.ToDomain()).ToList();
}

public async Task<IReadOnlyList<JournalEntry>> GetByPostingAsync(
    Guid clientId, PostingState posting, int skip, int limit, CancellationToken cancellationToken = default)
{
    // Match the stored representation of Posting (string enum name, as OnBooks / GetPendingThroughAsync do):
    FilterDefinition<JournalEntryDocument> filter = Builders<JournalEntryDocument>.Filter.And(
        Builders<JournalEntryDocument>.Filter.Eq(e => e.ClientId, clientId),
        Builders<JournalEntryDocument>.Filter.Eq("Posting", posting.ToString()));
    List<JournalEntryDocument> docs = await _entries.Find(filter)
        .SortBy(e => e.SequenceNumber)
        .Skip(skip > 0 ? skip : null)
        .Limit(limit > 0 ? limit : null)
        .ToListAsync(cancellationToken);
    return docs.Select(d => d.ToDomain()).ToList();
}
```

> Implementer: confirm the stored `Reference` and `Posting` representations on `JournalEntryDocument` (Posting is a string enum name — match how `GetPendingThroughAsync`/`OnBooks` filter it; if it uses `nameof(PostingState.X)`, use `posting.ToString()` which equals the name). Reference is a nullable string; an exact `Eq` is correct.

- [ ] **Step 4: Run, confirm pass** → PASS.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs Backend/Accounting101.Ledger.Mongo.Tests/JournalStoreQueryTests.cs
git commit -m "feat(ledger): GetByReferenceAsync + GetByPostingAsync journal queries"
```

---

## Task 2: `ListEntries` accepts `posting` + `reference` (validated, composed)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`ListEntries`)
- Test: `Backend/Accounting101.Ledger.Api.Tests/EntriesListFilterTests.cs` (create)

**Interfaces:**
- Consumes: `GetByReferenceAsync`, `GetByPostingAsync` (Task 1).

- [ ] **Step 1: Write the failing tests** (reuse the existing Api.Tests host/auth harness — DevToken with Read+Post+Approve permissions; copy from a sibling test)

```csharp
[Fact] public async Task Posting_pending_returns_only_unapproved()      { /* post several, approve some -> ?posting=PendingApproval returns exactly the unapproved */ }
[Fact] public async Task Posting_posted_returns_only_posted()           { /* -> ?posting=Posted returns the approved ones */ }
[Fact] public async Task Invalid_posting_value_returns_400()            { /* ?posting=Nope -> 400 */ }
[Fact] public async Task Reference_filter_returns_only_matching()       { /* post with reference R -> ?reference=R returns it */ }
[Fact] public async Task Absent_reference_returns_empty_not_all()       { /* ?reference=DOESNOTEXIST -> 200 with [] (proves silent-ignore is fixed) */ }
[Fact] public async Task Account_and_posting_compose()                  { /* ?account=X&posting=PendingApproval -> pending entries touching X */ }
```

- [ ] **Step 2: Run, confirm fail** — `?posting=` is not a param yet (ignored ⇒ returns all, failing the "only unapproved" assertion); `?reference=` returns all (failing the empty-on-absent assertion); invalid posting returns 200 not 400.

- [ ] **Step 3: Implement** — extend `ListEntries` (`LedgerEndpoints.cs:370`):
  - Add params `string? posting, string? reference` to the signature (minimal-API binds them from query).
  - Parse/validate `posting`: if non-null and not a case-insensitive `PendingApproval`/`Posted`, return `Results.Problem("posting must be 'PendingApproval' or 'Posted'.", statusCode: 400)`. Otherwise a nullable `PostingState?`.
  - Base-query precedence (reference first, then existing chain, then posting-only, then unfiltered) per the spec; refine by `posting` in-memory when the base was not the posting-only branch:
  ```csharp
  PostingState? postingState = ParsePosting(posting, out IResult? badPosting);
  if (badPosting is not null) return badPosting;

  IReadOnlyList<JournalEntry> entries;
  bool postingHandledByQuery = false;
  if (!string.IsNullOrWhiteSpace(reference))
      entries = await ctx.Ledger.Journal.GetByReferenceAsync(clientId, reference, cancellationToken);
  else if (sourceRef is { } source)
      entries = await ctx.Ledger.Journal.GetBySourceRefAsync(clientId, source, cancellationToken);
  else if (!string.IsNullOrWhiteSpace(dimension) && value is { } dimValue)
      entries = await ctx.Ledger.Journal.GetTouchingDimensionAsync(clientId, dimension, dimValue, cancellationToken);
  else if (account is { } accountId)
      entries = await ctx.Ledger.Journal.GetTouchingAccountAsync(clientId, accountId, cancellationToken);
  else if (postingState is { } ps)
  {   entries = await ctx.Ledger.Journal.GetByPostingAsync(clientId, ps, Page(skip), PageLimit(limit), cancellationToken);
      postingHandledByQuery = true; }
  else
      entries = await ctx.Ledger.Journal.GetByClientAsync(clientId, Page(skip), PageLimit(limit), cancellationToken);

  if (postingState is { } refine && !postingHandledByQuery)
      entries = entries.Where(e => e.Posting == refine).ToList();

  return Results.Ok(entries.Select(ToEntryResponse).ToList());
  ```
  Add a small `ParsePosting(string?, out IResult?)` helper near the other endpoint helpers (case-insensitive `Enum.TryParse<PostingState>` restricted to the two valid names). `JournalEntry.Posting` is a `PostingState` enum — compare directly.

- [ ] **Step 4: Run, confirm pass** → all `EntriesListFilterTests` green; run `CommandQueryTests` (or whichever exercises the existing `/entries` filters) to confirm no regression.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/EntriesListFilterTests.cs
git commit -m "feat(ledger-api): posting + reference filters on GET /entries (invalid posting => 400)"
```

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run individually: `JournalStoreQueryTests`, `EntriesListFilterTests`, and the existing entries-list/query test class — all green.
- [ ] Confirm existing filters + unfiltered paging unchanged; invalid posting is a 400; absent reference is `[]`.
- [ ] (read-only, low-risk) the per-task reviews serve as the gate; controller spot-checks the diff before finishing.

## Self-review (author)
- Spec coverage: posting filter (Task 1 query + Task 2 path + tests), reference filter (same), invalid-posting 400 (Task 2), composition (Task 2), no-regression (Task 2 Step 4).
- Type consistency: `GetByPostingAsync(Guid, PostingState, int, int, ct)`, `GetByReferenceAsync(Guid, string, ct)`, `JournalEntry.Posting : PostingState` compared directly.
- Open implementer check: stored `Posting`/`Reference` representation on `JournalEntryDocument` (Task 1 Step 3).
