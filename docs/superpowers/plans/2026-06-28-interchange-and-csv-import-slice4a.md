# Interchange Framework + CSV Statement Import — Slice 4a — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up a new `Accounting101.Interchange` project (entity- and format-pluggable import/export framework, readers only), a hand-rolled `DelimitedReader` + `CsvStatementImporter` with a configurable column mapping (incl. a status filter), and a parse-to-preview `POST /bank-statements/import?format=csv` endpoint on Reconciliation that creates nothing.

**Architecture:** `Accounting101.Interchange` is a zero-dependency POCO library: `IImporter<T>`/`IExporter<T>` resolved by `(typeof(T), InterchangeFormat)` via a singleton registry; `CsvStatementImporter` produces a neutral `ImportedStatement`. Reconciliation.Api references it, registers the default registry in `AddReconciliation`, and exposes the multipart preview endpoint (mapping the result to `BankStatementLineRequest`s). The existing validated `POST /bank-statements` does creation; import only parses. OFX is Slices 4b/4c; exporters are seam-only.

**Tech Stack:** C#/.NET 10, ASP.NET minimal APIs (multipart upload), xUnit, EphemeralMongo for E2E.

## Global Constraints

- New code under a new `Accounting101.Interchange/` project (+ its Tests) and `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/`. Slices 1-3 behavior, the cleared-method math, and other modules are UNCHANGED.
- `Accounting101.Interchange` has **no dependencies** (POCO framework). No HTTP, no Mongo, no module types. DI wiring is one `AddSingleton` line in the consumer using the pure `InterchangeRegistry.CreateDefault()` factory.
- **No GL mutation, no statement creation** in this slice — import returns a preview and creates nothing.
- Per-row CSV parse failures accumulate in `ImportResult.Warnings` (row number + reason) and skip that row — never a silent drop, never a whole-file abort. A `Status`/`ExcludeStatuses` filtered row is excluded silently (not a warning).
- CSV amount: exactly one of a single signed `Amount` column XOR a `Debit`+`Credit` pair (`amount = credit − debit`); otherwise `ArgumentException`.
- Money is `decimal`. Commit trailer, verbatim, on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Confirmed surface to integrate with

- Slice 1 request DTO (Reconciliation.Api `ReconciliationRequests.cs`): `BankStatementLineRequest(DateOnly Date, decimal Amount, string Description, string? ExternalRef)`; `RecordBankStatementRequest(Guid CashAccountId, DateOnly StatementDate, decimal OpeningBalance, decimal ClosingBalance, IReadOnlyList<BankStatementLineRequest> Lines)`. Existing `POST /clients/{c}/bank-statements` foots-or-422.
- `ReconciliationServiceExtensions.AddReconciliation(this IServiceCollection services, IConfiguration configuration)` — add the registry registration here. The Api csproj uses `FrameworkReference Microsoft.AspNetCore.App` + global usings incl. `Microsoft.Extensions.DependencyInjection`.
- `ReconciliationEndpoints.MapReconciliationEndpoints` — group `clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization()`. Add the import route here (no Program.cs change).
- Test host: `ReconciliationHostFixture.SeedSodClientAsync()→(clientId, controller, clerk, approver)`, `CashAccountId`. The import endpoint is read-only (any authenticated user; the Clerk works).
- Solution: `Accounting101.slnx` — add the two new projects with `dotnet sln Accounting101.slnx add <path>`.

---

### Task 1: `Accounting101.Interchange` project + framework core (TDD on the registry)

**Files:**
- Create: `Accounting101.Interchange/Accounting101.Interchange.csproj`, `InterchangeFormat.cs`, `Importer.cs`, `Exporter.cs`, `InterchangeRegistry.cs`
- Create (test): `Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj`, `InterchangeRegistryTests.cs`

**Interfaces:**
- Produces: `InterchangeFormat`, `IImporter<T>`, `ImportResult<T>`, `ImportOptions`, `IExporter<T>`, `ExportOptions`, `IInterchangeRegistry`, `InterchangeRegistry` — consumed by Tasks 2-5.

- [ ] **Step 1: Create the two projects**

