using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Accounting101.Interchange;

/// <summary>Parses an OFX bank-statement file — either the OFX 1.x SGML dialect (the Wells Fargo QFX dialect
/// and friends) or OFX 2.x XML — into one <see cref="ImportedStatement"/> per &lt;STMTRS&gt;. Tolerant: header
/// optional, unclosed leaves (1.x), multiple statements, LEDGERBAL (not AVAILBAL) closing balance, bad
/// transactions warned-and-skipped, malformed-but-readable files degrade to warnings. OFX 2.x XML is parsed
/// via the same routine over an XML navigator. No balances opening (OFX has none).</summary>
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
