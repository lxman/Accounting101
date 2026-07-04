namespace Accounting101.Ledger.Api.Platform;

/// <summary>
/// Process-wide "have this client's indexes been ensured yet?" latch. Extracted from
/// <c>ClientLedgerFactory</c> so it survives when the factory becomes request-scoped — the once-per-process
/// guarantee must outlive a single request.
/// </summary>
public interface IIndexGuard
{
    /// <summary>Claims the client for indexing; true exactly once until <see cref="Release"/>.</summary>
    bool TryClaim(Guid clientId);

    /// <summary>Re-arms the client (call after a failed index attempt so a later request retries).</summary>
    void Release(Guid clientId);
}
