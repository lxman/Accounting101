using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

/// <summary>
/// The invoice lifecycle against in-memory fakes: issuing a draft finalizes it (assigning a number),
/// posts and approves the composed A/R entry; voiding reverses that entry by its source back-link; and
/// the status guards hold. Number and status are derived from the (faked) store's lifecycle.
/// </summary>
public sealed class InvoiceServiceTests
{
    private static readonly InvoicePostingAccounts Accounts = new()
    {
        ReceivableAccountId = Guid.NewGuid(),
        RevenueAccountId = Guid.NewGuid(),
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

        IReadOnlyList<EntryResponse> onBooks = await h.Ledger.GetEntriesBySourceRefAsync(client, draft.Id);
        Assert.Equal("Posted", Assert.Single(onBooks).Posting);
    }

    [Fact]
    public async Task Voiding_an_issued_invoice_reverses_its_entry()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(client, customer.Id, OneLine(100m), taxRate: 0m, new DateOnly(2026, 3, 31));
        await h.Service.IssueAsync(client, draft.Id);

        Invoice voided = await h.Service.VoidAsync(client, draft.Id, "duplicate");

        Assert.Equal(InvoiceStatus.Void, voided.Status);
        IReadOnlyList<EntryResponse> entries = await h.Ledger.GetEntriesBySourceRefAsync(client, draft.Id);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.ReversalOf is not null);
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
