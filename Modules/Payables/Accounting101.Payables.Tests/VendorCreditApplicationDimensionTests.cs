using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Proves the vendor-credit-application recipe tags EACH A/P relief line with the Bill it relieves,
/// same as bill payment — one `Dr A/P {Vendor, Bill}` line per allocation, rather than one aggregate
/// line, plus one `Cr Vendor Credits {Vendor}` line for the total applied. A/P still requires only
/// Vendor at this stage (flipped in a later task), so the Bill tag is additive here; only the bare
/// fold is asserted for the Bill axis.
/// </summary>
public sealed class VendorCreditApplicationDimensionTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    /// <summary>Chart setup requires ManageAccounts — only the Controller holds that permission.</summary>
    private async Task SetUpChartAsync(HttpClient controllerHttp, Guid clientId)
    {
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.PayableAccountId}",
            new AccountRequest { Number = "2000", Name = "Accounts Payable", Type = "Liability", RequiredDimension = "Vendor" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.CashAccountId}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.VendorCreditsAccountId}",
            new AccountRequest { Number = "1300", Name = "Vendor Credits", Type = "Asset", RequiredDimension = "Vendor" }))
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
    public async Task Vendor_credit_application_produces_a_Bill_dimensioned_AP_line()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Vendor vendor = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest("PropCo", null)))
            .Content.ReadFromJsonAsync<Vendor>())!;

        // First bill ($100), overpaid $100 with only $40 allocated to it, leaving a $60 vendor credit.
        Bill firstBill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "March Rent");

        RecordBillPaymentRequest paymentRequest = new(
            vendor.Id,
            Date: new DateOnly(2026, 3, 31),
            Amount: 100m,
            Method: "check",
            Allocations: [new Allocation(firstBill.Id, 40m)]);
        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", paymentRequest))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        // Second bill ($100) — the vendor credit is applied against this one.
        Bill secondBill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "April Rent");

        VendorCreditApplicationRequest creditAppRequest = new(
            vendor.Id,
            Date: new DateOnly(2026, 4, 15),
            Allocations: [new Allocation(secondBill.Id, 60m)]);
        VendorCreditApplication creditApp = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendor-credit-applications", creditAppRequest))
            .Content.ReadFromJsonAsync<VendorCreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditApp.Id);

        EntryResponse postedEntry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={creditApp.Id}"))!);
        Assert.Equal("Posted", postedEntry.Posting);

        EntryLineResponse apLine = Assert.Single(postedEntry.Lines,
            l => l.AccountId == fixture.PayableAccountId && l.Direction == "Debit");
        Assert.Equal(60m, apLine.Amount);
        Assert.Equal(vendor.Id, apLine.Dimensions["Vendor"]);
        Assert.Equal(secondBill.Id, apLine.Dimensions["Bill"]);

        EntryLineResponse creditsLine = Assert.Single(postedEntry.Lines,
            l => l.AccountId == fixture.VendorCreditsAccountId && l.Direction == "Credit");
        Assert.Equal(60m, creditsLine.Amount);
        Assert.Equal(vendor.Id, creditsLine.Dimensions["Vendor"]);

        // The Bill-axis fold via the UNGATED bare /subledger endpoint (not the reconciliation endpoint,
        // which 422s until A/P requires Bill in a later task). A/P is credit-normal, so the debit-positive
        // fold reads a payable's balance as negative its open amount; open = -fold.
        SubledgerResponse fold = (await clerk.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?dimension=Bill"))!;

        SubledgerLineResponse foldSecondBill = fold.Lines.Single(
            l => l.AccountId == fixture.PayableAccountId && l.DimensionValue == secondBill.Id);
        Assert.Equal(-40m, foldSecondBill.Balance);
        Assert.Equal(40m, -foldSecondBill.Balance); // open = -fold: second bill has $40 open ($100 - $60 applied)
    }

    private async Task<Bill> EnterBillAsync(HttpClient clerk, HttpClient approver, Guid clientId, Guid vendorId, string description)
    {
        DraftBillRequest draftRequest = new(
            vendorId,
            BillDate: new DateOnly(2026, 3, 1),
            DueDate: null,
            VendorReference: null,
            Memo: null,
            Lines: [new BillLineBody(description, 100m, fixture.RentExpenseAccountId)]);
        Bill draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", draftRequest))
            .Content.ReadFromJsonAsync<Bill>())!;

        Bill entered = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{draft.Id}/enter", null))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered.Id);
        return entered;
    }
}
