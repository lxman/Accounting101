# OFX 1.x SGML Statement Import — Slice 4b — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tolerant hand-rolled `OfxStatementImporter` (Format = Ofx) that parses an OFX 1.x SGML bank statement into one or more `ImportedStatement`s, registered into the existing Interchange framework, plus the one endpoint touch to refuse OFX 2.x XML cleanly (422) until slice 4c.

**Architecture:** All parsing lives in `Accounting101.Interchange` (still zero-dependency). A pure `OfxScanner` holds the risky low-level SGML helpers (leaf-scan, block extraction, date/amount parse, header-strip + dialect detection), heavily unit-tested. `OfxStatementImporter` assembles them into `ImportedStatement`s and registers in `InterchangeRegistry.CreateDefault()`. The 4a preview endpoint already routes `format=ofx` (no `mapping` needed); the only endpoint change is a `NotSupportedException`→422 catch for the XML-refusal path.

**Tech Stack:** C#/.NET 10, xUnit, EphemeralMongo for E2E. Extends Slice 4a (the Interchange framework + CSV import + preview endpoint).

## Global Constraints

- New code only in `Accounting101.Interchange/` (+ its Tests) and one catch-clause in `…Reconciliation.Api/ReconciliationEndpoints.cs`. The Interchange project stays **zero-dependency**.
- **No GL mutation, no statement creation** — still parse-to-preview. No framework/CSV/registry-shape change (just one more importer registered for the same `ImportedStatement` entity under Format = Ofx).
- The OFX parser is **tolerant**: header optional; unclosed leaves read to the next `<`; multiple `<STMTRS>` → multiple statements; closing balance from `LEDGERBAL` (NOT `AVAILBAL`); a transaction with a bad date/amount → a **warning** and is skipped (never a crash); a non-zero status `<CODE>` or no `<STMTRS>` → an empty/partial result + a warning (never a 500).
- OFX **2.x XML** is detected and refused with `NotSupportedException("…not yet supported (slice 4c)…")` → mapped to 422.
- Decisions: `Reference` = `FITID`; `Description` = `NAME` (fall back to / combine with `MEMO`); `OpeningBalance` left null (OFX carries no opening).
- Money is `decimal`. Commit trailer, verbatim, on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Confirmed surface (from Slices 4a)

- `IImporter<T>` (`InterchangeFormat Format`, `ImportResult<T> Import(Stream, ImportOptions)`), `ImportResult<T>(IReadOnlyList<T> Records, IReadOnlyList<string> Warnings)`, `ImportOptions { CsvMapping? Csv }`, `InterchangeFormat { Csv, Ofx }` (all `Accounting101.Interchange`, zero-dep).
- `ImportedStatement(IReadOnlyList<ImportedLine> Lines, decimal? OpeningBalance, decimal? ClosingBalance, DateOnly? StatementDate, string? AccountHint)`; `ImportedLine(DateOnly Date, decimal Amount, string Description, string? Reference)`.
- `InterchangeRegistry.CreateDefault()` currently registers `CsvStatementImporter` for `ImportedStatement` — add the OFX importer alongside.
- The import endpoint (`…Reconciliation.Api/ReconciliationEndpoints.cs`, `ImportStatement`): for `format=ofx` it builds `new ImportOptions { Csv = null }` and resolves `IImporter<ImportedStatement>` by format; the `try { … importer.Import(stream, …) … } catch (ArgumentException ex) { 422 }` block (the only catch) must gain a `NotSupportedException`→422 catch.

---

### Task 1: `OfxScanner` — tolerant low-level SGML helpers (TDD)

**Files:**
- Create: `Accounting101.Interchange/OfxScanner.cs`
- Create (test): `Accounting101.Interchange.Tests/OfxScannerTests.cs`

**Interfaces:**
- Produces: `OfxScanner.Leaf`, `.Blocks`, `.TryParseOfxDate`, `.TryParseOfxAmount`, `.StripHeaderAndDetectDialect` — consumed by Task 2.

- [ ] **Step 1: Write the failing tests**

