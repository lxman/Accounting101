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
}
