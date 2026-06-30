using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;

namespace Accounting101.Payables.Tests;

/// <summary>Proves the draft edit (PUT) and discard (DELETE) HTTP endpoints on a real host.
/// Discard must leave the bill NotFound (deleted), not Voided. Editing keeps it a draft.</summary>
public sealed class BillDraftEndpointsTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    [Fact]
    public async Task Draft_can_be_edited_via_PUT()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        Guid vendorId = await BillSettlementScenario.CreateVendorAsync(clerk, clientId);

        Guid expense = fixture.RentExpenseAccountId;
        Bill draft = await PostDraftAsync(clerk, clientId, vendorId, expense, vendorReference: null);

        Bill edited = (await (await clerk.PutAsJsonAsync($"/clients/{clientId}/bills/{draft.Id}",
            new DraftBillRequest(vendorId, new DateOnly(2026, 3, 1), null, "VREF-1", null,
                [new BillLineBody("Rent", 100m, expense)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;

        Assert.Equal(BillStatus.Draft, edited.Status);
        Assert.Equal("VREF-1", edited.VendorReference);
    }

    [Fact]
    public async Task Draft_can_be_discarded_via_DELETE()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        Guid vendorId = await BillSettlementScenario.CreateVendorAsync(clerk, clientId);

        Guid expense = fixture.RentExpenseAccountId;
        Bill draft = await PostDraftAsync(clerk, clientId, vendorId, expense, vendorReference: null);

        HttpResponseMessage deleted = await clerk.DeleteAsync($"/clients/{clientId}/bills/{draft.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        HttpResponseMessage after = await clerk.GetAsync($"/clients/{clientId}/bills/{draft.Id}");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);   // draft is gone, not voided
    }

    private static async Task<Bill> PostDraftAsync(HttpClient clerk, Guid clientId, Guid vendorId, Guid expenseAccount, string? vendorReference)
    {
        DraftBillRequest req = new(vendorId, new DateOnly(2026, 3, 1), null, vendorReference, null,
            [new BillLineBody("Rent", 100m, expenseAccount)]);
        return (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", req))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Bill>())!;
    }
}
