using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Self-service capability lookup: any authenticated member may read their own resolved capabilities
/// on a client. Resolved server-side from the control DB (never the token's role claim), so it is the
/// single source of truth the frontend uses to drive role-based navigation and screen write-gating.
/// </summary>
public static class CapabilitiesEndpoints
{
    public static void MapCapabilitiesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/clients/{clientId:guid}")
           .RequireAuthorization()
           .MapGet("/me/capabilities", GetMyCapabilities);
    }

    private static async Task<IResult> GetMyCapabilities(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);
        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null)
            return Results.Forbid();

        bool deploymentAdmin = user.HasClaim("admin", "true");
        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        return Results.Ok(new CapabilitiesResponse(
            membership.Capabilities,
            membership.GrantedRoles.Select(r => r.ToString()).ToList(),
            deploymentAdmin,
            client?.EnabledModules ?? []));
    }
}
