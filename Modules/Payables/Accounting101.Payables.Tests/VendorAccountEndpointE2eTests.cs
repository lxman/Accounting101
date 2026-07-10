using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>Drives the real host and reconciles the vendor 360 invariants: AP balance = Σ open;
/// statement ends at AP balance; credit ledger ends at the vendor credit balance; 404 for unknown vendor.</summary>
public sealed class VendorAccountEndpointE2eTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
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

    [Fact]
    public async Task Vendor_account_reconciles()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Vendor vendor = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/vendors",
            new CreateVendorRequest("PropCo", null))).Content.ReadFromJsonAsync<Vendor>())!;

        // Bill 1 = $1000 due 2026-03-31, enter+approve, pay $1200 (allocate $1000) → $200 vendor credit.
        Bill bill1 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", new DraftBillRequest(
            vendor.Id, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, null,
            [new BillLineBody("Rent", 1000m, fixture.RentExpenseAccountId)]))).Content.ReadFromJsonAsync<Bill>())!;
        Bill entered1 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill1.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered1.Id);
        BillPayment pay = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendor.Id, new DateOnly(2026, 3, 15), 1200m, "check",
                [new Allocation(entered1.Id, 1000m)]))).Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, pay.Id);

        // Bill 2 = $800 due 2026-02-15 (overdue as of asOf), enter+approve, leave unpaid.
        Bill bill2 = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", new DraftBillRequest(
            vendor.Id, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 15), null, null,
            [new BillLineBody("Rent", 800m, fixture.RentExpenseAccountId)]))).Content.ReadFromJsonAsync<Bill>())!;
        Bill entered2 = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{bill2.Id}/enter", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, entered2.Id);

        VendorAccountView acct = (await clerk.GetFromJsonAsync<VendorAccountView>(
            $"/clients/{clientId}/vendors/{vendor.Id}/account?asOf=2026-04-15"))!;

        // bill1 fully paid → not open; bill2 ($800) open.
        Assert.Equal(800m, acct.ApBalance);
        Assert.Equal(800m, acct.OpenBills.Sum(b => b.OpenBalance));
        Assert.Equal(800m, acct.StatementLines[^1].Balance);             // statement ends at AP balance
        Assert.Equal(200m, acct.CreditLines[^1].CreditBalance);          // credit ledger ends at available credit

        // Reconcile credit ledger against the canonical credit-balance endpoint.
        var bal = (await clerk.GetFromJsonAsync<CreditBalanceDto>(
            $"/clients/{clientId}/vendors/{vendor.Id}/credit-balance"))!;
        Assert.Equal(bal.CreditBalance, acct.CreditLines[^1].CreditBalance);

        // bill2 is overdue (2026-02-15 → 2026-04-15 = 59 days) → 31-60 bucket.
        Assert.Equal(800m, acct.Aging.D31To60);
    }

    [Fact]
    public async Task Unknown_vendor_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage response = await clerk.GetAsync($"/clients/{clientId}/vendors/{Guid.NewGuid()}/account");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GET_account_rejects_non_iso_asOf_and_accepts_iso()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        Vendor vendor = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/vendors", new CreateVendorRequest("PropCo", null)))
            .Content.ReadFromJsonAsync<Vendor>())!;

        HttpResponseMessage bad = await clerk.GetAsync(
            $"/clients/{clientId}/vendors/{vendor.Id}/account?asOf={Uri.EscapeDataString("06/15/2026")}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        HttpResponseMessage ok = await clerk.GetAsync(
            $"/clients/{clientId}/vendors/{vendor.Id}/account?asOf=2026-06-15");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    private sealed record CreditBalanceDto(Guid VendorId, decimal CreditBalance);
}
