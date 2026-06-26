using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Receivables.Api;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves the draft edit/discard routes over a real host:
/// PUT /invoices/{id} edits a draft; DELETE /invoices/{id} discards a draft;
/// both refuse to operate on an issued invoice (409); and issue returns a
/// new id + number while GET of the old draft id yields 404.
/// </summary>
public sealed class ReceivablesDraftLifecycleTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        (await controller.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.ReceivableAccountId}",
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" }))
            .EnsureSuccessStatusCode();
        (await controller.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.RevenueAccountId}",
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" }))
            .EnsureSuccessStatusCode();
        (await controller.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.SalesTaxPayableAccountId}",
            new AccountRequest { Number = "2200", Name = "Sales Tax Payable", Type = "Liability" }))
            .EnsureSuccessStatusCode();
    }

    private static DraftInvoiceRequest SimpleRequest(Guid customerId) => new(
        customerId,
        [new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 500m }],
        TaxRate: 0m, IssueDate: new DateOnly(2026, 4, 30), DueDate: null, Memo: null);

    [Fact]
    public async Task PUT_edits_a_draft_and_GET_shows_the_new_fields()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Edit Co", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", SimpleRequest(customer.Id)))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Draft, draft.Status);

        // PUT with revised fields.
        DraftInvoiceRequest editRequest = new(
            customer.Id,
            [new InvoiceLine { Description = "Revised Consulting", Quantity = 2m, UnitPrice = 750m }],
            TaxRate: 0.05m, IssueDate: new DateOnly(2026, 5, 31), DueDate: new DateOnly(2026, 6, 30), Memo: "Updated");
        HttpResponseMessage putResponse = await clerk.PutAsJsonAsync($"/clients/{clientId}/invoices/{draft.Id}", editRequest);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        Invoice edited = (await putResponse.Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Draft, edited.Status);
        Assert.Equal(new DateOnly(2026, 5, 31), edited.IssueDate);
        Assert.Equal(new DateOnly(2026, 6, 30), edited.DueDate);
        Assert.Equal("Updated", edited.Memo);
        Assert.Single(edited.Lines);
        Assert.Equal("Revised Consulting", edited.Lines[0].Description);
        Assert.Equal(2m, edited.Lines[0].Quantity);
        Assert.Equal(750m, edited.Lines[0].UnitPrice);

        // GET confirms the store reflects the update.
        InvoiceView? view = await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{draft.Id}");
        Assert.NotNull(view);
        Assert.Equal("Revised Consulting", view.Invoice.Lines[0].Description);
    }

    [Fact]
    public async Task PUT_on_issued_invoice_returns_409_or_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Edit Issued Co", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", SimpleRequest(customer.Id)))
            .Content.ReadFromJsonAsync<Invoice>())!;

        HttpResponseMessage issueResponse = await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null);
        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
        Invoice issued = (await issueResponse.Content.ReadFromJsonAsync<Invoice>())!;

        // Attempt to edit the now-issued invoice — must be refused.
        HttpResponseMessage putResponse = await clerk.PutAsJsonAsync(
            $"/clients/{clientId}/invoices/{issued.Id}", SimpleRequest(customer.Id));

        Assert.True(
            putResponse.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity,
            $"Expected 409 or 422 but got {(int)putResponse.StatusCode}");
    }

    [Fact]
    public async Task DELETE_discards_a_draft_and_GET_returns_404()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Discard Co", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", SimpleRequest(customer.Id)))
            .Content.ReadFromJsonAsync<Invoice>())!;

        HttpResponseMessage deleteResponse = await clerk.DeleteAsync($"/clients/{clientId}/invoices/{draft.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Draft is gone — GET returns 404.
        HttpResponseMessage getResponse = await clerk.GetAsync($"/clients/{clientId}/invoices/{draft.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DELETE_on_issued_invoice_returns_409_or_422()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Discard Issued Co", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", SimpleRequest(customer.Id)))
            .Content.ReadFromJsonAsync<Invoice>())!;

        HttpResponseMessage issueResponse = await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null);
        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
        Invoice issued = (await issueResponse.Content.ReadFromJsonAsync<Invoice>())!;

        // Attempt to discard an issued invoice — must be refused.
        HttpResponseMessage deleteResponse = await clerk.DeleteAsync($"/clients/{clientId}/invoices/{issued.Id}");

        Assert.True(
            deleteResponse.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity,
            $"Expected 409 or 422 but got {(int)deleteResponse.StatusCode}");
    }

    [Fact]
    public async Task Issue_returns_invoice_with_new_id_and_number_draft_id_returns_404()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Issue Co", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", SimpleRequest(customer.Id)))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Guid draftId = draft.Id;

        HttpResponseMessage issueResponse = await clerk.PostAsync($"/clients/{clientId}/invoices/{draftId}/issue", null);
        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
        Invoice issued = (await issueResponse.Content.ReadFromJsonAsync<Invoice>())!;

        // The issued invoice has a new id (different from the draft id) and an assigned number.
        Assert.NotEqual(draftId, issued.Id);
        Assert.NotNull(issued.Number);
        Assert.Equal(InvoiceStatus.Issued, issued.Status);

        // GET by the old draft id → 404 (draft was deleted on promote).
        HttpResponseMessage draftGet = await clerk.GetAsync($"/clients/{clientId}/invoices/{draftId}");
        Assert.Equal(HttpStatusCode.NotFound, draftGet.StatusCode);

        // GET by the issued id → Issued.
        InvoiceView? issuedView = await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{issued.Id}");
        Assert.NotNull(issuedView);
        Assert.Equal(InvoiceStatus.Issued, issuedView.Invoice.Status);
        Assert.NotNull(issuedView.Invoice.Number);
    }
}
