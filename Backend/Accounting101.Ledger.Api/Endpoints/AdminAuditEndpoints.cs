using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>Read-only view of the control-plane audit trail. Deployment-admin only — the log spans all
/// clients and records who changed whose access.</summary>
public static class AdminAuditEndpoints
{
    public static void MapAdminAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/admin")
           .RequireAuthorization(AdminEndpoints.Policy)
           .MapGet("/audit", Query);
    }

    private static async Task<IResult> Query(
        AdminAuditStore audit, CancellationToken ct,
        Guid? clientId = null, Guid? actorUserId = null, Guid? targetUserId = null, int limit = 100)
    {
        IReadOnlyList<AdminAuditEntry> entries =
            await audit.QueryAsync(new AdminAuditFilter(clientId, actorUserId, targetUserId, limit), ct);
        return Results.Ok(entries.Select(e => new AdminAuditEntryResponse(
            e.Id, e.Timestamp, e.ActorUserId, e.ActorIsDeploymentAdmin, e.Action,
            e.ClientId, e.TargetUserId, e.TargetSetId,
            e.Before?.Capabilities, e.After?.Capabilities)).ToList());
    }
}
