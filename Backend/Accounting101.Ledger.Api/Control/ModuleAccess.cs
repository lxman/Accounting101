using Accounting101.Ledger.Api.Auth;

namespace Accounting101.Ledger.Api.Control;

/// <summary>Whether a module operation reads or mutates data — selects the .read vs .write capability.</summary>
public enum ModuleAccessLevel
{
    Read,
    Write,
}

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
    NotEntitled,
    NotMember,
    MissingCapability,
}

/// <summary>
/// Dual authorization for a module's data access: the module must be registered + enabled and own
/// the target namespace, AND the acting user must be a member of the client AND (for mapped subledger
/// modules) hold the per-module capability for the requested access level. The module half is the
/// new namespace wall; the user half reuses the existing membership check plus the capability gate.
/// The decision is expressed purely against the <see cref="ModuleIdentity"/> value — independent of
/// how that identity was established — so an out-of-process transport reuses this verbatim.
/// </summary>
public sealed class ModuleAccess(ControlStore control)
{
    public async Task<ModuleAccessDecision> AuthorizeAsync(
        ModuleIdentity caller,
        string targetNamespace,
        Guid userId,
        Guid clientId,
        ModuleAccessLevel level = ModuleAccessLevel.Write,
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

        // Entitlement (default-closed): the module must be in the client's EnabledModules. An unknown client
        // (not in this firm's control DB) is entitled to nothing — this is also the cross-firm isolation floor.
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is null || !client.EnabledModules.Contains(caller.Key))
            return ModuleAccessDecision.NotEntitled;

        Membership? membership = await control.GetMembershipAsync(userId, clientId, cancellationToken);
        if (membership is null)
            return ModuleAccessDecision.NotMember;

        // A mapped subledger module requires the acting user to hold its .read/.write capability. An
        // unmapped module key (no subledger area) falls back to membership-only, its historical behavior.
        string? required = Capabilities.CapabilityForModule(caller.Key, level);
        if (required is not null && !membership.Capabilities.Contains(required))
            return ModuleAccessDecision.MissingCapability;

        return ModuleAccessDecision.Allowed;
    }
}
