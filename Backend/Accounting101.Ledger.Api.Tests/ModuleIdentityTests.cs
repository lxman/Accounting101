using Accounting101.Ledger.Api.Auth;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The module identity is a value: its key doubles as the imposed namespace prefix, and two
/// identities with the same key are equal (record value semantics) — so "who is calling" and
/// "what namespace they own" can never disagree.
/// </summary>
public sealed class ModuleIdentityTests
{
    [Fact]
    public void Key_doubles_as_the_namespace_prefix()
    {
        ModuleIdentity identity = new("invoicing");
        Assert.Equal("invoicing", identity.Key);
        Assert.Equal("invoicing_", identity.Prefix);
    }

    [Fact]
    public void Identities_with_the_same_key_are_equal()
    {
        Assert.Equal(new ModuleIdentity("invoicing"), new ModuleIdentity("invoicing"));
        Assert.NotEqual(new ModuleIdentity("invoicing"), new ModuleIdentity("payroll"));
    }
}
