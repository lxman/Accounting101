using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using Microsoft.AspNetCore.Mvc;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>A ledger refusal on a disposition path is relayed with the engine's real status (a 4xx),
/// not an opaque 500. Exercised via a Clerk attempting to void a posted payment — reversing a posted
/// entry requires the Approver role, so the engine refuses (403); the module must relay it.</summary>
public sealed class LedgerErrorRelayE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    [Fact]
    public async Task Clerk_voiding_a_posted_payment_relays_the_ledger_status_not_500()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 100m);

        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 31), 100m, "check", [new Allocation(invoice, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // The clerk lacks Reverse permission; voiding a *posted* payment reverses the entry → engine refuses.
        HttpResponseMessage resp = await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/payments/{pay.Id}/void", new VoidInvoiceRequest("oops"));

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.InRange((int)resp.StatusCode, 400, 499);
        ProblemDetails? problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Detail));
    }
}
