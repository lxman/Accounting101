using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autofac;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services;
using DataAccess.WPF.Services.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace AccountTests.WPF;

public class AcctTests
{
    private readonly ITestOutputHelper _output;
    private readonly Random _random = new(DateTime.Now.Millisecond);
    private readonly IContainer _container;
    private readonly string _dbFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), $"XUnitTest_{Guid.NewGuid()}.db");

    public AcctTests(ITestOutputHelper output)
    {
        _output = output;
        ContainerBuilder builder = new();
        _ = builder.RegisterInstance<IDataStore>(new DataStore($"FileName={_dbFile};"));
        _container = builder.Build();
    }

    [Fact]
    public async Task AccountTests()
    {
        await using (ILifetimeScope scope = _container.BeginLifetimeScope())
        {
            IDataStore store = scope.Resolve<IDataStore>();
            DateTime start = DateTime.Now;
            List<AccountWithInfo> accounts = [];
            for (int x = 0; x < 500; x++)
            {
                Account acct = new();
                AccountInfo info = new() { Name = $"TestDebit{x}" };
                acct.Type = (BaseAccountTypes)_random.Next(0, 2);
                accounts.Add(new AccountWithInfo(acct, info));
            }
            for (int x = 0; x < 500; x++)
            {
                Account acct = new();
                AccountInfo info = new() { Name = $"TestCredit{x}" };
                acct.Type = (BaseAccountTypes)(_random.Next(0, 4) + 2);
                accounts.Add(new AccountWithInfo(acct, info));
            }
            await store.BulkInsertAccountsAsync(accounts);
            TimeSpan ts = DateTime.Now - start;
            _output.WriteLine($"Creation of initial 1000 accounts took {ts.TotalMilliseconds} ms.");
            start = DateTime.Now;
            //Assert.True(ts.TotalMilliseconds < 8000);
            DateOnly when = DateOnly.FromDateTime(DateTime.Now);
            List<Transaction> txs = [];
            for (int x = 0; x < 100000; x++)
            {
                int creditAcct = _random.Next(0, 500);
                int debitAcct = _random.Next(0, 500);
                while (debitAcct == creditAcct)
                {
                    debitAcct = _random.Next(0, 500);
                }

                when = when.AddDays(1);
                AccountWithInfo? credAcct = accounts.Find(a => a.Info.Name == $"TestCredit{creditAcct}");
                AccountWithInfo? debAcct = accounts.Find(a => a.Info.Name == $"TestDebit{debitAcct}");
                Transaction tx = new(credAcct?.Id ?? Guid.Empty, debAcct?.Id ?? Guid.Empty, _random.Next(-100, 100), when);
                txs.Add(tx);
                if (txs.Count % 1000 == 0)
                {
                    _output.WriteLine($"{txs.Count}");
                }
            }
            ts = DateTime.Now - start;
            _output.WriteLine($"Creating 100,000 transactions took {ts.TotalMilliseconds} ms.");
            start = DateTime.Now;
            await store.BulkInsertTransactionsAsync(txs);
            ts = DateTime.Now - start;
            _output.WriteLine($"Inserting 100,000 transactions took {ts.TotalMilliseconds} ms.");
            store.Dispose();
        }
        File.Delete(_dbFile);
    }
}