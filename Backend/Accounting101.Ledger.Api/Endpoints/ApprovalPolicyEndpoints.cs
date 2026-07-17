using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Core.Journal;

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

    private static async Task<long> CountPendingAsync(
        ClientLedgerFactory ledgers, Guid clientId, CancellationToken ct)
    {
        ClientLedger? ledger = await ledgers.CreateAsync(clientId, ct);
        return ledger is null ? 0 : await ledger.Journal.CountByPostingAsync(clientId, PostingState.PendingApproval, ct);
    }

    private static async Task<IResult> GetApprovalPolicy(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        ClientLedgerFactory ledgers, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminApprovalPolicy, actorFactory, control, ct))
            return Results.Forbid();

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();

        long pending = await CountPendingAsync(ledgers, clientId, ct);
        return Results.Ok(new ApprovalPolicyResponse(ApprovalPolicy.ModeOf(client), pending));
    }

    private static async Task<IResult> SetApprovalPolicy(
        Guid clientId, SetApprovalPolicyRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, ClientLedgerFactory ledgers, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminApprovalPolicy, actorFactory, control, ct))
            return Results.Forbid();

        if (request.Mode == ApprovalMode.Unspecified)
            return Results.Problem("Mode must be TwoPerson, SelfApprove, or AutoApprove.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();

        long pending = await CountPendingAsync(ledgers, clientId, ct);
        // Accepted race: an entry could post between this count and the persist below. The window is
        // tiny and benign — switching back to SelfApprove/TwoPerson is never blocked, so an admin can
        // recover (switch back, approve the straggler, switch forward). Not worth a control-store lock.
        if (request.Mode == ApprovalMode.AutoApprove && pending > 0)
            return Results.Problem(
                $"Cannot enable auto-approve while {pending} {(pending == 1 ? "entry awaits" : "entries await")} approval. Clear the approval queue first.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        client.ApprovalMode = request.Mode;
        await control.RegisterClientAsync(client, ct);
        return Results.Ok(new ApprovalPolicyResponse(request.Mode, pending));
    }
}
