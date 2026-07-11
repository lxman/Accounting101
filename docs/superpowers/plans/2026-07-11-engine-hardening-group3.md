# Engine hardening group-3 + leftovers — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the 9 remaining group-3 hardening items — covering index, `asOf` account-balance, discovery + typo guards, actionable parse errors, atomic batch POST, read-response labeling, reversal own-source override, readCap drift-guard test, and per-category revenue readiness.

**Architecture:** Mostly additive changes across the ledger engine (`Ledger.Api`, `Ledger.Mongo`, `Ledger.Contracts`) plus one module (`Receivables.Api`) and one UI spec. The batch POST reuses the existing single-post building blocks (`ValidateForPostAsync`, `AppendSequencedAsync`, `InTransactionAsync`) so a batch is N appends inside one transaction with one gapless sequence run. The one behavior change is the typo guard (tightened dimension validation).

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB.Driver, xUnit, Angular/Jasmine.

## Global Constraints

- Build/test the solution via **`Accounting101.slnx`** (NOT `.sln`). Example: `dotnet test Accounting101.slnx`.
- USD-only; do not add currency/FX handling.
- All new endpoints are member-gated exactly like their neighbors (`gateway.ResolveAsync(..., Permission.Read, ...)` for reads; `ResolveForPostAsync` for the batch write).
- No breaking changes to existing wire contracts: add new response fields **only** as trailing optional parameters (`= null`) on records.
- Commit after each task with a `feat(hardening):` / `test(hardening):` prefixed message ending with the co-author trailer:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- Whole solution must stay green after every task.

---

### Task 1: Covering index — add EffectiveDate to `client_status_posting`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs:245-267` (`EnsureIndexesAsync`)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/JournalStoreIndexTests.cs` (create)

**Interfaces:**
- Produces: no code interface change. The `EnsureIndexesAsync` method keeps its signature; the index named `client_status_posting` is replaced by `client_status_posting_effdate` (4 keys: ClientId, Status, Posting, EffectiveDate).

- [ ] **Step 1: Write the failing test**

Create `Backend/Accounting101.Ledger.Mongo.Tests/JournalStoreIndexTests.cs`. Use the shared EphemeralMongo (see any existing `*Tests` in that project for the `SharedMongo` fixture usage — e.g. `MongoJournalStoreTests`; copy its database-acquisition pattern exactly).

```csharp
using Accounting101.Ledger.Mongo;
using Accounting101.TestSupport;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Accounting101.Ledger.Mongo.Tests;

public sealed class JournalStoreIndexTests
{
    [Fact]
    public async Task EnsureIndexes_creates_the_four_key_covering_index_and_drops_the_old_three_key()
    {
        IMongoDatabase db = SharedMongo.Database($"idx_{Guid.NewGuid():N}");
        var store = new MongoJournalStore(db);

        await store.EnsureIndexesAsync();

        List<BsonDocument> indexes = await (await db.GetCollection<BsonDocument>("journal").Indexes.ListAsync()).ToListAsync();
        List<string> names = indexes.Select(i => i["name"].AsString).ToList();

        Assert.Contains("client_status_posting_effdate", names);
        Assert.DoesNotContain("client_status_posting", names);

        BsonDocument covering = indexes.Single(i => i["name"].AsString == "client_status_posting_effdate");
        BsonDocument key = covering["key"].AsBsonDocument;
        Assert.Equal(new[] { "ClientId", "Status", "Posting", "EffectiveDate" }, key.Names.ToArray());
    }
}
```

Note: confirm the collection name (`"journal"`) and the `SharedMongo.Database(...)` accessor against an existing test in this project before running; match whatever they use.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~JournalStoreIndexTests`
Expected: FAIL — the covering index is named `client_status_posting` with only 3 keys.

- [ ] **Step 3: Implement**

In `EnsureIndexesAsync`, replace the first index model (lines 252-253) with the 4-key form, and drop the old index by name first so the rename takes effect on an existing database:

```csharp
public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
{
    IndexKeysDefinitionBuilder<JournalEntryDocument> keys = Builders<JournalEntryDocument>.IndexKeys;

    // The 3-key client_status_posting was superseded by the 4-key covering index below; drop it so the
    // windowed folds stop residual-scanning on EffectiveDate. Ignore "not found" on a fresh database.
    try { await _entries.Indexes.DropOneAsync("client_status_posting", cancellationToken); }
    catch (MongoCommandException ex) when (ex.CodeName == "IndexNotFound") { }

    CreateIndexModel<JournalEntryDocument>[] models =
    [
        new(keys.Ascending(e => e.ClientId).Ascending(e => e.Status).Ascending(e => e.Posting).Ascending(e => e.EffectiveDate),
            new CreateIndexOptions { Name = "client_status_posting_effdate" }),
        new(keys.Ascending(e => e.ClientId).Ascending("Lines.AccountId"),
            new CreateIndexOptions { Name = "client_lineAccount" }),
        new(keys.Ascending(e => e.ClientId).Ascending(e => e.EffectiveDate),
            new CreateIndexOptions { Name = "client_effectiveDate" }),
        new(keys.Ascending(e => e.ClientId).Ascending(e => e.SequenceNumber),
            new CreateIndexOptions { Name = "client_sequence_unique", Unique = true }),
        new(keys.Ascending(e => e.ClientId).Ascending(e => e.SourceRef),
            new CreateIndexOptions { Name = "client_sourceRef" }),
        new(keys.Ascending(e => e.ClientId).Ascending("Lines.Dimensions.Type").Ascending("Lines.Dimensions.Value"),
            new CreateIndexOptions { Name = "client_lineDimension" }),
    ];

    await _entries.Indexes.CreateManyAsync(models, cancellationToken);
}
```

Confirm `using MongoDB.Driver;` is present (it is — the file already uses driver types).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~JournalStoreIndexTests`
Expected: PASS.

- [ ] **Step 5: Run the Mongo test project to catch regressions**

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests/Accounting101.Ledger.Mongo.Tests.csproj`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs Backend/Accounting101.Ledger.Mongo.Tests/JournalStoreIndexTests.cs
git commit -m "feat(hardening): 4-key covering index (client+status+posting+effdate)"
```

---

### Task 2: `asOf` on `GET /accounts/{id}/balance`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs:655-663` (`GetAccountBalance`)
- Test: `Backend/Accounting101.Ledger.Api.Tests/AccountBalanceAsOfTests.cs` (create)

**Interfaces:**
- Consumes: `ctx.Ledger.Journal.AggregateBalancesAsync(clientId, asOfDate, ct)` (existing; used by `GetTrialBalance` at line 529-530) and `ctx.Ledger.Projection.GetBalanceAsync(clientId, accountId, ct)` (existing).
- Produces: `GET /clients/{id}/accounts/{accountId}/balance?asOf=YYYY-MM-DD` returns the folded balance for that date; absent `asOf` unchanged.

