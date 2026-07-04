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

    /// <summary>Builds a control-plane audit entry for a per-client member mutation.</summary>
    private static AdminAuditEntry AuditEntry(
        ClaimsPrincipal user, IActorFactory actorFactory, string action, Guid clientId,
        Guid? targetUserId = null, AuditState? before = null, AuditState? after = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            ActorUserId = actorFactory.Create(user).UserId,
            ActorIsDeploymentAdmin = user.HasClaim("admin", "true"),
            Action = action,
            ClientId = clientId,
            TargetUserId = targetUserId,
            Before = before,
            After = after,
        };

    private static async Task<IResult> ListMembers(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        IReadOnlyList<Membership> members = await control.GetMembersAsync(clientId, ct);
        IReadOnlyList<CapabilitySet> catalog = await control.ListCapabilitySetsAsync(ct);
        return Results.Ok(members.Select(m => ToResponse(m, catalog)).ToList());
    }

    private static async Task<IResult> AddMember(
        Guid clientId, AddClientMemberRequest request, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, AdminAuditStore audit, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!TryParse(request.Roles, request.Capabilities, out List<LedgerRole> roles, out IResult? error)) return error!;
        // Effective grant = role-derived preset caps (live-bound by AC-2 Resolve) unioned with the inline
        // caps — checking Capabilities alone lets a caller escalate via the Roles field.
        IEnumerable<string> effectiveAdd = RolePresets.CapabilitiesFor(roles).Concat(request.Capabilities);
        if (await GrantScope.FirstNotHeldByCallerAsync(user, clientId, effectiveAdd, actorFactory, control, ct) is { } badAdd)
            return Results.Problem($"Cannot grant '{badAdd}' — you do not hold it.", statusCode: StatusCodes.Status422UnprocessableEntity);
        if (await control.IsMemberAsync(request.UserId, clientId, ct))
            return Results.Problem("Already a member.", statusCode: StatusCodes.Status409Conflict);
        await control.SetMembershipAsync(request.UserId, clientId, roles, request.Capabilities, ct);
        await audit.AppendAsync(AuditEntry(user, actorFactory, "MemberAdded", clientId, request.UserId,
            after: new AuditState { Capabilities = request.Capabilities }), ct);
        return Results.Ok(new MembershipResponse(request.UserId, clientId, request.Roles, request.Capabilities));
    }

    private static async Task<IResult> SetMember(
        Guid clientId, Guid userId, SetMemberRequest request, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, AdminAuditStore audit, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!TryParse(request.Roles, request.Capabilities, out List<LedgerRole> roles, out IResult? error)) return error!;
        // Effective grant = role-derived preset caps (live-bound by AC-2 Resolve) unioned with the inline
        // caps — checking Capabilities alone lets a caller escalate via the Roles field.
        IEnumerable<string> effectiveSet = RolePresets.CapabilitiesFor(roles).Concat(request.Capabilities);
        if (await GrantScope.FirstNotHeldByCallerAsync(user, clientId, effectiveSet, actorFactory, control, ct) is { } badSet)
            return Results.Problem($"Cannot grant '{badSet}' — you do not hold it.", statusCode: StatusCodes.Status422UnprocessableEntity);
        if (!await control.IsMemberAsync(userId, clientId, ct)) return Results.NotFound();
        bool keepsAdmin = request.Capabilities.Contains(Capabilities.AdminUsers);
        if (await WouldLeaveNoAdmin(control, clientId, userId, keepsAdmin, ct))
            return Results.Problem("Cannot remove the last administrator.", statusCode: StatusCodes.Status409Conflict);
        Membership? beforeSet = await control.GetMembershipAsync(userId, clientId, ct);
        await control.SetMembershipAsync(userId, clientId, roles, request.Capabilities, ct);
        await audit.AppendAsync(AuditEntry(user, actorFactory, "MemberCapabilitiesSet", clientId, userId,
            before: new AuditState { Capabilities = beforeSet?.Capabilities.ToList() },
            after: new AuditState { Capabilities = request.Capabilities }), ct);
        return Results.Ok(new MembershipResponse(userId, clientId, request.Roles, request.Capabilities));
    }

    // Assign a member to capability sets — the go-forward, live-bound grant. Resolved capabilities are
    // the union of the referenced sets' current capabilities (never client-supplied). Last-admin guarded.
    private static async Task<IResult> AssignSets(
        Guid clientId, Guid userId, AssignSetsRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, AdminAuditStore audit, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!await control.IsMemberAsync(userId, clientId, ct)) return Results.NotFound();
        Membership? beforeAssign = await control.GetMembershipAsync(userId, clientId, ct);

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

        if (await GrantScope.FirstNotHeldByCallerAsync(user, clientId, resolved, actorFactory, control, ct) is { } badAssign)
            return Results.Problem($"Cannot grant '{badAssign}' — you do not hold it.", statusCode: StatusCodes.Status422UnprocessableEntity);

        if (sets.Any(s => s.Restricted) && !user.HasClaim("admin", "true"))
            return Results.Problem("Only a deployment admin may assign a restricted capability set.",
                statusCode: StatusCodes.Status403Forbidden);

        bool keepsAdmin = resolved.Contains(Capabilities.AdminUsers);
        if (await WouldLeaveNoAdmin(control, clientId, userId, keepsAdmin, ct))
            return Results.Problem("Cannot remove the last administrator.", statusCode: StatusCodes.Status409Conflict);

        await control.SetMembershipSetsAsync(userId, clientId, request.SetIds, ct);
        await audit.AppendAsync(AuditEntry(user, actorFactory, "MemberSetsAssigned", clientId, userId,
            before: new AuditState { SetIds = beforeAssign?.GrantedSetIds.ToList(), Capabilities = beforeAssign?.Capabilities.ToList() },
            after: new AuditState { SetIds = request.SetIds, Capabilities = resolved.ToList() }), ct);
        return Results.Ok(new MembershipResponse(
            userId, clientId, sets.Select(s => s.Name).ToList(), resolved.ToList()));
    }

    private static async Task<IResult> RemoveMember(
        Guid clientId, Guid userId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, AdminAuditStore audit, CancellationToken ct)
    {
        if (!await CallerMayManage(user, clientId, actorFactory, control, ct)) return Results.Forbid();
        if (!await control.IsMemberAsync(userId, clientId, ct)) return Results.NotFound();
        if (await WouldLeaveNoAdmin(control, clientId, userId, changedUserKeepsAdmin: false, ct))
            return Results.Problem("Cannot remove the last administrator.", statusCode: StatusCodes.Status409Conflict);
        Membership? beforeRemove = await control.GetMembershipAsync(userId, clientId, ct);
        await control.RemoveMembershipAsync(userId, clientId, ct);
        await audit.AppendAsync(AuditEntry(user, actorFactory, "MemberRemoved", clientId, userId,
            before: new AuditState { SetIds = beforeRemove?.GrantedSetIds.ToList(), Capabilities = beforeRemove?.Capabilities.ToList() }), ct);
        return Results.NoContent();
    }
}
