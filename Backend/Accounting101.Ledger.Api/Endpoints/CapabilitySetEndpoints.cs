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
        new(s.Id, s.Name, s.Description, s.Capabilities, s.Builtin);

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
        return Results.Ok(sets.Select(ToResponse).ToList());
    }

    private static async Task<IResult> Create(CreateCapabilitySetRequest request, ControlStore control, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Name is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        if (ValidateCapabilities(request.Capabilities) is { } capError) return capError;
        if (await control.GetCapabilitySetByNameAsync(request.Name, ct) is not null)
            return Results.Problem($"A capability set named '{request.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);

        CapabilitySet set = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description,
            Capabilities = request.Capabilities,
            Builtin = false,
        };
        await control.CreateCapabilitySetAsync(set, ct);
        return Results.Created($"/capability-sets/{set.Id}", ToResponse(set));
    }

    private static async Task<IResult> Update(Guid id, UpdateCapabilitySetRequest request, ControlStore control, CancellationToken ct)
    {
        CapabilitySet? existing = await control.GetCapabilitySetAsync(id, ct);
        if (existing is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Name is required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        if (ValidateCapabilities(request.Capabilities) is { } capError) return capError;

        CapabilitySet? byName = await control.GetCapabilitySetByNameAsync(request.Name, ct);
        if (byName is not null && byName.Id != id)
            return Results.Problem($"A capability set named '{request.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);

        existing.Name = request.Name.Trim();
        existing.Description = request.Description;
        existing.Capabilities = request.Capabilities;
        await control.UpdateCapabilitySetAsync(existing, ct);
        return Results.Ok(ToResponse(existing));
    }

    private static async Task<IResult> Delete(Guid id, ControlStore control, CancellationToken ct)
    {
        CapabilitySet? existing = await control.GetCapabilitySetAsync(id, ct);
        if (existing is null) return Results.NotFound();
        if (existing.Builtin)
            return Results.Problem("Built-in capability sets cannot be deleted.", statusCode: StatusCodes.Status409Conflict);
        // In-use guard (members referencing this set) is added in AC-2, once GrantedSetIds exists.
        await control.DeleteCapabilitySetAsync(id, ct);
        return Results.NoContent();
    }
}
