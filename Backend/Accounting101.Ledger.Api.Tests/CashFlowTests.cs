using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The cash-flow statement end to end: posted activity classifies into operating / investing / financing,
/// the statement ties out and its ending cash reconciles to the balance-sheet cash, a year-end close inside
/// the reporting window does not distort the period (closing entries are excluded), and the range is required.
/// </summary>
public sealed class CashFlowTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> CreateAccountAsync(
        HttpClient http, Guid client, string number, string name, string type,
        string? cashFlow = null, bool retained = false)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{client}/accounts/{id}", new AccountRequest
        {
            Number = number, Name = name, Type = type, CashFlowActivity = cashFlow, IsRetainedEarnings = retained,
        })).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task PostAndApproveAsync(
        HttpClient http, Guid client, long seq, DateOnly date, Guid debit, Guid credit, decimal amount)
    {
        PostEntryRequest entry = new(null, seq, date, null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", entry);
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Cash_flow_ties_out_and_reconciles_to_the_balance_sheet_cash()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset", cashFlow: "Cash");
        Guid ar = await CreateAccountAsync(c.Http, c.ClientId, "1100", "Accounts Receivable", "Asset");
        Guid equipment = await CreateAccountAsync(c.Http, c.ClientId, "1500", "Equipment", "Asset", cashFlow: "Investing");
        Guid loan = await CreateAccountAsync(c.Http, c.ClientId, "2500", "Loan Payable", "Liability", cashFlow: "Financing");
        Guid stock = await CreateAccountAsync(c.Http, c.ClientId, "3000", "Common Stock", "Equity");
        Guid sales = await CreateAccountAsync(c.Http, c.ClientId, "4000", "Sales", "Revenue");
        Guid wages = await CreateAccountAsync(c.Http, c.ClientId, "5000", "Wages", "Expense");

        DateOnly date = new(2026, 3, 15);
        await PostAndApproveAsync(c.Http, c.ClientId, 1, date, cash, stock, 1000m);      // stock issued (financing)
        await PostAndApproveAsync(c.Http, c.ClientId, 2, date, cash, loan, 400m);        // borrowed (financing)
        await PostAndApproveAsync(c.Http, c.ClientId, 3, date, equipment, cash, 500m);   // bought equipment (investing)
        await PostAndApproveAsync(c.Http, c.ClientId, 4, date, ar, sales, 1000m);        // credit sale
        await PostAndApproveAsync(c.Http, c.ClientId, 5, date, cash, ar, 700m);          // collected
        await PostAndApproveAsync(c.Http, c.ClientId, 6, date, wages, cash, 300m);       // paid wages

        CashFlowStatementResponse cf = (await c.Http.GetFromJsonAsync<CashFlowStatementResponse>(
            $"/clients/{c.ClientId}/statements/cash-flow?from=2026-01-01&to=2026-12-31"))!;

        Assert.Equal(700m, cf.NetIncome);          // 1000 sales − 300 wages
        Assert.Equal(400m, cf.OperatingCash);      // 700 − 300 increase in A/R
        Assert.Equal(-500m, cf.Investing.Total);
        Assert.Equal(1400m, cf.Financing.Total);   // 400 loan + 1000 stock
        Assert.Equal(1300m, cf.NetChangeInCash);
        Assert.Equal(0m, cf.BeginningCash);
        Assert.Equal(1300m, cf.EndingCash);
        Assert.True(cf.TiesOut);

        // The statement's ending cash is the same cash the trial balance reports — the statements articulate.
        TrialBalanceResponse tb = (await c.Http.GetFromJsonAsync<TrialBalanceResponse>(
            $"/clients/{c.ClientId}/trial-balance"))!;
        Assert.Equal(cf.EndingCash, tb.Accounts.Single(a => a.AccountId == cash).Balance);
    }

    [Fact]
    public async Task A_year_end_close_inside_the_window_does_not_distort_the_period()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset", cashFlow: "Cash");
        Guid stock = await CreateAccountAsync(c.Http, c.ClientId, "3000", "Common Stock", "Equity");
        await CreateAccountAsync(c.Http, c.ClientId, "3900", "Retained Earnings", "Equity", retained: true);
        Guid sales = await CreateAccountAsync(c.Http, c.ClientId, "4000", "Sales", "Revenue");
        Guid wages = await CreateAccountAsync(c.Http, c.ClientId, "5000", "Wages", "Expense");

        DateOnly date = new(2026, 6, 30);
        await PostAndApproveAsync(c.Http, c.ClientId, 1, date, cash, stock, 1000m);
        await PostAndApproveAsync(c.Http, c.ClientId, 2, date, cash, sales, 500m);
        await PostAndApproveAsync(c.Http, c.ClientId, 3, date, wages, cash, 200m);

        // Close the fiscal year: posts a Closing entry dated 2026-12-31 that resets the temporaries into RE.
        (await c.Http.PostAsJsonAsync($"/clients/{c.ClientId}/periods/close-year",
            new CloseYearRequest(new DateOnly(2026, 12, 31), 100))).EnsureSuccessStatusCode();

        // The full-year window contains the close, but the closing entry is excluded, so the period's real
        // revenue, expense, and net income are intact — not zeroed by the reset.
        IncomeStatementResponse income = (await c.Http.GetFromJsonAsync<IncomeStatementResponse>(
            $"/clients/{c.ClientId}/statements/income-statement?from=2026-01-01&to=2026-12-31"))!;
        Assert.Equal(500m, income.Revenue.Total);
        Assert.Equal(200m, income.Expenses.Total);
        Assert.Equal(300m, income.NetIncome);

        CashFlowStatementResponse cf = (await c.Http.GetFromJsonAsync<CashFlowStatementResponse>(
            $"/clients/{c.ClientId}/statements/cash-flow?from=2026-01-01&to=2026-12-31"))!;
        Assert.Equal(300m, cf.NetIncome);
        Assert.Equal(1300m, cf.EndingCash); // 1000 stock + 500 collected − 200 wages
        Assert.True(cf.TiesOut);
    }

    [Fact]
    public async Task Cash_flow_requires_a_date_range()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage missing = await c.Http.GetAsync($"/clients/{c.ClientId}/statements/cash-flow");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
    }
}
