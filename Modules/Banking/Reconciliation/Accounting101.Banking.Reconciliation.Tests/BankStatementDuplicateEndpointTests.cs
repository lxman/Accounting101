using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Reconciliation.Api;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>The duplicate lookup over HTTP. Both query parameters are required, and the result is scoped
/// to one cash account and one date.</summary>
public sealed class BankStatementDuplicateEndpointTests(ReconciliationHostFixture fixture)
    : IClassFixture<ReconciliationHostFixture>
{
    private static readonly DateOnly StmtDate = new(2026, 1, 31);

    private RecordBankStatementRequest ValidStatementRequest() =>
        new(fixture.CashAccountId, StmtDate, 0m, 60m,
            [
                new BankStatementLineRequest(new DateOnly(2026, 1, 20), 100m, "deposit", null),
                new BankStatementLineRequest(new DateOnly(2026, 1, 20), -40m, "payment", null),
            ]);

    [Fact]
    public async Task Without_a_cashAccountId_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?statementDate=2026-01-31");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Without_a_statementDate_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?cashAccountId={fixture.CashAccountId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task With_an_unparseable_statementDate_returns_400()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?cashAccountId={fixture.CashAccountId}&statementDate=not-a-date");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("problem+json", response.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task With_no_matching_statements_returns_an_empty_array()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?cashAccountId={Guid.NewGuid()}&statementDate=2026-01-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<BankStatement>? matches = await response.Content.ReadFromJsonAsync<List<BankStatement>>();
        Assert.NotNull(matches);
        Assert.Empty(matches);
    }

    /// <summary>The assertion the endpoint exists for: nothing else in this file records a statement, so a
    /// transposed argument or a mis-bound query key would leave every other test here green while every
    /// import silently reported "no duplicates found".</summary>
    [Fact]
    public async Task With_a_matching_statement_returns_it()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync(LedgerRole.Clerk);
        BankStatement recorded = (await (await http.PostAsJsonAsync(
            $"/clients/{clientId}/bank-statements", ValidStatementRequest()))
            .Content.ReadFromJsonAsync<BankStatement>())!;

        HttpResponseMessage response = await http.GetAsync(
            $"/clients/{clientId}/bank-statements/duplicates?cashAccountId={fixture.CashAccountId}&statementDate=2026-01-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<BankStatement>? matches = await response.Content.ReadFromJsonAsync<List<BankStatement>>();
        Assert.NotNull(matches);
        BankStatement match = Assert.Single(matches);
        Assert.Equal(recorded.Id, match.Id);
        Assert.Equal(fixture.CashAccountId, match.CashAccountId);
        Assert.Equal(StmtDate, match.StatementDate);
    }
}
