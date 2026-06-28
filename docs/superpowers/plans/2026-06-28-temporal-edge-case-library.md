# Temporal / Period-&-Fiscal-Year Edge-Case Library — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an HTTP-level edge-case test library for the engine's temporal/period/fiscal-year boundaries, pinning exact outcomes (status + message substring on rejections; exact balances + statement effects on accepted sequences).

**Architecture:** Organized xUnit E2E tests in `Backend/Accounting101.Ledger.Api.Tests/Temporal/`, driven through the real host via the existing `ApiFixture` (WebApplicationFactory + shared EphemeralMongo). One shared helper + five focused files. No product code changes unless a scenario surfaces a real bug.

**Tech Stack:** .NET 10, xUnit, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.AspNetCore.Mvc.ProblemDetails`.

## Global Constraints

- All new files live under `Backend/Accounting101.Ledger.Api.Tests/Temporal/`. Namespace stays `Accounting101.Ledger.Api.Tests`.
- Each test class uses `IClassFixture<ApiFixture>`.
- Rejections assert the exact `HttpStatusCode` AND that ProblemDetails `detail` contains the documented substring, case-insensitively. Accepted/integrity assert exact `decimal` balances, `IsBalanced`, and statement totals.
- Do NOT duplicate already-E2E-covered scenarios: the Dec/June FY-end guards (`FiscalYearCloseGuardTests`), close-with-pending blockers (`PeriodCloseApiTests`), post-into-closed (`CommandQueryTests`), reverse-into-closed (`ReverseTests`), closing-entry-zeroes-temporaries (`AccountTests`).
- No product behavior changes. If a scenario reveals wrong behavior (a different status/message/balance than asserted), STOP and surface it as a finding — never bend the test or change product code to make it pass; never silently skip.
- Commit trailer, verbatim, on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- Test run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~<ClassName>"`

## Confirmed contracts (used across tasks)

Routes + request/response (all under `/clients/{clientId}`):
- Onboarding: `POST /onboarding`, body `OnboardingRequest(DateOnly AsOf, IReadOnlyList<OpeningBalanceLine> Balances)`, `OpeningBalanceLine(Guid AccountId, decimal SignedAmount)` (debit-positive) → **201** `EntryResponse` (Type "Opening", Posting "Posted"); unbalanced → 422. Seeds the inception freeze at `AsOf − 1` (a post dated `≤ AsOf−1` → 409 closed; `≥ AsOf` → allowed). Requires ManageAccounts (Controller/Admin).
- Monthly close: `POST /periods/close`, body `ClosePeriodRequest(DateOnly AsOf)` → **200** `CloseResponse(DateOnly AsOf, IReadOnlyList<AccountBalanceResponse> OpeningBalances)`; FY-end date → 409 (detail names close-year); pending in-period → 409 (blockers); `AsOf ≤ current closedThrough` → **409** detail `"Period is already closed through {date}."`.
- Year-end close: `POST /periods/close-year`, body `CloseYearRequest(DateOnly FiscalYearEnd)` → **200** `CloseYearResponse(EntryResponse? ClosingEntry)`; wrong date → 409 detail `"{date} is not this client's fiscal year-end ({fye})."`; temporaries present but no retained-earnings account → **409** detail `"Year-end close requires a designated retained-earnings account."`
- Reopen: `POST /periods/reopen` (step-up policy — needs a fresh `auth_time` claim AND the Reopen permission/Admin role), body `ReopenRequest(DateOnly? ReopenThrough, string? Reason)` → **204**; not-earlier-than-current → **409** detail `"Reopen must move the freeze earlier than the current close ({date})."`; nothing to reopen → 409.
- Trial balance: `GET /trial-balance?asOf=yyyy-MM-dd` → `TrialBalanceResponse(DateOnly? AsOf, IReadOnlyList<AccountBalanceResponse> Accounts)`, `AccountBalanceResponse(Guid AccountId, decimal Balance)` (signed, debit-positive).
- Balance sheet: `GET /statements/balance-sheet?asOf=` → `BalanceSheetResponse(..., bool IsBalanced)`.

