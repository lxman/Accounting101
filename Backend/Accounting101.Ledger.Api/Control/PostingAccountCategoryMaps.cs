namespace Accounting101.Ledger.Api.Control;

/// <summary>Which modules support a per-client revenue-category map, and where the deployment-default
/// map lives in process config. Only receivables today; fan out by adding a row.</summary>
public static class PostingAccountCategoryMaps
{
    private static readonly IReadOnlyDictionary<string, string> ConfigSections = new Dictionary<string, string>
    {
        ["receivables"] = "Receivables:Accounts:RevenueByCategory",
    };

    /// <summary>The module's config-fallback section, or null when the module has no category map.</summary>
    public static string? ConfigSectionFor(string moduleKey) =>
        ConfigSections.TryGetValue(moduleKey, out string? section) ? section : null;
}
