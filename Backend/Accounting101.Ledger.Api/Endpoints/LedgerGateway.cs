using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Mongo;
using Microsoft.AspNetCore.Http;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Shared front door for every ledger endpoint: turns the authenticated principal into an
/// <see cref="Actor"/>, authorizes it against the control DB (the membership's capability set must
/// contain the capability corresponding to the required <see cref="Permission"/>), and resolves the
/// client's ledger — collapsing authn + authz + tenant resolution into one call. Segregation of
/// duties (an individual check) is layered on at the endpoint.
/// </summary>
public sealed class LedgerGateway(IActorFactory actorFactory, ControlStore control, ClientLedgerFactory ledgers, ModuleAccess moduleAccess)
{
    public async Task<LedgerContext> ResolveAsync(
        ClaimsPrincipal user, Guid clientId, Permission required, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);

        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null || !membership.Capabilities.Contains(Capabilities.CapabilityForPermission(required)))
            return LedgerContext.Forbidden();

        ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
        return ledger is null ? LedgerContext.NotFound() : LedgerContext.Ok(actor, ledger);
    }

    /// <summary>Authorize a member on a client by a specific capability string (not a GL
    /// <see cref="Permission"/>) — for areas whose capability has no <see cref="Permission"/> mapping
    /// (e.g. <see cref="Capabilities.AuditRead"/>). Mirrors <see cref="ResolveAsync"/> but checks the
    /// capability directly, leaving the Permission↔capability maps untouched.</summary>
    public async Task<LedgerContext> ResolveCapabilityAsync(
        ClaimsPrincipal user, Guid clientId, string capability, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);

        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null || !membership.Capabilities.Contains(capability))
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
                module, module.Key, actor.UserId, clientId, ModuleAccessLevel.Write, cancellationToken);
            if (decision != ModuleAccessDecision.Allowed)
                return LedgerContext.Forbidden();

            ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
            return ledger is null ? LedgerContext.NotFound() : LedgerContext.Ok(actor, ledger, module.Key);
        }

        // Raw path: user must hold Post permission (unchanged from today).
        return await ResolveAsync(user, clientId, Permission.Post, cancellationToken);
    }

    /// <summary>Resolve actor + ledger for a caller who is a MEMBER of the client, without gating on any
    /// specific permission — so an entry-mutation endpoint can load the target entry before deciding the
    /// authorization path. Mutation authz is applied afterward via <see cref="AuthorizeEntryMutationAsync"/>.</summary>
    public async Task<LedgerContext> ResolveMemberAsync(
        ClaimsPrincipal user, Guid clientId, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);
        Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
        if (membership is null) return LedgerContext.Forbidden();
        ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
        return ledger is null ? LedgerContext.NotFound() : LedgerContext.Ok(actor, ledger);
    }

    /// <summary>Authorize a void/reverse/revise of an already-loaded entry. Two paths, decided by whether the
    /// target entry is module-owned:
    /// <list type="bullet">
    ///   <item><b>Manual entry</b> (<paramref name="entryViaModule"/> null): unchanged — the caller's role must
    ///     hold <paramref name="rawPermission"/>.</item>
    ///   <item><b>Module-owned entry</b>: the request MUST carry the owning module's credential
    ///     (<c>X-Module-Key</c>/<c>X-Module-Secret</c> whose key equals <paramref name="entryViaModule"/>) and that
    ///     module must be authorized (registered + enabled, caller a member). A raw caller — no matching module
    ///     credential — is refused (409): the correction must go through the owning module. (Break-glass admin
    ///     override is a documented follow-up; module-owned entries are default-closed to raw mutation.)</item>
    /// </list>
    /// Returns null when the mutation is allowed, or an <see cref="IResult"/> error (403/409) when refused.</summary>
    public async Task<IResult?> AuthorizeEntryMutationAsync(
        ClaimsPrincipal user, Guid clientId, string? entryViaModule, Permission rawPermission,
        IModuleAuthenticator moduleAuth, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);

        if (entryViaModule is null)
        {
            Membership? membership = await control.GetMembershipAsync(actor.UserId, clientId, cancellationToken);
            if (membership is null || !membership.Capabilities.Contains(Capabilities.CapabilityForPermission(rawPermission)))
                return Results.Forbid();
            return null;
        }

        ModuleIdentity? module = await moduleAuth.AuthenticateAsync();
        if (module is null || !string.Equals(module.Key, entryViaModule, StringComparison.Ordinal))
            return Results.Problem(
                $"Entry belongs to module '{entryViaModule}'; void or reverse it through that module, not the raw journal.",
                statusCode: StatusCodes.Status409Conflict);

        ModuleAccessDecision decision = await moduleAccess.AuthorizeAsync(
            module, module.Key, actor.UserId, clientId, ModuleAccessLevel.Write, cancellationToken);
        return decision == ModuleAccessDecision.Allowed ? null : Results.Forbid();
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
