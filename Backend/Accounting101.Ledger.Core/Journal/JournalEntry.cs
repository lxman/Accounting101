namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// A balanced, multi-line journal entry — the single source of truth in the ledger.
/// It cannot exist unbalanced: construct only through <see cref="Create"/> (or
/// <see cref="JournalEntryBuilder"/>), which enforces the invariants. All internal
/// identifiers are GUIDs.
/// </summary>
public sealed record JournalEntry
{
    public Guid Id { get; private init; }
    public Guid ClientId { get; private init; }
    public long SequenceNumber { get; private init; }
    public DateOnly EffectiveDate { get; private init; }
    public DateTimeOffset PostedAt { get; private init; }
    public EntryType Type { get; private init; }
    public LifecycleStatus Status { get; private init; }
    public PostingState Posting { get; private init; }
    public Guid? Supersedes { get; private init; }
    public Guid? SupersededBy { get; private init; }
    public Guid? ReversalOf { get; private init; }
    public Guid? ReversedBy { get; private init; }
    public Guid? SourceRef { get; private init; }

    /// <summary>
    /// Opaque discriminator naming the kind of document <see cref="SourceRef"/> points at
    /// (e.g. "Invoice", "PayRun"). The engine never interprets it; it tells an upstream module
    /// which of its stores to resolve the back-link in. Null when the entry has no source document.
    /// </summary>
    public string? SourceType { get; private init; }

    public string? Reference { get; private init; }
    public string? Memo { get; private init; }
    public int Version { get; private init; }
    public AuditStamp Audit { get; private init; }

    /// <summary>The postings. Always &gt;= 2 and always balanced (signed effects sum to zero).</summary>
    public IReadOnlyList<Line> Lines { get; private init; }

    private JournalEntry(
        Guid id, Guid clientId, long sequenceNumber, DateOnly effectiveDate, DateTimeOffset postedAt,
        EntryType type, LifecycleStatus status, PostingState posting,
        Guid? supersedes, Guid? supersededBy, Guid? reversalOf, Guid? reversedBy,
        Guid? sourceRef, string? sourceType, string? reference, string? memo, int version,
        AuditStamp audit, IReadOnlyList<Line> lines)
    {
        Id = id;
        ClientId = clientId;
        SequenceNumber = sequenceNumber;
        EffectiveDate = effectiveDate;
        PostedAt = postedAt;
        Type = type;
        Status = status;
        Posting = posting;
        Supersedes = supersedes;
        SupersededBy = supersededBy;
        ReversalOf = reversalOf;
        ReversedBy = reversedBy;
        SourceRef = sourceRef;
        SourceType = sourceType;
        Reference = reference;
        Memo = memo;
        Version = version;
        Audit = audit;
        Lines = lines;
    }

    /// <summary>
    /// Creates a validated entry. Throws <see cref="ArgumentException"/> if there are
    /// fewer than two lines, or <see cref="UnbalancedEntryException"/> if the lines do
    /// not balance. New entries default to <see cref="LifecycleStatus.Active"/> and
    /// <see cref="PostingState.PendingApproval"/> (hold-for-review by default).
    /// </summary>
    public static JournalEntry Create(
        Guid id,
        Guid clientId,
        long sequenceNumber,
        DateOnly effectiveDate,
        DateTimeOffset postedAt,
        EntryType type,
        AuditStamp audit,
        IReadOnlyList<Line> lines,
        PostingState posting = PostingState.PendingApproval,
        LifecycleStatus status = LifecycleStatus.Active,
        int version = 1,
        Guid? supersedes = null,
        Guid? supersededBy = null,
        Guid? reversalOf = null,
        Guid? reversedBy = null,
        Guid? sourceRef = null,
        string? sourceType = null,
        string? reference = null,
        string? memo = null)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count < 2)
            throw new ArgumentException("A journal entry needs at least two lines.", nameof(lines));

        decimal imbalance = SumSignedEffects(lines);
        if (imbalance != 0m)
            throw new UnbalancedEntryException(imbalance);

        return new JournalEntry(
            id, clientId, sequenceNumber, effectiveDate, postedAt,
            type, status, posting,
            supersedes, supersededBy, reversalOf, reversedBy,
            sourceRef, sourceType, reference, memo, version,
            audit, [.. lines]); // defensive immutable snapshot
    }

    /// <summary>Net signed effect across all lines. Always zero for a valid entry.</summary>
    public decimal SignedTotal() => SumSignedEffects(Lines);

    /// <summary>
    /// This entry's net effect on a single account (debit-positive); zero if the
    /// account is untouched. The cross-entry replay that gates on Status/Posting is
    /// a later concern — this is just one entry's contribution.
    /// </summary>
    public decimal BalanceFor(Guid accountId)
    {
        return Lines.Where(line => line.AccountId == accountId).Sum(line => line.SignedEffect);
    }

    /// <summary>
    /// Approve a pending entry, putting it on the books (PendingApproval -&gt; Posted).
    /// Content is unchanged, so the balanced invariant still holds.
    /// </summary>
    public JournalEntry Approve(Guid approvedBy)
    {
        if (Posting != PostingState.PendingApproval)
            throw new InvalidOperationException(
                $"Only a {PostingState.PendingApproval} entry can be approved; this one is {Posting}.");

        return this with
        {
            Posting = PostingState.Posted,
            Version = Version + 1,
            Audit = Audit with { ApprovedBy = approvedBy },
        };
    }

    /// <summary>Void an active entry (delete-as-event). The entry and its references remain.</summary>
    public JournalEntry Void()
    {
        if (Status != LifecycleStatus.Active)
            throw new InvalidOperationException(
                $"Only an {LifecycleStatus.Active} entry can be voided; this one is {Status}.");

        return this with { Status = LifecycleStatus.Voided, Version = Version + 1 };
    }

    /// <summary>Mark this active entry superseded by a replacement (the edit path).</summary>
    public JournalEntry Supersede(Guid replacementId)
    {
        if (Status != LifecycleStatus.Active)
            throw new InvalidOperationException(
                $"Only an {LifecycleStatus.Active} entry can be superseded; this one is {Status}.");

        return this with
        {
            Status = LifecycleStatus.Superseded,
            SupersededBy = replacementId,
            Version = Version + 1,
        };
    }

    private static decimal SumSignedEffects(IReadOnlyList<Line> lines)
    {
        return lines.Sum(line => line.SignedEffect);
    }
}
