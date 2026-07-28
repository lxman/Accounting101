namespace Accounting101.Inventory.Tests;

/// <summary>
/// Grants immediately and excludes nobody — for tests that must still be able to drive two callers into
/// the same critical section.
///
/// The store's insert-only create is the arbiter of created-vs-replayed, and that guarantee is the
/// PORTABLE one: it holds even across processes, where <see cref="ItemWriteLock"/> does not. Tests that
/// pin it therefore have to bypass the lock, because with the lock in place the interleaving they need
/// is unreachable through the service — and worse, a rendezvous blocking inside the critical section
/// would strand its second participant and die on RaceRendezvous's timeout.
/// </summary>
internal sealed class NoOpItemWriteLock : IItemWriteLock
{
    private static readonly IDisposable Nothing = new NoRelease();

    public Task<IDisposable> AcquireAsync(Guid clientId, Guid itemId, CancellationToken ct = default) =>
        Task.FromResult(Nothing);

    private sealed class NoRelease : IDisposable
    {
        public void Dispose() { }
    }
}
