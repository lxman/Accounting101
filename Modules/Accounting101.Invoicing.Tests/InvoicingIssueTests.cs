using System.Net;
using System.Net.Http.Json;
using Accounting101.Invoicing;
using Accounting101.Invoicing.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

/// <summary>
/// The whole loop through the composed host: a member sets up a chart, creates a customer, drafts and
/// issues an invoice through invoicing's HTTP endpoints — and the A/R entry lands in the journal (posted
/// via the loopback ledger client, token forwarded) with the A/R-by-customer subledger tying out.
/// </summary>
public sealed class InvoicingIssueTests(InvoicingHostFixture fixture) : IClassFixture<InvoicingHostFixture>
{
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        // A/R is a control account requiring the Customer dimension; Revenue and Sales Tax Payable are plain.
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
    public async Task Issuing_an_invoice_through_the_host_lands_an_AR_entry_that_reconciles()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        Customer customer = (await (await http.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Beta LLC", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        DraftInvoiceRequest draftRequest = new(
            customer.Id,
            [new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 100m }],
            TaxRate: 0.07m, IssueDate: new DateOnly(2026, 3, 31), DueDate: null, Memo: null);
        HttpResponseMessage draftResponse = await http.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest);
        Assert.Equal(HttpStatusCode.Created, draftResponse.StatusCode);
        Invoice draft = (await draftResponse.Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Draft, draft.Status);
        Assert.Null(draft.Number);

        Invoice issued = (await (await http.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Issued, issued.Status);
        Assert.NotNull(issued.Number);

        // The A/R entry landed, posted, tagged by customer, debit = total (100 + 7 tax).
        EntryResponse[] entries = (await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!;
        EntryResponse entry = Assert.Single(entries);
        Assert.Equal("Posted", entry.Posting);
        EntryLineResponse ar = entry.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId);
        Assert.Equal(107m, ar.Amount);
        Assert.Equal(customer.Id, ar.Dimensions["Customer"]);   // EntryLineResponse.Dimensions is non-nullable

        // The A/R-by-customer subledger ties out to the control account.
        SubledgerReconciliationResponse recon = (await http.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(recon.TiesOut);
    }
}
