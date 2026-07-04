using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class DefaultFirmSeederTests
{
    private static IConfiguration Config(string controlDb, string platformDb) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:ControlDatabase"] = controlDb,
            ["Mongo:PlatformDatabase"] = platformDb,
            ["Mongo:ClusterKey"] = "default",
        }).Build();

    [Fact]
    public async Task Seeds_the_default_firm_pointing_at_the_configured_control_db()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string platformDb = "platform_" + Guid.NewGuid().ToString("N");
        string controlDb = "control_" + Guid.NewGuid().ToString("N");
        PlatformStore platform = new(new MongoClient(runner.ConnectionString).GetDatabase(platformDb));

        DefaultFirmSeeder seeder = new(platform, Config(controlDb, platformDb));
        await seeder.StartAsync(CancellationToken.None);

        FirmRegistration firm = (await platform.GetFirmAsync(TenancyDefaults.DefaultFirmId))!;
        Assert.Equal(controlDb, firm.ControlDatabase);
        Assert.Equal("default", firm.ClusterKey);
        Assert.Equal(FirmStatus.Active, firm.Status);
    }

    [Fact]
    public async Task Is_idempotent_and_does_not_clobber_an_existing_default_firm()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string platformDb = "platform_" + Guid.NewGuid().ToString("N");
        PlatformStore platform = new(new MongoClient(runner.ConnectionString).GetDatabase(platformDb));

        // A pre-existing default firm with a hand-set control DB the seeder must not overwrite.
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = TenancyDefaults.DefaultFirmId, Name = "Default Firm",
            ControlDatabase = "hand_set_control", ClusterKey = "default",
        });

        await new DefaultFirmSeeder(platform, Config("some_other_control", platformDb))
            .StartAsync(CancellationToken.None);

        FirmRegistration firm = (await platform.GetFirmAsync(TenancyDefaults.DefaultFirmId))!;
        Assert.Equal("hand_set_control", firm.ControlDatabase);
    }
}
