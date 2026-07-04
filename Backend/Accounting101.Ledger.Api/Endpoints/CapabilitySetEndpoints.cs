using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Deployment-wide capability-set management. Sets are global infrastructure, so this surface is
/// deployment-admin only (the <see cref="AdminEndpoints.Policy"/>); per-client assignment of sets
/// to members is a separate, <c>admin.users</c>-gated surface (AC-2). Built-in sets (seeded from
/// role presets) are editable in place but not deletable.
/// </summary>
public static class CapabilitySetEndpoints
{
    public static void MapCapabilitySetEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/capability-sets").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", List);
        g.MapPost("", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapDelete("/{id:guid}", Delete);
    }

    private static CapabilitySetResponse ToResponse(CapabilitySet s) =>
        new(s.Id, s.Name, s.Description, s.Capabilities, s.Builtin, Restricted: s.Restricted);

    /// <summary>Snapshot of a capability set's definition, for before/after audit entries.</summary>
    private static AuditState SetState(CapabilitySet s) =>
        new() { Name = s.Name, Restricted = s.Restricted, Capabilities = s.Capabilities.ToList() };

    // Every capability must be in the known vocabulary; returns a 422 problem for the first offender.
    private static IResult? ValidateCapabilities(IReadOnlyList<string> capabilities)
    {
        foreach (string cap in capabilities)
            if (!Capabilities.All.Contains(cap))
                return Results.Problem($"Unknown capability '{cap}'.", statusCode: StatusCodes.Status422UnprocessableEntity);
        return null;
    }

    private static async Task<IResult> List(ControlStore control, CancellationToken ct)
    {
        IReadOnlyList<CapabilitySet> sets = await control.ListCapabilitySetsAsync(ct);
        List<CapabilitySetResponse> responses = [];
        foreach (CapabilitySet s in sets)
        {
            long count = await control.CountMembersReferencingSetAsync(s.Id, ct);
            responses.Add(ToResponse(s) with { AffectedMemberCount = (int)count });
        }
        return Results.Ok(responses);
    }

    private static async Task<IResult> Create(CreateCapabilitySetRequest request, ControlStore control, AdminAuditStore audit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Name is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        IReadOnlyList<string> capabilities = request.Capabilities ?? [];
        if (ValidateCapabilities(capabilities) is { } capError) return capError;
        if (await control.GetCapabilitySetByNameAsync(request.Name, ct) is not null)
            return Results.Problem($"A capability set named '{request.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);

        CapabilitySet set = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description,
            Capabilities = capabilities,
            Builtin = false,
            Restricted = request.Restricted,
        };
        await control.CreateCapabilitySetAsync(set, ct);
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, ActorIsDeploymentAdmin = true,
            Action = "SetCreated", TargetSetId = set.Id, After = SetState(set),
        }, ct);
        return Results.Created($"/capability-sets/{set.Id}", ToResponse(set));
    }

    private static async Task<IResult> Update(Guid id, UpdateCapabilitySetRequest request, ControlStore control, AdminAuditStore audit, CancellationToken ct)
    {
        CapabilitySet? existing = await control.GetCapabilitySetAsync(id, ct);
        if (existing is null) return Results.NotFound();
        AuditState beforeState = SetState(existing);
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Name is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        IReadOnlyList<string> capabilities = request.Capabilities ?? [];
        if (ValidateCapabilities(capabilities) is { } capError) return capError;

        CapabilitySet? byName = await control.GetCapabilitySetByNameAsync(request.Name, ct);
        if (byName is not null && byName.Id != id)
            return Results.Problem($"A capability set named '{request.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);

        existing.Name = request.Name.Trim();
        existing.Description = request.Description;
        existing.Capabilities = capabilities;
        existing.Restricted = request.Restricted;
        await control.UpdateCapabilitySetAsync(existing, ct);
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, ActorIsDeploymentAdmin = true,
            Action = "SetUpdated", TargetSetId = id, Before = beforeState, After = SetState(existing),
        }, ct);
        long affected = await control.CountMembersReferencingSetAsync(id, ct);
        return Results.Ok(ToResponse(existing) with { AffectedMemberCount = (int)affected });
    }

    private static async Task<IResult> Delete(Guid id, ControlStore control, AdminAuditStore audit, CancellationToken ct)
    {
        CapabilitySet? existing = await control.GetCapabilitySetAsync(id, ct);
        if (existing is null) return Results.NotFound();
        if (existing.Builtin)
            return Results.Problem("Built-in capability sets cannot be deleted.", statusCode: StatusCodes.Status409Conflict);
        long referencing = await control.CountMembersReferencingSetAsync(id, ct);
        if (referencing > 0)
            return Results.Problem($"{referencing} member(s) reference this set; reassign them before deleting.",
                statusCode: StatusCodes.Status409Conflict);
        await control.DeleteCapabilitySetAsync(id, ct);
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, ActorIsDeploymentAdmin = true,
            Action = "SetDeleted", TargetSetId = id, Before = SetState(existing),
        }, ct);
        return Results.NoContent();
    }
}
