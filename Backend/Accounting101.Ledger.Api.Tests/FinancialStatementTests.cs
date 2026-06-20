using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The financial-statement reads end to end: posted activity flows through the chart classification
/// into a balanced balance sheet and an income statement, the two reconcile on net income, the as-of
/// balance sheet excludes later activity, and an income statement demands a valid date range.
/// </summary>
public sealed class FinancialStatementTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> CreateAccountAsync(
        HttpClient http, Guid client, string number, string name, string type, bool retained = false)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{client}/accounts/{id}",
            new AccountRequest { Number = number, Name = name, Type = type, IsRetainedEarnings = retained })).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task PostAndApproveAsync(
        HttpClient http, Guid client, long seq, DateOnly date, Guid debit, Guid credit, decimal amount)
    {
        _ = seq; // sequence is engine-assigned now; the parameter just keeps call sites readable
        PostEntryRequest entry = new(null, date, null, null,
            [new PostLineRequest(debit, "Debit", amount), new PostLineRequest(credit, "Credit", amount)]);

        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", entry);
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;

        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Balance_sheet_and_income_statement_articulate()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        await CreateAccountAsync(c.Http, c.ClientId, "2000", "Note Payable", "Liability");
        Guid commonStock = await CreateAccountAsync(c.Http, c.ClientId, "3000", "Common Stock", "Equity");
        await CreateAccountAsync(c.Http, c.ClientId, "3900", "Retained Earnings", "Equity", retained: true);
        Guid sales = await CreateAccountAsync(c.Http, c.ClientId, "4000", "Sales", "Revenue");
        Guid rent = await CreateAccountAsync(c.Http, c.ClientId, "5000", "Rent Expense", "Expense");

        DateOnly date = new(2026, 3, 15);
        await PostAndApproveAsync(c.Http, c.ClientId, 1, date, cash, commonStock, 1000m); // owner investment
        await PostAndApproveAsync(c.Http, c.ClientId, 2, date, cash, sales, 400m);        // revenue
        await PostAndApproveAsync(c.Http, c.ClientId, 3, date, rent, cash, 200m);         // expense

        BalanceSheetResponse sheet = (await c.Http.GetFromJsonAsync<BalanceSheetResponse>(
            $"/clients/{c.ClientId}/statements/balance-sheet?asOf=2026-03-31"))!;

        Assert.True(sheet.IsBalanced);
        Assert.Equal(1200m, sheet.TotalAssets);                  // 1000 + 400 − 200
        Assert.Equal(1200m, sheet.TotalLiabilitiesAndEquity);
        Assert.Equal(1200m, sheet.Assets.Lines.Single(l => l.AccountId == cash).Amount);

        StatementLineResponse netIncome = sheet.Equity.Lines.Single(l => l.AccountId is null);
        Assert.Equal("Net income", netIncome.Name);
        Assert.Equal(200m, netIncome.Amount);

        IncomeStatementResponse income = (await c.Http.GetFromJsonAsync<IncomeStatementResponse>(
            $"/clients/{c.ClientId}/statements/income-statement?from=2026-01-01&to=2026-03-31"))!;

        Assert.Equal(400m, income.Revenue.Total);
        Assert.Equal(200m, income.Expenses.Total);
        Assert.Equal(200m, income.NetIncome);
        Assert.Equal(netIncome.Amount, income.NetIncome); // the statements reconcile
    }

    [Fact]
    public async Task Balance_sheet_as_of_excludes_later_activity()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await CreateAccountAsync(c.Http, c.ClientId, "1000", "Cash", "Asset");
        Guid sales = await CreateAccountAsync(c.Http, c.ClientId, "4000", "Sales", "Revenue");

        await PostAndApproveAsync(c.Http, c.ClientId, 1, new DateOnly(2026, 1, 31), cash, sales, 100m);
        await PostAndApproveAsync(c.Http, c.ClientId, 2, new DateOnly(2026, 2, 28), cash, sales, 50m);

        BalanceSheetResponse january = (await c.Http.GetFromJsonAsync<BalanceSheetResponse>(
            $"/clients/{c.ClientId}/statements/balance-sheet?asOf=2026-01-31"))!;
        Assert.Equal(100m, january.Assets.Lines.Single(l => l.AccountId == cash).Amount);
        Assert.True(january.IsBalanced);

        BalanceSheetResponse february = (await c.Http.GetFromJsonAsync<BalanceSheetResponse>(
            $"/clients/{c.ClientId}/statements/balance-sheet?asOf=2026-02-28"))!;
        Assert.Equal(150m, february.Assets.Lines.Single(l => l.AccountId == cash).Amount);
    }

    [Fact]
    public async Task Income_statement_requires_a_date_range()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage missing = await c.Http.GetAsync($"/clients/{c.ClientId}/statements/income-statement");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);

        HttpResponseMessage reversed = await c.Http.GetAsync(
            $"/clients/{c.ClientId}/statements/income-statement?from=2026-03-31&to=2026-01-01");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reversed.StatusCode);
    }
}
