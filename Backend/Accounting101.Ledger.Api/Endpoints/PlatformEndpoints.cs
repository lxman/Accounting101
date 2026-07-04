using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Contracts;
using MongoDB.Driver;

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
        platform.MapPost("/firms", ProvisionFirm);
        platform.MapPatch("/firms/{firmId:guid}/status", SetFirmStatus);
        platform.MapGet("/clusters", ListClusters);
        platform.MapPost("/clusters", RegisterCluster);
        platform.MapGet("/usage", GetUsage);
    }

    private static async Task<IResult> ListFirms(PlatformStore platform, CancellationToken cancellationToken)
    {
        IReadOnlyList<FirmRegistration> firms = await platform.ListFirmsAsync(cancellationToken);
        return Results.Ok(firms.Select(ToResponse).ToList());
    }

    private static async Task<IResult> ProvisionFirm(
        ProvisionFirmRequest request, PlatformStore platform, IMongoClientFactory factory,
        IEnumerable<ModuleRegistration> modules, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Problem("Firm name is required.", statusCode: StatusCodes.Status400BadRequest);

        string clusterKey = string.IsNullOrWhiteSpace(request.ClusterKey) ? "default" : request.ClusterKey;

        IMongoClient client;
        try
        {
            client = await factory.GetAsync(clusterKey, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem($"Unknown cluster '{clusterKey}'.", statusCode: StatusCodes.Status400BadRequest);
        }

        Guid firmId = Guid.NewGuid();
        string controlDatabase = "firm_" + firmId.ToString("N") + "_control";
        FirmRegistration firm = new()
        {
            Id = firmId,
            Name = request.Name,
            ControlDatabase = controlDatabase,
            ClusterKey = clusterKey,
            Status = FirmStatus.Active,
            CreatedUtc = DateTime.UtcNow,
        };
        // Seed the new firm's control DB FIRST, then commit the registration — so a seed failure leaves no
        // registered (but unusable) firm and the operator can retry cleanly. Seeding is idempotent, and no
        // membership is created here (memberships are per-client).
        ControlStore control = new(client.GetDatabase(controlDatabase));
        await control.SeedBuiltinCapabilitySetsAsync(cancellationToken);
        await control.SeedModulesAsync(modules, cancellationToken);

        await platform.RegisterFirmAsync(firm, cancellationToken);

        return Results.Created($"/platform/firms/{firmId}", ToResponse(firm));
    }

    private static async Task<IResult> SetFirmStatus(
        Guid firmId, SetFirmStatusRequest request, PlatformStore platform, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(request.Status, ignoreCase: true, out FirmStatus status))
            return Results.Problem($"Unknown status '{request.Status}'.", statusCode: StatusCodes.Status422UnprocessableEntity);

        if (await platform.GetFirmAsync(firmId, cancellationToken) is null)
            return Results.NotFound();

        await platform.SetFirmStatusAsync(firmId, status, cancellationToken);
        FirmRegistration updated = (await platform.GetFirmAsync(firmId, cancellationToken))!;
        return Results.Ok(ToResponse(updated));
    }

    private static async Task<IResult> ListClusters(PlatformStore platform, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClusterRegistration> clusters = await platform.ListClustersAsync(cancellationToken);
        return Results.Ok(clusters
            .Select(c => new ClusterResponse(c.Key, !string.IsNullOrEmpty(c.ConnectionString)))
            .ToList());
    }

    private static async Task<IResult> RegisterCluster(
        RegisterClusterRequest request, PlatformStore platform, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.ConnectionString))
            return Results.Problem("Cluster key and connection string are required.", statusCode: StatusCodes.Status400BadRequest);

        await platform.RegisterClusterAsync(
            new ClusterRegistration { Key = request.Key, ConnectionString = request.ConnectionString }, cancellationToken);
        return Results.Created($"/platform/clusters/{request.Key}", new ClusterResponse(request.Key, true));
    }

    private static async Task<IResult> GetUsage(
        PlatformStore platform, IMongoClientFactory factory, CancellationToken cancellationToken)
    {
        IReadOnlyList<FirmRegistration> firms = await platform.ListFirmsAsync(cancellationToken);
        List<FirmUsageResponse> result = [];
        foreach (FirmRegistration firm in firms)
        {
            IMongoClient mongo;
            try
            {
                mongo = await factory.GetAsync(firm.ClusterKey, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Cluster no longer registered — skip this firm rather than fail the whole meter.
                continue;
            }

            ControlStore control = new(mongo.GetDatabase(firm.ControlDatabase));
            IReadOnlyList<ClientRegistration> clients = await control.ListClientsAsync(cancellationToken);
            List<ClientRegistration> active = clients.Where(c => c.Status == ClientStatus.Active).ToList();

            Dictionary<string, int> counts = active
                .SelectMany(c => c.EnabledModules)
                .GroupBy(key => key)
                .ToDictionary(g => g.Key, g => g.Count());

            result.Add(new FirmUsageResponse(firm.Id, firm.Name, active.Count, counts));
        }
        return Results.Ok(new UsageResponse(result));
    }

    private static FirmResponse ToResponse(FirmRegistration f) =>
        new(f.Id, f.Name, f.Status.ToString(), f.ClusterKey, f.ControlDatabase, f.CreatedUtc);
}
