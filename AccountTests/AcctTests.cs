using Autofac;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace AccountTests
{
    public class AcctTests
    {
        private readonly Random Random = new(DateTime.Now.Millisecond);
        private readonly IContainer Container;
        private const string DbFile = @"C:\temp\Accounts.db";

        public AcctTests()
        {
            ContainerBuilder? builder = new ContainerBuilder();
            _ = builder.RegisterType<DataStore>()
                .As<IDataStore>()
                .SingleInstance();
            Container = builder.Build();
        }

        [Fact]
        public void AccountTests()
        {
            using (ILifetimeScope scope = Container.BeginLifetimeScope())
            {
                IDataStore store = scope.Resolve<IDataStore>();
                DateTime start = DateTime.Now;
                List<AccountWInfo> accounts = new();
                for (int x = 0; x < 500; x++)
                {
                    Account acct = new();
                    AccountInfo info = new() { Name = $"Test{x}" };
                    acct.IsDebitAccount = Random.Next(0, 2) > 0;
                    accounts.Add(new AccountWInfo(acct, info));
                }
                store.BulkInsert(accounts);
                TimeSpan ts = DateTime.Now - start;
                Debug.WriteLine($"Creation of initial accounts took {ts.TotalMilliseconds} ms.");
                start = DateTime.Now;
                ts.Milliseconds.Should().BeLessThan(8000);
                DateTime when = DateTime.Now;
                List<Transaction> txs = new();
                for (int x = 0; x < 100000; x++)
                {
                    int creditAcct = Random.Next(0, 500);
                    int debitAcct = Random.Next(0, 500);
                    while (debitAcct == creditAcct)
                    {
                        debitAcct = Random.Next(0, 500);
                    }

                    when = when.AddMilliseconds(1);
                    AccountWInfo? credAcct = accounts.Find(a => a.Info.Name == $"Test{creditAcct}");
                    AccountWInfo? debAcct = accounts.Find(a => a.Info.Name == $"Tests{debitAcct}");
                    Transaction tx = new(credAcct?.Id ?? Guid.Empty, debAcct?.Id ?? Guid.Empty, Random.Next(-100, 100), when);
                    txs.Add(tx);
                    if (txs.Count % 1000 == 0)
                    {
                        Debug.WriteLine(txs.Count);
                    }
                }
                ts = DateTime.Now - start;
                Debug.WriteLine($"Creating 100,000 transactions took {ts.TotalMilliseconds} ms.");
                start = DateTime.Now;
                Transactions.BulkInsert(store, txs);
                ts = DateTime.Now - start;
                Debug.WriteLine($"Inserting 100,000 transactions took {ts.TotalMilliseconds} ms.");
                store.Dispose();
            }
            File.Delete(DbFile);
        }
    }
}