namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// The capability vocabulary — the atomic unit of what a member may see/do, in "area.level" form.
/// The nine gl.* capabilities map 1:1 to <see cref="Permission"/> (the GL/chart authority the host
/// enforces); subledger/assurance/admin capabilities are advisory for the UI until server-side
/// enforcement lands (a later slice).
/// </summary>
public static class Capabilities
{
    // GL — 1:1 with Permission.
    public const string GlRead = "gl.read";
    public const string GlPost = "gl.post";
    public const string GlRevise = "gl.revise";
    public const string GlApprove = "gl.approve";
    public const string GlVoid = "gl.void";
    public const string GlReverse = "gl.reverse";
    public const string GlClose = "gl.close";
    public const string GlManageAccounts = "gl.manageAccounts";
    public const string GlReopen = "gl.reopen";

    // Subledgers.
    public const string ArRead = "ar.read";
    public const string ArWrite = "ar.write";
    public const string ApRead = "ap.read";
    public const string ApWrite = "ap.write";
    public const string PayrollRead = "payroll.read";
    public const string PayrollWrite = "payroll.write";
    public const string CashRead = "cash.read";
    public const string CashWrite = "cash.write";
    public const string BankRecRead = "bankrec.read";
    public const string BankRecWrite = "bankrec.write";
    public const string FixedAssetsRead = "fixedassets.read";
    public const string FixedAssetsWrite = "fixedassets.write";

    // Assurance.
    public const string AuditRead = "audit.read";
    public const string ReportsRead = "reports.read";

    // Admin.
    public const string AdminUsers = "admin.users";
    public const string AdminFirm = "admin.firm";
    public const string AdminClient = "admin.client";
    public const string AdminFiscal = "admin.fiscal";
    public const string AdminPostingAccounts = "admin.postingAccounts";

    private static readonly Dictionary<Permission, string> PermissionToCapability = new()
    {
        [Permission.Read] = GlRead,
        [Permission.Post] = GlPost,
        [Permission.Revise] = GlRevise,
        [Permission.Approve] = GlApprove,
        [Permission.Void] = GlVoid,
        [Permission.Reverse] = GlReverse,
        [Permission.Close] = GlClose,
        [Permission.ManageAccounts] = GlManageAccounts,
        [Permission.Reopen] = GlReopen,
    };

    private static readonly Dictionary<string, Permission> CapabilityToPermission =
        PermissionToCapability.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>The gl.* capability that corresponds to a GL permission.</summary>
    public static string CapabilityForPermission(Permission permission) => PermissionToCapability[permission];

    /// <summary>The GL permission a capability corresponds to, or null for non-gl.* capabilities.</summary>
    public static Permission? PermissionForCapability(string capability) =>
        CapabilityToPermission.TryGetValue(capability, out Permission p) ? p : null;

    /// <summary>
    /// The subledger capability a module requires for a given access level, or null when the module key
    /// has no subledger area (a non-subledger module — falls back to membership-only authorization).
    /// </summary>
    public static string? CapabilityForModule(string moduleKey, ModuleAccessLevel level) => moduleKey switch
    {
        "receivables"    => level == ModuleAccessLevel.Write ? ArWrite : ArRead,
        "payables"       => level == ModuleAccessLevel.Write ? ApWrite : ApRead,
        "payroll"        => level == ModuleAccessLevel.Write ? PayrollWrite : PayrollRead,
        "cash"           => level == ModuleAccessLevel.Write ? CashWrite : CashRead,
        "reconciliation" => level == ModuleAccessLevel.Write ? BankRecWrite : BankRecRead,
        "fixedassets"    => level == ModuleAccessLevel.Write ? FixedAssetsWrite : FixedAssetsRead,
        _ => null,
    };

    /// <summary>Every capability in the vocabulary.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        GlRead, GlPost, GlRevise, GlApprove, GlVoid, GlReverse, GlClose, GlManageAccounts, GlReopen,
        ArRead, ArWrite, ApRead, ApWrite, PayrollRead, PayrollWrite, CashRead, CashWrite,
        BankRecRead, BankRecWrite, FixedAssetsRead, FixedAssetsWrite,
        AuditRead, ReportsRead,
        AdminUsers, AdminFirm, AdminClient, AdminFiscal, AdminPostingAccounts,
    };
}
