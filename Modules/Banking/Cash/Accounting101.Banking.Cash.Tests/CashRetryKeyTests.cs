using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Cash.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>The retry-key contract on POST /cash-disbursements and /cash-deposits: absent key behaves as
/// before, a replayed key returns the stored document without creating a second one, and a key reused for
/// different financial content is a 422 relayed from the ledger.</summary>
public sealed class CashRetryKeyTests(CashHostFixture fixture) : IClassFixture<CashHostFixture>
{
    private async Task SetUpCashChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,            "1000", "Cash",             "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.InterestExpenseAccountId, "6200", "Interest Expense", "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.LoanPayableAccountId,     "2500", "Loan Payable",     "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.MembersCapitalAccountId,  "3000", "Members' Capital", "Equity",    null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    // ── Cash Disbursements ───────────────────────────────────────────────────

    [Fact]
    public async Task Disbursement_without_a_key_the_post_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpCashChartAsync(controller, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements",
            new RecordCashDisbursementRequest(
                [new CashLineRequest(fixture.InterestExpenseAccountId, 500m)],
                new DateOnly(2026, 6, 30), "LOAN-2026-06", "Loan payment"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Disbursement_a_replayed_key_returns_the_stored_document_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpCashChartAsync(controller, clientId);

        Guid key = Guid.NewGuid();
        RecordCashDisbursementRequest req = new(
            [new CashLineRequest(fixture.InterestExpenseAccountId, 500m)],
            new DateOnly(2026, 6, 30), "LOAN-2026-06", "Loan payment", key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        CashDisbursement a = (await first.Content.ReadFromJsonAsync<CashDisbursement>())!;
        CashDisbursement b = (await second.Content.ReadFromJsonAsync<CashDisbursement>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        PagedResponse<CashDisbursementView> all = (await clerk.GetFromJsonAsync<PagedResponse<CashDisbursementView>>(
            $"/clients/{clientId}/cash-disbursements"))!;
        Assert.Single(all.Items);
    }

    [Fact]
    public async Task Disbursement_the_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpCashChartAsync(controller, clientId);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements",
            new RecordCashDisbursementRequest(
                [new CashLineRequest(fixture.InterestExpenseAccountId, 500m)],
                new DateOnly(2026, 6, 30), "LOAN-2026-06", "Loan payment", key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-disbursements",
            new RecordCashDisbursementRequest(
                [new CashLineRequest(fixture.InterestExpenseAccountId, 600m)],   // divergence: different line amount
                new DateOnly(2026, 6, 30), "LOAN-2026-06", "Loan payment", key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }

    // ── Cash Deposits ────────────────────────────────────────────────────────

    [Fact]
    public async Task Deposit_without_a_key_the_post_creates_as_before()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpCashChartAsync(controller, clientId);

        HttpResponseMessage resp = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits",
            new RecordCashDepositRequest(
                [new CashLineRequest(fixture.MembersCapitalAccountId, 25_000m)],
                new DateOnly(2026, 1, 2), "CONTRIB-2026", "Owner contribution"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Deposit_a_replayed_key_returns_the_stored_document_and_creates_no_second_one()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpCashChartAsync(controller, clientId);

        Guid key = Guid.NewGuid();
        RecordCashDepositRequest req = new(
            [new CashLineRequest(fixture.MembersCapitalAccountId, 25_000m)],
            new DateOnly(2026, 1, 2), "CONTRIB-2026", "Owner contribution", key);

        HttpResponseMessage first = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits", req);
        HttpResponseMessage second = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits", req);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);   // replay, not a create

        CashDeposit a = (await first.Content.ReadFromJsonAsync<CashDeposit>())!;
        CashDeposit b = (await second.Content.ReadFromJsonAsync<CashDeposit>())!;
        Assert.Equal(key, a.Id);
        Assert.Equal(a.Id, b.Id);

        PagedResponse<CashDepositView> all = (await clerk.GetFromJsonAsync<PagedResponse<CashDepositView>>(
            $"/clients/{clientId}/cash-deposits"))!;
        Assert.Single(all.Items);
    }

    [Fact]
    public async Task Deposit_the_same_key_with_different_content_is_a_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpCashChartAsync(controller, clientId);

        Guid key = Guid.NewGuid();
        await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits",
            new RecordCashDepositRequest(
                [new CashLineRequest(fixture.MembersCapitalAccountId, 25_000m)],
                new DateOnly(2026, 1, 2), "CONTRIB-2026", "Owner contribution", key));

        HttpResponseMessage divergent = await clerk.PostAsJsonAsync($"/clients/{clientId}/cash-deposits",
            new RecordCashDepositRequest(
                [new CashLineRequest(fixture.MembersCapitalAccountId, 30_000m)],   // divergence: different line amount
                new DateOnly(2026, 1, 2), "CONTRIB-2026", "Owner contribution", key));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divergent.StatusCode);
        ProblemDetails? problem = await divergent.Content.ReadFromJsonAsync<ProblemDetails>();

        // Pinned contract: the Angular helper in core/api/problem-details.ts matches on this exact text.
        Assert.Equal("An entry with this id already exists with different content.", problem!.Detail);
    }
}
