using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// A module call is authorized only when the module is registered + enabled, owns the target
/// namespace, AND the acting user is a member of the client. The decision is made against the
/// ModuleIdentity value with no transport in play, which is what keeps an out-of-process path additive.
/// </summary>
public sealed class ModuleAccessTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<(ModuleAccess access, Guid userId, Guid clientId)> SeedAsync(bool enabled = true)
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = enabled });
        SeededClient client = await fixture.SeedClientAsync();
        return (new ModuleAccess(control), client.UserId, client.ClientId);
    }

    [Fact]
    public async Task Member_of_a_registered_enabled_owning_module_is_allowed()
    {
        (ModuleAccess access, Guid userId, Guid clientId) = await SeedAsync();
        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("invoicing"), "invoicing", userId, clientId);
        Assert.Equal(ModuleAccessDecision.Allowed, decision);
    }

    [Fact]
    public async Task A_non_member_user_is_denied()
    {
        (ModuleAccess access, _, Guid clientId) = await SeedAsync();
        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("invoicing"), "invoicing", Guid.NewGuid(), clientId);
        Assert.Equal(ModuleAccessDecision.NotMember, decision);
    }

    [Fact]
    public async Task A_module_reaching_outside_its_namespace_is_denied()
    {
        (ModuleAccess access, Guid userId, Guid clientId) = await SeedAsync();
        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("invoicing"), "payroll", userId, clientId);
        Assert.Equal(ModuleAccessDecision.NotOwner, decision);
    }

    [Fact]
    public async Task An_unregistered_module_is_denied()
    {
        SeededClient client = await fixture.SeedClientAsync();
        ModuleAccess access = new(fixture.Control());
        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("ghost"), "ghost", client.UserId, client.ClientId);
        Assert.Equal(ModuleAccessDecision.Unregistered, decision);
    }

    [Fact]
    public async Task A_disabled_module_is_denied()
    {
        (ModuleAccess access, Guid userId, Guid clientId) = await SeedAsync(enabled: false);
        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("invoicing"), "invoicing", userId, clientId);
        Assert.Equal(ModuleAccessDecision.Disabled, decision);
    }
}
