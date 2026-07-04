using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// No-self-escalation: a non-deployment-admin caller may only grant capabilities they themselves hold
/// on the client. Prevents a per-client <c>admin.users</c> holder from granting (or self-granting)
/// authority beyond their own — the difference between "we have RBAC" and RBAC that resists sprawl.
/// </summary>
internal static class GrantScope
{
    /// <summary>The first granted capability the caller does not hold, or null if the caller is a
    /// deployment admin (exempt) or holds every granted capability.</summary>
    public static async Task<string?> FirstNotHeldByCallerAsync(
        ClaimsPrincipal user, Guid clientId, IEnumerable<string> grantedCapabilities,
        IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (user.HasClaim("admin", "true")) return null;

        Actor actor = actorFactory.Create(user);
        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, ct);
        HashSet<string> own = membership is null ? [] : [.. membership.Capabilities];

        foreach (string capability in grantedCapabilities)
            if (!own.Contains(capability)) return capability;
        return null;
    }
}
