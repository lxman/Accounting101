using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>Proves the payables end-to-end through the real host: a bill is entered and approved, an
/// over-payment produces vendor credit, that credit applies to a later bill, and both subledgers (A/P
/// and Vendor Credits) tie out.</summary>
public sealed class PayablesE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,     "2000", "Accounts Payable",  "Liability", null, ["Vendor", "Bill"]);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,         "1000", "Cash",              "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId,"1300", "Vendor Credits",    "Asset",     "Vendor");
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId,  "5200", "Rent Expense",      "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.UtilitiesExpenseAccountId, "5300", "Utilities Expense", "Expense", null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension, string[]? requiredDimensions = null)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest
            {
                Number = number, Name = name, Type = type,
                RequiredDimension = requiredDimension, RequiredDimensions = requiredDimensions,
            }))
            .EnsureSuccessStatusCode();
    }

    private static async Task ApproveBySourceRefAsync(
        HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Bill_overpayment_vendor_credit_application_and_subledgers_tie_out()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        // Create a vendor.
        Vendor vendor = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest("PropCo", null)))
            .Content.ReadFromJsonAsync<Vendor>())!;

        // Draft a bill with two expense lines: Rent $6000, Utilities $800 = total $6800.
        DraftBillRequest draftRequest = new(
            vendor.Id,
            BillDate: new DateOnly(2026, 3, 1),
            DueDate: new DateOnly(2026, 3, 31),
            VendorReference: "INV-001",
            Memo: null,
            Lines:
            [
                new BillLineBody("March Rent", 6000m, fixture.RentExpenseAccountId),
                new BillLineBody("March Utilities", 800m, fixture.UtilitiesExpenseAccountId),
            ]);

        Bill bill1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", draftRequest))
            .Content.ReadFromJsonAsync<Bill>())!;

        // Enter the bill (posts A/P entry as PendingApproval).
        Bill entered1 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill1.Id}/enter", null))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<Bill>())!;

        // Approve the A/P entry via the separate approver.
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered1.Id);

        // Over-pay the bill: $7000 vs $6800, allocating $6800 to the bill.
        RecordBillPaymentRequest paymentRequest = new(
            vendor.Id,
            Date: new DateOnly(2026, 3, 31),
            Amount: 7000m,
            Method: "check",
            Allocations: [new Allocation(entered1.Id, 6800m)]);

        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", paymentRequest))
            .Content.ReadFromJsonAsync<BillPayment>())!;

        // Approve the payment entry.
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        // Assert bill1 is fully paid with zero open balance.
        BillView v1 = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{entered1.Id}"))!;
        Assert.Equal(SettlementStatus.Paid, v1.SettlementStatus);
        Assert.Equal(0m, v1.OpenBalance);

        // Assert vendor credit balance is $200 (the $200 over-payment).
        var creditBalanceResult = (await clerk.GetFromJsonAsync<VendorCreditBalanceResponse>(
            $"/clients/{clientId}/vendors/{vendor.Id}/credit-balance"))!;
        Assert.Equal(200m, creditBalanceResult.CreditBalance);

        // Draft and enter a second bill (Rent $1000).
        DraftBillRequest draft2 = new(
            vendor.Id,
            BillDate: new DateOnly(2026, 4, 1),
            DueDate: null,
            VendorReference: null,
            Memo: null,
            Lines: [new BillLineBody("April Rent", 1000m, fixture.RentExpenseAccountId)]);

        Bill bill2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", draft2))
            .Content.ReadFromJsonAsync<Bill>())!;
        Bill entered2 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill2.Id}/enter", null))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered2.Id);

        // Apply the $200 vendor credit to bill2.
        VendorCreditApplicationRequest creditAppRequest = new(
            vendor.Id,
            Date: new DateOnly(2026, 4, 15),
            Allocations: [new Allocation(entered2.Id, 200m)]);
        VendorCreditApplication creditApp = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendor-credit-applications", creditAppRequest))
            .Content.ReadFromJsonAsync<VendorCreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditApp.Id);

        // Assert bill2's open balance dropped by $200 ($1000 - $200 = $800).
        BillView v2 = (await clerk.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{entered2.Id}"))!;
        Assert.Equal(800m, v2.OpenBalance);

        // Assert BOTH subledger reconciliations tie out.
        SubledgerReconciliationResponse ap = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.PayableAccountId}&dimension=Vendor"))!;
        Assert.True(ap.TiesOut);

        SubledgerReconciliationResponse vendorCredits = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.VendorCreditsAccountId}&dimension=Vendor"))!;
        Assert.True(vendorCredits.TiesOut);
    }

    // Private helper record to deserialize the anonymous credit-balance response.
    private sealed record VendorCreditBalanceResponse(Guid VendorId, decimal CreditBalance);
}
