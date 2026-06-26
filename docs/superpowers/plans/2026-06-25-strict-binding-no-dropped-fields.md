# Strict JSON binding — reject unknown/misnamed fields — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Stop the API silently dropping misnamed/unknown JSON fields. (1) Align the entry-type field (`entryType`→`type`) to match the response; (2) reject any unmapped JSON property across the whole Host (engine + modules) with a clear `400` naming the field.

**Architecture:** A single `ConfigureHttpJsonOptions(UnmappedMemberHandling.Disallow)` covers every endpoint (one `WebApplication`); early middleware turns the resulting `JsonException` into a ProblemDetails whose Detail names the field. The contract rename removes the one request/response naming asymmetry so strict binding doesn't turn an intuitive round-trip into a 400.

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs, System.Text.Json, xUnit + WebApplicationFactory + EphemeralMongo.

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- Reject unknown fields; honor mapped fields unchanged (no silent drops). The 400 must name the offending field.
- One global config covers engine + Receivables + Payables (single Host `WebApplication`).
- Do Task 1 (rename) BEFORE Task 2 (strict binding), so `type` is a mapped field when Disallow turns on.
- Tests use EphemeralMongo / WebApplicationFactory; run a class at a time when host-boot flakiness appears.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage explicit file lists; check for stray churn.

---

## Task 1: Align the entry-type field (`entryType` → `type`)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/PostEntryRequest.cs` (rename member `EntryType` → `Type`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`ParsePostableType(request.EntryType)` → `request.Type`)
- Modify: any caller/test constructing `PostEntryRequest` with a named `EntryType:` arg → `Type:`
- Test: `Backend/Accounting101.Ledger.Api.Tests/` (add a round-trip type test)

**Interfaces:**
- Produces: `PostEntryRequest.Type` (string?, wire field `type`) — matches `EntryResponse.Type`.

- [ ] **Step 1: Write/adjust the failing test**

```csharp
[Fact]
public async Task Posting_with_type_Adjusting_stores_Adjusting()
{
    // POST /entries with the body's entry-type field named "type": "Adjusting" (a balanced entry),
    // approve it, GET it back, assert the response "type" == "Adjusting".
    // (Before the rename, sending "type" was silently dropped and stored "Standard" — this is the regression.)
}
```

> Use the existing Api.Tests host/auth harness. Build the request JSON with the field named `type` (what a caller naturally uses, matching the GET response).

- [ ] **Step 2: Run, confirm fail** — today `type` is unmapped (request field is `entryType`), so it stores `Standard` and the assertion fails.
Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter "Posting_with_type_Adjusting_stores_Adjusting"`.

- [ ] **Step 3: Implement**
- In `PostEntryRequest.cs`, rename the last positional member `string? EntryType = null` → `string? Type = null`; update the doc comment (`"Standard" (default) or "Adjusting"`).
- In `LedgerEndpoints.cs`, change `ParsePostableType(request.EntryType)` → `ParsePostableType(request.Type)`.
- Search the solution for `.EntryType` and `EntryType:` on `PostEntryRequest` and update (positional constructions need no change). Do NOT touch the domain `EntryType` enum or `JournalEntry.Type` — only the request DTO member.

- [ ] **Step 4: Run, confirm pass** — the new test passes; run `CommandQueryTests` (which constructs entries with the type) to confirm no regression.

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Ledger.Contracts/PostEntryRequest.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs <touched test/caller files>
git commit -m "feat(contracts): align entry-type request field to 'type' (matches the response; honors Adjusting)"
```

---

## Task 2: Strict binding — reject unmapped JSON fields with a clear 400

**Files:**
- Modify: `Backend/Accounting101.Host/Program.cs` (JSON config + 400-surfacing middleware) — or the existing `Accounting101.Ledger.Api.Hosting` seam if one is cleaner; it must cover engine + modules.
- Test: `Backend/Accounting101.Ledger.Api.Tests/StrictBindingTests.cs` (create)

