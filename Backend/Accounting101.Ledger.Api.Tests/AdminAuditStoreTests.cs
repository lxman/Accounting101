using System.Reflection;
using Accounting101.Ledger.Api.Control;

namespace Accounting101.Ledger.Api.Tests;

public sealed class AdminAuditStoreTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Append_then_query_round_trips_newest_first_and_filters()
    {
        AdminAuditStore audit = fixture.Audit();
        Guid actor = Guid.NewGuid(), client = Guid.NewGuid(), target = Guid.NewGuid();

        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ActorUserId = actor, Action = "MemberAdded", ClientId = client, TargetUserId = target,
        });
        await audit.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(), Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            ActorUserId = actor, Action = "MemberRemoved", ClientId = client, TargetUserId = target,
        });

        IReadOnlyList<AdminAuditEntry> byActor = await audit.QueryAsync(new AdminAuditFilter(ActorUserId: actor));
        Assert.Equal(2, byActor.Count);
        Assert.Equal("MemberRemoved", byActor[0].Action);   // newest first
        Assert.Equal("MemberAdded", byActor[1].Action);

        IReadOnlyList<AdminAuditEntry> other = await audit.QueryAsync(new AdminAuditFilter(ActorUserId: Guid.NewGuid()));
        Assert.Empty(other);
    }

    [Fact]
    public void Store_exposes_no_mutation_of_existing_entries()
    {
        string[] methods = typeof(AdminAuditStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).ToArray();
        Assert.DoesNotContain(methods, n =>
            n.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Replace", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Remove", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("AppendAsync", methods);
        Assert.Contains("QueryAsync", methods);
    }
}
