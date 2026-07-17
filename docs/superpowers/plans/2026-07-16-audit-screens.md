# Audit Screens (Audit Trail + Verify Integrity) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the two per-client "Assurance ▸ Audit" screens (Audit Trail + Verify Integrity) the nav already promises but that currently fall through to the "Coming soon" placeholder — adding paging, a verify diagnostic, `audit.read` enforcement, and the two frontend screens.

**Architecture:** Backend — make `GET /clients/{id}/audit` dual-shape (`PagedResponse` when paged, mirroring `/entries`); add a diagnostic `VerifyDetailedAsync` that decomposes the failure `VerifyAsync` already detects; and enforce the dedicated `audit.read` capability on the two Audit-area endpoints via a new capability-string gateway method. Frontend — a paginated Audit Trail screen (mirrors `entry-list`) with a `gl.read`-gated whole-row drill to the journal entry, and a Verify Integrity action card that humanizes the diagnostic. Nav already gates the `area: 'audit'` group on `audit.read`.

**Tech Stack:** .NET 10 (minimal APIs, xUnit + EphemeralMongo via `MongoFixture`/`ApiFixture`); Angular 22 (standalone, OnPush, zoneless, signals), Tailwind v4, Spartan Helm; Vitest + TestBed.

## Global Constraints

- **Backend:** namespaces follow folder structure. `GET /audit` dual-shape is additive — the bare `List<AuditRecordResponse>` path is preserved for the existing entry-detail/spine consumers; only a supplied `skip`/`limit` switches to `PagedResponse`. `VerifyAsync`'s external `bool` behavior is unchanged (it delegates to the new detailed method). `GET /audit/{entryId}` gating is **unchanged** (`gl.read`). **Rider auto-converts explicit types to `var`** — stage the explicit file list per task and check `git diff --cached --stat` for stray churn before each commit.
- **`audit.read` enforcement:** the two Audit-area endpoints (`GetClientAudit`, `VerifyAudit`) require `Capabilities.AuditRead`; the entry-timeline (`GetEntryAudit`) stays on `Permission.Read` (`gl.read`). `audit.read` is in every Reads role preset, so no preset-holder is locked out; `LedgerRole.ArClerk` = `{gl.read, ar.read, ar.write}` (no `audit.read`) is the gating-test fixture.
- **Verify diagnostic taxonomy** (each mapped from the exact check in the current `VerifyAsync` walk): `SequenceGap`/`BrokenLink`/`HashMismatch` (carry `BrokenAtSequence`), `TailTruncated`, `HeadMismatch`. `Failure` serializes as the enum-name string.
- **Frontend:** standalone, `OnPush`, zoneless. TS imports single-quoted; HTML attrs double-quoted; 2-space template indent. The trail-row journal drill is `gl.read`-gated (a row is clickable only when the user holds `gl.read` AND the row has an `entryId`). Conditional Tailwind classes with special chars (`hover:bg-muted/50`) must use a `[class]="cond ? '…' : ''"` string binding, NOT `[class.hover:bg-muted/50]` (special chars break the `[class.x]` form). FE test runner is **Vitest** (`vi.fn`/`vi.spyOn` global; nav spies `.mockResolvedValue(true)`).
- **Wire shapes** identical backend ↔ frontend (host `JsonNamingPolicy.CamelCase`): `PagedResponse<AuditRecordResponse>{ items, total, skip, limit }`; `AuditVerifyResponse{ valid, recordCount, headSequence: number|null, failure: string|null, brokenAtSequence: number|null }`.
- Only touch files named per task. Do NOT change `GET /audit/{entryId}` gating, the journal entry-detail screen, the `/audit/reconciliations` placeholder, subledger-reconciliation/admin-audit surfaces, or unrelated modules.
- `environment.ts` stays modified/uncommitted (never commit).
- Branch `feat/audit-screens`. Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

### Task 1: Backend — audit-log pagination (dual-shape)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs` (add `CountForClientAsync`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`GetClientAudit` dual-shape)
- Test (extend): `Backend/Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs` (count)
- Test (create): `Backend/Accounting101.Ledger.Api.Tests/AuditEndpointTests.cs` (host dual-shape)

**Interfaces:**
- Consumes: `MongoAuditLog.GetForClientAsync` (existing), `Page()`/`PageLimit()` (existing), `PagedResponse<T>` (`Accounting101.Ledger.Contracts`), `ToAuditResponses` (existing helper), `ApiFixture.SeedClientAsync`.
- Produces: `MongoAuditLog.CountForClientAsync(Guid clientId, CancellationToken) → Task<long>`; `GET /clients/{id}/audit?skip&limit` → `PagedResponse<AuditRecordResponse>` when paged.

- [ ] **Step 1: Write the failing tests**

Append to `AuditLogTests.cs` (a new test class alongside `AuditHeadTests`, reusing its `MongoFixture` pattern; add `using Accounting101.Ledger.Mongo.Documents;` is already present):
```csharp
public sealed class AuditCountTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static Actor User() => new() { UserId = Guid.NewGuid(), Name = "tester", Claims = [new Claim("role", "tester")] };

    [Fact]
    public async Task CountForClient_returns_the_per_client_record_count()
    {
        MongoAuditLog audit = new(fixture.Database, "audit_count_" + Guid.NewGuid().ToString("N"));
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        for (int i = 0; i < 3; i++)
            await audit.AppendAsync(a, Guid.NewGuid(), 1, AuditAction.Created, User(), null, DateTimeOffset.UtcNow);
        await audit.AppendAsync(b, Guid.NewGuid(), 1, AuditAction.Created, User(), null, DateTimeOffset.UtcNow);

        Assert.Equal(3, await audit.CountForClientAsync(a));
        Assert.Equal(1, await audit.CountForClientAsync(b));
    }
}
```

