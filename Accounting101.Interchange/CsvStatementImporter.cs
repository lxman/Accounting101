using System.Globalization;
using System.Text;

namespace Accounting101.Interchange;

/// <summary>Parses a bank CSV into one <see cref="ImportedStatement"/> using a configurable
/// <see cref="CsvMapping"/>. Per-row parse failures become warnings (the row is skipped); status-filtered
/// rows are dropped silently. CSV carries no balances, so they are left null for the user to supply.</summary>
public sealed class CsvStatementImporter : IImporter<ImportedStatement>
{
    public InterchangeFormat Format => InterchangeFormat.Csv;

    public ImportResult<ImportedStatement> Import(Stream source, ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CsvMapping mapping = options.Csv
            ?? throw new ArgumentException("CSV import requires options.Csv (a CsvMapping).");

        bool signed = mapping.Amount is not null;
        bool debitCredit = mapping.Debit is not null && mapping.Credit is not null;
        if (signed == debitCredit)
            throw new ArgumentException("CSV mapping must set exactly one of Amount, or both Debit and Credit.");

        using StreamReader reader = new(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string text = reader.ReadToEnd();
        IReadOnlyList<IReadOnlyList<string>> rows = DelimitedReader.ReadRows(text, mapping.Delimiter ?? ',');

        Dictionary<string, int>? header = null;
        int firstDataRow = 0;
        if (mapping.HasHeader)
        {
            if (rows.Count == 0)
                return new ImportResult<ImportedStatement>([new ImportedStatement([], null, null, null, null)], []);
            header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows[0].Count; i++) header[rows[0][i].Trim()] = i;
            firstDataRow = 1;
        }

        int dateCol = ResolveColumn(mapping.Date, header, "Date", required: true);
        int descCol = ResolveColumn(mapping.Description, header, "Description", required: true);
        int amountCol = ResolveColumn(mapping.Amount, header, "Amount", required: false);
        int debitCol = ResolveColumn(mapping.Debit, header, "Debit", required: false);
        int creditCol = ResolveColumn(mapping.Credit, header, "Credit", required: false);
        int refCol = ResolveColumn(mapping.Reference, header, "Reference", required: false);
        int statusCol = ResolveColumn(mapping.Status, header, "Status", required: false);
        var excluded = new HashSet<string>(mapping.ExcludeStatuses ?? [], StringComparer.OrdinalIgnoreCase);

        List<ImportedLine> lines = [];
        List<string> warnings = [];

        for (int r = firstDataRow; r < rows.Count; r++)
        {
            IReadOnlyList<string> row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;                          // blank row

            if (statusCol >= 0 && excluded.Count > 0 && excluded.Contains(Cell(row, statusCol).Trim()))
                continue;                                                              // filtered (e.g. Pending) — not a warning

            if (!TryParseDate(Cell(row, dateCol), mapping.DateFormat, out DateOnly date))
            {
                warnings.Add($"Row {r + 1}: could not parse date '{Cell(row, dateCol)}' — row skipped.");
                continue;
            }

            decimal amount;
            if (signed)
            {
                if (!TryParseAmount(Cell(row, amountCol), out amount))
                {
                    warnings.Add($"Row {r + 1}: could not parse amount '{Cell(row, amountCol)}' — row skipped.");
                    continue;
                }
            }
            else
            {
                bool okD = TryParseAmount(Cell(row, debitCol), out decimal debit, allowEmptyAsZero: true);
                bool okC = TryParseAmount(Cell(row, creditCol), out decimal credit, allowEmptyAsZero: true);
                if (!okD || !okC)
                {
                    warnings.Add($"Row {r + 1}: could not parse debit/credit '{Cell(row, debitCol)}'/'{Cell(row, creditCol)}' — row skipped.");
                    continue;
                }
                amount = credit - debit;
            }

            string description = Cell(row, descCol).Trim();
            string? reference = refCol >= 0 ? NullIfEmpty(Cell(row, refCol)) : null;
            lines.Add(new ImportedLine(date, amount, description, reference));
        }

        return new ImportResult<ImportedStatement>(
            [new ImportedStatement(lines, null, null, null, null)], warnings);
    }

    private static int ResolveColumn(ColumnRef? r, IReadOnlyDictionary<string, int>? header, string field, bool required)
    {
        if (r is null)
        {
            if (required) throw new ArgumentException($"CSV mapping is missing the required '{field}' column.");
            return -1;
        }
        if (r.Index is { } idx) return idx;
        if (r.Header is { } name)
        {
            if (header is null) throw new ArgumentException($"CSV mapping for '{field}' uses a header name but HasHeader is false.");
            if (header.TryGetValue(name.Trim(), out int hi)) return hi;
            throw new ArgumentException($"CSV header has no column named '{name}' (for '{field}').");
        }
        throw new ArgumentException($"CSV mapping for '{field}' must set either Index or Header.");
    }

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : string.Empty;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool TryParseDate(string s, string? format, out DateOnly date)
    {
        s = s.Trim();
        if (!string.IsNullOrEmpty(format))
            return DateOnly.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        string[] formats = ["yyyy-MM-dd", "MM/dd/yyyy", "yyyyMMdd"];
        return DateOnly.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryParseAmount(string s, out decimal amount, bool allowEmptyAsZero = false)
    {
        s = s.Trim();
        if (allowEmptyAsZero && s.Length == 0) { amount = 0m; return true; }
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }
}
