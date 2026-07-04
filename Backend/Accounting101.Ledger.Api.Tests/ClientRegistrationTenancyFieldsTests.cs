using Accounting101.Ledger.Api.Control;
using Accounting101.TestSupport;
using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Accounting101.Ledger.Api.Tests;

public sealed class ClientRegistrationTenancyFieldsTests
{
    private static async Task<IMongoDatabase> FreshDbAsync()
    {
        IMongoRunner runner = await SharedMongo.InstanceAsync();
        return new MongoClient(runner.ConnectionString)
            .GetDatabase("ctl_tenancy_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task New_tenancy_fields_round_trip()
    {
        ControlStore control = new(await FreshDbAsync());
        Guid id = Guid.NewGuid();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = id,
            Name = "Acme",
            DatabaseName = "client_x",
            Status = ClientStatus.Archived,
            EnabledModules = ["payroll", "payables"],
            CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ArchivedUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        ClientRegistration reg = (await control.GetClientAsync(id))!;
        Assert.Equal(ClientStatus.Archived, reg.Status);
        Assert.Equal(new[] { "payroll", "payables" }, reg.EnabledModules);
        Assert.Equal(2026, reg.CreatedUtc.Year);
        Assert.NotNull(reg.ArchivedUtc);
    }

    [Fact]
    public async Task Legacy_document_without_the_fields_defaults_to_Active_and_empty()
    {
        IMongoDatabase db = await FreshDbAsync();
        Guid id = Guid.NewGuid();

        // A pre-existing registration written before these fields existed: only the original members.
        await db.GetCollection<BsonDocument>("clients").InsertOneAsync(new BsonDocument
        {
            { "_id", new BsonBinaryData(id, GuidRepresentation.Standard) },
            { "Name", "Legacy Co" },
            { "DatabaseName", "client_legacy" },
            { "RequireSegregationOfDuties", false },
            { "FiscalYearEndMonth", 12 },
        });

        ControlStore control = new(db);
        ClientRegistration reg = (await control.GetClientAsync(id))!;
        Assert.Equal(ClientStatus.Active, reg.Status);
        Assert.Empty(reg.EnabledModules);
        Assert.Null(reg.ArchivedUtc);
    }
}
