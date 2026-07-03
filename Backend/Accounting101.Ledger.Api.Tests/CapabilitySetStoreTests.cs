using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CapabilitySetStoreTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Create_then_get_round_trips_the_set()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = new()
        {
            Id = Guid.NewGuid(),
            Name = "Warehouse Clerk " + Guid.NewGuid().ToString("N"),
            Description = "Receiving desk",
            Capabilities = [Capabilities.GlRead, Capabilities.ApWrite],
            Builtin = false,
        };
        await control.CreateCapabilitySetAsync(set);

        CapabilitySet fetched = (await control.GetCapabilitySetAsync(set.Id))!;
        Assert.Equal(set.Name, fetched.Name);
        Assert.Equal("Receiving desk", fetched.Description);
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.ApWrite }.SetEquals(fetched.Capabilities));
        Assert.False(fetched.Builtin);
    }

    [Fact]
    public async Task GetByName_is_case_insensitive()
    {
        ControlStore control = fixture.Control();
        string name = "Mixed Case " + Guid.NewGuid().ToString("N");
        await control.CreateCapabilitySetAsync(new CapabilitySet { Id = Guid.NewGuid(), Name = name, Capabilities = [] });

        Assert.NotNull(await control.GetCapabilitySetByNameAsync(name.ToUpperInvariant()));
    }

    [Fact]
    public async Task Update_replaces_capabilities_and_name()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = new() { Id = Guid.NewGuid(), Name = "Before " + Guid.NewGuid().ToString("N"), Capabilities = [Capabilities.GlRead] };
        await control.CreateCapabilitySetAsync(set);

        set.Name = "After " + Guid.NewGuid().ToString("N");
        set.Capabilities = [Capabilities.GlRead, Capabilities.GlPost];
        await control.UpdateCapabilitySetAsync(set);

        CapabilitySet fetched = (await control.GetCapabilitySetAsync(set.Id))!;
        Assert.StartsWith("After ", fetched.Name);
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.GlPost }.SetEquals(fetched.Capabilities));
    }

    [Fact]
    public async Task Delete_removes_the_set()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = new() { Id = Guid.NewGuid(), Name = "Doomed " + Guid.NewGuid().ToString("N"), Capabilities = [] };
        await control.CreateCapabilitySetAsync(set);
        await control.DeleteCapabilitySetAsync(set.Id);
        Assert.Null(await control.GetCapabilitySetAsync(set.Id));
    }

    [Fact]
    public async Task Seeding_creates_one_builtin_per_role()
    {
        ControlStore control = new(fixture.Mongo.GetDatabase("ctl_seed_" + Guid.NewGuid().ToString("N")));
        await control.SeedBuiltinCapabilitySetsAsync();

        IReadOnlyList<CapabilitySet> all = await control.ListCapabilitySetsAsync();
        foreach (LedgerRole role in Enum.GetValues<LedgerRole>())
        {
            CapabilitySet? set = all.FirstOrDefault(s => s.Name == role.ToString());
            Assert.NotNull(set);
            Assert.True(set!.Builtin);
            Assert.True(RolePresets.For(role).SetEquals(set.Capabilities));
        }
    }

    [Fact]
    public async Task Seeding_is_idempotent_and_never_overwrites_an_edited_builtin()
    {
        ControlStore control = new(fixture.Mongo.GetDatabase("ctl_seed_" + Guid.NewGuid().ToString("N")));
        await control.SeedBuiltinCapabilitySetsAsync();

        // Owner edits a built-in in place.
        CapabilitySet clerk = (await control.GetCapabilitySetByNameAsync("Clerk"))!;
        clerk.Capabilities = [Capabilities.GlRead];
        await control.UpdateCapabilitySetAsync(clerk);

        // Re-seed (e.g. next startup) must NOT restore the preset.
        await control.SeedBuiltinCapabilitySetsAsync();

        CapabilitySet after = (await control.GetCapabilitySetByNameAsync("Clerk"))!;
        Assert.Equal(clerk.Id, after.Id);
        Assert.True(new HashSet<string> { Capabilities.GlRead }.SetEquals(after.Capabilities));
    }
}
