using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using Accounting101.Ledger.Contracts;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>An uneven multi-bill payment split leaves every open balance exact with the A/P subledger
/// tying out and the books balanced.</summary>
public sealed class BillRoundingE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Uneven_split_across_bills_leaves_exact_balances_and_subledger_ties_out()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid b1 = await EnterBillAsync(clerk, approver, clientId, vendor, 33.33m, fixture.RentExpenseAccountId);
        Guid b2 = await EnterBillAsync(clerk, approver, clientId, vendor, 33.33m, fixture.RentExpenseAccountId);
        Guid b3 = await EnterBillAsync(clerk, approver, clientId, vendor, 33.34m, fixture.RentExpenseAccountId);

        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 31), 100.00m, "check",
                    [new Allocation(b1, 33.33m), new Allocation(b2, 33.33m), new Allocation(b3, 33.34m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        foreach (Guid id in new[] { b1, b2, b3 })
        {
            BillView v = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{id}"))!;
            Assert.Equal(0m, v.OpenBalance);
            Assert.Equal(SettlementStatus.Paid, v.SettlementStatus);
        }

        SubledgerReconciliationResponse ap = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.PayableAccountId}&dimension=Vendor"))!;
        Assert.True(ap.TiesOut);
        await AssertBalancedAsync(clerk, clientId, new DateOnly(2026, 3, 31));
    }
}
