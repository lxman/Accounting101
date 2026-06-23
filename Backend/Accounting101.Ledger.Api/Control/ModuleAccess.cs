using Accounting101.Ledger.Api.Auth;

namespace Accounting101.Ledger.Api.Control;

/// <summary>
/// The reason a module call was allowed or refused. Every refusal maps to HTTP 403 at the boundary;
/// the distinct reasons exist for logging and tests.
/// </summary>
public enum ModuleAccessDecision
{
    Allowed,
    Unregistered,
    Disabled,
    NotOwner,
    NotMember,
}

/// <summary>
/// Dual authorization for a module's data access: the module must be registered + enabled and own
/// the target namespace, AND the acting user must be a member of the client. The module half is the
/// new namespace wall; the user half reuses the existing membership check. The decision is expressed
/// purely against the <see cref="ModuleIdentity"/> value — independent of how that identity was
/// established — so an out-of-process transport reuses this verbatim.
/// </summary>
public sealed class ModuleAccess(ControlStore control)
{
    public async Task<ModuleAccessDecision> AuthorizeAsync(
        ModuleIdentity caller,
        string targetNamespace,
        Guid userId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        ModuleRegistration? module = await control.GetModuleAsync(caller.Key, cancellationToken);
        if (module is null)
            return ModuleAccessDecision.Unregistered;
        if (!module.Enabled)
            return ModuleAccessDecision.Disabled;

        // Ownership is derived from identity (the store imposes target = caller.Key); the explicit
        // check makes any future named-target path safe too.
        if (caller.Key != targetNamespace)
            return ModuleAccessDecision.NotOwner;

        if (!await control.IsMemberAsync(userId, clientId, cancellationToken))
            return ModuleAccessDecision.NotMember;

        return ModuleAccessDecision.Allowed;
    }
}
