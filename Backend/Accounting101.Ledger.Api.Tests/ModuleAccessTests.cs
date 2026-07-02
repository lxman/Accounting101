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

    private async Task<(ModuleAccess access, Guid userId, Guid clientId)> SeedReceivablesAsync(LedgerRole role)
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "receivables", Name = "Receivables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: role);
        return (new ModuleAccess(control), client.UserId, client.ClientId);
    }

    [Fact]
    public async Task A_member_holding_ar_write_may_write_receivables()
    {
        (ModuleAccess access, Guid userId, Guid clientId) = await SeedReceivablesAsync(LedgerRole.Controller);
        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("receivables"), "receivables", userId, clientId, ModuleAccessLevel.Write);
        Assert.Equal(ModuleAccessDecision.Allowed, decision);
    }

    [Fact]
    public async Task A_member_without_ar_write_is_denied_a_receivables_write()
    {
        (ModuleAccess access, Guid userId, Guid clientId) = await SeedReceivablesAsync(LedgerRole.Auditor);
        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("receivables"), "receivables", userId, clientId, ModuleAccessLevel.Write);
        Assert.Equal(ModuleAccessDecision.MissingCapability, decision);
    }

    [Fact]
    public async Task An_auditor_may_read_receivables()
    {
        (ModuleAccess access, Guid userId, Guid clientId) = await SeedReceivablesAsync(LedgerRole.Auditor);
        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("receivables"), "receivables", userId, clientId, ModuleAccessLevel.Read);
        Assert.Equal(ModuleAccessDecision.Allowed, decision);
    }

    [Fact]
    public async Task A_wrong_module_clerk_cannot_write_another_module()
    {
        // ArClerk holds ar.write but NOT ap.write.
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "payables", Name = "Payables", Enabled = true });
        SeededClient client = await fixture.SeedClientAsync(role: LedgerRole.ArClerk);
        ModuleAccess access = new(control);
        ModuleAccessDecision decision = await access.AuthorizeAsync(
            new ModuleIdentity("payables"), "payables", client.UserId, client.ClientId, ModuleAccessLevel.Write);
        Assert.Equal(ModuleAccessDecision.MissingCapability, decision);
    }
}
