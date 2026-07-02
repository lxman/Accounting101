using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>Static capability vocabulary + role presets — backend truth for the admin member editor.</summary>
public static class CapabilityCatalogEndpoints
{
    public static void MapCapabilityCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/capabilities/catalog", GetCatalog).RequireAuthorization();
    }

    private static IResult GetCatalog()
    {
        List<RolePresetDto> roles = Enum.GetValues<LedgerRole>()
            .Select(r => new RolePresetDto(r.ToString(), RolePresets.For(r).ToList()))
            .ToList();
        return Results.Ok(new CapabilityCatalogResponse(Capabilities.All.OrderBy(c => c).ToList(), roles));
    }
}
