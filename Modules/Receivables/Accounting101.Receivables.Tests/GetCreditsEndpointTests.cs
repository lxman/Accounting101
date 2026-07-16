using System.Net;
using System.Net.Http.Json;
using Accounting101.Receivables.Api;
using Accounting101.Ledger.Contracts;   // AccountRequest
using Accounting101.Settlement;

namespace Accounting101.Receivables.Tests;

/// <summary>Proves the unified read endpoint that powers the UI's Credits list: it returns a customer's
/// credit-notes, write-offs, and credit-applications as one date-descending list (correct type tag,
/// amount = Σ allocations, memo for notes/write-offs and null for credit-applications) and rejects a
/// missing customerId.</summary>
public sealed class GetCreditsEndpointTests(ReceivablesHostFixture fixture) : IClassFixture<ReceivablesHostFixture>
{
    private async Task SetUpChartAsync(HttpClient controller, Guid clientId)
    {
        await PutAccountAsync(controller, clientId, fixture.ReceivableAccountId, "1100", "Accounts Receivable", "Asset", null, ["Customer", "Invoice"]);
        await PutAccountAsync(controller, clientId, fixture.RevenueAccountId, "4000", "Revenue", "Revenue", null);
        await PutAccountAsync(controller, clientId, fixture.SalesTaxPayableAccountId, "2200", "Sales Tax Payable", "Liability", null);
        await PutAccountAsync(controller, clientId, fixture.CashAccountId, "1000", "Cash", "Asset", null);
        await PutAccountAsync(controller, clientId, fixture.CustomerCreditsAccountId, "2300", "Customer Credits", "Liability", "Customer");
        await PutAccountAsync(controller, clientId, fixture.BadDebtExpenseAccountId, "6000", "Bad Debt Expense", "Expense", null);
        await PutAccountAsync(controller, clientId, fixture.SalesReturnsAccountId, "4900", "Sales Returns", "Revenue", null);
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

    private static async Task<Guid> IssueInvoiceAsync(HttpClient clerk, HttpClient approver, Guid clientId, Guid customerId, decimal amount)
    {
        DraftInvoiceRequest draftRequest = new(customerId,
            [new InvoiceLine { Description = "Services", Quantity = 1m, UnitPrice = amount, Taxable = false }],
            TaxRate: 0m, IssueDate: new DateOnly(2026, 3, 1), DueDate: null, Memo: null);
        Invoice draft = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/invoices", draftRequest))
            .Content.ReadFromJsonAsync<Invoice>())!;
        Invoice issued = (await (await clerk.PostAsync($"/clients/{clientId}/invoices/{draft.Id}/issue", null))
            .Content.ReadFromJsonAsync<Invoice>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, issued.Id);
        return issued.Id;
    }

    /// <summary>Approve every PendingApproval entry sourced from the given document — allocation validation
    /// (open balance, available credit) now folds the ledger, which only reflects Posted (approved) entries.</summary>
    private static async Task ApproveBySourceRefAsync(HttpClient reader, HttpClient approver, Guid clientId, Guid sourceRef)
    {
        EntryResponse[] entries = (await reader.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={sourceRef}"))!;
        foreach (EntryResponse e in entries.Where(e => e.Posting == "PendingApproval"))
            (await approver.PostAsync($"/clients/{clientId}/entries/{e.Id}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GET_credits_returns_unified_date_descending_list_with_memo()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv1 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);   // → write-off
        Guid inv2 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);   // → credit-note
        Guid inv3 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);   // → overpaid (creates credit)
        Guid inv4 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);   // → credit-application target

        // Overpay inv3 by 50 → 50 of unapplied customer credit.
        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 2), 150m, "check",
                    [new Allocation(inv3, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        WriteOff writeOff = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/write-offs",
            new WriteOffRequest(customer.Id, new DateOnly(2026, 3, 5), [new Allocation(inv1, 100m)], "uncollectible")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<WriteOff>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, writeOff.Id);
        CreditApplication creditApp = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
            new CreditApplicationRequest(customer.Id, new DateOnly(2026, 3, 8), [new Allocation(inv4, 50m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditApp.Id);
        CreditNote creditNote = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 10), [new Allocation(inv2, 100m)], "returned goods")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreditNote>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditNote.Id);

        // The Credits list is a read surface — amounts reflect only Posted relief, so every disposition
        // above is approved before we read it back.
        CreditDocument[] list = (await clerk.GetFromJsonAsync<CreditDocument[]>(
            $"/clients/{clientId}/credits?customerId={customer.Id}"))!;

        Assert.Equal(3, list.Length);                          // payment is not a credit doc
        Assert.Equal("credit-note", list[0].Type);             // 3/10 — newest first
        Assert.Equal(100m, list[0].Amount);
        Assert.Equal("returned goods", list[0].Memo);
        Assert.False(list[0].Voided);

        Assert.Equal("credit-application", list[1].Type);      // 3/8
        Assert.Equal(50m, list[1].Amount);
        Assert.Null(list[1].Memo);