`OfxScannerTests.cs`:
```csharp
namespace Accounting101.Interchange.Tests;

public sealed class OfxScannerTests
{
    [Fact]
    public void Leaf_reads_an_unclosed_value_up_to_the_next_tag()
    {
        Assert.Equal("INFO", OfxScanner.Leaf("<CODE>0<SEVERITY>INFO</STATUS>", "SEVERITY"));
        Assert.Equal("-12.34", OfxScanner.Leaf("<TRNAMT>-12.34<FITID>X1", "TRNAMT"));
        Assert.Equal("1234", OfxScanner.Leaf("<INTU.BID>1234<INTU.USERID>u", "INTU.BID")); // dotted tag
        Assert.Null(OfxScanner.Leaf("<NAME>x", "MISSING"));
    }

    [Fact]
    public void Blocks_returns_each_closed_aggregate_body()
    {
        string s = "<STMTTRN><FITID>A</STMTTRN><STMTTRN><FITID>B</STMTTRN>";
        var blocks = OfxScanner.Blocks(s, "STMTTRN");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("A", OfxScanner.Leaf(blocks[0], "FITID"));
        Assert.Equal("B", OfxScanner.Leaf(blocks[1], "FITID"));
    }

    [Fact]
    public void TryParseOfxDate_takes_the_leading_eight_digits()
    {
        Assert.True(OfxScanner.TryParseOfxDate("20260628", out DateOnly d1));
        Assert.Equal(new DateOnly(2026, 6, 28), d1);
        Assert.True(OfxScanner.TryParseOfxDate("20260628120000.000[-5:EST]", out DateOnly d2));
        Assert.Equal(new DateOnly(2026, 6, 28), d2);
        Assert.False(OfxScanner.TryParseOfxDate("2026", out _));
        Assert.False(OfxScanner.TryParseOfxDate("", out _));
    }

    [Fact]
    public void TryParseOfxAmount_handles_dot_and_comma_decimals()
    {
        Assert.True(OfxScanner.TryParseOfxAmount("-12.34", out decimal a1)); Assert.Equal(-12.34m, a1);
        Assert.True(OfxScanner.TryParseOfxAmount("1234.56", out decimal a2)); Assert.Equal(1234.56m, a2);
        Assert.True(OfxScanner.TryParseOfxAmount("-12,34", out decimal a3)); Assert.Equal(-12.34m, a3); // locale comma
        Assert.False(OfxScanner.TryParseOfxAmount("", out _));
        Assert.False(OfxScanner.TryParseOfxAmount("abc", out _));
    }

    [Fact]
    public void StripHeaderAndDetectDialect_strips_1x_header_and_flags_2x()
    {
        string wf = "OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\n<OFX><BANK></OFX>";
        string body = OfxScanner.StripHeaderAndDetectDialect(wf, out bool wfXml);
        Assert.StartsWith("<OFX", body);
        Assert.False(wfXml);

        OfxScanner.StripHeaderAndDetectDialect("<OFX><BANK></OFX>", out bool headerlessXml); // no preamble
        Assert.False(headerlessXml);

        OfxScanner.StripHeaderAndDetectDialect("<?xml version=\"1.0\"?><?OFX OFXHEADER=\"200\" VERSION=\"203\"?><OFX/>", out bool xml);
        Assert.True(xml);
    }
}
```

