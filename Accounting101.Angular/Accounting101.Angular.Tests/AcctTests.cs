using Accounting101.Angular.DataAccess;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Autofac;
using Xunit.Abstractions;

namespace Accounting101.Angular.Tests;

[Collection("Mongo Database")]
public class AcctTests
{
    private readonly ITestOutputHelper _output;
    private readonly Random _random = new(DateTime.Now.Millisecond);
    private readonly IContainer _container;
    private const string DbName = "TestDb";

    public AcctTests(ITestOutputHelper output)
    {
        _output = output;
        ContainerBuilder builder = new();
        _ = builder.RegisterInstance<IDataStore>(new DataStore("mongodb://localhost:27017/", true));
        _container = builder.Build();
    }

    [Fact]
    public async Task AccountTests()
    {
        await using ILifetimeScope scope = _container.BeginLifetimeScope();
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
        await store.BulkInsertAccountsAsync(DbName, accounts);
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
        await store.BulkInsertTransactionsAsync(DbName, txs);
        ts = DateTime.Now - start;
        _output.WriteLine($"Inserting 100,000 transactions took {ts.TotalMilliseconds} ms.");
        await store.DropCollectionAsync<Transaction>(DbName, CollectionNames.Transaction);
        await store.DropCollectionAsync<AccountInfo>(DbName, CollectionNames.AccountInfo);
        await store.DropCollectionAsync<Account>(DbName, CollectionNames.Account);
        store.Dispose();
    }
}