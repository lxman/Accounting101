using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets;

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
    /// books. The fixed-assets module uses this when voiding a run whose entry was never approved
    /// (nothing to reverse). For posted entries, use <see cref="ReverseAsync"/> instead.
    /// </summary>
    Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every entry the engine has tied to a source document — how the module finds the entry a run produced.</summary>
    Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default);

    /// <summary>Read a per-dimension control-account fold: the signed (debit-positive) balance of
    /// <paramref name="account"/> grouped by the value of dimension <paramref name="dimension"/>
    /// (e.g. "Asset"). This is how ledger-first read paths derive balances.
    /// <para>
    /// <paramref name="includePending"/> (default false) keeps the fold Posted-only, matching what is
    /// actually on the books — the correct semantics for every read (asset accumulated depreciation, NBV).
    /// Pass <c>true</c> only from a write-path compute that must include a not-yet-approved run (declining-
    /// balance basing the next period, disposal catch-up); never from a read.
    /// </para></summary>
    Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false);

    /// <summary>The client's full chart of accounts — how chart-readiness checks compare the module's
    /// account requirements against what is actually configured.</summary>
    Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default);
}
