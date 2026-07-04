namespace Accounting101.FixedAssets;

/// <summary>Resolves the depreciation strategy for an asset's stored method. Built once from the
/// registered strategies (DI passes all IDepreciationMethod implementations).</summary>
public sealed class DepreciationMethodSelector
{
    private readonly Dictionary<DepreciationMethod, IDepreciationMethod> _byMethod;

    public DepreciationMethodSelector(IEnumerable<IDepreciationMethod> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        _byMethod = methods.ToDictionary(m => m.Method);
    }

    public IDepreciationMethod For(DepreciationMethod method) =>
        _byMethod.TryGetValue(method, out IDepreciationMethod? m)
            ? m
            : throw new InvalidOperationException($"No depreciation strategy registered for {method}.");
}
