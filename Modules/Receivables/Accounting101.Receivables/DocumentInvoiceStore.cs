using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

/// <summary>
/// Persists invoices through the engine's document store as <em>evidentiary</em> data: a draft is created
/// mutable, finalized (issued) into an append-only numbered document, and voided. Number and status are
/// derived from the engine's envelope, never stored. The module owns no database connection.
/// </summary>
public sealed class DocumentInvoiceStore(IDocumentStore documents) : IInvoiceStore
{
    private const string Collection = "invoices";

    public async Task<Invoice> CreateDraftAsync(Guid clientId, InvoiceBody body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        Guid id = await documents.CreateAsync(clientId, Collection, body, Tags(body.CustomerId), cancellationToken);
        DocumentResult<InvoiceBody>? result = await documents.GetAsync<InvoiceBody>(clientId, Collection, id, cancellationToken);
        return Map(result!);
    }

    public async Task<Invoice> FinalizeAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await documents.FinalizeAsync(clientId, Collection, invoiceId, cancellationToken);
        DocumentResult<InvoiceBody>? result = await documents.GetAsync<InvoiceBody>(clientId, Collection, invoiceId, cancellationToken);
        return Map(result!);
    }

    public Task VoidAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default) =>
        documents.VoidAsync(clientId, Collection, invoiceId, cancellationToken);

    public async Task<Invoice?> GetAsync(Guid clientId, Guid invoiceId, CancellationToken cancellationToken = default)
    {
        DocumentResult<InvoiceBody>? result = await documents.GetAsync<InvoiceBody>(clientId, Collection, invoiceId, cancellationToken);
        return result is null ? null : Map(result);
    }

    public async Task<IReadOnlyList<Invoice>> GetByCustomerAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DocumentResult<InvoiceBody>> results =
            await documents.QueryAsync<InvoiceBody>(clientId, Collection, Tags(customerId), cancellationToken);
        return results.Select(Map).ToList();
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
            // a pre-finalize invoice is Active (and any other non-issued/voided state) — it reads as Draft
            _ => InvoiceStatus.Draft,
        },
        TaxRate = result.Body.TaxRate,
        Memo = result.Body.Memo,
        Lines = result.Body.Lines
            .Select(l => new InvoiceLine { Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, Taxable = l.Taxable, RevenueCategory = l.RevenueCategory })
            .ToList(),
    };
}
