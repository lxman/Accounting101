namespace Accounting101.ModuleKit;

/// <summary>
/// The authorization decision for a module's advisory chart-readiness report: a member may read it
/// iff they are a deployment admin, a client admin, or hold that module's read capability. Deliberately
/// independent of the client's enabled-modules entitlement, so a chart can be previewed for a module
/// before it is enabled (the onboarding path). Membership is enforced upstream (the capabilities lookup
/// and the chart read both require it) and is not represented here.
/// </summary>
public static class ReadinessAccess
{
    private const string ClientAdmin = "admin.client";

    /// <summary>The "{area}.read" capability a module's readiness requires, or null for an unknown module key.</summary>
    public static string? ReadCapabilityFor(string moduleKey) => moduleKey switch
    {
        "receivables" => "ar.read",
        "payables"    => "ap.read",
        "payroll"     => "payroll.read",
        "cash"        => "cash.read",
        "fixedassets" => "fixedassets.read",
        "inventory"   => "inventory.read",
        _ => null,
    };

    /// <summary>Deployment admin OR client admin OR holds the module's read capability. Fail-closed on an unknown key.</summary>
    public static bool Allows(string moduleKey, bool deploymentAdmin, IReadOnlyCollection<string> capabilities)
    {
        if (deploymentAdmin || capabilities.Contains(ClientAdmin))
            return true;
        string? required = ReadCapabilityFor(moduleKey);
        return required is not null && capabilities.Contains(required);
    }
}
