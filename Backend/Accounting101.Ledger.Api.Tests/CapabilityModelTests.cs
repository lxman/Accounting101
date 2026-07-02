using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CapabilityModelTests
{
    [Fact]
    public void Gl_capabilities_round_trip_with_permissions()
    {
        foreach (Permission p in Enum.GetValues<Permission>())
        {
            string cap = Capabilities.CapabilityForPermission(p);
            Assert.StartsWith("gl.", cap);
            Assert.Equal(p, Capabilities.PermissionForCapability(cap));
        }
    }

    [Fact]
    public void Non_gl_capability_has_no_permission()
    {
        Assert.Null(Capabilities.PermissionForCapability("ar.write"));
    }

    [Theory]
    [InlineData(LedgerRole.Auditor, "Read")]
    [InlineData(LedgerRole.Clerk, "Read")]
    [InlineData(LedgerRole.Approver, "Read,Approve,Void,Reverse")]
    [InlineData(LedgerRole.Controller, "Read,Post,Revise,Approve,Void,Reverse,Close,ManageAccounts")]
    [InlineData(LedgerRole.Admin, "Read,Post,Revise,Approve,Void,Reverse,Close,ManageAccounts,Reopen")]
    public void Preset_gl_capabilities_match_the_legacy_role_permission_matrix(LedgerRole role, string expectedPermissions)
    {
        // The gl.* capabilities in a preset must map to exactly that role's RolePermissions set —
        // this is the invariant that keeps GL enforcement unchanged when LedgerGateway flips.
        HashSet<Permission> fromPreset = RolePresets.For(role)
            .Select(Capabilities.PermissionForCapability)
            .Where(p => p is not null).Select(p => p!.Value).ToHashSet();
        HashSet<Permission> expected = expectedPermissions.Split(',').Select(Enum.Parse<Permission>).ToHashSet();
        Assert.Equal(expected, fromPreset);
        Assert.True(expected.All(p => RolePermissions.Allows(role, p)));
        Assert.True(Enum.GetValues<Permission>().Where(p => !expected.Contains(p)).All(p => !RolePermissions.Allows(role, p)));
    }

    [Fact]
    public void Narrow_clerks_hold_only_gl_read_among_gl_capabilities()
    {
        foreach (LedgerRole role in new[] { LedgerRole.ArClerk, LedgerRole.ApClerk, LedgerRole.PayrollClerk, LedgerRole.CashClerk })
        {
            IEnumerable<string> gl = RolePresets.For(role).Where(c => c.StartsWith("gl."));
            Assert.Equal([Capabilities.GlRead], gl);
        }
        Assert.Contains(Capabilities.ArWrite, RolePresets.For(LedgerRole.ArClerk));
        Assert.DoesNotContain(Capabilities.ApWrite, RolePresets.For(LedgerRole.ArClerk));
    }

    [Fact]
    public void CapabilitiesFor_unions_presets()
    {
        HashSet<string> union = RolePresets.CapabilitiesFor([LedgerRole.ArClerk, LedgerRole.ApClerk]);
        Assert.Contains(Capabilities.ArWrite, union);
        Assert.Contains(Capabilities.ApWrite, union);
    }

    [Fact]
    public void Every_preset_capability_is_in_the_vocabulary()
    {
        foreach (LedgerRole role in Enum.GetValues<LedgerRole>())
            Assert.True(RolePresets.For(role).IsSubsetOf(Capabilities.All), $"{role} has an unknown capability");
    }
}
