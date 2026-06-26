using System.Net.Http.Json;
using Accounting101.Ledger.Contracts;
using Accounting101.Receivables;
using Accounting101.Receivables.Api;
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves the end-to-end module-credential path for Receivables (Task 2 of the module-poster-identity
/// slice). When Receivables issues an invoice or records a payment it POSTs to the engine's ledger
/// endpoint; the engine stamps <c>ViaModule = "receivables"</c> on the resulting entry.
///
/// RED before Task 2: <c>HttpLedgerClient.PostAsync</c> and <c>ValidateAsync</c> do not yet attach
/// <c>X-Module-Key</c> / <c>X-Module-Secret</c>, so the raw path is used and <c>ViaModule</c> is null.
/// GREEN after Task 2: the credential headers are forwarded and the stamp is set on both paths.
/// </summary>
public sealed class ModuleViaReceivablesTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId,      "1100", "Accounts Receivable", "Asset",     "Customer");
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId,         "4000", "Revenue",             "Revenue",   null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable",   "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId,            "1000", "Cash",                "Asset",     null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits",    "Liability", "Customer");
    }

    private static async Task PutAccountAsync(HttpClient http, Guid clientId, Guid accountId,
        string number, string name, string type, string? requiredDimension)
    {
        (await http.PutAsJsonAsync($"/clients/{clientId}/accounts/{accountId}",
            new AccountRequest { Number = number, Name = name, Type = type, RequiredDimension = requiredDimension }))
            .EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient clerk, Guid clientId)
    {
        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("ViaModuleCo", null)))
            .Content.ReadFromJsonAsync<Customer>())!;
        return customer.Id;
    }

    private static async Task<Guid> IssueInvoiceAsync(
        HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draftRequest = new(
            customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, issued.Id);
        return issued.Id;
    }

    private static async Task ApproveBySourceRefAsync(
        HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Issue an invoice via the Receivables module and verify the resulting engine journal entry carries
    /// <c>ViaModule = "receivables"</c>. Before Task 2 this is null (RED); after it is "receivables" (GREEN).
    /// The issue path exercises both <c>ValidateAsync</c> (pre-flight dry-run) and <c>PostAsync</c>.
    /// </summary>
    [Fact]
    public async Task Issuing_an_invoice_stamps_ViaModule_receivables()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 500m);

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={invoice}"))!;

        Assert.Single(entries);

        // KEY ASSERTION: the engine must stamp ViaModule = "receivables" because the client sent
        // X-Module-Key / X-Module-Secret alongside the forwarded user token.
        Assert.Equal("receivables", entries[0].ViaModule);
    }

    /// <summary>
    /// Record a payment via the Receivables module and verify the resulting engine journal entry carries
    /// <c>ViaModule = "receivables"</c>. Proves the payment (cash-application) posting path is also
    /// credentialed, not just the invoice-issue path.
    /// </summary>
    [Fact]
    public async Task Recording_a_payment_stamps_ViaModule_receivables()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Guid customer = await CreateCustomerAsync(clerk, clientId);
        Guid invoice = await IssueInvoiceAsync(clerk, approver, clientId, customer, 500m);

        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer, new DateOnly(2026, 3, 5), 500m, "wire", [new Allocation(invoice, 500m)])))
            .Content.ReadFromJsonAsync<Payment>())!;

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={payment.Id}"))!;

        Assert.Single(entries);

        // KEY ASSERTION: payment entries must also carry ViaModule = "receivables".
        Assert.Equal("receivables", entries[0].ViaModule);
    }
}
