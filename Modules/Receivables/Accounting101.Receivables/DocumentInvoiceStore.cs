using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

/// <summary>
/// Persists invoices through the engine's document store using a two-tier split:
/// <list type="bullet">
///   <item><b>invoice-drafts</b> (plain) — freely editable and discardable scratch copies; never part of the
///   evidentiary record.</item>
///   <item><b>invoices</b> (evidentiary) — append-only numbered documents; entered only on issue (via
///   <see cref="PromoteDraftAsync"/>). Number and status are derived from the engine envelope, never stored.</item>
/// </list>
/// The module owns no database connection; the engine's <see cref="IDocumentStore"/> is the only dependency.
/// </summary>
public sealed class DocumentInvoiceStore(IDocumentStore documents) : IInvoiceStore
{
    private const string Drafts = "invoice-drafts";   // plain
    private const string Collection = "invoices";      // evidentiary

    public async Task<Invoice> CreateDraftAsync(Guid clientId, InvoiceBody body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = Guid.NewGuid();
        await documents.PutAsync(clientId, Drafts, id, body, Tags(body.CustomerId), cancellationToken);
        DocumentResult<InvoiceBody>? r = await documents.GetAsync<InvoiceBody>(clientId, Drafts, id, cancellationToken);
        return Map(r!);
    }

    public async Task<Invoice> UpdateDraftAsync(Guid clientId, Guid invoiceId, InvoiceBody body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, cancellationToken) is null)
            throw new InvalidOperationException($"Invoice {invoiceId} is not an editable draft.");
        await documents.PutAsync(clientId, Drafts, invoiceId, body, Tags(body.CustomerId), cancellationToken);
        DocumentResult<InvoiceBody>? r = await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, cancellationToken);
        return Map(r!);
    }

    public async Task DiscardDraftAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        if (await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, cancellationToken) is null)
            throw new InvalidOperationException($"Invoice {invoiceId} is not a discardable draft.");
        await documents.DeleteAsync(clientId, Drafts, invoiceId, cancellationToken);
    }

    public async Task<Invoice> PromoteDraftAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        DocumentResult<InvoiceBody>? draft = await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, cancellationToken)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} is not a draft awaiting issue.");
        Guid issuedId = await documents.CreateAsync(clientId, Collection, draft.Body, Tags(draft.Body.CustomerId), cancellationToken);
        await documents.FinalizeAsync(clientId, Collection, issuedId, cancellationToken);
        await documents.DeleteAsync(clientId, Drafts, invoiceId, cancellationToken);
        DocumentResult<InvoiceBody>? issued = await documents.GetAsync<InvoiceBody>(clientId, Collection, issuedId, cancellationToken);
        return Map(issued!);
    }

    public Task VoidAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default) =>
        documents.VoidAsync(clientId, Collection, invoiceId, cancellationToken);

    public async Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        DocumentResult<InvoiceBody>? draft = await documents.GetAsync<InvoiceBody>(clientId, Drafts, invoiceId, cancellationToken);
        if (draft is not null) return Map(draft);
        DocumentResult<InvoiceBody>? issued = await documents.GetAsync<InvoiceBody>(clientId, Collection, invoiceId, cancellationToken);
        return issued is null ? null : Map(issued);
    }

    public async Task<IReadOnlyList<Invoice>> GetByCustomerAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DocumentResult<InvoiceBody>> drafts = await documents.QueryAsync<InvoiceBody>(clientId, Drafts, Tags(customerId), cancellationToken: cancellationToken);
        IReadOnlyList<DocumentResult<InvoiceBody>> issued = await documents.QueryAsync<InvoiceBody>(clientId, Collection, Tags(customerId), cancellationToken: cancellationToken);
        return drafts.Concat(issued).Select(Map).ToList();
    }

    private static Dictionary<string, string> Tags(Guid customerId) => new() { ["Customer"] = customerId.ToString() };

    private static Invoice Map(DocumentResult<InvoiceBody> result) => new()
    {
        Id = result.Id,
        CustomerId = result.Body.CustomerId,
        Number = result.Sequence is { } seq ? $"INV-{seq:D5}" : null,
        IssueDate = result.Body.IssueDate,
        DueDate = result.Body.DueDate,
        Status = result.State switch
        {
            DocumentLifecycle.Finalized => InvoiceStatus.Issued,
            DocumentLifecycle.Voided or DocumentLifecycle.Superseded => InvoiceStatus.Void,
            // a plain draft or any non-finalized/voided state — reads as Draft
            _ => InvoiceStatus.Draft,
        },
        TaxRate = result.Body.TaxRate,
        Memo = result.Body.Memo,
        Lines = result.Body.Lines
            .Select(l => new InvoiceLine { Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, Taxable = l.Taxable, RevenueCategory = l.RevenueCategory })
            .ToList(),
    };
}
