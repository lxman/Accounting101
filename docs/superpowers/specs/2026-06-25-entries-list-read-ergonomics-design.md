# Entries-list read ergonomics — Design

**Date:** 2026-06-25
**Status:** Spec for review
**Context:** `GET /clients/{id}/entries` supports `account`/`sourceRef`/`dimension`/`value`/`skip`/`limit`, but: (1) there is **no `posting` filter**, so an approver who wants "what needs approving" must page the whole journal — the exact footgun that stranded entries in the first dog-food run (the approver missed PendingApproval entries past page 1); (2) a clerk's `?reference=…` is **silently ignored** (not a recognized param), returning the full list and giving false confidence to a duplicate pre-check. This slice adds a real `posting` filter and a real `reference` filter, and makes an invalid `posting` value a `400` (never silently ignored).

## Principle

This is the **read-side complement** to the close gate (read primary, command guard backstop): a `posting=PendingApproval` filter makes the approver's job a single scoped query instead of a paginate-to-the-end scan, so approval misses become unlikely; the close gate remains the backstop. And a filter parameter must either **work or be rejected** — silently ignoring `reference` is worse than not offering it.

## Approach

Extend `ListEntries` with two optional query params, both DB-filtered (not post-hoc in-memory over a page):

### Component 1 — store queries (`MongoJournalStore`)

```csharp
/// Entries whose Reference equals the given value (exact match), in sequence order.
Task<IReadOnlyList<JournalEntry>> GetByReferenceAsync(Guid clientId, string reference, CancellationToken ct = default);

/// Entries in the given posting state (PendingApproval | Posted), paged, in sequence order —
/// served by the existing (ClientId, Status, Posting) index.
Task<IReadOnlyList<JournalEntry>> GetByPostingAsync(Guid clientId, PostingState posting, int skip, int limit, CancellationToken ct = default);
```

- `GetByReferenceAsync`: filter `ClientId == clientId AND Reference == reference`. (References are sparse and short; an index is optional — note it, don't require it. A non-matching reference returns empty, which is the correct, honest answer — not "everything.")
- `GetByPostingAsync`: filter `ClientId == clientId AND Posting == posting`, sorted by `SequenceNumber`, paged. The `client_status_posting` index serves the `(ClientId, …, Posting)` predicate.

### Component 2 — `ListEntries` accepts `posting` and `reference`

New optional params: `string? posting`, `string? reference`. Composition keeps the existing mutually-exclusive base-filter precedence and treats `posting` as a refinement:

1. **Validate `posting`** first: if provided and not a case-insensitive match for `PendingApproval` or `Posted` → `400 Problem("posting must be 'PendingApproval' or 'Posted'.")`. (Never silently ignore.)
2. **Pick the base query** by precedence: `reference` → `sourceRef` → `dimension(+value)` → `account` → `posting`-only → unfiltered (paged). I.e. `reference` joins the head of the existing precedence chain; a `posting`-only request (no other base filter) uses the dedicated `GetByPostingAsync` (DB-filtered + paged — this is the approver's path and must not paginate the whole journal).
3. **Refine by `posting`** when a *base* filter other than the posting-only branch produced the result: filter the (bounded) result in memory to the requested posting state. So `?account=X&posting=PendingApproval` returns pending entries touching X; `?reference=R&posting=Posted` etc. (The base results here — by reference/sourceRef/dimension/account — are already bounded, so in-memory refinement is safe; only the *all-entries* path must filter in the DB, which the `posting`-only branch does.)

```
posting = parse/validate(postingParam)        // 400 on invalid
if    (reference is set) base = GetByReferenceAsync(clientId, reference)
elif  (sourceRef is set) base = GetBySourceRefAsync(...)
elif  (dimension+value)  base = GetTouchingDimensionAsync(...)
elif  (account is set)   base = GetTouchingAccountAsync(...)
elif  (posting is set)   base = GetByPostingAsync(clientId, posting, page, limit)   // DB-filtered + paged
else                     base = GetByClientAsync(clientId, page, limit)             // unchanged
if (posting is set && base was NOT the GetByPostingAsync branch)
    base = base.Where(e => e.Posting == posting)
return base.Select(ToEntryResponse)
```

## Why this fixes the observed problems

- **Approver:** `GET /entries?posting=PendingApproval` returns exactly the entries awaiting approval — no full-journal pagination, no missed tail. (Run-1's stranding root cause; complements the merged close gate.)
- **Clerk dup pre-check:** `GET /entries?reference=TAXPAY-02` returns the matching entries (possibly empty) — a truthful filter instead of a silently-ignored param. (Less critical now that idempotency shipped, but it removes the false-confidence trap.)
- **No silent ignores:** an invalid `posting` value is a `400`, not a full unfiltered list masquerading as a filtered one.

## Error handling

- Invalid `posting` → `400` ProblemDetails with the allowed values.
- `reference` with no match → `200` with `[]` (honest empty, not "everything").
- Unchanged: the unfiltered path keeps its default `limit` cap (200, max 1000).

## Testing

Store (`Accounting101.Ledger.Mongo.Tests`, EphemeralMongo):
- `GetByReferenceAsync` returns only entries with the exact reference; empty for a non-match; does not match a different client.
- `GetByPostingAsync` returns only entries in the given posting state, paged, sequence-ordered.

API (`Accounting101.Ledger.Api.Tests`):
- `?posting=PendingApproval` returns only pending entries (post several, approve some, assert the filter returns exactly the unapproved ones) and does NOT require paging to find them.
- `?posting=Posted` returns only posted entries.
- Invalid `?posting=Nope` → `400`.
- `?reference=R` returns only entries with reference `R`; `?reference=absent` → `200 []` (not the full list — this is the regression that proves the silent-ignore is fixed).
- `?account=X&posting=PendingApproval` returns pending entries touching X (composition).
- Existing `account`/`sourceRef`/`dimension` filters and the unfiltered paged path are unchanged (regression).

## Scope

**In scope:** `GetByReferenceAsync` + `GetByPostingAsync` store queries; the `posting` + `reference` params on `ListEntries` with validation and composition; tests. Read-only; no write-path or schema change (an optional `Reference` index is a noted, non-required follow-up).

**Out of scope:** a `GET /dimensions` / `GET /source-types` discovery endpoint (a separate group-3 ergonomics item); newest-first ordering (sequence-ordered is fine with the filter).

## Global constraints

- .NET 10; tests use EphemeralMongo; build 0 warnings; commit per slice; TDD.
- A filter parameter must work or be rejected — never silently ignored.
- Read-only, domain-agnostic; no policy.
