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