- [ ] **Step 1: Write the failing test**

Study an existing API E2E test in `Backend/Accounting101.Ledger.Api.Tests/` (e.g. any test that posts + approves an entry then reads a balance) to reuse its `WebApplicationFactory` fixture, auth header helper (DevToken scheme — `Authorization: DevToken ...`, NOT Bearer), and client-onboarding helper. Mirror that harness exactly.

```csharp
[Fact]
public async Task AccountBalance_with_asOf_folds_to_that_date_and_matches_trial_balance()
{
    // Arrange: onboard a client, post+approve one entry dated 2026-03-15 that debits account A.
    // (Reuse the fixture's post+approve helper; capture accountA id and clientId.)
    // ... harness setup ...

    // A date BEFORE the entry → zero; a date ON/after → the entry amount.
    var before = await client.GetFromJsonAsync<AccountBalanceResponse>(
        $"/clients/{clientId}/accounts/{accountA}/balance?asOf=2026-03-14");
    var after = await client.GetFromJsonAsync<AccountBalanceResponse>(
        $"/clients/{clientId}/accounts/{accountA}/balance?asOf=2026-03-15");
    var tb = await client.GetFromJsonAsync<TrialBalanceResponse>(
        $"/clients/{clientId}/trial-balance?asOf=2026-03-15");

    Assert.Equal(0m, before!.Balance);
    Assert.Equal(tb!.Accounts.Single(a => a.AccountId == accountA).Balance, after!.Balance);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~AccountBalanceAsOfTests`
Expected: FAIL — `asOf` is ignored; `before` returns the live projection (the entry amount), not 0.

- [ ] **Step 3: Implement**

Replace `GetAccountBalance` (lines 655-663):

```csharp
private static async Task<IResult> GetAccountBalance(
    Guid clientId, Guid accountId, DateOnly? asOf, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
{
    LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
    if (ctx.Failed) return ctx.Error;

    // Absent asOf: the O(1) live projection. With asOf: a point-in-time fold from the journal, exactly as
    // GetTrialBalance does, then plucked for this account (0 when it has no activity through that date).
    decimal balance = asOf is { } asOfDate
        ? (await ctx.Ledger.Journal.AggregateBalancesAsync(clientId, asOfDate, cancellationToken)).GetValueOrDefault(accountId)
        : await ctx.Ledger.Projection.GetBalanceAsync(clientId, accountId, cancellationToken);

    return Results.Ok(new AccountBalanceResponse(accountId, balance));
}
```

