namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// Role → default capability bundle. Roles are grant-time PRESETS: at grant time a role expands into
/// the capability set stored on the membership (the authority). The gl.* of each of the five original
/// presets mirrors <see cref="RolePermissions"/> exactly, so flipping GL enforcement to capabilities
/// is behavior-preserving (see CapabilityModelTests).
/// </summary>
public static class RolePresets
{
    private static readonly string[] Reads =
    [
        Capabilities.GlRead, Capabilities.ArRead, Capabilities.ApRead, Capabilities.PayrollRead,
        Capabilities.CashRead, Capabilities.BankRecRead, Capabilities.FixedAssetsRead,
        Capabilities.AuditRead, Capabilities.ReportsRead,
    ];

    private static readonly string[] ModuleWrites =
    [
        Capabilities.ArWrite, Capabilities.ApWrite, Capabilities.PayrollWrite,
        Capabilities.CashWrite, Capabilities.BankRecWrite, Capabilities.FixedAssetsWrite,
    ];

    private static readonly Dictionary<LedgerRole, HashSet<string>> Map = new()
    {
        [LedgerRole.Auditor] = [.. Reads],
        [LedgerRole.Clerk] = [.. Reads, .. ModuleWrites],
        [LedgerRole.Approver] = [.. Reads, Capabilities.GlApprove, Capabilities.GlVoid, Capabilities.GlReverse],
        [LedgerRole.Controller] =
        [
            .. Reads, .. ModuleWrites,
            Capabilities.GlPost, Capabilities.GlRevise, Capabilities.GlApprove, Capabilities.GlVoid,
            Capabilities.GlReverse, Capabilities.GlClose, Capabilities.GlManageAccounts,
        ],
        [LedgerRole.Admin] =
        [
            .. Reads, .. ModuleWrites,
            Capabilities.GlPost, Capabilities.GlRevise, Capabilities.GlApprove, Capabilities.GlVoid,
            Capabilities.GlReverse, Capabilities.GlClose, Capabilities.GlManageAccounts, Capabilities.GlReopen,
            Capabilities.AdminUsers, Capabilities.AdminFirm, Capabilities.AdminClient,
            Capabilities.AdminFiscal, Capabilities.AdminPostingAccounts,
        ],
        [LedgerRole.ArClerk] = [.. Reads, Capabilities.ArWrite],
        [LedgerRole.ApClerk] = [.. Reads, Capabilities.ApWrite],
        [LedgerRole.PayrollClerk] = [.. Reads, Capabilities.PayrollWrite],
        [LedgerRole.CashClerk] = [.. Reads, Capabilities.CashWrite, Capabilities.BankRecWrite],
    };

    /// <summary>The default capability set for a role.</summary>
    public static IReadOnlySet<string> For(LedgerRole role) => Map[role];

    /// <summary>Union of the presets for the given roles (the union of overlapping roles).</summary>
    public static HashSet<string> CapabilitiesFor(IEnumerable<LedgerRole> roles)
    {
        HashSet<string> union = [];
        foreach (LedgerRole role in roles) union.UnionWith(Map[role]);
        return union;
    }
}