Create `AuditEndpointTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The Audit-area HTTP surface: paged log, verify diagnostic, and audit.read enforcement.</summary>
public sealed class AuditEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task PostApproveAsync(HttpClient http, Guid client, DateOnly date, Guid debit, Guid credit, decimal amount)
    {
        PostEntryRequest req = new(null, date, null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);
        PostEntryResponse created = (await (await http.PostAsJsonAsync($"/clients/{client}/entries", req))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Audit_log_is_bare_when_unpaged_and_a_paged_envelope_when_paged()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        await PostApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 31), cash, revenue, 100m);
        await PostApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 31), cash, revenue, 50m);

        AuditRecordResponse[] bare = (await c.Http.GetFromJsonAsync<AuditRecordResponse[]>(
            $"/clients/{c.ClientId}/audit"))!;
        Assert.NotEmpty(bare);

        PagedResponse<AuditRecordResponse> page = (await c.Http.GetFromJsonAsync<PagedResponse<AuditRecordResponse>>(
            $"/clients/{c.ClientId}/audit?skip=0&limit=2"))!;
        Assert.Equal(bare.Length, page.Total);
        Assert.Equal(0, page.Skip);
        Assert.Equal(2, page.Limit);
        Assert.True(page.Items.Count <= 2);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "FullyQualifiedName~AuditCountTests"` → BUILD FAILURE (`CountForClientAsync` missing).
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AuditEndpointTests"` → the paged test FAILS (deserialize of `PagedResponse` fails against the bare array).

- [ ] **Step 3: Add `CountForClientAsync`**

In `MongoAuditLog.cs`, add next to `GetForClientAsync` (~line 187):
```csharp
    /// <summary>The count of the client's audit records — the <c>Total</c> for a paged audit-log response.</summary>
    public Task<long> CountForClientAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        _audit.CountDocumentsAsync(a => a.ClientId == clientId, cancellationToken: cancellationToken);
```

- [ ] **Step 4: Make `GetClientAudit` dual-shape**

In `LedgerEndpoints.cs`, replace `GetClientAudit` (lines 816–824) with:
```csharp
    private static async Task<IResult> GetClientAudit(
        Guid clientId, int? skip, int? limit, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        List<AuditRecordResponse> items = ToAuditResponses(
            await ctx.Ledger.Audit.GetForClientAsync(clientId, Page(skip), PageLimit(limit), cancellationToken));
        if (skip is not null || limit is not null)
        {
            long total = await ctx.Ledger.Audit.CountForClientAsync(clientId, cancellationToken);
            return Results.Ok(new PagedResponse<AuditRecordResponse>(items, total, Page(skip), PageLimit(limit)));
        }
        return Results.Ok(items);
    }
```
(Task 3 later swaps the `ResolveAsync(..., Permission.Read, ...)` line for the `audit.read` check — leave it as-is here.)

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "FullyQualifiedName~AuditCountTests"` → PASS.
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AuditEndpointTests"` → PASS.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs Backend/Accounting101.Ledger.Api.Tests/AuditEndpointTests.cs
git commit -m "feat(ledger): paged audit log (PagedResponse when skip/limit supplied)"
```

---

### Task 2: Backend — verify diagnostic

**Files:**
- Create: `Backend/Accounting101.Ledger.Mongo/AuditChainVerification.cs`
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs` (add `VerifyDetailedAsync`; `VerifyAsync` delegates)
- Modify: `Backend/Accounting101.Ledger.Contracts/EntryResponses.cs` (extend `AuditVerifyResponse`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`VerifyAudit` maps the diagnostic)
- Test (extend): `Backend/Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs` (per-failure-kind)
- Test (extend): `Backend/Accounting101.Ledger.Api.Tests/AuditEndpointTests.cs` (host verify shape)

**Interfaces:**
- Consumes: `AuditRecordDocument`, `AuditHeadDocument`, `FindHeadAsync`, `ComputeHash` (all existing in `MongoAuditLog`).
- Produces: `AuditChainFailure` enum `{SequenceGap, BrokenLink, HashMismatch, TailTruncated, HeadMismatch}`; `AuditChainVerification(bool Valid, long RecordCount, long? HeadSequence, AuditChainFailure? Failure, long? BrokenAtSequence)`; `MongoAuditLog.VerifyDetailedAsync(Guid, CancellationToken) → Task<AuditChainVerification>`; `AuditVerifyResponse(bool Valid, long RecordCount, long? HeadSequence, string? Failure, long? BrokenAtSequence)`.

- [ ] **Step 1: Write the failing tests**

