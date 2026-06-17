using System.Security.Claims;
using Accounting101.Ledger.Mongo;
using MongoClaim = Accounting101.Ledger.Mongo.Claim;

namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Default <see cref="IActorFactory"/>: the subject claim becomes <see cref="Actor.UserId"/>, the
/// name claim becomes <see cref="Actor.Name"/>, and every remaining claim is snapshotted verbatim
/// (the engine stores claims opaquely — it does not interpret them).
/// </summary>
public sealed class ClaimsActorFactory : IActorFactory
{
    public Actor Create(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out Guid userId))
            throw new InvalidOperationException("Authenticated principal has no usable subject id.");

        List<MongoClaim> claims = principal.Claims
            .Where(c => c.Type is not (ClaimTypes.NameIdentifier or ClaimTypes.Name))
            .Select(c => new MongoClaim(c.Type, c.Value))
            .ToList();

        return new Actor
        {
            UserId = userId,
            Name = principal.FindFirstValue(ClaimTypes.Name),
            Claims = claims,
        };
    }
}
