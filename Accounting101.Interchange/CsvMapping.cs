namespace Accounting101.Interchange;

/// <summary>A reference to a CSV column, by zero-based index OR by header name (requires a header row).</summary>
public sealed record ColumnRef(int? Index, string? Header);

/// <summary>How to read a bank CSV: which columns hold which fields, the amount-sign convention, the date
/// format, and an optional status filter. Amount is either a single signed column OR a Debit+Credit pair
/// (amount = credit − debit). When a Status column is mapped, rows whose status is in ExcludeStatuses are
/// dropped before parsing (e.g. skip "Pending").</summary>
public sealed record CsvMapping(
    ColumnRef Date,
    ColumnRef? Amount,
    ColumnRef? Debit,
    ColumnRef? Credit,
    ColumnRef Description,
    ColumnRef? Reference,
    string? DateFormat,
    bool HasHeader,
    char? Delimiter = null,                          // null → ','  (nullable for forgiving JSON binding)
    ColumnRef? Status = null,
    IReadOnlyList<string>? ExcludeStatuses = null);
