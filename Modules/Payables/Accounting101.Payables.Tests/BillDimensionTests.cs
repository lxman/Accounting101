using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Proves the bill-enter recipe tags the A/P line with BOTH the Vendor and Bill dimensions. A/P still
/// requires only Vendor at this stage (flipped in a later task), so the Bill tag is additive here — the
/// reconciliation endpoint would 422 on a dimension the account doesn't require; only the bare fold is
/// asserted for the Bill axis.
/// </summary>
public sealed class BillDimensionTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    /// <summary>Chart setup requires ManageAccounts — only the Controller holds that permission.</summary>
    private async Task SetUpChartAsync(HttpClient controllerHttp, Guid clientId)
    {
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.PayableAccountId}",
            new AccountRequest { Number = "2000", Name = "Accounts Payable", Type = "Liability", RequiredDimension = "Vendor" }))
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

        // The Bill-axis fold via the UNGATED bare /subledger endpoint (not the reconciliation endpoint,
        // which 422s until A/P requires Bill in a later task). A/P is credit-normal, so the debit-positive
        // fold reads the payable's balance as negative the bill total.
        SubledgerResponse fold = (await clerk.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?dimension=Bill"))!;
        SubledgerLineResponse apFold = fold.Lines.Single(
            l => l.AccountId == fixture.PayableAccountId && l.DimensionValue == entered.Id);
        Assert.Equal(-entered.Total, apFold.Balance);
    }
}