- [ ] **Step 2: Run, verify it FAILS**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --filter "FullyQualifiedName~OfxScannerTests" --nologo`
Expected: FAIL (OfxScanner not defined).

- [ ] **Step 3: Create `OfxScanner.cs`**

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting101.Interchange;

/// <summary>Tolerant low-level scanning for OFX 1.x SGML — where leaf tags are unclosed (a value runs to the
/// next '&lt;') while aggregates (STMTTRN, STMTRS, LEDGERBAL, …) are closed. Pure string helpers; no I/O.</summary>
public static class OfxScanner
{
    /// <summary>The value of the first <c>&lt;tag&gt;</c> in <paramref name="scope"/>, read up to the next
    /// '&lt;' (handles unclosed leaves like <c>&lt;SEVERITY&gt;INFO&lt;/STATUS&gt;</c>). Null if absent.</summary>
    public static string? Leaf(string scope, string tag)
    {
        string open = "<" + tag + ">";
        int i = scope.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int start = i + open.Length;
        int end = scope.IndexOf('<', start);
        if (end < 0) end = scope.Length;
        return scope[start..end].Trim();
    }

    /// <summary>The inner text of each <c>&lt;aggregate&gt;…&lt;/aggregate&gt;</c> (closed aggregates; the
    /// shapes we read — STMTRS, STMTTRN, LEDGERBAL — do not self-nest, so first-close scanning is correct).</summary>
    public static IReadOnlyList<string> Blocks(string scope, string aggregate)
    {
        string open = "<" + aggregate + ">";
        string close = "</" + aggregate + ">";
        List<string> blocks = [];
        int pos = 0;
        while (true)
        {
            int i = scope.IndexOf(open, pos, StringComparison.OrdinalIgnoreCase);
            if (i < 0) break;
            int contentStart = i + open.Length;
            int j = scope.IndexOf(close, contentStart, StringComparison.OrdinalIgnoreCase);
            if (j < 0) break;
            blocks.Add(scope[contentStart..j]);
            pos = j + close.Length;
        }
        return blocks;
    }

    /// <summary>An OFX date (<c>YYYYMMDD</c> optionally followed by time / fractional / [tz]) → its date part,
    /// from the leading 8 digits. False if fewer than 8 leading digits.</summary>
    public static bool TryParseOfxDate(string? raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        int n = 0;
        while (n < raw.Length && char.IsDigit(raw[n])) n++;
        return n >= 8 && DateOnly.TryParseExact(raw[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>An OFX amount (signed decimal). Invariant '.' first; if that fails and a ',' is present without
    /// a '.', retry with ',' as the decimal separator (non-US-locale exports).</summary>
    public static bool TryParseOfxAmount(string? raw, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)) return true;
        if (raw.Contains(',') && !raw.Contains('.'))
            return decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
        return false;
    }

    /// <summary>Returns the text from the first <c>&lt;OFX</c> onward (drops any <c>OFXHEADER:</c>/<c>DATA:</c>
    /// preamble) and flags whether the content is OFX 2.x (XML) rather than 1.x SGML.</summary>
    public static string StripHeaderAndDetectDialect(string text, out bool isXml)
    {
        isXml = text.Contains("<?xml", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<?OFX", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, "OFXHEADER\\s*[:=]\\s*\"?2", RegexOptions.IgnoreCase)
            || Regex.IsMatch(text, "\\bVERSION\\s*[:=]\\s*\"?2\\d\\d", RegexOptions.IgnoreCase);
        int i = text.IndexOf("<OFX", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? text : text[i..];
    }
}
```

- [ ] **Step 4: Run, verify the scanner tests PASS**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --filter "FullyQualifiedName~OfxScannerTests" --nologo`
Expected: 5/5 PASS.

- [ ] **Step 5: Commit**

```bash
git add Accounting101.Interchange/OfxScanner.cs Accounting101.Interchange.Tests/OfxScannerTests.cs
git commit -m "$(cat <<'EOF'
feat(interchange): OfxScanner — tolerant OFX 1.x SGML scan helpers

Leaf (unclosed-leaf read to next tag), Blocks (closed-aggregate bodies),
TryParseOfxDate (leading 8 digits), TryParseOfxAmount (dot or comma decimal),
and StripHeaderAndDetectDialect (drop preamble, flag 2.x XML). Unit-tested.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `OfxStatementImporter` + register (TDD)

**Files:**
- Create: `Accounting101.Interchange/OfxStatementImporter.cs`
- Modify: `Accounting101.Interchange/InterchangeRegistry.cs` (register the OFX importer in `CreateDefault`)
- Create (test): `Accounting101.Interchange.Tests/OfxStatementImporterTests.cs`

