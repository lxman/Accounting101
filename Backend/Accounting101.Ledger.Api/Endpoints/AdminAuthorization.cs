using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// The gate for per-client administrative endpoints: a deployment admin (trusted <c>admin=true</c>
/// token claim) may always act; otherwise the acting user must be a member of the client holding the
/// required <c>admin.*</c> capability. Control-plane provisioning (create/list clients) has no
/// per-client context and stays deployment-admin-only via the endpoint policy.
/// </summary>
internal static class AdminAuthorization
{
    public static async Task<bool> MayAsync(
        ClaimsPrincipal user, Guid clientId, string capability,
        IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (user.HasClaim("admin", "true"))
            return true;
        Actor actor = actorFactory.Create(user);
        Membership? m = await control.GetMembershipAsync(actor.UserId, clientId, ct);
        return m is not null && m.Capabilities.Contains(capability);
    }
}
