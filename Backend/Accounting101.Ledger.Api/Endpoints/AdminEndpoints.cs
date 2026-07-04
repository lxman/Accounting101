using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Deployment control-plane: provision clients and grant users roles. These operate on the control
/// database (above any single client), so they sit behind the deployment-admin policy — a trusted
/// token claim the IdP issues to firm administrators — rather than per-client membership.
/// </summary>
public static class AdminEndpoints
{
    public const string Policy = "DeploymentAdmin";

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Control-plane: deployment admin only — no per-client context exists to gate on.
        RouteGroupBuilder deployment = app.MapGroup("/admin").RequireAuthorization(Policy);
        deployment.MapPost("/clients", CreateClient);
        deployment.MapGet("/clients", ListClients);

        // Per-client admin: deployment admin OR the matching admin.* capability (checked in-handler).
        RouteGroupBuilder perClient = app.MapGroup("/admin").RequireAuthorization();
        perClient.MapPut("/clients/{clientId:guid}/fiscal-year-end", SetFiscalYearEnd);
        perClient.MapPost("/clients/{clientId:guid}/members", AddMember);
        perClient.MapGet("/clients/{clientId:guid}/members", ListMembers);
    }

    private static async Task<IResult> CreateClient(
        CreateClientRequest request, ControlStore control, CancellationToken cancellationToken)
    {
        if (request.FiscalYearEndMonth is < 1 or > 12)
            return Results.Problem("FiscalYearEndMonth must be between 1 and 12.", statusCode: StatusCodes.Status400BadRequest);

        Guid id = Guid.NewGuid();
        string database = string.IsNullOrWhiteSpace(request.DatabaseName)
            ? "client_" + id.ToString("N")
            : request.DatabaseName;

        ClientRegistration registration = new()
        {
            Id = id,
            Name = request.Name,
            DatabaseName = database,
            RequireSegregationOfDuties = request.RequireSegregationOfDuties,
            FiscalYearEndMonth = request.FiscalYearEndMonth,
        };
        await control.RegisterClientAsync(registration, cancellationToken);

        return Results.Created(
            $"/admin/clients/{id}",
            new ClientRegistrationResponse(id, registration.Name, registration.DatabaseName,
                registration.RequireSegregationOfDuties, FiscalYear.MonthOf(registration)));
    }

    private static async Task<IResult> SetFiscalYearEnd(
        Guid clientId, SetFiscalYearEndRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminFiscal, actorFactory, control, cancellationToken))
            return Results.Forbid();

        if (request.FiscalYearEndMonth is < 1 or > 12)
            return Results.Problem("FiscalYearEndMonth must be between 1 and 12.", statusCode: StatusCodes.Status400BadRequest);

        // Forward-only: already-closed years are immutable in the journal, so changing the scalar only
        // affects the validation of future closes. No effective-dated history is needed.
        ClientRegistration? registration = await control.GetClientAsync(clientId, cancellationToken);
        if (registration is null)
            return Results.NotFound();

        registration.FiscalYearEndMonth = request.FiscalYearEndMonth;
        await control.RegisterClientAsync(registration, cancellationToken);

        return Results.Ok(new ClientRegistrationResponse(registration.Id, registration.Name, registration.DatabaseName,
            registration.RequireSegregationOfDuties, FiscalYear.MonthOf(registration)));
    }

    private static async Task<IResult> ListClients(ControlStore control, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClientRegistration> clients = await control.ListClientsAsync(cancellationToken);
        return Results.Ok(clients
            .Select(c => new ClientRegistrationResponse(c.Id, c.Name, c.DatabaseName, c.RequireSegregationOfDuties, FiscalYear.MonthOf(c)))
            .ToList());
    }

    private static async Task<IResult> AddMember(
        Guid clientId, AddMemberRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, AdminAuditStore audit, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminUsers, actorFactory, control, cancellationToken))
            return Results.Forbid();

        if (!Enum.TryParse(request.Role, ignoreCase: true, out LedgerRole role))
            return Results.Problem($"Unknown role '{request.Role}'.", statusCode: StatusCodes.Status422UnprocessableEntity);

        if (await GrantScope.FirstNotHeldByCallerAsync(user, clientId, RolePresets.For(role), actorFactory, control, cancellationToken) is { } badRole)
            return Results.Problem($"Cannot grant '{badRole}' — you do not hold it.", statusCode: StatusCodes.Status422UnprocessableEntity);

        if (await control.GetClientAsync(clientId, cancellationToken) is null)
            return Results.NotFound();

        await control.AddMembershipAsync(request.UserId, clientId, role, cancellationToken);
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow,
            ActorUserId = actorFactory.Create(user).UserId,
            ActorIsDeploymentAdmin = user.HasClaim("admin", "true"),
            Action = "MemberAdded", ClientId = clientId, TargetUserId = request.UserId,
            After = new AuditState { Capabilities = [.. RolePresets.For(role)] },
        }, cancellationToken);
        return Results.Ok(new MembershipResponse(
            request.UserId, clientId, [role.ToString()], [.. RolePresets.CapabilitiesFor([role])]));
    }

    private static async Task<IResult> ListMembers(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminUsers, actorFactory, control, cancellationToken))
            return Results.Forbid();

        IReadOnlyList<Membership> members = await control.GetMembersAsync(clientId, cancellationToken);
        return Results.Ok(members.Select(m => new MembershipResponse(
            m.UserId, m.ClientId, m.GrantedRoles.Select(r => r.ToString()).ToList(), m.Capabilities)).ToList());
    }
}