Append to `AuditLogTests.cs` a new class. It appends a clean chain, then tampers the Mongo docs directly to force each failure. It needs the audit collection name to reach the records, and the fixed `audit-head` collection:
```csharp
public sealed class AuditVerifyDetailedTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private static Actor User() => new() { UserId = Guid.NewGuid(), Name = "tester", Claims = [new Claim("role", "tester")] };

    private async Task<(MongoAuditLog audit, string coll, Guid clientId)> SeededChainAsync(int n)
    {
        string coll = "audit_verify_" + Guid.NewGuid().ToString("N");
        MongoAuditLog audit = new(fixture.Database, coll);
        Guid clientId = Guid.NewGuid();
        for (int i = 0; i < n; i++)
            await audit.AppendAsync(clientId, Guid.NewGuid(), 1, AuditAction.Created, User(), null, DateTimeOffset.UtcNow);
        return (audit, coll, clientId);
    }

    private IMongoCollection<AuditRecordDocument> Records(string coll) => fixture.Database.GetCollection<AuditRecordDocument>(coll);
    private IMongoCollection<AuditHeadDocument> Head() => fixture.Database.GetCollection<AuditHeadDocument>("audit-head");

    [Fact]
    public async Task Clean_chain_is_valid_with_counts()
    {
        (MongoAuditLog audit, _, Guid clientId) = await SeededChainAsync(3);
        AuditChainVerification v = await audit.VerifyDetailedAsync(clientId);
        Assert.True(v.Valid);
        Assert.Null(v.Failure);
        Assert.Equal(3, v.RecordCount);
        Assert.Equal(3, v.HeadSequence);
        Assert.Null(v.BrokenAtSequence);
        Assert.True(await audit.VerifyAsync(clientId)); // bool delegate unchanged
    }

    [Fact]
    public async Task Tampered_record_is_HashMismatch_at_its_sequence()
    {
        (MongoAuditLog audit, string coll, Guid clientId) = await SeededChainAsync(3);
        await Records(coll).UpdateOneAsync(
            r => r.ClientId == clientId && r.Sequence == 2,
            Builders<AuditRecordDocument>.Update.Set(r => r.Reason, "tampered"));
        AuditChainVerification v = await audit.VerifyDetailedAsync(clientId);
        Assert.False(v.Valid);
        Assert.Equal(AuditChainFailure.HashMismatch, v.Failure);
        Assert.Equal(2, v.BrokenAtSequence);
        Assert.False(await audit.VerifyAsync(clientId));
    }

    [Fact]
    public async Task Rewritten_link_is_BrokenLink_at_its_sequence()
    {
        (MongoAuditLog audit, string coll, Guid clientId) = await SeededChainAsync(3);
        await Records(coll).UpdateOneAsync(
            r => r.ClientId == clientId && r.Sequence == 2,
            Builders<AuditRecordDocument>.Update.Set(r => r.PreviousHash, "DEADBEEF"));
        AuditChainVerification v = await audit.VerifyDetailedAsync(clientId);
        Assert.False(v.Valid);
        Assert.Equal(AuditChainFailure.BrokenLink, v.Failure);
        Assert.Equal(2, v.BrokenAtSequence);
    }

    [Fact]
    public async Task Missing_middle_record_is_SequenceGap()
    {
        (MongoAuditLog audit, string coll, Guid clientId) = await SeededChainAsync(3);
        await Records(coll).DeleteOneAsync(r => r.ClientId == clientId && r.Sequence == 2);
        AuditChainVerification v = await audit.VerifyDetailedAsync(clientId);
        Assert.False(v.Valid);
        Assert.Equal(AuditChainFailure.SequenceGap, v.Failure);
        Assert.Equal(2, v.BrokenAtSequence);
    }

    [Fact]
    public async Task Deleted_newest_record_is_TailTruncated()
    {
        (MongoAuditLog audit, string coll, Guid clientId) = await SeededChainAsync(3);
        await Records(coll).DeleteOneAsync(r => r.ClientId == clientId && r.Sequence == 3);
        AuditChainVerification v = await audit.VerifyDetailedAsync(clientId);
        Assert.False(v.Valid);
        Assert.Equal(AuditChainFailure.TailTruncated, v.Failure);
        Assert.Equal(3, v.BrokenAtSequence);
    }

    [Fact]
    public async Task Rewritten_head_hash_is_HeadMismatch()
    {
        (MongoAuditLog audit, _, Guid clientId) = await SeededChainAsync(3);
        await Head().UpdateOneAsync(
            h => h.ClientId == clientId,
            Builders<AuditHeadDocument>.Update.Set(h => h.Hash, "DEADBEEF"));
        AuditChainVerification v = await audit.VerifyDetailedAsync(clientId);
        Assert.False(v.Valid);
        Assert.Equal(AuditChainFailure.HeadMismatch, v.Failure);
    }
}
```
Ensure `AuditLogTests.cs` has `using MongoDB.Driver;` (it does) and `using Accounting101.Ledger.Mongo.Documents;` (it does).

