namespace Accounting101.Payables;

/// <summary>The module's vendor store — reference data via the engine's document store.</summary>
public interface IVendorStore
{
    Task SaveAsync(Guid clientId, Vendor vendor, CancellationToken ct = default);
    Task<Vendor?> GetAsync(Guid clientId, Guid vendorId, CancellationToken ct = default);
    Task<IReadOnlyList<Vendor>> ListAsync(Guid clientId, CancellationToken ct = default);
}
