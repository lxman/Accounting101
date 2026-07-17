using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>The chart-driven aggregating reconciliations endpoint: every dimensioned control account tied
/// out per dimension, drift surfaced as a variance, non-control accounts absent, and audit.read gating.</summary>
public sealed class SubledgerReconciliationsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<Guid> PutAccountAsync(HttpClient http, Guid clientId, AccountRequest request)
    {
        Guid id = Guid.NewGuid();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{id}", request)).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task PostAsync(HttpClient http, Guid client, PostEntryRequest entry)
    {
        PostEntryResponse created = (await (await http.PostAsJsonAsync($"/clients/{client}/entries", entry))
            .Content.ReadFromJsonAsync<PostEntryResponse>())!;
        (await http.PostAsync($"/clients/{client}/entries/{created.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    private static PostEntryRequest ArSale(Guid ar, Guid revenue, decimal amount, Dictionary<string, Guid>? dims) =>
        new(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(ar, "Debit", amount, Dimensions: dims),
             new PostLineRequest(revenue, "Credit", amount)]);

    [Fact]
    public async Task Lists_a_tying_control_account_per_dimension()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid ar = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" });
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });
        Guid custA = Guid.NewGuid(), custB = Guid.NewGuid();
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 100m, new() { ["Customer"] = custA }));
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 60m, new() { ["Customer"] = custB }));

        SubledgerReconciliationsResponse resp = (await c.Http.GetFromJsonAsync<SubledgerReconciliationsResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliations"))!;

        SubledgerReconciliationLine line = Assert.Single(resp.Lines, l => l.Account == ar);
        Assert.Equal("Customer", line.Dimension);
        Assert.Equal("1200", line.Number);
        Assert.Equal("Accounts Receivable", line.Name);
        Assert.Equal(160m, line.ControlBalance);
        Assert.Equal(160m, line.SubledgerTotal);
        Assert.Equal(0m, line.Variance);
        Assert.True(line.TiesOut);
    }

    [Fact]
    public async Task Surfaces_a_variance_from_untagged_drift()
    {
        SeededClient c = await fixture.SeedClientAsync();
        // AR starts as a PLAIN account so an untagged line can be posted, then gains the Customer dimension.
        Guid ar = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset" });
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 25m, dims: null)); // untagged — allowed while plain

        // Retroactively make AR a control account requiring Customer (no guard blocks this).
        (await c.Http.PutAsJsonAsync($"/clients/{c.ClientId}/accounts/{ar}",
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" }))
            .EnsureSuccessStatusCode();
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 100m, new() { ["Customer"] = Guid.NewGuid() })); // tagged

        SubledgerReconciliationsResponse resp = (await c.Http.GetFromJsonAsync<SubledgerReconciliationsResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliations"))!;

        SubledgerReconciliationLine line = Assert.Single(resp.Lines, l => l.Account == ar);
        Assert.Equal(125m, line.ControlBalance);   // 25 untagged + 100 tagged
        Assert.Equal(100m, line.SubledgerTotal);   // only the tagged line carries Customer
        Assert.Equal(25m, line.Variance);          // the untagged remainder
        Assert.False(line.TiesOut);
    }

    [Fact]
    public async Task A_two_dimension_control_account_yields_a_line_per_dimension()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid ar = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimensions = ["Customer", "Invoice"] });
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });
        await PostAsync(c.Http, c.ClientId, ArSale(ar, revenue, 80m,
            new() { ["Customer"] = Guid.NewGuid(), ["Invoice"] = Guid.NewGuid() }));

        SubledgerReconciliationsResponse resp = (await c.Http.GetFromJsonAsync<SubledgerReconciliationsResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliations"))!;

        List<SubledgerReconciliationLine> arLines = resp.Lines.Where(l => l.Account == ar).ToList();
        Assert.Equal(2, arLines.Count);
        Assert.Contains(arLines, l => l.Dimension == "Customer" && l.TiesOut && l.ControlBalance == 80m);
        Assert.Contains(arLines, l => l.Dimension == "Invoice" && l.TiesOut && l.ControlBalance == 80m);
    }

    [Fact]
    public async Task Non_control_accounts_are_absent()
    {
        SeededClient c = await fixture.SeedClientAsync();
        Guid cash = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" });
        Guid revenue = await PutAccountAsync(c.Http, c.ClientId,
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" });
        await PostAsync(c.Http, c.ClientId, new PostEntryRequest(null, new DateOnly(2026, 3, 31), null, null,
            [new PostLineRequest(cash, "Debit", 50m), new PostLineRequest(revenue, "Credit", 50m)]));

        SubledgerReconciliationsResponse resp = (await c.Http.GetFromJsonAsync<SubledgerReconciliationsResponse>(
            $"/clients/{c.ClientId}/subledger/reconciliations"))!;

        Assert.DoesNotContain(resp.Lines, l => l.Account == cash);
        Assert.Empty(resp.Lines); // no dimensioned control accounts at all → empty
    }

    [Fact]
    public async Task Requires_audit_read()
    {
        SeededClient c = await fixture.SeedClientAsync();   // Controller: holds audit.read
        HttpClient arClerk = await fixture.AddMemberAsync(c.ClientId, LedgerRole.ArClerk, "AR Clerk"); // gl.read, no audit.read

        Assert.Equal(HttpStatusCode.Forbidden,
            (await arClerk.GetAsync($"/clients/{c.ClientId}/subledger/reconciliations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await c.Http.GetAsync($"/clients/{c.ClientId}/subledger/reconciliations")).StatusCode);
    }
}
