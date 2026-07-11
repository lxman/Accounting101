# OFX 2.x XML Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Import OFX 2.x XML bank statements into the same `ImportedStatement` shape as OFX 1.x, by sharing one statement-assembly routine across both dialects behind an `IOfxNode` navigator abstraction.

**Architecture:** Backend-only, contained to `Accounting101.Interchange`. A new `IOfxNode { Leaf, Blocks }` with a `SgmlOfxNode` (wraps the existing `OfxScanner`) and an `XmlOfxNode` (wraps `XElement`, LocalName + case-insensitive). The importer's inline STMTRS/STMTTRN loop is extracted to one `AssembleStatements(IOfxNode, warnings)` and driven by the navigator chosen from the already-detected `isXml` flag.

**Tech Stack:** C# .NET (`System.Xml.Linq`), xUnit.

## Global Constraints

- OFX 2.x uses the **identical element names** as 1.x (`STMTRS`, `STMTTRN`, `DTPOSTED`, `TRNAMT`, `FITID`, `NAME`, `MEMO`, `LEDGERBAL`, `BALAMT`, `DTASOF`, `ACCTID`). Value formats (dates `YYYYMMDD…`, signed decimal amounts) are identical too — reuse `OfxScanner.TryParseOfxDate`/`TryParseOfxAmount` and the importer's `Describe`/`Clean` unchanged.
- **The existing 1.x tests (`OfxScannerTests`, `OfxStatementImporterTests`, `InterchangeRegistryTests`) must stay green** — they are the proof the refactor preserved 1.x behaviour. Do not modify existing test assertions.
- Tolerance: malformed XML → a warning result, never a throw; bad `DTPOSTED`/`TRNAMT` → warn-and-skip; 0 `STMTRS` → check `CODE`, warn.
- `XmlOfxNode` matches on `Name.LocalName`, case-insensitively (tolerant of namespaced/mixed-case exports; OFX 2.x is spec'd unqualified-uppercase, mirroring the SGML `OrdinalIgnoreCase`).
- Reader only — no OFX export. No change to CSV, the reconciliation domain/endpoint, or the UI. Build/test with `.slnx` (repo uses `Accounting101.slnx`).

---

### Task 1: `IOfxNode` + `SgmlOfxNode` + `XmlOfxNode`

**Files:**
- Create: `Accounting101.Interchange/OfxNode.cs`
- Test: `Accounting101.Interchange.Tests/OfxNodeTests.cs`

**Interfaces:**
- Consumes: `OfxScanner.Leaf`/`Blocks` (existing).
- Produces: `IOfxNode { string? Leaf(string); IReadOnlyList<IOfxNode> Blocks(string); }`, `SgmlOfxNode(string scope)`, `XmlOfxNode(XElement element)` — Task 2 consumes these.

- [ ] **Step 1: Write the failing navigator tests.**

Create `Accounting101.Interchange.Tests/OfxNodeTests.cs`:

```csharp
using System.Xml.Linq;

namespace Accounting101.Interchange.Tests;

public sealed class OfxNodeTests
{
    // ── SgmlOfxNode delegates to OfxScanner ──────────────────────────────────
    [Fact]
    public void Sgml_leaf_reads_first_unclosed_tag_value()
    {
        var node = new SgmlOfxNode("<A><ACCTID>123<X>y</A>");
        Assert.Equal("123", node.Leaf("ACCTID"));
        Assert.Null(node.Leaf("NOPE"));
    }

    [Fact]
    public void Sgml_blocks_returns_each_aggregate_as_a_node()
    {
        var node = new SgmlOfxNode("<L><STMTTRN><FITID>A</STMTTRN><STMTTRN><FITID>B</STMTTRN></L>");
        IReadOnlyList<IOfxNode> txns = node.Blocks("STMTTRN");
        Assert.Equal(2, txns.Count);
        Assert.Equal("A", txns[0].Leaf("FITID"));
        Assert.Equal("B", txns[1].Leaf("FITID"));
    }

    // ── XmlOfxNode over XElement ─────────────────────────────────────────────
    private static XmlOfxNode Xml(string xml) => new(XDocument.Parse(xml).Root!);

    [Fact]
    public void Xml_leaf_reads_first_descendant_trimmed()
    {
        XmlOfxNode node = Xml("<STMTRS><BANKACCTFROM><ACCTID>  123  </ACCTID></BANKACCTFROM></STMTRS>");
        Assert.Equal("123", node.Leaf("ACCTID"));   // any-depth descendant, trimmed
        Assert.Null(node.Leaf("NOPE"));
    }

    [Fact]
    public void Xml_blocks_are_scoped_to_the_node()
    {
        XmlOfxNode stmt = Xml("<STMTRS><BANKTRANLIST>" +
            "<STMTTRN><FITID>A</FITID></STMTTRN><STMTTRN><FITID>B</FITID></STMTTRN></BANKTRANLIST>" +
            "<LEDGERBAL><BALAMT>900.00</BALAMT></LEDGERBAL></STMTRS>");
        IReadOnlyList<IOfxNode> txns = stmt.Blocks("STMTTRN");
        Assert.Equal(2, txns.Count);
        Assert.Equal("A", txns[0].Leaf("FITID"));
        IReadOnlyList<IOfxNode> ledger = stmt.Blocks("LEDGERBAL");
        Assert.Single(ledger);
        Assert.Equal("900.00", ledger[0].Leaf("BALAMT"));   // scoped to the LEDGERBAL node
    }

    [Fact]
    public void Xml_matches_localname_case_insensitively_and_ignores_namespace()
    {
        XmlOfxNode node = Xml("<OFX xmlns='http://ofx.example'><stmtrs><AcctId>Z9</AcctId></stmtrs></OFX>");
        Assert.Single(node.Blocks("STMTRS"));                       // case-insensitive + namespaced
        Assert.Equal("Z9", node.Blocks("STMTRS")[0].Leaf("ACCTID"));
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test Accounting101.Interchange.Tests --filter OfxNodeTests`
Expected: FAIL — `IOfxNode`/`SgmlOfxNode`/`XmlOfxNode` do not exist.

- [ ] **Step 3: Implement the navigators.**

Create `Accounting101.Interchange/OfxNode.cs`:

```csharp
using System.Xml.Linq;

namespace Accounting101.Interchange;

/// <summary>A read-only navigator over one OFX dialect: pull a leaf value by tag, or the child aggregates
/// by name. Two implementations (<see cref="SgmlOfxNode"/> tolerant 1.x scan, <see cref="XmlOfxNode"/> 2.x
/// XML) let one assembly routine drive both dialects.</summary>
public interface IOfxNode
{
    /// <summary>The first descendant leaf with this tag (trimmed), or null.</summary>
    string? Leaf(string tag);

    /// <summary>The descendant aggregates with this name, each as its own node.</summary>
    IReadOnlyList<IOfxNode> Blocks(string aggregate);
}

/// <summary>1.x SGML navigator — delegates to the tolerant <see cref="OfxScanner"/> over a scope string.</summary>
public sealed class SgmlOfxNode(string scope) : IOfxNode
{
    public string? Leaf(string tag) => OfxScanner.Leaf(scope, tag);

    public IReadOnlyList<IOfxNode> Blocks(string aggregate) =>
        OfxScanner.Blocks(scope, aggregate).Select(s => (IOfxNode)new SgmlOfxNode(s)).ToList();
}

/// <summary>2.x XML navigator over an <see cref="XElement"/>. Matches on local name, case-insensitively, so a
/// namespaced or mixed-case export still reads (OFX 2.x is spec'd unqualified-uppercase).</summary>
public sealed class XmlOfxNode(XElement element) : IOfxNode
{
    public string? Leaf(string tag) =>
        element.Descendants().FirstOrDefault(e => NameMatches(e, tag))?.Value.Trim();

    public IReadOnlyList<IOfxNode> Blocks(string aggregate) =>
        element.Descendants().Where(e => NameMatches(e, aggregate)).Select(e => (IOfxNode)new XmlOfxNode(e)).ToList();

    private static bool NameMatches(XElement e, string tag) =>
        string.Equals(e.Name.LocalName, tag, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Run to verify it passes.**

Run: `dotnet test Accounting101.Interchange.Tests --filter OfxNodeTests`
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add Accounting101.Interchange/OfxNode.cs Accounting101.Interchange.Tests/OfxNodeTests.cs
git commit -m "feat(interchange): IOfxNode navigator with SGML + XML implementations"
```

---

### Task 2: Refactor the importer to `AssembleStatements(IOfxNode, …)` (SGML through the abstraction)

**Files:**
- Modify: `Accounting101.Interchange/OfxStatementImporter.cs`

**Interfaces:**
- Consumes: `SgmlOfxNode` (Task 1); `IOfxNode`.
- Produces: `AssembleStatements(IOfxNode root, List<string> warnings)` — Task 3 feeds it an `XmlOfxNode`.

**Context:** This task changes NO behaviour — it routes the existing 1.x path through `SgmlOfxNode`/`AssembleStatements`. The 1.x `NotSupportedException` for XML stays until Task 3. The existing 1.x tests are the regression guard: they must stay green untouched.

- [ ] **Step 1: Extract `AssembleStatements` and route the SGML path through it.**

In `OfxStatementImporter.cs`, replace the body of `Import` from `string body = OfxScanner.StripHeaderAndDetectDialect(...)` through the end of the method, AND add the new private method. The `Import` method becomes:

```csharp
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

        List<string> warnings = [];
        return AssembleStatements(new SgmlOfxNode(body), warnings);
    }

    private static ImportResult<ImportedStatement> AssembleStatements(IOfxNode root, List<string> warnings)
    {
        List<ImportedStatement> statements = [];

        IReadOnlyList<IOfxNode> stmtBlocks = root.Blocks("STMTRS");
        if (stmtBlocks.Count == 0)
        {
            string? code = root.Leaf("CODE");
            warnings.Add(code is not null && code != "0"
                ? $"OFX response carried status code {code}; no bank statement (<STMTRS>) found."
                : "No bank statement (<STMTRS>) found in the OFX file.");
            return new ImportResult<ImportedStatement>([], warnings);
        }

        for (int si = 0; si < stmtBlocks.Count; si++)
        {
            IOfxNode stmt = stmtBlocks[si];
            string? acctId = Clean(stmt.Leaf("ACCTID"));

            decimal? closing = null;
            DateOnly? asOf = null;
            IReadOnlyList<IOfxNode> ledger = stmt.Blocks("LEDGERBAL");
            if (ledger.Count > 0)
            {
                if (OfxScanner.TryParseOfxAmount(ledger[0].Leaf("BALAMT"), out decimal bal)) closing = bal;
                if (OfxScanner.TryParseOfxDate(ledger[0].Leaf("DTASOF"), out DateOnly d)) asOf = d;
            }

            List<ImportedLine> lines = [];
            IReadOnlyList<IOfxNode> txns = stmt.Blocks("STMTTRN");
            for (int ti = 0; ti < txns.Count; ti++)
            {
                IOfxNode tx = txns[ti];
                string? fitid = Clean(tx.Leaf("FITID"));
                string id = fitid ?? $"#{ti + 1}";
                string acct = acctId ?? $"#{si + 1}";

                if (!OfxScanner.TryParseOfxDate(tx.Leaf("DTPOSTED"), out DateOnly date))
                {
                    warnings.Add($"Statement {acct}, transaction {id}: missing or unparseable DTPOSTED — skipped.");
                    continue;
                }
                if (!OfxScanner.TryParseOfxAmount(tx.Leaf("TRNAMT"), out decimal amount))
                {
                    warnings.Add($"Statement {acct}, transaction {id}: missing or unparseable TRNAMT — skipped.");
                    continue;
                }
                string description = Describe(tx.Leaf("NAME"), tx.Leaf("MEMO"));
                lines.Add(new ImportedLine(date, amount, description, fitid));
            }

            statements.Add(new ImportedStatement(lines, null, closing, asOf, acctId));
        }

        return new ImportResult<ImportedStatement>(statements, warnings);
    }
```

Leave `Clean` and `Describe` (the two private helpers below) unchanged.

- [ ] **Step 2: Build.**

Run: `dotnet build Accounting101.slnx`
Expected: succeeds.

- [ ] **Step 3: Run the existing OFX tests — the regression guard.**

Run: `dotnet test Accounting101.Interchange.Tests`
Expected: ALL pass, including every existing `OfxStatementImporterTests` and `OfxScannerTests` case — proving the SGML path is behaviour-identical through the abstraction. If any 1.x test fails, the extraction diverged — fix it, do not touch the test.

- [ ] **Step 4: Commit.**

```bash
git add Accounting101.Interchange/OfxStatementImporter.cs
git commit -m "refactor(interchange): assemble OFX statements over IOfxNode (1.x unchanged)"
```

---

### Task 3: Wire the OFX 2.x XML path + tests + doc

**Files:**
- Modify: `Accounting101.Interchange/OfxStatementImporter.cs`
- Test: `Accounting101.Interchange.Tests/OfxStatementImporterTests.cs`

**Interfaces:**
- Consumes: `XmlOfxNode` (Task 1), `AssembleStatements` (Task 2).
- Produces: OFX 2.x XML import.

- [ ] **Step 1: Write the failing OFX 2.x tests.**

Append to `OfxStatementImporterTests.cs` (inside the class). The `WfXml` constant is the XML twin of the existing `WfStyle` 1.x fixture — same data, so it must yield the same `ImportedStatement`:

```csharp
    // XML twin of WfStyle (OFX 2.x): same data, well-formed XML with an <?xml?>/<?OFX?> preamble.
    private const string WfXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<?OFX OFXHEADER=\"200\" VERSION=\"211\" SECURITY=\"NONE\" OLDFILEUID=\"NONE\" NEWFILEUID=\"NONE\"?>\n" +
        "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD</CURDEF>" +
        "<BANKACCTFROM><BANKID>121000248</BANKID><ACCTID>1234567890</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>" +
        "<BANKTRANLIST><DTSTART>20260601</DTSTART><DTEND>20260630</DTEND>" +
        "<STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20260628120000.000[-5:EST]</DTPOSTED><TRNAMT>1200.00</TRNAMT><FITID>A1</FITID><NAME>PAYROLL</NAME></STMTTRN>" +
        "<STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20260627</DTPOSTED><TRNAMT>-300.00</TRNAMT><FITID>A2</FITID><NAME>CHECK 1021</NAME><MEMO>RENT</MEMO></STMTTRN>" +
        "</BANKTRANLIST>" +
        "<LEDGERBAL><BALAMT>900.00</BALAMT><DTASOF>20260630</DTASOF></LEDGERBAL>" +
        "<AVAILBAL><BALAMT>850.00</BALAMT><DTASOF>20260630</DTASOF></AVAILBAL>" +
        "</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

    [Fact]
    public void Parses_an_ofx_2x_xml_statement_identically_to_its_1x_twin()
    {
        ImportResult<ImportedStatement> result = Import(WfXml);
        Assert.Empty(result.Warnings);
        ImportedStatement s = Assert.Single(result.Records);
        Assert.Equal("1234567890", s.AccountHint);
        Assert.Equal(900.00m, s.ClosingBalance);                       // LEDGERBAL, not AVAILBAL
        Assert.Null(s.OpeningBalance);
        Assert.Equal(new DateOnly(2026, 6, 30), s.StatementDate);
        Assert.Equal(2, s.Lines.Count);
        Assert.Equal(1200.00m, s.Lines[0].Amount);
        Assert.Equal("PAYROLL", s.Lines[0].Description);
        Assert.Equal("A1", s.Lines[0].Reference);
        Assert.Equal(new DateOnly(2026, 6, 28), s.Lines[0].Date);      // time/offset stripped
        Assert.Equal(-300.00m, s.Lines[1].Amount);
        Assert.Equal("CHECK 1021 — RENT", s.Lines[1].Description);     // NAME + MEMO
        Assert.Equal("A2", s.Lines[1].Reference);
    }

    [Fact]
    public void Ofx_2x_bad_transaction_is_warned_and_skipped()
    {
        string xml =
            "<?xml version=\"1.0\"?><OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS>" +
            "<BANKACCTFROM><ACCTID>9</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>" +
            "<BANKTRANLIST>" +
            "<STMTTRN><DTPOSTED>NOPE</DTPOSTED><TRNAMT>5.00</TRNAMT><FITID>B1</FITID><NAME>BAD DATE</NAME></STMTTRN>" +
            "<STMTTRN><DTPOSTED>20260601</DTPOSTED><TRNAMT>5.00</TRNAMT><FITID>B2</FITID><NAME>GOOD</NAME></STMTTRN>" +
            "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(xml);
        ImportedStatement s = Assert.Single(result.Records);
        Assert.Single(s.Lines);                                        // only the good one
        Assert.Equal("B2", s.Lines[0].Reference);
        Assert.Contains(result.Warnings, w => w.Contains("B1") && w.Contains("DTPOSTED"));
    }

    [Fact]
    public void Malformed_ofx_2x_xml_degrades_to_a_warning_not_a_throw()
    {
        string broken = "<?xml version=\"1.0\"?><OFX><STMTRS><ACCTID>1</ACCTID>"; // unclosed root — invalid XML
        ImportResult<ImportedStatement> result = Import(broken);
        Assert.Empty(result.Records);
        Assert.Single(result.Warnings);
        Assert.Contains("could not be parsed", result.Warnings[0]);
    }

    [Fact]
    public void Ofx_2x_status_error_with_no_statement_warns()
    {
        string xml =
            "<?xml version=\"1.0\"?><OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>2000</CODE><SEVERITY>ERROR</SEVERITY></STATUS></SONRS></SIGNONMSGSRSV1></OFX>";
        ImportResult<ImportedStatement> result = Import(xml);
        Assert.Empty(result.Records);
        Assert.Contains(result.Warnings, w => w.Contains("2000"));
    }
```

- [ ] **Step 2: Run to verify the 2.x tests fail.**

Run: `dotnet test Accounting101.Interchange.Tests --filter OfxStatementImporterTests`
Expected: the new 2.x tests FAIL — `Import` still throws `NotSupportedException` for `isXml`.

- [ ] **Step 3: Wire the XML path.**

In `OfxStatementImporter.cs`, add `using System.Xml.Linq;` and `using System.Xml;` at the top (alongside `using System.Text;`). Replace the `if (isXml) throw …;` block in `Import` with:

```csharp
        List<string> warnings = [];
        IOfxNode root;
        if (isXml)
        {
            try { root = new XmlOfxNode(XDocument.Parse(body).Root!); }
            catch (XmlException ex)
            {
                return new ImportResult<ImportedStatement>([], [$"OFX 2.x XML could not be parsed: {ex.Message}"]);
            }
        }
        else root = new SgmlOfxNode(body);

        return AssembleStatements(root, warnings);
```

(Delete the now-unused `NotSupportedException` throw and the separate `List<string> warnings = [];`/`return AssembleStatements(new SgmlOfxNode(body), warnings);` lines from Task 2 — the block above replaces them.)

- [ ] **Step 4: Update the class doc comment.**

Change the `<summary>` on `OfxStatementImporter` (top of the file): remove "OFX 2.x XML is refused (slice 4c)" and state that both 1.x SGML and 2.x XML are supported. For example, replace "OFX 2.x XML is refused (slice 4c)." with "OFX 2.x XML is parsed via the same routine over an XML navigator."

- [ ] **Step 5: Run the 2.x tests + the full Interchange suite (1.x regression).**

Run: `dotnet test Accounting101.Interchange.Tests`
Expected: ALL pass — the 4 new 2.x tests AND every existing 1.x test.

- [ ] **Step 6: Commit.**

```bash
git add Accounting101.Interchange/OfxStatementImporter.cs Accounting101.Interchange.Tests/OfxStatementImporterTests.cs
git commit -m "feat(interchange): parse OFX 2.x XML statements"
```

---

## Notes for the whole-branch review

- Confirm the 1.x path is behaviour-identical (existing 1.x tests unmodified and green) — the refactor's only job.
- Confirm the OFX 2.x `WfXml` parity test yields the SAME `ImportedStatement` as the 1.x `WfStyle` test.
- Confirm malformed XML degrades to a warning (no `XmlException` escapes `Import`).
- No change outside `Accounting101.Interchange` (+ its tests). No engine/module/UI change. `environment.ts` untouched.
- Optional dev-stack smoke (upload a real 2.x `.ofx` via the Bank Rec import screen) — decide at merge; unit tests carry the coverage.
