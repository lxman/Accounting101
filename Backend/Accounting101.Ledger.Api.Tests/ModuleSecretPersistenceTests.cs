using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Api.Hosting;
using Accounting101.Ledger.Api.Platform;
using Accounting101.Ledger.Contracts;
using Accounting101.TestSupport;
using EphemeralMongo;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ModuleSecretPersistenceTests
{
    [Fact]
    public async Task Resolver_loads_the_same_secret_on_a_second_boot()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        IMongoClient mongo = new MongoClient(runner.ConnectionString);
        PlatformStore platform = new(mongo.GetDatabase("platform_" + Guid.NewGuid().ToString("N")));

        // Boot 1 — fresh in-process singletons, as a process has at startup.
        ModuleRegistration reg1 = new() { Key = "receivables", Name = "Receivables", Enabled = true };
        ModuleCredential cred1 = new("receivables");
        await new ModuleSecretResolver([reg1], [cred1], platform).StartAsync(CancellationToken.None);

        Assert.NotEmpty(reg1.Secret);
        Assert.Equal(reg1.Secret, cred1.Secret); // registration + credential agree
        string bootOne = reg1.Secret;

        // Boot 2 — brand-new singletons (a "restart"), SAME platform DB.
        ModuleRegistration reg2 = new() { Key = "receivables", Name = "Receivables", Enabled = true };
        ModuleCredential cred2 = new("receivables");
        await new ModuleSecretResolver([reg2], [cred2], platform).StartAsync(CancellationToken.None);

        Assert.Equal(bootOne, reg2.Secret);   // stable across the restart — loaded, not regenerated
        Assert.Equal(bootOne, cred2.Secret);
    }

    [Fact]
    public async Task A_firm_provisioned_before_a_restart_keeps_valid_module_secrets()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        string conn = runner.ConnectionString;
        IMongoClient mongo = new MongoClient(conn);
        string platformDb = "platform_" + Guid.NewGuid().ToString("N");
        string controlDb = "control_" + Guid.NewGuid().ToString("N");

        WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                b.UseSetting("Mongo:ConnectionString", conn)
                 .UseSetting("Mongo:ControlDatabase", controlDb)
                 .UseSetting("Mongo:PlatformDatabase", platformDb));

        // Host 1: boot (resolver persists secrets, ModuleRegistrar seeds the default firm), then provision Firm B.
        string firmBControlDb;
        await using (WebApplicationFactory<Program> host1 = Build())
        {
            HttpClient op = host1.CreateClient();
            op.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                DevTokenDefaults.Scheme,
                DevToken.Encode(new DevTokenPayload(Guid.NewGuid(), "Operator", [new DevClaim("platform", "true")])));

            HttpResponseMessage created = await op.PostAsJsonAsync("/platform/firms", new ProvisionFirmRequest { Name = "Firm B" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            firmBControlDb = (await created.Content.ReadFromJsonAsync<FirmResponse>())!.ControlDatabase;
        }

        // The secret persisted under host 1 (read without regenerating — generate() must not be used).
        PlatformStore platform = new(mongo.GetDatabase(platformDb));
        string persisted = await platform.GetOrCreateModuleSecretAsync("receivables", () => "MUST-NOT-BE-USED");

        // Host 2: a restart — fresh process, SAME DBs. Its resolver must load the persisted secret.
        await using (WebApplicationFactory<Program> host2 = Build())
            _ = host2.CreateClient(); // force host boot

        // Firm B (provisioned under host 1) still holds the secret host 2 now uses → its modules authenticate.
        ControlStore firmBControl = new(mongo.GetDatabase(firmBControlDb));
        ModuleRegistration? firmBReceivables = await firmBControl.GetModuleAsync("receivables");
        Assert.NotNull(firmBReceivables);
        Assert.Equal(persisted, firmBReceivables!.Secret);

        // Host 2 did not regenerate: the persisted secret is unchanged after the restart.
        string afterRestart = await platform.GetOrCreateModuleSecretAsync("receivables", () => "MUST-NOT-BE-USED-2");
        Assert.Equal(persisted, afterRestart);
    }
}
