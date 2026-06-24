using Accounting101.Ledger.Contracts;

namespace Accounting101.Invoicing;

/// <summary>
/// The module's entire view of the ledger engine — the seam it posts through. In production this is an
/// HTTP client to the engine that forwards the caller's identity, so every post still passes the engine's
/// policy layer; in tests it can be a fake or point at the engine's in-memory host. The module knows
/// nothing else about the engine: it speaks only the wire contract.
/// </summary>
public interface ILedgerClient
{
    Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default);

    Task<EntryResponse> ApproveAsync(Guid clientId, Guid entryId, CancellationToken cancellationToken = default);

    Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default);

    /// <summary>Withdraw an entry — voids it. For an approved entry the projection is reversed; for a
    /// not-yet-approved (pending) entry it simply marks it voided (it was never on the books).</summary>
    Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every entry the engine has tied to a source document — how the module finds the entry an invoice produced.</summary>
    Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default);
}