`Accounting101.Interchange/Accounting101.Interchange.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- The segregated import/export subsystem: entity- and format-pluggable IImporter<T>/IExporter<T>.
       Zero dependencies — pure parsing/serialization, no HTTP/Mongo/module types. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

`Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj` (copy the exact `PackageReference` versions from `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj` — `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, and the `<Using Include="Xunit" />` item):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <!-- copy these versions verbatim from the Reconciliation.Tests csproj -->
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="COPY" />
    <PackageReference Include="xunit" Version="COPY" />
    <PackageReference Include="xunit.runner.visualstudio" Version="COPY" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Accounting101.Interchange\Accounting101.Interchange.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing registry tests**

`InterchangeRegistryTests.cs`:
```csharp
namespace Accounting101.Interchange.Tests;

public sealed class InterchangeRegistryTests
{
    private sealed record Thing(string Value);

    private sealed class FakeThingImporter(InterchangeFormat format) : IImporter<Thing>
    {
        public InterchangeFormat Format => format;
        public ImportResult<Thing> Import(Stream source, ImportOptions options) =>
            new([new Thing("x")], []);
    }

    [Fact]
    public void Resolves_a_registered_importer_by_entity_and_format()
    {
        InterchangeRegistry registry = new();
        FakeThingImporter csv = new(InterchangeFormat.Csv);
        registry.Register<Thing>(csv);

        Assert.Same(csv, registry.Resolve<Thing>(InterchangeFormat.Csv));
    }

    [Fact]
    public void Returns_null_for_an_unregistered_format_or_entity()
    {
        InterchangeRegistry registry = new();
        registry.Register<Thing>(new FakeThingImporter(InterchangeFormat.Csv));

        Assert.Null(registry.Resolve<Thing>(InterchangeFormat.Ofx));   // wrong format
        Assert.Null(registry.Resolve<string>(InterchangeFormat.Csv));  // wrong entity type
    }
}
```

- [ ] **Step 3: Run, verify it FAILS to compile**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --nologo`
Expected: FAIL (types not defined).

- [ ] **Step 4: Create the framework files**

`InterchangeFormat.cs`:
```csharp
namespace Accounting101.Interchange;

/// <summary>A data interchange format. CSV and OFX today; JSON/TSV are future additions.</summary>
public enum InterchangeFormat { Csv, Ofx }
```

`Importer.cs`:
```csharp
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
```

`Exporter.cs`:
```csharp
namespace Accounting101.Interchange;

/// <summary>Format-specific export options. Grows as exporters are added.</summary>
public sealed class ExportOptions;

/// <summary>Writes records of type <typeparamref name="T"/> to a destination stream in one format. The seam
/// exists now; implementations arrive in a later slice (read now, write later).</summary>
public interface IExporter<T>
{
    InterchangeFormat Format { get; }
    void Export(IEnumerable<T> records, Stream destination, ExportOptions options);
}
```

`InterchangeRegistry.cs`:
```csharp
namespace Accounting101.Interchange;

/// <summary>Resolves importers (and later exporters) by entity type + format.</summary>
public interface IInterchangeRegistry
{
    void Register<T>(IImporter<T> importer);
    IImporter<T>? Resolve<T>(InterchangeFormat format);
}

/// <summary>In-memory registry keyed by (entity type, format). Importers are stateless (options come per
/// call), so a single instance is shared. <see cref="CreateDefault"/> builds the app's standard registry.</summary>
public sealed class InterchangeRegistry : IInterchangeRegistry
{
    private readonly Dictionary<(Type, InterchangeFormat), object> _importers = [];

    public void Register<T>(IImporter<T> importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        _importers[(typeof(T), importer.Format)] = importer;
    }

    public IImporter<T>? Resolve<T>(InterchangeFormat format) =>
        _importers.TryGetValue((typeof(T), format), out object? importer) ? (IImporter<T>)importer : null;

    // CreateDefault() is added in Task 3, once CsvStatementImporter exists.
}
```

- [ ] **Step 5: Run, verify the registry tests PASS**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --filter "FullyQualifiedName~InterchangeRegistryTests" --nologo`
Expected: 2/2 PASS.

- [ ] **Step 6: Add the projects to the solution + commit**

