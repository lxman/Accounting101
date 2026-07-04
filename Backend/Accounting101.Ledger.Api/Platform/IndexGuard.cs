using System.Collections.Concurrent;

namespace Accounting101.Ledger.Api.Platform;

/// <summary>Singleton <see cref="IIndexGuard"/> backed by a concurrent set of already-indexed client ids.</summary>
public sealed class IndexGuard : IIndexGuard
{
    private readonly ConcurrentDictionary<Guid, bool> _indexed = new();

    public bool TryClaim(Guid clientId) => _indexed.TryAdd(clientId, true);

    public void Release(Guid clientId) => _indexed.TryRemove(clientId, out _);
}
