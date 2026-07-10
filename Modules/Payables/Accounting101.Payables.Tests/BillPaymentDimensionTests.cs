using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Proves the bill-payment recipe tags EACH A/P line with the Bill it relieves — a single split payment
/// against two bills produces two `Dr A/P` lines, one per allocation, rather than one aggregate line. A/P
/// still requires only Vendor at this stage (flipped in a later task), so the Bill tag is additive here;
/// only the bare fold is asserted for the Bill axis.
/// </summary>
public sealed class BillPaymentDimensionTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
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
    public async Task Split_payment_produces_one_Bill_dimensioned_AP_line_per_allocation()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Vendor vendor = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest("PropCo", null)))
            .Content.ReadFromJsonAsync<Vendor>())!;

        // Two bills, $100 each, for the same vendor.
        Bill billA = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "March Rent A");
        Bill billB = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "March Rent B");

        // One payment of $150, split $100 to bill A and $50 to bill B.
        RecordBillPaymentRequest paymentRequest = new(
            vendor.Id,
            Date: new DateOnly(2026, 3, 31),
            Amount: 150m,
            Method: "check",
            Allocations: [new Allocation(billA.Id, 100m), new Allocation(billB.Id, 50m)]);

        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", paymentRequest))
            .Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        EntryResponse postedEntry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={payment.Id}"))!);
        Assert.Equal("Posted", postedEntry.Posting);

        List<EntryLineResponse> apLines = postedEntry.Lines
            .Where(l => l.AccountId == fixture.PayableAccountId && l.Direction == "Debit")
            .ToList();
        Assert.Equal(2, apLines.Count);

        EntryLineResponse lineA = apLines.Single(l => l.Dimensions["Bill"] == billA.Id);
        Assert.Equal(100m, lineA.Amount);
        Assert.Equal(vendor.Id, lineA.Dimensions["Vendor"]);

        EntryLineResponse lineB = apLines.Single(l => l.Dimensions["Bill"] == billB.Id);
        Assert.Equal(50m, lineB.Amount);
        Assert.Equal(vendor.Id, lineB.Dimensions["Vendor"]);

        // The Bill-axis fold via the UNGATED bare /subledger endpoint (not the reconciliation endpoint,
        // which 422s until A/P requires Bill in a later task). A/P is credit-normal, so the debit-positive
        // fold reads a payable's balance as negative its open amount; open = -fold.
        SubledgerResponse fold = (await clerk.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?dimension=Bill"))!;

        SubledgerLineResponse foldA = fold.Lines.Single(
            l => l.AccountId == fixture.PayableAccountId && l.DimensionValue == billA.Id);
        Assert.Equal(0m, foldA.Balance);
        Assert.Equal(0m, -foldA.Balance); // open = -fold: bill A is fully paid

        SubledgerLineResponse foldB = fold.Lines.Single(
            l => l.AccountId == fixture.PayableAccountId && l.DimensionValue == billB.Id);
        Assert.Equal(-50m, foldB.Balance);
        Assert.Equal(50m, -foldB.Balance); // open = -fold: bill B has $50 open
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
