using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Accounting101.Settlement;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Proves the derived A/P read paths (<see cref="VendorAccountService.GetAccountAsync"/> via
/// <c>GET /vendors/{id}/account</c> and <c>GET /bills/{id}</c>; <see cref="BillPaymentService.GetVendorCreditBalanceAsync"/>
/// via <c>GET /vendors/{id}/credit-balance</c>) now fold the ledger instead of the module's stored
/// <c>Allocation[]</c>. Unlike the AR original, A/P is a LIABILITY (credit-normal): the debit-positive
/// fold reads an outstanding payable NEGATIVE, so a bill's open balance is <c>−fold</c> — the mirror image
/// of AR's A/R (debit-normal, <c>open = +fold</c>). Vendor Credits, by contrast, is an ASSET (debit-normal,
/// same normal balance as AR's A/R): its fold reads available credit directly POSITIVE — no negation —
/// which is itself the mirror of AR's Customer Credits (a liability, negated). Both sign rules are pinned
/// below against the raw fold, not just the derived figure, so a sign regression fails loudly.
/// <para>
/// Driven entirely over HTTP (not by resolving <c>VendorAccountService</c>/<c>BillPaymentService</c> from
/// DI directly): this host's multi-firm tenancy plumbing populates <c>FirmScope</c> from
/// <c>FirmResolutionMiddleware</c> during real request dispatch, so the module's document stores can only
/// be constructed inside an actual HTTP request — resolving them from a bare DI scope throws. See the AR
/// analog (<c>Accounting101.Receivables.Tests.FoldReadTests</c>) and <c>SubledgerReadTests</c> for the same
/// reasoning.
/// </para>
/// </summary>
public sealed class FoldReadTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controllerHttp, Guid clientId)
    {
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.PayableAccountId}",
            new AccountRequest { Number = "2000", Name = "Accounts Payable", Type = "Liability", RequiredDimensions = ["Vendor", "Bill"] }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.RentExpenseAccountId}",
            new AccountRequest { Number = "5200", Name = "Rent Expense", Type = "Expense" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.CashAccountId}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.VendorCreditsAccountId}",
            new AccountRequest { Number = "1300", Name = "Vendor Credits", Type = "Asset", RequiredDimension = "Vendor" }))
            .EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateVendorAsync(HttpClient http, Guid clientId)
    {
        Vendor vendor = (await (await http.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest("PropCo", null)))
            .Content.ReadFromJsonAsync<Vendor>())!;
        return vendor.Id;
    }

    private async Task<Bill> EnterBillAsync(HttpClient http, Guid clientId, Guid vendorId, decimal amount)
    {
        DraftBillRequest draftRequest = new(
            vendorId, new DateOnly(2026, 3, 1), null, null, null,
            [new BillLineBody("Rent", amount, fixture.RentExpenseAccountId)]);
        Bill draft = (await (await http.PostAsJsonAsync($"/clients/{clientId}/bills", draftRequest))
            .Content.ReadFromJsonAsync<Bill>())!;
        HttpResponseMessage enterResp = await http.PostAsync($"/clients/{clientId}/bills/{draft.Id}/enter", null);
        enterResp.EnsureSuccessStatusCode();
        return (await enterResp.Content.ReadFromJsonAsync<Bill>())!;
    }

    /// <summary>Approve every PendingApproval entry sourced from the given document.</summary>
    private static async Task ApproveSourceEntryAsync(HttpClient http, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await http.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    private sealed record CreditBalanceResponse(Guid VendorId, decimal CreditBalance);

    [Fact]
    public async Task Bill_open_balance_is_negated_from_the_credit_normal_AP_fold()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid vendorId = await CreateVendorAsync(http, clientId);

        Bill entered = await EnterBillAsync(http, clientId, vendorId, 100m);
        await ApproveSourceEntryAsync(http, clientId, entered.Id);

        HttpResponseMessage payResp = await http.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendorId, new DateOnly(2026, 3, 31), 30m, "check", [new Allocation(entered.Id, 30m)]));
        payResp.EnsureSuccessStatusCode();
        BillPayment payment = (await payResp.Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveSourceEntryAsync(http, clientId, payment.Id);

        BillView view = (await http.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{entered.Id}"))!;
        Assert.Equal(70m, view.OpenBalance);

        decimal billFold = (await http.GetFromJsonAsync<SubledgerResponse>(
                $"/clients/{clientId}/subledger?account={fixture.PayableAccountId}&dimension=Bill"))!
            .Lines.Single(l => l.DimensionValue == entered.Id).Balance;
        // A/P is credit-normal: the debit-positive fold reads the outstanding payable NEGATIVE.
        // open = −fold, the mirror of AR's open = +fold.
        Assert.Equal(-billFold, view.OpenBalance);
        Assert.Equal(-70m, billFold);
    }

    [Fact]
    public async Task Unapproved_payment_does_not_reduce_the_open_balance_until_the_entry_is_approved()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid vendorId = await CreateVendorAsync(http, clientId);

        Bill entered = await EnterBillAsync(http, clientId, vendorId, 100m);
        await ApproveSourceEntryAsync(http, clientId, entered.Id);

        HttpResponseMessage payResp = await http.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendorId, new DateOnly(2026, 3, 31), 30m, "check", [new Allocation(entered.Id, 30m)]));
        payResp.EnsureSuccessStatusCode();
        BillPayment payment = (await payResp.Content.ReadFromJsonAsync<BillPayment>())!;

        // Not yet approved: the payment's A/P-relief line is not on the books, so the fold-derived open
        // balance must still read the full 100 — reads are Posted-only.
        BillView before = (await http.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{entered.Id}"))!;
        Assert.Equal(100m, before.OpenBalance);

        await ApproveSourceEntryAsync(http, clientId, payment.Id);

        BillView after = (await http.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{entered.Id}"))!;
        Assert.Equal(70m, after.OpenBalance);
    }

    [Fact]
    public async Task Vendor_credit_balance_from_the_fold_is_positive_with_no_negation()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid vendorId = await CreateVendorAsync(http, clientId);

        Bill entered = await EnterBillAsync(http, clientId, vendorId, 40m);
        await ApproveSourceEntryAsync(http, clientId, entered.Id);

        // Pay 100, allocate 40 to the bill (fully paying it) -> 60 unapplied -> 60 vendor credit.
        HttpResponseMessage payResp = await http.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendorId, new DateOnly(2026, 3, 31), 100m, "check", [new Allocation(entered.Id, 40m)]));
        payResp.EnsureSuccessStatusCode();
        BillPayment payment = (await payResp.Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveSourceEntryAsync(http, clientId, payment.Id);

        CreditBalanceResponse balance = (await http.GetFromJsonAsync<CreditBalanceResponse>(
            $"/clients/{clientId}/vendors/{vendorId}/credit-balance"))!;
        Assert.Equal(60m, balance.CreditBalance);

        VendorAccountView view = (await http.GetFromJsonAsync<VendorAccountView>(
            $"/clients/{clientId}/vendors/{vendorId}/account?asOf=2026-04-01"))!;
        Assert.Equal(60m, view.CreditBalance);

        // Prove the sign is read directly, not accidentally negated: Vendor Credits is an ASSET
        // (debit-normal), so the raw fold on it is ALREADY positive +60 — unlike AR's Customer Credits
        // (a liability), this must NOT be negated. A negation bug would read −60.
        decimal rawFold = (await http.GetFromJsonAsync<SubledgerResponse>(
                $"/clients/{clientId}/subledger?account={fixture.VendorCreditsAccountId}&dimension=Vendor"))!
            .Lines.Single(l => l.DimensionValue == vendorId).Balance;
        Assert.Equal(60m, rawFold);
        Assert.NotEqual(-60m, rawFold);
    }

    [Fact]
    public async Task Payment_persists_no_allocation_array_and_bill_fold_still_reads_open_balance()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid vendorId = await CreateVendorAsync(http, clientId);

        Bill entered = await EnterBillAsync(http, clientId, vendorId, 100m);
        await ApproveSourceEntryAsync(http, clientId, entered.Id);

        HttpResponseMessage payResp = await http.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendorId, new DateOnly(2026, 3, 31), 30m, "check", [new Allocation(entered.Id, 30m)]));
        payResp.EnsureSuccessStatusCode();
        BillPayment payment = (await payResp.Content.ReadFromJsonAsync<BillPayment>())!;
        await ApproveSourceEntryAsync(http, clientId, payment.Id);

        // The persisted BillPayment carries no allocation array at all — a compile-time guarantee: the type
        // has no Allocations property to read. Proven here via reflection so this test fails loudly (not
        // silently) if the property is ever reintroduced.
        Assert.Null(typeof(BillPayment).GetProperty("Allocations"));

        BillPayment[] byVendor = (await http.GetFromJsonAsync<BillPayment[]>(
            $"/clients/{clientId}/bill-payments?vendorId={vendorId}"))!;
        Assert.Contains(byVendor, p => p.Id == payment.Id);

        // The bill's open balance is folded from the ledger, not from any stored allocation array — it
        // still reads 70 (100 − 30) purely from the fold.
        BillView view = (await http.GetFromJsonAsync<BillView>($"/clients/{clientId}/bills/{entered.Id}"))!;
        Assert.Equal(70m, view.OpenBalance);
    }

    [Fact]
    public async Task Second_unapproved_overlapping_payment_is_rejected_pending_inclusive()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);
        Guid vendorId = await CreateVendorAsync(http, clientId);

        Bill entered = await EnterBillAsync(http, clientId, vendorId, 100m);
        await ApproveSourceEntryAsync(http, clientId, entered.Id);

        // First payment allocates the full 100 to the bill but is left UNAPPROVED.
        HttpResponseMessage firstResp = await http.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendorId, new DateOnly(2026, 3, 31), 100m, "check", [new Allocation(entered.Id, 100m)]));
        firstResp.EnsureSuccessStatusCode();

        // Second payment, also allocating 100 to the same bill, must be rejected: even though the first
        // entry is still PendingApproval (not yet on the books for READS), the write-path reservation check
        // is pending-inclusive so the two don't both pass and over-relieve the bill once approved.
        HttpResponseMessage secondResp = await http.PostAsJsonAsync($"/clients/{clientId}/bill-payments",
            new RecordBillPaymentRequest(vendorId, new DateOnly(2026, 4, 1), 100m, "check", [new Allocation(entered.Id, 100m)]));
        Assert.False(secondResp.IsSuccessStatusCode);
    }
}
