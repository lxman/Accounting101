using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.AccountGroups;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Accounting101.Angular.DataAccess;

public static class Accounts
{
    public static async Task<Guid> CreateAccountAsync(this IDataStore store, string dbName, Account acct, AccountInfo info)
    {
        await store.CreateOneGlobalScopeAsync(dbName, info);
        if (info.Id == Guid.Empty)
        {
            return Guid.Empty;
        }

        acct.InfoId = info.Id.ToString();
        await store.CreateOneClientScopeAsync(dbName, acct);
        await AddOneToGroupAsync(store, dbName, acct.ClientId, acct);
        store.NotifyChange(typeof(Account), ChangeType.Created);
        return acct.Id;
    }

    public static async Task<Guid> CreateAccountAsync(this IDataStore store, string dbName, AccountWithInfo acct)
    {
        await store.CreateOneGlobalScopeAsync(dbName, acct.Info);
        if (acct.Id == Guid.Empty)
        {
            return Guid.Empty;
        }

        acct.Info.Id = acct.Id;
        acct.InfoId = acct.Id.ToString();
        await store.CreateOneClientScopeAsync(dbName, acct);
        await AddOneToGroupAsync(store, dbName, acct.ClientId, acct);
        store.NotifyChange(typeof(Account), ChangeType.Created);
        return acct.Id;
    }

    public static async Task BulkInsertAccountsAsync(this IDataStore store, string dbName, List<AccountWithInfo> accts)
    {
        IMongoCollection<AccountInfo> infos = store.GetCollection<AccountInfo>(dbName, CollectionNames.AccountInfo)!;
        foreach (AccountWithInfo awi in accts)
        {
            await infos.InsertOneAsync(awi.Info);
            awi.InfoId = awi.Info.Id.ToString();
        }
        await store.GetCollection<Account>(dbName, CollectionNames.Account)?.InsertManyAsync(accts.Select(a => new Account(a)))!;
        store.NotifyChange(typeof(Accounts), ChangeType.Created);
    }

    public static async Task<AccountWithEverything?> GetAccountWithEverythingAsync(this IDataStore store, string dbName, string clientId, string accountId)
    {
        AccountWithInfo? acct = await store.GetAccountWithInfoByIdAsync(dbName, clientId, accountId);
        if (acct is null) return null;

        // Changed: Collect the transactions from the async enumerable
        List<Transaction> transactions = [];
        await foreach (List<Transaction> batch in store.TransactionsForAccountAsync(dbName, acct.Id.ToString()))
        {
            transactions.AddRange(batch);
        }
        transactions = transactions.OrderBy(t => t.When).ToList();

        CheckPoint? checkPoint = await store.GetCheckpointAsync(dbName, acct.Id.ToString());
        return new AccountWithEverything(acct, transactions, checkPoint);
    }

    public static async Task<AccountWithInfo?> FindAccountByNameAsync(this IDataStore store, string dbName, string name)
    {
        IMongoCollection<AccountInfo>? infos = store.GetCollection<AccountInfo>(dbName, CollectionNames.AccountInfo);
        AccountInfo? info = await infos?.AsQueryable().FirstAsync(i => i.Name == name)! ?? null;
        if (info is null)
        {
            return null;
        }

        IMongoCollection<Account>? accts = store.GetCollection<Account>(dbName, CollectionNames.Account);
        Account? a = await accts?.AsQueryable().FirstAsync(a => a.InfoId == info.Id.ToString())!;
        return a is null
            ? null
            : new AccountWithInfo(a, info);
    }

    public static async Task<IEnumerable<AccountWithInfo>?> AccountsForClientAsync(this IDataStore store, string dbName, string clientId)
    {
        List<Account>? accts = await store.GetAllClientScopeAsync<Account>(dbName, clientId);
        if (accts is null) return null;
        List<AccountInfo>? infos = await store.GetAllGlobalScopeAsync<AccountInfo>(dbName);
        if (infos is null) return null;
        List<AccountWithInfo> acctsWInfos = [];
        accts.ToList().ForEach(a =>
        {
            acctsWInfos.Add(new AccountWithInfo(a, infos.First(i => i.Id == Guid.Parse((ReadOnlySpan<char>)a.InfoId))));
        });
        return acctsWInfos;
    }

    public static async Task<DateRange?> GetAccountTransactionsDateRangeAsync(this IDataStore store, string dbName, Guid accountId)
    {
        Account? acct = await store.GetCollection<Account>(dbName, CollectionNames.Account)?.AsQueryable().FirstOrDefaultAsync(a => a.Id == accountId)!;
        if (acct is null) return null;

        // Changed: Collect the transactions from the async enumerable
        List<Transaction> transactions = [];
        await foreach (List<Transaction> batch in store.TransactionsForAccountAsync(dbName, acct.Id.ToString()))
        {
            transactions.AddRange(batch);
        }

        if (transactions.Count == 0)
            return new DateRange(acct.Created, acct.Created);

        DateRange dateRange = new(acct.Created, transactions.Max(t => t.When));
        return dateRange;
    }