```bash
dotnet sln Accounting101.slnx add Accounting101.Interchange/Accounting101.Interchange.csproj
dotnet sln Accounting101.slnx add Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj
git add Accounting101.Interchange/ Accounting101.Interchange.Tests/ Accounting101.slnx
git commit -m "$(cat <<'EOF'
feat(interchange): framework core — IImporter/IExporter + (T,format) registry

New zero-dependency Accounting101.Interchange project: InterchangeFormat,
IImporter<T>/IExporter<T>, ImportResult<T>/ImportOptions, and a registry that
resolves importers by entity type + format. Registry unit-tested. Exporter is
seam-only (read now, write later).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `DelimitedReader` (CSV primitive, TDD)

**Files:**
- Create: `Accounting101.Interchange/DelimitedReader.cs`
- Create (test): `Accounting101.Interchange.Tests/DelimitedReaderTests.cs`

**Interfaces:**
- Produces: `DelimitedReader.ReadRows` — consumed by Task 3.

- [ ] **Step 1: Write the failing tests**

`DelimitedReaderTests.cs`:
```csharp
namespace Accounting101.Interchange.Tests;

public sealed class DelimitedReaderTests
{
    [Fact]
    public void Reads_plain_rows()
    {
        var rows = DelimitedReader.ReadRows("a,b,c\n1,2,3\n", ',');
        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0]);
        Assert.Equal(["1", "2", "3"], rows[1]);
    }

    [Fact]
    public void Keeps_a_quoted_field_with_an_embedded_delimiter()
    {
        var rows = DelimitedReader.ReadRows("\"a,b\",c\n", ',');
        Assert.Equal(["a,b", "c"], rows[0]);
    }

    [Fact]
    public void Unescapes_doubled_quotes_inside_a_quoted_field()
    {
        var rows = DelimitedReader.ReadRows("\"she said \"\"hi\"\"\",x\n", ',');
        Assert.Equal(["she said \"hi\"", "x"], rows[0]);
    }

    [Fact]
    public void Keeps_an_embedded_newline_inside_quotes()
    {
        var rows = DelimitedReader.ReadRows("\"line1\nline2\",b\n", ',');
        Assert.Single(rows);
        Assert.Equal(["line1\nline2", "b"], rows[0]);
    }

    [Fact]
    public void Tolerates_crlf_and_a_missing_final_newline()
    {
        var rows = DelimitedReader.ReadRows("a,b\r\nc,d", ',');
        Assert.Equal(2, rows.Count);
        Assert.Equal(["c", "d"], rows[1]);
    }

    [Fact]
    public void Skips_blank_lines_and_honors_a_custom_delimiter()
    {
        var rows = DelimitedReader.ReadRows("a;b\n\nc;d\n", ';');
        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b"], rows[0]);
        Assert.Equal(["c", "d"], rows[1]);
    }
}
```

- [ ] **Step 2: Run, verify it FAILS**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --filter "FullyQualifiedName~DelimitedReaderTests" --nologo`
Expected: FAIL (DelimitedReader not defined).

- [ ] **Step 3: Create `DelimitedReader.cs`**

```csharp
using System.Text;

namespace Accounting101.Interchange;

/// <summary>A small RFC-4180-style delimited-text reader: quoted fields, doubled-quote escaping, embedded
/// delimiters/newlines inside quotes, CRLF/LF tolerance, and blank-line skipping. Entity-agnostic — returns
/// rows of string cells.</summary>
public static class DelimitedReader
{
    public static IReadOnlyList<IReadOnlyList<string>> ReadRows(string text, char delimiter)
    {
        List<IReadOnlyList<string>> rows = [];
        List<string> fields = [];
        StringBuilder cell = new();
        bool inQuotes = false;
        bool rowStarted = false;

        void EndField()
        {
            fields.Add(cell.ToString());
            cell.Clear();
        }

        void EndRow()
        {
            EndField();
            rows.Add(fields);
            fields = [];
            rowStarted = false;
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                cell.Append(c); i++; continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; rowStarted = true; i++; break;
                case '\r': i++; break;                                  // CRLF: ignore \r, \n ends the row
                case '\n':
                    if (rowStarted || cell.Length > 0 || fields.Count > 0) EndRow();  // skip wholly-blank lines
                    i++; break;
                default:
                    if (c == delimiter) { EndField(); rowStarted = true; }
                    else { cell.Append(c); rowStarted = true; }
                    i++; break;
            }
        }

        if (rowStarted || cell.Length > 0 || fields.Count > 0) EndRow();   // trailing row with no final newline
        return rows;
    }
}
```

