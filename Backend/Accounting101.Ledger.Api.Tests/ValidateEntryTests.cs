using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// <c>POST /clients/{clientId}/entries/validate</c> — a side-effect-free dry run of a post.
/// Every test asserts that the journal is unchanged after the call (no new entries written).
/// </summary>
public sealed class ValidateEntryTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ---- helpers -------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> ValidateAsync(HttpClient http, Guid clientId, PostEntryRequest request) =>
        http.PostAsJsonAsync($"/clients/{clientId}/entries/validate", request);

    private static Task<HttpResponseMessage> PostEntryAsync(HttpClient http, Guid clientId, PostEntryRequest request) =>
        http.PostAsJsonAsync($"/clients/{clientId}/entries", request);

    private static PostEntryRequest BalancedEntry(Guid debit, Guid credit, decimal amount = 100m, DateOnly? date = null) =>
        new(null, date ?? new DateOnly(2026, 6, 30), null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

    private static PostEntryRequest UnbalancedEntry(Guid debit, Guid credit) =>
        new(null, new DateOnly(2026, 6, 30), null, null,
            [new PostLineRequest(debit, "Debit", 100m), new PostLineRequest(credit, "Credit", 99m)]);

    /// <summary>Count of journal entries visible via GET /entries for this client.</summary>
    private static async Task<int> EntryCountAsync(HttpClient http, Guid clientId)
    {
        HttpResponseMessage resp = await http.GetAsync($"/clients/{clientId}/entries");
        resp.EnsureSuccessStatusCode();
        List<EntryResponse>? entries = await resp.Content.ReadFromJsonAsync<List<EntryResponse>>();
        return entries?.Count ?? 0;
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient http, Guid clientId, AccountRequest request)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", request)).EnsureSuccessStatusCode();
        return id;
    }

    // ---- tests ---------------------------------------------------------------------------------

    /// <summary>(a) Balanced entry, open period, chart-valid → 200 {valid:true}, journal unchanged.</summary>
    [Fact]
    public async Task Validate_balanced_open_period_entry_returns_200_valid_and_writes_nothing()
    {
        SeededClient c = await fixture.SeedClientAsync();
        int before = await EntryCountAsync(c.Http, c.ClientId);

        PostEntryRequest request = BalancedEntry(Guid.NewGuid(), Guid.NewGuid());
        HttpResponseMessage resp = await ValidateAsync(c.Http, c.ClientId, request);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        EntryValidationResponse? body = await resp.Content.ReadFromJsonAsync<EntryValidationResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Valid);

        int after = await EntryCountAsync(c.Http, c.ClientId);
        Assert.Equal(before, after); // nothing written
    }

    /// <summary>(b) Effective date in a closed period → 409 detail contains "closed", no write.</summary>
    [Fact]
    public async Task Validate_closed_period_date_returns_409_closed_and_writes_nothing()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // Post an entry and approve it so there is a balance to snapshot, then close through that date.
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        HttpResponseMessage posted = await PostEntryAsync(c.Http, c.ClientId, BalancedEntry(debit, credit, date: new DateOnly(2026, 3, 31)));
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/periods/close",
            new ClosePeriodRequest(new DateOnly(2026, 3, 31)))).EnsureSuccessStatusCode();

        int before = await EntryCountAsync(c.Http, c.ClientId);

        // Validate with a date inside the closed period.
        PostEntryRequest request = BalancedEntry(debit, credit, date: new DateOnly(2026, 3, 15));
        HttpResponseMessage resp = await ValidateAsync(c.Http, c.ClientId, request);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("closed", problem!.Detail, StringComparison.OrdinalIgnoreCase);

        int after = await EntryCountAsync(c.Http, c.ClientId);
        Assert.Equal(before, after); // nothing written
    }

    /// <summary>(c) Unbalanced entry → 422, no write.</summary>
    [Fact]
    public async Task Validate_unbalanced_entry_returns_422_and_writes_nothing()
    {
        SeededClient c = await fixture.SeedClientAsync();
        int before = await EntryCountAsync(c.Http, c.ClientId);

        PostEntryRequest request = UnbalancedEntry(Guid.NewGuid(), Guid.NewGuid());
        HttpResponseMessage resp = await ValidateAsync(c.Http, c.ClientId, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        int after = await EntryCountAsync(c.Http, c.ClientId);
        Assert.Equal(before, after); // nothing written
    }

    /// <summary>(d) Missing/non-postable account or missing required dimension → 422, no write.</summary>
    [Fact]
    public async Task Validate_missing_account_returns_422_and_writes_nothing()
    {
        SeededClient c = await fixture.SeedClientAsync();
        // Create one account so the chart is non-empty (triggers the chart validation path).
        Guid knownAccount = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });

        int before = await EntryCountAsync(c.Http, c.ClientId);

        // Second line hits a random Guid not in the chart.
        PostEntryRequest request = BalancedEntry(knownAccount, Guid.NewGuid());
        HttpResponseMessage resp = await ValidateAsync(c.Http, c.ClientId, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        int after = await EntryCountAsync(c.Http, c.ClientId);
        Assert.Equal(before, after); // nothing written
    }

    [Fact]
    public async Task Validate_missing_required_dimension_returns_422_and_writes_nothing()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid receivable = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" });
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });

        int before = await EntryCountAsync(c.Http, c.ClientId);

        // Missing the required Customer dimension.
        PostEntryRequest request = BalancedEntry(receivable, revenue);
        HttpResponseMessage resp = await ValidateAsync(c.Http, c.ClientId, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        int after = await EntryCountAsync(c.Http, c.ClientId);
        Assert.Equal(before, after); // nothing written
    }

    /// <summary>
    /// (f) Module credential authorizes validate — an Approver (no Post permission) can call
    /// <c>POST /entries/validate</c> when a registered + enabled module credential is supplied.
    /// Mirrors <see cref="ModulePostingTests.Module_credential_allows_approver_to_post_and_stamps_ViaModule"/>
    /// but hits the validate endpoint and asserts 200 {valid:true}.
    /// </summary>
    [Fact]
    public async Task Module_credential_authorizes_validate_for_user_without_Post_permission()
    {
        const string moduleKey = "receivables";
        const string moduleSecret = "rcv-secret-01";

        SeededClient c = await fixture.SeedClientAsync("ModuleValidateTest");

        // Add an Approver who does NOT hold Post permission.
        Guid approverId = Guid.NewGuid();
        await fixture.Control().AddMembershipAsync(approverId, c.ClientId, LedgerRole.Approver);
        HttpClient approverHttp = fixture.ClientFor(approverId, "Approver");

        // Register the module.
        await fixture.Control().RegisterModuleAsync(new ModuleRegistration
        {
            Key = moduleKey,
            Name = "Receivables",
            Enabled = true,
            Secret = moduleSecret,
        });

        PostEntryRequest body = BalancedEntry(Guid.NewGuid(), Guid.NewGuid());

        // The seeded client has an empty chart of accounts, so chart validation short-circuits and
        // returns valid:true even for random-GUID account IDs — the engine only validates accounts
        // that are actually in the chart.
        int before = await EntryCountAsync(approverHttp, c.ClientId);

        // Build a validate request with module headers.
        HttpRequestMessage req = new(HttpMethod.Post, $"/clients/{c.ClientId}/entries/validate");
        req.Headers.Add("X-Module-Key", moduleKey);
        req.Headers.Add("X-Module-Secret", moduleSecret);
        req.Content = JsonContent.Create(body);
        HttpResponseMessage resp = await approverHttp.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        EntryValidationResponse? result = await resp.Content.ReadFromJsonAsync<EntryValidationResponse>();
        Assert.NotNull(result);
        Assert.True(result!.Valid);

        int after = await EntryCountAsync(approverHttp, c.ClientId);
        Assert.Equal(before, after); // validate writes nothing
    }

    /// <summary>
    /// (e) Parity: validate and post the same closed-period request return identical status + detail.
    /// This guards against the validation routine drifting from the real post.
    /// </summary>
    [Fact]
    public async Task Validate_and_post_same_closed_period_request_return_identical_status_and_detail()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // Close a period.
        Guid debit = Guid.NewGuid(), credit = Guid.NewGuid();
        HttpResponseMessage posted = await PostEntryAsync(c.Http, c.ClientId,
            BalancedEntry(debit, credit, date: new DateOnly(2026, 3, 31)));
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/periods/close",
            new ClosePeriodRequest(new DateOnly(2026, 3, 31)))).EnsureSuccessStatusCode();

        PostEntryRequest closedPeriodRequest = BalancedEntry(debit, credit, date: new DateOnly(2026, 3, 15));

        // Validate the would-be post.
        HttpResponseMessage validateResp = await ValidateAsync(c.Http, c.ClientId, closedPeriodRequest);
        ProblemDetails? validateProblem = await validateResp.Content.ReadFromJsonAsync<ProblemDetails>();

        // Post the same request — should match exactly.
        HttpResponseMessage postResp = await PostEntryAsync(c.Http, c.ClientId, closedPeriodRequest);
        ProblemDetails? postProblem = await postResp.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(validateResp.StatusCode, postResp.StatusCode);
        Assert.Equal(validateProblem?.Detail, postProblem?.Detail);
    }
}
