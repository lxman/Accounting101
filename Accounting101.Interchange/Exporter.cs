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
