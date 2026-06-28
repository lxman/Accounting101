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
