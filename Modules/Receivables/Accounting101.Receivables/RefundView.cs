namespace Accounting101.Receivables;

/// <summary>A refund plus the id of its posted journal entry — what the refund detail endpoint returns.
/// The entry id lets the UI drill from the refund to the GL entry that recorded it.</summary>
public sealed record RefundView(Refund Refund, Guid? JournalEntryId);
