using Accounting101.Inventory;
using Accounting101.Ledger.Contracts;
using Accounting101.ModuleKit;

namespace Accounting101.Inventory.Api;

/// <summary>The inventory HTTP surface: the item-register lifecycle under /clients/{clientId}. Responses
/// are ItemView; only the request body is a DTO.</summary>
public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder clients = app.MapGroup("/clients/{clientId:guid}").RequireAuthorization();

        clients.MapPost("/items", CreateItem);
        clients.MapPut("/items/{itemId:guid}", UpdateItem);
        clients.MapPost("/items/{itemId:guid}/deactivate", DeactivateItem);
        clients.MapPost("/items/{itemId:guid}/reactivate", ReactivateItem);
        clients.MapGet("/items/{itemId:guid}", GetItem);
        clients.MapGet("/items", ListItems);

        clients.MapPost("/movements", RecordMovement);
        clients.MapPost("/movements/{id:guid}/void", VoidMovement);
        clients.MapGet("/movements/{id:guid}", GetMovement);
        clients.MapGet("/movements", ListMovements);

        clients.MapGet("/inventory/chart-readiness", ChartReadiness);
    }

    private static async Task<IResult> RecordMovement(
        Guid clientId, RecordMovementRequest request, InventoryMovementService service, CancellationToken cancellationToken)
    {
        try
        {
            StockMovement movement = await service.RecordAsync(clientId, request.ToRequest(), cancellationToken);
            return Results.Created($"/clients/{clientId}/movements/{movement.Id}", new StockMovementView(movement));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> VoidMovement(
        Guid clientId, Guid id, VoidReasonRequest? request, InventoryMovementService service, CancellationToken cancellationToken)
    {
        try
        {
            StockMovement movement = await service.VoidAsync(clientId, id, request?.Reason, cancellationToken);
            return Results.Ok(new StockMovementView(movement));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetMovement(
        Guid clientId, Guid id, IStockMovementStore store, CancellationToken cancellationToken)
    {
        StockMovement? movement = await store.GetAsync(clientId, id, cancellationToken);
        return movement is null ? Results.NotFound() : Results.Ok(new StockMovementView(movement));
    }

    private static async Task<IResult> ListMovements(
        Guid clientId, Guid? itemId, int? skip, int? limit, string? order, bool? includeVoided,
        IStockMovementStore store, CancellationToken cancellationToken)
    {
        if (itemId is not { } id)
            return Results.Problem("itemId is required.", statusCode: StatusCodes.Status400BadRequest);

        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

        PagedResponse<StockMovement> page = await store.GetByItemPagedAsync(
            clientId, id, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeVoided ?? false, cancellationToken);

        return Results.Ok(new PagedResponse<StockMovementView>(
            page.Items.Select(m => new StockMovementView(m)).ToList(), page.Total, page.Skip, page.Limit));
    }

    private static async Task<IResult> CreateItem(
        Guid clientId, SaveItemRequest request, InventoryService service, CancellationToken cancellationToken)
    {
        try
        {
            Item item = await service.CreateAsync(clientId, request.ToBody(), cancellationToken);
            return Results.Created($"/clients/{clientId}/items/{item.Id}", new ItemView(item));
        }
        catch (InvalidOperationException ex) // duplicate Sku
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> UpdateItem(
        Guid clientId, Guid itemId, SaveItemRequest request, InventoryService service, CancellationToken cancellationToken)
    {
        try
        {
            UpdateResult result = await service.UpdateAsync(clientId, itemId, request.ToBody(), cancellationToken);
            return result.Outcome switch
            {
                UpdateOutcome.Updated => Results.Ok(new ItemView(result.Item!)),
                UpdateOutcome.NotFound => Results.NotFound(),
                UpdateOutcome.Inactive => Results.Problem(
                    "Item is inactive; reactivate it before editing.", statusCode: StatusCodes.Status409Conflict),
                UpdateOutcome.DuplicateSku => Results.Problem(
                    "An item with that Sku already exists.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem("Unexpected update result.", statusCode: StatusCodes.Status500InternalServerError),
            };
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> DeactivateItem(
        Guid clientId, Guid itemId, InventoryService service, CancellationToken cancellationToken)
    {
        DeactivateResult result = await service.DeactivateAsync(clientId, itemId, cancellationToken);
        return result switch
        {
            DeactivateResult.Deactivated => Results.NoContent(),
            DeactivateResult.NotFound => Results.NotFound(),
            DeactivateResult.AlreadyInactive => Results.Problem(
                "Item is already inactive.", statusCode: StatusCodes.Status409Conflict),
            DeactivateResult.HasStock => Results.Problem(
                "Item has stock on hand; issue or adjust it to zero before deactivating.", statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem("Unexpected deactivate result.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> ReactivateItem(
        Guid clientId, Guid itemId, InventoryService service, CancellationToken cancellationToken)
    {
        ReactivateResult result = await service.ReactivateAsync(clientId, itemId, cancellationToken);
        if (result == ReactivateResult.NotFound) return Results.NotFound();
        if (result == ReactivateResult.AlreadyActive)
            return Results.Problem("Item is already active.", statusCode: StatusCodes.Status409Conflict);
        Item? item = await service.GetAsync(clientId, itemId, cancellationToken);
        return item is null ? Results.NotFound() : Results.Ok(new ItemView(item));
    }

    private static async Task<IResult> GetItem(
        Guid clientId, Guid itemId, InventoryService service, CancellationToken cancellationToken)
    {
        Item? item = await service.GetAsync(clientId, itemId, cancellationToken);
        return item is null ? Results.NotFound() : Results.Ok(new ItemView(item));
    }

    private static async Task<IResult> ListItems(
        Guid clientId, int? skip, int? limit, string? order, bool? includeInactive,
        InventoryService service, CancellationToken cancellationToken)
    {
        if (!TryOrder(order, out bool descending))
            return Results.Problem("order must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);

        PagedResponse<Item> page = await service.GetPagedAsync(
            clientId, Math.Max(0, skip ?? 0), Math.Clamp(limit ?? 50, 1, 200), descending, includeInactive ?? false, cancellationToken);

        return Results.Ok(new PagedResponse<ItemView>(
            page.Items.Select(i => new ItemView(i)).ToList(), page.Total, page.Skip, page.Limit));
    }

    private static bool TryOrder(string? order, out bool descending)
    {
        descending = true;
        if (string.IsNullOrEmpty(order)) return true;
        if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase)) { descending = true; return true; }
        if (string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase)) { descending = false; return true; }
        return false;
    }

    // ── Chart Readiness ─────────────────────────────────────────────────────

    private static async Task<IResult> ChartReadiness(
        Guid clientId, InventoryChartRequirements requirements, ILedgerClient ledger, CancellationToken cancellationToken)
    {
        CapabilitiesResponse caps = await ledger.GetMyCapabilitiesAsync(clientId, cancellationToken); // non-member → relayed 403
        if (!ReadinessAccess.Allows("inventory", caps.DeploymentAdmin, caps.Capabilities))
            return Results.Problem("Not authorized to view this module's chart readiness.", statusCode: StatusCodes.Status403Forbidden);

        IReadOnlyList<AccountRequirement> reqs = await requirements.ForAsync(clientId, cancellationToken);
        IReadOnlyList<AccountResponse> chart = await ledger.GetAccountsAsync(clientId, cancellationToken);
        return Results.Ok(ChartReadinessChecker.Check(reqs, chart, "inventory"));
    }
}
