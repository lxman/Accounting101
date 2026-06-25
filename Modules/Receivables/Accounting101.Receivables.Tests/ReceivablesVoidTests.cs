using System.Net.Http.Json;
using Accounting101.Receivables;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// Void paths under SoD-ON:
/// <list type="bullet">
///   <item><b>Void-of-posted</b>: Clerk issues → Approver approves (entry becomes Posted) → Approver voids
///   the invoice (reversal entry created, PendingApproval) → Approver approves the reversal → net is zero.</item>
///   <item><b>Void-of-pending</b>: Clerk issues → Approver voids BEFORE approval → the pending entry is
///   withdrawn (Voided) with no reversal created; nothing ever hit the books.</item>
/// </list>
/// </summary>
public sealed class ReceivablesVoidTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
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

    /// <summary>Issue a draft invoice as the Clerk and return the draft id.</summary>
    private static async Task<Invoice> IssueAsync(HttpClient clerk, Guid clientId, Customer customer)
    {
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices",
                new DraftInvoiceRequest(customer.Id,
                    [new InvoiceLine { Description = "Work", Quantity = 1m, UnitPrice = 100m }],
                    0m, new DateOnly(2026, 3, 31), null, null)))
            .Content.ReadFromJsonAsync<Invoice>())!;

        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Issued, issued.Status);
        return draft; // callers need the draft id (= the SourceRef) for entry lookup
    }

    [Fact]
    public async Task Voiding_a_posted_invoice_creates_a_reversal_that_nets_to_zero_after_approval()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Gamma SoD", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice draft = await IssueAsync(clerk, clientId, customer);

        // Approve the A/R entry so it lands on the books (Posted).
        EntryResponse[] pending = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!;
        EntryResponse arEntry = Assert.Single(pending);
        Assert.Equal("PendingApproval", arEntry.Posting);
        (await approver.PostAsync($"/clients/{clientId}/entries/{arEntry.Id}/approve", null))
            .EnsureSuccessStatusCode();

        // Verify entry is now Posted.
        EntryResponse[] postedEntries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!;
        Assert.Equal("Posted", Assert.Single(postedEntries).Posting);

        // Approver voids the invoice — the service reverses the posted entry; reversal lands PendingApproval.
        // The Approver's token is forwarded: it has Reverse permission, so the loopback call succeeds.
        Invoice voided = (await (await approver.PostAsJsonAsync(
                $"/clients/{clientId}/invoices/{draft.Id}/void", new VoidInvoiceRequest("duplicate")))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Void, voided.Status);

        // Both the original entry and the reversal are returned by SourceRef (reversal inherits SourceRef).
        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!;
        Assert.Equal(2, entries.Length);
        EntryResponse reversal = Assert.Single(entries, e => e.ReversalOf is not null);
        Assert.Equal("PendingApproval", reversal.Posting); // reversal awaits its own approval under SoD

        // The reversal was created by the Approver (via the loopback call). Under SoD the author cannot
        // approve their own entry. The Controller is a distinct actor and has Approve permission.
        (await controller.PostAsync($"/clients/{clientId}/entries/{reversal.Id}/approve", null))
            .EnsureSuccessStatusCode();

        SubledgerReconciliationResponse recon = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(recon.TiesOut);
        Assert.Equal(0m, recon.ControlBalance); // nothing on the books — original + reversal net to zero
    }

    [Fact]
    public async Task Voiding_a_pending_invoice_withdraws_the_entry_with_no_reversal()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) =
            await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);

        Customer customer = (await (await clerk.PostAsJsonAsync(
                $"/clients/{clientId}/customers", new CreateCustomerRequest("Delta SoD", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Invoice draft = await IssueAsync(clerk, clientId, customer);

        // The entry is PendingApproval — NOT yet on the books.
        EntryResponse[] beforeVoid = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!;
        EntryResponse arEntry = Assert.Single(beforeVoid);
        Assert.Equal("PendingApproval", arEntry.Posting);

        // Approver voids the invoice BEFORE approval — the service withdraws (Voids) the pending entry;
        // no reversal is created because nothing was ever on the books.
        // Approver has Void permission; the forwarded token is accepted by the ledger's VoidEntry endpoint.
        Invoice voided = (await (await approver.PostAsJsonAsync(
                $"/clients/{clientId}/invoices/{draft.Id}/void", new VoidInvoiceRequest("cancelled")))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Assert.Equal(InvoiceStatus.Void, voided.Status);

        // Still exactly one entry (the original, now Voided) — no reversal was created.
        EntryResponse[] afterVoid = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={draft.Id}"))!;
        EntryResponse withdrawn = Assert.Single(afterVoid);
        Assert.Null(withdrawn.ReversalOf); // no reversal: the entry was withdrawn, not reversed
        Assert.Equal("Voided", withdrawn.Status);

        // The books are clean — the entry never posted, so the subledger is empty and tied out at zero.
        SubledgerReconciliationResponse recon = (await clerk.GetFromJsonAsync<SubledgerReconciliationResponse>(
            $"/clients/{clientId}/subledger/reconciliation?account={fixture.ReceivableAccountId}&dimension=Customer"))!;
        Assert.True(recon.TiesOut);
        Assert.Equal(0m, recon.ControlBalance);
    }
}
