using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>Per-client posting-account configuration: which chart account each enabled module posts to
/// per slot. Gated by <c>admin.postingAccounts</c> (a deployment admin overrides). Values are advisory
/// against the chart (readiness), never enforced here.</summary>
public static class PostingAccountEndpoints
{
    public static void MapPostingAccountEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/clients/{clientId:guid}/posting-accounts").RequireAuthorization();
        g.MapGet("", GetPostingAccounts);
        g.MapPut("/{moduleKey}", SetPostingAccounts);
    }

    private static async Task<IResult> GetPostingAccounts(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        PostingAccountStore store, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminPostingAccounts, actorFactory, control, ct))
            return Results.Forbid();

        ClientRegistration? client = await control.GetClientAsync(clientId, ct);
        if (client is null) return Results.NotFound();

        HashSet<string> enabled = [.. client.EnabledModules];
        PostingAccountsDoc? doc = await store.GetAsync(clientId, ct);

        Guid? Current(string moduleKey, string slotKey) =>
            doc is not null && doc.Accounts.TryGetValue(moduleKey, out Dictionary<string, Guid>? slots)
                && slots.TryGetValue(slotKey, out Guid id) && id != Guid.Empty
                ? id : null;

        List<PostingAccountSlotResponse> slots = PostingAccountSlots.All
            .Where(s => enabled.Contains(s.ModuleKey))
            .Select(s => new PostingAccountSlotResponse(
                s.ModuleKey, s.SlotKey, s.Label, s.ExpectedType, s.RequiredDimensions, Current(s.ModuleKey, s.SlotKey)))
            .ToList();

        return Results.Ok(new PostingAccountsResponse(slots));
    }

    private static async Task<IResult> SetPostingAccounts(
        Guid clientId, string moduleKey, SetPostingAccountsRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, PostingAccountStore store, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminPostingAccounts, actorFactory, control, ct))
            return Results.Forbid();

        if (await control.GetClientAsync(clientId, ct) is null) return Results.NotFound();

        IReadOnlyList<PostingAccountSlot> moduleSlots = PostingAccountSlots.ForModule(moduleKey);
        if (moduleSlots.Count == 0)
            return Results.Problem($"Unknown posting-accounts module '{moduleKey}'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        HashSet<string> validSlots = [.. moduleSlots.Select(s => s.SlotKey)];
        IReadOnlyDictionary<string, Guid> slots = request.Slots ?? new Dictionary<string, Guid>();
        if (slots.Keys.FirstOrDefault(k => !validSlots.Contains(k)) is { } bad)
            return Results.Problem($"Unknown slot '{bad}' for module '{moduleKey}'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        await store.SetModuleAsync(clientId, moduleKey, slots, ct);
        return Results.Ok(new PostingAccountsModuleResponse(moduleKey, slots));
    }
}
