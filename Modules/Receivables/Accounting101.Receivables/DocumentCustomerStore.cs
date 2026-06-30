using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

/// <summary>
/// Persists customers through the engine's document store as <em>reference</em> data (mutable, audited).
/// The module owns no database connection — it speaks only <see cref="IDocumentStore"/>.
/// </summary>
public sealed class DocumentCustomerStore(IDocumentStore documents) : ICustomerStore
{
    private const string Collection = "customers";

    public Task SaveAsync(Guid clientId, Customer customer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return documents.PutAsync(
            clientId, Collection, customer.Id,
            new CustomerBody(customer.Name, customer.Email),
            new Dictionary<string, string>(),
            cancellationToken);
    }

    public async Task<Customer?> GetAsync(Guid clientId, Guid customerId, CancellationToken cancellationToken = default)
    {
        DocumentResult<CustomerBody>? result = await documents.GetAsync<CustomerBody>(clientId, Collection, customerId, cancellationToken);
        return result is null
            ? null
            : new Customer { Id = result.Id, Name = result.Body.Name, Email = result.Body.Email };
    }

    public async Task<IReadOnlyList<Customer>> ListAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DocumentResult<CustomerBody>> results = await documents.QueryAsync<CustomerBody>(
            clientId, Collection, new Dictionary<string, string>(), cancellationToken: cancellationToken);
        return results
            .Select(r => new Customer { Id = r.Id, Name = r.Body.Name, Email = r.Body.Email })
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