Minimal-API binds the new `DateOnly? asOf` parameter from the query string automatically (same as `GetTrialBalance`). No route registration change needed.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~AccountBalanceAsOfTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/AccountBalanceAsOfTests.cs
git commit -m "feat(hardening): asOf fold on GET /accounts/{id}/balance"
```

---

### Task 3: Labeling — account number + name on balance/trial-balance/subledger responses

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/EntryResponses.cs:49-51` (`AccountBalanceResponse`, `TrialBalanceResponse` unchanged shape but its lines carry the new fields)
- Modify: `Backend/Accounting101.Ledger.Contracts/SubledgerContracts.cs:13` (`SubledgerLineResponse`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` — `GetAccountBalance` (Task 2 form), `GetTrialBalance` (~522-533), `GetSubledger` (~562-568), and the `ToAccountBalances` helper (~844-845)
- Test: `Backend/Accounting101.Ledger.Api.Tests/AccountLabelingTests.cs` (create)

**Interfaces:**
- Consumes: `ctx.Ledger.Accounts.GetChartAsync(clientId, ct)` returning `ChartOfAccounts` with `chart.Find(Guid) -> Account?` (used at line 1108/1116); `Account.Number` (string), `Account.Name` (string).
- Produces: `AccountBalanceResponse(Guid AccountId, decimal Balance, string? Number = null, string? Name = null)`; `SubledgerLineResponse(Guid AccountId, Guid DimensionValue, decimal Balance, string? Number = null, string? Name = null)`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task AccountBalance_and_trial_balance_carry_account_number_and_name()
{
    // Arrange: onboard client, create account "1000"/"Cash", post+approve an entry touching it.
    // ... harness setup (reuse Task 2 fixture) ...

    var bal = await client.GetFromJsonAsync<AccountBalanceResponse>($"/clients/{clientId}/accounts/{cash}/balance");
    Assert.Equal("1000", bal!.Number);
    Assert.Equal("Cash", bal.Name);

    var tb = await client.GetFromJsonAsync<TrialBalanceResponse>($"/clients/{clientId}/trial-balance");
    AccountBalanceResponse line = tb!.Accounts.Single(a => a.AccountId == cash);
    Assert.Equal("1000", line.Number);
    Assert.Equal("Cash", line.Name);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~AccountLabelingTests`
Expected: FAIL — `Number`/`Name` do not exist on `AccountBalanceResponse` (compile error), then null once added but before wiring.

- [ ] **Step 3: Add the optional fields to the records**

`EntryResponses.cs:49`:
```csharp
public sealed record AccountBalanceResponse(Guid AccountId, decimal Balance, string? Number = null, string? Name = null);
```
`SubledgerContracts.cs:13`:
```csharp
public sealed record SubledgerLineResponse(Guid AccountId, Guid DimensionValue, decimal Balance, string? Number = null, string? Name = null);
```

- [ ] **Step 4: Wire enrichment in the three endpoints**

Add a shared helper near `ToAccountBalances` (line 844) that maps balances to labeled responses using a chart:

```csharp
private static List<AccountBalanceResponse> ToLabeledBalances(
    IReadOnlyDictionary<Guid, decimal> balances, ChartOfAccounts chart) =>
    balances.Select(kv =>
    {
        Account? a = chart.Find(kv.Key);
        return new AccountBalanceResponse(kv.Key, kv.Value, a?.Number, a?.Name);
    }).ToList();
```

In `GetTrialBalance` (line 533), load the chart and use the labeled mapper:
```csharp
ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
return Results.Ok(new TrialBalanceResponse(asOf, ToLabeledBalances(balances, chart)));
```

In `GetAccountBalance` (Task 2 form), after computing `balance`, load the chart and label:
```csharp
Account? account = (await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken)).Find(accountId);
return Results.Ok(new AccountBalanceResponse(accountId, balance, account?.Number, account?.Name));
```

In `GetSubledger` (line 562-568), load the chart and label each line:
```csharp
ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
return Results.Ok(new SubledgerResponse(
    dimension, asOf,
    balances.Select(b =>
    {
        Account? a = chart.Find(b.AccountId);
        return new SubledgerLineResponse(b.AccountId, b.DimensionValue, b.Balance, a?.Number, a?.Name);
    }).ToList()));
```

Confirm `ChartOfAccounts` and `Account` are already in scope in this file (they are — used by `ChartFieldViolationsAsync`). Leave `ToAccountBalances` (used by `CloseResponse`) as-is.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~AccountLabelingTests`
Expected: PASS.

- [ ] **Step 6: Run the API test project**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: green (existing tests that deserialize these records still pass — fields are additive/optional).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/EntryResponses.cs Backend/Accounting101.Ledger.Contracts/SubledgerContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/AccountLabelingTests.cs
git commit -m "feat(hardening): account number+name on balance/trial-balance/subledger responses"
```

---

### Task 4: Actionable enum parse errors (account + onboarding paths)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` — `UpsertAccount` handler (find it near line 700-720, the one that calls `MapAccount` and catches `ArgumentException`), `MapAccount` (825-843), onboarding opening-line mapping `MapOpeningLines` (~1052) and its caller `Onboard`.
- Test: `Backend/Accounting101.Ledger.Api.Tests/EnumParseErrorTests.cs` (create)

**Interfaces:**
- Produces: a private helper `TryParseEnum<TEnum>(string? raw, string field, out TEnum value, out (string Field, string[] Msgs)? error) where TEnum : struct, Enum`. Used to turn bad `Type`/`CashFlowActivity`/`Direction` strings into structured `ValidationProblem` (422) responses instead of raw `Enum.Parse` exceptions.

Read `UpsertAccount` and `Onboard` in full before editing (around lines 690-740 and 360-380). The current `UpsertAccount` maps via `MapAccount` (which throws `Enum.Parse` for a bad `Type`/`CashFlowActivity`) and catches `ArgumentException` at ~707 to return `Unprocessable(ex.Message)` — a raw `"Requested value 'aset' was not found."`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task UpsertAccount_with_invalid_type_returns_structured_422_naming_valid_values()
{
    // Arrange: onboard client. PUT an account with Type = "aset".
    var body = new { Number = "1000", Name = "Cash", Type = "aset" };
    HttpResponseMessage res = await client.PutAsJsonAsync($"/clients/{clientId}/accounts/{Guid.NewGuid()}", body);

    Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    ValidationProblemDetails? p = await res.Content.ReadFromJsonAsync<ValidationProblemDetails>();
    Assert.True(p!.Errors.ContainsKey("type"));
    Assert.Contains("Asset", p.Errors["type"][0]);           // lists the valid values
    Assert.Contains("aset", p.Errors["type"][0]);            // echoes the bad value
}

[Fact]
public async Task Onboarding_with_invalid_direction_returns_structured_422()
{
    // POST /onboarding with an opening line whose Direction = "dr".
    // Assert 422 ValidationProblemDetails with a "balances[0].direction"-style key naming Debit/Credit.
}
```

For the onboarding test, inspect the actual onboarding request shape (`OnboardingRequest` uses `OpeningBalanceLine` which has NO Direction — the sign of `Balance` conveys side). **Verify** whether onboarding actually parses a `Direction` enum. If `MapOpeningLines` does NOT parse an enum (opening lines are signed decimals, not direction strings), then onboarding has no enum-parse hazard — in that case DROP the onboarding test and note it, and scope Task 4 to `UpsertAccount`'s `Type` and `CashFlowActivity` only. (The spec's onboarding mention was provisional; the account path is the confirmed one.)

- [ ] **Step 2: Run the test(s) to verify they fail**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~EnumParseErrorTests`
Expected: FAIL — response is a plain 422 with the raw `Enum.Parse` message, no `type` errors key.

- [ ] **Step 3: Add the helper**

Add near the other mapping helpers (after `ValidationProblem`, ~line 810):

```csharp
/// <summary>Parse an enum case-insensitively, or produce a structured field error listing the valid
/// values — so a bad wire value returns an actionable 422 instead of a raw Enum.Parse message.</summary>
private static bool TryParseEnum<TEnum>(
    string? raw, string field, out TEnum value, out KeyValuePair<string, string[]>? error)
    where TEnum : struct, Enum
{
    if (Enum.TryParse(raw, ignoreCase: true, out value) && Enum.IsDefined(value))
    {
        error = null;
        return true;
    }
    string valid = string.Join(", ", Enum.GetNames<TEnum>());
    error = new KeyValuePair<string, string[]>(
        field, [$"'{raw}' is not a valid {typeof(TEnum).Name}. Valid: {valid}."]);
    return false;
}
```

- [ ] **Step 4: Use it in `UpsertAccount`**

Refactor `UpsertAccount` so it validates `Type` and `CashFlowActivity` via `TryParseEnum` BEFORE calling `MapAccount`, collecting errors into a dictionary and returning `ValidationProblem` on any failure. Change `MapAccount` to accept the already-parsed `AccountType`/`CashFlowActivity?` (remove the `Enum.Parse` calls at 831/839). Concretely, add a pre-parse block in the handler:

```csharp
Dictionary<string, string[]> parseErrors = [];
if (!TryParseEnum<AccountType>(request.Type, "type", out AccountType accountType, out var typeErr))
    parseErrors.Add(typeErr!.Value.Key, typeErr.Value.Value);

CashFlowActivity? cashFlow = null;
if (request.CashFlowActivity is { } cfa)
{
    if (TryParseEnum<CashFlowActivity>(cfa, "cashFlowActivity", out CashFlowActivity parsedCfa, out var cfaErr))
        cashFlow = parsedCfa;
    else
        parseErrors.Add(cfaErr!.Value.Key, cfaErr.Value.Value);
}

if (parseErrors.Count > 0) return ValidationProblem(parseErrors);
```

Then change `MapAccount`'s signature to `MapAccount(Guid clientId, Guid accountId, AccountRequest request, AccountType type, CashFlowActivity? cashFlow)` and set `Type = type` / `CashFlowActivity = cashFlow` (delete lines 831 and 837-839's `Enum.Parse`). Keep the existing `catch (ArgumentException) => Unprocessable(...)` for the remaining `MapAccount` throws (e.g. unknown `RequiredDimension` normalization) — those are not enum parses.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~EnumParseErrorTests`
Expected: PASS.

- [ ] **Step 6: Run the API test project**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: green.

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/EnumParseErrorTests.cs
git commit -m "feat(hardening): structured enum parse errors on account upsert"
```

---

