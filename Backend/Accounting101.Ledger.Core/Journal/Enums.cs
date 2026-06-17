namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// Which column a posting sits in. The arithmetic is carried by the sign of the
/// amount; Direction is the domain's vocabulary and how the entry reads back.
/// </summary>
public enum Direction
{
    Debit,
    Credit,
}

/// <summary>Classifies an entry for reporting and close behaviour.</summary>
public enum EntryType
{
    Opening,
    Standard,
    Adjusting,
    Closing,
    Reversing,
}

/// <summary>
/// Version lifecycle of an entry. Editing supersedes (never mutates); replay
/// counts only <see cref="Active"/> entries.
/// </summary>
public enum LifecycleStatus
{
    Active,
    Superseded,
    Voided,
}

/// <summary>
/// Approval lifecycle (maker-checker), orthogonal to <see cref="LifecycleStatus"/>.
/// Replay counts only <see cref="Posted"/> entries; a <see cref="PendingApproval"/>
/// entry is durable and audited but not yet on the books.
/// </summary>
public enum PostingState
{
    PendingApproval,
    Posted,
}
