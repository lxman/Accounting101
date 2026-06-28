namespace Accounting101.Interchange.Tests;

public sealed class CsvStatementImporterTests
{
    private static ImportResult<ImportedStatement> Import(string csv, CsvMapping mapping)
    {
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(csv));
        return new CsvStatementImporter().Import(stream, new ImportOptions { Csv = mapping });
    }

    private static ColumnRef H(string name) => new(null, name);
    private static ColumnRef Idx(int i) => new(i, null);

    [Fact]
    public void Wells_fargo_layout_parses_posted_lines_and_excludes_pending()
    {
        // Synthetic rows in the real WF layout (no personal data).
        string csv =
            "\"DATE\",\"DESCRIPTION\",\"AMOUNT\",\"CHECK #\",\"STATUS\"\n" +
            "\"06/29/2026\",\"PURCHASE COFFEE\",\"-4.50\",\"\",\"Pending\"\n" +
            "\"06/28/2026\",\"PAYROLL DEPOSIT\",\"1200.00\",\"\",\"Posted\"\n" +
            "\"06/27/2026\",\"CHECK 1021\",\"-300.00\",\"1021\",\"Posted\"\n";
        CsvMapping mapping = new(
            Date: H("DATE"), Amount: H("AMOUNT"), Debit: null, Credit: null,
            Description: H("DESCRIPTION"), Reference: H("CHECK #"),
            DateFormat: "MM/dd/yyyy", HasHeader: true,
            Status: H("STATUS"), ExcludeStatuses: ["Pending"]);

        ImportResult<ImportedStatement> result = Import(csv, mapping);

        Assert.Single(result.Records);
        IReadOnlyList<ImportedLine> lines = result.Records[0].Lines;
        Assert.Equal(2, lines.Count);                                  // Pending excluded
        Assert.Equal(new DateOnly(2026, 6, 28), lines[0].Date);
        Assert.Equal(1200.00m, lines[0].Amount);
        Assert.Equal("PAYROLL DEPOSIT", lines[0].Description);
        Assert.Equal(-300.00m, lines[1].Amount);
        Assert.Equal("1021", lines[1].Reference);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Debit_and_credit_columns_combine_to_a_signed_amount()
    {
        string csv = "date,desc,debit,credit\n2026-01-05,fee,25.00,\n2026-01-06,dep,,100.00\n";
        CsvMapping mapping = new(
            Date: H("date"), Amount: null, Debit: H("debit"), Credit: H("credit"),
            Description: H("desc"), Reference: null, DateFormat: null, HasHeader: true);

        IReadOnlyList<ImportedLine> lines = Import(csv, mapping).Records[0].Lines;
        Assert.Equal(-25.00m, lines[0].Amount);                        // credit − debit
        Assert.Equal(100.00m, lines[1].Amount);
    }

    [Fact]
    public void Positional_no_header_mapping_works()
    {
        string csv = "2026-02-01,opening dep,500.00\n";
        CsvMapping mapping = new(
            Date: Idx(0), Amount: Idx(2), Debit: null, Credit: null,
            Description: Idx(1), Reference: null, DateFormat: null, HasHeader: false);

        IReadOnlyList<ImportedLine> lines = Import(csv, mapping).Records[0].Lines;
        Assert.Single(lines);
        Assert.Equal(500.00m, lines[0].Amount);
    }

    [Fact]
    public void A_bad_date_or_amount_is_skipped_and_warned()
    {
        string csv = "date,desc,amount\n2026-01-01,ok,10.00\nNOTADATE,bad,5.00\n2026-01-02,bademt,xyz\n";
        CsvMapping mapping = new(
            Date: H("date"), Amount: H("amount"), Debit: null, Credit: null,
            Description: H("desc"), Reference: null, DateFormat: null, HasHeader: true);

        ImportResult<ImportedStatement> result = Import(csv, mapping);
        Assert.Single(result.Records[0].Lines);                        // only the good row
        Assert.Equal(2, result.Warnings.Count);                        // the bad date + bad amount
    }

    [Fact]
    public void A_mapping_with_no_amount_columns_throws()
    {
        string csv = "date,desc\n2026-01-01,x\n";
        CsvMapping mapping = new(
            Date: H("date"), Amount: null, Debit: null, Credit: null,
            Description: H("desc"), Reference: null, DateFormat: null, HasHeader: true);

        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(csv));
        Assert.Throws<ArgumentException>(() => new CsvStatementImporter().Import(stream, new ImportOptions { Csv = mapping }));
    }
}
