namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// In-process establishment: the host stamps the module's identity at wiring time and this returns
/// it verbatim. Trust is structural — there is no network boundary to forge across, exactly as the
/// host is already trusted to wire the engine's authentication. The out-of-process counterpart (a
/// credential-verifying authenticator) is added behind <see cref="IModuleAuthenticator"/> without
/// touching this type or any consumer.
/// </summary>
public sealed class HostStampedModuleAuthenticator(ModuleIdentity identity) : IModuleAuthenticator
{
    public Task<ModuleIdentity?> AuthenticateAsync() => Task.FromResult<ModuleIdentity?>(identity);
}
