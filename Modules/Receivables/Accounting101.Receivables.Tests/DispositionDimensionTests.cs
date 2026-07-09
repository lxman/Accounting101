using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves the three A/R-relieving dispositions (write-off, credit note, credit application) emit one
/// Invoice-dimensioned A/R line PER allocation — same recipe shape as payments (Task 4). Refund relieves
/// no A/R and is out of scope. A/R now requires {Customer, Invoice} (flipped in Task 6), so the Invoice
/// tag is load-bearing — omitting it would 422.
/// </summary>
public sealed class DispositionDimensionTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    /// <summary>Chart setup requires ManageAccounts — the single non-SoD Controller holds every capability.</summary>
    private async Task SetUpChartAsync(HttpClient http, Guid clientId)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.ReceivableAccountId}",
            new AccountRequest { Number = "1200", Name = "Accounts Receivable", Type = "Asset", RequiredDimensions = ["Customer", "Invoice"] }))
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
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.BadDebtExpenseAccountId}",
            new AccountRequest { Number = "6000", Name = "Bad Debt Expense", Type = "Expense" }))
            .EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.SalesReturnsAccountId}",
            new AccountRequest { Number = "4900", Name = "Sales Returns", Type = "Revenue" }))
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
    public async Task Writeoff_relieves_the_invoice_via_an_Invoice_tagged_AR_line()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        Customer customer = (await (await http.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne Enterprises", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice invoice = await IssueInvoiceAsync(http, clientId, customer.Id, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoice.Id);

        HttpResponseMessage woResp = await http.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer.Id, new DateOnly(2026, 3, 6), [new Allocation(invoice.Id, 40m)], "uncollectible"));
        woResp.EnsureSuccessStatusCode();
        WriteOff writeOff = (await woResp.Content.ReadFromJsonAsync<WriteOff>())!;
        await ApproveSourceEntryAsync(http, clientId, writeOff.Id);

        EntryResponse entry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={writeOff.Id}"))!);
        Assert.Equal("Posted", entry.Posting);

        EntryLineResponse ar = entry.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId);
        Assert.Equal(40m, ar.Amount);
        Assert.Equal("Credit", ar.Direction);
        Assert.Equal(invoice.Id, ar.Dimensions["Invoice"]);
        Assert.Equal(customer.Id, ar.Dimensions["Customer"]);

        SubledgerResponse fold = (await http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?dimension=Invoice"))!;
        SubledgerLineResponse byInvoice = fold.Lines.Single(
            l => l.AccountId == fixture.ReceivableAccountId && l.DimensionValue == invoice.Id);
        Assert.Equal(60m, byInvoice.Balance);
    }

    [Fact]
    public async Task Split_writeoff_emits_one_Invoice_tagged_AR_line_per_allocation()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        Customer customer = (await (await http.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne Enterprises", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice invoiceA = await IssueInvoiceAsync(http, clientId, customer.Id, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoiceA.Id);
        Invoice invoiceB = await IssueInvoiceAsync(http, clientId, customer.Id, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoiceB.Id);

        HttpResponseMessage woResp = await http.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer.Id, new DateOnly(2026, 3, 6),
                [new Allocation(invoiceA.Id, 100m), new Allocation(invoiceB.Id, 30m)], "uncollectible"));
        woResp.EnsureSuccessStatusCode();
        WriteOff writeOff = (await woResp.Content.ReadFromJsonAsync<WriteOff>())!;
        await ApproveSourceEntryAsync(http, clientId, writeOff.Id);

        EntryResponse entry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={writeOff.Id}"))!);
        List<EntryLineResponse> arLines = entry.Lines.Where(l => l.AccountId == fixture.ReceivableAccountId).ToList();
        Assert.Equal(2, arLines.Count);
        Assert.Contains(arLines, l =>
            l.Dimensions["Invoice"] == invoiceA.Id && l.Amount == 100m && l.Dimensions["Customer"] == customer.Id);
        Assert.Contains(arLines, l =>
            l.Dimensions["Invoice"] == invoiceB.Id && l.Amount == 30m && l.Dimensions["Customer"] == customer.Id);

        EntryLineResponse badDebt = entry.Lines.Single(l => l.AccountId == fixture.BadDebtExpenseAccountId);
        Assert.Equal(130m, badDebt.Amount);
        Assert.Equal("Debit", badDebt.Direction);
    }

    [Fact]
    public async Task CreditApplication_relieves_the_invoice_via_an_Invoice_tagged_AR_line()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        Customer customer = (await (await http.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne Enterprises", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        // Over-pay a small invoice to generate a customer credit balance.
        Invoice fundingInvoice = await IssueInvoiceAsync(http, clientId, customer.Id, 50m);
        await ApproveSourceEntryAsync(http, clientId, fundingInvoice.Id);
        HttpResponseMessage payResp = await http.PostAsJsonAsync($"/clients/{clientId}/payments",
            new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 2), 100m, "check",
                [new Allocation(fundingInvoice.Id, 50m)]));
        payResp.EnsureSuccessStatusCode();
        Payment payment = (await payResp.Content.ReadFromJsonAsync<Payment>())!;
        await ApproveSourceEntryAsync(http, clientId, payment.Id);

        Invoice invoice = await IssueInvoiceAsync(http, clientId, customer.Id, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoice.Id);

        HttpResponseMessage caResp = await http.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
            new CreditApplicationRequest(customer.Id, new DateOnly(2026, 3, 10), [new Allocation(invoice.Id, 40m)]));
        caResp.EnsureSuccessStatusCode();
        CreditApplication creditApp = (await caResp.Content.ReadFromJsonAsync<CreditApplication>())!;
        await ApproveSourceEntryAsync(http, clientId, creditApp.Id);

        EntryResponse entry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={creditApp.Id}"))!);
        EntryLineResponse ar = entry.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId);
        Assert.Equal(40m, ar.Amount);
        Assert.Equal("Credit", ar.Direction);
        Assert.Equal(invoice.Id, ar.Dimensions["Invoice"]);
        Assert.Equal(customer.Id, ar.Dimensions["Customer"]);

        SubledgerResponse fold = (await http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?dimension=Invoice"))!;
        SubledgerLineResponse byInvoice = fold.Lines.Single(
            l => l.AccountId == fixture.ReceivableAccountId && l.DimensionValue == invoice.Id);
        Assert.Equal(60m, byInvoice.Balance);
    }

    [Fact]
    public async Task CreditNote_relieves_the_invoice_via_an_Invoice_tagged_AR_line()
    {
        (Guid clientId, HttpClient http) = await fixture.SeedClientAsync();
        await SetUpChartAsync(http, clientId);

        Customer customer = (await (await http.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne Enterprises", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice invoice = await IssueInvoiceAsync(http, clientId, customer.Id, 100m);
        await ApproveSourceEntryAsync(http, clientId, invoice.Id);

        HttpResponseMessage cnResp = await http.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 6), [new Allocation(invoice.Id, 25m)], "returned goods"));
        cnResp.EnsureSuccessStatusCode();
        CreditNote creditNote = (await cnResp.Content.ReadFromJsonAsync<CreditNote>())!;
        await ApproveSourceEntryAsync(http, clientId, creditNote.Id);

        EntryResponse entry = Assert.Single((await http.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={creditNote.Id}"))!);
        EntryLineResponse ar = entry.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId);
        Assert.Equal(25m, ar.Amount);
        Assert.Equal("Credit", ar.Direction);
        Assert.Equal(invoice.Id, ar.Dimensions["Invoice"]);
        Assert.Equal(customer.Id, ar.Dimensions["Customer"]);

        SubledgerResponse fold = (await http.GetFromJsonAsync<SubledgerResponse>(
            $"/clients/{clientId}/subledger?dimension=Invoice"))!;
        SubledgerLineResponse byInvoice = fold.Lines.Single(
            l => l.AccountId == fixture.ReceivableAccountId && l.DimensionValue == invoice.Id);
        Assert.Equal(75m, byInvoice.Balance);
    }
}