**Interfaces:**
- Consumes: `OfxScanner` (Task 1), `IImporter<T>`/`ImportResult<T>`/`ImportOptions`/`ImportedStatement`/`ImportedLine` (Slice 4a).
- Produces: `OfxStatementImporter` — consumed by the endpoint (Task 3) via the registry.

- [ ] **Step 1: Write the failing importer tests**

`OfxStatementImporterTests.cs`:
```csharp
using System.Text;

namespace Accounting101.Interchange.Tests;

public sealed class OfxStatementImporterTests
{
    private static ImportResult<ImportedStatement> Import(string ofx, Encoding? enc = null)
    {
        using MemoryStream stream = new((enc ?? Encoding.UTF8).GetBytes(ofx));
        return new OfxStatementImporter().Import(stream, new ImportOptions());
    }

    // Wells-Fargo-shaped: full 1.x header, LEDGERBAL + AVAILBAL, 2 transactions, a DTPOSTED with time+offset.
    private const string WfStyle =
        "OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\nENCODING:USASCII\nCHARSET:1252\n" +
        "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD" +
        "<BANKACCTFROM><BANKID>121000248<ACCTID>1234567890<ACCTTYPE>CHECKING</BANKACCTFROM>" +
        "<BANKTRANLIST><DTSTART>20260601<DTEND>20260630" +
        "<STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260628120000.000[-5:EST]<TRNAMT>1200.00<FITID>A1<NAME>PAYROLL</STMTTRN>" +
        "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260627<TRNAMT>-300.00<FITID>A2<NAME>CHECK 1021<MEMO>RENT</STMTTRN>" +
        "</BANKTRANLIST>" +
        "<LEDGERBAL><BALAMT>900.00<DTASOF>20260630</LEDGERBAL>" +
        "<AVAILBAL><BALAMT>850.00<DTASOF>20260630</AVAILBAL>" +
        "</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

    [Fact]
    public void Parses_a_wells_fargo_style_statement_with_ledger_balance_and_account()
    {
        ImportResult<ImportedStatement> result = Import(WfStyle);
        Assert.Single(result.Records);
        ImportedStatement s = result.Records[0];
        Assert.Equal("1234567890", s.AccountHint);
        Assert.Equal(900.00m, s.ClosingBalance);                       // LEDGERBAL, not AVAILBAL (850)
        Assert.Null(s.OpeningBalance);
        Assert.Equal(new DateOnly(2026, 6, 30), s.StatementDate);
        Assert.Equal(2, s.Lines.Count);
        Assert.Equal(1200.00m, s.Lines[0].Amount);
        Assert.Equal("PAYROLL", s.Lines[0].Description);
        Assert.Equal("A1", s.Lines[0].Reference);                      // FITID
        Assert.Equal(new DateOnly(2026, 6, 28), s.Lines[0].Date);      // time/offset stripped
        Assert.Equal(-300.00m, s.Lines[1].Amount);
        Assert.Equal("CHECK 1021 — RENT", s.Lines[1].Description);     // NAME + MEMO
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parses_a_headerless_locale_file_with_comma_decimals()
    {
        string fra =
            "<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20160414211744<LANGUAGE>FRA</SONRS></SIGNONMSGSRSV1>" +
            "<BANKMSGSRSV1><STMTTRNRS><STMTRS>" +
            "<BANKACCTFROM><BANKID>30002<BRANCHID>00550<ACCTID>FR761234<ACCTTYPE>CHECKING</BANKACCTFROM>" +
            "<BANKTRANLIST><STMTTRN><DTPOSTED>20160410<TRNAMT>-12,34<FITID>F1<NAME>CAFE</STMTTRN></BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>1000,00<DTASOF>20160414</LEDGERBAL>" +
            "</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(fra);
        Assert.Single(result.Records);
        Assert.Equal("FR761234", result.Records[0].AccountHint);
        Assert.Equal(1000.00m, result.Records[0].ClosingBalance);
        Assert.Single(result.Records[0].Lines);
        Assert.Equal(-12.34m, result.Records[0].Lines[0].Amount);       // comma decimal
    }

    [Fact]
    public void A_file_with_multiple_statements_yields_multiple_records()
    {
        string multi =
            "<OFX><BANKMSGSRSV1>" +
            "<STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>ACCT-A<ACCTTYPE>CHECKING</BANKACCTFROM>" +
            "<BANKTRANLIST><STMTTRN><DTPOSTED>20260601<TRNAMT>10.00<FITID>X1<NAME>A1</STMTTRN></BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>10.00<DTASOF>20260630</LEDGERBAL></STMTRS></STMTTRNRS>" +
            "<STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>ACCT-B<ACCTTYPE>SAVINGS</BANKACCTFROM>" +
            "<BANKTRANLIST><STMTTRN><DTPOSTED>20260602<TRNAMT>20.00<FITID>Y1<NAME>B1</STMTTRN></BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>20.00<DTASOF>20260630</LEDGERBAL></STMTRS></STMTTRNRS>" +
            "</BANKMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(multi);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("ACCT-A", result.Records[0].AccountHint);
        Assert.Equal("ACCT-B", result.Records[1].AccountHint);
    }

    [Fact]
    public void A_transaction_missing_fitid_parses_and_an_empty_amount_warns_and_skips()
    {
        string emptyTags =
            "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>E1<ACCTTYPE>CHECKING</BANKACCTFROM>" +
            "<BANKTRANLIST>" +
            "<STMTTRN><DTPOSTED>20260601<TRNAMT>5.00<NAME>GOOD-NO-FITID</STMTTRN>" +
            "<STMTTRN><DTPOSTED>20260602<TRNAMT><NAME>EMPTY-AMOUNT</STMTTRN>" +
            "</BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>5.00<DTASOF>20260630</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(emptyTags);
        Assert.Single(result.Records[0].Lines);                         // only the good txn
        Assert.Null(result.Records[0].Lines[0].Reference);              // missing FITID → null
        Assert.Single(result.Warnings);                                 // the empty-amount txn
    }

    [Fact]
    public void An_ofx_2x_xml_input_is_refused()
    {
        string xml =
            "<?xml version=\"1.0\"?><?OFX OFXHEADER=\"200\" VERSION=\"203\" SECURITY=\"NONE\"?>" +
            "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));
        Assert.Throws<NotSupportedException>(() => new OfxStatementImporter().Import(stream, new ImportOptions()));
    }

    [Fact]
    public void An_error_response_with_no_statement_warns_instead_of_throwing()
    {
        string err =
            "<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>15500<SEVERITY>ERROR<MESSAGE>Invalid login</STATUS></SONRS></SIGNONMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(err);
        Assert.Empty(result.Records);
        Assert.Single(result.Warnings);
        Assert.Contains("15500", result.Warnings[0]);
    }
}
```