Seeding + chart:
- A client with a chosen FY-end month is created via the admin path: `AdminClient()` → `POST /admin/clients` body `CreateClientRequest { Name, FiscalYearEndMonth }` → `ClientRegistrationResponse` (`.Id`); then `POST /admin/clients/{id}/members` body `AddMemberRequest(Guid userId, "Controller")`; then `fixture.ClientFor(userId, name, ("role", "Controller"))`.
- Default `fixture.SeedClientAsync(role: LedgerRole.Admin)` → `SeededClient(Guid ClientId, string Database, Guid UserId, HttpClient Http)`, December FY, Admin member (can post/approve/close AND, with a fresh-auth client, reopen).
- Accounts: `PUT /clients/{c}/accounts/{id}` body `AccountRequest { Number, Name, Type, IsRetainedEarnings }`.
- Post+approve: `POST /entries` body `PostEntryRequest(Guid? Id, DateOnly EffectiveDate, string? Reference, string? Memo, IReadOnlyList<PostLineRequest> Lines)`, `PostLineRequest(Guid AccountId, string Direction, decimal Amount)`; then `POST /entries/{id}/approve`.

`using` namespaces for the tests: `System.Net`, `System.Net.Http.Json`, `Accounting101.Ledger.Contracts`, `Accounting101.Ledger.Api.Control`, `Microsoft.AspNetCore.Mvc`.

---

### Task 1: Shared helper + inception-floor E2E

**Files:**
- Create: `Backend/Accounting101.Ledger.Api.Tests/Temporal/TemporalScenario.cs`
- Create: `Backend/Accounting101.Ledger.Api.Tests/Temporal/InceptionFloorE2eTests.cs`

**Interfaces:**
- Produces (used by Tasks 2-5): static helper `TemporalScenario` with
  `FreshAuth() -> string`,
  `SeedFyeClientAsync(ApiFixture fixture, int fiscalYearEndMonth, string name) -> Task<(Guid ClientId, HttpClient Http)>`,
  `CreateAccountAsync(HttpClient http, Guid clientId, string number, string name, string type, bool retained = false) -> Task<Guid>`,
  `OnboardAsync(HttpClient http, Guid clientId, DateOnly asOf, params (Guid AccountId, decimal Signed)[] balances) -> Task<HttpResponseMessage>`,
  `PostAsync(HttpClient http, Guid clientId, DateOnly date, Guid debit, Guid credit, decimal amount) -> Task<HttpResponseMessage>`,
  `PostAndApproveAsync(HttpClient http, Guid clientId, DateOnly date, Guid debit, Guid credit, decimal amount) -> Task`,
  `CloseAsync(HttpClient http, Guid clientId, DateOnly asOf) -> Task<HttpResponseMessage>`,
  `CloseYearAsync(HttpClient http, Guid clientId, DateOnly fye) -> Task<HttpResponseMessage>`,
  `ReopenAsync(ApiFixture fixture, Guid clientId, Guid adminUserId, DateOnly? through, string? reason) -> Task<HttpResponseMessage>`,
  `AssertProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string substring) -> Task`,
  `AssertBalancedAsync(HttpClient http, Guid clientId, DateOnly asOf) -> Task`,
  `AccountBalanceAsync(HttpClient http, Guid clientId, Guid accountId, DateOnly asOf) -> Task<decimal>`.

- [ ] **Step 1: Write the shared helper**

