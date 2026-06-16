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
}
