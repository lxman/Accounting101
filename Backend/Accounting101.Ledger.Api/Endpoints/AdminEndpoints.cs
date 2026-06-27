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
        RouteGroupBuilder admin = app.MapGroup("/admin").RequireAuthorization(Policy);

        admin.MapPost("/clients", CreateClient);
        admin.MapGet("/clients", ListClients);
        admin.MapPost("/clients/{clientId:guid}/members", AddMember);
        admin.MapGet("/clients/{clientId:guid}/members", ListMembers);
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

    private static async Task<IResult> ListClients(ControlStore control, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClientRegistration> clients = await control.ListClientsAsync(cancellationToken);
        return Results.Ok(clients
            .Select(c => new ClientRegistrationResponse(c.Id, c.Name, c.DatabaseName, c.RequireSegregationOfDuties, FiscalYear.MonthOf(c)))
            .ToList());
    }

    private static async Task<IResult> AddMember(
        Guid clientId, AddMemberRequest request, ControlStore control, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(request.Role, ignoreCase: true, out LedgerRole role))
            return Results.Problem($"Unknown role '{request.Role}'.", statusCode: StatusCodes.Status422UnprocessableEntity);

        if (await control.GetClientAsync(clientId, cancellationToken) is null)
            return Results.NotFound();

        await control.AddMembershipAsync(request.UserId, clientId, role, cancellationToken);
        return Results.Ok(new MembershipResponse(request.UserId, clientId, role.ToString()));
    }

    private static async Task<IResult> ListMembers(Guid clientId, ControlStore control, CancellationToken cancellationToken)
    {
        IReadOnlyList<Membership> members = await control.GetMembersAsync(clientId, cancellationToken);
        return Results.Ok(members.Select(m => new MembershipResponse(m.UserId, m.ClientId, m.Role.ToString())).ToList());
    }
}
