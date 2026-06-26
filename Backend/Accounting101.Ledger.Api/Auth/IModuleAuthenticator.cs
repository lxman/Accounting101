namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// Establishes the identity of the calling module — the module-side twin of <see cref="IActorFactory"/>.
/// How the identity is established varies by deployment (host-stamped in-process; credential-verified
/// out-of-process), but the call site is identical: callers authorize against the returned
/// <see cref="ModuleIdentity"/> value, never against the transport. Returns null when the caller
/// cannot be established as a known module.
/// </summary>
public interface IModuleAuthenticator
{
    Task<ModuleIdentity?> AuthenticateAsync();
}
