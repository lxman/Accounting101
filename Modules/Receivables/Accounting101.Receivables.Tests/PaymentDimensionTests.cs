using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves the payment-posting recipe emits one Invoice-dimensioned A/R credit line PER allocation,
/// replacing the single aggregate line. The per-invoice split moves from the module's Allocation[]
/// onto ledger dimensions; A/R still requires only Customer at this point (flipped later), so the new
/// tag is purely additive — it rides along and the Invoice-axis fold ties out once it's present.
/// </summary>
public sealed class PaymentDimensionTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    /// <summary>Chart setup requires ManageAccounts — the single non-SoD Controller holds every capability.</summary>
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
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.CashAccountId}",
            new AccountRequest { Number = "1000", Name = "Cash", Type = "Asset" }))
            .EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.CustomerCreditsAccountId}",
            new AccountRequest { Number = "2300", Name = "Customer Credits", Type = "Liability", RequiredDimension = "Customer" }))
            .EnsureSuccessStatusCode();
    }

    private static async Task<Invoice> IssueInvoiceAsync(HttpClient http, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draftRequest = new(
            customerId,
            [new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await http.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        HttpResponseMessage issueResp = await http.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null);
        issueResp.EnsureSuccessStatusCode();
        return (await issueResp.Content.ReadFromJsonAsync<Invoice>())!;
    }

    /// <summary>Approve the (single) PendingApproval entry sourced from the given document.</summary>
    private static async Task ApproveSourceEntryAsync(HttpClient http, Guid clientId, Guid sourceRef)
    {
        EntryResponse entry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!);
        (await http.PostAsync($"/clients/{clientId}/entries/{entry.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Split_payment_emits_one_Invoice_tagged_AR_line_per_allocation()
    {
        // Non-SoD SeedClientAsync => the same Controller may approve their own entries; the engine still
        // holds every post PendingApproval until an explicit /approve call.
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        Customer customer = (await (await http.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne Enterprises", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice invoiceA = await IssueInvoiceAsync(http, clientId, customer.Id, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoiceA.Id);
        Invoice invoiceB = await IssueInvoiceAsync(http, clientId, customer.Id, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoiceB.Id);

        RecordPaymentRequest paymentRequest = new(customer.Id, new DateOnly(2026, 3, 31), 150m, "check",
            [new Allocation(invoiceA.Id, 100m), new Allocation(invoiceB.Id, 50m)]);
        HttpResponseMessage paymentResp = await http.PostAsJsonAsync($"/clients/{clientId}/payments", paymentRequest);
        paymentResp.EnsureSuccessStatusCode();
        Payment payment = (await paymentResp.Content.ReadFromJsonAsync<Payment>())!;

        await ApproveSourceEntryAsync(http, clientId, payment.Id);

        EntryResponse postedEntry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={payment.Id}"))!);
        Assert.Equal("Posted", postedEntry.Posting);

        List<EntryLineResponse> arLines = postedEntry.Lines
            .Where(l => l.AccountId == fixture.ReceivableAccountId).ToList();
        Assert.Equal(2, arLines.Count);
        Assert.Contains(arLines, l =>
            l.Dimensions["Invoice"] == invoiceA.Id && l.Amount == 100m && l.Dimensions["Customer"] == customer.Id);
        Assert.Contains(arLines, l =>
            l.Dimensions["Invoice"] == invoiceB.Id && l.Amount == 50m && l.Dimensions["Customer"] == customer.Id);

        // Folds: A fully relieved (open 0), B still open 50. NOTE: the /subledger/reconciliation endpoint
        // can't be used here — it 422s unless the queried dimension is in the account's RequiredDimensions,
        // and A/R still requires only Customer at this stage (flipped to {Customer, Invoice} in Task 6).
        // The bare /subledger endpoint bypasses that gate when no 'account' is supplied, so it can still
        // prove the fold is correct: the per-allocation A/R lines, tagged with Invoice, net to the right
        // open balance per invoice.
        SubledgerResponse fold = (await http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?dimension=Invoice"))!;
        SubledgerLineResponse byA = fold.Lines.Single(
            l => l.AccountId == fixture.ReceivableAccountId && l.DimensionValue == invoiceA.Id);
        SubledgerLineResponse byB = fold.Lines.Single(
            l => l.AccountId == fixture.ReceivableAccountId && l.DimensionValue == invoiceB.Id);
        Assert.Equal(0m, byA.Balance);
        Assert.Equal(50m, byB.Balance);
    }
}
