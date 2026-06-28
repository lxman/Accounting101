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
