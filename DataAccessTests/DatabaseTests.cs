using System;
using System.IO;
using Autofac;
using DataAccess;
using DataAccess.Interfaces;
using DataAccess.Models;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using FluentAssertions;
using Xunit;

namespace DataAccessTests
{
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
        public void AccountTests()
        {
            using (ILifetimeScope scope = _container.BeginLifetimeScope())
            {
                Account acct = new();
                AccountInfo info = new()
                {
                    Name = "Test"
                };
                acct.StartBalance = 0;
                Guid id = scope.Resolve<IDataStore>().CreateAccount(acct, info);
                _ = id.Should().NotBeEmpty();
                bool result = File.Exists(_dbFile);
                _ = result.Should().BeTrue();
                scope.Resolve<IDataStore>().Dispose();
            }
            File.Delete(_dbFile);
        }

        [Fact]
        public void TransactionTests()
        {
            using (ILifetimeScope scope = _container.BeginLifetimeScope())
            {
                Account acctCredit = new();
                Account acctDebit = new();
                AccountInfo infoCredit = new() { Name = "Credit Account" };
                AccountInfo infoDebit = new() { Name = "Debit Account" };
                IDataStore store = scope.Resolve<IDataStore>();
                Guid idCredit = store.CreateAccount(acctCredit, infoCredit);
                _ = idCredit.Should().NotBeEmpty();
                Guid idDebit = store.CreateAccount(acctDebit, infoDebit);
                _ = idDebit.Should().NotBeEmpty();
                bool result = File.Exists(_dbFile);
                _ = result.Should().BeTrue();
                Transaction tx = new(idCredit, idDebit, 0, DateTime.Now);
                Guid txId = store.CreateTransaction(tx);
                _ = txId.Should().NotBeEmpty();
                store.Dispose();
            }
            File.Delete(_dbFile);
        }

        [Fact]
        public void ClientTests()
        {
            using (ILifetimeScope scope = _container.BeginLifetimeScope())
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
                Guid nameId = store.CreateName(name);
                _ = nameId.Should().NotBeEmpty();
                Guid addressId = store.CreateAddress(address);
                _ = addressId.Should().NotBeEmpty();
                bool result = File.Exists(_dbFile);
                _ = result.Should().BeTrue();
                c.PersonNameId = nameId;
                c.AddressId = addressId;
                Guid clientId = store.CreateClient(c);
                _ = clientId.Should().NotBeEmpty();
                store.Dispose();
            }
            File.Delete(_dbFile);
        }
    }
}