namespace Accounting101.TestSupport;

/// <summary>
/// Releases every participant once the Nth arrives, so concurrent callers can be forced into a
/// specific interleaving. Used to drive two racers into the time-of-check-to-time-of-use window
/// between a service's existence probe and its atomic create — a window two real HTTP requests
/// cannot be relied on to enter, because they usually serialize.
///
/// The timeout is load-bearing: a test that declares more participants than actually arrive must
/// fail in seconds with a clear message rather than block the suite until the runner gives up.
/// </summary>
public sealed class RaceRendezvous(int participants, TimeSpan? timeout = null)
{
    private readonly TaskCompletionSource _allArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);
    private int _arrived;

    public async Task ArriveAndWaitAsync(CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _arrived) >= participants)
            _allArrived.TrySetResult();

        Task completed = await Task.WhenAny(_allArrived.Task, Task.Delay(_timeout, ct));
        if (completed != _allArrived.Task)
            throw new TimeoutException(
                $"RaceRendezvous timed out after {_timeout.TotalSeconds:0.#}s: expected {participants} " +
                $"participants, only {Volatile.Read(ref _arrived)} arrived.");
    }
}
