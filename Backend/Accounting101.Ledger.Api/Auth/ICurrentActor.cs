using Accounting101.Ledger.Mongo;

namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Resolves the acting <see cref="Actor"/> for the current request. This is how the engine derives the
/// "who" itself — a module never supplies identity, so it cannot act as another user. Backed by the
/// authenticated request in production; faked in tests.
/// </summary>
public interface ICurrentActor
{
    Actor Get();
}
