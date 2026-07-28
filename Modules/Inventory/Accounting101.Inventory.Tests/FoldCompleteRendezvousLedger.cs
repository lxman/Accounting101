using Accounting101.Ledger.Contracts;

namespace Accounting101.Inventory.Tests;

/// <summary>
/// Runs the caller's barrier after the fold's LAST read, so no racer that barrier holds starts writing
/// until every racer it is waiting for has finished reading the whole valuation.
///
/// The fold is THREE reads, not one: <see cref="ItemValuationService.GetAsync"/> takes value from
/// <c>GetSubledgerAsync</c>, then quantity from <c>ProjectQuantityAsync</c>, which reads the movement
/// store and then <c>GetEntriesBySourceRefsAsync</c>. Nothing holds them together when the item write
/// lock is bypassed, so a racer preempted mid-fold can resume with a stale value and a fresh quantity —
/// an incoherent average, a different extended cost, and a divergent-content rejection from the ledger
/// fake. Blocking on the last of the three closes that window WITHOUT serialising the racers, which is
/// exactly what a test that must reach the un-serialised interleaving needs.
///
/// Only <c>GetEntriesBySourceRefsAsync</c> is intercepted; everything else delegates untouched.
///
/// WHAT THE HOOK IS. <paramref name="onFoldComplete"/> is the caller's own barrier, so the same decorator
/// serves both shapes this project needs. A test whose racers run UNLOCKED passes a plain
/// <c>RaceRendezvous.ArriveAndWaitAsync</c> — every racer always waits. A test whose racers contend for a
/// real item write lock cannot: only one of them can be inside the section this hook fires in, so an
/// unconditional wait would strand it. Those pass <see cref="FoldOverlapRacer.FoldCompletedAsync"/>, which
/// waits only for racers the lock is NOT protecting. See <see cref="FoldOverlapGate"/> for why.
/// </summary>
internal sealed class FoldCompleteRendezvousLedger(ILedgerClient inner, Func<CancellationToken, Task> onFoldComplete)
    : ILedgerClient
{
    public async Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(
        Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<EntryResponse> entries = await inner.GetEntriesBySourceRefsAsync(clientId, sourceRefs, cancellationToken);
        await onFoldComplete(cancellationToken);                 // this caller's fold is now complete
        return entries;
    }

    public Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default) =>
        inner.PostAsync(clientId, entry, cancellationToken);

    public Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default) =>
        inner.ReverseAsync(clientId, entryId, request, cancellationToken);

    public Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default) =>
        inner.VoidAsync(clientId, entryId, request, cancellationToken);

    public Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default) =>
        inner.GetEntriesBySourceRefAsync(clientId, sourceRef, cancellationToken);

    public Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false) =>
        inner.GetSubledgerAsync(clientId, account, dimension, asOf, cancellationToken, includePending);

    public Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        inner.GetAccountsAsync(clientId, cancellationToken);

    public Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        inner.GetMyCapabilitiesAsync(clientId, cancellationToken);
}