- [ ] **Step 2: Run, verify it FAILS**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --filter "FullyQualifiedName~OfxStatementImporterTests" --nologo`
Expected: FAIL (OfxStatementImporter not defined).

- [ ] **Step 3: Create `OfxStatementImporter.cs`**

```csharp
using System.Text;

namespace Accounting101.Interchange;

/// <summary>Parses an OFX 1.x SGML bank-statement file (the Wells Fargo QFX dialect and friends) into one
/// <see cref="ImportedStatement"/> per &lt;STMTRS&gt;. Tolerant: header optional, unclosed leaves, multiple
/// statements, LEDGERBAL (not AVAILBAL) closing balance, bad transactions warned-and-skipped, malformed-but-
/// readable files degrade to warnings. OFX 2.x XML is refused (slice 4c). No balances opening (OFX has none).</summary>
public sealed class OfxStatementImporter : IImporter<ImportedStatement>
{
    public InterchangeFormat Format => InterchangeFormat.Ofx;

    public ImportResult<ImportedStatement> Import(Stream source, ImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);

        using MemoryStream ms = new();
        source.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        // Peek the (ASCII) header to honor a CHARSET:1252 declaration; otherwise UTF-8 (USASCII reads fine as UTF-8).
        string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        Encoding encoding = head.Contains("CHARSET:1252", StringComparison.OrdinalIgnoreCase) ? Encoding.Latin1 : Encoding.UTF8;
        string text = encoding.GetString(bytes);

