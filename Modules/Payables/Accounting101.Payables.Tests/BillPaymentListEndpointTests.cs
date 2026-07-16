using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>Proves GET /clients/{clientId}/bill-payments?vendorId returns the vendor's recorded
/// payments, 400s without vendorId, and is client-isolated.</summary>
public sealed class BillPaymentListEndpointTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension, string[]? requiredDimensions = null) =>
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest
            {
                Number = number, Name = name, Type = type,
                RequiredDimension = requiredDimension, RequiredDimensions = requiredDimensions,
            }))
            .EnsureSuccessStatusCode();

    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId, "2000", "Accounts Payable", "Liability", null, ["Vendor", "Bill"]);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId, "1300", "Vendor Credits", "Asset", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId, "5200", "Rent Expense", "Expense", null);
    }

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

    [Fact]
    public async Task Lists_a_vendors_recorded_payments()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,          "1000", "Cash",           "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId, "1300", "Vendor Credits", "Asset", "Vendor");
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,       "2000", "Accounts Payable","Liability", null, ["Vendor", "Bill"]);

        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // A pure prepayment (no allocations) → full amount becomes vendor credit; no bill needed.
        RecordBillPaymentRequest req = new(vendor.Id, new DateOnly(2026, 3, 1), 500m, "check", []);
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments", req)).EnsureSuccessStatusCode();

        BillPayment[] payments = (await clerk.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientId}/bill-payments?vendorId={vendor.Id}"))!;
        Assert.Single(payments);
        Assert.Equal(500m, payments[0].Amount);
        Assert.Equal(vendor.Id, payments[0].VendorId);
    }

    [Fact]
    public async Task Requires_vendorId()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage response = await clerk.GetAsync($"/clients/{clientId}/bill-payments");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Is_client_isolated()
    {
        (Guid clientAId, _, HttpClient clerkA, _) = await fixture.SeedSodClientAsync();
        (Guid clientBId, _, HttpClient clerkB, _) = await fixture.SeedSodClientAsync();
        Vendor vendorB = (await (await clerkB.PostAsJsonAsync($"/clients/{clientBId}/vendors",
            new CreateVendorRequest("Other", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // Client A asks for client B's vendor id → empty (A has no such payments).
        BillPayment[] payments = (await clerkA.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientAId}/bill-payments?vendorId={vendorB.Id}"))!;
        Assert.Empty(payments);
    }

    [Fact]
    public async Task GET_bill_payment_by_id_returns_allocations_unapplied_and_journal_entry_id()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        Bill billA = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "Rent A");   // 100
        Bill billB = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "Rent B");   // 100

        // Pay 150 allocating 100→A and 30→B (total 130 applied), leaving 20 unapplied.
        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor.Id, new DateOnly(2026, 3, 31), 150m, "check",
                    [new Allocation(billA.Id, 100m), new Allocation(billB.Id, 30m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={payment.Id}"))!;
        Guid expectedEntryId = entries.Single(e => e is { Status: "Active", ReversalOf: null }).Id;

        BillPaymentView view = (await clerk.GetFromJsonAsync<BillPaymentView>(
            $"/clients/{clientId}/bill-payments/{payment.Id}"))!;

        Assert.Equal(payment.Id, view.Payment.Id);
        Assert.Equal(150m, view.Payment.Amount);
        Assert.Equal("check", view.Payment.Method);
        Assert.False(view.Payment.Voided);
        Assert.Equal(expectedEntryId, view.JournalEntryId);
        Assert.Equal(20m, view.Unapplied);

        Assert.Equal(2, view.Allocations.Count);
        BillAllocationLine a1 = view.Allocations.Single(a => a.BillId == billA.Id);
        Assert.Equal(100m, a1.Amount);
        Assert.Equal(billA.Number, a1.BillNumber);
        BillAllocationLine a2 = view.Allocations.Single(a => a.BillId == billB.Id);
        Assert.Equal(30m, a2.Amount);
        Assert.Equal(billB.Number, a2.BillNumber);
    }

    [Fact]
    public async Task GET_fully_allocated_bill_payment_has_zero_unapplied()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("Wayne", null))).Content.ReadFromJsonAsync<Vendor>())!;
        Bill bill = await EnterBillAsync(clerk, approver, clientId, vendor.Id, "Rent");

        BillPayment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
                new RecordBillPaymentRequest(vendor.Id, new DateOnly(2026, 3, 6), 100m, "check",
                    [new Allocation(bill.Id, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        BillPaymentView view = (await clerk.GetFromJsonAsync<BillPaymentView>(
            $"/clients/{clientId}/bill-payments/{payment.Id}"))!;

        Assert.Equal(0m, view.Unapplied);
        Assert.Equal(100m, Assert.Single(view.Allocations).Amount);
    }

    [Fact]
    public async Task GET_bill_payment_by_unknown_id_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/bill-payments/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
