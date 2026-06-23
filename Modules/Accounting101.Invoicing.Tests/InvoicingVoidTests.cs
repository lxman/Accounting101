using System.Net.Http.Json;
using Accounting101.Invoicing;
using Accounting101.Invoicing.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

/// <summary>Voiding an issued invoice through the host reverses its A/R entry (found by the source
/// back-link) and flips the invoice to Void.</summary>
public sealed class InvoicingVoidTests(InvoicingHostFixture fixture) : IClassFixture<InvoicingHostFixture>
{
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.ReceivableAccountId}",
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" }))
            .EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.RevenueAccountId}",
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" }))
            .EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.SalesTaxPayableAccountId}",
            new AccountRequest { Number = "2200", Name = "Sales Tax Payable", Type = "Liability" }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Voiding_an_issued_invoice_reverses_its_entry()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        Customer customer = (await (await http.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Gamma", null)))
            .Content.ReadFromJsonAsync<Customer>())!;
        Invoice draft = (await (await http.PostAsJsonAsync($"/clients/{clientId}/invoices",
            new DraftInvoiceRequest(customer.Id,
                [new InvoiceLine { Description = "Work", Quantity = 1m, UnitPrice = 100m }],
                0m, new DateOnly(2026, 3, 31), null, null)))
            .Content.ReadFromJsonAsync<Invoice>())!;
        await http.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null);

        Invoice voided = (await (await http.PostAsJsonAsync(
            $"/clients/{clientId}/invoices/{draft.Id}/void", new VoidInvoiceRequest("duplicate")))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Void, voided.Status);

        // The source now resolves to two entries — the original and its reversal.
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!;
        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, e => e.ReversalOf is not null);
    }
}
