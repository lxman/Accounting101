using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class PlatformStoreTests
{
    private static async Task<PlatformStore> FreshStoreAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        return new PlatformStore(new MongoClient(runner.ConnectionString)
            .GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Firm_round_trips_lists_and_status_updates()
    {
        PlatformStore platform = await FreshStoreAsync();
        Guid firmId = Guid.NewGuid();
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = firmId,
            Name = "Ledger Pros",
            ControlDatabase = "firm_x_control",
            ClusterKey = "default",
            CreatedUtc = new DateTime(2026, 7, 4, 0, 0, 0, DateTimeKind.Utc),
        });

        FirmRegistration firm = (await platform.GetFirmAsync(firmId))!;
        Assert.Equal("Ledger Pros", firm.Name);
        Assert.Equal("firm_x_control", firm.ControlDatabase);
        Assert.Equal(FirmStatus.Active, firm.Status);

        await platform.SetFirmStatusAsync(firmId, FirmStatus.Suspended);
        Assert.Equal(FirmStatus.Suspended, (await platform.GetFirmAsync(firmId))!.Status);

        Assert.Contains(await platform.ListFirmsAsync(), f => f.Id == firmId);
    }

    [Fact]
    public async Task Cluster_round_trips()
    {
        PlatformStore platform = await FreshStoreAsync();
        await platform.RegisterClusterAsync(new ClusterRegistration
        {
            Key = "cluster-2",
            ConnectionString = "mongodb://c2.example",
        });

        Assert.Equal("mongodb://c2.example", (await platform.GetClusterAsync("cluster-2"))!.ConnectionString);
        Assert.Contains(await platform.ListClustersAsync(), c => c.Key == "cluster-2");
        Assert.Null(await platform.GetClusterAsync("missing"));
    }
}
