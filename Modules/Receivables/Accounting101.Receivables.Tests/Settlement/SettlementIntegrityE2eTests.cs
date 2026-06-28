using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;
using Accounting101.Ledger.Contracts;
using static Accounting101.Receivables.Tests.SettlementScenario;

namespace Accounting101.Receivables.Tests;

/// <summary>A messy disposition sequence on one invoice keeps the books balanced and both subledgers tying
/// out at every approved step, with exact derived open balances throughout.</summary>
public sealed class SettlementIntegrityE2eTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private static readonly DateOnly AsOf = new(2026, 3, 31);

    [Fact]
    public async Task Partial_pay_credit_note_write_off_then_void_stay_consistent()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 1000m);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 1000m);

        // Partial payment 600 → open 400.
        Payment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 600m, "check", [new Allocation(invoice, 600m)])))
            .Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 400m);

        // Credit note 100 → open 300.
        CreditNote cn = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
                new CreditNoteRequest(customer, new DateOnly(2026, 3, 6), [new Allocation(invoice, 100m)], "adjustment")))
            .Content.ReadFromJsonAsync<CreditNote>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, cn.Id);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 300m);

        // Write off the remaining 300 → open 0.
        WriteOff wo = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
                new WriteOffRequest(customer, new DateOnly(2026, 3, 7), [new Allocation(invoice, 300m)], "uncollectible")))
            .Content.ReadFromJsonAsync<WriteOff>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, wo.Id);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 0m);

        // Void the write-off → open back to 300. Voiding an approved (posted) write-off reverses a posted
        // GL entry — an Approver action. The reversal lands PendingApproval; under SoD the Approver authored
        // it, so a distinct actor (the Controller) approves it (matches ReceivablesVoidTests).
        (await approver.PostAsJsonAsync($"/clients/{clientId}/write-offs/{wo.Id}/void",
            new VoidInvoiceRequest("re-evaluated"))).EnsureSuccessStatusCode();
        await ApproveBySourceRefAsync(controller, controller, clientId, wo.Id);
        await AssertConsistentAsync(clerk, clientId, invoice, expectedOpen: 300m);
    }

    private async Task AssertConsistentAsync(HttpClient http, Guid clientId, Guid invoice, decimal expectedOpen)
    {
        InvoiceView v = (await http.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{invoice}"))!;
        Assert.Equal(expectedOpen, v.OpenBalance);

        await AssertBalancedAsync(http, clientId, AsOf);

        SubledgerReconciliationResponse ar = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(ar.TiesOut, $"A/R subledger did not tie out (variance {ar.Variance}) at open {expectedOpen}");

        SubledgerReconciliationResponse credits = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.CustomerCreditsAccountId}&dimension=Customer"))!;
        Assert.True(credits.TiesOut, $"Customer-credits subledger did not tie out (variance {credits.Variance})");
    }
}
