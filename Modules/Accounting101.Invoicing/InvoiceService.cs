using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

/// <summary>
/// The invoicing lifecycle: draft an invoice, issue it (which posts and approves the A/R entry through
/// the engine), and void it (which reverses that entry). The module owns its documents; the engine owns
/// the ledger. The link between them is the source back-link — void finds the entry an invoice produced
/// by querying the engine for it, rather than storing the entry id, so the two can't drift.
/// </summary>
public sealed class InvoiceService(
    IInvoiceStore invoices,
    ICustomerStore customers,
    IInvoiceNumbers numbers,
    IInvoiceAccountsProvider accounts,
    ILedgerClient ledger)
{
    public async Task<Customer> CreateCustomerAsync(
        Guid clientId, string name, string? email = null, CancellationToken cancellationToken = default)
    {
        Customer customer = new() { Id = Guid.NewGuid(), Name = name, Email = email };
        await customers.SaveAsync(clientId, customer, cancellationToken);
        return customer;
    }

    /// <summary>Create a draft invoice. It is assigned a number but has no ledger effect until issued.</summary>
    public async Task<Invoice> DraftAsync(
        Guid clientId, Guid customerId, IReadOnlyList<InvoiceLine> lines, decimal taxRate,
        DateOnly issueDate, DateOnly? dueDate = null, string? memo = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (await customers.GetAsync(clientId, customerId, cancellationToken) is null)
            throw new InvalidOperationException($"Customer {customerId} does not exist.");
        if (lines.Count == 0)
            throw new InvalidOperationException("An invoice needs at least one line.");

        Invoice invoice = new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Number = await numbers.NextAsync(clientId, cancellationToken),
            IssueDate = issueDate,
            DueDate = dueDate,
            Status = InvoiceStatus.Draft,
            TaxRate = taxRate,
            Memo = memo,
            Lines = lines,
        };
        await invoices.SaveAsync(clientId, invoice, cancellationToken);
        return invoice;
    }

    /// <summary>
    /// Issue a draft: compose its entry, post it, and approve it onto the books. The A/R-by-customer
    /// subledger and reconciliation light up from the engine the moment this commits.
    /// </summary>
    public async Task<Invoice> IssueAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        Invoice invoice = await RequireAsync(clientId, invoiceId, cancellationToken);
        if (invoice.Status != InvoiceStatus.Draft)
            throw new InvalidOperationException($"Only a draft invoice can be issued; {invoice.Number} is {invoice.Status}.");
        if (invoice.Total <= 0m)
            throw new InvalidOperationException($"Invoice {invoice.Number} must total more than zero to be issued.");

        InvoicePostingAccounts postingAccounts = await accounts.GetAsync(clientId, cancellationToken);
        PostEntryRequest entry = InvoicePosting.Compose(invoice, postingAccounts);

        PostEntryResponse posted = await ledger.PostAsync(clientId, entry, cancellationToken);
        await ledger.ApproveAsync(clientId, posted.Id, cancellationToken);

        Invoice issued = invoice with { Status = InvoiceStatus.Issued };
        await invoices.SaveAsync(clientId, issued, cancellationToken);
        return issued;
    }

    /// <summary>
    /// Void an issued invoice: find the entry it produced (by source back-link) and reverse it. The engine
    /// refuses a second reversal, so a double-void can't over-correct.
    /// </summary>
    public async Task<Invoice> VoidAsync(
        Guid clientId, Guid invoiceId, string? reason = null, CancellationToken cancellationToken = default)
    {
        Invoice invoice = await RequireAsync(clientId, invoiceId, cancellationToken);
        if (invoice.Status != InvoiceStatus.Issued)
            throw new InvalidOperationException($"Only an issued invoice can be voided; {invoice.Number} is {invoice.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, invoice.Id, cancellationToken);
        EntryResponse issueEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null })
            ?? throw new InvalidOperationException($"No posted entry found for invoice {invoice.Number} to reverse.");

        EntryResponse reversal = await ledger.ReverseAsync(
            clientId, issueEntry.Id, new ReverseRequest(invoice.IssueDate, reason ?? $"Voided invoice {invoice.Number}"), cancellationToken);
        await ledger.ApproveAsync(clientId, reversal.Id, cancellationToken);

        Invoice voided = invoice with { Status = InvoiceStatus.Void };
        await invoices.SaveAsync(clientId, voided, cancellationToken);
        return voided;
    }

    private async Task<Invoice> RequireAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken) =>
        await invoices.GetAsync(clientId, invoiceId, cancellationToken)
        ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");
}
