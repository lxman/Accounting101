using System.Net.Http.Headers;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Receivables.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves the module's ledger client can read a per-dimension subledger fold — the read path that
/// later ledger-first work (Task 7) uses to derive AR balances instead of a module-owned mirror.
/// </summary>
public sealed class SubledgerReadTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    /// <summary>Chart setup requires ManageAccounts — only the Controller holds that permission.</summary>
    private async Task SetUpChartAsync(HttpClient controllerHttp, Guid clientId)
    {
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.ReceivableAccountId}",
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimensions = ["Customer", "Invoice"] }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.RevenueAccountId}",
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.SalesTaxPayableAccountId}",
            new AccountRequest { Number = "2200", Name = "Sales Tax Payable", Type = "Liability" }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetSubledgerAsync_returns_the_AR_fold_for_a_customer()
    {
        // Non-SoD SeedClientAsync => the same Controller may approve their own entry (SoD isn't required);
        // the engine still holds every post PendingApproval until an explicit /approve call, so one is
        // still needed here before the entry contributes to the on-the-books subledger fold.
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        Customer customer = (await (await http.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne Enterprises", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        DraftInvoiceRequest draftRequest = new(
            customer.Id,
            [new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 100m }],
            TaxRate: 0.07m, IssueDate: new DateOnly(2026, 3, 31), DueDate: null, Memo: null);
        Invoice draft = (await (await http.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;

        HttpResponseMessage issueResponse = await http.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null);
        issueResponse.EnsureSuccessStatusCode();
        Invoice issued = (await issueResponse.Content.ReadFromJsonAsync<Invoice>())!;
        decimal total = issued.Total;

        EntryResponse arEntry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={issued.Id}"))!);
        (await http.PostAsync($"/clients/{clientId}/entries/{arEntry.Id}/approve", null)).EnsureSuccessStatusCode();

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
            await ledger.GetSubledgerAsync(clientId, fixture.ReceivableAccountId, "Customer", null, default);

        SubledgerLineResponse line = Assert.Single(fold, l => l.DimensionValue == customer.Id);
        Assert.Equal(total, line.Balance);
    }
}
