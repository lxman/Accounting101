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
