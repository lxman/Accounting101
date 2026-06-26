using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Derives the <see cref="Actor"/> from the authenticated principal on the current request — the same
/// <see cref="IActorFactory"/> path the ledger endpoints use, read from ambient request state so the
/// module-facing document API needs no identity parameter.
/// </summary>
public sealed class HttpContextCurrentActor(IHttpContextAccessor accessor, IActorFactory actorFactory) : ICurrentActor
{
    public Actor Get()
    {
        HttpContext context = accessor.HttpContext
            ?? throw new InvalidOperationException("No active HTTP request; cannot derive the acting principal.");
        return actorFactory.Create(context.User);
    }
}
