using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Per-client approval-mode policy: read and change a client's <see cref="ApprovalMode"/>. Gated by
/// <c>admin.approvalPolicy</c> (a deployment admin overrides). Weakening segregation of duties is a
/// sensitive single lever, so it rides its own capability rather than general client admin.
/// </summary>
public static class ApprovalPolicyEndpoints
{
    public static void MapApprovalPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/clients/{clientId:guid}/approval-policy").RequireAuthorization();
        g.MapGet("", GetApprovalPolicy);
        g.MapPut("", SetApprovalPolicy);
    }

    private static async Task<IResult> GetApprovalPolicy(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminApprovalPolicy, actorFactory, control, ct))
            return Results.Forbid();

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();
        return Results.Ok(new ApprovalPolicyResponse(ApprovalPolicy.ModeOf(client)));
    }

    private static async Task<IResult> SetApprovalPolicy(
        Guid clientId, SetApprovalPolicyRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminApprovalPolicy, actorFactory, control, ct))
            return Results.Forbid();

        if (request.Mode == ApprovalMode.Unspecified)
            return Results.Problem("Mode must be TwoPerson, SelfApprove, or AutoApprove.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();

        client.ApprovalMode = request.Mode;
        await control.RegisterClientAsync(client, ct);
        return Results.Ok(new ApprovalPolicyResponse(request.Mode));
    }
}
