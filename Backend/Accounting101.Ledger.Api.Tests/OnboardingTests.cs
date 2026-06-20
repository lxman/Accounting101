using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Onboarding: a client's carried-in opening balances are booked as one balanced Opening journal
/// entry, so they flow into the trial balance like any posting. The figures must balance, and only
/// setup roles (Controller / Admin) may run it.
/// </summary>
public sealed class OnboardingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> CreateAccountAsync(
        HttpClient http, Guid client, string number, string name, string type, bool retained = false)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{client}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type, IsRetainedEarnings = retained })).EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task Opening_balances_are_booked_and_flow_into_the_trial_balance()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid loan = await CreateAccountAsync(c.Http, c.ClientId, "2000", "Note Payable", "Liability");
        Guid equity = await CreateAccountAsync(c.Http, c.ClientId, "3000", "Retained Earnings", "Equity", retained: true);

        // Carried-in trial balance: cash 1000 (debit), loan 400 (credit), equity 600 (credit) — nets to zero.
        OnboardingRequest request = new(new DateOnly(2025, 12, 31),
        [
            new OpeningBalanceLine(cash, 1000m),
            new OpeningBalanceLine(loan, -400m),
            new OpeningBalanceLine(equity, -600m),
        ]);

        HttpResponseMessage resp = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/onboarding", request);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        EntryResponse opening = (await resp.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal("Opening", opening.Type);
        Assert.Equal("Posted", opening.Posting);

        TrialBalanceResponse tb = (await c.Http.GetFromJsonAsync<TrialBalanceResponse>($"/clients/{c.ClientId}/trial-balance"))!;
        decimal Bal(Guid a) => tb.Accounts.Single(x => x.AccountId == a).Balance;
        Assert.Equal(1000m, Bal(cash));
        Assert.Equal(-400m, Bal(loan));
        Assert.Equal(-600m, Bal(equity));
    }

    [Fact]
    public async Task An_unbalanced_opening_is_rejected()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid equity = await CreateAccountAsync(c.Http, c.ClientId, "3000", "Retained Earnings", "Equity", retained: true);

        OnboardingRequest request = new(new DateOnly(2025, 12, 31),
        [
            new OpeningBalanceLine(cash, 1000m),
            new OpeningBalanceLine(equity, -900m), // does not net to zero
        ]);

        HttpResponseMessage resp = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/onboarding", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task A_clerk_cannot_onboard()
    {
        SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Clerk);
        OnboardingRequest request = new(new DateOnly(2025, 12, 31),
        [
            new OpeningBalanceLine(Guid.NewGuid(), 100m),
            new OpeningBalanceLine(Guid.NewGuid(), -100m),
        ]);

        HttpResponseMessage resp = await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/onboarding", request);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
