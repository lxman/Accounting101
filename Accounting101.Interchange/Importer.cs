namespace Accounting101.Interchange;

/// <summary>The outcome of an import: the parsed records (one file may yield several — e.g. an OFX file
/// with multiple account statements) plus any non-fatal warnings (a skipped/unparseable row, a dropped field).</summary>
public sealed record ImportResult<T>(IReadOnlyList<T> Records, IReadOnlyList<string> Warnings);

/// <summary>Format-specific import options. Only the member for the chosen format is consulted.</summary>
public sealed class ImportOptions
{
    /// <summary>Required when importing CSV; ignored otherwise.</summary>
    public CsvMapping? Csv { get; init; }
}

/// <summary>Reads a source stream into records of type <typeparamref name="T"/> for one format.</summary>
public interface IImporter<T>
{
    InterchangeFormat Format { get; }
    ImportResult<T> Import(Stream source, ImportOptions options);
}