        string body = OfxScanner.StripHeaderAndDetectDialect(text, out bool isXml);
        if (isXml)
            throw new NotSupportedException("OFX 2.x XML import is not yet supported (slice 4c). Re-export as OFX/QFX 1.x, or import the CSV.");

        List<ImportedStatement> statements = [];
        List<string> warnings = [];

        IReadOnlyList<string> stmtBlocks = OfxScanner.Blocks(body, "STMTRS");
        if (stmtBlocks.Count == 0)
        {
            string? code = OfxScanner.Leaf(body, "CODE");
            warnings.Add(code is not null && code != "0"
                ? $"OFX response carried status code {code}; no bank statement (<STMTRS>) found."
                : "No bank statement (<STMTRS>) found in the OFX file.");
            return new ImportResult<ImportedStatement>([], warnings);
        }

        for (int si = 0; si < stmtBlocks.Count; si++)
        {
            string stmt = stmtBlocks[si];
            string? acctId = Clean(OfxScanner.Leaf(stmt, "ACCTID"));

            decimal? closing = null;
            DateOnly? asOf = null;
            IReadOnlyList<string> ledger = OfxScanner.Blocks(stmt, "LEDGERBAL");
            if (ledger.Count > 0)
            {
                if (OfxScanner.TryParseOfxAmount(OfxScanner.Leaf(ledger[0], "BALAMT"), out decimal bal)) closing = bal;
                if (OfxScanner.TryParseOfxDate(OfxScanner.Leaf(ledger[0], "DTASOF"), out DateOnly d)) asOf = d;
            }

            List<ImportedLine> lines = [];
            IReadOnlyList<string> txns = OfxScanner.Blocks(stmt, "STMTTRN");
            for (int ti = 0; ti < txns.Count; ti++)
            {
                string tx = txns[ti];
                string? fitid = Clean(OfxScanner.Leaf(tx, "FITID"));
                string id = fitid ?? $"#{ti + 1}";
                string acct = acctId ?? $"#{si + 1}";

                if (!OfxScanner.TryParseOfxDate(OfxScanner.Leaf(tx, "DTPOSTED"), out DateOnly date))
                {
                    warnings.Add($"Statement {acct}, transaction {id}: missing or unparseable DTPOSTED — skipped.");
                    continue;
                }
                if (!OfxScanner.TryParseOfxAmount(OfxScanner.Leaf(tx, "TRNAMT"), out decimal amount))
                {
                    warnings.Add($"Statement {acct}, transaction {id}: missing or unparseable TRNAMT — skipped.");
                    continue;
                }
                string description = Describe(OfxScanner.Leaf(tx, "NAME"), OfxScanner.Leaf(tx, "MEMO"));
                lines.Add(new ImportedLine(date, amount, description, fitid));
            }

            statements.Add(new ImportedStatement(lines, null, closing, asOf, acctId));
        }

