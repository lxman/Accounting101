using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables.Tests;

/// <summary>
/// The invoice lifecycle against in-memory fakes: issuing a draft finalizes it (assigning a number)
/// and posts the A/R entry (PendingApproval — never auto-approved); voiding branches on whether the
/// entry is already on the books (reverse, stays pending) or still pending (withdraw via VoidAsync).
/// Status guards hold. Number and status are derived from the (faked) store's lifecycle.
/// </summary>
public sealed class InvoiceServiceTests
{
    private static readonly InvoicePostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(),
        DefaultRevenueAccountId = Guid.NewGuid(),
        SalesTaxPayableAccountId = Guid.NewGuid(),
    };

    private sealed record Harness(InvoiceService Service, FakeLedgerClient Ledger);

    private static Harness NewHarness()
    {
        FakeLedgerClient ledger = new();
        InvoiceService service = new(
            new InMemoryInvoiceStore(),
            new InMemoryCustomerStore(),
            new FixedAccountsProvider(Accounts),
            ledger);
        return new Harness(service, ledger);
    }

    private static IReadOnlyList<InvoiceLine> OneLine(decimal amount) =>
        [new InvoiceLine { Description = "Work", Quantity = 1m, UnitPrice = amount }];

    [Fact]
    public async Task Issuing_a_draft_finalizes_posts_and_marks_it_issued()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(client, customer.Id, OneLine(100m), taxRate: 0.07m, new DateOnly(2026, 3, 31));

        Assert.Equal(InvoiceStatus.Draft, draft.Status);
        Assert.Null(draft.Number);                              // a draft has no number

        Invoice issued = await h.Service.IssueAsync(client, draft.Id);

        Assert.Equal(InvoiceStatus.Issued, issued.Status);
        Assert.NotNull(issued.Number);                          // finalize assigned one

        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(draft.Id, entry.SourceRef);
        Assert.Equal("Invoice", entry.SourceType);
        Assert.Equal(issued.Number, entry.Reference);           // the entry carries the invoice number
        PostLineRequest ar = entry.Lines.Single(l => l.AccountId == Accounts.ReceivableAccountId);
        Assert.Equal(107m, ar.Amount);
        Assert.Equal(customer.Id, ar.Dimensions!["Customer"]);

        // Maker-checker: the entry is posted but NOT auto-approved — a separate approver books it.
        IReadOnlyList<EntryResponse> entries = await h.Ledger.GetEntriesBySourceRefAsync(client, draft.Id);
        Assert.Equal("PendingApproval", Assert.Single(entries).Posting);
    }

    [Fact]
    public async Task Issuing_posts_the_AR_entry_pending_approval_and_does_not_approve_it()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(client, customer.Id, OneLine(100m), taxRate: 0m, new DateOnly(2026, 3, 31));

        Invoice issued = await h.Service.IssueAsync(client, draft.Id);

        IReadOnlyList<EntryResponse> entries = await h.Ledger.GetEntriesBySourceRefAsync(client, issued.Id);
        EntryResponse arEntry = Assert.Single(entries);
        Assert.Equal("Active", arEntry.Status);
        Assert.Equal("PendingApproval", arEntry.Posting);       // headline change: module does NOT self-approve
        Assert.Equal(InvoiceStatus.Issued, issued.Status);
    }

    [Fact]
    public async Task Voiding_an_approved_invoice_reverses_its_entry_without_approving_the_reversal()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(client, customer.Id, OneLine(100m), taxRate: 0m, new DateOnly(2026, 3, 31));
        Invoice issued = await h.Service.IssueAsync(client, draft.Id);
        // A separate approver puts the entry on the books — this is the normal flow after Issue.
        IReadOnlyList<EntryResponse> afterIssue = await h.Ledger.GetEntriesBySourceRefAsync(client, issued.Id);
        await h.Ledger.ApproveAsync(client, afterIssue.Single().Id);

        await h.Service.VoidAsync(client, issued.Id, "duplicate");

        IReadOnlyList<EntryResponse> entries = await h.Ledger.GetEntriesBySourceRefAsync(client, issued.Id);
        Assert.Contains(entries, e => e.ReversalOf is not null);    // a reversal entry was created
        Assert.All(
            entries.Where(e => e.ReversalOf is not null),
            r => Assert.Equal("PendingApproval", r.Posting));        // reversal is NOT auto-approved
        Assert.Equal(InvoiceStatus.Void, (await h.Service.GetAsync(client, issued.Id))!.Status);
    }

    [Fact]
    public async Task Voiding_an_invoice_whose_entry_is_still_pending_withdraws_that_entry()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(client, customer.Id, OneLine(100m), taxRate: 0m, new DateOnly(2026, 3, 31));
        Invoice issued = await h.Service.IssueAsync(client, draft.Id);  // entry is pending, never approved

        await h.Service.VoidAsync(client, issued.Id, "issued in error");

        IReadOnlyList<EntryResponse> entries = await h.Ledger.GetEntriesBySourceRefAsync(client, issued.Id);
        Assert.DoesNotContain(entries, e => e.ReversalOf is not null);  // nothing reversed (was never on the books)
        Assert.Contains(entries, e => e.Status == "Voided");            // pending entry was withdrawn
        Assert.Equal(InvoiceStatus.Void, (await h.Service.GetAsync(client, issued.Id))!.Status);
    }

    [Fact]
    public async Task Issuing_into_a_closed_period_throws_LedgerClientException_and_leaves_invoice_draft()
    {
        Harness h = NewHarness();
        // Wire the fake to reject — simulates a closed-period 409 from the engine.
        h.Ledger.OnValidate = _ => throw new LedgerClientException(409, "Period is closed through 2024-09-30.");

        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(client, customer.Id, OneLine(100m), taxRate: 0m, new DateOnly(2024, 3, 31));

        // IssueAsync must throw; the document must stay Draft; nothing must be posted.
        await Assert.ThrowsAsync<LedgerClientException>(() => h.Service.IssueAsync(client, draft.Id));

        Invoice? readBack = await h.Service.GetAsync(client, draft.Id);
        Assert.NotNull(readBack);
        Assert.Equal(InvoiceStatus.Draft, readBack.Status);   // still a draft — never finalized
        Assert.Empty(h.Ledger.Posted);                        // nothing reached the ledger
    }

    [Fact]
    public async Task A_non_draft_invoice_cannot_be_issued()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(client, customer.Id, OneLine(100m), taxRate: 0m, new DateOnly(2026, 3, 31));
        await h.Service.IssueAsync(client, draft.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.IssueAsync(client, draft.Id));
    }

    [Fact]
    public async Task A_draft_invoice_cannot_be_voided()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(client, customer.Id, OneLine(100m), taxRate: 0m, new DateOnly(2026, 3, 31));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.VoidAsync(client, draft.Id));
    }
}
