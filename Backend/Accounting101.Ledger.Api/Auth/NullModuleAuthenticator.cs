namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Fallback <see cref="IModuleAuthenticator"/> registered by the engine when no module has been
/// installed via <c>AddModule</c>. Always returns null so <see cref="Endpoints.LedgerGateway"/>
/// falls through to the raw user-permission path, preserving backward-compatible behaviour for
/// hosts that do not install any modules.
/// </summary>
internal sealed class NullModuleAuthenticator : IModuleAuthenticator
{
    public Task<ModuleIdentity?> AuthenticateAsync() => Task.FromResult<ModuleIdentity?>(null);
}