Create `Temporal/TemporalScenario.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>Shared setup + assertion helpers for the temporal / period / fiscal-year edge-case E2E
/// scenarios, driven through the real host.</summary>
internal static class TemporalScenario
{
    internal static string FreshAuth() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    /// <summary>Create a client with the given fiscal-year-end month via the admin path, add a Controller
    /// member, and return an HttpClient authenticated as that member.</summary>
    internal static async Task<(Guid ClientId, HttpClient Http)> SeedFyeClientAsync(
        ApiFixture fixture, int fiscalYearEndMonth, string name)
    {
        HttpClient admin = fixture.AdminClient();
        ClientRegistrationResponse reg = (await (await admin.PostAsJsonAsync(
                "/admin/clients", new CreateClientRequest { Name = name, FiscalYearEndMonth = fiscalYearEndMonth }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;

        Guid userId = Guid.NewGuid();
        (await admin.PostAsJsonAsync($"/admin/clients/{reg.Id}/members", new AddMemberRequest(userId, "Controller")))
            .EnsureSuccessStatusCode();

        return (reg.Id, fixture.ClientFor(userId, $"{name} Controller", ("role", "Controller")));
    }

    internal static async Task<Guid> CreateAccountAsync(
        HttpClient http, Guid clientId, string number, string name, string type, bool retained = false)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type, IsRetainedEarnings = retained }))
            .EnsureSuccessStatusCode();
        return id;
    }

    internal static Task<HttpResponseMessage> OnboardAsync(
        HttpClient http, Guid clientId, DateOnly asOf, params (Guid AccountId, decimal Signed)[] balances) =>
        http.PostAsJsonAsync($"/clients/{clientId}/onboarding",
            new OnboardingRequest(asOf, balances.Select(b => new OpeningBalanceLine(b.AccountId, b.Signed)).ToList()));

    internal static Task<HttpResponseMessage> PostAsync(
        HttpClient http, Guid clientId, DateOnly date, Guid debit, Guid credit, decimal amount) =>
        http.PostAsJsonAsync($"/clients/{clientId}/entries",
            new PostEntryRequest(null, date, null, null,
                [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]));

    internal static async Task PostAndApproveAsync(
        HttpClient http, Guid clientId, DateOnly date, Guid debit, Guid credit, decimal amount)
    {
        PostEntryResponse created = (await (await PostAsync(http, clientId, date, debit, credit, amount))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{clientId}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    internal static Task<HttpResponseMessage> CloseAsync(HttpClient http, Guid clientId, DateOnly asOf) =>
        http.PostAsJsonAsync($"/clients/{clientId}/periods/close", new ClosePeriodRequest(asOf));

    internal static Task<HttpResponseMessage> CloseYearAsync(HttpClient http, Guid clientId, DateOnly fye) =>
        http.PostAsJsonAsync($"/clients/{clientId}/periods/close-year", new CloseYearRequest(fye));

    /// <summary>Reopen via a freshly-stepped-up admin client (the period reopen endpoint requires both the
    /// Reopen permission and a recent auth_time claim).</summary>
    internal static Task<HttpResponseMessage> ReopenAsync(
        ApiFixture fixture, Guid clientId, Guid adminUserId, DateOnly? through, string? reason)
    {
        HttpClient adminFresh = fixture.ClientFor(adminUserId, "Admin", ("auth_time", FreshAuth()));
        return adminFresh.PostAsJsonAsync($"/clients/{clientId}/periods/reopen", new ReopenRequest(through, reason));
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string substring)
    {
        Assert.Equal(status, resp.StatusCode);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(substring, problem!.Detail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task AssertBalancedAsync(HttpClient http, Guid clientId, DateOnly asOf)
    {
        BalanceSheetResponse sheet = (await http.GetFromJsonAsync<BalanceSheetResponse>(
            $"/clients/{clientId}/statements/balance-sheet?asOf={asOf:yyyy-MM-dd}"))!;
        Assert.True(sheet.IsBalanced,
            $"Balance sheet not balanced as of {asOf}: assets {sheet.TotalAssets} vs L+E {sheet.TotalLiabilitiesAndEquity}");
    }

    /// <summary>The signed (debit-positive) balance of one account on the trial balance as of a date; 0 if the
    /// account does not appear (e.g. a zeroed temporary).</summary>
    internal static async Task<decimal> AccountBalanceAsync(HttpClient http, Guid clientId, Guid accountId, DateOnly asOf)
    {
        TrialBalanceResponse tb = (await http.GetFromJsonAsync<TrialBalanceResponse>(
            $"/clients/{clientId}/trial-balance?asOf={asOf:yyyy-MM-dd}"))!;
        return tb.Accounts.SingleOrDefault(a => a.AccountId == accountId)?.Balance ?? 0m;
    }
}
```

