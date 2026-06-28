using System.Net.Http.Json;
using Accounting101.Payables.Api;
using Accounting101.Settlement;
using Accounting101.Ledger.Contracts;
using static Accounting101.Payables.Tests.BillSettlementScenario;

namespace Accounting101.Payables.Tests;

/// <summary>A messy bill-settlement sequence (partial pay, overpay-to-credit, apply credit, void) keeps the
/// books balanced and both subledgers tying out at every approved step, with exact derived open balances.</summary>
public sealed class BillSettlementIntegrityE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private static readonly DateOnly AsOf = new(2026, 3, 31);

    [Fact]
    public async Task Partial_pay_overpay_apply_credit_then_void_stay_consistent()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId, fixture);
        Guid vendor = await CreateVendorAsync(clerk, clientId);
        Guid bill1 = await EnterBillAsync(clerk, approver, clientId, vendor, 1000m, fixture.RentExpenseAccountId);
        Guid bill2 = await EnterBillAsync(clerk, approver, clientId, vendor, 500m, fixture.RentExpenseAccountId);
        await AssertConsistentAsync(clerk, clientId, bill1, 1000m, bill2, 500m);

        // Partial pay bill1 600 → open 400.
        BillPayment pay1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 5), 600m, "check", [new Allocation(bill1, 600m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay1.Id);
        await AssertConsistentAsync(clerk, clientId, bill1, 400m, bill2, 500m);

        // Overpay bill2: pay 600 allocate 500 → bill2 open 0, vendor credit 100.
        BillPayment pay2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor, new DateOnly(2026, 3, 6), 600m, "check", [new Allocation(bill2, 500m)])))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay2.Id);
        await AssertConsistentAsync(clerk, clientId, bill1, 400m, bill2, 0m);

        // Apply the 100 vendor credit to bill1 → bill1 open 300.
        VendorCreditApplication app = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
                new VendorCreditApplicationRequest(vendor, new DateOnly(2026, 3, 7), [new Allocation(bill1, 100m)])))
            .Content.ReadFromJsonAsync<VendorCreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, app.Id);
        await AssertConsistentAsync(clerk, clientId, bill1, 300m, bill2, 0m);

        // Void pay1 (a plain payment with NO credit entanglement). Voiding an approved (posted) payment
        // reverses a posted GL entry — an Approver action; the reversal lands PendingApproval and, under SoD,
        // is approved by a distinct actor (the Controller). bill1 loses pay1's 600 but keeps the applied 100
        // credit → open 900; bill2 stays 0; vendor credit stays 0.
        // (We void pay1, not pay2: pay2's overpayment credit is already applied to bill1, so voiding pay2
        // would drive the vendor-credit balance negative — an incoherent state worth probing separately, but
        // not what this integrity sweep should assert.)
        (await approver.PostAsJsonAsync($"/clients/{clientId}/bill-payments/{pay1.Id}/void",
            new VoidReasonRequest("reversed"))).EnsureSuccessStatusCode();
        await ApproveBySourceRefAsync(controller, controller, clientId, pay1.Id);
        await AssertConsistentAsync(clerk, clientId, bill1, 900m, bill2, 0m);

        // Pin contra-account routing at the terminal state: rent expense reflects both bills (1500) and was
        // untouched by the cash-side churn; the vendor-credit lifecycle (created 100 -> applied 100) nets to 0.
        IncomeStatementResponse income = (await clerk.GetFromJsonAsync<IncomeStatementResponse>(
            $"/clients/{clientId}/statements/income-statement?from=2026-01-01&to=2026-03-31"))!;
        Assert.Equal(1500m, income.Expenses.Total);

        decimal vendorCredit = (await clerk.GetFromJsonAsync<VendorCreditBalanceProbe>(
            $"/clients/{clientId}/vendors/{vendor}/credit-balance"))!.CreditBalance;
        Assert.Equal(0m, vendorCredit);
    }

    private async Task AssertConsistentAsync(
        HttpClient http, Guid clientId, Guid bill1, decimal open1, Guid bill2, decimal open2)
    {
        BillView v1 = (await http.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{bill1}"))!;
        Assert.Equal(open1, v1.OpenBalance);
        BillView v2 = (await http.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{bill2}"))!;
        Assert.Equal(open2, v2.OpenBalance);

        await AssertBalancedAsync(http, clientId, AsOf);

        SubledgerReconciliationResponse ap = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.PayableAccountId}&dimension=Vendor"))!;
        Assert.True(ap.TiesOut, $"A/P subledger did not tie out (variance {ap.Variance})");

        SubledgerReconciliationResponse vc = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.VendorCreditsAccountId}&dimension=Vendor"))!;
        Assert.True(vc.TiesOut, $"Vendor-credits subledger did not tie out (variance {vc.Variance})");
    }

    private sealed record VendorCreditBalanceProbe(Guid VendorId, decimal CreditBalance);
}
