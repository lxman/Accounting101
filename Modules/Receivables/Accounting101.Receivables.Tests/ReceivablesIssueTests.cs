using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Proves the maker-checker flow under SoD-ON: a Clerk issues an invoice (the A/R entry lands
/// PendingApproval — not yet on the books), then a SEPARATE Approver approves it (SoD satisfied),
/// at which point the entry is Posted and the A/R-by-customer subledger ties out.
/// </summary>
public sealed class ReceivablesIssueTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    /// <summary>Chart setup requires ManageAccounts — only the Controller holds that permission.</summary>
    private async Task SetUpChartAsync(HttpClient controllerHttp, Guid clientId)
    {
        // A/R is a control account requiring the Customer dimension; Revenue and Sales Tax Payable are plain.
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
    public async Task Issuing_a_license_bearing_invoice_splits_revenue_natively_no_reclass()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        // Add the Software License Revenue account the "License" category maps to.
        (await controller.PutAsJsonAsync($"/clients/{clientId}/accounts/{fixture.LicenseRevenueAccountId}",
            new AccountRequest { Number = "4100", Name = "Software License Revenue", Type = "Revenue" }))
            .EnsureSuccessStatusCode();

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark Industries", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        // Consulting (default revenue, exempt) + a software license (License category, taxable).
        DraftInvoiceRequest draftRequest = new(
            customer.Id,
            [
                new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 9250m, Taxable = false },
                new InvoiceLine { Description = "Software license", Quantity = 1m, UnitPrice = 2000m, RevenueCategory = "License" },
            ],
            TaxRate: 0.08m, IssueDate: new DateOnly(2026, 3, 31), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;

        (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null)).EnsureSuccessStatusCode();

        EntryResponse arEntry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!);
        (await approver.PostAsync($"/clients/{clientId}/entries/{arEntry.Id}/approve", null)).EnsureSuccessStatusCode();

        // ONE entry, revenue split across the two accounts directly — no reclass entry exists.
        EntryResponse entry = Assert.Single((await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!);
        Assert.Equal(9250m, entry.Lines.Single(l => l.AccountId == fixture.RevenueAccountId).Amount);          // consulting -> default
        Assert.Equal(2000m, entry.Lines.Single(l => l.AccountId == fixture.LicenseRevenueAccountId).Amount);   // license -> 4100, natively
        Assert.Equal(160m, entry.Lines.Single(l => l.AccountId == fixture.SalesTaxPayableAccountId).Amount);
        Assert.Equal(11410m, entry.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId).Amount);
    }

    [Fact]
    public async Task Issuing_then_a_separate_approver_books_the_AR_entry_under_SoD()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();

        // Chart setup must run as the Controller — the Clerk has Post/Revise but not ManageAccounts.
        await SetUpChartAsync(controller, clientId);

        // Clerk creates the customer + drafts + issues.
        // The receivables endpoints forward the caller's token; the loopback ledger call runs as the Clerk.
        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Beta LLC", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        DraftInvoiceRequest draftRequest = new(
            customer.Id,
            [new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 100m }],
            TaxRate: 0.07m, IssueDate: new DateOnly(2026, 3, 31), DueDate: null, Memo: null);
        HttpResponseMessage draftResponse = await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest);
        Assert.Equal(HttpStatusCode.Created, draftResponse.StatusCode);
        Invoice draft = (await draftResponse.Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Draft, draft.Status);

        HttpResponseMessage issueResponse = await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null);
        string issueBody = await issueResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"Issued\"", issueBody, StringComparison.OrdinalIgnoreCase);

        Invoice issued = System.Text.Json.JsonSerializer.Deserialize<Invoice>(issueBody,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(InvoiceStatus.Issued, issued.Status);
        Assert.NotNull(issued.Number);

        // The A/R entry is PendingApproval — the Clerk issued but SoD prevents self-approve.
        EntryResponse[] pending = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!;
        EntryResponse arEntry = Assert.Single(pending);
        Assert.Equal("PendingApproval", arEntry.Posting);

        // A DIFFERENT actor approves — SoD is satisfied; this is the whole point of the fix.
        (await approver.PostAsync($"/clients/{clientId}/entries/{arEntry.Id}/approve", null))
            .EnsureSuccessStatusCode();

        // Now it's on the books: entry is Posted and the subledger ties out.
        EntryResponse[] posted = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!;
        EntryResponse postedEntry = Assert.Single(posted);
        Assert.Equal("Posted", postedEntry.Posting);
        EntryLineResponse ar = postedEntry.Lines.Single(l => l.AccountId == fixture.ReceivableAccountId);
        Assert.Equal(107m, ar.Amount);
        Assert.Equal(customer.Id, ar.Dimensions["Customer"]);

        // The A/R-by-customer subledger ties out to the control account.
        SubledgerReconciliationResponse recon = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(recon.TiesOut);
    }
}
