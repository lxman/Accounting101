using Accounting101.Ledger.Api.Platform;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Http;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class FirmResolutionMiddlewareTests
{
    private sealed class FixedFirm(Guid firmId) : IFirmContext { public Guid FirmId => firmId; }

    private static async Task<(PlatformStore Platform, IMongoClientFactory Factory, IMongoClient Home)> BackendAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient home = new MongoClient(runner.ConnectionString);
        PlatformStore platform = new(home.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));
        await platform.RegisterClusterAsync(new ClusterRegistration { Key = "default", ConnectionString = runner.ConnectionString });
        return (platform, new MongoClientFactory(home, "default", platform), home);
    }

    [Fact]
    public async Task Populates_firm_scope_for_an_active_firm()
    {
        (PlatformStore platform, IMongoClientFactory factory, _) = await BackendAsync();
        Guid firmId = Guid.NewGuid();
        string controlDb = "firm_" + firmId.ToString("N") + "_control";
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = firmId, Name = "Firm A", ControlDatabase = controlDb, ClusterKey = "default",
        });

        FirmScope scope = new();
        bool nextRan = false;
        FirmResolutionMiddleware middleware = new(_ => { nextRan = true; return Task.CompletedTask; });
        DefaultHttpContext ctx = new();

        await middleware.InvokeAsync(ctx, new FixedFirm(firmId), platform, factory, scope);

        Assert.True(nextRan);
        Assert.Equal(firmId, scope.RequireFirm().Id);
        Assert.Equal(controlDb, scope.RequireControlDatabase().DatabaseNamespace.DatabaseName);
    }

    [Fact]
    public async Task Rejects_an_unknown_firm_with_403_and_does_not_call_next()
    {
        (PlatformStore platform, IMongoClientFactory factory, _) = await BackendAsync();
        FirmScope scope = new();
        bool nextRan = false;
        FirmResolutionMiddleware middleware = new(_ => { nextRan = true; return Task.CompletedTask; });
        DefaultHttpContext ctx = new();
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx, new FixedFirm(Guid.NewGuid()), platform, factory, scope);

        Assert.False(nextRan);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Null(scope.Firm);
    }

    [Fact]
    public async Task Rejects_a_suspended_firm_with_403()
    {
        (PlatformStore platform, IMongoClientFactory factory, _) = await BackendAsync();
        Guid firmId = Guid.NewGuid();
        await platform.RegisterFirmAsync(new FirmRegistration
        {
            Id = firmId, Name = "Suspended", ControlDatabase = "x_control", ClusterKey = "default",
            Status = FirmStatus.Suspended,
        });

        FirmScope scope = new();
        bool nextRan = false;
        FirmResolutionMiddleware middleware = new(_ => { nextRan = true; return Task.CompletedTask; });
        DefaultHttpContext ctx = new();
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx, new FixedFirm(firmId), platform, factory, scope);

        Assert.False(nextRan);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }
}
