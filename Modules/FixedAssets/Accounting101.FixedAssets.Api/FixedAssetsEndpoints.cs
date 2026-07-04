using Accounting101.FixedAssets;
using Accounting101.Ledger.Contracts;

namespace Accounting101.FixedAssets.Api;

/// <summary>The fixed-assets HTTP surface: the asset-register lifecycle under /clients/{clientId}.
/// Responses are AssetView; only the request body is a DTO.</summary>
public static class FixedAssetsEndpoints
{
    public static void MapFixedAssetsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/assets", CreateAsset);
        clients.MapPut("/assets/{assetId:guid}", UpdateAsset);
        clients.MapPost("/assets/{assetId:guid}/deactivate", DeactivateAsset);
        clients.MapPost("/assets/{assetId:guid}/reactivate", ReactivateAsset);
        clients.MapGet("/assets/{assetId:guid}", GetAsset);
        clients.MapGet("/assets", ListAssets);
    }

    private static async Task<IResult> CreateAsset(
        Guid clientId, SaveAssetRequest request, FixedAssetsService service, CancellationToken cancellationToken)
    {
        try
        {
            Asset asset = await service.CreateAsync(clientId, request.ToBody(), cancellationToken);
            return Results.Created($"/clients/{clientId}/assets/{asset.Id}", new AssetView(asset));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> UpdateAsset(
        Guid clientId, Guid assetId, SaveAssetRequest request, FixedAssetsService service, CancellationToken cancellationToken)
    {
        try
        {
            UpdateResult result = await service.UpdateAsync(clientId, assetId, request.ToBody(), cancellationToken);
            return result.Outcome switch
            {
                UpdateOutcome.Updated => Results.Ok(new AssetView(result.Asset!)),
                UpdateOutcome.NotFound => Results.NotFound(),
                UpdateOutcome.Inactive => Results.Problem(
                    "Asset is inactive; reactivate it before editing.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem("Unexpected update result.", statusCode: StatusCodes.Status500InternalServerError),
            };
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> ReactivateAsset(
        Guid clientId, Guid assetId, FixedAssetsService service, CancellationToken cancellationToken)
    {
        ReactivateResult result = await service.ReactivateAsync(clientId, assetId, cancellationToken);
        if (result == ReactivateResult.NotFound) return Results.NotFound();
        if (result == ReactivateResult.AlreadyActive)
            return Results.Problem("Asset is already active.", statusCode: StatusCodes.Status409Conflict);
        Asset? asset = await service.GetAsync(clientId, assetId, cancellationToken);
        return asset is null ? Results.NotFound() : Results.Ok(new AssetView(asset));
    }

    private static async Task<IResult> DeactivateAsset(
        Guid clientId, Guid assetId, FixedAssetsService service, CancellationToken cancellationToken)
    {
        DeactivateResult result = await service.DeactivateAsync(clientId, assetId, cancellationToken);
        return result switch
        {
            DeactivateResult.Deactivated => Results.NoContent(),
            DeactivateResult.NotFound => Results.NotFound(),
            DeactivateResult.AlreadyInactive => Results.Problem(
                "Asset is already inactive.", statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem("Unexpected deactivate result.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> GetAsset(
        Guid clientId, Guid assetId, FixedAssetsService service, CancellationToken cancellationToken)
    {
        Asset? asset = await service.GetAsync(clientId, assetId, cancellationToken);
        return asset is null ? Results.NotFound() : Results.Ok(new AssetView(asset));
    }

    private static async Task<IResult> ListAssets(
        Guid clientId, int? skip, int? limit, string? order, bool? includeInactive,
        IAssetStore store, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

        PagedResponse<Asset> page = await store.GetByClientPagedAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeInactive ?? false, cancellationToken);

        return Results.Ok(new PagedResponse<AssetView>(
            page.Items.Select(a => new AssetView(a)).ToList(), page.Total, page.Skip, page.Limit));
    }

    private static bool TryOrder(string? order, out bool descending)
    {
        descending = true;
        if (string.IsNullOrEmpty(order)) return true;
        if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) { descending = true; return true; }
        if (string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)) { descending = false; return true; }
        return false;
    }
}
