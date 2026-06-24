using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The subsidiary ledger end to end: posting A/R with a customer dimension lets the books be broken out
/// per customer, the per-customer balances tie to the A/R control balance on the trial balance, a
/// customer's detail is resolvable, and the dimension is required.
/// </summary>
public sealed class SubledgerTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task PostArSaleAsync(
        HttpClient http, Guid client, long seq, Guid ar, Guid revenue, Guid customer, decimal amount)
    {
        _ = seq; // sequence is engine-assigned now
        PostEntryRequest entry = new(
            null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(ar, "Debit", amount, Dimensions: new Dictionary<string, Guid> { ["Customer"] = customer }),
             new PostLineRequest(revenue, "Credit", amount)]);

        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", entry);
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    // Register a standard A/R control account (RequiredDimension = Customer).
    private static async Task<Guid> RegisterArAsync(HttpClient http, Guid clientId)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}",
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" }))
            .EnsureSuccessStatusCode();
        return id;
    }

    // Register a plain Revenue account (no RequiredDimension).
    private static async Task<Guid> RegisterRevenueAsync(HttpClient http, Guid clientId)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}",
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" }))
            .EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task Subledger_breaks_ar_out_by_customer_and_ties_to_the_control_balance()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // Register accounts in the chart so the control-account guard can validate RequiredDimension.
        Guid ar = await RegisterArAsync(c.Http, c.ClientId);
        Guid revenue = await RegisterRevenueAsync(c.Http, c.ClientId);

        Guid custA = Guid.NewGuid(), custB = Guid.NewGuid();

        await PostArSaleAsync(c.Http, c.ClientId, 1, ar, revenue, custA, 100m);
        await PostArSaleAsync(c.Http, c.ClientId, 2, ar, revenue, custB, 60m);
        await PostArSaleAsync(c.Http, c.ClientId, 3, ar, revenue, custA, 40m);

        SubledgerResponse sub = (await c.Http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{c.ClientId}/subledger?dimension=Customer&account={ar}"))!;

        Assert.Equal("Customer", sub.Dimension);
        Assert.Equal(140m, sub.Lines.Single(l => l.DimensionValue == custA).Balance);
        Assert.Equal(60m, sub.Lines.Single(l => l.DimensionValue == custB).Balance);

        // The subledger ties to the A/R control balance the trial balance reports.
        TrialBalanceResponse tb = (await c.Http.GetFromJsonAsync<TrialBalanceResponse>(
            $"/clients/{c.ClientId}/trial-balance"))!;
        Assert.Equal(tb.Accounts.Single(a => a.AccountId == ar).Balance, sub.Lines.Sum(l => l.Balance));
    }

    [Fact]
    public async Task A_customers_detail_is_resolvable_through_the_entry_list()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid ar = Guid.NewGuid(), revenue = Guid.NewGuid();
        Guid custA = Guid.NewGuid(), custB = Guid.NewGuid();

        await PostArSaleAsync(c.Http, c.ClientId, 1, ar, revenue, custA, 100m);
        await PostArSaleAsync(c.Http, c.ClientId, 2, ar, revenue, custB, 60m);
        await PostArSaleAsync(c.Http, c.ClientId, 3, ar, revenue, custA, 40m);

        List<EntryResponse> forCustA = (await c.Http.GetFromJsonAsync<List<EntryResponse>>(
            $"/clients/{c.ClientId}/entries?dimension=Customer&value={custA}"))!;

        Assert.Equal([1, 3], forCustA.Select(e => e.SequenceNumber).OrderBy(n => n));
    }

    [Fact]
    public async Task Reconciliation_ties_out_when_every_control_line_is_tagged()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // Register accounts so the control-account guard can validate RequiredDimension.
        Guid ar = await RegisterArAsync(c.Http, c.ClientId);
        Guid revenue = await RegisterRevenueAsync(c.Http, c.ClientId);

        Guid custA = Guid.NewGuid(), custB = Guid.NewGuid();

        await PostArSaleAsync(c.Http, c.ClientId, 1, ar, revenue, custA, 100m);
        await PostArSaleAsync(c.Http, c.ClientId, 2, ar, revenue, custB, 60m);

        SubledgerReconciliationResponse rec = (await c.Http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliation?account={ar}&dimension=Customer"))!;

        Assert.Equal(160m, rec.ControlBalance);
        Assert.Equal(160m, rec.SubledgerTotal);
        Assert.Equal(0m, rec.Variance);
        Assert.True(rec.TiesOut);
    }

    [Fact]
    public async Task Reconciliation_with_all_lines_tagged_to_different_customers_ties_out()
    {
        // Replaces the former "untagged remainder" test. Under the new control-account guard,
        // posting an untagged line to a RequiredDimension account is blocked at the chart layer,
        // so the "variance = whole balance" scenario that motivated this guard can no longer arise
        // once accounts are properly registered. This test confirms the reconciliation still
        // handles multiple tagged lines and reports a zero variance correctly.
        SeededClient c = await fixture.SeedClientAsync();

        Guid ar = await RegisterArAsync(c.Http, c.ClientId);
        Guid revenue = await RegisterRevenueAsync(c.Http, c.ClientId);

        Guid custA = Guid.NewGuid(), custB = Guid.NewGuid();

        await PostArSaleAsync(c.Http, c.ClientId, 1, ar, revenue, custA, 100m);
        await PostArSaleAsync(c.Http, c.ClientId, 2, ar, revenue, custB, 50m);

        SubledgerReconciliationResponse rec = (await c.Http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliation?account={ar}&dimension=Customer"))!;

        Assert.Equal(150m, rec.ControlBalance);
        Assert.Equal(150m, rec.SubledgerTotal);
        Assert.Equal(0m, rec.Variance);
        Assert.True(rec.TiesOut);
    }

    [Fact]
    public async Task Subledger_requires_a_dimension_but_accepts_any_type()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // Missing the dimension is the only bad request — the engine doesn't have a fixed dimension vocabulary.
        HttpResponseMessage missing = await c.Http.GetAsync($"/clients/{c.ClientId}/subledger");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);

        // An axis the engine has never heard of is valid — it just has no postings yet, so it returns empty.
        SubledgerResponse department = (await c.Http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{c.ClientId}/subledger?dimension=Department"))!;
        Assert.Equal("Department", department.Dimension);
        Assert.Empty(department.Lines);
    }

    // ---- Control-account guard tests --------------------------------------------------------

    private static async Task<Guid> CreateAccountAsync(
        HttpClient http, Guid clientId, AccountRequest request)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", request))
            .EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task Reconciliation_on_a_non_control_account_returns_422()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // Accrued Expenses — a plain liability with no RequiredDimension.
        Guid accruedExpenses = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "2100", Name = "Accrued Expenses", Type = "Liability" });
        Guid cashAccount = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });

        // Post a balanced entry touching the non-control account.
        PostEntryRequest entry = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(cashAccount, "Debit", 100m),
             new PostLineRequest(accruedExpenses, "Credit", 100m)]);
        PostEntryResponse posted = (await (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", entry))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{posted.Id}/approve", null)).EnsureSuccessStatusCode();

        // Reconciliation against a non-control account must be rejected with 422.
        HttpResponseMessage resp = await c.Http.GetAsync(
            $"/clients/{c.ClientId}/subledger/reconciliation?account={accruedExpenses}&dimension=Vendor");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not a control account", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconciliation_with_mismatched_dimension_returns_422()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // A/R is a control account requiring the "Customer" dimension.
        Guid ar = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" });
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });

        Guid custA = Guid.NewGuid();
        PostEntryRequest entry = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(ar, "Debit", 100m, Dimensions: new Dictionary<string, Guid> { ["Customer"] = custA }),
             new PostLineRequest(revenue, "Credit", 100m)]);
        PostEntryResponse posted = (await (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", entry))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{posted.Id}/approve", null)).EnsureSuccessStatusCode();

        // Asking for dimension=Vendor when the control requires Customer must be rejected with 422.
        HttpResponseMessage resp = await c.Http.GetAsync(
            $"/clients/{c.ClientId}/subledger/reconciliation?account={ar}&dimension=Vendor");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Customer", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconciliation_happy_path_on_control_account_with_correct_dimension_succeeds()
    {
        SeededClient c = await fixture.SeedClientAsync();

        Guid ar = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" });
        Guid revenue = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });

        Guid custA = Guid.NewGuid();
        PostEntryRequest entry = new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(ar, "Debit", 200m, Dimensions: new Dictionary<string, Guid> { ["Customer"] = custA }),
             new PostLineRequest(revenue, "Credit", 200m)]);
        PostEntryResponse posted = (await (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/entries", entry))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{posted.Id}/approve", null)).EnsureSuccessStatusCode();

        SubledgerReconciliationResponse rec = (await c.Http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliation?account={ar}&dimension=Customer"))!;

        Assert.Equal(200m, rec.ControlBalance);
        Assert.Equal(200m, rec.SubledgerTotal);
        Assert.Equal(0m, rec.Variance);
        Assert.True(rec.TiesOut);
    }

    [Fact]
    public async Task GetSubledger_with_named_non_control_account_returns_422()
    {
        SeededClient c = await fixture.SeedClientAsync();

        Guid plain = await CreateAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "5000", Name = "Salaries Expense", Type = "Expense" });

        HttpResponseMessage resp = await c.Http.GetAsync(
            $"/clients/{c.ClientId}/subledger?dimension=Vendor&account={plain}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not a control account", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSubledger_without_account_accepts_any_dimension_unchanged()
    {
        SeededClient c = await fixture.SeedClientAsync();

        // account-less cross-account query must still work — guard only fires when account is named.
        SubledgerResponse resp = (await c.Http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{c.ClientId}/subledger?dimension=Vendor"))!;
        Assert.Equal("Vendor", resp.Dimension);
        Assert.Empty(resp.Lines);
    }
}
