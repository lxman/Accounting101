namespace Accounting101.Ledger.Core.Journal;

/// <summary>
/// Ergonomic assembly of a <see cref="JournalEntry"/>. Add postings fluently with
/// <see cref="Debit"/>/<see cref="Credit"/>, then call <see cref="Build"/>, which
/// runs the same validation as <see cref="JournalEntry.Create"/>. Line ids are
/// generated unless supplied.
/// </summary>
public sealed class JournalEntryBuilder(
    Guid id,
    Guid clientId,
    long sequenceNumber,
    DateOnly effectiveDate,
    DateTimeOffset postedAt,
    AuditStamp audit)
{
    private readonly List<Line> _lines = [];

    public EntryType Type { get; set; } = EntryType.Standard;
    public PostingState Posting { get; set; } = PostingState.PendingApproval;
    public string? Memo { get; set; }
    public string? Reference { get; set; }
    public Guid? SourceRef { get; set; }
    public string? SourceType { get; set; }

    public JournalEntryBuilder Debit(Guid accountId, decimal amount,
        IReadOnlyDictionary<string, Guid>? dimensions = null,
        string? lineMemo = null, Guid? lineId = null)
        => AddLine(Direction.Debit, accountId, amount, dimensions, lineMemo, lineId);

    public JournalEntryBuilder Credit(Guid accountId, decimal amount,
        IReadOnlyDictionary<string, Guid>? dimensions = null,
        string? lineMemo = null, Guid? lineId = null)
        => AddLine(Direction.Credit, accountId, amount, dimensions, lineMemo, lineId);

    public JournalEntryBuilder AddLine(Direction direction, Guid accountId, decimal amount,
        IReadOnlyDictionary<string, Guid>? dimensions = null,
        string? lineMemo = null, Guid? lineId = null)
    {
        Line line = new()
        {
            Id = lineId ?? Guid.NewGuid(),
            AccountId = accountId,
            Direction = direction,
            Amount = amount,
            LineMemo = lineMemo,
        };
        _lines.Add(dimensions is null ? line : line with { Dimensions = dimensions });
        return this;
    }

    public JournalEntry Build() => JournalEntry.Create(
        id: id,
        clientId: clientId,
        sequenceNumber: sequenceNumber,
        effectiveDate: effectiveDate,
        postedAt: postedAt,
        type: Type,
        audit: audit,
        lines: _lines,
        posting: Posting,
        sourceRef: SourceRef,
        sourceType: SourceType,
        reference: Reference,
        memo: Memo);
}