- [ ] **Step 2: Write the inception-floor test**

Create `Temporal/InceptionFloorE2eTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The inception floor: onboarding seeds a closed-period freeze at (opening date − 1), so a post
/// dated before the client's opening date is rejected while one on/after is accepted — and the opening
/// entry itself is not blocked by its own freeze.</summary>
public sealed class InceptionFloorE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Posts_before_the_opening_date_are_rejected_and_on_or_after_are_allowed()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId, "4000", "Revenue", "Revenue");
        Guid equity = await CreateAccountAsync(c.Http, c.ClientId, "3000", "Retained Earnings", "Equity", retained: true);

        // Onboard as of 2024-01-01 → the opening entry posts (not blocked by its own freeze) and the
        // inception freeze is seeded at 2023-12-31.
        HttpResponseMessage onboard = await OnboardAsync(c.Http, c.ClientId, new DateOnly(2024, 1, 1),
            (cash, 100m), (equity, -100m));
        Assert.Equal(HttpStatusCode.Created, onboard.StatusCode);
        EntryResponse opening = (await onboard.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal("Opening", opening.Type);

        // A post dated on the inception freeze (2023-12-31) is rejected as a closed period.
        HttpResponseMessage before = await PostAsync(c.Http, c.ClientId, new DateOnly(2023, 12, 31), cash, revenue, 10m);
        await AssertProblemAsync(before, HttpStatusCode.Conflict, "closed");

        // A post dated on the opening date (2024-01-01) is accepted.
        HttpResponseMessage onOpening = await PostAsync(c.Http, c.ClientId, new DateOnly(2024, 1, 1), cash, revenue, 10m);
        Assert.Equal(HttpStatusCode.Created, onOpening.StatusCode);
    }
}
```

- [ ] **Step 3: Run, expect PASS**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~InceptionFloorE2eTests"`
Expected: PASS. Any deviation (e.g. the before-date post is not 409, or the message lacks "closed") is a finding — STOP.

- [ ] **Step 4: Commit**

