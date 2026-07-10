using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Proves the end-to-end module-credential path for Payables (Task 4 of the module-poster-identity
/// slice). When Payables enters a bill it POSTs to the engine's ledger endpoint; the engine stamps
/// <c>ViaModule = "payables"</c> on the resulting entry.
///
/// RED before Task 4: <c>HttpLedgerClient.PostAsync</c> does not yet attach <c>X-Module-Key</c> /
/// <c>X-Module-Secret</c>, so the raw path is used and <c>ViaModule</c> is null.
/// GREEN after Task 4: the credential headers are forwarded and the stamp is set.
/// </summary>
public sealed class ModuleViaPayablesTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.PayableAccountId,          "2000", "Accounts Payable",  "Liability", null, ["Vendor", "Bill"]);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,              "1000", "Cash",              "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.VendorCreditsAccountId,     "1300", "Vendor Credits",    "Asset",     "Vendor");
        await PutAccountAsync(controller, clientId, fixture.RentExpenseAccountId,       "5200", "Rent Expense",      "Expense",   null);
        await PutAccountAsync(controller, clientId, fixture.UtilitiesExpenseAccountId,  "5300", "Utilities Expense", "Expense",   null);
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

    /// <summary>
    /// Enter a bill via the Payables module and verify the resulting engine journal entry carries
    /// <c>ViaModule = "payables"</c>. Before Task 4 this is null (RED); after it is "payables" (GREEN).
    /// </summary>
    [Fact]
    public async Task Entering_a_bill_stamps_ViaModule_payables_on_the_engine_entry()
    {
        // Seed a SoD client (Clerk enters bills; Controller sets up the chart).
        (Guid clientId, HttpClient controller, HttpClient clerk, _) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        // Create a vendor.
        Vendor vendor = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest("ViaModuleCo", null)))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<Vendor>())!;

        // Draft a simple bill (single rent line).
        DraftBillRequest draftRequest = new(
            vendor.Id,
            BillDate: new DateOnly(2026, 6, 26),
            DueDate: null,
            VendorReference: "INV-VIAMOD-01",
            Memo: null,
            Lines: [new BillLineBody("June Rent", 5000m, fixture.RentExpenseAccountId)]);

        Bill draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/bills", draftRequest))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<Bill>())!;

        // Enter the bill — this triggers HttpLedgerClient.PostAsync and produces the engine entry.
        Bill entered = (await (await clerk.PostAsync($"/clients/{clientId}/bills/{draft.Id}/enter", null))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<Bill>())!;

        // Read the resulting engine entry back via sourceRef.
        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={entered.Id}"))!;

        Assert.Single(entries);

        // KEY ASSERTION: the engine must stamp ViaModule = "payables" because the client sent
        // X-Module-Key / X-Module-Secret alongside the forwarded user token.
        Assert.Equal("payables", entries[0].ViaModule);
    }
}
