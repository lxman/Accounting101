using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Per-client member management for a client Admin (holds <c>admin.users</c>) or a deployment admin.
/// Distinct from the deployment-only <see cref="AdminEndpoints"/> provisioning surface. Enforces a
/// last-admin guard so a client can never be left without an administrator.
/// </summary>
public static class MemberEndpoints
{
    public static void MapMemberEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/clients/{clientId:guid}/members").RequireAuthorization();
        g.MapGet("", ListMembers);
        g.MapPost("", AddMember);
        g.MapPut("/{userId:guid}", SetMember);
        g.MapPut("/{userId:guid}/sets", AssignSets);
        g.MapDelete("/{userId:guid}", RemoveMember);
    }

    // Allow deployment admins (admin=true claim) or a member holding admin.users.
    private static Task<bool> CallerMayManage(ClaimsPrincipal user, Guid clientId, IActorFactory actorFactory, ControlStore control, CancellationToken ct) =>
        AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminUsers, actorFactory, control, ct);

    private static MembershipResponse ToResponse(Membership m, IReadOnlyList<CapabilitySet> catalog)
    {
        Dictionary<Guid, string> nameById = catalog.ToDictionary(s => s.Id, s => s.Name);
        List<string> setNames = m.GrantedSetIds
            .Where(nameById.ContainsKey).Select(id => nameById[id]).ToList();
        return new MembershipResponse(
            m.UserId, m.ClientId,
            m.GrantedRoles.Select(r => r.ToString()).ToList(), m.Capabilities,
            m.GrantedSetIds, setNames);
    }

    private static bool TryParse(IReadOnlyList<string> roleNames, IReadOnlyList<string> capabilities, out List<LedgerRole> roles, out IResult? error)
    {
        roles = [];
        foreach (string name in roleNames)
        {
            if (!Enum.TryParse(name, ignoreCase: true, out LedgerRole role))
            { error = Results.Problem($"Unknown role '{name}'.", statusCode: StatusCodes.Status422UnprocessableEntity); return false; }
            roles.Add(role);
        }
        foreach (string cap in capabilities)
            if (!Capabilities.All.Contains(cap))
            { error = Results.Problem($"Unknown capability '{cap}'.", statusCode: StatusCodes.Status422UnprocessableEntity); return false; }
        error = null;
        return true;
    }

    // A change is allowed unless it would leave the client with zero admin.users holders.
    private static async Task<bool> WouldLeaveNoAdmin(ControlStore control, Guid clientId, Guid changedUser, bool changedUserKeepsAdmin, CancellationToken ct)
    {
        IReadOnlyList<Membership> members = await control.GetMembersAsync(clientId, ct);
        bool anotherAdminRemains = members.Any(m => m.UserId != changedUser && m.Capabilities.Contains(Capabilities.AdminUsers));
        return !anotherAdminRemains && !changedUserKeepsAdmin;
    }

    private static async Task<IResult> ListMembers(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        IReadOnlyList<Membership> members = await control.GetMembersAsync(clientId, ct);
        IReadOnlyList<CapabilitySet> catalog = await control.ListCapabilitySetsAsync(ct);
        return Results.Ok(members.Select(m => ToResponse(m, catalog)).ToList());
    }

    private static async Task<IResult> AddMember(
        Guid clientId, AddClientMemberRequest request, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!TryParse(request.Roles, request.Capabilities, out List<LedgerRole> roles, out IResult? error)) return error!;
        if (await control.IsMemberAsync(request.UserId, clientId, ct))
            return Results.Problem("Already a member.", statusCode: StatusCodes.Status409Conflict);
        await control.SetMembershipAsync(request.UserId, clientId, roles, request.Capabilities, ct);
        return Results.Ok(new MembershipResponse(request.UserId, clientId, request.Roles, request.Capabilities));
    }

    private static async Task<IResult> SetMember(
        Guid clientId, Guid userId, SetMemberRequest request, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!TryParse(request.Roles, request.Capabilities, out List<LedgerRole> roles, out IResult? error)) return error!;
        if (!await control.IsMemberAsync(userId, clientId, ct)) return Results.NotFound();
        bool keepsAdmin = request.Capabilities.Contains(Capabilities.AdminUsers);
        if (await WouldLeaveNoAdmin(control, clientId, userId, keepsAdmin, ct))
            return Results.Problem("Cannot remove the last administrator.", statusCode: StatusCodes.Status409Conflict);
        await control.SetMembershipAsync(userId, clientId, roles, request.Capabilities, ct);
        return Results.Ok(new MembershipResponse(userId, clientId, request.Roles, request.Capabilities));
    }

    // Assign a member to capability sets — the go-forward, live-bound grant. Resolved capabilities are
    // the union of the referenced sets' current capabilities (never client-supplied). Last-admin guarded.
    private static async Task<IResult> AssignSets(
        Guid clientId, Guid userId, AssignSetsRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!await control.IsMemberAsync(userId, clientId, ct)) return Results.NotFound();

        // Validate every set exists and gather the sets we resolve from.
        List<CapabilitySet> sets = [];
        foreach (Guid setId in request.SetIds)
        {
            CapabilitySet? set = await control.GetCapabilitySetAsync(setId, ct);
            if (set is null)
                return Results.Problem($"Unknown capability set '{setId}'.", statusCode: StatusCodes.Status422UnprocessableEntity);
            sets.Add(set);
        }

        HashSet<string> resolved = [];
        foreach (CapabilitySet set in sets) resolved.UnionWith(set.Capabilities);

        bool keepsAdmin = resolved.Contains(Capabilities.AdminUsers);
        if (await WouldLeaveNoAdmin(control, clientId, userId, keepsAdmin, ct))
            return Results.Problem("Cannot remove the last administrator.", statusCode: StatusCodes.Status409Conflict);

        await control.SetMembershipSetsAsync(userId, clientId, request.SetIds, ct);
        return Results.Ok(new MembershipResponse(
            userId, clientId, sets.Select(s => s.Name).ToList(), resolved.ToList()));
    }

    private static async Task<IResult> RemoveMember(
        Guid clientId, Guid userId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!await control.IsMemberAsync(userId, clientId, ct)) return Results.NotFound();
        if (await WouldLeaveNoAdmin(control, clientId, userId, changedUserKeepsAdmin: false, ct))
            return Results.Problem("Cannot remove the last administrator.", statusCode: StatusCodes.Status409Conflict);
        await control.RemoveMembershipAsync(userId, clientId, ct);
        return Results.NoContent();
    }
}
