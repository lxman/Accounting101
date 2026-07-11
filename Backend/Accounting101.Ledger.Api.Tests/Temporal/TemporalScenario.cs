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
    /// member, and return an HttpClient authenticated as that member. Explicitly requests SelfApprove —
    /// these scenarios drive the whole lifecycle (post + approve + close) through one actor, and are not
    /// about segregation of duties; the admin create handler otherwise defaults an omitted mode to
    /// TwoPerson, which would forbid this single actor from approving its own posts.</summary>
    internal static async Task<(Guid ClientId, HttpClient Http)> SeedFyeClientAsync(
        ApiFixture fixture, int fiscalYearEndMonth, string name)
    {
        HttpClient admin = fixture.AdminClient();
        ClientRegistrationResponse reg = (await (await admin.PostAsJsonAsync(
                "/admin/clients", new CreateClientRequest
                {
                    Name = name, FiscalYearEndMonth = fiscalYearEndMonth, ApprovalMode = ApprovalMode.SelfApprove,
                }))
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
