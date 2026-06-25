using Accounting101.Ledger.Contracts;

namespace Accounting101.Payables;

/// <summary>
/// Persists vendors through the engine's document store as <em>reference</em> data (mutable, audited).
/// The module owns no database connection — it speaks only <see cref="IDocumentStore"/>.
/// </summary>
public sealed class DocumentVendorStore(IDocumentStore documents) : IVendorStore
{
    private const string Collection = "vendors";

    public Task SaveAsync(Guid clientId, Vendor vendor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        return documents.PutAsync(
            clientId, Collection, vendor.Id,
            new VendorBody(vendor.Name, vendor.Email),
            new Dictionary<string, string>(),
            ct);
    }

    public async Task<Vendor?> GetAsync(Guid clientId, Guid vendorId, CancellationToken ct = default)
    {
        DocumentResult<VendorBody>? result = await documents.GetAsync<VendorBody>(clientId, Collection, vendorId, ct);
        return result is null
            ? null
            : new Vendor { Id = result.Id, Name = result.Body.Name, Email = result.Body.Email };
    }
}
