using Accounting101.Ledger.Contracts;

namespace Accounting101.Banking.Reconciliation;

/// <summary>Bank adjustments as evidentiary documents (one-step record; voidable).</summary>
public interface IBankAdjustmentStore
{
    /// <summary>Records the adjustment under <paramref name="id"/>. <c>Created</c> is <c>true</c> only for
    /// the caller that won the underlying atomic create; under two concurrent callers racing the SAME id
    /// exactly one gets <c>true</c>, and the loser still gets back the winner's persisted document but must
    /// report its own call as a replay. A caller cannot infer this from an earlier existence check — that
    /// check is TOCTOU under concurrency.</summary>
    Task<(BankAdjustment Adjustment, bool Created)> RecordAsync(Guid clientId, Guid id, BankAdjustmentBody body, CancellationToken ct = default);
    Task VoidAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<BankAdjustment?> GetAsync(Guid clientId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<BankAdjustment>> GetByReconciliationAsync(Guid clientId, Guid reconciliationId, CancellationToken ct = default);
}

/// <summary>The module's POSTING window onto the engine (Slice 3): posts adjustments via module identity
/// and reverses/voids them. Distinct from the Slice 1 read-only reader. Mirrors the Cash module's seam.</summary>
public interface ILedgerClient
{
    Task<PostEntryResponse> PostAsync(Guid clientId, PostEntryRequest entry, CancellationToken cancellationToken = default);
    Task<EntryResponse> ReverseAsync(Guid clientId, Guid entryId, ReverseRequest request, CancellationToken cancellationToken = default);
    Task<EntryResponse> VoidAsync(Guid clientId, Guid entryId, VoidRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EntryResponse>> GetEntriesBySourceRefAsync(Guid clientId, Guid sourceRef, CancellationToken cancellationToken = default);
}
