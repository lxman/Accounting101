using System;
using System.IO;
using System.Threading.Tasks;
using Autofac;
using DataAccess.WPF;
using DataAccess.WPF.Interfaces;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services;
using DataAccess.WPF.Services.Interfaces;
using Xunit;

namespace DataAccessTests.WPF;

public class DatabaseTests
{
    private readonly IContainer _container;
    private readonly string _dbFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), $"XUnitTest_{Guid.NewGuid()}.db");

    public DatabaseTests()
    {
        ContainerBuilder builder = new();
        _ = builder.RegisterInstance<IDataStore>(new DataStore($"FileName={_dbFile};"));
        _container = builder.Build();
    }

    [Fact]
    public async Task AccountTests()
    {
        await using (ILifetimeScope scope = _container.BeginLifetimeScope())
        {
            Account acct = new();
            AccountInfo info = new()
            {
                Name = "Test"
            };
            acct.StartBalance = 0;
            Guid id = await scope.Resolve<IDataStore>().CreateAccountAsync(acct, info);
            Assert.NotEqual(id, Guid.Empty);
            bool result = File.Exists(_dbFile);
            Assert.True(result);
            scope.Resolve<IDataStore>().Dispose();
        }
        File.Delete(_dbFile);
    }

    [Fact]
    public async Task TransactionTests()
    {
        await using (ILifetimeScope scope = _container.BeginLifetimeScope())
        {
            Account acctCredit = new();
            Account acctDebit = new();
            AccountInfo infoCredit = new() { Name = "Credit Account" };
            AccountInfo infoDebit = new() { Name = "Debit Account" };
            IDataStore store = scope.Resolve<IDataStore>();
            Guid idCredit = await store.CreateAccountAsync(acctCredit, infoCredit);
            Assert.NotEqual(idCredit, Guid.Empty);
            Guid idDebit = await store.CreateAccountAsync(acctDebit, infoDebit);
            Assert.NotEqual(idDebit, Guid.Empty);
            bool result = File.Exists(_dbFile);
            Assert.True(result);
            Transaction tx = new(idCredit, idDebit, 0, DateOnly.FromDateTime(DateTime.Now));
            Guid txId = await store.CreateTransactionAsync(tx);
            Assert.NotEqual(txId, Guid.Empty);
            store.Dispose();
        }
        File.Delete(_dbFile);
    }

    [Fact]
    public async Task ClientTests()
    {
        await using (ILifetimeScope scope = _container.BeginLifetimeScope())
        {
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
            Guid nameId = await store.CreateNameAsync(name);
            Assert.NotEqual(nameId, Guid.Empty);
            Guid addressId = await store.CreateAddressAsync(address);
            Assert.NotEqual(addressId, Guid.Empty);
            bool result = File.Exists(_dbFile);
            Assert.True(result);
            c.PersonNameId = nameId;
            c.AddressId = addressId;
            Guid clientId = await store.CreateClientAsync(c);
            Assert.NotEqual(clientId, Guid.Empty);
            store.Dispose();
        }
        File.Delete(_dbFile);
    }
}