```bash
git add Backend/Accounting101.Ledger.Api.Tests/Temporal/TemporalScenario.cs \
        Backend/Accounting101.Ledger.Api.Tests/Temporal/InceptionFloorE2eTests.cs
git commit -m "$(cat <<'EOF'
test(ledger): inception-floor E2E + shared temporal scenario helper

Pins end-to-end that onboarding freezes pre-opening dates: a post dated
before the opening date is rejected (409 closed), one on/after is accepted,
and the opening entry is not blocked by its own freeze.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Reopen behavioral effect E2E

**Files:**
- Create: `Backend/Accounting101.Ledger.Api.Tests/Temporal/ReopenEffectE2eTests.cs`

**Interfaces:**
- Consumes: `TemporalScenario` (Task 1).

- [ ] **Step 1: Write the reopen-effect tests**

Create `Temporal/ReopenEffectE2eTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>Reopen's behavioral effect (beyond the authz checks in ReopenTests): a full reopen re-opens a
/// frozen period so a previously-blocked backdated post succeeds; a partial reopen moves the freeze to an
/// earlier date (posts after the new freeze succeed, posts still before it are rejected); and a reopen that
/// does not move earlier than the current close is refused.</summary>
public sealed class ReopenEffectE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(SeededClient c, Guid cash, Guid revenue)> ArrangeClosedAsync(DateOnly closeThrough)
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Admin);
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId, "4000", "Revenue", "Revenue");
        await PostAndApproveAsync(c.Http, c.ClientId, closeThrough, cash, revenue, 100m);
        (await CloseAsync(c.Http, c.ClientId, closeThrough)).EnsureSuccessStatusCode();
        return (c, cash, revenue);
    }

    [Fact]
    public async Task A_full_reopen_lets_a_previously_blocked_backdated_post_succeed()
    {
        (SeededClient c, Guid cash, Guid revenue) = await ArrangeClosedAsync(new DateOnly(2026, 3, 31));

        // Frozen: a backdated post is refused.
        HttpResponseMessage blocked = await PostAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 15), cash, revenue, 10m);
        await AssertProblemAsync(blocked, HttpStatusCode.Conflict, "closed");

        // Full reopen (clear the freeze).
        Assert.Equal(HttpStatusCode.NoContent,
            (await ReopenAsync(fixture, c.ClientId, c.UserId, null, "closed too early")).StatusCode);

        // The same backdated post now succeeds.
        HttpResponseMessage ok = await PostAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 15), cash, revenue, 10m);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task A_partial_reopen_moves_the_freeze_earlier()
    {
        (SeededClient c, Guid cash, Guid revenue) = await ArrangeClosedAsync(new DateOnly(2026, 3, 31));

        // Reopen the freeze back to 2026-02-28 (closed-through now 2026-02-28).
        Assert.Equal(HttpStatusCode.NoContent,
            (await ReopenAsync(fixture, c.ClientId, c.UserId, new DateOnly(2026, 2, 28), "partial")).StatusCode);

        // A post dated AFTER the new freeze (in March, previously closed) now succeeds.
        HttpResponseMessage march = await PostAsync(c.Http, c.ClientId, new DateOnly(2026, 3, 15), cash, revenue, 10m);
        Assert.Equal(HttpStatusCode.Created, march.StatusCode);

        // A post still on/before the new freeze (2026-02-28) is still rejected.
        HttpResponseMessage feb = await PostAsync(c.Http, c.ClientId, new DateOnly(2026, 2, 28), cash, revenue, 10m);
        await AssertProblemAsync(feb, HttpStatusCode.Conflict, "closed");
    }

    [Fact]
    public async Task A_reopen_that_does_not_move_earlier_is_refused()
    {
        (SeededClient c, _, _) = await ArrangeClosedAsync(new DateOnly(2026, 3, 31));

        // Reopen "through" a date >= the current close is not a reopen — refused.
        HttpResponseMessage resp = await ReopenAsync(fixture, c.ClientId, c.UserId, new DateOnly(2026, 4, 30), "noop");
        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "earlier");
    }
}
```

- [ ] **Step 2: Run, expect PASS**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~ReopenEffectE2eTests"`
Expected: 3 PASS. Deviation → finding → STOP.

- [ ] **Step 3: Commit**