Append to `AuditEndpointTests.cs` a host-level valid-path shape test:
```csharp
    [Fact]
    public async Task Verify_reports_a_valid_chain_with_diagnostic_fields()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        await PostApproveAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 31), cash, revenue, 100m);

        AuditVerifyResponse v = (await c.Http.GetFromJsonAsync<AuditVerifyResponse>(
            $"/clients/{c.ClientId}/audit/verify"))!;
        Assert.True(v.Valid);
        Assert.Null(v.Failure);
        Assert.Null(v.BrokenAtSequence);
        Assert.True(v.RecordCount > 0);
        Assert.Equal(v.RecordCount, v.HeadSequence);
    }
```
(If a pre-existing `CommandQueryTests.Audit_verify_reports_a_valid_chain` exists, leave it — it constructs `AuditVerifyResponse` only via deserialization of a superset, which still binds. Do not edit that file.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "FullyQualifiedName~AuditVerifyDetailedTests"` → BUILD FAILURE (`AuditChainVerification`/`VerifyDetailedAsync` missing).
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AuditEndpointTests"` → the verify-shape test FAILS to bind the new fields.

- [ ] **Step 3: Create `AuditChainVerification.cs`**

```csharp
namespace Accounting101.Ledger.Mongo;

/// <summary>How a client's audit chain failed verification. Mapped 1:1 from the checks in
/// <see cref="MongoAuditLog.VerifyDetailedAsync"/>.</summary>
public enum AuditChainFailure
{
    /// <summary>A record's sequence is not the expected next value (a record is missing / non-contiguous).</summary>
    SequenceGap,
    /// <summary>A record's PreviousHash does not match its predecessor's hash.</summary>
    BrokenLink,
    /// <summary>A record's stored hash does not recompute from its content (the record was edited).</summary>
    HashMismatch,
    /// <summary>The walk is internally clean but the guarded head remembers records past the chain tail
    /// (the newest N records were deleted).</summary>
    TailTruncated,
    /// <summary>The walk is clean and the head sequence matches the tail, but the head hash does not
    /// (or the head is missing though records exist).</summary>
    HeadMismatch,
}

/// <summary>The detailed result of verifying a client's audit chain: whether it is intact, how many
/// records were walked, the guarded head sequence, and — when broken — the failure kind and the
/// sequence at which it was detected.</summary>
public sealed record AuditChainVerification(
    bool Valid, long RecordCount, long? HeadSequence, AuditChainFailure? Failure, long? BrokenAtSequence);
```

- [ ] **Step 4: Add `VerifyDetailedAsync` and delegate `VerifyAsync`**

In `MongoAuditLog.cs`, replace `VerifyAsync` (lines 193–220) with:
```csharp
    /// <summary>
    /// Verify the client's chain and, when broken, diagnose how: every record must link to its
    /// predecessor and its stored hash must recompute, and the walked tail must reconcile with the
    /// guarded head (which catches tail truncation). The failure taxonomy maps 1:1 to the checks below.
    /// </summary>
    public async Task<AuditChainVerification> VerifyDetailedAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        List<AuditRecordDocument> records = await _audit
            .Find(a => a.ClientId == clientId)
            .SortBy(a => a.Sequence)
            .ToListAsync(cancellationToken);

        AuditHeadDocument? head = await FindHeadAsync(clientId, cancellationToken: cancellationToken);
        long? headSeq = head?.Sequence;

        var previousHash = string.Empty;
        long expectedSeq = 1;
        foreach (AuditRecordDocument record in records)
        {
            if (record.Sequence != expectedSeq)
                return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.SequenceGap, expectedSeq);
            if (record.PreviousHash != previousHash)
                return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.BrokenLink, record.Sequence);
            if (record.Hash != ComputeHash(record))
                return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.HashMismatch, record.Sequence);

            previousHash = record.Hash;
            expectedSeq++;
        }

        if (records.Count == 0)
            return head is null || head.Sequence == 0
                ? new AuditChainVerification(true, 0, headSeq, null, null)
                : new AuditChainVerification(false, 0, headSeq, AuditChainFailure.TailTruncated, 1);

        AuditRecordDocument last = records[^1];
        if (head is null || head.Sequence < last.Sequence)
            return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.HeadMismatch, null);
        if (head.Sequence > last.Sequence)
            return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.TailTruncated, last.Sequence + 1);
        if (head.Hash != last.Hash)
            return new AuditChainVerification(false, records.Count, headSeq, AuditChainFailure.HeadMismatch, null);

        return new AuditChainVerification(true, records.Count, headSeq, null, null);
    }

    /// <summary>Pass/fail chain verification — delegates to <see cref="VerifyDetailedAsync"/>. Behavior
    /// preserved for existing callers.</summary>
    public async Task<bool> VerifyAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        (await VerifyDetailedAsync(clientId, cancellationToken)).Valid;
```

- [ ] **Step 5: Extend the response contract and the endpoint**

In `EntryResponses.cs`, replace `AuditVerifyResponse` (line 58):
```csharp
public sealed record AuditVerifyResponse(
    bool Valid, long RecordCount, long? HeadSequence, string? Failure, long? BrokenAtSequence);
```

In `LedgerEndpoints.cs`, replace `VerifyAudit` (lines 835–843) with:
```csharp
    private static async Task<IResult> VerifyAudit(
        Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        AuditChainVerification v = await ctx.Ledger.Audit.VerifyDetailedAsync(clientId, cancellationToken);
        return Results.Ok(new AuditVerifyResponse(v.Valid, v.RecordCount, v.HeadSequence, v.Failure?.ToString(), v.BrokenAtSequence));
    }
```
(Task 3 swaps the `ResolveAsync(..., Permission.Read, ...)` line for the `audit.read` check.)

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests --filter "FullyQualifiedName~AuditVerifyDetailedTests"` → PASS (6 tests).
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AuditEndpointTests"` → PASS.
Sanity: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~CommandQueryTests"` → still green (the pre-existing verify test binds the superset response).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Mongo/AuditChainVerification.cs Backend/Accounting101.Ledger.Mongo/MongoAuditLog.cs Backend/Accounting101.Ledger.Contracts/EntryResponses.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Mongo.Tests/AuditLogTests.cs Backend/Accounting101.Ledger.Api.Tests/AuditEndpointTests.cs
git commit -m "feat(ledger): diagnostic audit-chain verification (failure kind + broken-at sequence)"
```

---

### Task 3: Backend — `audit.read` enforcement

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs` (add `ResolveCapabilityAsync`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (switch `GetClientAudit` + `VerifyAudit`)
- Test (extend): `Backend/Accounting101.Ledger.Api.Tests/AuditEndpointTests.cs`

**Interfaces:**
- Consumes: `ControlStore.GetMembershipAsync`, `Membership.Capabilities`, `Capabilities.AuditRead`, `ClientLedgerFactory`, `ApiFixture.AddMemberAsync`, `LedgerRole.ArClerk`.
- Produces: `LedgerGateway.ResolveCapabilityAsync(ClaimsPrincipal, Guid, string capability, CancellationToken) → Task<LedgerContext>`.

- [ ] **Step 1: Write the failing test**

Append to `AuditEndpointTests.cs` (add `using Accounting101.Ledger.Api.Control;` for `LedgerRole` at the top of the file):
```csharp
    [Fact]
    public async Task Audit_area_requires_audit_read_but_entry_timeline_stays_on_gl_read()
    {
        SeededClient c = await fixture.SeedClientAsync();   // Controller: holds audit.read + gl.read
        Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();
        PostEntryRequest req = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(cash, "Debit", 100m), new PostLineRequest(revenue, "Credit", 100m)]);
        PostEntryResponse created = (await (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", req))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();

        // A member with gl.read but NOT audit.read (ArClerk preset = {gl.read, ar.read, ar.write}).
        HttpClient arClerk = await fixture.AddMemberAsync(c.ClientId, LedgerRole.ArClerk, "AR Clerk");

        // Entry-timeline stays gl.read → reachable by the AR clerk.
        Assert.Equal(HttpStatusCode.OK, (await arClerk.GetAsync($"/clients/{c.ClientId}/audit/{created.Id}")).StatusCode);

        // The Audit-area endpoints require audit.read → forbidden for the AR clerk.
        Assert.Equal(HttpStatusCode.Forbidden, (await arClerk.GetAsync($"/clients/{c.ClientId}/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await arClerk.GetAsync($"/clients/{c.ClientId}/audit/verify")).StatusCode);

        // The controller (holds audit.read) still gets through.
        Assert.Equal(HttpStatusCode.OK, (await c.Http.GetAsync($"/clients/{c.ClientId}/audit")).StatusCode);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AuditEndpointTests"` → the new test FAILS (`/audit` and `/audit/verify` currently return 200 for the AR clerk, who holds `gl.read`).

- [ ] **Step 3: Add `ResolveCapabilityAsync`**

In `LedgerGateway.cs`, add after `ResolveAsync` (~line 31):
```csharp
    /// <summary>Authorize a member on a client by a specific capability string (not a GL
    /// <see cref="Permission"/>) — for areas whose capability has no <see cref="Permission"/> mapping
    /// (e.g. <see cref="Capabilities.AuditRead"/>). Mirrors <see cref="ResolveAsync"/> but checks the
    /// capability directly, leaving the Permission↔capability maps untouched.</summary>
    public async Task<LedgerContext> ResolveCapabilityAsync(
        ClaimsPrincipal user, Guid clientId, string capability, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);

        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null || !membership.Capabilities.Contains(capability))
            return LedgerContext.Forbidden();

        ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
        return ledger is null ? LedgerContext.NotFound() : LedgerContext.Ok(actor, ledger);
    }
```

- [ ] **Step 4: Switch the two Audit-area endpoints**

In `LedgerEndpoints.cs`, in `GetClientAudit` (Task 1's version) change the first line of the body:
```csharp
        LedgerContext ctx = await gateway.ResolveCapabilityAsync(user, clientId, Capabilities.AuditRead, cancellationToken);
```
In `VerifyAudit` (Task 2's version) change the first line of the body identically:
```csharp
        LedgerContext ctx = await gateway.ResolveCapabilityAsync(user, clientId, Capabilities.AuditRead, cancellationToken);
```
Leave `GetEntryAudit` on `gateway.ResolveAsync(user, clientId, Permission.Read, ...)`. Ensure `LedgerEndpoints.cs` can see `Capabilities` (it is in `Accounting101.Ledger.Api.Control`; add `using Accounting101.Ledger.Api.Control;` if not already imported).

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "FullyQualifiedName~AuditEndpointTests"` → PASS (all Audit endpoint tests, incl. the gating boundary).

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerGateway.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/AuditEndpointTests.cs
git commit -m "feat(ledger): enforce audit.read on the audit log + verify endpoints"
```

---

### Task 4: Frontend — Audit Trail screen

**Files:**
- Modify: `UI/Angular/src/app/core/audit/audit.ts` (add `AuditVerifyResponse` interface)
- Modify: `UI/Angular/src/app/core/audit/audit.service.ts` (add `clientAudit`)
- Create: `UI/Angular/src/app/features/audit/audit-trail.ts`
- Create: `UI/Angular/src/app/features/audit/audit-trail.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (route + `built` array)

**Interfaces:**
- Consumes: Task 1's `PagedResponse<AuditRecordResponse>` wire shape; `AuditRecordResponse` (existing FE); `PagedResponse` (`core/api/paged-response`); `Paginator`, `displayDate`, `CapabilityService`, `Router`, `ClientContextService`.
- Produces: `AuditService.clientAudit(skip, limit)`; `AuditTrail` component; route `/audit/trail`.

- [ ] **Step 1: Write the failing spec**

Create `audit-trail.spec.ts` (mirrors `entry-list.spec.ts` — stubs `AuditService`, uses `provideCapabilities`):
```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { of } from 'rxjs';
import { AuditTrail } from './audit-trail';
import { AuditService } from '../../core/audit/audit.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { PagedResponse } from '../../core/api/paged-response';
import { AuditRecordResponse } from '../../core/audit/audit';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000003';

function rec(o: Partial<AuditRecordResponse> = {}): AuditRecordResponse {
  return { sequence: 1, action: 'Created', entryId: 'e1', entryVersion: 1, at: '2026-03-15T00:00:00Z',
    reason: null, actor: { userId: 'u1', name: 'Alice', claims: [] }, ...o };
}
const page: PagedResponse<AuditRecordResponse> = {
  items: [rec({ sequence: 1, entryId: 'e1' }), rec({ sequence: 2, action: 'AccountCreated', entryId: null })],
  total: 3, skip: 0, limit: 2,
};

async function boot(caps: string[] = ['audit.read', 'gl.read']) {
  const stub = { clientAudit: vi.fn().mockReturnValue(of(page)) };
  await TestBed.configureTestingModule({
    imports: [AuditTrail],
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideCapabilities(...caps),
      { provide: AuditService, useValue: stub }],
  }).compileComponents();
  TestBed.inject(ClientContextService).select(clientId);
  const f = TestBed.createComponent(AuditTrail);
  f.detectChanges(); await f.whenStable(); f.detectChanges();
  return { f, stub };
}

describe('AuditTrail', () => {
  it('renders a row per record and the page count', async () => {
    const { f } = await boot();
    expect((f.nativeElement as HTMLElement).querySelectorAll('tbody tr').length).toBe(2);
    expect((f.nativeElement as HTMLElement).textContent).toContain('Page 1 of 2'); // total 3 / limit 2
    expect((f.nativeElement as HTMLElement).textContent).toContain('Alice');
  });

  it('drills a row with an entry to the journal entry when the user has gl.read', async () => {
    const { f } = await boot(['audit.read', 'gl.read']);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const rows = [...(f.nativeElement as HTMLElement).querySelectorAll('tbody tr')] as HTMLElement[];
    rows[0].dispatchEvent(new MouseEvent('click', { bubbles: true }));  // entryId e1
    rows[1].dispatchEvent(new MouseEvent('click', { bubbles: true }));  // entryId null → no nav
    expect(nav.mock.calls.map(c => c[0])).toEqual([['/journal', 'e1']]);
  });

  it('does not drill when the user lacks gl.read', async () => {
    const { f } = await boot(['audit.read']);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    ([...(f.nativeElement as HTMLElement).querySelectorAll('tbody tr')] as HTMLElement[])
      .forEach(r => r.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    expect(nav).not.toHaveBeenCalled();
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run (from `UI/Angular`): `npx ng test --include='**/audit-trail.spec.ts' --watch=false` → FAIL (cannot resolve `./audit-trail`).

- [ ] **Step 3: Add the `AuditVerifyResponse` interface**

In `core/audit/audit.ts`, append:
```ts
export interface AuditVerifyResponse {
  valid: boolean; recordCount: number; headSequence: number | null;
  failure: string | null; brokenAtSequence: number | null;
}
```

- [ ] **Step 4: Add `clientAudit` to the service**

In `core/audit/audit.service.ts`, add the imports and method:
- Add `import { Observable } from 'rxjs';` and `import { PagedResponse } from '../api/paged-response';` at the top.
- Add the method inside the class (after `entryAudit`):
```ts
  clientAudit(skip: number, limit: number): Observable<PagedResponse<AuditRecordResponse>> {
    return this.http.get<PagedResponse<AuditRecordResponse>>(
      `${environment.apiBaseUrl}/clients/${this.client.clientId()}/audit?skip=${skip}&limit=${limit}`,
    );
  }
```

- [ ] **Step 5: Create the `audit-trail` component**

Create `features/audit/audit-trail.ts`:
```ts
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ClientContextService } from '../../core/client/client-context.service';
import { AuditService } from '../../core/audit/audit.service';
import { AuditRecordResponse } from '../../core/audit/audit';
import { PagedResponse } from '../../core/api/paged-response';
import { CapabilityService } from '../../core/capabilities/capability.service';
import { displayDate } from '../../core/format/display';
import { Paginator } from '../../shared/paginator';

@Component({
  selector: 'app-audit-trail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmTableImports, Paginator],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <h1 class="text-2xl font-bold">Audit Trail</h1>

      @if (loading()) { <p class="text-muted-foreground text-sm">Loading…</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (!loading() && !error()) {
        @if (records().length === 0) {
          <p class="text-muted-foreground text-sm">No audit activity.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr><th hlmTh>#</th><th hlmTh>Date</th><th hlmTh>Action</th><th hlmTh>Actor</th><th hlmTh>Reason</th><th hlmTh>Entry</th></tr>
              </thead>
              <tbody hlmTBody>
                @for (r of records(); track r.sequence) {
                  <tr hlmTr
                      [class]="canOpen(r) ? 'cursor-pointer hover:bg-muted/50' : ''"
                      [attr.role]="canOpen(r) ? 'button' : null"
                      [attr.tabindex]="canOpen(r) ? 0 : null"
                      (click)="canOpen(r) && open(r)"
                      (keydown.enter)="canOpen(r) && open(r)">
                    <td hlmTd>{{ r.sequence }}</td>
                    <td hlmTd>{{ formatDate(r.at) }}</td>
                    <td hlmTd>{{ r.action }}</td>
                    <td hlmTd>{{ r.actor.name ?? r.actor.userId }}</td>
                    <td hlmTd>{{ r.reason ?? '—' }}</td>
                    <td hlmTd>{{ canOpen(r) ? '↗' : '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Audit pagination" (previous)="prevPage()" (next)="nextPage()" />
        }
      }
    </div>
  `,
})
export class AuditTrail {
  private readonly svc = inject(AuditService);
  private readonly client = inject(ClientContextService);
  private readonly caps = inject(CapabilityService);
  private readonly router = inject(Router);

  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  private readonly canDrill = computed(() => this.caps.has('gl.read'));

  private readonly query = computed(() => ({ id: this.client.clientId(), skip: this.skip(), limit: this.limit() }));

  private readonly pageData = toSignal(
    toObservable(this.query).pipe(
      tap(() => { this.loading.set(true); this.error.set(null); }),
      switchMap(({ id, skip, limit }) => {
        if (!id) { this.loading.set(false); return of(null); }
        return this.svc.clientAudit(skip, limit).pipe(
          tap(() => this.loading.set(false)),
          catchError((e: unknown) => {
            this.error.set((e as { message?: string })?.message ?? 'Error loading audit trail');
            this.loading.set(false);
            return of(null);
          }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<AuditRecordResponse> | null },
  );

  readonly records = computed(() => this.pageData()?.items ?? []);
  readonly pageCount = computed(() => {
    const p = this.pageData();
    if (!p || p.total === 0) return 1;
    return Math.ceil(p.total / p.limit);
  });
  readonly currentPage = computed(() => {
    const p = this.pageData();
    if (!p) return 1;
    return Math.floor(p.skip / p.limit) + 1;
  });

  canOpen(r: AuditRecordResponse): boolean { return this.canDrill() && !!r.entryId; }
  open(r: AuditRecordResponse): void { if (r.entryId) void this.router.navigate(['/journal', r.entryId]); }

  prevPage(): void { const s = this.skip(), l = this.limit(); if (s > 0) this.skip.set(Math.max(0, s - l)); }
  nextPage(): void { const s = this.skip(), l = this.limit(); if (this.currentPage() < this.pageCount()) this.skip.set(s + l); }

  formatDate(d: string): string { return displayDate(d); }
}
```

- [ ] **Step 6: Add the route**

In `app.routes.ts`:
- Add the import next to the other feature imports: `import { AuditTrail } from './features/audit/audit-trail';`
- Add a route entry BEFORE the placeholder-fallback IIFE (near the other top-level routes), e.g. after the `admin/approval-policy` route:
```ts
  { path: 'audit/trail', component: AuditTrail },
```
- Add `'/audit/trail'` to the `built` array inside the fallback IIFE (so it is excluded from the Placeholder fallback while `/audit/reconciliations` stays on it):
```ts
    const built = ['/dashboard', '/journal', '/trial-balance', '/statements', '/accounts', '/receivables', '/payables', '/payroll', '/fixed-assets', '/cash', '/inventory', '/admin/users', '/admin/access/sets', '/admin/access/sets/new', '/admin/approval-policy', '/audit/trail'];
```

- [ ] **Step 7: Run the spec + compile gate**

Run (from `UI/Angular`): `npx ng test --include='**/audit-trail.spec.ts' --watch=false` → 3 specs PASS.
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 8: Commit**

```bash
git add UI/Angular/src/app/core/audit/audit.ts UI/Angular/src/app/core/audit/audit.service.ts UI/Angular/src/app/features/audit/audit-trail.ts UI/Angular/src/app/features/audit/audit-trail.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): audit trail screen (paged, gl.read-gated journal drill)"
```

---

### Task 5: Frontend — Verify Integrity screen

**Files:**
- Modify: `UI/Angular/src/app/core/audit/audit.service.ts` (add `verify`)
- Create: `UI/Angular/src/app/features/audit/verify-integrity.ts`
- Create: `UI/Angular/src/app/features/audit/verify-integrity.spec.ts`
- Modify: `UI/Angular/src/app/app.routes.ts` (route + `/audit` redirect + `built` array)

**Interfaces:**
- Consumes: Task 2's `AuditVerifyResponse` wire shape; `AuditService`; `AuditVerifyResponse` (FE interface from Task 4).
- Produces: `AuditService.verify()`; `VerifyIntegrity` component; route `/audit/verify` + `/audit` redirect.

- [ ] **Step 1: Write the failing spec**

Create `verify-integrity.spec.ts`:
```ts
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { VerifyIntegrity } from './verify-integrity';
import { AuditService } from '../../core/audit/audit.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { AuditVerifyResponse } from '../../core/audit/audit';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000003';

async function boot(resp: AuditVerifyResponse) {
  const stub = { verify: vi.fn().mockReturnValue(of(resp)) };
  await TestBed.configureTestingModule({
    imports: [VerifyIntegrity],
    providers: [provideZonelessChangeDetection(), { provide: AuditService, useValue: stub }],
  }).compileComponents();
  TestBed.inject(ClientContextService).select(clientId);
  const f = TestBed.createComponent(VerifyIntegrity);
  f.detectChanges();
  return f;
}

function clickCheck(f: { nativeElement: HTMLElement }) {
  const btn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent?.includes('Check integrity'))!;
  btn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}

describe('VerifyIntegrity', () => {
  it('reports an intact chain', async () => {
    const f = await boot({ valid: true, recordCount: 42, headSequence: 42, failure: null, brokenAtSequence: null });
    clickCheck(f); f.detectChanges(); await f.whenStable(); f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).toContain('intact');
    expect((f.nativeElement as HTMLElement).textContent).toContain('42');
  });

  it('humanizes a tampered record', async () => {
    const f = await boot({ valid: false, recordCount: 42, headSequence: 42, failure: 'HashMismatch', brokenAtSequence: 12 });
    clickCheck(f); f.detectChanges(); await f.whenStable(); f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).toContain('Tampered record at sequence 12');
  });

  it('humanizes a truncated tail', async () => {
    const f = await boot({ valid: false, recordCount: 40, headSequence: 42, failure: 'TailTruncated', brokenAtSequence: 41 });
    clickCheck(f); f.detectChanges(); await f.whenStable(); f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).toContain('deleted from the end');
  });
});
```

- [ ] **Step 2: Run the spec to verify it fails**

Run (from `UI/Angular`): `npx ng test --include='**/verify-integrity.spec.ts' --watch=false` → FAIL (cannot resolve `./verify-integrity`).

- [ ] **Step 3: Add `verify` to the service**

In `core/audit/audit.service.ts`, add `AuditVerifyResponse` to the `./audit` import and the method (after `clientAudit`):
```ts
  verify(): Observable<AuditVerifyResponse> {
    return this.http.get<AuditVerifyResponse>(
      `${environment.apiBaseUrl}/clients/${this.client.clientId()}/audit/verify`,
    );
  }
```
(Update the existing import: `import { AuditRecordResponse, AuditVerifyResponse } from './audit';`.)

- [ ] **Step 4: Create the `verify-integrity` component**

Create `features/audit/verify-integrity.ts`:
```ts
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmButton } from '@spartan-ng/helm/button';
import { AuditService } from '../../core/audit/audit.service';
import { AuditVerifyResponse } from '../../core/audit/audit';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-verify-integrity',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-xl">
      <h1 class="text-2xl font-bold">Verify Integrity</h1>
      <p class="text-sm text-muted-foreground">
        Recompute the client's audit hash chain and reconcile it against the guarded chain head.
      </p>
      <button hlmBtn size="sm" class="w-fit" [disabled]="checking()" (click)="check()">Check integrity</button>

      @if (checking()) { <p class="text-muted-foreground text-sm">Checking…</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (result(); as r) {
        @if (r.valid) {
          <div class="rounded border border-border p-3 text-sm text-green-700 dark:text-green-400">
            ✅ Audit chain intact — {{ r.recordCount }} records verified.
          </div>
        } @else {
          <div class="rounded border border-destructive p-3 text-sm text-destructive flex flex-col gap-1">
            <span>❌ Integrity check failed: {{ describe(r) }}</span>
            <span class="text-muted-foreground">Contact a deployment administrator.</span>
          </div>
        }
      }
    </div>
  `,
})
export class VerifyIntegrity {
  private readonly svc = inject(AuditService);
  private readonly destroyRef = inject(DestroyRef);

  readonly result = signal<AuditVerifyResponse | null>(null);
  readonly checking = signal(false);
  readonly error = signal<string | null>(null);

  check(): void {
    this.checking.set(true);
    this.error.set(null);
    this.result.set(null);
    this.svc.verify().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (r) => { this.result.set(r); this.checking.set(false); },
      error: (e) => { this.error.set(extractProblem(e).detail); this.checking.set(false); },
    });
  }

  describe(r: AuditVerifyResponse): string {
    const at = r.brokenAtSequence;
    switch (r.failure) {
      case 'HashMismatch': return `Tampered record at sequence ${at}.`;
      case 'BrokenLink': return `Broken chain link at sequence ${at}.`;
      case 'SequenceGap': return `Missing record at sequence ${at}.`;
      case 'TailTruncated': return `Records deleted from the end of the chain (missing from sequence ${at}).`;
      case 'HeadMismatch': return `Chain head mismatch — the recorded head does not match the chain tail.`;
      default: return `The audit chain could not be verified.`;
    }
  }
}
```

- [ ] **Step 5: Add the route + `/audit` redirect**

In `app.routes.ts`:
- Import: `import { VerifyIntegrity } from './features/audit/verify-integrity';`
- Add routes near the `audit/trail` route:
```ts
  { path: 'audit/verify', component: VerifyIntegrity },
  { path: 'audit', redirectTo: 'audit/trail', pathMatch: 'full' },
```
- Add `'/audit/verify'` to the `built` array (now: `..., '/audit/trail', '/audit/verify'`).

- [ ] **Step 6: Run the spec + compile gate**

Run (from `UI/Angular`): `npx ng test --include='**/verify-integrity.spec.ts' --watch=false` → 3 specs PASS.
Run: `npx ng build --configuration development` → `Application bundle generation complete`.

- [ ] **Step 7: Commit**

```bash
git add UI/Angular/src/app/core/audit/audit.service.ts UI/Angular/src/app/features/audit/verify-integrity.ts UI/Angular/src/app/features/audit/verify-integrity.spec.ts UI/Angular/src/app/app.routes.ts
git commit -m "feat(ui): verify-integrity screen (diagnostic audit-chain check)"
```

---

## Self-Review

**Spec coverage:**
- B1 audit-log dual-shape pagination + `CountForClientAsync` → Task 1. ✓
- B2 verify diagnostic (`VerifyDetailedAsync` + `AuditChainVerification`/`AuditChainFailure` taxonomy + extended `AuditVerifyResponse` + `VerifyAsync` delegates) → Task 2, with one engine test per failure kind + happy path. ✓
- B3 `audit.read` enforcement via `ResolveCapabilityAsync` on the two Audit-area endpoints; entry-timeline stays `gl.read` → Task 3, proven by the ArClerk boundary test. ✓
- F1 service methods + interfaces → Tasks 4/5. ✓
- F2 Audit Trail (paged, actor/action/reason columns, `gl.read`-gated whole-row journal drill, non-entry rows inert) → Task 4. ✓
- F3 Verify Integrity (action card, humanized diagnostic per failure kind) → Task 5. ✓
- F4 routes (`/audit/trail`, `/audit/verify`, `/audit`→redirect) added to `built` so `/audit/reconciliations` stays on Placeholder; nav-gate (`area: 'audit'` → `audit.read`) already in place, no new route guard → Tasks 4/5. ✓
- Testing: backend pagination + verify taxonomy + gating; FE trail paging/render/drill + verify pass/fail-diagnostic. ✓

**Placeholder scan:** every step contains complete code; no TBD.

**Type/name consistency:** `AuditVerifyResponse(bool Valid, long RecordCount, long? HeadSequence, string? Failure, long? BrokenAtSequence)` identical backend record ↔ FE interface (`valid, recordCount, headSequence, failure, brokenAtSequence`); `AuditChainFailure` enum names (`SequenceGap/BrokenLink/HashMismatch/TailTruncated/HeadMismatch`) are exactly the strings the FE `describe()` switches on and the tests assert; `CountForClientAsync`/`VerifyDetailedAsync`/`ResolveCapabilityAsync`/`clientAudit`/`verify` names consistent across producer and consumer; `PagedResponse<AuditRecordResponse>` shape matches `core/api/paged-response.ts`; the `built` array grows to include exactly `/audit/trail` + `/audit/verify` (never bare `/audit`, which would strand `/audit/reconciliations`); `Capabilities.AuditRead` = `"audit.read"` = the `area: 'audit'` nav gate via `hasArea`.

## Execution Handoff

Two execution options:
1. **Subagent-Driven (recommended)** — fresh implementer per task, per-task review, final whole-branch review.
2. **Inline Execution** — execute in this session with checkpoints.
