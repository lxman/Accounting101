using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

/// <summary>
/// The invoicing lifecycle: draft an invoice, issue it (finalize the document — which assigns the number —
/// then post and approve the A/R entry through the engine), and void it (reverse that entry, then void the
/// document). The module owns its documents; the engine owns the ledger. The link between them is the
/// source back-link — void finds the entry an invoice produced by querying the engine for it.
/// </summary>
public sealed class InvoiceService(
    IInvoiceStore invoices,
    ICustomerStore customers,
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

    /// <summary>Create a draft invoice. It has no number and no ledger effect until issued.</summary>
    public async Task<Invoice> DraftAsync(
        Guid clientId, Guid customerId, IReadOnlyList<InvoiceLine> lines, decimal taxRate,
        DateOnly issueDate, DateOnly? dueDate = null, string? memo = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (await customers.GetAsync(clientId, customerId, cancellationToken) is null)
            throw new InvalidOperationException($"Customer {customerId} does not exist.");
        if (lines.Count == 0)
            throw new InvalidOperationException("An invoice needs at least one line.");

        InvoiceBody body = new(
            customerId, issueDate, dueDate, taxRate, memo,
            lines.Select(l => new LineBody(l.Description, l.Quantity, l.UnitPrice, l.Taxable)).ToList());

        return await invoices.CreateDraftAsync(clientId, body, cancellationToken);
    }

    /// <summary>
    /// Issue a draft: finalize it (assigns the number), then compose, post, and approve its A/R entry.
    /// The total-must-be-positive check runs before finalize, since finalize cannot be undone.
    /// </summary>
    public async Task<Invoice> IssueAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        Invoice draft = await RequireAsync(clientId, invoiceId, cancellationToken);
        if (draft.Status != InvoiceStatus.Draft)
            throw new InvalidOperationException($"Only a draft invoice can be issued; {invoiceId} is {draft.Status}.");
        if (draft.Total <= 0m)
            throw new InvalidOperationException($"Invoice {invoiceId} must total more than zero to be issued.");

        Invoice issued = await invoices.FinalizeAsync(clientId, invoiceId, cancellationToken);

        InvoicePostingAccounts postingAccounts = await accounts.GetAsync(clientId, cancellationToken);
        PostEntryRequest entry = InvoicePosting.Compose(issued, postingAccounts);
        PostEntryResponse posted = await ledger.PostAsync(clientId, entry, cancellationToken);
        await ledger.ApproveAsync(clientId, posted.Id, cancellationToken);

        return issued;
    }

    /// <summary>
    /// Void an issued invoice: find the entry it produced (by source back-link), reverse and approve that,
    /// then void the document. Correct the books before marking the source.
    /// </summary>
    public async Task<Invoice> VoidAsync(
        Guid clientId, Guid invoiceId, string? reason = null, CancellationToken cancellationToken = default)
    {
        Invoice invoice = await RequireAsync(clientId, invoiceId, cancellationToken);
        if (invoice.Status != InvoiceStatus.Issued)
            throw new InvalidOperationException($"Only an issued invoice can be voided; {invoiceId} is {invoice.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, invoiceId, cancellationToken);
        EntryResponse issueEntry = spawned.FirstOrDefault(e => e is { Status: "Active", Posting: "Posted", ReversalOf: null })
            ?? throw new InvalidOperationException($"No posted entry found for invoice {invoice.Number} to reverse.");

        EntryResponse reversal = await ledger.ReverseAsync(
            clientId, issueEntry.Id, new ReverseRequest(invoice.IssueDate, reason ?? $"Voided invoice {invoice.Number}"), cancellationToken);
        await ledger.ApproveAsync(clientId, reversal.Id, cancellationToken);

        await invoices.VoidAsync(clientId, invoiceId, cancellationToken);
        return await RequireAsync(clientId, invoiceId, cancellationToken);
    }

    /// <summary>Fetch an invoice (number/status derived from the engine envelope), or null if not found.</summary>
    public Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default) =>
        invoices.GetAsync(clientId, invoiceId, cancellationToken);

    private async Task<Invoice> RequireAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken) =>
        await invoices.GetAsync(clientId, invoiceId, cancellationToken)
        ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");
}