```bash
git add Backend/Accounting101.Ledger.Api.Tests/Temporal/ReopenEffectE2eTests.cs
git commit -m "$(cat <<'EOF'
test(ledger): reopen behavioral-effect E2E

Pins that a full reopen re-opens a frozen period (the blocked backdated post
then succeeds), a partial reopen moves the freeze earlier (March opens, Feb
stays frozen), and a reopen that does not move earlier than the current close
is refused (409).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: February / leap-year fiscal-year-end E2E

**Files:**
- Create: `Backend/Accounting101.Ledger.Api.Tests/Temporal/FiscalYearBoundaryE2eTests.cs`

**Interfaces:**
- Consumes: `TemporalScenario` (Task 1).

`EndDateFor(2, 2024) = 2024-02-29` (leap); `EndDateFor(2, 2025) = 2025-02-28`; `EndDateFor(2, 2024) ≠ 2024-06-30`.

- [ ] **Step 1: Write the February-FY tests**

Create `Temporal/FiscalYearBoundaryE2eTests.cs`:

```csharp
using System.Net;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>Fiscal-year-end guards for a FEBRUARY-fiscal-year client — exercises the leap-aware
/// FiscalYear.EndDateFor end-to-end (2024-02-29 leap, 2025-02-28 non-leap), extending the Dec/June
/// coverage in FiscalYearCloseGuardTests.</summary>
public sealed class FiscalYearBoundaryE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(Guid clientId, HttpClient http, Guid cash, Guid revenue)> ArrangeFebClientAsync(string name)
    {
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync(fixture, 2, name);
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        await CreateAccountAsync(http, clientId, "3900", "Retained Earnings", "Equity", retained: true);
        return (clientId, http, cash, revenue);
    }

    [Fact]
    public async Task Close_year_on_the_leap_day_fiscal_year_end_succeeds()
    {
        (Guid clientId, HttpClient http, Guid cash, Guid revenue) = await ArrangeFebClientAsync("FebCo-Leap");
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 2, 29), cash, revenue, 100m);

        Assert.Equal(HttpStatusCode.OK, (await CloseYearAsync(http, clientId, new DateOnly(2024, 2, 29))).StatusCode);
    }

    [Fact]
    public async Task Monthly_close_on_the_leap_day_fiscal_year_end_is_refused()
    {
        (Guid clientId, HttpClient http, Guid cash, Guid revenue) = await ArrangeFebClientAsync("FebCo-LeapBlock");
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 2, 29), cash, revenue, 100m);

        await AssertProblemAsync(await CloseAsync(http, clientId, new DateOnly(2024, 2, 29)),
            HttpStatusCode.Conflict, "close-year");
    }

    [Fact]
    public async Task Close_year_on_the_non_leap_year_end_succeeds()
    {
        (Guid clientId, HttpClient http, Guid cash, Guid revenue) = await ArrangeFebClientAsync("FebCo-NonLeap");
        // FY2025 ends 2025-02-28 (non-leap). Activity dated on that date, then close-year it.
        await PostAndApproveAsync(http, clientId, new DateOnly(2025, 2, 28), cash, revenue, 100m);

        Assert.Equal(HttpStatusCode.OK, (await CloseYearAsync(http, clientId, new DateOnly(2025, 2, 28))).StatusCode);
    }

    [Fact]
    public async Task Close_year_on_a_wrong_date_names_the_february_fiscal_year_end()
    {
        (Guid clientId, HttpClient http, _, _) = await ArrangeFebClientAsync("FebCo-WrongDate");

        // 2024-06-30 is not the Feb client's FY-end; the guard names the real one (2024-02-29).
        await AssertProblemAsync(await CloseYearAsync(http, clientId, new DateOnly(2024, 6, 30)),
            HttpStatusCode.Conflict, "2024-02-29");
    }
}
```

- [ ] **Step 2: Run, expect PASS**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~FiscalYearBoundaryE2eTests"`
Expected: 4 PASS. If the leap-day close-year is rejected, or the wrong-date guard names a different date, that is a finding — STOP.

- [ ] **Step 3: Commit**

```bash
git add Backend/Accounting101.Ledger.Api.Tests/Temporal/FiscalYearBoundaryE2eTests.cs
git commit -m "$(cat <<'EOF'
test(ledger): February/leap-year fiscal-year-end guards E2E

Exercises the leap-aware EndDateFor end-to-end for a February-FY client:
close-year on 2024-02-29 (leap) and 2025-02-28 (non-leap) succeed; a monthly
close on the leap-day FY-end is refused (use close-year); a wrong-date
close-year names the real FY-end (2024-02-29).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Monotonic close pointer + no-RE close-year E2E

**Files:**
- Create: `Backend/Accounting101.Ledger.Api.Tests/Temporal/PeriodCloseSequenceE2eTests.cs`

**Interfaces:**
- Consumes: `TemporalScenario` (Task 1).

- [ ] **Step 1: Write the tests**

Create `Temporal/PeriodCloseSequenceE2eTests.cs`:

```csharp
using System.Net;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The close pointer is a single monotonic "closed-through" high-water date: closing at or before
/// the current close is refused, a later date is accepted. Plus: a year-end close with temporary-account
/// activity but no retained-earnings account is refused with a clear reason.</summary>
public sealed class PeriodCloseSequenceE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task The_close_pointer_is_monotonic()
    {
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync(fixture, 12, "MonoCo");
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 2, 15), cash, revenue, 50m);
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 4, 15), cash, revenue, 50m);

        // Close through March 31.
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(http, clientId, new DateOnly(2024, 3, 31))).StatusCode);

        // Closing at an earlier date is refused (already closed through 2024-03-31).
        await AssertProblemAsync(await CloseAsync(http, clientId, new DateOnly(2024, 2, 28)),
            HttpStatusCode.Conflict, "already closed");

        // Closing at the SAME date is refused too.
        await AssertProblemAsync(await CloseAsync(http, clientId, new DateOnly(2024, 3, 31)),
            HttpStatusCode.Conflict, "already closed");

        // Closing at a LATER date is accepted.
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(http, clientId, new DateOnly(2024, 4, 30))).StatusCode);
    }

    [Fact]
    public async Task Close_year_without_a_retained_earnings_account_is_refused()
    {
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync(fixture, 12, "NoReCo");
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        // Temporary-account activity exists, but NO retained-earnings account is designated.
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 12, 31), cash, revenue, 100m);

        await AssertProblemAsync(await CloseYearAsync(http, clientId, new DateOnly(2024, 12, 31)),
            HttpStatusCode.Conflict, "retained-earnings account");
    }
}
```

- [ ] **Step 2: Run, expect PASS**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~PeriodCloseSequenceE2eTests"`
Expected: 2 PASS. If the earlier/equal close is not 409 "already closed", or the no-RE close-year is not 409 "retained-earnings account", that is a finding — STOP.