    public static async Task<decimal> GetAccountBalanceAsync(this IDataStore store, string dbName, Guid accountId)
    {
        Account? acct = await store.GetCollection<Account>(dbName, CollectionNames.Account)?.AsQueryable().FirstOrDefaultAsync(a => a.Id == accountId)!;
        if (acct is null) return 0;

        // Changed: Collect the transactions from the async enumerable
        List<Transaction> transactions = [];
        await foreach (List<Transaction> batch in store.TransactionsForAccountAsync(dbName, acct.Id.ToString()))
        {
            transactions.AddRange(batch);
        }

        decimal balance = 0;
        bool isDebit = acct.IsDebitAccount;
        transactions.ForEach(t =>
        {
            if (isDebit)
            {
                if (t.DebitedAccountId == acct.Id.ToString()) balance += t.Amount;
                else
                {
                    balance -= t.Amount;
                }
            }
            else
            {
                if (t.DebitedAccountId == acct.Id.ToString()) balance -= t.Amount;
                else
                {
                    balance += t.Amount;
                }
            }
        });
        return balance;
    }

    public static async Task<decimal> GetAccountBalanceOnDateAsync(this IDataStore store, string dbName, string clientId, string accountId, DateOnly date)
    {
        AccountWithInfo? acct = await store.GetAccountWithInfoByIdAsync(dbName, clientId, accountId);
        if (acct is null || acct.Created > date) return 0;

        // Changed: Collect the transactions from the async enumerable
        List<Transaction> transactions = [];
        await foreach (List<Transaction> batch in store.TransactionsForAccountAsync(dbName, acct.Id.ToString()))
        {
            transactions.AddRange(batch);
        }

        List<Transaction> inDate = transactions.Where(t => t.When <= date).ToList();
        return BalanceCalculator.Calculate(acct, inDate);
    }

    public static async Task<AccountWithInfo?> GetAccountWithInfoByIdAsync(this IDataStore store, string dbName, string clientId, string accountId)
    {
        Account? acct = (await store.GetAllClientScopeAsync<Account>(dbName, clientId))!.FirstOrDefault(acct => acct.Id == Guid.Parse((ReadOnlySpan<char>)accountId));
        if (acct is null) return null;
        AccountInfo? info = (await store.GetAllGlobalScopeAsync<AccountInfo>(dbName))!.FirstOrDefault(a => a.Id == Guid.Parse((ReadOnlySpan<char>)acct.InfoId));
        return info is null
            ? null
            : new AccountWithInfo(acct, info);
    }

    public static async Task<bool> UpdateAccountAsync(this IDataStore store, string dbName, AccountWithInfo acct)
    {
        bool? result = await store.UpdateOneGlobalScopeAsync(dbName, acct.Info);
        if (!result.HasValue || !result.Value) return false;
        result = await store.UpdateOneClientScopeAsync(dbName, acct);
        if (!result.HasValue || !result.Value) return false;
        store.NotifyChange(typeof(Account), ChangeType.Updated);
        return result.Value;
    }

    public static async Task<bool> DeleteAccountAsync(this IDataStore store, string dbName, string clientId, string accountId)
    {
        Account? acct = (await store.GetAllClientScopeAsync<Account>(dbName, clientId))!.FirstOrDefault(a => a.Id == Guid.Parse((ReadOnlySpan<char>)accountId));
        if (acct is null) return false;
        bool? result = await store.DeleteOneGlobalScopeAsync<AccountInfo>(dbName, Guid.Parse((ReadOnlySpan<char>)acct.InfoId));
        if (!result.HasValue || !result.Value) return false;
        result = await store.DeleteOneClientScopeAsync<Account>(dbName, clientId, Guid.Parse((ReadOnlySpan<char>)accountId));
        if (!result.HasValue || !result.Value) return false;

        // Changed: Collect the transactions from the async enumerable
        List<Transaction> transactions = [];
        await foreach (List<Transaction> batch in store.TransactionsForAccountAsync(dbName, accountId))
        {
            transactions.AddRange(batch);
        }

        foreach (Transaction t in transactions)
        {
            await store.DeleteTransactionAsync(dbName, t.Id);
        }
        store.NotifyChange(typeof(Account), ChangeType.Deleted);
        return result.Value;
    }

    private static async Task AddOneToGroupAsync(IDataStore dataStore, string dbName, string clientId, Account a)
    {
        RootGroup group = await dataStore.GetRootGroupAsync(dbName, clientId);
        switch (a.Type.ToString())
        {
            case "Asset":
                group.Assets.Items.Add(new AccountGroupListItem { Type = AccountGroupListItemType.Account, AccountId = a.Id.ToString() });
                break;
            case "Liability":
                group.Liabilities.Items.Add(new AccountGroupListItem { Type = AccountGroupListItemType.Account, AccountId = a.Id.ToString() });
                break;
            case "Equity":
                group.Equity.Items.Add(new AccountGroupListItem { Type = AccountGroupListItemType.Account, AccountId = a.Id.ToString() });
                break;
            case "Revenue":
                group.Revenue.Items.Add(new AccountGroupListItem { Type = AccountGroupListItemType.Account, AccountId = a.Id.ToString() });
                break;
            case "Expense":
                group.Expenses.Items.Add(new AccountGroupListItem { Type = AccountGroupListItemType.Account, AccountId = a.Id.ToString() });
                break;
        }

        await dataStore.SaveRootGroupAsync(dbName, clientId, group);
    }
}