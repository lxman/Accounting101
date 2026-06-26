namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// Who created/posted/approved an entry, and when. This is the current ownership
/// stamp; the full change history (before/after deltas) lives in the audit log,
/// not here.
/// </summary>
public sealed record AuditStamp
{
    public required Guid CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public Guid? PostedBy { get; init; }
    public Guid? ApprovedBy { get; init; }

    /// <summary>
    /// The key of the module that originated this entry (e.g. "payables"), or null for a raw
    /// accountant entry. Populated only when the posting came through a module credential path;
    /// never set by the user directly.
    /// </summary>
    public string? ViaModule { get; init; }
}
