using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Tenancy;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>
/// Shared front door for every ledger endpoint: turns the authenticated principal into an
/// <see cref="Actor"/>, authorizes it against the control DB (membership), and resolves the
/// client's ledger — collapsing the auth + authz + tenant-resolution boilerplate into one call.
/// </summary>
public sealed class LedgerGateway(IActorFactory actorFactory, ControlStore control, ClientLedgerFactory ledgers)
{
    public async Task<LedgerContext> ResolveAsync(ClaimsPrincipal user, Guid clientId, CancellationToken cancellationToken)
    {
        Actor actor = actorFactory.Create(user);
        if (!await control.IsMemberAsync(actor.UserId, clientId, cancellationToken))
            return LedgerContext.Forbidden();

        ClientLedger? ledger = await ledgers.CreateAsync(clientId, cancellationToken);
        return ledger is null ? LedgerContext.NotFound() : LedgerContext.Ok(actor, ledger);
    }
}

/// <summary>The resolved request context, or the HTTP error that prevented it (403/404).</summary>
public sealed class LedgerContext
{
    private LedgerContext(Actor? actor, ClientLedger? ledger, IResult? error)
    {
        Actor = actor;
        Ledger = ledger;
        Error = error;
    }

    public Actor? Actor { get; }
    public ClientLedger? Ledger { get; }
    public IResult? Error { get; }

    [MemberNotNullWhen(true, nameof(Error))]
    [MemberNotNullWhen(false, nameof(Actor))]
    [MemberNotNullWhen(false, nameof(Ledger))]
    public bool Failed => Error is not null;

    public static LedgerContext Ok(Actor actor, ClientLedger ledger) => new(actor, ledger, null);
    public static LedgerContext Forbidden() => new(null, null, Results.Forbid());
    public static LedgerContext NotFound() => new(null, null, Results.NotFound());
}
