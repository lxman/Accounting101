using Accounting101.Ledger.Api.Auth;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// In-process, a module presents no credential: the host stamps its identity at wiring time and the
/// authenticator simply returns it. This is the module-side twin of the user auth handler — a future
/// out-of-process authenticator verifies a credential behind the same seam, changing only the impl.
/// </summary>
public sealed class ModuleAuthenticatorTests
{
    [Fact]
    public void Host_stamped_authenticator_returns_the_stamped_identity()
    {
        IModuleAuthenticator authenticator = new HostStampedModuleAuthenticator(new ModuleIdentity("invoicing"));
        ModuleIdentity? identity = authenticator.Authenticate();
        Assert.Equal(new ModuleIdentity("invoicing"), identity);
    }
}
