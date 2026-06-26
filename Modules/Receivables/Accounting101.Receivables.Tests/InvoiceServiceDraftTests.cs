namespace Accounting101.Receivables.Tests;

/// <summary>
/// Unit tests for InvoiceService.EditDraftAsync and InvoiceService.DiscardDraftAsync —
/// both delegate to the store after re-running the same validation as DraftAsync.
/// Uses the same in-memory fakes harness as InvoiceServiceTests.
/// </summary>
public sealed class InvoiceServiceDraftTests
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

    private static IReadOnlyList<InvoiceLine> OneLine(string description = "Work", decimal amount = 100m) =>
        [new InvoiceLine { Description = description, Quantity = 1m, UnitPrice = amount }];

    // 1. EditDraftAsync persists the updated body — a subsequent GET shows the new fields.
    [Fact]
    public async Task EditDraftAsync_updates_body_and_get_reflects_new_fields()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(
            client, customer.Id, OneLine("Original Work", 100m), taxRate: 0m,
            new DateOnly(2026, 1, 31));

        IReadOnlyList<InvoiceLine> newLines =
        [
            new InvoiceLine { Description = "Revised Work", Quantity = 2m, UnitPrice = 75m },
        ];
        Invoice edited = await h.Service.EditDraftAsync(
            client, draft.Id, customer.Id, newLines, taxRate: 0.08m,
            new DateOnly(2026, 2, 28), dueDate: new DateOnly(2026, 3, 28), memo: "Updated memo");

        Assert.Equal(InvoiceStatus.Draft, edited.Status);
        Assert.Null(edited.Number);
        Assert.Equal(new DateOnly(2026, 2, 28), edited.IssueDate);
        Assert.Equal(new DateOnly(2026, 3, 28), edited.DueDate);
        Assert.Equal("Updated memo", edited.Memo);
        Assert.Single(edited.Lines);
        Assert.Equal("Revised Work", edited.Lines[0].Description);
        Assert.Equal(2m, edited.Lines[0].Quantity);
        Assert.Equal(75m, edited.Lines[0].UnitPrice);

        Invoice? readBack = await h.Service.GetAsync(client, draft.Id);
        Assert.NotNull(readBack);
        Assert.Equal("Revised Work", readBack.Lines[0].Description);
    }

    // 2. EditDraftAsync on an issued invoice id throws — must mention void and re-issue.
    [Fact]
    public async Task EditDraftAsync_on_issued_invoice_throws()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(
            client, customer.Id, OneLine(), taxRate: 0m, new DateOnly(2026, 1, 31));
        Invoice issued = await h.Service.IssueAsync(client, draft.Id);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.EditDraftAsync(
                client, issued.Id, customer.Id, OneLine("Changed", 50m), taxRate: 0m,
                new DateOnly(2026, 2, 28)));

        // The store's guard message must indicate the invoice is not an editable draft.
        Assert.Contains("not", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // 3a. EditDraftAsync with zero lines throws.
    [Fact]
    public async Task EditDraftAsync_with_zero_lines_throws()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(
            client, customer.Id, OneLine(), taxRate: 0m, new DateOnly(2026, 1, 31));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.EditDraftAsync(
                client, draft.Id, customer.Id, [], taxRate: 0m, new DateOnly(2026, 2, 28)));
    }

    // 3b. EditDraftAsync with an unknown customer throws.
    [Fact]
    public async Task EditDraftAsync_with_unknown_customer_throws()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(
            client, customer.Id, OneLine(), taxRate: 0m, new DateOnly(2026, 1, 31));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.EditDraftAsync(
                client, draft.Id, Guid.NewGuid(), OneLine("Work", 50m), taxRate: 0m,
                new DateOnly(2026, 2, 28)));
    }

    // 4. DiscardDraftAsync removes the draft — a subsequent GET returns null.
    [Fact]
    public async Task DiscardDraftAsync_removes_draft_and_get_returns_null()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(
            client, customer.Id, OneLine(), taxRate: 0m, new DateOnly(2026, 1, 31));

        await h.Service.DiscardDraftAsync(client, draft.Id);

        Invoice? readBack = await h.Service.GetAsync(client, draft.Id);
        Assert.Null(readBack);
    }

    // 5. DiscardDraftAsync on an issued invoice throws — the store's guard prevents it.
    [Fact]
    public async Task DiscardDraftAsync_on_issued_invoice_throws()
    {
        Harness h = NewHarness();
        var client = Guid.NewGuid();
        Customer customer = await h.Service.CreateCustomerAsync(client, "Acme");
        Invoice draft = await h.Service.DraftAsync(
            client, customer.Id, OneLine(), taxRate: 0m, new DateOnly(2026, 1, 31));
        Invoice issued = await h.Service.IssueAsync(client, draft.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.DiscardDraftAsync(client, issued.Id));
    }
}
