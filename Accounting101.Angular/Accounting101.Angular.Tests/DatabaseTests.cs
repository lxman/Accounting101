using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Interfaces;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Accounting101.Angular.DataAccess.ZipCodeData;
using Autofac;
using MongoDB.Driver;

namespace Accounting101.Angular.Tests;

[Collection("Mongo Database")]
public class DatabaseTests
{
    private readonly MongoFixture _fixture;
    private readonly IContainer _container;
    private const string DatabaseName = "XUnitTest";

    public DatabaseTests(MongoFixture fixture)
    {
        _fixture = fixture;
        ContainerBuilder builder = new();
        _ = builder.RegisterInstance<IDataStore>(new DataStore("mongodb://localhost:27017/", true));
        _container = builder.Build();
    }

    [Fact]
    public async Task AccountTests()
    {
        await using ILifetimeScope scope = _container.BeginLifetimeScope();
        Account acct = new();
        AccountInfo info = new()
        {
            Name = "Test"
        };
        acct.StartBalance = 0;
        Guid id = await scope.Resolve<IDataStore>().CreateAccountAsync(DatabaseName, acct, info);
        Assert.NotEqual(id, Guid.Empty);
        await scope.Resolve<IDataStore>().DropCollectionAsync<AccountInfo>(DatabaseName, CollectionNames.AccountInfo);
        await scope.Resolve<IDataStore>().DropCollectionAsync<Account>(DatabaseName, CollectionNames.Account);
        await CleanupAsync(scope);
        scope.Resolve<IDataStore>().Dispose();
    }

    [Fact]
    public async Task TransactionTests()
    {
        await using ILifetimeScope scope = _container.BeginLifetimeScope();
        Account acctCredit = new();
        Account acctDebit = new();
        AccountInfo infoCredit = new() { Name = "Credit Account" };
        AccountInfo infoDebit = new() { Name = "Debit Account" };
        IDataStore store = scope.Resolve<IDataStore>();
        Guid idCredit = await store.CreateAccountAsync(DatabaseName, acctCredit, infoCredit);
        Assert.NotEqual(idCredit, Guid.Empty);
        Guid idDebit = await store.CreateAccountAsync(DatabaseName, acctDebit, infoDebit);
        Assert.NotEqual(idDebit, Guid.Empty);
        Transaction tx = new(idCredit, idDebit, 0, DateOnly.FromDateTime(DateTime.Now));
        Guid txId = await store.CreateTransactionAsync(DatabaseName, tx);
        Assert.NotEqual(txId, Guid.Empty);
        await store.DropCollectionAsync<Transaction>(DatabaseName, CollectionNames.Transaction);
        await store.DropCollectionAsync<AccountInfo>(DatabaseName, CollectionNames.AccountInfo);
        await store.DropCollectionAsync<Account>(DatabaseName, CollectionNames.Account);
        await CleanupAsync(scope);
        store.Dispose();
    }

    [Fact]
    public async Task ClientTests()
    {
        await using ILifetimeScope scope = _container.BeginLifetimeScope();
        PersonName name = new()
        {
            First = "Michael",
            Middle = "Stuart",
            Last = "Jordan"
        };
        IAddress address = new UsAddress()
        {
            Line1 = "84 Pounding Mill Creek",
            Line2 = "Pounding Mill Creek Estates",
            City = "Hayesville",
            State = "NC",
            Zip = "28904"
        };
        Client c = new()
        {
            BusinessName = "JordanSoft"
        };
        IDataStore store = scope.Resolve<IDataStore>();
        Guid nameId = await store.CreateNameAsync(DatabaseName, name);
        Assert.NotEqual(nameId, Guid.Empty);
        Guid addressId = await store.CreateAddressAsync(DatabaseName, address);
        Assert.NotEqual(addressId, Guid.Empty);
        c.PersonNameId = nameId;
        c.AddressId = addressId;
        Guid clientId = await store.CreateClientAsync(DatabaseName, c);
        Assert.NotEqual(clientId, Guid.Empty);
        await store.DeleteOneAsync<Client>(DatabaseName, clientId);
        await store.DeleteOneAsync<PersonName>(DatabaseName, nameId);
        await store.DropCollectionAsync<Client>(DatabaseName, CollectionNames.Client);
        await store.DropCollectionAsync<PersonName>(DatabaseName, CollectionNames.PersonName);
        await store.Instance(DatabaseName)?.DropCollectionAsync(CollectionNames.Address);
        await CleanupAsync(scope);
        FilterDefinition<IAddress> filter = Builders<IAddress>.Filter.Eq(x => x.Id, addressId);
        await store.DeleteAddressAsync(DatabaseName, addressId);
        store.Dispose();
    }

    private static async Task CleanupAsync(ILifetimeScope scope)
    {
        await scope.Resolve<IDataStore>().DropCollectionAsync<ZipCodeEntry>("ZipInfo", "ZipInfo");
    }
}