- [ ] **Step 4: Run, verify the tests PASS**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --filter "FullyQualifiedName~DelimitedReaderTests" --nologo`
Expected: 6/6 PASS.

- [ ] **Step 5: Commit**

```bash
git add Accounting101.Interchange/DelimitedReader.cs Accounting101.Interchange.Tests/DelimitedReaderTests.cs
git commit -m "$(cat <<'EOF'
feat(interchange): DelimitedReader — RFC-4180-style CSV primitive

Quoted fields, doubled-quote escaping, embedded delimiters/newlines, CRLF/LF,
blank-line skipping, custom delimiter. Entity-agnostic rows-of-strings.
Unit-tested.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `ImportedStatement` DTOs + `CsvMapping` + `CsvStatementImporter` + `CreateDefault` (TDD)

**Files:**
- Create: `Accounting101.Interchange/ImportedStatement.cs`, `CsvMapping.cs`, `CsvStatementImporter.cs`
- Modify: `Accounting101.Interchange/InterchangeRegistry.cs` (add `CreateDefault`)
- Create (test): `Accounting101.Interchange.Tests/CsvStatementImporterTests.cs`

**Interfaces:**
- Consumes: `IImporter<T>`, `ImportResult<T>`, `ImportOptions`, `DelimitedReader`, `InterchangeRegistry` (Tasks 1-2).
- Produces: `ImportedStatement`, `ImportedLine`, `ColumnRef`, `CsvMapping`, `CsvStatementImporter`, `InterchangeRegistry.CreateDefault()` — consumed by Tasks 4-5.

- [ ] **Step 1: Write the failing importer tests**

`CsvStatementImporterTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run, verify it FAILS**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --filter "FullyQualifiedName~CsvStatementImporterTests" --nologo`
Expected: FAIL (types not defined).

- [ ] **Step 3: Create the DTOs + mapping**

`ImportedStatement.cs`:
```csharp
namespace Accounting101.Interchange;

/// <summary>A parsed bank statement — the neutral shape importers produce (NOT the Reconciliation domain's
/// BankStatement). Balances/date are populated only when the source format carries them (OFX does; CSV
/// usually doesn't).</summary>
public sealed record ImportedStatement(
    IReadOnlyList<ImportedLine> Lines, decimal? OpeningBalance, decimal? ClosingBalance,
    DateOnly? StatementDate, string? AccountHint);

/// <summary>One parsed statement line. Amount is signed from the bank's perspective (+ in, − out).</summary>
public sealed record ImportedLine(DateOnly Date, decimal Amount, string Description, string? Reference);
```

`CsvMapping.cs`:
```csharp
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
```

- [ ] **Step 4: Create `CsvStatementImporter.cs`**

```csharp
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
```

- [ ] **Step 5: Add `InterchangeRegistry.CreateDefault()`**

In `InterchangeRegistry.cs`, replace the `// CreateDefault()` comment with:
```csharp
    /// <summary>The app's standard registry: the bundled first-party importers. Add new importers here.</summary>
    public static InterchangeRegistry CreateDefault()
    {
        InterchangeRegistry registry = new();
        registry.Register<ImportedStatement>(new CsvStatementImporter());
        return registry;
    }
```

- [ ] **Step 6: Run, verify all importer tests PASS**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --nologo`
Expected: all PASS (2 registry + 6 delimited + 5 importer = 13).

- [ ] **Step 7: Commit**

```bash
git add Accounting101.Interchange/ImportedStatement.cs Accounting101.Interchange/CsvMapping.cs \
        Accounting101.Interchange/CsvStatementImporter.cs Accounting101.Interchange/InterchangeRegistry.cs \
        Accounting101.Interchange.Tests/CsvStatementImporterTests.cs
git commit -m "$(cat <<'EOF'
feat(interchange): ImportedStatement + configurable CsvStatementImporter

Neutral ImportedStatement/ImportedLine DTOs; CsvMapping (column refs by index or
header, signed-amount XOR debit/credit, date format, status filter); the importer
maps rows -> lines, warns-and-skips bad rows, drops status-excluded rows.
CreateDefault() registers it. Unit-tested incl. the Wells Fargo layout.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Reconciliation.Api — preview endpoint + DI wiring

