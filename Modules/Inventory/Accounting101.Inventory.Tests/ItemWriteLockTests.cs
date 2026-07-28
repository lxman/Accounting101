namespace Accounting101.Inventory.Tests;

/// <summary>The per-item mutex behind the fold race fix. These are the primitive's own guarantees;
/// the service-level races that depend on them live in MovementFoldRaceTests.</summary>
public sealed class ItemWriteLockTests
{
    private static readonly Guid Client = Guid.NewGuid();

    [Fact]
    public async Task A_second_acquire_of_the_same_item_waits_for_the_first_to_release()
    {
        ItemWriteLock gate = new();
        Guid item = Guid.NewGuid();

        IDisposable first = await gate.AcquireAsync(Client, item);
        Task<IDisposable> second = gate.AcquireAsync(Client, item);

        Assert.False(second.IsCompleted);          // blocked while the first is held
        first.Dispose();
        IDisposable acquired = await second;       // released, so it completes
        acquired.Dispose();
    }

    [Fact]
    public async Task Different_items_do_not_block_each_other()
    {
        ItemWriteLock gate = new();

        IDisposable a = await gate.AcquireAsync(Client, Guid.NewGuid());
        IDisposable b = await gate.AcquireAsync(Client, Guid.NewGuid());   // must not hang

        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task The_same_item_under_a_different_client_does_not_block()
    {
        ItemWriteLock gate = new();
        Guid item = Guid.NewGuid();

        IDisposable a = await gate.AcquireAsync(Guid.NewGuid(), item);
        IDisposable b = await gate.AcquireAsync(Guid.NewGuid(), item);     // must not hang

        a.Dispose();
        b.Dispose();
    }

    /// <summary>A double dispose must not over-release the semaphore — that would raise its count above
    /// one and silently destroy mutual exclusion for every later caller.</summary>
    [Fact]
    public async Task Disposing_twice_does_not_over_release()
    {
        ItemWriteLock gate = new();
        Guid item = Guid.NewGuid();

        IDisposable held = await gate.AcquireAsync(Client, item);
        held.Dispose();
        held.Dispose();

        IDisposable one = await gate.AcquireAsync(Client, item);
        Task<IDisposable> two = gate.AcquireAsync(Client, item);
        Assert.False(two.IsCompleted);             // still mutually exclusive

        one.Dispose();
        (await two).Dispose();
    }
}
