using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// The platform-operator control plane: provision and manage firms and clusters in the platform_control
/// registry. Gated by the <see cref="Policy"/> (a trusted <c>platform=true</c> token claim) — one tier
/// above firm admin. These handlers operate on the singleton <see cref="PlatformStore"/> and do not use
/// the request's firm scope.
/// </summary>
public static class PlatformEndpoints
{
    public const string Policy = "PlatformAdmin";

    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder platform = app.MapGroup("/platform").RequireAuthorization(Policy);
        platform.MapGet("/firms", ListFirms);
    }

    private static async Task<IResult> ListFirms(PlatformStore platform, CancellationToken cancellationToken)
    {
        IReadOnlyList<FirmRegistration> firms = await platform.ListFirmsAsync(cancellationToken);
        return Results.Ok(firms.Select(ToResponse).ToList());
    }

    private static FirmResponse ToResponse(FirmRegistration f) =>
        new(f.Id, f.Name, f.Status.ToString(), f.ClusterKey, f.ControlDatabase, f.CreatedUtc);
}
