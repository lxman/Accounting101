using Accounting101.Invoicing;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing.Tests;

/// <summary>
/// The invoice lifecycle against an in-memory engine: issuing a draft posts and approves the composed
/// A/R entry and flips the invoice to Issued; voiding finds that entry by its source back-link and
/// reverses it; and the status guards hold (no issuing a non-draft, no voiding a non-issued invoice).
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
            new FakeInvoiceNumbers(),
            new FixedAccountsProvider(Accounts),
            ledger);
        return new Harness(service, ledger);
    }

    private static IReadOnlyList<InvoiceLine> OneLine(decimal amount) =>
        [new InvoiceLine { Description = "Work", Quantity = 1m, UnitPrice = amount }];

    [Fact]
    public async Task Issuing_a_draft_posts_the_composed_entry_and_marks_it_issued()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(client, customer.Id, OneLine(100m), taxRate: 0.07m, new DateOnly(2026, 3, 31));

        Invoice issued = await h.Service.IssueAsync(client, draft.Id);

        Assert.Equal(InvoiceStatus.Issued, issued.Status);

        // Exactly one entry was posted, and it is the recipe's output, tagged and back-linked.
        PostEntryRequest entry = Assert.Single(h.Ledger.Posted);
        Assert.Equal(draft.Id, entry.SourceRef);
        Assert.Equal("Invoice", entry.SourceType);
        PostLineRequest ar = entry.Lines.Single(l => l.AccountId == Accounts.ReceivableAccountId);
        Assert.Equal(107m, ar.Amount);                          // 100 + 7 tax
        Assert.Equal(customer.Id, ar.Dimensions!["Customer"]);

        // It was approved onto the books.
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

        // The invoice's source now resolves to two entries — the original and its reversal.
        IReadOnlyList<EntryResponse> entries = await h.Ledger.GetEntriesBySourceRefAsync(client, draft.Id);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.ReversalOf is not null); // the reversal inherited the source link
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
