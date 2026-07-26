using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Contracts;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Deployment control-plane: provision clients and grant users roles. These operate on the control
/// database (above any single client), so they sit behind the deployment-admin policy — a trusted
/// token claim the IdP issues to firm administrators — rather than per-client membership.
/// </summary>
public static class AdminEndpoints
{
    public const string Policy = "DeploymentAdmin";

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Control-plane: deployment admin only — no per-client context exists to gate on.
        RouteGroupBuilder deployment = app.MapGroup("/admin").RequireAuthorization(Policy);
        deployment.MapPost("/clients", CreateClient);
        deployment.MapGet("/clients", ListClients);

        // Per-client admin: deployment admin OR the matching admin.* capability (checked in-handler).
        RouteGroupBuilder perClient = app.MapGroup("/admin").RequireAuthorization();
        perClient.MapPut("/clients/{clientId:guid}/fiscal-year-end", SetFiscalYearEnd);
        perClient.MapGet("/clients/{clientId:guid}/fiscal-year-end", GetFiscalYearEnd);
        perClient.MapPost("/clients/{clientId:guid}/members", AddMember);
        perClient.MapGet("/clients/{clientId:guid}/members", ListMembers);
        perClient.MapPut("/clients/{clientId:guid}/modules", SetClientModules);
        perClient.MapGet("/clients/{clientId:guid}/modules", GetClientModules);
    }

    private static async Task<IResult> CreateClient(
        CreateClientRequest request, ControlStore control, CancellationToken cancellationToken)
    {
        if (request.FiscalYearEndMonth is < 1 or > 12)
            return Results.Problem("FiscalYearEndMonth must be between 1 and 12.", statusCode: StatusCodes.Status400BadRequest);

        string baseCurrency = string.IsNullOrWhiteSpace(request.BaseCurrency)
            ? "USD"
            : request.BaseCurrency.Trim().ToUpperInvariant();
        if (!Iso4217Currencies.IsKnown(baseCurrency))
            return Results.Problem($"Unknown or ineligible currency '{request.BaseCurrency}'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        Guid id = Guid.NewGuid();
        string database = string.IsNullOrWhiteSpace(request.DatabaseName)
            ? "client_" + id.ToString("N")
            : request.DatabaseName;

        ApprovalMode mode = request.ApprovalMode == ApprovalMode.Unspecified
            ? ApprovalMode.TwoPerson
            : request.ApprovalMode;

        ClientRegistration registration = new()
        {
            Id = id,
            Name = request.Name,
            DatabaseName = database,
            ApprovalMode = mode,
            FiscalYearEndMonth = request.FiscalYearEndMonth,
            BaseCurrency = baseCurrency,
        };
        await control.RegisterClientAsync(registration, cancellationToken);

        return Results.Created(
            $"/admin/clients/{id}",
            new ClientRegistrationResponse(id, registration.Name, registration.DatabaseName,
                ApprovalPolicy.ModeOf(registration), FiscalYear.MonthOf(registration), registration.BaseCurrency));
    }

    private static async Task<IResult> GetFiscalYearEnd(
        Guid clientId, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminFiscal, actorFactory, control, cancellationToken))
            return Results.Forbid();

        ClientRegistration? registration = await control.GetClientAsync(clientId, cancellationToken);
        if (registration is null)
            return Results.NotFound();

        return Results.Ok(new FiscalYearEndResponse(FiscalYear.MonthOf(registration)));
    }

    private static async Task<IResult> SetFiscalYearEnd(
        Guid clientId, SetFiscalYearEndRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminFiscal, actorFactory, control, cancellationToken))
            return Results.Forbid();

        if (request.FiscalYearEndMonth is < 1 or > 12)
            return Results.Problem("FiscalYearEndMonth must be between 1 and 12.", statusCode: StatusCodes.Status400BadRequest);

        // Forward-only: already-closed years are immutable in the journal, so changing the scalar only
        // affects the validation of future closes. No effective-dated history is needed.
        ClientRegistration? registration = await control.GetClientAsync(clientId, cancellationToken);
        if (registration is null)
            return Results.NotFound();

        registration.FiscalYearEndMonth = request.FiscalYearEndMonth;
        await control.RegisterClientAsync(registration, cancellationToken);

        return Results.Ok(new ClientRegistrationResponse(registration.Id, registration.Name, registration.DatabaseName,
            ApprovalPolicy.ModeOf(registration), FiscalYear.MonthOf(registration), registration.BaseCurrency));
    }

    private static async Task<IResult> ListClients(ControlStore control, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClientRegistration> clients = await control.ListClientsAsync(cancellationToken);
        return Results.Ok(clients
            .Select(c => new ClientRegistrationResponse(c.Id, c.Name, c.DatabaseName,
                ApprovalPolicy.ModeOf(c), FiscalYear.MonthOf(c), c.BaseCurrency))
            .ToList());
    }

    private static async Task<IResult> AddMember(
        Guid clientId, AddMemberRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, AdminAuditStore audit, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminUsers, actorFactory, control, cancellationToken))
            return Results.Forbid();

        if (!Enum.TryParse(request.Role, ignoreCase: true, out LedgerRole role))
            return Results.Problem($"Unknown role '{request.Role}'.", statusCode: StatusCodes.Status422UnprocessableEntity);

        if (await GrantScope.FirstNotHeldByCallerAsync(user, clientId, RolePresets.For(role), actorFactory, control, cancellationToken) is { } badRole)
            return Results.Problem($"Cannot grant '{badRole}' — you do not hold it.", statusCode: StatusCodes.Status422UnprocessableEntity);

        if (await control.GetClientAsync(clientId, cancellationToken) is null)
            return Results.NotFound();

        await control.AddMembershipAsync(request.UserId, clientId, role, cancellationToken);
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow,
            ActorUserId = actorFactory.Create(user).UserId,
            ActorIsDeploymentAdmin = user.HasClaim("admin", "true"),
            Action = "MemberAdded", ClientId = clientId, TargetUserId = request.UserId,
            After = new AuditState { Capabilities = [.. RolePresets.For(role)] },
        }, cancellationToken);
        return Results.Ok(new MembershipResponse(
            request.UserId, clientId, [role.ToString()], [.. RolePresets.CapabilitiesFor([role])]));
    }

    private static async Task<IResult> ListMembers(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminUsers, actorFactory, control, cancellationToken))
            return Results.Forbid();

        IReadOnlyList<Membership> members = await control.GetMembersAsync(clientId, cancellationToken);
        return Results.Ok(members.Select(m => new MembershipResponse(
            m.UserId, m.ClientId, m.GrantedRoles.Select(r => r.ToString()).ToList(), m.Capabilities)).ToList());
    }

    private static async Task<IResult> GetClientModules(
        Guid clientId, ClaimsPrincipal user, IActorFactory actorFactory, ControlStore control,
        InstalledModules installed, CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminClient, actorFactory, control, cancellationToken))
            return Results.Forbid();

        ClientRegistration? client = await control.GetClientAsync(clientId, cancellationToken);
        if (client is null)
            return Results.NotFound();

        // Entitlement RAW, deliberately un-intersected: this is the one surface that lets support tell
        // "not sold" apart from "not installed". Sorted installed keys so the diff is easy to eyeball.
        return Results.Ok(new ClientModulesResponse(
            clientId,
            client.EnabledModules,
            SortedInstalledKeys(installed)));
    }

    // Ordering exists purely so a support reader can eyeball the difference between sold and
    // installed keys — it carries no meaning in the wire contract itself.
    private static string[] SortedInstalledKeys(InstalledModules installed) =>
        [.. installed.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    private static async Task<IResult> SetClientModules(
        Guid clientId, SetClientModulesRequest request, ClaimsPrincipal user,
        IActorFactory actorFactory, ControlStore control, InstalledModules installed,
        CancellationToken cancellationToken)
    {
        if (!await AdminAuthorization.MayAsync(user, clientId, Capabilities.AdminClient, actorFactory, control, cancellationToken))
            return Results.Forbid();

        ClientRegistration? existing = await control.GetClientAsync(clientId, cancellationToken);
        if (existing is null)
            return Results.NotFound();

        IReadOnlyList<string> keys = request.ModuleKeys ?? [];

        // Refuse to SELL what this deployment cannot RUN — but only for keys this request would newly
        // add. A key already present in the stored entitlement is carried forward regardless of this
        // host's installed set: SetClientModulesRequest is a documented full replace ("Idempotent",
        // AdminContracts.cs), so an unrelated edit (e.g. adding "payables") must not silently erase an
        // entitlement the client already bought just because a later deployment stopped composing its
        // module ("Entitlement is never erased" — a redeploy must never delete what a customer bought,
        // and re-installing must restore it). The guard's purpose is fully served by rejecting NEW
        // keys — extending it to already-stored keys would turn every unrelated edit into a silent
        // erasure. Removing an uninstalled entitlement stays possible by simply omitting it, which is
        // an explicit act rather than a side effect of this guard.
        //
        // Taken from DI (what the host actually composed) rather than the control DB's modules
        // collection: ModuleRegistrar seeds that only into the default firm, so it can be partial or
        // stale under multi-firm.
        //
        // All keys are checked before anything is written — a half-applied module set is worse than
        // either clean outcome.
        string[] absent = [.. keys.Where(k => !installed.Has(k) && !existing.EnabledModules.Contains(k))];
        if (absent.Length > 0)
            return Results.Problem(
                $"Module(s) not installed in this deployment: {string.Join(", ", absent)}.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        await control.SetClientModulesAsync(clientId, keys, cancellationToken);
        return Results.Ok(new ClientModulesResponse(clientId, keys, SortedInstalledKeys(installed)));
    }
}
