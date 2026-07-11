using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ApprovalCapabilityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public void Vocabulary_adds_approval_policy_and_drops_firm()
    {
        Assert.Contains("admin.approvalPolicy", Capabilities.All);
        Assert.DoesNotContain("admin.firm", Capabilities.All);
    }

    [Fact]
    public void Admin_preset_has_approval_policy_and_not_firm()
    {
        IReadOnlySet<string> admin = RolePresets.For(LedgerRole.Admin);
        Assert.Contains(Capabilities.AdminApprovalPolicy, admin);
        Assert.DoesNotContain("admin.firm", admin);
    }

    [Fact]
    public async Task Approval_policy_admin_narrow_set_is_seeded()
    {
        // Boot the host (mints a client) so seeding runs, then read the control DB's capability sets.
        await fixture.SeedClientAsync("SeedProbe");
        IReadOnlyList<CapabilitySet> sets = await fixture.Control().ListCapabilitySetsAsync();
        CapabilitySet? set = sets.FirstOrDefault(s => s.Name == "Approval Policy Admin");
        Assert.NotNull(set);
        Assert.Equal(new[] { Capabilities.AdminApprovalPolicy, Capabilities.GlRead }, set!.Capabilities);
    }
}