### Task 5: Typo guard — reject undeclared dimension keys on control accounts

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs:1105-1140` (`ChartFieldViolationsAsync`)
- Test: `Backend/Accounting101.Ledger.Api.Tests/DimensionTypoGuardTests.cs` (create)

**Interfaces:**
- Consumes: `Account.RequiredDimensions` (IReadOnlyList<string>), `Line.Dimensions` (IReadOnlyDictionary<string, Guid>).
- Produces: no signature change; `ChartFieldViolationsAsync` now also emits a `lines[i].accountId` error when a control account's line carries a dimension key not in its `RequiredDimensions`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Posting_a_typod_dimension_key_on_a_control_account_returns_422()
{
    // Arrange: onboard client; create a control account "1200"/"Accounts Receivable" with RequiredDimensions=["Customer"].
    // Post an entry whose AR line carries Dimensions = { "Custommer": <guid> } (typo) balanced by a cash line.
    HttpResponseMessage res = await client.PostAsJsonAsync($"/clients/{clientId}/entries", request);

    Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    ValidationProblemDetails? p = await res.Content.ReadFromJsonAsync<ValidationProblemDetails>();
    Assert.Contains(p!.Errors, e => e.Key.StartsWith("lines[") && e.Value.Any(m => m.Contains("Custommer") && m.Contains("Customer")));
}

[Fact]
public async Task Posting_a_dimension_key_on_a_non_control_account_is_allowed()
{
    // A plain expense account (no RequiredDimensions) with a line carrying an informational dimension → 201/accepted.
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~DimensionTypoGuardTests`
Expected: the typo test FAILS (entry currently posts — the extra key is silently stored). The non-control test passes.

- [ ] **Step 3: Implement**

In `ChartFieldViolationsAsync`, inside the `else` branch (account is non-null, ~after line 1132), add the reverse check — every key the line carries must be declared, but only for control accounts (those with at least one required dimension):

```csharp
foreach (string dimension in account.RequiredDimensions)
    if (!line.Dimensions.ContainsKey(dimension))
        lineErrors.Add($"Account {account.Number} \"{account.Name}\" requires a {dimension} on the posting line.");

// Typo guard: a control account (one that declares required dimensions) must not carry an UNDECLARED
// dimension key — a misspelled key ("Custommer") would otherwise be stored silently and the subledger
// fold, which keys on the declared dimension, would never see it. Non-control accounts are untouched.
if (account.RequiredDimensions.Count > 0)
    foreach (string key in line.Dimensions.Keys)
        if (!account.RequiredDimensions.Contains(key))
            lineErrors.Add(
                $"Account {account.Number} \"{account.Name}\" does not declare the dimension '{key}' "
                + $"(expected: {string.Join(", ", account.RequiredDimensions)}).");
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~DimensionTypoGuardTests`
Expected: PASS.

- [ ] **Step 5: Run the full solution to catch any module/E2E that posts an undeclared key**

