using Accounting101.Ledger.Contracts;

namespace Accounting101.Receivables;

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

    /// <summary>
    /// Withdraw a pending (not-yet-approved) entry — marks it Voided without leaving any effect on the
    /// books. The receivables module uses this when voiding an invoice whose A/R entry was never approved
    /// (nothing to reverse). For posted entries, use <see cref="ReverseAsync"/> instead.
    /// </summary>
    Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dry-run the would-be post without writing anything. Returns on a clean validation; throws
    /// <c>LedgerClientException</c> with the engine's status and reason on any rejection (closed
    /// period, chart violation, unbalanced entry). Lets callers catch a bad date or account before
    /// committing the document, so the document is never finalized against an entry the engine would refuse.
    /// </summary>
    Task ValidateAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default);

    /// <summary>Every entry the engine has tied to a source document — how the module finds the entry an invoice produced.</summary>
    Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default);

    /// <summary>Read a per-dimension control-account fold: the signed (debit-positive) balance of
    /// <paramref name="account"/> grouped by the value of dimension <paramref name="dimension"/>
    /// (e.g. "Customer" or "Invoice"). This is how ledger-first read paths derive balances.
    /// <para>
    /// <paramref name="includePending"/> (default false) keeps the fold Posted-only, matching what is
    /// actually on the books — the correct semantics for every read (open balance, aging, views). Pass
    /// <c>true</c> only from write-path validation that must reserve against a not-yet-approved relief
    /// (e.g. rejecting a second unapproved payment that would over-apply an invoice); never from a read.
    /// </para></summary>
    Task<IReadOnlyList<SubledgerLineResponse>> GetSubledgerAsync(
        Guid clientId, Guid account, string dimension, DateOnly? asOf, CancellationToken cancellationToken = default,
        bool includePending = false);

    /// <summary>The client's full chart of accounts — how chart-readiness checks compare the module's
    /// account requirements against what is actually configured.</summary>
    Task<IReadOnlyList<AccountResponse>> GetAccountsAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>The acting user's resolved capabilities on the client (for readiness authorization). 403 if not a member.</summary>
    Task<CapabilitiesResponse> GetMyCapabilitiesAsync(Guid clientId, CancellationToken cancellationToken = default);
}