- [ ] **Step 3: Commit**

```bash
git add Backend/Accounting101.Ledger.Api.Tests/Temporal/PeriodCloseSequenceE2eTests.cs
git commit -m "$(cat <<'EOF'
test(ledger): monotonic close pointer + no-RE close-year E2E

Pins that the closed-through pointer only moves forward (close at/before the
current close is refused, a later date is accepted) and that a year-end close
with temporary activity but no retained-earnings account is refused with a
clear reason.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Two-year fiscal-cycle integrity sweep E2E

**Files:**
- Create: `Backend/Accounting101.Ledger.Api.Tests/Temporal/FiscalCycleIntegrityE2eTests.cs`

**Interfaces:**
- Consumes: `TemporalScenario` (Task 1).

Expected balances (signed, debit-positive). FY2024: revenue cr 1000, expense dr 600 → net income cr 400 → after close-year, revenue = 0, expense = 0, retained earnings = −400 (credit). FY2025: revenue cr 800, expense dr 300 → net income cr 500 → after close-year, revenue = 0, expense = 0, retained earnings = −900 (accumulated −400 − 500).

- [ ] **Step 1: Write the integrity sweep**

Create `Temporal/FiscalCycleIntegrityE2eTests.cs`:

```csharp
using System.Net;
using static Accounting101.Ledger.Api.Tests.TemporalScenario;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>A two-year fiscal cycle: each year-end close zeros the temporary accounts and rolls net income
/// into retained earnings, and retained earnings ACCUMULATES across both years. The books balance at each
/// year-end.</summary>
public sealed class FiscalCycleIntegrityE2eTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Retained_earnings_accumulates_and_temporaries_re_zero_across_two_year_ends()
    {
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync(fixture, 12, "CycleCo");
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        Guid expense = await CreateAccountAsync(http, clientId, "5000", "Expense", "Expense");
        Guid retained = await CreateAccountAsync(http, clientId, "3900", "Retained Earnings", "Equity", retained: true);

        // FY2024: revenue 1000, expense 600 → net income 400.
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 6, 15), cash, revenue, 1000m);
        await PostAndApproveAsync(http, clientId, new DateOnly(2024, 6, 20), expense, cash, 600m);
        Assert.Equal(HttpStatusCode.OK, (await CloseYearAsync(http, clientId, new DateOnly(2024, 12, 31))).StatusCode);

        DateOnly ye1 = new(2024, 12, 31);
        Assert.Equal(0m, await AccountBalanceAsync(http, clientId, revenue, ye1));    // temporary zeroed
        Assert.Equal(0m, await AccountBalanceAsync(http, clientId, expense, ye1));    // temporary zeroed
        Assert.Equal(-400m, await AccountBalanceAsync(http, clientId, retained, ye1)); // net income #1 (credit)
        await AssertBalancedAsync(http, clientId, ye1);

        // FY2025: revenue 800, expense 300 → net income 500.
        await PostAndApproveAsync(http, clientId, new DateOnly(2025, 6, 15), cash, revenue, 800m);
        await PostAndApproveAsync(http, clientId, new DateOnly(2025, 6, 20), expense, cash, 300m);
        Assert.Equal(HttpStatusCode.OK, (await CloseYearAsync(http, clientId, new DateOnly(2025, 12, 31))).StatusCode);

        DateOnly ye2 = new(2025, 12, 31);
        Assert.Equal(0m, await AccountBalanceAsync(http, clientId, revenue, ye2));    // re-zeroed
        Assert.Equal(0m, await AccountBalanceAsync(http, clientId, expense, ye2));    // re-zeroed
        Assert.Equal(-900m, await AccountBalanceAsync(http, clientId, retained, ye2)); // ACCUMULATED (−400 − 500)
        await AssertBalancedAsync(http, clientId, ye2);
    }
}
```

- [ ] **Step 2: Run, expect PASS**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj --filter "FullyQualifiedName~FiscalCycleIntegrityE2eTests"`
Expected: PASS. If retained earnings does not accumulate to −900, or a temporary is not re-zeroed, or a balance sheet is unbalanced, that is a finding — STOP and report the observed values.

