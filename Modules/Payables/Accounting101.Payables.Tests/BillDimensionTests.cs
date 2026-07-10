using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Proves the bill-enter recipe tags the A/P line with BOTH the Vendor and Bill dimensions. A/P now
/// requires both (flipped in Task 5), so the tag is load-bearing — omitting it would 422.
/// </summary>
public sealed class BillDimensionTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    /// <summary>Chart setup requires ManageAccounts — only the Controller holds that permission.</summary>
    private async Task SetUpChartAsync(HttpClient controllerHttp, Guid clientId)
    {
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.PayableAccountId}",
            new AccountRequest { Number = "2000", Name = "Accounts Payable", Type = "Liability", RequiredDimensions = ["Vendor", "Bill"] }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.RentExpenseAccountId}",
            new AccountRequest { Number = "5200", Name = "Rent Expense", Type = "Expense" }))
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
    public async Task Entered_bill_AP_line_carries_Vendor_and_Bill_dimensions()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Vendor vendor = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest("PropCo", null)))
            .Content.ReadFromJsonAsync<Vendor>())!;

        DraftBillRequest draftRequest = new(
            vendor.Id,
            BillDate: new DateOnly(2026, 3, 1),
            DueDate: null,
            VendorReference: null,
            Memo: null,
            Lines: [new BillLineBody("March Rent", 6000m, fixture.RentExpenseAccountId)]);
        Bill draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", draftRequest))
            .Content.ReadFromJsonAsync<Bill>())!;

        HttpResponseMessage enterResponse = await clerk.PostAsync($"/clients/{clientId}/bills/{draft.Id}/enter", null);
        enterResponse.EnsureSuccessStatusCode();
        Bill entered = (await enterResponse.Content.ReadFromJsonAsync<Bill>())!;

        // The A/P entry lands PendingApproval under SoD — approve before asserting Posted.
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered.Id);

        EntryResponse postedEntry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={entered.Id}"))!);
        Assert.Equal("Posted", postedEntry.Posting);

        EntryLineResponse ap = postedEntry.Lines.Single(l => l.AccountId == fixture.PayableAccountId);
        Assert.Equal(vendor.Id, ap.Dimensions["Vendor"]);
        Assert.Equal(entered.Id, ap.Dimensions["Bill"]);

        // The Bill-axis fold. A/P is credit-normal, so the debit-positive fold reads the payable's balance
        // as negative the bill total. A/P now requires {Vendor, Bill} (Task 5), so the stronger
        // /subledger/reconciliation assertion (gated on the account's RequiredDimensions) is available too;
        // assert both the bare fold and the reconciliation tie-out.
        SubledgerResponse fold = (await clerk.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?dimension=Bill"))!;
        SubledgerLineResponse apFold = fold.Lines.Single(
            l => l.AccountId == fixture.PayableAccountId && l.DimensionValue == entered.Id);
        Assert.Equal(-entered.Total, apFold.Balance);

        SubledgerReconciliationResponse recon = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.PayableAccountId}&dimension=Bill"))!;
        Assert.True(recon.TiesOut);
    }
}
