# Per-Client Fiscal Year-End + Close Guards — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the fiscal year-end a per-client config (`ClientRegistration.FiscalYearEndMonth`, default 12) and add two host-layer guards: a monthly close on the fiscal year-end date is refused (`409`, "use close-year"), and `close-year` on a non-fiscal-year-end date is refused (`409`).

**Architecture:** FY-end is a per-client **policy**, like `RequireSegregationOfDuties` — stored in `ClientRegistration` (control DB), enforced in the API endpoint handlers via `ControlStore.GetClientAsync` (the exact path `ApproveEntry` uses for SoD). The engine (`LedgerService`) is **unchanged** — it stays policy-light.

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs, MongoDB control store, xUnit + EphemeralMongo + `WebApplicationFactory<Program>`.

**Spec:** `docs/superpowers/specs/2026-06-26-fiscal-year-end-config-and-close-guards-design.md`

## Global Constraints
- .NET 10; build **0 warnings**; commit per task; TDD.
- Engine (`LedgerService`) untouched — the guard is host-layer only, mirroring the SoD enforcement path.
- Backward-compatible: `FiscalYearEndMonth` defaults to 12; legacy registrations (field absent → BSON deserializes to 0) normalize to 12 at read. The only intended behavior change: a Dec-FY client's Dec-31 monthly close now routes to `close-year`.
- `409 Conflict` for both guards (matches the close family's error shape); 400 for an out-of-range month at create.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## Task 1: Config plumbing + the fiscal-year helper

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs`
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` (`CreateClientRequest`, `ClientRegistrationResponse`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` (`CreateClient` validation + copy; `ListClients` response)
- Create: `Backend/Accounting101.Ledger.Api/Control/FiscalYear.cs` (helper)
- Test: `Backend/Accounting101.Ledger.Api.Tests/AdminTests.cs` (config behavior) + a `FiscalYear` unit test (new small test class)

**Interfaces:**
- Produces `ClientRegistration.FiscalYearEndMonth` (int, default 12).
- Produces `CreateClientRequest.FiscalYearEndMonth` (int, default 12) and `ClientRegistrationResponse.FiscalYearEndMonth` (int).
- Produces `FiscalYear.MonthOf(ClientRegistration) → int` (normalizes 0/out-of-range → 12) and `FiscalYear.EndDateFor(int month, int year) → DateOnly` (last day of that month).

- [ ] **Step 1: Write failing tests.**

`FiscalYear` unit test (new `Backend/Accounting101.Ledger.Api.Tests/FiscalYearTests.cs`):
```csharp
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class FiscalYearTests
{
    [Theory]
    [InlineData(12, 2024, "2024-12-31")]
    [InlineData(6, 2024, "2024-06-30")]
    [InlineData(2, 2024, "2024-02-29")]   // leap year
    [InlineData(2, 2025, "2025-02-28")]
    public void EndDateFor_is_the_last_day_of_the_month(int month, int year, string expected)
        => Assert.Equal(DateOnly.Parse(expected), FiscalYear.EndDateFor(month, year));

    [Theory]
    [InlineData(6, 6)]
    [InlineData(0, 12)]    // legacy registration (field absent -> 0) defaults to December
    [InlineData(13, 12)]   // out of range -> December
    public void MonthOf_normalizes_to_a_valid_month_defaulting_to_December(int stored, int expected)
        => Assert.Equal(expected, FiscalYear.MonthOf(new ClientRegistration { FiscalYearEndMonth = stored }));
}
```

`AdminTests.cs` additions (mirror the file's existing `CreateClientRequest` calls — read it first for the auth/admin-token harness):
```csharp
[Fact]
public async Task Create_client_stores_and_returns_the_fiscal_year_end_month()
{
    HttpResponseMessage created = await Admin().PostAsJsonAsync("/admin/clients",
        new CreateClientRequest { Name = "JuneCo", FiscalYearEndMonth = 6 });
    created.EnsureSuccessStatusCode();
    ClientRegistrationResponse body = (await created.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
    Assert.Equal(6, body.FiscalYearEndMonth);
}

[Fact]
public async Task Create_client_defaults_fiscal_year_end_to_december()
{
    HttpResponseMessage created = await Admin().PostAsJsonAsync("/admin/clients",
        new CreateClientRequest { Name = "DefaultCo" });   // FiscalYearEndMonth omitted
    ClientRegistrationResponse body = (await created.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
    Assert.Equal(12, body.FiscalYearEndMonth);
}

[Theory]
[InlineData(0)]
[InlineData(13)]
public async Task Create_client_rejects_an_out_of_range_fiscal_year_end_month(int month)
{
    HttpResponseMessage created = await Admin().PostAsJsonAsync("/admin/clients",
        new CreateClientRequest { Name = "BadCo", FiscalYearEndMonth = month });
    Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
}
```
(`Admin()` = the test's authenticated admin client — use whatever helper `AdminTests` already uses to POST `/admin/clients`.)

- [ ] **Step 2: Run, confirm fail** (members/fields absent → compile/assert failures).

- [ ] **Step 3: Implement.**

`ClientRegistration.cs` — add the property:
```csharp
    /// <summary>The month (1-12) the client's fiscal year ends; the fiscal year ends on the LAST day of
    /// that month. December (12) by default — a per-client policy, like SoD. Legacy registrations stored
    /// before this field existed deserialize to 0; readers normalize via <see cref="FiscalYear.MonthOf"/>.</summary>
    public int FiscalYearEndMonth { get; set; } = 12;
```

`FiscalYear.cs` (new):
```csharp
namespace Accounting101.Ledger.Api.Control;

/// <summary>Fiscal-year-end helpers. A fiscal year ends on the last day of a configured month.</summary>
public static class FiscalYear
{
    /// <summary>The client's fiscal-year-end month (1-12), defaulting to December (12) for legacy
    /// registrations whose field deserialized to 0 (or any out-of-range value).</summary>
    public static int MonthOf(ClientRegistration client) =>
        client.FiscalYearEndMonth is >= 1 and <= 12 ? client.FiscalYearEndMonth : 12;

    /// <summary>The fiscal-year-end date for a given year: the last calendar day of <paramref name="month"/>.</summary>
    public static DateOnly EndDateFor(int month, int year) =>
        new(year, month, DateTime.DaysInMonth(year, month));
}
```

`AdminContracts.cs` — add the field to request + response:
```csharp
public sealed record CreateClientRequest
{
    public required string Name { get; init; }
    public string? DatabaseName { get; init; }
    public bool RequireSegregationOfDuties { get; init; }
    /// <summary>Month (1-12) the fiscal year ends; defaults to December.</summary>
    public int FiscalYearEndMonth { get; init; } = 12;
}

public sealed record ClientRegistrationResponse(
    Guid Id, string Name, string DatabaseName, bool RequireSegregationOfDuties, int FiscalYearEndMonth);
```

`AdminEndpoints.cs` — `CreateClient`: validate range, copy to registration, include in response; and update `ListClients`'s response projection:
```csharp
    private static async Task<IResult> CreateClient(
        CreateClientRequest request, ControlStore control, CancellationToken cancellationToken)
    {
        if (request.FiscalYearEndMonth is < 1 or > 12)
            return Results.Problem("FiscalYearEndMonth must be between 1 and 12.", statusCode: StatusCodes.Status400BadRequest);

        Guid id = Guid.NewGuid();
        string database = string.IsNullOrWhiteSpace(request.DatabaseName)
            ? "client_" + id.ToString("N")
            : request.DatabaseName;

        ClientRegistration registration = new()
        {
            Id = id,
            Name = request.Name,
            DatabaseName = database,
            RequireSegregationOfDuties = request.RequireSegregationOfDuties,
            FiscalYearEndMonth = request.FiscalYearEndMonth,
        };
        await control.RegisterClientAsync(registration, cancellationToken);

        return Results.Created($"/admin/clients/{id}",
            new ClientRegistrationResponse(id, registration.Name, registration.DatabaseName,
                registration.RequireSegregationOfDuties, registration.FiscalYearEndMonth));
    }
```
And in `ListClients` (and any other `ClientRegistrationResponse` construction — e.g. `ListClients`):
```csharp
            .Select(c => new ClientRegistrationResponse(
                c.Id, c.Name, c.DatabaseName, c.RequireSegregationOfDuties, FiscalYear.MonthOf(c)))
```
(Use `FiscalYear.MonthOf(c)` on reads so legacy 0 → 12 in responses too.)

- [ ] **Step 4: Run, confirm pass** — `FiscalYearTests` + the new `AdminTests` green; full solution builds 0 warnings. (Any other `ClientRegistrationResponse(...)` construction sites must be updated for the new arity — grep `new ClientRegistrationResponse(` and fix all.)

- [ ] **Step 5: Commit** — `feat(ledger): per-client FiscalYearEndMonth config + FiscalYear helper`.

---

## Task 2: The two close guards

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/LedgerEndpoints.cs` (`ClosePeriod` ~line 280, `CloseYear` ~line 669 — add `ControlStore control` param + the guard)
- Test: `Backend/Accounting101.Ledger.Api.Tests/FiscalYearCloseGuardTests.cs` (new)

**Interfaces:**
- Consumes Task 1's `FiscalYear.MonthOf` / `FiscalYear.EndDateFor` and `ClientRegistration.FiscalYearEndMonth`.
- `ControlStore` is already a registered DI service injected into endpoint handlers (see `ApproveEntry`/`CreateClient`).

- [ ] **Step 1: Write failing tests** (`FiscalYearCloseGuardTests.cs`, real host). The test fixture must register clients with a chosen `FiscalYearEndMonth` (via `POST /admin/clients` with the admin token, or the fixture's control-store seed — mirror however the existing close/policy tests seed a client + a Controller member, then set/override `FiscalYearEndMonth`). Seed a Controller (has `Close`), post + approve at least one entry so a close has content, then:
```csharp
// Default December client
[Fact]
public async Task Monthly_close_on_a_non_fiscal_year_end_succeeds()      // close {asOf: 2024-11-30} -> 200
[Fact]
public async Task Monthly_close_on_the_fiscal_year_end_is_refused()      // close {asOf: 2024-12-31} -> 409, detail names close-year
[Fact]
public async Task Close_year_on_the_fiscal_year_end_succeeds()           // close-year {fiscalYearEnd: 2024-12-31} -> 200
[Fact]
public async Task Close_year_on_a_non_fiscal_year_end_is_refused()       // close-year {fiscalYearEnd: 2024-06-30} -> 409

// A June-fiscal-year client (FiscalYearEndMonth = 6)
[Fact]
public async Task June_client_monthly_close_on_june_30_is_refused()      // close {asOf: 2024-06-30} -> 409
[Fact]
public async Task June_client_monthly_close_on_dec_31_succeeds()         // close {asOf: 2024-12-31} -> 200 (ordinary month for them)
[Fact]
public async Task June_client_close_year_on_june_30_succeeds()           // close-year {fiscalYearEnd: 2024-06-30} -> 200
[Fact]
public async Task June_client_close_year_on_dec_31_is_refused()          // close-year {fiscalYearEnd: 2024-12-31} -> 409
```
Assert the 409 bodies are the guidance messages (monthly-close → mentions `close-year`; close-year → names the real fiscal year-end). For the "succeeds" cases, ensure no pending entries block the close (approve everything first, or close an empty/approved period).

- [ ] **Step 2: Run, confirm fail** — guards absent: the FY-end monthly close currently returns 200 (not 409); the wrong-date close-year currently returns 200.

- [ ] **Step 3: Implement.**

`ClosePeriod` — add `ControlStore control` to the signature (after `gateway`, like `ApproveEntry`) and guard before `CloseAsync`:
```csharp
    private static async Task<IResult> ClosePeriod(
        Guid clientId, ClosePeriodRequest request, LedgerGateway gateway, ControlStore control,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Close, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // Fiscal-year-end guard (host policy, per client): the year-end is closed via close-year, which
        // also posts the closing entry — a plain monthly close there would strand it.
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is not null)
        {
            DateOnly fye = FiscalYear.EndDateFor(FiscalYear.MonthOf(client), request.AsOf.Year);
            if (request.AsOf == fye)
                return Results.Problem(
                    detail: $"{request.AsOf:yyyy-MM-dd} is this client's fiscal year-end. Run the year-end close "
                          + $"(POST /clients/{clientId}/periods/close-year) instead of a monthly close.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["useEndpoint"] = "periods/close-year",
                        ["fiscalYearEnd"] = fye.ToString("yyyy-MM-dd"),
                    });
        }

        try
        {
            IReadOnlyDictionary<Guid, decimal> balances = await ctx.Ledger.Service.CloseAsync(clientId, request.AsOf, ctx.Actor, cancellationToken);
            return Results.Ok(new CloseResponse(request.AsOf, ToAccountBalances(balances)));
        }
        catch (PeriodCloseBlockedException ex) { /* unchanged */ }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }
```
(Keep the existing `catch` blocks exactly as they are.)

`CloseYear` — add `ControlStore control` to the signature and guard before `CloseYearAsync`:
```csharp
    private static async Task<IResult> CloseYear(
        Guid clientId, CloseYearRequest request, LedgerGateway gateway, ControlStore control,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        LedgerContext ctx = await gateway.ResolveAsync(user, clientId, Permission.Close, cancellationToken);
        if (ctx.Failed) return ctx.Error;

        // Symmetric fiscal-year-end guard: close-year closes the fiscal year — refuse any other date.
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is not null)
        {
            DateOnly fye = FiscalYear.EndDateFor(FiscalYear.MonthOf(client), request.FiscalYearEnd.Year);
            if (request.FiscalYearEnd != fye)
                return Results.Problem(
                    detail: $"{request.FiscalYearEnd:yyyy-MM-dd} is not this client's fiscal year-end ({fye:yyyy-MM-dd}). "
                          + "close-year closes the fiscal year; use the monthly close for an ordinary period.",
                    statusCode: StatusCodes.Status409Conflict);
        }

        ChartOfAccounts chart = await ctx.Ledger.Accounts.GetChartAsync(clientId, cancellationToken);
        try { /* existing CloseYearAsync call + existing catch blocks unchanged */ }
    }
```
(Leave the `ChartOfAccounts` fetch, the `CloseYearAsync` call, and every existing `catch` block exactly as they are — only the signature param + the guard block are added.)

Confirm `ControlStore` and `ClientRegistration` are in scope (add `using Accounting101.Ledger.Api.Control;` if not already imported in `LedgerEndpoints.cs` — it already references `ControlStore` types elsewhere via the SoD check, so likely present).

- [ ] **Step 4: Run, confirm pass** — `FiscalYearCloseGuardTests` green; re-run the existing close/close-year/policy test classes (no regression — a default Dec client's ordinary monthly closes still work; only the Dec-31 monthly close now 409s). Full solution 0 warnings.

- [ ] **Step 5: Commit** — `feat(ledger): close guards — monthly close refuses FY-end, close-year refuses non-FY-end`.

---

## Final verification
- [ ] `dotnet build Accounting101.slnx -c Debug` → 0 warnings.
- [ ] Run: `FiscalYearTests`, `AdminTests`, `FiscalYearCloseGuardTests`, plus the existing close/close-year/policy test classes — all green.
- [ ] Confirm: a client's `FiscalYearEndMonth` is set at creation (default 12) + surfaced; a monthly close on the FY-end date 409s with close-year guidance; `close-year` on a non-FY-end date 409s; a June-FY client behaves symmetrically; the engine (`LedgerService`) is unchanged.
- [ ] Whole-branch review on the most capable model (it's an auth/close-policy surface), then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- **Spec coverage:** config on `ClientRegistration` + `CreateClientRequest`/`Response` + admin validation (T1), `FiscalYear` helper (T1), monthly-close guard (T2), symmetric close-year guard (T2), 409 + guidance + hints (T2), backward-compat default 12 + legacy 0→12 normalization (T1 `MonthOf`, used in both guards + responses).
- **Type consistency:** `FiscalYearEndMonth` (int) flows `CreateClientRequest` → `ClientRegistration` → `ClientRegistrationResponse`; `FiscalYear.MonthOf`/`EndDateFor` signatures stable T1→T2; the `ClientRegistrationResponse` arity change forces updates at every construction site (T1 Step 4 grep).
- **Open implementer checks:** (a) find ALL `new ClientRegistrationResponse(...)` sites for the new arity; (b) the close-guard tests need a client seeded with a chosen `FiscalYearEndMonth` + a Controller member + approved content to close — mirror the existing close-test fixture; (c) engine `LedgerService` must NOT be touched — the guard is host-layer only.
