using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace Accounting101.Inventory.Tests;

/// <summary>Boots the real composition-root host (engine + all modules incl. inventory) against a
/// disposable EphemeralMongo. Scaffold task — the inventory module does not post yet, so there is no
/// posting-accounts config and no loopback ledger client to repoint (add both back once the module posts).</summary>
public sealed class InventoryHostFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _connectionString = "";

    public IMongoClient Mongo { get; private set; } = null!;
    public string ControlDatabase { get; } = "control_" + Guid.NewGuid().ToString("N");
    public string PlatformDatabase { get; } = "platform_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        _connectionString = runner.ConnectionString;
        Mongo = new MongoClient(_connectionString);
    }

    async Task IAsyncLifetime.DisposeAsync() => await ((IAsyncDisposable)this).DisposeAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mongo:ConnectionString", _connectionString);
        builder.UseSetting("Mongo:ControlDatabase", ControlDatabase);
        builder.UseSetting("Mongo:PlatformDatabase", PlatformDatabase);
    }
}
