using System.Security.Claims;
using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Maps the authenticated <see cref="ClaimsPrincipal"/> to the engine's <see cref="Actor"/> — the
/// point-in-time principal snapshot the audit log records. This seam is stable across identity
/// providers: only the authentication scheme that produced the principal varies.
/// </summary>
public interface IActorFactory
{
    Actor Create(ClaimsPrincipal principal);
}