        return new ImportResult<ImportedStatement>(statements, warnings);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Describe(string? name, string? memo)
    {
        name = Clean(name);
        memo = Clean(memo);
        if (name is null) return memo ?? string.Empty;
        if (memo is null || string.Equals(name, memo, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{name} — {memo}";
    }
}
```

- [ ] **Step 4: Register it in `CreateDefault`**

In `InterchangeRegistry.cs`, inside `CreateDefault()`, add the OFX importer next to the CSV one:
```csharp
        registry.Register<ImportedStatement>(new OfxStatementImporter());
```
(Both register for `ImportedStatement` under different `Format` values — Csv and Ofx — so they coexist.)

- [ ] **Step 5: Run, verify the importer tests PASS + whole project green**

Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --nologo`
Expected: all PASS (13 from 4a + 5 scanner + 6 importer = 24).

- [ ] **Step 6: Commit**

```bash
git add Accounting101.Interchange/OfxStatementImporter.cs Accounting101.Interchange/InterchangeRegistry.cs \
        Accounting101.Interchange.Tests/OfxStatementImporterTests.cs
git commit -m "$(cat <<'EOF'
feat(interchange): OfxStatementImporter (1.x SGML) + register

Parses OFX 1.x SGML into ImportedStatement(s): per-STMTRS account + LEDGERBAL
closing, per-STMTTRN line (FITID reference, NAME+MEMO description, signed
amount, date), warn-and-skip bad txns, comma-decimal + header-optional +
multiple-statement tolerance. Refuses OFX 2.x XML (slice 4c). Registered in
CreateDefault for Format=Ofx.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Endpoint XML-refusal catch + OFX E2E

**Files:**
- Modify: `…/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs` (add `NotSupportedException`→422 to the import handler)
- Create (test): `…/Accounting101.Banking.Reconciliation.Tests/OfxImportE2eTests.cs`

**Interfaces:**
- Consumes: the now-registered OFX importer (Task 2); the existing import endpoint + `ImportPreviewResponse`/`StatementPreview` + `RecordBankStatementRequest` (Slice 4a / Slice 1).

- [ ] **Step 1: Add the `NotSupportedException`→422 catch**

In `ReconciliationEndpoints.cs`, in the `ImportStatement` handler's `try { … importer.Import(...) … }` block, add a catch for `NotSupportedException` next to the existing `ArgumentException` catch (so the OFX XML-refusal relays as 422, not a 500):
```csharp
        catch (NotSupportedException ex) // e.g. OFX 2.x XML, not yet supported
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
```
(Place it immediately after the existing `catch (ArgumentException ex) { … 422 … }`. Do not change anything else in the handler.)

- [ ] **Step 2: Build the host**

Run: `dotnet build Accounting101.Host/Accounting101.Host.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 3: Write the OFX E2E**

`OfxImportE2eTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Accounting101.Banking.Reconciliation;
using Accounting101.Banking.Reconciliation.Api;

namespace Accounting101.Banking.Reconciliation.Tests;

/// <summary>End-to-end OFX 1.x import: upload a synthetic OFX/QFX file, get a parse-to-preview with the
/// LEDGERBAL closing balance + account hint, then submit the previewed lines (opening computed so it foots)
/// to the existing statement endpoint. Plus: an OFX 2.x XML upload is refused (422).</summary>
public sealed class OfxImportE2eTests(ReconciliationHostFixture fixture) : IClassFixture<ReconciliationHostFixture>
{
    // Synthetic OFX 1.x SGML (no personal data): 2 txns, LEDGERBAL 900, account 1234567890.
    private const string Ofx1x =
        "OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\nENCODING:USASCII\n" +
        "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD" +
        "<BANKACCTFROM><BANKID>121000248<ACCTID>1234567890<ACCTTYPE>CHECKING</BANKACCTFROM>" +
        "<BANKTRANLIST><DTSTART>20260601<DTEND>20260630" +
        "<STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260628<TRNAMT>1200.00<FITID>A1<NAME>PAYROLL</STMTTRN>" +
        "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260627<TRNAMT>-300.00<FITID>A2<NAME>CHECK 1021</STMTTRN>" +
        "</BANKTRANLIST><LEDGERBAL><BALAMT>900.00<DTASOF>20260630</LEDGERBAL>" +
        "</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

    private const string Ofx2xXml =
        "<?xml version=\"1.0\"?><?OFX OFXHEADER=\"200\" VERSION=\"203\" SECURITY=\"NONE\"?>" +
        "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

    private static MultipartFormDataContent Multipart(string ofx, string filename)
    {
        MultipartFormDataContent content = [];
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(ofx));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(fileContent, "file", filename);
        content.Add(new StringContent("ofx"), "format");
        return content;
    }

    [Fact]
    public async Task Imports_an_ofx_1x_file_to_a_preview_then_submits_a_footing_statement()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();

        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import", Multipart(Ofx1x, "Checking.qfx"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        ImportPreviewResponse preview = (await resp.Content.ReadFromJsonAsync<ImportPreviewResponse>())!;

        Assert.Single(preview.Statements);
        StatementPreview s = preview.Statements[0];
        Assert.Equal(2, s.Lines.Count);
        Assert.Equal(900.00m, s.DetectedClosingBalance);
        Assert.Equal("1234567890", s.AccountHint);
        Assert.Empty(preview.Warnings);

        // Submit: opening = closing − Σ lines, so it foots (Σ = 1200 − 300 = 900 → opening 0, closing 900).
        decimal sum = s.Lines.Sum(l => l.Amount);
        decimal closing = s.DetectedClosingBalance!.Value;
        HttpResponseMessage create = await clerk.PostAsJsonAsync($"/clients/{clientId}/bank-statements",
            new RecordBankStatementRequest(fixture.CashAccountId, s.StatementDate ?? new DateOnly(2026, 6, 30),
                closing - sum, closing, s.Lines));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task An_ofx_2x_xml_upload_is_refused_422()
    {
        (Guid clientId, _, HttpClient clerk, _) = await fixture.SeedSodClientAsync();
        HttpResponseMessage resp = await clerk.PostAsync(
            $"/clients/{clientId}/bank-statements/import", Multipart(Ofx2xXml, "Checking-v2.ofx"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
```

- [ ] **Step 4: Run the focused project + Interchange**

Run: `dotnet test Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/Accounting101.Banking.Reconciliation.Tests.csproj --nologo`
Expected: all PASS (33 from 4a + 2 new = 35).
Run: `dotnet test Accounting101.Interchange.Tests/Accounting101.Interchange.Tests.csproj --nologo`
Expected: 24/24.

- [ ] **Step 5: Build the whole solution — confirm no regressions**

Run: `dotnet build Accounting101.slnx --nologo`
Expected: Build succeeded (only pre-existing NU19xx transitive warnings).

- [ ] **Step 6: Commit**

```bash
git add Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/ReconciliationEndpoints.cs \
        Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Tests/OfxImportE2eTests.cs
git commit -m "$(cat <<'EOF'
feat(reconciliation): OFX import E2E + XML-refusal relay

Maps the OFX importer's NotSupportedException to 422 (OFX 2.x XML refused).
E2E uploads a synthetic OFX 1.x file, asserts the preview's LEDGERBAL closing
balance + account hint + lines, then round-trips into a footing statement;
plus an OFX 2.x upload returns 422.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**
- Tolerant SGML scan (leaf/blocks/date/amount/header-detect) → Task 1 (`OfxScanner`). ✓
- `OfxStatementImporter` (per-STMTRS, LEDGERBAL-not-AVAILBAL, warn-and-skip, multiple statements, comma-decimal, header-optional, FITID/NAME+MEMO) + register → Task 2. ✓
- OFX 2.x XML refused → Task 2 (`NotSupportedException`) + Task 3 (endpoint 422). ✓
- Graceful empty/error result (non-zero CODE / no STMTRS → warning) → Task 2. ✓
- Endpoint unchanged except the XML-refusal catch; OFX needs no mapping → Task 3. ✓
- E2E (upload → preview → round-trip; XML → 422) → Task 3. ✓
- Interchange stays zero-dependency (OfxScanner/Importer use only BCL) → Tasks 1-2. ✓

**2. Placeholder scan:** No TBD/TODO; full code for every file; commands explicit. The synthetic OFX samples are complete literal strings.

**3. Type consistency:** `OfxScanner.Leaf/Blocks/TryParseOfxDate/TryParseOfxAmount/StripHeaderAndDetectDialect` defined in Task 1, consumed unchanged in Task 2. `OfxStatementImporter : IImporter<ImportedStatement>` produces `ImportedStatement(Lines, null opening, closing, asOf, acctId)` / `ImportedLine(date, amount, description, fitid)` — matching the Slice-4a DTOs. `InterchangeRegistry.CreateDefault()` gains one `Register<ImportedStatement>(new OfxStatementImporter())`. The endpoint catch uses the existing `ImportStatement` shape; the E2E uses `ImportPreviewResponse`/`StatementPreview`/`RecordBankStatementRequest` (Slice 4a/1) and the `ReconciliationHostFixture` (`SeedSodClientAsync`, `CashAccountId`). ✓
