using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;   // AccountRequest
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the read endpoint that powers the UI's Refunds list: it returns a customer's refunds
/// (amount + surfaced memo + voided) as a date-descending list, and rejects a missing customerId.</summary>
public sealed class GetRefundsEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", null, ["Customer", "Invoice"]);
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
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

    private static async Task<Guid> IssueInvoiceAsync(HttpClient clerk, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draftRequest = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        return issued.Id;
    }

    [Fact]
    public async Task GET_refunds_returns_date_descending_list_with_memo_and_voided()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        // Create $100 of unapplied credit: issue a $100 invoice, pay $200 allocating $100 → $100 credit.
        Guid invoiceId = await IssueInvoiceAsync(clerk, clientId, customer.Id, 100m);
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 2), 200m, "check",
                [new Allocation(invoiceId, 100m)]))).EnsureSuccessStatusCode();

        Refund first = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer.Id, new DateOnly(2026, 3, 5), 30m, "partial")))
            .Content.ReadFromJsonAsync<Refund>())!;
        (await clerk.PostAsJsonAsync($"/clients/{clientId}/refunds",
            new RefundRequest(customer.Id, new DateOnly(2026, 3, 10), 40m, "balance")))
            .EnsureSuccessStatusCode();
        // Void requires the Controller — module document write (ar.write) plus GL void; transitions the
        // pending ledger entry.
        (await controller.PostAsync($"/clients/{clientId}/refunds/{first.Id}/void", null)).EnsureSuccessStatusCode();

        Refund[] list = (await clerk.GetFromJsonAsync<Refund[]>(
            $"/clients/{clientId}/refunds?customerId={customer.Id}"))!;

        Assert.Equal(2, list.Length);
        Assert.Equal(40m, list[0].Amount);          // 3/10 newest first
        Assert.Equal("balance", list[0].Memo);
        Assert.False(list[0].Voided);
        Assert.Equal(30m, list[1].Amount);          // 3/5
        Assert.Equal("partial", list[1].Memo);
        Assert.True(list[1].Voided);
    }

    [Fact]
    public async Task GET_refunds_without_customerId_is_400()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/refunds");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
