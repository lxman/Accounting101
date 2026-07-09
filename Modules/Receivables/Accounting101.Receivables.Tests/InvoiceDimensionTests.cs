using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves the invoice-issue recipe tags the A/R line with BOTH the Customer and Invoice dimensions.
/// A/R still requires only Customer at this point (flipped later), so this is purely additive — the
/// tag rides along and the Invoice-axis fold ties out once it's present.
/// </summary>
public sealed class InvoiceDimensionTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    /// <summary>Chart setup requires ManageAccounts — only the Controller holds that permission.</summary>
    private async Task SetUpChartAsync(HttpClient controllerHttp, Guid clientId)
    {
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.ReceivableAccountId}",
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimension = "Customer" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.RevenueAccountId}",
            new AccountRequest { Number = "4000", Name = "Revenue", Type = "Revenue" }))
            .EnsureSuccessStatusCode();
        (await controllerHttp.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.SalesTaxPayableAccountId}",
            new AccountRequest { Number = "2200", Name = "Sales Tax Payable", Type = "Liability" }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Issued_invoice_AR_line_carries_Customer_and_Invoice_dimensions()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne Enterprises", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        DraftInvoiceRequest draftRequest = new(
            customer.Id,
            [new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 100m }],
            TaxRate: 0.07m, IssueDate: new DateOnly(2026, 3, 31), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;

        HttpResponseMessage issueResp = await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null);
        issueResp.EnsureSuccessStatusCode();
        Invoice issued = (await issueResp.Content.ReadFromJsonAsync<Invoice>())!;

        // The A/R entry lands PendingApproval under SoD — approve before asserting Posted.
        EntryResponse pendingEntry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={issued.Id}"))!);
        (await approver.PostAsync($"/clients/{clientId}/entries/{pendingEntry.Id}/approve", null))
            .EnsureSuccessStatusCode();

        EntryResponse postedEntry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={issued.Id}"))!);
        Assert.Equal("Posted", postedEntry.Posting);

        EntryLineResponse ar = postedEntry.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId);
        Assert.Equal(customer.Id, ar.Dimensions["Customer"]);
        Assert.Equal(issued.Id, ar.Dimensions["Invoice"]);

        // The Invoice-axis fold ties to the invoice total. NOTE: the /subledger/reconciliation endpoint
        // can't be used here — it 422s unless the queried dimension is in the account's RequiredDimensions,
        // and A/R still requires only Customer at this stage (flipped to {Customer, Invoice} in Task 6).
        // The bare /subledger endpoint bypasses that gate when no 'account' is supplied, so it can still
        // prove the fold is correct: the A/R line, now tagged with Invoice, sums to the invoice total.
        SubledgerResponse fold = (await clerk.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?dimension=Invoice"))!;
        SubledgerLineResponse arFold = fold.Lines.Single(
            l => l.AccountId == fixture.ReceivableAccountId && l.DimensionValue == issued.Id);
        Assert.Equal(ar.Amount, arFold.Balance);
    }
}
