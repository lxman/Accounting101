using Accounting101.Ledger.Api.Control;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class MembershipStoreTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Granting_a_role_stores_its_preset_capabilities_and_granted_role()
    {
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        ControlStore control = fixture.Control();
        await control.AddMembershipAsync(user, client, LedgerRole.Controller);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.Equal([LedgerRole.Controller], m.GrantedRoles);
        Assert.True(RolePresets.For(LedgerRole.Controller).SetEquals(m.Capabilities));
    }

    [Fact]
    public async Task Granting_multiple_roles_unions_their_capabilities()
    {
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        ControlStore control = fixture.Control();
        await control.AddMembershipRolesAsync(user, client, [LedgerRole.ArClerk, LedgerRole.ApClerk]);

        Membership m = (await control.GetMembershipAsync(user, client))!;
        Assert.Contains(Capabilities.ArWrite, m.Capabilities);
        Assert.Contains(Capabilities.ApWrite, m.Capabilities);
    }

    [Fact]
    public async Task A_legacy_role_only_document_is_backfilled_on_read()
    {
        // Simulate a pre-migration doc that has only the old "Role" field.
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        IMongoCollection<Membership> raw = fixture.Mongo.GetDatabase(fixture.ControlDatabase).GetCollection<Membership>("memberships");
        await raw.InsertOneAsync(new Membership { Id = Guid.NewGuid(), UserId = user, ClientId = client, LegacyRole = LedgerRole.Approver });

        Membership m = (await fixture.Control().GetMembershipAsync(user, client))!;
        Assert.Equal([LedgerRole.Approver], m.GrantedRoles);
        Assert.True(RolePresets.For(LedgerRole.Approver).SetEquals(m.Capabilities));
    }

    [Fact]
    public async Task SetMembership_creates_then_replaces_roles_and_capabilities()
    {
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        ControlStore control = fixture.Control();

        await control.SetMembershipAsync(user, client, [LedgerRole.Auditor], ["gl.read", "ar.read"]);
        Membership created = (await control.GetMembershipAsync(user, client))!;
        Assert.Equal([LedgerRole.Auditor], created.GrantedRoles);
        Assert.True(new HashSet<string> { "gl.read", "ar.read" }.SetEquals(created.Capabilities));

        await control.SetMembershipAsync(user, client, [LedgerRole.Controller], ["gl.read", "gl.post"]);
        Membership replaced = (await control.GetMembershipAsync(user, client))!;
        Assert.Equal([LedgerRole.Controller], replaced.GrantedRoles);
        Assert.True(new HashSet<string> { "gl.read", "gl.post" }.SetEquals(replaced.Capabilities));
    }

    [Fact]
    public async Task RemoveMembership_deletes_the_member()
    {
        Guid user = Guid.NewGuid(), client = Guid.NewGuid();
        ControlStore control = fixture.Control();
        await control.SetMembershipAsync(user, client, [LedgerRole.Auditor], ["gl.read"]);
        await control.RemoveMembershipAsync(user, client);
        Assert.Null(await control.GetMembershipAsync(user, client));
    }
}