**Files:**
- Create: `…/Accounting101.Banking.Reconciliation.Api/ImportResponses.cs`
- Modify: `…/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs` (add the import route + handler), `ReconciliationServiceExtensions.cs` (register the registry), `Accounting101.Banking.Reconciliation.Api.csproj` (reference Interchange)

**Interfaces:**
- Consumes: `IInterchangeRegistry`, `InterchangeRegistry.CreateDefault`, `IImporter<ImportedStatement>`, `ImportedStatement`, `ImportOptions`, `CsvMapping` (Tasks 1-3); `BankStatementLineRequest` (Slice 1).

- [ ] **Step 1: Reference the Interchange project**

In `Accounting101.Banking.Reconciliation.Api.csproj`, add to the `ProjectReference` ItemGroup:
```xml
    <ProjectReference Include="..\..\..\..\Accounting101.Interchange\Accounting101.Interchange.csproj" />
```
(Confirm the relative depth — the Api project sits at `Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/`, and `Accounting101.Interchange/` is at the repo root, so `..\..\..\..\` reaches the root. Match the existing `..\..\..\..\Backend\...` references' depth.)

- [ ] **Step 2: Create the preview response DTOs**

`ImportResponses.cs`:
```csharp
namespace Accounting101.Banking.Reconciliation.Api;

/// <summary>The parse-to-preview result of importing a bank export: one or more parsed statements (a file
/// may carry several) plus non-fatal warnings. Nothing is created — the client reviews, supplies any missing
/// balances, and submits to POST /bank-statements.</summary>
public sealed record ImportPreviewResponse(IReadOnlyList<StatementPreview> Statements, IReadOnlyList<string> Warnings);

public sealed record StatementPreview(
    IReadOnlyList<BankStatementLineRequest> Lines,
    decimal? DetectedOpeningBalance, decimal? DetectedClosingBalance,
    DateOnly? StatementDate, string? AccountHint);
```

- [ ] **Step 3: Register the registry**

In `ReconciliationServiceExtensions.cs`, add `using Accounting101.Interchange;` at the top, and inside `AddReconciliation` (after the existing registrations, before `return services;`):
```csharp
        // Import/export framework (Slice 4a) — the default registry (CSV statement importer registered).
        services.AddSingleton<IInterchangeRegistry>(InterchangeRegistry.CreateDefault());
```

- [ ] **Step 4: Add the import endpoint**

In `ReconciliationEndpoints.cs`, add these usings at the top (alongside the existing ones):
```csharp
using System.Text.Json;
using Accounting101.Interchange;
using Microsoft.AspNetCore.Mvc;
```
Register the route inside `MapReconciliationEndpoints` (after the existing `/bank-statements` GET routes):
```csharp
        clients.MapPost("/bank-statements/import", ImportStatement).DisableAntiforgery();
```
and add the handler:
```csharp
    private static async Task<IResult> ImportStatement(
        Guid clientId, IFormFile? file, [FromForm] string? format, [FromForm] string? mapping,
        IInterchangeRegistry registry, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Results.Problem("A non-empty file is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        if (!Enum.TryParse(format, ignoreCase: true, out InterchangeFormat fmt))
            return Results.Problem($"Unsupported or missing format '{format}'.", statusCode: StatusCodes.Status400BadRequest);

        IImporter<ImportedStatement>? importer = registry.Resolve<ImportedStatement>(fmt);
        if (importer is null)
            return Results.Problem($"No statement importer is registered for format '{fmt}'.", statusCode: StatusCodes.Status400BadRequest);

        CsvMapping? csvMapping = null;
        if (fmt == InterchangeFormat.Csv)
        {
            if (string.IsNullOrWhiteSpace(mapping))
                return Results.Problem("A CSV 'mapping' is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
            try
            {
                csvMapping = JsonSerializer.Deserialize<CsvMapping>(mapping, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException ex)
            {
                return Results.Problem($"Invalid mapping JSON: {ex.Message}", statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            if (csvMapping is null)
                return Results.Problem("A CSV 'mapping' is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        try
        {
            await using Stream stream = file.OpenReadStream();
            ImportResult<ImportedStatement> result = importer.Import(stream, new ImportOptions { Csv = csvMapping });
            List<StatementPreview> statements = result.Records
                .Select(s => new StatementPreview(
                    s.Lines.Select(l => new BankStatementLineRequest(l.Date, l.Amount, l.Description, l.Reference)).ToList(),
                    s.OpeningBalance, s.ClosingBalance, s.StatementDate, s.AccountHint))
                .ToList();
            return Results.Ok(new ImportPreviewResponse(statements, result.Warnings));
        }
        catch (ArgumentException ex) // invalid mapping (no amount columns, missing header, header-name without HasHeader)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
```

- [ ] **Step 5: Build the host, verify it composes**

Run: `dotnet build Accounting101.Host/Accounting101.Host.csproj --nologo`
Expected: Build succeeded (the Interchange ref resolves; the registry registers; the endpoint compiles).

- [ ] **Step 6: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ImportResponses.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationServiceExtensions.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/Accounting101.Banking.Reconciliation.Api.csproj
git commit -m "$(cat <<'EOF'
feat(reconciliation): parse-to-preview /bank-statements/import endpoint

Multipart import endpoint that resolves an IImporter<ImportedStatement> by
format, parses the upload, and returns a preview (lines + detected balances +
warnings) — creating nothing. Registers the Interchange default registry in
AddReconciliation. CSV mapping arrives as a JSON form field.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: E2E — upload CSV → preview → submit round-trip

**Files:**
- Create: `…/Accounting101.Banking.Reconciliation.Tests/StatementImportE2eTests.cs`

**Interfaces:**
- Consumes: the full module + host (Tasks 1-4); the existing `ReconciliationHostFixture`, `RecordBankStatementRequest`/`BankStatementLineRequest`, the import response DTOs.

- [ ] **Step 1: Write the E2E**

`StatementImportE2eTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Accounting101.Banking.Reconciliation;
using Accounting101.Banking.Reconciliation.Api;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>End-to-end import: upload a Wells-Fargo-shaped CSV (synthetic), get a parse-to-preview that
/// excludes Pending rows and warns on a bad row, then submit the previewed lines to the existing statement
/// endpoint — proving the round-trip into a real, footing statement.</summary>
public sealed class StatementImportE2eTests(ReconciliationHostFixture fixture) : IClassFixture<ReconciliationHostFixture>
{
    private const string WfMapping =
        """
        {"date":{"header":"DATE"},"amount":{"header":"AMOUNT"},"description":{"header":"DESCRIPTION"},
         "reference":{"header":"CHECK #"},"status":{"header":"STATUS"},"excludeStatuses":["Pending"],
         "dateFormat":"MM/dd/yyyy","hasHeader":true}
        """;

    private static MultipartFormDataContent Multipart(string csv, string format, string? mapping)
    {
        MultipartFormDataContent content = [];
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(fileContent, "file", "Checking.csv");
        content.Add(new StringContent(format), "format");
        if (mapping is not null) content.Add(new StringContent(mapping), "mapping");
        return content;
    }

    [Fact]
    public async Task Imports_a_csv_to_a_preview_then_submits_a_footing_statement()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();

        string csv =
            "\"DATE\",\"DESCRIPTION\",\"AMOUNT\",\"CHECK #\",\"STATUS\"\n" +
            "\"06/29/2026\",\"PURCHASE COFFEE\",\"-4.50\",\"\",\"Pending\"\n" +    // excluded
            "\"06/28/2026\",\"PAYROLL DEPOSIT\",\"1200.00\",\"\",\"Posted\"\n" +
            "\"06/27/2026\",\"CHECK 1021\",\"-300.00\",\"1021\",\"Posted\"\n";

        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import?ignored=1", Multipart(csv, "csv", WfMapping));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        ImportPreviewResponse preview = (await resp.Content.ReadFromJsonAsync<ImportPreviewResponse>())!;

        Assert.Single(preview.Statements);
        IReadOnlyList<BankStatementLineRequest> lines = preview.Statements[0].Lines;
        Assert.Equal(2, lines.Count);                                  // Pending excluded
        Assert.Equal(1200.00m, lines[0].Amount);
        Assert.Equal(-300.00m, lines[1].Amount);
        Assert.Empty(preview.Warnings);

        // Submit the previewed lines as a real statement: opening 0, closing = Σ lines (it foots).
        decimal closing = lines.Sum(l => l.Amount);
        HttpResponseMessage create = await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
            new RecordBankStatementRequest(fixture.CashAccountId, new DateOnly(2026, 6, 30), 0m, closing, lines));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task A_missing_mapping_is_rejected_422()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import", Multipart("DATE,DESCRIPTION,AMOUNT\n", "csv", mapping: null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task An_unparseable_row_lands_in_warnings_and_the_rest_parse()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        string csv =
            "\"DATE\",\"DESCRIPTION\",\"AMOUNT\",\"CHECK #\",\"STATUS\"\n" +
            "\"06/28/2026\",\"GOOD\",\"10.00\",\"\",\"Posted\"\n" +
            "\"NOTADATE\",\"BAD\",\"5.00\",\"\",\"Posted\"\n";

        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import", Multipart(csv, "csv", WfMapping));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        ImportPreviewResponse preview = (await resp.Content.ReadFromJsonAsync<ImportPreviewResponse>())!;
        Assert.Single(preview.Statements[0].Lines);                    // only GOOD
        Assert.Single(preview.Warnings);                               // the NOTADATE row
    }
}
```

> Implementer note: this exercises the multipart upload through the real host with the antiforgery-disabled endpoint. If the upload is rejected for antiforgery (400 with an antiforgery message) or the Clerk is refused (403), that is a finding — the import is a read-only parse any authenticated user may call; STOP and report DONE_WITH_CONCERNS with the exact status, don't paper over it. If a balance/line assertion differs, report observed-vs-expected.

- [ ] **Step 2: Run the full Interchange + Reconciliation projects**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --nologo`
Expected: 13/13 PASS.
Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --nologo`
Expected: all PASS (30 existing + 3 new E2E = 33).

- [ ] **Step 3: Build the whole solution — confirm no regressions**

Run: `dotnet build Accounting101.slnx --nologo`
Expected: Build succeeded (only pre-existing NU19xx transitive warnings).

- [ ] **Step 4: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/StatementImportE2eTests.cs
git commit -m "$(cat <<'EOF'
test(reconciliation): CSV import E2E — preview, exclude pending, round-trip

Uploads a Wells-Fargo-shaped CSV (synthetic), asserts the preview excludes
Pending rows and surfaces a warning for a bad row, then submits the previewed
lines to the existing statement endpoint to prove the round-trip foots. Plus:
missing mapping -> 422.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- `Accounting101.Interchange` framework (IImporter/IExporter, ImportResult, options, registry) → Task 1. ✓
- Generic `DelimitedReader` → Task 2. ✓
- `ImportedStatement` DTOs + configurable `CsvMapping` (signed XOR debit/credit, status filter) + `CsvStatementImporter` (warn-and-skip) → Task 3. ✓
- `CreateDefault` registry factory (zero-dep DI seam) → Task 3, wired in Task 4. ✓
- Parse-to-preview endpoint (multipart, creates nothing, maps to BankStatementLineRequest) → Task 4. ✓
- E2E round-trip + 422 + warnings → Task 5. ✓
- IExporter seam present, no writers → Task 1. ✓
- WF layout as canonical test (synthetic) → Tasks 3 + 5. ✓

**2. Placeholder scan:** No TBD/TODO; full code for every file; commands explicit. The test-csproj `Version="COPY"` markers are an explicit instruction to copy verbatim from a named existing csproj (versions are environment-specific), not a vague placeholder.

**3. Type consistency:** `IImporter<T>`/`ImportResult<T>`/`ImportOptions`/`IInterchangeRegistry`/`InterchangeRegistry` (Task 1) consumed unchanged in 2-5. `DelimitedReader.ReadRows(string, char)` (Task 2) used by `CsvStatementImporter` (Task 3). `ImportedStatement(Lines, OpeningBalance, ClosingBalance, StatementDate, AccountHint)`, `ImportedLine(Date, Amount, Description, Reference)`, `ColumnRef(int? Index, string? Header)`, `CsvMapping(Date, Amount, Debit, Credit, Description, Reference, DateFormat, HasHeader, Delimiter?, Status?, ExcludeStatuses?)` defined in Task 3, consumed by Task 4 (JSON deserialization) and Task 5 (the mapping JSON matches the record property names under Web/camelCase defaults). `BankStatementLineRequest`/`RecordBankStatementRequest` match the Slice 1 source. `ImportPreviewResponse`/`StatementPreview` defined in Task 4, consumed in Task 5. `InterchangeRegistry.CreateDefault()` defined in Task 3, used in Task 4. ✓
