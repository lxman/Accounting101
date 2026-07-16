using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>Proves GET /clients/{clientId}/vendor-credit-applications?vendorId returns the vendor's
/// recorded credit applications, 400s without vendorId, and is client-isolated.</summary>
public sealed class VendorCreditApplicationListEndpointTests(PayablesHostFixture fixture)
    : IClassFixture<PayablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,        "2000", "Accounts Payable", "Liability", null, ["Vendor", "Bill"]);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,           "1000", "Cash",             "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId,  "1300", "Vendor Credits",   "Asset",     "Vendor");
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId,    "5200", "Rent Expense",     "Expense",   null);
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension, string[]? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest
            {
                Number = number, Name = name, Type = type,
                RequiredDimension = requiredDimension, RequiredDimensions = requiredDimensions,
            }))
            .EnsureSuccessStatusCode();

    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    private async Task<Bill> EnterBillAsync(HttpClient clerk, HttpClient approver, Guid clientId, Guid vendorId, string description)
    {
        DraftBillRequest draftRequest = new(vendorId, BillDate: new DateOnly(2026, 3, 1), DueDate: null,
            VendorReference: null, Memo: null, Lines: [new BillLineBody(description, 100m, fixture.RentExpenseAccountId)]);
        Bill draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", draftRequest))
            .Content.ReadFromJsonAsync<Bill>())!;
        Bill entered = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{draft.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered.Id);
        return entered;
    }

    /// <summary>Over-pays firstBill to leave vendor credit, then applies `applyAmount` of it to targetBill.
    /// Returns the approved credit application.</summary>
    private async Task<VendorCreditApplication> ApplyVendorCreditAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid vendorId, Guid firstBillId, Guid targetBillId, decimal applyAmount)
    {
        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendorId, new DateOnly(2026, 3, 31), 100m, "check",
                    [new Allocation(firstBillId, 40m)])))   // pay 100, allocate 40 → 60 vendor credit
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        VendorCreditApplication creditApp = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
                new VendorCreditApplicationRequest(vendorId, new DateOnly(2026, 4, 15), [new Allocation(targetBillId, applyAmount)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<VendorCreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditApp.Id);
        return creditApp;
    }

    [Fact]
    public async Task Lists_a_vendors_credit_applications()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // Bill 1 ($100), enter+approve, then overpay $150 (allocating $100) → $50 vendor credit.
        Bill bill1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", new DraftBillRequest(
            vendor.Id, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", 100m, fixture.RentExpenseAccountId)]))).Content.ReadFromJsonAsync<Bill>())!;
        Bill entered1 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill1.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered1.Id);
        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor.Id, new DateOnly(2026, 3, 2), 150m, "check",
                [new Allocation(entered1.Id, 100m)]))).Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // Bill 2 ($40), enter+approve, then apply $40 of the vendor credit to it.
        Bill bill2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", new DraftBillRequest(
            vendor.Id, new DateOnly(2026, 4, 1), null, null, null,
            [new BillLineBody("Rent", 40m, fixture.RentExpenseAccountId)]))).Content.ReadFromJsonAsync<Bill>())!;
        Bill entered2 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill2.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered2.Id);
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendor-credit-applications",
            new VendorCreditApplicationRequest(vendor.Id, new DateOnly(2026, 4, 2), [new Allocation(entered2.Id, 40m)])))
            .EnsureSuccessStatusCode();

        VendorCreditApplication[] apps = (await clerk.GetFromJsonAsync<VendorCreditApplication[]>(
            $"/clients/{clientId}/vendor-credit-applications?vendorId={vendor.Id}"))!;
        Assert.Single(apps);
        Assert.Equal(vendor.Id, apps[0].VendorId);

        // VendorCreditApplication carries no allocation array — prove the 40 applied by folding it from the
        // document's own posted entry instead.
        EntryResponse[] appEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={apps[0].Id}"))!;
        EntryResponse appEntry = appEntries.Single(e => e.ReversalOf is null);
        Assert.Equal(40m, appEntry.Lines.Where(l => l.AccountId == fixture.PayableAccountId).Sum(l => l.Amount));
    }

    [Fact]
    public async Task Requires_vendorId()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage response = await clerk.GetAsync($"/clients/{clientId}/vendor-credit-applications");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Is_client_isolated()
    {
        (Guid clientAId, _, HttpClient clerkA, _) = await fixture.SeedSodClientAsync();
        (Guid clientBId, _, HttpClient clerkB, _) = await fixture.SeedSodClientAsync();
        Vendor vendorB = (await (await clerkB.PostAsJsonAsync($"/clients/{clientBId}/vendors",
            new CreateVendorRequest("Other", null))).Content.ReadFromJsonAsync<Vendor>())!;

        VendorCreditApplication[] apps = (await clerkA.GetFromJsonAsync<VendorCreditApplication[]>(
            $"/clients/{clientAId}/vendor-credit-applications?vendorId={vendorB.Id}"))!;
        Assert.Empty(apps);
    }

    [Fact]
    public async Task GET_vendor_credit_by_id_returns_allocations_and_journal_entry_id()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        Bill firstBill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "March Rent");   // overpaid → credit
        Bill targetBill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "April Rent");  // credit applied here
        VendorCreditApplication creditApp = await ApplyVendorCreditAsync(clerk, approver, clientId, vendor.Id, firstBill.Id, targetBill.Id, 60m);

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={creditApp.Id}"))!;
        Guid expectedEntryId = entries.Single(e => e is { Status: "Active", ReversalOf: null }).Id;

        VendorCreditView view = (await clerk.GetFromJsonAsync<VendorCreditView>(
            $"/clients/{clientId}/vendor-credit-applications/{creditApp.Id}"))!;

        Assert.Equal(creditApp.Id, view.Credit.Id);
        Assert.False(view.Credit.Voided);
        Assert.Equal(expectedEntryId, view.JournalEntryId);
        BillAllocationLine alloc = Assert.Single(view.Allocations);
        Assert.Equal(targetBill.Id, alloc.BillId);
        Assert.Equal(60m, alloc.Amount);
        Assert.Equal(targetBill.Number, alloc.BillNumber);
    }

    [Fact]
    public async Task GET_vendor_credit_by_unknown_id_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/vendor-credit-applications/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
