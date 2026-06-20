using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Contracts;

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
        PostEntryRequest entry = new(
            null, seq, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(ar, "Debit", amount, CustomerId: customer),
             new PostLineRequest(revenue, "Credit", amount)]);

        HttpResponseMessage posted = await http.PostAsJsonAsync($"/clients/{client}/entries", entry);
        posted.EnsureSuccessStatusCode();
        PostEntryResponse created = (await posted.Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Subledger_breaks_ar_out_by_customer_and_ties_to_the_control_balance()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid ar = Guid.NewGuid(), revenue = Guid.NewGuid();
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
    public async Task Subledger_requires_a_valid_dimension()
    {
        SeededClient c = await fixture.SeedClientAsync();

        HttpResponseMessage missing = await c.Http.GetAsync($"/clients/{c.ClientId}/subledger");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);

        HttpResponseMessage bogus = await c.Http.GetAsync($"/clients/{c.ClientId}/subledger?dimension=Department");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bogus.StatusCode);
    }
}