Run: `dotnet test Accounting101.slnx`
Expected: green. **If a module test fails because a recipe posts an extra dimension to a control account, that is a real latent bug the guard just surfaced** — investigate the recipe (it is likely sending a dimension the account does not declare). Fix the recipe or the account's `RequiredDimensions`, do not weaken the guard. Report any such finding.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/DimensionTypoGuardTests.cs
git commit -m "feat(hardening): reject undeclared dimension keys on control accounts (typo guard)"
```

---

### Task 6: Discovery endpoints — `GET /dimensions` and `GET /source-types`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs` (add `DistinctSourceTypesAsync`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` — register two routes (near lines 44-57) + two handlers
- Test: `Backend/Accounting101.Ledger.Api.Tests/DiscoveryEndpointsTests.cs` (create)

**Interfaces:**
- Produces:
  - `MongoJournalStore.DistinctSourceTypesAsync(Guid clientId, CancellationToken ct) -> Task<IReadOnlyList<string>>` — distinct non-null `SourceType` values in the client's journal, sorted.
  - `GET /clients/{id}/dimensions -> string[]` (distinct `RequiredDimensions` across the chart, sorted).
  - `GET /clients/{id}/source-types -> string[]`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Dimensions_returns_distinct_required_dimensions_from_the_chart()
{
    // Arrange: onboard; create AR account RequiredDimensions=["Customer","Invoice"], AP account ["Vendor"].
    string[]? dims = await client.GetFromJsonAsync<string[]>($"/clients/{clientId}/dimensions");
    Assert.Equal(new[] { "Customer", "Invoice", "Vendor" }, dims!.OrderBy(x => x));
}

[Fact]
public async Task SourceTypes_returns_distinct_source_types_in_use()
{
    // Arrange: post two entries with SourceType "invoice" and one with "bill".
    string[]? types = await client.GetFromJsonAsync<string[]>($"/clients/{clientId}/source-types");
    Assert.Equal(new[] { "bill", "invoice" }, types!.OrderBy(x => x));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~DiscoveryEndpointsTests`
Expected: FAIL — routes return 404.

- [ ] **Step 3: Add the store method**

In `MongoJournalStore`, add:

```csharp
/// <summary>Distinct, non-empty SourceType values present in a client's journal — the discovery list a
/// module uses to offer source-type filters. Sorted for a stable response.</summary>
public async Task<IReadOnlyList<string>> DistinctSourceTypesAsync(
    Guid clientId, CancellationToken cancellationToken = default)
{
    FilterDefinition<JournalEntryDocument> filter = Builders<JournalEntryDocument>.Filter.And(
        Builders<JournalEntryDocument>.Filter.Eq(e => e.ClientId, clientId),
        Builders<JournalEntryDocument>.Filter.Ne(e => e.SourceType, null));
    List<string> values = await (await _entries.DistinctAsync(e => e.SourceType!, filter, cancellationToken: cancellationToken))
        .ToListAsync(cancellationToken);
    return values.Where(v => !string.IsNullOrWhiteSpace(v)).OrderBy(v => v, StringComparer.Ordinal).ToList();
}
```

- [ ] **Step 4: Register routes + handlers**

Add to the read-route block (after line 52's `/accounts`):
```csharp
clients.MapGet("/dimensions", GetDimensions);
clients.MapGet("/source-types", GetSourceTypes);
```

Add the handlers near the other GETs:
```csharp
private static async Task<IResult> GetDimensions(
    Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
{
    LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
    if (ctx.Failed) return ctx.Error;

    ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
    List<string> dims = chart.Accounts
        .SelectMany(a => a.RequiredDimensions)
        .Distinct()
        .OrderBy(d => d, StringComparer.Ordinal)
        .ToList();
    return Results.Ok(dims);
}

private static async Task<IResult> GetSourceTypes(
    Guid clientId, LedgerGateway gateway, ClaimsPrincipal user, CancellationToken cancellationToken)
{
    LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Read, cancellationToken);
    if (ctx.Failed) return ctx.Error;

    IReadOnlyList<string> types = await ctx.Ledger.Journal.DistinctSourceTypesAsync(clientId, cancellationToken);
    return Results.Ok(types);
}
```

Confirm `chart.Accounts` is enumerable of `Account` (it is — `ChartFieldViolationsAsync` uses `chart.Accounts.Count`). Confirm the `Permission.Read` overload and `ctx.Ledger.Accounts`/`ctx.Ledger.Journal` accessors match the neighbors.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~DiscoveryEndpointsTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Backend/Accounting101.Ledger.Mongo/MongoJournalStore.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/DiscoveryEndpointsTests.cs
git commit -m "feat(hardening): GET /dimensions + /source-types discovery endpoints"
```

---

### Task 7: Reversal own-source override

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/Commands.cs:29` (`ReverseRequest`)
- Modify: `Backend/Accounting101.Ledger.Mongo/LedgerService.cs:189-246` (`ReverseAsync`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs:280-284` (the `ReverseEntry` call site)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/ReversalSourceOverrideTests.cs` (create) — service-level test.

**Interfaces:**
- Produces:
  - `ReverseRequest(DateOnly ReversalDate, string? Reason, Guid? SourceRef = null, string? SourceType = null)`.
  - `ReverseAsync(Guid originalId, DateOnly reversalDate, Actor actor, string? reason = null, Guid? sourceRef = null, string? sourceType = null, CancellationToken cancellationToken = default)` — when `sourceRef`/`sourceType` are supplied, the reversal carries them instead of inheriting the original's.

- [ ] **Step 1: Write the failing test**

Reuse the `LedgerService` construction pattern from an existing `Backend/Accounting101.Ledger.Mongo.Tests` test that posts+approves+reverses (search for `ReverseAsync` usages in that project).

```csharp
[Fact]
public async Task Reverse_with_source_override_tags_the_reversal_with_its_own_document()
{
    // Arrange: post+approve an entry with SourceRef=orig, SourceType="invoice".
    Guid myDoc = Guid.NewGuid();
    JournalEntry reversal = await service.ReverseAsync(
        original.Id, reversalDate, actor, reason: "credit memo", sourceRef: myDoc, sourceType: "credit-memo");

    Assert.Equal(myDoc, reversal.SourceRef);
    Assert.Equal("credit-memo", reversal.SourceType);
}

[Fact]
public async Task Reverse_without_override_inherits_the_originals_source()
{
    JournalEntry reversal = await service.ReverseAsync(original.Id, reversalDate, actor);
    Assert.Equal(original.SourceRef, reversal.SourceRef);
    Assert.Equal(original.SourceType, reversal.SourceType);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~ReversalSourceOverrideTests`
Expected: FAIL — `ReverseAsync` has no `sourceRef`/`sourceType` parameters (compile error).

- [ ] **Step 3: Implement**

`Commands.cs:29`:
```csharp
public sealed record ReverseRequest(DateOnly ReversalDate, string? Reason, Guid? SourceRef = null, string? SourceType = null);
```

`ReverseAsync` — add the two optional parameters and use them in the `JournalEntry.Create` call (lines 227-228):
```csharp
public async Task<JournalEntry> ReverseAsync(
    Guid originalId, DateOnly reversalDate, Actor actor, string? reason = null,
    Guid? sourceRef = null, string? sourceType = null, CancellationToken cancellationToken = default)
{
    // ... unchanged body through line 216 ...

    JournalEntry reversal = JournalEntry.Create(
        id: Guid.NewGuid(),
        clientId: original.ClientId,
        sequenceNumber: 0,
        effectiveDate: reversalDate,
        postedAt: DateTimeOffset.UtcNow,
        type: EntryType.Reversing,
        audit: new AuditStamp { CreatedBy = actor.UserId, CreatedAt = DateTimeOffset.UtcNow },
        lines: reversedLines,
        reversalOf: originalId,
        sourceRef: sourceRef ?? original.SourceRef,     // caller override, else stay linked to the same document
        sourceType: sourceType ?? original.SourceType,  // (a credit-memo module tags the reversal with its own doc)
        reference: original.Reference,
        memo: reason ?? $"Reversal of entry {originalId}");

    // ... unchanged transaction body ...
}
```

Note: the existing call passes `cancellationToken` positionally at the end; because two optional params are inserted before it, update the endpoint call (next step) to use named args to be safe.

- [ ] **Step 4: Thread through the endpoint**

`LedgerEndpoints.cs:282-283`:
```csharp
JournalEntry reversal = await ctx.Ledger.Service.ReverseAsync(
    originalId, request.ReversalDate, ctx.Actor, request.Reason,
    request.SourceRef, request.SourceType, cancellationToken);
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~ReversalSourceOverrideTests`
Expected: PASS.

- [ ] **Step 6: Run affected projects**

Run: `dotnet test Backend/Accounting101.Ledger.Mongo.Tests/Accounting101.Ledger.Mongo.Tests.csproj Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: green (existing `ReverseAsync` callers still compile — new params are optional).

- [ ] **Step 7: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/Commands.cs Backend/Accounting101.Ledger.Mongo/LedgerService.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Mongo.Tests/ReversalSourceOverrideTests.cs
git commit -m "feat(hardening): optional own-source override on reversal"
```

---

### Task 8: Batch POST — service (`LedgerService.PostBatchAsync`)

**Files:**
- Modify: `Backend/Accounting101.Ledger.Mongo/LedgerService.cs` (add `PostBatchAsync` near `PostAsync`, line 65)
- Test: `Backend/Accounting101.Ledger.Mongo.Tests/PostBatchTests.cs` (create)

**Interfaces:**
- Consumes: `AppendSequencedAsync(entry, session, ct)` (line 45), `EnsureOpenForPostAsync(clientId, date, session, ct)` (line 460), `EnsureOpenAsync(clientId, date, ct)` (fast-fail, line 69's helper), `InTransactionAsync(body, ct)` (line ~416), `_audit.AppendAsync(...)`.
- Produces: `LedgerService.PostBatchAsync(IReadOnlyList<JournalEntry> entries, Actor actor, CancellationToken ct = default) -> Task<IReadOnlyList<JournalEntry>>` — writes every entry in ONE transaction, each with its own gapless sequence number; returns the sequenced entries in input order. Any failure (freeze, duplicate key, unbalanced) rolls the whole batch back and throws.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task PostBatch_writes_all_entries_with_consecutive_sequence_numbers()
{
    JournalEntry a = MakeBalancedEntry(clientId, date);   // reuse the project's entry-builder helper
    JournalEntry b = MakeBalancedEntry(clientId, date);

    IReadOnlyList<JournalEntry> written = await service.PostBatchAsync([a, b], actor);

    Assert.Equal(2, written.Count);
    Assert.Equal(written[0].SequenceNumber + 1, written[1].SequenceNumber);  // gapless, consecutive
    Assert.NotNull(await journal.GetAsync(a.Id));
    Assert.NotNull(await journal.GetAsync(b.Id));
}

[Fact]
public async Task PostBatch_rolls_back_entirely_when_one_entry_lands_in_a_closed_period()
{
    // Close through `date`. A batch [openDatedEntry, closedDatedEntry] must throw and write NEITHER.
    await service.CloseAsync(clientId, date, actor);
    JournalEntry ok = MakeBalancedEntry(clientId, date.AddDays(1));
    JournalEntry bad = MakeBalancedEntry(clientId, date);   // in the frozen period

    await Assert.ThrowsAsync<InvalidOperationException>(() => service.PostBatchAsync([ok, bad], actor));

    Assert.Null(await journal.GetAsync(ok.Id));   // rolled back with the bad one
    Assert.Null(await journal.GetAsync(bad.Id));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~PostBatchTests`
Expected: FAIL — `PostBatchAsync` does not exist.

- [ ] **Step 3: Implement**

Add after `PostAsync` (line 78):

```csharp
/// <summary>Record many entries as one business event — atomic all-or-nothing. Every entry is appended
/// in a single transaction, each taking its own gapless sequence number; if any entry fails its freeze
/// check or write, the whole batch rolls back. Returns the sequenced entries in input order.</summary>
public async Task<IReadOnlyList<JournalEntry>> PostBatchAsync(
    IReadOnlyList<JournalEntry> entries, Actor actor, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(actor);
    if (entries.Count == 0) throw new ArgumentException("A batch must contain at least one entry.", nameof(entries));

    // Fast-fail freeze check per entry, outside the transaction (mirrors PostAsync's pre-check).
    foreach (JournalEntry entry in entries)
        await EnsureOpenAsync(entry.ClientId, entry.EffectiveDate, cancellationToken);

    DateTimeOffset now = DateTimeOffset.UtcNow;
    JournalEntry[] recorded = new JournalEntry[entries.Count];
    await InTransactionAsync(async session =>
    {
        for (int i = 0; i < entries.Count; i++)
        {
            JournalEntry entry = entries[i];
            await EnsureOpenForPostAsync(entry.ClientId, entry.EffectiveDate, session, cancellationToken); // authoritative, via session
            JournalEntry posted = await AppendSequencedAsync(entry, session, cancellationToken);           // $inc journal:{clientId}
            await _audit.AppendAsync(posted.ClientId, posted.Id, posted.Version, AuditAction.Created, actor, null, now, session, cancellationToken);
            recorded[i] = posted;
        }
    }, cancellationToken);

    return recorded;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~PostBatchTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Mongo/LedgerService.cs Backend/Accounting101.Ledger.Mongo.Tests/PostBatchTests.cs
git commit -m "feat(hardening): atomic PostBatchAsync (all-or-nothing multi-entry)"
```

---

### Task 9: Batch POST — endpoint (`POST /entries/batch`)

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/BatchContracts.cs` (`PostBatchRequest`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` — register `/entries/batch` (near line 31) + `PostBatch` handler
- Test: `Backend/Accounting101.Ledger.Api.Tests/PostBatchEndpointTests.cs` (create)

**Interfaces:**
- Consumes: `TryMapEntry(clientId, request, actor, viaModule, out entry, out errors)` (line ~900), `ValidateForPostAsync(clientId, request, ctx, ct)` (line 147) — reuse for per-entry validation; `ctx.Ledger.Service.PostBatchAsync(...)` (Task 8); `ctx.Ledger.Service.GetEntryAsync(clientId, id, ct)` (used at line 74); `EntryComparison.SameFinancialContent(existing, mapped)` (line 79).
- Produces: `POST /clients/{id}/entries/batch` body `PostBatchRequest(IReadOnlyList<PostEntryRequest> Entries)`; returns `201 PostEntryResponse[]` (input order) on write, `200 PostEntryResponse[]` on full replay, `422` on validation / mixed-replay / too-large / empty.

- [ ] **Step 1: Write the contract**

`Backend/Accounting101.Ledger.Contracts/BatchContracts.cs`:
```csharp
namespace Accounting101.Ledger.Contracts;

/// <summary>Post many journal entries as one atomic business event (e.g. a payroll run). All-or-nothing:
/// every entry validates and writes, or none do. Max 500 entries.</summary>
public sealed record PostBatchRequest(IReadOnlyList<PostEntryRequest> Entries);
```

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact]
public async Task Batch_posts_all_entries_and_returns_them_in_order() { /* 2 valid entries -> 201, array of 2 */ }

[Fact]
public async Task Batch_with_one_unbalanced_entry_writes_none_and_returns_422_keyed_by_index()
{
    // entries[1] is unbalanced -> 422; assert an errors key starts with "entries[1]" and that a
    // subsequent trial-balance shows NEITHER entry was written.
}

[Fact]
public async Task Batch_replay_all_ids_present_returns_200() { /* post a batch, POST the same batch again -> 200 same ids */ }

[Fact]
public async Task Batch_mixed_replay_returns_422()
{
    // Re-POST a batch where one id was already used and one is new -> 422 (partial replay refusal).
}

[Fact]
public async Task Batch_over_500_returns_422() { /* 501 trivially-valid entries -> 422 too-large */ }

[Fact]
public async Task Batch_empty_returns_422() { /* { entries: [] } -> 422 */ }
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~PostBatchEndpointTests`
Expected: FAIL — route 404.

- [ ] **Step 4: Register the route**

After line 31 (`clients.MapPost("/entries", PostEntry);`):
```csharp
clients.MapPost("/entries/batch", PostBatch);
```

- [ ] **Step 5: Implement the handler**

Add near `PostEntry`. This mirrors `PostEntry`'s structure but over a list: size guard → per-entry idempotency classification → validate-all → atomic write.

```csharp
private const int MaxBatchEntries = 500;

private static async Task<IResult> PostBatch(
    Guid clientId, PostBatchRequest request, LedgerGateway gateway, IModuleAuthenticator moduleAuth,
    ClaimsPrincipal user, CancellationToken cancellationToken)
{
    LedgerContext ctx = await gateway.ResolveForPostAsync(user, clientId, moduleAuth, cancellationToken);
    if (ctx.Failed) return ctx.Error;

    IReadOnlyList<PostEntryRequest> reqs = request.Entries ?? [];
    if (reqs.Count == 0) return Unprocessable("A batch must contain at least one entry.");
    if (reqs.Count > MaxBatchEntries) return Unprocessable($"A batch may contain at most {MaxBatchEntries} entries; got {reqs.Count}.");

    // Idempotency classification: look up every supplied id up front. All-present+content-match => replay;
    // none-present => write; any mix, or a present id with different content => 422 (ambiguous partial replay).
    int suppliedIds = 0, matchedExisting = 0;
    List<PostEntryResponse> replay = [];
    Dictionary<string, string[]> errors = [];
    for (int i = 0; i < reqs.Count; i++)
    {
        if (reqs[i].Id is not { } id) continue;
        suppliedIds++;
        JournalEntry? existing = await ctx.Ledger!.Service.GetEntryAsync(clientId, id, cancellationToken);
        if (existing is null) continue;

        if (!TryMapEntry(clientId, reqs[i], ctx.Actor!, ctx.ViaModule, out JournalEntry? mapped, out _)
            || !EntryComparison.SameFinancialContent(existing, mapped!))
        {
            errors[$"entries[{i}].id"] = ["An entry with this id already exists with different content."];
            continue;
        }
        matchedExisting++;
        replay.Add(new PostEntryResponse(existing.Id, existing.Status.ToString(), existing.Posting.ToString()));
    }
    if (errors.Count > 0) return ValidationProblem(errors);

    // Whole-batch replay only when EVERY entry carries an id AND every one already exists with matching content.
    if (matchedExisting > 0)
    {
        if (matchedExisting == reqs.Count) return Results.Ok(replay);
        return Unprocessable("Partial replay: some entries in this batch already exist and some do not. "
                           + "Re-submit the batch with all-new ids, or replay the exact original batch.");
    }

    // Validate every entry (map + chart + typo + balance + freeze), collecting per-index errors.
    JournalEntry[] mappedEntries = new JournalEntry[reqs.Count];
    for (int i = 0; i < reqs.Count; i++)
    {
        (IResult? rejection, JournalEntry? entry) = await ValidateForPostAsync(clientId, reqs[i], ctx, cancellationToken);
        if (rejection is not null)
        {
            // Re-key the single-entry errors under an entries[i]. prefix so the caller can locate the bad entry.
            foreach (KeyValuePair<string, string[]> kv in await ExtractProblemErrorsAsync(rejection))
                errors[$"entries[{i}].{kv.Key}"] = kv.Value;
            continue;
        }
        mappedEntries[i] = entry!;
    }
    if (errors.Count > 0) return ValidationProblem(errors);

    try
    {
        IReadOnlyList<JournalEntry> written = await ctx.Ledger!.Service.PostBatchAsync(mappedEntries, ctx.Actor!, cancellationToken);
        List<PostEntryResponse> body = written
            .Select(e => new PostEntryResponse(e.Id, e.Status.ToString(), e.Posting.ToString()))
            .ToList();
        return Results.Created($"/clients/{clientId}/entries/batch", body);
    }
    catch (InvalidOperationException ex) // a freeze that raced past the pre-check
    {
        return Conflict(ex.Message);
    }
    catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
    {
        return Conflict("An entry id or sequence number in this batch already exists.");
    }
}
```

**Problem:** `ValidateForPostAsync` returns an `IResult` (a `ValidationProblem`/`Conflict`), not a raw error dictionary, so re-keying under `entries[i].` needs the errors back out of the result. Two clean options — pick the simpler one:

- **Preferred:** refactor `ValidateForPostAsync` to return the error *dictionary* (or a small record `(Dictionary<string,string[]>? Errors, IResult? NonValidationRejection, JournalEntry? Entry)`) instead of a prebuilt `IResult`, and have the single-post `PostEntry`/`ValidateEntry` wrap it in `ValidationProblem`/`Conflict` at their call sites. Then the batch handler reads the dictionary directly and re-keys it — delete the `ExtractProblemErrorsAsync` shim. This keeps one validation routine and avoids parsing an `IResult`.

Do the preferred refactor. Concretely, change `ValidateForPostAsync` to:
```csharp
private static async Task<(Dictionary<string, string[]>? Errors, IResult? Conflict, JournalEntry? Entry)>
    ValidateForPostAsync(Guid clientId, PostEntryRequest request, LedgerContext ctx, CancellationToken ct)
{
    if (!TryMapEntry(clientId, request, ctx.Actor!, ctx.ViaModule, out JournalEntry? entry, out Dictionary<string, string[]> parseErrors))
        return (parseErrors, null, null);

    Dictionary<string, string[]> chartErrors = await ChartFieldViolationsAsync(ctx.Ledger!.Accounts, clientId, entry!.Lines, ct);
    if (chartErrors.Count > 0) return (chartErrors, null, null);

    try { await ctx.Ledger!.Service.EnsureOpenForPostAsync(clientId, entry.EffectiveDate, ct); }
    catch (InvalidOperationException ex) { return (null, Conflict(ex.Message), null); }

    return (null, null, entry);
}
```
Update `PostEntry` (line 85) and `ValidateEntry` (line 133):
```csharp
(Dictionary<string, string[]>? errs, IResult? conflict, JournalEntry? entry) = await ValidateForPostAsync(clientId, request, ctx, cancellationToken);
if (errs is not null) return ValidationProblem(errs);
if (conflict is not null) return conflict;
```
Then the batch handler collects `errs` directly (re-keyed) and treats `conflict` as an immediate `entries[i]`-scoped freeze error (add to `errors[$"entries[{i}].effectiveDate"]` with the message). Remove the `ExtractProblemErrorsAsync` reference entirely.

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~PostBatchEndpointTests`
Expected: PASS.

- [ ] **Step 7: Run the API + Mongo projects (the ValidateForPostAsync refactor touches the single-post path)**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: green — `PostEntry`/`ValidateEntry` behavior is unchanged (same 422/409 outputs).

- [ ] **Step 8: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/BatchContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/PostBatchEndpointTests.cs
git commit -m "feat(hardening): POST /entries/batch atomic multi-entry endpoint"
```

---

### Task 10: readCap drift-guard tests

**Files:**
- Test (C#): `Backend/Accounting101.Ledger.Api.Tests/ReadCapDriftTests.cs` (create)
- Test (Angular): `UI/Angular/src/app/core/chart-health/chart-health.spec.ts` (create)

**Interfaces:**
- Consumes: `Accounting101.ModuleKit.ReadinessAccess.ReadCapabilityFor(string) -> string?`; `Accounting101.Ledger.Api.Control.Capabilities.CapabilityForModule(string, ModuleAccessLevel) -> string?` with `ModuleAccessLevel.Read`; the Angular `CHART_HEALTH_MODULES` constant.
- Produces: tests only.

Confirm `Accounting101.Ledger.Api.Tests` references both `Accounting101.Ledger.Api` and the ModuleKit assembly (`Accounting101.ModuleKit`). If ModuleKit is not already referenced, add a `ProjectReference` to it in the test `.csproj` (it is a domain-safe assembly).

- [ ] **Step 1: Write the C# drift test**

```csharp
using Accounting101.Ledger.Api.Control;
using Accounting101.ModuleKit;
using Xunit;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ReadCapDriftTests
{
    [Theory]
    [InlineData("receivables")]
    [InlineData("payables")]
    [InlineData("payroll")]
    [InlineData("cash")]
    [InlineData("fixedassets")]
    [InlineData("inventory")]
    public void ReadinessAccess_readCap_matches_engine_read_capability(string moduleKey)
    {
        Assert.Equal(
            Capabilities.CapabilityForModule(moduleKey, ModuleAccessLevel.Read),
            ReadinessAccess.ReadCapabilityFor(moduleKey));
    }

    [Fact]
    public void Both_maps_return_null_for_an_unknown_module_key()
    {
        Assert.Null(ReadinessAccess.ReadCapabilityFor("ghost"));
        Assert.Null(Capabilities.CapabilityForModule("ghost", ModuleAccessLevel.Read));
    }
}
```

- [ ] **Step 2: Run to verify it passes now (it guards, so it should be green)**

Run: `dotnet test Accounting101.slnx --filter FullyQualifiedName~ReadCapDriftTests`
Expected: PASS (the two backend maps agree today). This test fails only if a future edit drifts them — which is its job. To prove it actually guards, temporarily change one `ReadinessAccess` value, re-run, see it FAIL, then revert.

- [ ] **Step 3: Write the Angular spec**

`UI/Angular/src/app/core/chart-health/chart-health.spec.ts`:
```typescript
import { CHART_HEALTH_MODULES } from './chart-health';

describe('CHART_HEALTH_MODULES readCap', () => {
  const expected: Record<string, string> = {
    receivables: 'ar.read',
    payables: 'ap.read',
    payroll: 'payroll.read',
    cash: 'cash.read',
    fixedassets: 'fixedassets.read',
    inventory: 'inventory.read',
  };

  it('maps every module to its {area}.read capability', () => {
    expect(CHART_HEALTH_MODULES.length).toBe(Object.keys(expected).length);
    for (const m of CHART_HEALTH_MODULES) {
      expect(m.readCap).toBe(expected[m.key]);
    }
  });
});
```

- [ ] **Step 4: Run the Angular test**

Run (from `UI/Angular`): `npm test -- --watch=false --include='**/chart-health.spec.ts'`
(If the project uses a different test invocation, match `package.json`'s `test` script; run the whole suite if targeted include is unsupported.)
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api.Tests/ReadCapDriftTests.cs UI/Angular/src/app/core/chart-health/chart-health.spec.ts
git commit -m "test(hardening): drift guards cross-checking the 3-home readCap map"
```

---

### Task 11: Per-category revenue readiness (Receivables)

**Files:**
- Modify: `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesChartRequirements.cs`
- Test: `Modules/Receivables/Accounting101.Receivables.Tests/ChartReadinessE2eTests.cs` (extend — read it first to reuse its host fixture; the fixture already configures `Receivables:Accounts:RevenueByCategory:License`, see `ReceivablesHostFixture.cs:57`)

**Interfaces:**
- Consumes: `InvoicePostingAccounts.RevenueAccountsByCategory` (`IReadOnlyDictionary<string, Guid>`); `AccountRequirement(Guid, string, string?, IReadOnlyList<string>)`.
- Produces: `ReceivablesChartRequirements.ForAsync` additionally emits one `AccountRequirement` per configured revenue category.

- [ ] **Step 1: Write the failing test**

Read `ChartReadinessE2eTests.cs` and `ReceivablesHostFixture.cs` first. The fixture configures a `License` revenue category to `LicenseRevenueAccountId`. Add a test asserting the readiness report includes a requirement labeled for that category, and that pointing the category at a non-existent account surfaces a gap.

```csharp
[Fact]
public async Task Readiness_includes_a_requirement_for_each_configured_revenue_category()
{
    // The fixture maps category "License" -> LicenseRevenueAccountId. With that account present as Revenue,
    // the readiness report must contain an Ok result labeled "Revenue: License".
    ChartReadinessReport report = await GetReadiness(clientId);   // reuse the test's existing readiness call
    Assert.Contains(report.Accounts, a => a.Label == "Revenue: License");
}
```
If the existing test class already has a "chart is ready" happy-path, extend that assertion set rather than duplicating fixture setup.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter FullyQualifiedName~ChartReadinessE2eTests`
Expected: FAIL — no "Revenue: License" requirement is declared.

- [ ] **Step 3: Implement**

In `ReceivablesChartRequirements.ForAsync`, after building the fixed list, append one requirement per configured category. Restructure to a mutable list:

```csharp
public async Task<IReadOnlyList<AccountRequirement>> ForAsync(Guid clientId, CancellationToken ct = default)
{
    InvoicePostingAccounts inv = await invoiceAccounts.GetAsync(clientId, ct);
    PaymentPostingAccounts pay = await paymentAccounts.GetAsync(clientId, ct);

    List<AccountRequirement> requirements =
    [
        new(inv.ReceivableAccountId,       "Accounts Receivable", "Asset",     ["Customer", "Invoice"]),
        new(pay.CustomerCreditsAccountId,  "Customer Credits",    "Liability", ["Customer"]),
        new(inv.DefaultRevenueAccountId,   "Revenue",             "Revenue",   []),
        new(inv.SalesTaxPayableAccountId,  "Sales Tax Payable",   "Liability", []),
        new(pay.CashAccountId,             "Cash",                "Asset",     []),
        new(pay.BadDebtExpenseAccountId,   "Bad Debt Expense",    "Expense",   []),
        new(pay.SalesReturnsAccountId,     "Sales Returns",       "Revenue",   []),
    ];

    // Per-category revenue accounts an invoice line may post to (configured RevenueByCategory map). Each must
    // be a real, correctly-typed Revenue account, or a line tagged with that category would fail to post.
    foreach ((string category, Guid accountId) in inv.RevenueAccountsByCategory)
        requirements.Add(new(accountId, $"Revenue: {category}", "Revenue", []));

    return requirements;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Modules/Receivables/Accounting101.Receivables.Tests/Accounting101.Receivables.Tests.csproj --filter FullyQualifiedName~ChartReadinessE2eTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Modules/Receivables/Accounting101.Receivables.Api/ReceivablesChartRequirements.cs Modules/Receivables/Accounting101.Receivables.Tests/ChartReadinessE2eTests.cs
git commit -m "feat(hardening): readiness covers configured revenue-by-category accounts"
```

---

## Final verification

- [ ] **Whole solution green:** `dotnet test Accounting101.slnx` — all projects pass.
- [ ] **Angular suite green:** from `UI/Angular`, run the test script (`npm test -- --watch=false`).
- [ ] Confirm no stray files (screenshots/, scratch) were left in the repo root.

## Self-review notes (coverage map)

| Spec item | Task |
|-----------|------|
| 1 Covering index | Task 1 |
| 2 asOf account-balance | Task 2 |
| 3 Discovery endpoints | Task 6 |
| 3 Typo guard | Task 5 |
| 4 Actionable parse errors | Task 4 |
| 5 Batch POST (service) | Task 8 |
| 5 Batch POST (endpoint) | Task 9 |
| 6 Labeling | Task 3 |
| 7 Reversal own-source override | Task 7 |
| 8 readCap drift-guard | Task 10 |
| 9 Per-category revenue readiness | Task 11 |
