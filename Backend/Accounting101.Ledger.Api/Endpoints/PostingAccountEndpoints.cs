using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>Per-client posting-account configuration: which chart account each enabled module posts to
/// per slot. Gated by <c>admin.postingAccounts</c> (a deployment admin overrides). Values are advisory
/// against the chart (readiness), never enforced here. Modules with a dynamic category map (receivables)
/// additionally expose GET/PUT {moduleKey}/revenue-categories.</summary>
public static class PostingAccountEndpoints
{
    public static void MapPostingAccountEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder g = app.MapGroup("/clients/{clientId:guid}/posting-accounts").RequireAuthorization();
        g.MapGet("", GetPostingAccounts);
        g.MapPut("/{moduleKey}", SetPostingAccounts);
        g.MapGet("/{moduleKey}/revenue-categories", GetRevenueCategories);
        g.MapPut("/{moduleKey}/revenue-categories", SetRevenueCategories);
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

    private static async Task<IResult> GetRevenueCategories(
        Guid clientId, string moduleKey, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        PostingAccountStore store, IConfiguration configuration, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminPostingAccounts, actorFactory, control, ct))
            return Results.Forbid();
        if (await control.GetClientAsync(clientId, ct) is null) return Results.NotFound();

        string? section = PostingAccountCategoryMaps.ConfigSectionFor(moduleKey);
        if (section is null)
            return Results.Problem($"Module '{moduleKey}' does not support a revenue-category map.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        PostingAccountsDoc? doc = await store.GetAsync(clientId, ct);
        if (doc is not null && doc.CategoryMaps.TryGetValue(moduleKey, out Dictionary<string, Guid>? stored))
            return Results.Ok(new RevenueCategoriesResponse(moduleKey, stored, "stored"));

        // Config fallback, strict like the module provider: absent section → empty; malformed id → loud.
        Dictionary<string, Guid> fromConfig = configuration.GetSection(section).GetChildren().ToDictionary(
            child => child.Key,
            child => Guid.TryParse(child.Value, out Guid id)
                ? id
                : throw new InvalidOperationException(
                    $"Revenue category '{child.Key}' has a malformed account id '{child.Value}'."));
        return Results.Ok(new RevenueCategoriesResponse(moduleKey, fromConfig, "config"));
    }

    private static async Task<IResult> SetRevenueCategories(
        Guid clientId, string moduleKey, SetRevenueCategoriesRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, PostingAccountStore store, CancellationToken ct)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminPostingAccounts, actorFactory, control, ct))
            return Results.Forbid();
        if (await control.GetClientAsync(clientId, ct) is null) return Results.NotFound();

        if (PostingAccountCategoryMaps.ConfigSectionFor(moduleKey) is null)
            return Results.Problem($"Module '{moduleKey}' does not support a revenue-category map.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        IReadOnlyDictionary<string, Guid> categories = request.Categories ?? new Dictionary<string, Guid>();
        // Names become BSON element names under CategoryMaps.<moduleKey>; dot/dollar keys are unsafe there.
        if (categories.Keys.FirstOrDefault(k =>
                string.IsNullOrWhiteSpace(k) || k.Contains('.') || k.StartsWith('$')) is { } bad)
            return Results.Problem(
                $"Invalid revenue category name '{bad}': names must be non-blank and must not contain '.' or start with '$'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        await store.SetCategoryMapAsync(clientId, moduleKey, categories, ct);
        return Results.Ok(new RevenueCategoriesResponse(moduleKey, categories, "stored"));
    }
}
