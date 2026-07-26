using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Reconciliation.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>The retry-key contract on POST /reconciliations/{id}/adjustments: absent key behaves as before,
/// a replayed key returns the stored adjustment without creating a second one, a key reused for different
/// financial content is a 422 relayed from the ledger — and, pinning the route's naming trap (the route's
/// {id} is the reconciliation's, the body's Id is the adjustment's own, and a mix-up would key every
/// adjustment by its reconciliation), a distinct fresh key posted under two different reconciliations is
/// two independent creates, never a collision.</summary>
public sealed class AdjustmentRetryKeyTests(ReconciliationHostFixture fixture) : IClassFixture<ReconciliationHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset");
        await PutAccountAsync(controller, clientId, fixture.InterestExpenseAccountId, "5000", "Interest Expense", "Expense");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId, string number, string name, string type)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type }))
            .EnsureSuccessStatusCode();
    }

    /// <summary>Records a one-line bank statement that foots at zero and starts a reconciliation against it.</summary>
    private async Task<Reconciliation> StartReconciliationAsync(HttpClient clerk, Guid clientId, DateOnly statementDate)
    {
        BankStatement statement = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
                new RecordBankStatementRequest(fixture.CashAccountId, statementDate, 0m, 0m,
                    [new BankStatementLineRequest(statementDate, 0m, "no activity", null)])))
            .Content.ReadFromJsonAsync<BankStatement>())!;
        return (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations",
                new StartReconciliationRequest(statement.Id)))
            .Content.ReadFromJsonAsync<Reconciliation>())!;
    }

    [Fact]
    public async Task Without_a_key_the_post_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Reconciliation rec = await StartReconciliationAsync(clerk, clientId, new DateOnly(2026, 6, 30));

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments",
            new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 25m, AdjustmentKind.Charge, null, "bank fee"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task A_replayed_key_returns_the_stored_adjustment_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Reconciliation rec = await StartReconciliationAsync(clerk, clientId, new DateOnly(2026, 6, 30));

        Guid key = Guid.NewGuid();
        RecordAdjustmentRequest req = new(fixture.InterestExpenseAccountId, 25m, AdjustmentKind.Charge, null, "bank fee", key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        BankAdjustment a = (await first.Content.ReadFromJsonAsync<BankAdjustment>())!;
        BankAdjustment b = (await second.Content.ReadFromJsonAsync<BankAdjustment>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        PagedResponse<BankAdjustment> all = (await clerk.GetFromJsonAsync<PagedResponse<BankAdjustment>>(
            $"/clients/{clientId}/reconciliations/{rec.Id}/adjustments"))!;
        Assert.Single(all.Items);
    }

    [Fact]
    public async Task The_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Reconciliation rec = await StartReconciliationAsync(clerk, clientId, new DateOnly(2026, 6, 30));

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments",
            new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 25m, AdjustmentKind.Charge, null, "bank fee", key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{rec.Id}/adjustments",
            new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 30m, AdjustmentKind.Charge, null, "bank fee", key));   // divergence: different amount

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }

    [Fact]
    public async Task A_distinct_fresh_key_under_two_different_reconciliations_is_two_independent_creates()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Reconciliation recA = await StartReconciliationAsync(clerk, clientId, new DateOnly(2026, 6, 30));
        Reconciliation recB = await StartReconciliationAsync(clerk, clientId, new DateOnly(2026, 7, 31));

        Guid keyA = Guid.NewGuid();
        Guid keyB = Guid.NewGuid();

        HttpResponseMessage respA = await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{recA.Id}/adjustments",
            new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 25m, AdjustmentKind.Charge, null, "bank fee", keyA));
        HttpResponseMessage respB = await clerk.PostAsJsonAsync($"/clients/{clientId}/reconciliations/{recB.Id}/adjustments",
            new RecordAdjustmentRequest(fixture.InterestExpenseAccountId, 25m, AdjustmentKind.Charge, null, "bank fee", keyB));

        Assert.Equal(HttpStatusCode.Created, respA.StatusCode);   // NOT a replay of the first — proves reconciliationId never leaked into the doc id
        Assert.Equal(HttpStatusCode.Created, respB.StatusCode);

        BankAdjustment a = (await respA.Content.ReadFromJsonAsync<BankAdjustment>())!;
        BankAdjustment b = (await respB.Content.ReadFromJsonAsync<BankAdjustment>())!;
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(keyA, a.Id);   // the adjustment's own id is the supplied key, not either reconciliation's id
        Assert.Equal(keyB, b.Id);
    }
}
