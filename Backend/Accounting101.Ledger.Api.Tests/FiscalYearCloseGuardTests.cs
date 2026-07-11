using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// Fiscal-year-end close guards (host policy):
///   • ClosePeriod rejects a request whose asOf date is the client's fiscal year-end (must use close-year).
///   • CloseYear rejects a request whose fiscalYearEnd is NOT the client's fiscal year-end.
/// Both guards are per-client: a December client and a June client have symmetric but opposite reactions.
/// </summary>
public sealed class FiscalYearCloseGuardTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ---- Helpers ----------------------------------------------------------------------------

    private static async Task<Guid> CreateAccountAsync(
        HttpClient http, Guid clientId, string number, string name, string type)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type }))
            .EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>
    /// Post one entry dated <paramref name="date"/> and immediately approve it so no pending
    /// entries block a subsequent close.
    /// </summary>
    private static async Task PostAndApproveEntryAsync(
        HttpClient http, Guid clientId, Guid debit, Guid credit, DateOnly date)
    {
        HttpResponseMessage posted = await http.PostAsJsonAsync(
            $"/clients/{clientId}/entries",
            new PostEntryRequest(null, date, null, null,
            [
                new PostLineRequest(debit, "Debit", 100m),
                new PostLineRequest(credit, "Credit", 100m),
            ]));
        posted.EnsureSuccessStatusCode();
        Guid entryId = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!.Id;

        (await http.PostAsync($"/clients/{clientId}/entries/{entryId}/approve", null))
            .EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Seed a client via the admin HTTP endpoint so that <c>FiscalYearEndMonth</c> is set
    /// at the control-store level (same code path production uses).
    /// Returns an HttpClient authenticated as a Controller member of that client.
    /// </summary>
    private async Task<(Guid ClientId, HttpClient Http)> SeedFyeClientAsync(
        string name, int fiscalYearEndMonth)
    {
        HttpClient admin = fixture.AdminClient();

        // Create the client with the desired fiscal-year-end month. SelfApprove: these scenarios post and
        // approve through the one Controller actor and are not about segregation of duties; the admin create
        // handler otherwise defaults an omitted mode to TwoPerson, forbidding this actor's self-approve.
        HttpResponseMessage created = await admin.PostAsJsonAsync(
            "/admin/clients",
            new CreateClientRequest
            {
                Name = name, FiscalYearEndMonth = fiscalYearEndMonth, ApprovalMode = ApprovalMode.SelfApprove,
            });
        created.EnsureSuccessStatusCode();
        ClientRegistrationResponse reg = (await created.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;

        // Add a Controller member.
        Guid userId = Guid.NewGuid();
        (await admin.PostAsJsonAsync(
            $"/admin/clients/{reg.Id}/members",
            new AddMemberRequest(userId, "Controller")))
            .EnsureSuccessStatusCode();

        HttpClient http = fixture.ClientFor(userId, $"{name} Controller", ("role", "Controller"));
        return (reg.Id, http);
    }

    // ---- December-fiscal-year client --------------------------------------------------------

    [Fact]
    public async Task Monthly_close_on_a_non_fiscal_year_end_succeeds()
    {
        // Default December fiscal year-end; November 30 is a plain month-end — guard should pass.
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync("DecCo-NonFye", 12);
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");

        await PostAndApproveEntryAsync(http, clientId, cash, revenue, new DateOnly(2024, 11, 30));

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/periods/close",
            new ClosePeriodRequest(new DateOnly(2024, 11, 30)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Monthly_close_on_the_fiscal_year_end_is_refused()
    {
        // December 31 is the fiscal year-end for a December client → must use close-year.
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync("DecCo-FyeBlock", 12);
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");

        await PostAndApproveEntryAsync(http, clientId, cash, revenue, new DateOnly(2024, 12, 31));

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/periods/close",
            new ClosePeriodRequest(new DateOnly(2024, 12, 31)));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        // Body must mention the close-year endpoint.
        Assert.Contains("close-year", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Close_year_on_the_fiscal_year_end_succeeds()
    {
        // December 31 is the correct fiscal year-end date → close-year should succeed.
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync("DecCo-FyeOk", 12);
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        Guid retained = await CreateAccountAsync(http, clientId, "3900", "Retained Earnings", "Equity");

        // Mark retained-earnings account.
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{retained}",
            new AccountRequest { Number = "3900", Name = "Retained Earnings", Type = "Equity", IsRetainedEarnings = true }))
            .EnsureSuccessStatusCode();

        await PostAndApproveEntryAsync(http, clientId, cash, revenue, new DateOnly(2024, 12, 31));

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/periods/close-year",
            new CloseYearRequest(new DateOnly(2024, 12, 31)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Close_year_on_a_non_fiscal_year_end_is_refused()
    {
        // June 30 is NOT the fiscal year-end for a December client → guard rejects it.
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync("DecCo-WrongDate", 12);

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/periods/close-year",
            new CloseYearRequest(new DateOnly(2024, 6, 30)));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        // Body must name the real fiscal year-end.
        Assert.Contains("2024-12-31", body);
    }

    // ---- June-fiscal-year client ------------------------------------------------------------

    [Fact]
    public async Task June_client_monthly_close_on_june_30_is_refused()
    {
        // June 30 is the fiscal year-end for a June client → must use close-year.
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync("JuneCo-FyeBlock", 6);
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");

        await PostAndApproveEntryAsync(http, clientId, cash, revenue, new DateOnly(2024, 6, 30));

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/periods/close",
            new ClosePeriodRequest(new DateOnly(2024, 6, 30)));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("close-year", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task June_client_monthly_close_on_dec_31_succeeds()
    {
        // December 31 is an ordinary month-end for a June-FY client → guard should pass.
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync("JuneCo-NonFye", 6);
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");

        // Close June first (as close-year so we can reach December).
        Guid retained = await CreateAccountAsync(http, clientId, "3900", "Retained Earnings", "Equity");
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{retained}",
            new AccountRequest { Number = "3900", Name = "Retained Earnings", Type = "Equity", IsRetainedEarnings = true }))
            .EnsureSuccessStatusCode();

        await PostAndApproveEntryAsync(http, clientId, cash, revenue, new DateOnly(2024, 6, 30));
        (await http.PostAsJsonAsync(
            $"/clients/{clientId}/periods/close-year",
            new CloseYearRequest(new DateOnly(2024, 6, 30)))).EnsureSuccessStatusCode();

        // Now post a December entry and attempt a plain monthly close.
        await PostAndApproveEntryAsync(http, clientId, cash, revenue, new DateOnly(2024, 12, 31));

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/periods/close",
            new ClosePeriodRequest(new DateOnly(2024, 12, 31)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task June_client_close_year_on_june_30_succeeds()
    {
        // June 30 is the correct fiscal year-end for a June client.
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync("JuneCo-FyeOk", 6);
        Guid cash = await CreateAccountAsync(http, clientId, "1000", "Cash", "Asset");
        Guid revenue = await CreateAccountAsync(http, clientId, "4000", "Revenue", "Revenue");
        Guid retained = await CreateAccountAsync(http, clientId, "3900", "Retained Earnings", "Equity");

        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{retained}",
            new AccountRequest { Number = "3900", Name = "Retained Earnings", Type = "Equity", IsRetainedEarnings = true }))
            .EnsureSuccessStatusCode();

        await PostAndApproveEntryAsync(http, clientId, cash, revenue, new DateOnly(2024, 6, 30));

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/periods/close-year",
            new CloseYearRequest(new DateOnly(2024, 6, 30)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task June_client_close_year_on_dec_31_is_refused()
    {
        // December 31 is NOT the fiscal year-end for a June client → guard rejects it.
        (Guid clientId, HttpClient http) = await SeedFyeClientAsync("JuneCo-WrongDate", 6);

        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/clients/{clientId}/periods/close-year",
            new CloseYearRequest(new DateOnly(2024, 12, 31)));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        // Body must name the real fiscal year-end (June 30 2024).
        Assert.Contains("2024-06-30", body);
    }
}
