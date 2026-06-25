using System.Collections.Concurrent;
using Accounting101.Payables;

namespace Accounting101.Payables.Tests;

internal sealed class InMemoryVendorStore : IVendorStore
{
    private readonly ConcurrentDictionary<(Guid, Guid), Vendor> _store = new();

    public Task SaveAsync(Guid clientId, Vendor vendor, CancellationToken ct = default)
    {
        _store[(clientId, vendor.Id)] = vendor;
        return Task.CompletedTask;
    }

    public Task<Vendor?> GetAsync(Guid clientId, Guid vendorId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault((clientId, vendorId)));
}
