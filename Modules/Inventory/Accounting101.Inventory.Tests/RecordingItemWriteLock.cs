using System.Collections.Concurrent;

namespace Accounting101.Inventory.Tests;

/// <summary>Records which (client, item) keys were acquired, so a test can assert a write path locks the
/// right item. Delegates to a real lock so behaviour is unchanged.</summary>
internal sealed class RecordingItemWriteLock(IItemWriteLock inner) : IItemWriteLock
{
    private readonly ConcurrentQueue<(Guid ClientId, Guid ItemId)> _acquired = new();

    public IReadOnlyList<(Guid ClientId, Guid ItemId)> Acquired => _acquired.ToList();

    public async Task<IDisposable> AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default)
    {
        _acquired.Enqueue((clientId, itemId));
        return await inner.AcquireAsync(clientId, itemId, ct);
    }
}