**Interfaces:**
- Consumes: Task 1's `type` field (so `type` is mapped and only genuinely-unknown fields are rejected).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Unknown_field_in_entry_post_returns_400_naming_the_field()
{
    // POST /clients/{id}/entries with a balanced entry body PLUS an unmapped field, e.g. "entryType":"Adjusting"
    // (the now-old name) or "bogus":1.
    HttpResponseMessage resp = await Client.PostAsync($"/clients/{clientId}/entries", JsonBodyWithExtraField());
    Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    string body = await resp.Content.ReadAsStringAsync();
    Assert.Contains("entryType", body);   // (or the bogus field name) — the 400 names the offending field
}

[Fact]
public async Task Well_formed_entry_post_still_succeeds()
{
    // a valid typed PostEntryRequest (only mapped fields) -> 201, unchanged.
}

[Fact]
public async Task Misnamed_date_field_is_rejected_not_silently_defaulted()
{
    // POST an entry body with "date" instead of "effectiveDate" -> 400 naming "date" (proves the date footgun's
    // ROOT is caught at the boundary, not silently defaulted to 0001-01-01).
}
```

> Build these bodies as raw JSON strings/objects (not typed `PostEntryRequest`), so you can include the unmapped field. Reuse the existing host/auth harness for the client + token.

- [ ] **Step 2: Run, confirm fail** — today the unknown field is silently ignored and the post returns `201` (or `200`), so the `400`/Contains assertions fail.

- [ ] **Step 3: Implement**

In `Program.cs` (before `app.Build()`):
```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);
```
After `app.Build()`, before the `Map*Endpoints()` calls, add the surfacing middleware:
```csharp
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (BadHttpRequestException ex) when (ex.InnerException is System.Text.Json.JsonException je)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title  = "Invalid request body",
            Detail = je.Message,
        }, ctx.RequestAborted);
    }
});
```
> Confirm `BadHttpRequestException` is what minimal-API body binding throws on a JSON failure in .NET 10, with the `JsonException` as `InnerException`. If the framework surfaces it differently (e.g. the exception is the `JsonException` directly, or a different wrapper), adjust the `catch` filter accordingly — the goal is: any JSON body-binding failure → 400 ProblemDetails whose Detail is the JsonException message. Verify with the Step-1 tests.

- [ ] **Step 4: Run, confirm pass; then sweep for regressions**
- New `StrictBindingTests` green.
- **Run the full affected suites** and fix fallout: `Accounting101.Ledger.Api.Tests`, the `Accounting101.Ledger.Mongo.Tests` classes that post through the API host (if any), and `Accounting101.Receivables.Tests` / `Accounting101.Payables.Tests`. Any test that sent a body with an extra/misnamed field will now `400`. For each: confirm it was relying on the silent-ignore (not a real product behavior), fix the test payload to use only mapped fields, and record it. If a break reveals a real consumer that legitimately sends extra fields, STOP and report rather than weakening the binding.
Run each class individually (host-boot/EphemeralMongo flakiness).

- [ ] **Step 5: Build clean, commit**
```bash
git add Backend/Accounting101.Host/Program.cs Backend/Accounting101.Ledger.Api.Tests/StrictBindingTests.cs <any test payloads fixed>
git commit -m "feat(host): reject unmapped JSON fields with a clear 400 (no silently-dropped input)"
```

---

## Final verification
- [ ] `dotnet build` full solution → 0 warnings.
- [ ] Run individually: `StrictBindingTests`, the type round-trip test, `CommandQueryTests`, `PostingValidationTests`, `IdempotentPostTests`, `PeriodCloseApiTests`, and the Receivables/Payables suites — all green.
- [ ] Confirm: `type: "Adjusting"` is honored; an unknown field anywhere (engine or module) → `400` naming it; valid requests unchanged.
- [ ] Whole-branch review on the most capable model (a global request-handling + contract change), then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- Spec coverage: field alignment (Task 1 + test), strict binding global (Task 2 config), clear 400 naming the field (Task 2 middleware + tests), date-field root caught (Task 2 test), regression sweep (Task 2 Step 4).
- Type consistency: `PostEntryRequest.Type` matches `EntryResponse.Type`; `ParsePostableType(request.Type)`.
- Open implementer checks (flagged): (a) the exact exception type minimal-API body binding throws on a JSON failure in .NET 10 (Task 2 Step 3); (b) the breadth of existing tests that relied on silent-ignore (Task 2 Step 4).