        Assert.Equal("write-off", list[2].Type);               // 3/5
        Assert.Equal(100m, list[2].Amount);
        Assert.Equal("uncollectible", list[2].Memo);
    }

    [Fact]
    public async Task GET_credits_includes_voided_dispositions()
    {
        // Regression test: voided credit-notes must appear in GET /credits with Voided==true.
        // Before the fix, QueryAsync excluded voided docs by default and they vanished.
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);

        // Record a credit-note as clerk.
        CreditNote cn = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 10), [new Allocation(inv, 100m)], "issued in error")))
            .Content.ReadFromJsonAsync<CreditNote>())!;

        // Void requires the Controller — module document write (ar.write) plus GL void/reverse.
        (await controller.PostAsync($"/clients/{clientId}/credit-notes/{cn.Id}/void", null)).EnsureSuccessStatusCode();

        CreditDocument[] list = (await clerk.GetFromJsonAsync<CreditDocument[]>(
            $"/clients/{clientId}/credits?customerId={customer.Id}"))!;

        // The voided credit-note must be present (not silently dropped).
        CreditDocument? voided = list.FirstOrDefault(d => d.Id == cn.Id);
        Assert.NotNull(voided);
        Assert.Equal("credit-note", voided.Type);
        Assert.True(voided.Voided);
    }

    [Fact]
    public async Task GET_credits_without_customerId_is_400()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/credits");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GET_credit_note_by_id_returns_folded_amount_allocations_and_journal_entry_id()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Stark", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv1 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);
        Guid inv2 = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);

        // A credit note allocated across two invoices: 60 to inv1, 40 to inv2 (total 100).
        CreditNote creditNote = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 10),
                [new Allocation(inv1, 60m), new Allocation(inv2, 40m)], "returned goods")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreditNote>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditNote.Id);

        InvoiceView iv1 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{inv1}"))!;
        InvoiceView iv2 = (await clerk.GetFromJsonAsync<InvoiceView>($"/clients/{clientId}/invoices/{inv2}"))!;

        EntryResponse[] entries = (await clerk.GetFromJsonAsync<EntryResponse[]>(
            $"/clients/{clientId}/entries?sourceRef={creditNote.Id}"))!;
        Guid expectedEntryId = entries.Single(e => e is { Status: "Active", ReversalOf: null }).Id;

        CreditView view = (await clerk.GetFromJsonAsync<CreditView>(
            $"/clients/{clientId}/credits/credit-note/{creditNote.Id}"))!;

        Assert.Equal("credit-note", view.Credit.Type);
        Assert.Equal(creditNote.Id, view.Credit.Id);
        Assert.Equal(100m, view.Credit.Amount);
        Assert.Equal("returned goods", view.Credit.Memo);
        Assert.False(view.Credit.Voided);
        Assert.Equal(expectedEntryId, view.JournalEntryId);

        Assert.Equal(2, view.Allocations.Count);
        InvoiceAllocationLine a1 = view.Allocations.Single(a => a.InvoiceId == inv1);
        Assert.Equal(60m, a1.Amount);
        Assert.Equal(iv1.Invoice.Number, a1.InvoiceNumber);
        InvoiceAllocationLine a2 = view.Allocations.Single(a => a.InvoiceId == inv2);
        Assert.Equal(40m, a2.Amount);
        Assert.Equal(iv2.Invoice.Number, a2.InvoiceNumber);
    }

    [Fact]
    public async Task GET_credit_application_by_id_has_null_memo_and_resolved_allocations()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Wayne", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid invSrc = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);     // overpaid → credit
        Guid invTarget = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);  // credit-application target

        // Overpay invSrc by 50 → 50 of unapplied customer credit.
        Payment payment = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/payments",
                new RecordPaymentRequest(customer.Id, new DateOnly(2026, 3, 2), 150m, "check",
                    [new Allocation(invSrc, 100m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Payment>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, payment.Id);

        CreditApplication creditApp = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-applications",
                new CreditApplicationRequest(customer.Id, new DateOnly(2026, 3, 8), [new Allocation(invTarget, 50m)])))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreditApplication>())!;
        await ApproveBySourceRefAsync(clerk, approver, clientId, creditApp.Id);

        CreditView view = (await clerk.GetFromJsonAsync<CreditView>(
            $"/clients/{clientId}/credits/credit-application/{creditApp.Id}"))!;

        Assert.Equal("credit-application", view.Credit.Type);
        Assert.Null(view.Credit.Memo);
        Assert.Equal(50m, view.Credit.Amount);
        InvoiceAllocationLine only = Assert.Single(view.Allocations);
        Assert.Equal(invTarget, only.InvoiceId);
        Assert.Equal(50m, only.Amount);
    }

    [Fact]
    public async Task GET_credit_before_approval_shows_no_amount_or_allocations()
    {
        (Guid clientId, HttpClient controller, HttpClient clerk, HttpClient approver) = await fixture.SeedSodClientAsync();
        await SetUpChartAsync(controller, clientId);
        Customer customer = (await (await clerk.PostAsJsonAsync(
            $"/clients/{clientId}/customers", new CreateCustomerRequest("Kent", null)))
            .Content.ReadFromJsonAsync<Customer>())!;

        Guid inv = await IssueInvoiceAsync(clerk, approver, clientId, customer.Id, 100m);

        // Post a credit note as clerk but DO NOT approve it — it is Active but PendingApproval (not on the books).
        CreditNote creditNote = (await (await clerk.PostAsJsonAsync($"/clients/{clientId}/credit-notes",
            new CreditNoteRequest(customer.Id, new DateOnly(2026, 3, 10), [new Allocation(inv, 100m)], "returned goods")))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreditNote>())!;

        CreditView view = (await clerk.GetFromJsonAsync<CreditView>(
            $"/clients/{clientId}/credits/credit-note/{creditNote.Id}"))!;

        // Not yet on the books: amount 0, no allocations, no journal drill — consistent with the list's
        // Posted-only fold ("a document's relief must not show before its own posting does").
        Assert.Equal(0m, view.Credit.Amount);
        Assert.Empty(view.Allocations);
        Assert.Null(view.JournalEntryId);
    }

    [Fact]
    public async Task GET_credit_by_unknown_id_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/credits/credit-note/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GET_credit_by_unknown_type_is_404()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage res = await clerk.GetAsync($"/clients/{clientId}/credits/bogus/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
