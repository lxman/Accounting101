namespace Accounting101.Interchange;

/// <summary>Column-to-field mapping for CSV imports. Populated by Task 2 (CsvStatementImporter).</summary>
public sealed class CsvMapping
{
    /// <summary>Zero-based column index for the transaction date field.</summary>
    public int DateColumn { get; init; }

    /// <summary>Zero-based column index for the description/payee field.</summary>
    public int DescriptionColumn { get; init; }

    /// <summary>Zero-based column index for the amount field (positive = debit, negative = credit).</summary>
    public int AmountColumn { get; init; }

    /// <summary>Whether the CSV file has a header row to skip.</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>Date format string, e.g. "MM/dd/yyyy". Defaults to ISO 8601.</summary>
    public string DateFormat { get; init; } = "yyyy-MM-dd";
}
