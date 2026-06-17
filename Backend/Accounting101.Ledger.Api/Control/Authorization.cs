namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// A user's role on a client's books (assigned per client in the control DB). Roles separate
/// <em>capabilities</em>; segregation of duties separately separates <em>individuals</em>.
/// </summary>
public enum LedgerRole
{
    /// <summary>Read-only — sees everything, changes nothing.</summary>
    Auditor,

    /// <summary>Enters and revises entries (maker), but cannot approve them.</summary>
    Clerk,

    /// <summary>Approves, voids, and reverses (checker), but does not enter.</summary>
    Approver,

    /// <summary>Full journal authority: enter, approve, reverse, and close periods.</summary>
    Controller,

    /// <summary>Everything a controller can do, plus (future) reopen, chart-of-accounts, and user admin.</summary>
    Admin,
}

/// <summary>A capability the host gates an endpoint on. Maps to roles via <see cref="RolePermissions"/>.</summary>
public enum Permission
{
    Read,
    Post,
    Revise,
    Approve,
    Void,
    Reverse,
    Close,
}

/// <summary>The role → permission matrix. The single source of truth for "what can this role do".</summary>
public static class RolePermissions
{
    private static readonly Dictionary<LedgerRole, HashSet<Permission>> Map = new()
    {
        [LedgerRole.Auditor] = [Permission.Read],
        [LedgerRole.Clerk] = [Permission.Read, Permission.Post, Permission.Revise],
        [LedgerRole.Approver] = [Permission.Read, Permission.Approve, Permission.Void, Permission.Reverse],
        [LedgerRole.Controller] =
            [Permission.Read, Permission.Post, Permission.Revise, Permission.Approve, Permission.Void, Permission.Reverse, Permission.Close],
        // Admin mirrors Controller across the journal surface today; it also owns the (future) reopen,
        // chart-of-accounts, and user-administration endpoints once those exist.
        [LedgerRole.Admin] =
            [Permission.Read, Permission.Post, Permission.Revise, Permission.Approve, Permission.Void, Permission.Reverse, Permission.Close],
    };

    public static bool Allows(LedgerRole role, Permission permission) =>
        Map.TryGetValue(role, out HashSet<Permission>? permissions) && permissions.Contains(permission);
}