- [ ] **Step 3: Run the full Ledger.Api suite — confirm no regressions**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests/Accounting101.Ledger.Api.Tests.csproj`
Expected: green (modulo any pre-existing unrelated failure noted in memory, e.g. a host-dependent test — confirm any failure predates this branch via `git stash` + re-run if unsure).

- [ ] **Step 4: Commit**

```bash
git add Backend/Accounting101.Ledger.Api.Tests/Temporal/FiscalCycleIntegrityE2eTests.cs
git commit -m "$(cat <<'EOF'
test(ledger): two-year fiscal-cycle integrity sweep E2E

Across two consecutive year-end closes, asserts temporaries re-zero each year
and retained earnings accumulates (−400 then −900), with the balance sheet
balanced at each year-end.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- Inception floor E2E → Task 1. ✓
- Reopen behavioral effect (full clear / partial / not-earlier guard) → Task 2. ✓
- February/leap-year FY-end → Task 3. ✓
- Monotonic close pointer + no-RE close-year → Task 4. ✓
- Two-year fiscal-cycle integrity sweep (RE accumulation, temporary re-zeroing, balanced) → Task 5. ✓
- Assertions are status+substring (rejections) / exact balances + IsBalanced (accepted). ✓
- Does NOT duplicate Dec/June FY-end, pending-blocker, post-into-closed, reverse-into-closed (none of those scenarios appear). ✓
- Discoveries-are-findings: Global Constraints + per-task STOP notes. ✓

**2. Placeholder scan:** No TBD/TODO; every test body complete; all commands explicit. ✓

**3. Type consistency:** Helper signatures defined in Task 1 are used consistently in Tasks 2-5 (`SeedFyeClientAsync`, `CreateAccountAsync`, `OnboardAsync`, `PostAsync`, `PostAndApproveAsync`, `CloseAsync`, `CloseYearAsync`, `ReopenAsync`, `AssertProblemAsync`, `AssertBalancedAsync`, `AccountBalanceAsync`). DTO/route names match the confirmed-contracts section (`OnboardingRequest`/`OpeningBalanceLine`, `ClosePeriodRequest`, `CloseYearRequest`, `ReopenRequest`, `CreateClientRequest`/`AddMemberRequest`/`ClientRegistrationResponse`, `TrialBalanceResponse`/`AccountBalanceResponse`, `BalanceSheetResponse`). `SeededClient(ClientId, Database, UserId, Http)` shape matches `ApiFixture`. ✓
