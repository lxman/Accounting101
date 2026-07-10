using System.Net;
using System.Net.Http.Json;
using Accounting101.Banking.Cash.Api;
using Accounting101.Ledger.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Accounting101.Banking.Cash.Tests;

/// <summary>A ledger refusal on a disposition path is relayed with the engine's real status (a 4xx),
/// not an opaque 500. Exercised via voiding a cash disbursement whose entry now falls in a closed
/// period: the module-owned-entry guard authorizes the module's own reverse call via its module
/// credential regardless of the caller's raw GL permissions, so the period freeze is the refusal that
/// survives to prove the relay — the engine still refuses (409), and the module must relay it, not
/// swallow it into 500.</summary>
public sealed class LedgerErrorRelayE2eTests(CashHostFixture fixture) : IClassFixture<CashHostFixture>
{
    private async Task SetUpCashChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,            "1000", "Cash",             "Asset",   null);
        await PutAccountAsync(controller, clientId, fixture.InterestExpenseAccountId, "6200", "Interest Expense", "Expense", null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Voiding_a_disbursement_dated_into_a_closed_period_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpCashChartAsync(controller, clientId);

        var disbursementDate = new DateOnly(2026, 3, 31);
        RecordCashDisbursementRequest request = new(
            Lines: [new CashLineRequest(fixture.InterestExpenseAccountId, 500m)],
            Date: disbursementDate,
            Reference: "LOAN-2026-03",
            Memo: "Interest payment");

        CashDisbursement disbursement = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/cash-disbursements", request))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CashDisbursement>())!;

        // Approve the spawned entry so it is Posted — a subsequent void must reverse it, not withdraw it.
        EntryResponse entry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={disbursement.Id}"))!);
        (await approver.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();

        // Close the period the disbursement's entry is dated in. The Controller holds both cash.write and
        // gl.void under SoD, so the module's own reverse call (module-credentialed) is authorized
        // regardless of raw GL permissions — but the engine still refuses to mutate an entry whose
        // effective date is now frozen.
        (await controller.PostAsJsonAsync($"/clients/{clientId}/periods/close", new { asOf = disbursementDate }))
            .EnsureSuccessStatusCode();

        HttpResponseMessage resp = await controller.PostAsJsonAsync(
            $"/clients/{clientId}/cash-disbursements/{disbursement.Id}/void", new VoidReasonRequest("oops"));

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.InRange((int)resp.StatusCode, 400, 499);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Detail));
    }
}
