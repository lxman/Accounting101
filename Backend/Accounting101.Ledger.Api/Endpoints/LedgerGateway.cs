using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Shared front door for every ledger endpoint: turns the authenticated principal into an
/// <see cref="Actor"/>, authorizes it against the control DB (membership role must hold the required
/// <see cref="Permission"/>), and resolves the client's ledger — collapsing authn + authz + tenant
/// resolution into one call. Segregation of duties (an individual check) is layered on at the endpoint.
/// </summary>
public sealed class LedgerGateway(IActorFactory actorFactory, ControlStore control, ClientLedgerFactory ledgers, ModuleAccess moduleAccess)
{
    public async Task<LedgerContext> ResolveAsync(
        ClaimsPrincipal user, Guid clientId, Permission required, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);

        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null || !RolePermissions.Allows(membership.Role, required))
            return LedgerContext.Forbidden();

        ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
        return ledger is null ? LedgerContext.NotFound() : LedgerContext.Ok(actor, ledger);
    }

    /// <summary>
    /// Posting-specific resolution that supports two authorization paths decided by whether a module
    /// credential was established in the current request:
    /// <list type="bullet">
    ///   <item><b>Module-originated</b>: the request carries valid <c>X-Module-Key</c> + <c>X-Module-Secret</c>
    ///     headers → authorize the MODULE via <see cref="ModuleAccess"/> (registered, enabled, member) AND
    ///     confirm the user is a member of the client. The user's role need NOT hold
    ///     <see cref="Permission.Post"/>. <see cref="LedgerContext.ViaModule"/> is set to the module key.</item>
    ///   <item><b>Raw</b>: no module credential → unchanged from today — authorize the user's
    ///     <see cref="Permission.Post"/>. <see cref="LedgerContext.ViaModule"/> is null.</item>
    /// </list>
    /// <paramref name="moduleAuth"/> is passed in by the (scoped) endpoint rather than injected into
    /// the singleton gateway — this avoids capturing a scoped service inside a singleton.
    /// </summary>
    public async Task<LedgerContext> ResolveForPostAsync(
        ClaimsPrincipal user, Guid clientId, IModuleAuthenticator moduleAuth, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);

        ModuleIdentity? module = await moduleAuth.AuthenticateAsync();

        if (module is not null)
        {
            // Module-originated path: authorize the module (registered + enabled + user is member).
            // Passing caller.Key as targetNamespace satisfies the ownership check (caller.Key == targetNamespace).
            ModuleAccessDecision decision = await moduleAccess.AuthorizeAsync(
                module, module.Key, actor.UserId, clientId, cancellationToken);
            if (decision != ModuleAccessDecision.Allowed)
                return LedgerContext.Forbidden();

            ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
            return ledger is null ? LedgerContext.NotFound() : LedgerContext.Ok(actor, ledger, module.Key);
        }

        // Raw path: user must hold Post permission (unchanged from today).
        return await ResolveAsync(user, clientId, Permission.Post, cancellationToken);
    }
}

/// <summary>The resolved request context, or the HTTP error that prevented it (403/404).</summary>
public sealed class LedgerContext
{
    private LedgerContext(Actor? actor, ClientLedger? ledger, IResult? error, string? viaModule = null)
    {
        Actor = actor;
        Ledger = ledger;
        Error = error;
        ViaModule = viaModule;
    }

    public Actor? Actor { get; }
    public ClientLedger? Ledger { get; }
    public IResult? Error { get; }

    /// <summary>
    /// The module key that authorized this post, or null when the request came through the raw user path.
    /// Set only by <see cref="LedgerGateway.ResolveForPostAsync"/>; always null on contexts produced by
    /// <see cref="LedgerGateway.ResolveAsync"/>.
    /// </summary>
    public string? ViaModule { get; }

    [MemberNotNullWhen(true, nameof(Error))]
    [MemberNotNullWhen(false, nameof(Actor))]
    [MemberNotNullWhen(false, nameof(Ledger))]
    public bool Failed => Error is not null;

    public static LedgerContext Ok(Actor actor, ClientLedger ledger, string? viaModule = null) =>
        new(actor, ledger, null, viaModule);
    public static LedgerContext Forbidden() => new(null, null, Results.Forbid());
    public static LedgerContext NotFound() => new(null, null, Results.NotFound());
}
