using System.Net.Http.Headers;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Payables.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Payables.Tests;

/// <summary>
/// Proves the module's ledger client can read a per-dimension subledger fold — the read path that later
/// ledger-first work uses to derive A/P balances instead of a module-owned mirror. A/P is a LIABILITY
/// (credit-normal), so the debit-positive fold reads a payable's balance as NEGATIVE the bill total —
/// the mirror image of the Receivables A/R test, where the fold is positive because A/R is debit-normal.
/// </summary>
public sealed class SubledgerReadTests(PayablesHostFixture fixture) : IClassFixture<PayablesHostFixture>
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

    [Fact]
    public async Task GetSubledgerAsync_returns_the_AP_fold_for_a_vendor()
    {
        // Non-SoD SeedClientAsync => the same Controller may approve their own entry (SoD isn't required);
        // the engine still holds every post PendingApproval until an explicit /approve call, so one is
        // still needed here before the entry contributes to the on-the-books subledger fold.
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        Vendor vendor = (await (await http.PostAsJsonAsync(
                $"/clients/{clientId}/vendors", new CreateVendorRequest("PropCo", null)))
            .Content.ReadFromJsonAsync<Vendor>())!;

        DraftBillRequest draftRequest = new(
            vendor.Id,
            BillDate: new DateOnly(2026, 3, 1),
            DueDate: null,
            VendorReference: null,
            Memo: null,
            Lines: [new BillLineBody("March Rent", 6000m, fixture.RentExpenseAccountId)]);
        Bill draft = (await (await http.PostAsJsonAsync($"/clients/{clientId}/bills", draftRequest))
            .Content.ReadFromJsonAsync<Bill>())!;

        HttpResponseMessage enterResponse = await http.PostAsync($"/clients/{clientId}/bills/{draft.Id}/enter", null);
        enterResponse.EnsureSuccessStatusCode();
        Bill entered = (await enterResponse.Content.ReadFromJsonAsync<Bill>())!;
        decimal total = entered.Total;

        EntryResponse apEntry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={entered.Id}"))!);
        (await http.PostAsync($"/clients/{clientId}/entries/{apEntry.Id}/approve", null)).EnsureSuccessStatusCode();

        // Resolve the module's own ledger client from the host's DI container. It reads the caller's
        // bearer token off the ambient HttpContext (see HttpLedgerClient.Forwarded), so a fake HttpContext
        // carrying the seeded Controller's token stands in for the request pipeline that would normally
        // supply it.
        using IServiceScope scope = fixture.Services.CreateScope();
        IHttpContextAccessor accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        accessor.HttpContext.Request.Headers.Authorization =
            ((AuthenticationHeaderValue)http.DefaultRequestHeaders.Authorization!).ToString();
        ILedgerClient ledger = scope.ServiceProvider.GetRequiredService<ILedgerClient>();

        IReadOnlyList<SubledgerLineResponse> fold =
            await ledger.GetSubledgerAsync(clientId, fixture.PayableAccountId, "Vendor", null, default);

        SubledgerLineResponse line = Assert.Single(fold, l => l.DimensionValue == vendor.Id);
        // A/P is credit-normal; the debit-positive fold reads the payable as negative the bill total.
        Assert.Equal(-total, line.Balance);
    }
}
