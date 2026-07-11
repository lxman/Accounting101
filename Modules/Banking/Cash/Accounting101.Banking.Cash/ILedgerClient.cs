using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Cash;

/// <summary>
/// The module's entire view of the ledger engine — the seam it posts through. In production this is an
/// HTTP client to the engine that forwards the caller's identity, so every post still passes the engine's
/// policy layer; in tests it can be a fake or point at the engine's in-memory host. The module knows
/// nothing else about the engine: it speaks only the wire contract.
/// </summary>
public interface ILedgerClient
{
    Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default);

    Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraw a pending (not-yet-approved) entry — marks it Voided without leaving any effect on the
    /// books. The cash module uses this when voiding a disbursement or deposit whose entry was never
    /// approved (nothing to reverse). For posted entries, use <see cref="ReverseAsync"/> instead.
    /// </summary>
    Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every entry the engine has tied to a source document — how the module finds the entry a disbursement or deposit produced.</summary>
    Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default);

    /// <summary>Every entry tied to any of the given source documents, in one round-trip — how a list
    /// read folds ledger-truth status across a page without an N+1 of singular lookups.</summary>
    Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefsAsync(Guid clientId, IReadOnlyList<Guid> sourceRefs, CancellationToken cancellationToken = default);

    /// <summary>The client's full chart of accounts — how chart-readiness checks compare the module's
    /// account requirements against what is actually configured.</summary>
    Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>The acting user's resolved capabilities on the client (for readiness authorization). 403 if not a member.</summary>
    Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default);
}
