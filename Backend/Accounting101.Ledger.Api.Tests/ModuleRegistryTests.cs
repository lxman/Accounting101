using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The control DB carries a module registry beside clients and memberships: registering a module is
/// an idempotent upsert keyed by the module key, and the registry can be read back individually and
/// as a list.
/// </summary>
public sealed class ModuleRegistryTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task A_module_is_registered_read_back_and_listed()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "invoicing", Name = "Invoicing", Enabled = true });

        ModuleRegistration? read = await control.GetModuleAsync("invoicing");
        Assert.NotNull(read);
        Assert.Equal("Invoicing", read!.Name);
        Assert.True(read.Enabled);

        IReadOnlyList<ModuleRegistration> all = await control.ListModulesAsync();
        Assert.Contains(all, m => m.Key == "invoicing");
    }

    [Fact]
    public async Task Re_registering_the_same_key_upserts_rather_than_duplicating()
    {
        ControlStore control = fixture.Control();
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "expenses", Name = "Expenses", Enabled = true });
        await control.RegisterModuleAsync(new ModuleRegistration { Key = "expenses", Name = "Expenses", Enabled = false });

        IReadOnlyList<ModuleRegistration> all = await control.ListModulesAsync();
        Assert.Single(all, m => m.Key == "expenses");
        Assert.False((await control.GetModuleAsync("expenses"))!.Enabled);
    }

    [Fact]
    public async Task An_unregistered_module_reads_back_null()
    {
        Assert.Null(await fixture.Control().GetModuleAsync("does-not-exist"));
    }
}
