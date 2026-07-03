using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MembershipResolutionTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static async Task<CapabilitySet> NewSetAsync(ControlStore control, params string[] caps)
    {
        CapabilitySet set = new()
        {
            Id = Guid.NewGuid(),
            Name = "Set " + Guid.NewGuid().ToString("N"),
            Capabilities = caps,
        };
        await control.CreateCapabilitySetAsync(set);
        return set;
    }

    [Fact]
    public async Task Resolved_capabilities_are_the_union_of_referenced_sets()
    {
        ControlStore control = fixture.Control();
        CapabilitySet a = await NewSetAsync(control, Capabilities.GlRead, Capabilities.ArWrite);
        CapabilitySet b = await NewSetAsync(control, Capabilities.ApWrite);

        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        await control.SetMembershipSetsAsync(user, client, [a.Id, b.Id]);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.ArWrite, Capabilities.ApWrite }
            .SetEquals(m.Capabilities));
    }

    [Fact]
    public async Task Editing_a_set_changes_an_assigned_members_capabilities_on_next_read()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = await NewSetAsync(control, Capabilities.GlRead);
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        await control.SetMembershipSetsAsync(user, client, [set.Id]);

        Membership before = (await control.GetMembershipAsync(user, client))!;
        Assert.DoesNotContain(Capabilities.GlPost, before.Capabilities);

        // Owner edits the set in place — no re-apply step.
        set.Capabilities = [Capabilities.GlRead, Capabilities.GlPost];
        await control.UpdateCapabilitySetAsync(set);

        Membership after = (await control.GetMembershipAsync(user, client))!;
        Assert.Contains(Capabilities.GlPost, after.Capabilities);
    }

    [Fact]
    public async Task Two_members_referencing_the_same_set_resolve_identically()
    {
        ControlStore control = fixture.Control();
        CapabilitySet set = await NewSetAsync(control, Capabilities.GlRead, Capabilities.CashWrite);
        Guid client = Guid.NewGuid();
        Guid u1 = Guid.NewGuid(), u2 = Guid.NewGuid();
        await control.SetMembershipSetsAsync(u1, client, [set.Id]);
        await control.SetMembershipSetsAsync(u2, client, [set.Id]);

        Membership m1 = (await control.GetMembershipAsync(u1, client))!;
        Membership m2 = (await control.GetMembershipAsync(u2, client))!;
        Assert.True(new HashSet<string>(m1.Capabilities).SetEquals(m2.Capabilities));
    }

    [Fact]
    public async Task A_role_grant_with_no_set_ids_resolves_via_the_builtin_set()
    {
        ControlStore control = fixture.Control();
        await control.SeedBuiltinCapabilitySetsAsync();   // built-in sets must exist to live-bind roles

        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        await control.AddMembershipRolesAsync(user, client, [LedgerRole.Controller]);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.True(RolePresets.For(LedgerRole.Controller).SetEquals(m.Capabilities));
    }

    [Fact]
    public async Task A_legacy_inline_only_grant_keeps_its_stored_capabilities()
    {
        ControlStore control = fixture.Control();
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        // A custom grant: roles empty, inline caps, no set ids (Slice D raw-cap shape).
        await control.SetMembershipAsync(user, client, [], [Capabilities.GlRead, Capabilities.AuditRead]);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.True(new HashSet<string> { Capabilities.GlRead, Capabilities.AuditRead }.SetEquals(m.Capabilities));
    }
}
