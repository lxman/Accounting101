using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

/// <summary>
/// The receivables lifecycle: draft an invoice, issue it (finalize — assigns the number —
/// then post its A/R entry, which lands PendingApproval for a separate approver to book), and
/// void it (reverse the entry if Posted, or withdraw it if still pending — never self-approving).
/// The module owns its documents; the engine owns the ledger. The link between them is the
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
            lines.Select(l => new LineBody(l.Description, l.Quantity, l.UnitPrice, l.Taxable, l.RevenueCategory)).ToList());

        return await invoices.CreateDraftAsync(clientId, body, cancellationToken);
    }

    /// <summary>
    /// Issue a draft: validate the would-be A/R post first (pre-flight), then finalize (assigns the number),
    /// recompose with the assigned number, and post. Pre-flighting means a rejected date or chart violation
    /// throws <see cref="LedgerClientException"/> BEFORE finalize, so the document stays Draft — no orphan.
    /// The entry lands PendingApproval — approval is the client's normal maker-checker flow (SoD).
    /// </summary>
    public async Task<Invoice> IssueAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        Invoice draft = await RequireAsync(clientId, invoiceId, cancellationToken);
        if (draft.Status != InvoiceStatus.Draft)
            throw new InvalidOperationException($"Only a draft invoice can be issued; {invoiceId} is {draft.Status}.");
        if (draft.Total <= 0m)
            throw new InvalidOperationException($"Invoice {invoiceId} must total more than zero to be issued.");

        // Resolve accounts and pre-flight against the draft (Number is null → Reference is null, which validation ignores).
        // If the engine would reject (closed period, chart violation, unbalanced entry), this throws LedgerClientException
        // and the document remains a Draft — finalize has not run, so there is no orphan.
        InvoicePostingAccounts postingAccounts = await accounts.GetAsync(clientId, cancellationToken);
        PostEntryRequest preflight = InvoicePosting.Compose(draft, postingAccounts);
        await ledger.ValidateAsync(clientId, preflight, cancellationToken);

        // Validation passed — it is safe to commit the document.
        Invoice issued = await invoices.PromoteDraftAsync(clientId, invoiceId, cancellationToken);

        // Recompose with the now-assigned invoice number so the posted entry carries it in Reference.
        PostEntryRequest entry = InvoicePosting.Compose(issued, postingAccounts);
        await ledger.PostAsync(clientId, entry, cancellationToken);   // lands PendingApproval; approval is the client's normal flow

        return issued;
    }

    /// <summary>
    /// Void an issued invoice: find the entry it produced (by source back-link), then branch on its state.
    /// If the entry is on the books (Posted) → reverse it (reversal lands PendingApproval; no auto-approve).
    /// If the entry is still pending (PendingApproval) → withdraw it via VoidAsync (nothing was on the books).
    /// Then void the document. Correct the books before marking the source.
    /// </summary>
    public async Task<Invoice> VoidAsync(
        Guid clientId, Guid invoiceId, string? reason = null, CancellationToken cancellationToken = default)
    {
        Invoice invoice = await RequireAsync(clientId, invoiceId, cancellationToken);
        if (invoice.Status != InvoiceStatus.Issued)
            throw new InvalidOperationException($"Only an issued invoice can be voided; {invoiceId} is {invoice.Status}.");

        IReadOnlyList<EntryResponse> spawned = await ledger.GetEntriesBySourceRefAsync(clientId, invoiceId, cancellationToken);
        EntryResponse issueEntry = spawned.FirstOrDefault(e => e is { Status: "Active", ReversalOf: null })
            ?? throw new InvalidOperationException($"No entry found for invoice {invoice.Number} to void.");

        if (issueEntry.Posting == "Posted")
        {
            // On the books: reverse it. The reversal lands PendingApproval — the GL correction awaits approval.
            await ledger.ReverseAsync(
                clientId, issueEntry.Id, new ReverseRequest(invoice.IssueDate, reason ?? $"Voided invoice {invoice.Number}"), cancellationToken);
        }
        else
        {
            // Never approved onto the books: nothing to reverse — withdraw the pending entry.
            await ledger.VoidAsync(clientId, issueEntry.Id, new VoidRequest(reason ?? $"Voided invoice {invoice.Number}"), cancellationToken);
        }

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
