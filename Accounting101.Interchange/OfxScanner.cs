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
        const NumberStyles decimalStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowTrailingSign;
        if (decimal.TryParse(raw, decimalStyles, CultureInfo.InvariantCulture, out amount)) return true;
        if (raw.Contains(',') && !raw.Contains('.'))
            return decimal.TryParse(raw.Replace(',', '.'), decimalStyles, CultureInfo.InvariantCulture, out amount);
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
