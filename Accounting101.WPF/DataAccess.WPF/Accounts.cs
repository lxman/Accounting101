using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess.WPF;

public static class Accounts
{
    public static async Task<Guid> CreateAccountAsync(this IDataStore store, Account acct, AccountInfo info)
    {
        Guid infoId = (await store.GetCollection<AccountInfo>(CollectionNames.AccountInfo)?.InsertAsync(info)!).AsGuid;
        if (infoId == Guid.Empty)
        {
            return Guid.Empty;
        }

        acct.InfoId = infoId;
        Guid result = (await store.GetCollection<Account>(CollectionNames.Account)?.InsertAsync(acct)!).AsGuid;
        if (result != Guid.Empty) store.NotifyChange(typeof(Account), ChangeType.Created);
        return result;
    }

    public static async Task<Guid> CreateAccountAsync(this IDataStore store, AccountWithInfo acct)
    {
        Guid infoId = (await store.GetCollection<AccountInfo>(CollectionNames.AccountInfo)?.InsertAsync(acct.Info)!).AsGuid;
        if (infoId == Guid.Empty)
        {
            return Guid.Empty;
        }

        acct.Info.Id = infoId;
        acct.InfoId = infoId;
        Guid result = (await store.GetCollection<Account>(CollectionNames.Account)?.InsertAsync(new Account(acct))!).AsGuid;
        if (result != Guid.Empty) store.NotifyChange(typeof(Account), ChangeType.Created);
        return result;
    }

    public static async Task BulkInsertAccountsAsync(this IDataStore store, List<AccountWithInfo> accts)
    {
        ILiteCollectionAsync<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo);
        accts.ForEach(a =>
        {
            a.Info.Id = infos?.InsertAsync(a.Info).GetAwaiter().GetResult().AsGuid ?? Guid.Empty;
        });
        int? result = await store.GetCollection<Account>(CollectionNames.Account)?.InsertBulkAsync(accts.Select(a => new Account(a)))!;
        if (result > 0) store.NotifyChange(typeof(Accounts), ChangeType.Created);
    }

    public static async Task<AccountWithEverything?> GetAccountWithEverythingAsync(this IDataStore store,
        Guid accountId)
    {
        AccountWithInfo? acct = await store.GetAccountWithInfoAsync(accountId);
        if (acct is null) return null;
        List<Transaction> transactions = (await store.TransactionsForAccountAsync(acct.Id))?.OrderBy(t => t.When).ToList() ?? [];
        CheckPoint? checkPoint = await store.GetCheckpointAsync(acct.Id);
        return new AccountWithEverything(acct, transactions, checkPoint);
    }

    public static async Task<AccountWithInfo?> FindAccountByNameAsync(this IDataStore store, string name)
    {
        ILiteCollectionAsync<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo);
        AccountInfo? info = await infos?.FindOneAsync(i => i.Name == name)! ?? null;
        if (info is null)
        {
            return null;
        }

        ILiteCollectionAsync<Account>? accts = store.GetCollection<Account>(CollectionNames.Account);
        Account? a = await accts?.FindOneAsync(a => a.InfoId == info.Id)!;
        return a is null
            ? null
            : new AccountWithInfo(a, info);
    }

    public static async Task<IEnumerable<AccountWithInfo>?> AccountsForClientAsync(this IDataStore store, Guid clientId)
    {
        IEnumerable<Account>? accts = await store.GetCollection<Account>(CollectionNames.Account)
            ?.FindAsync(a => a.ClientId == clientId)!;
        if (accts is null) return null;
        ILiteCollectionAsync<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo);
        if (infos is null) return null;
        List<AccountWithInfo> acctsWInfos = [];
        accts.ToList().ForEach(a =>
        {
            acctsWInfos.Add(new AccountWithInfo(a, infos.FindOneAsync(i => i.Id == a.InfoId).GetAwaiter().GetResult()));
        });
        return acctsWInfos;
    }

    public static async Task<DateRange?> GetAccountTransactionsDateRangeAsync(this IDataStore store, Guid accountId)
    {
        Account? acct = await store.GetCollection<Account>(CollectionNames.Account)?.FindByIdAsync(accountId)!;
        if (acct is null) return null;
        List<Transaction>? transactions = await store.TransactionsForAccountAsync(acct.Id);
        if (transactions is null) return null;
        DateRange dateRange = new(acct.Created, transactions.Max(t => t.When));
        return dateRange;
    }

    public static async Task<decimal> GetAccountBalanceAsync(this IDataStore store, Guid accountId)
    {
        Account? acct = await store.GetCollection<Account>(CollectionNames.Account)?.FindByIdAsync(accountId)!;
        if (acct is null) return 0;
        List<Transaction>? transactions = await store.TransactionsForAccountAsync(acct.Id);
        if (transactions is null) return 0;
        decimal balance = 0;
        bool isDebit = acct.IsDebitAccount;
        transactions.ForEach(t =>
        {
            if (isDebit)
            {
                if (t.DebitedAccountId == acct.Id) balance += t.Amount;
                else
                {
                    balance -= t.Amount;
                }
            }
            else
            {
                if (t.DebitedAccountId == acct.Id) balance -= t.Amount;
                else
                {
                    balance += t.Amount;
                }
            }
        });
        return balance;
    }

    public static async Task<decimal> GetAccountBalanceOnDateAsync(this IDataStore store, Guid accountId, DateOnly date)
    {
        AccountWithInfo? acct = await store.GetAccountWithInfoAsync(accountId);
        if (acct is null || acct.Created > date) return 0;
        List<Transaction>? transactions = await store.TransactionsForAccountAsync(acct.Id);
        if (transactions is null) return acct.StartBalance;
        List<Transaction> inDate = transactions.Where(t => t.When <= date).ToList();
        return BalanceCalculator.Calculate(acct, inDate);
    }

    public static async Task<AccountWithInfo?> GetAccountWithInfoAsync(this IDataStore store, Guid accountId)
    {
        Account? acct = await store.GetCollection<Account>(CollectionNames.Account)?.FindByIdAsync(accountId)!;
        if (acct is null) return null;
        AccountInfo? info = await store.GetCollection<AccountInfo>(CollectionNames.AccountInfo)?.FindByIdAsync(acct.InfoId)!;
        return info is null
            ? null
            : new AccountWithInfo(acct, info);
    }

    public static async Task<bool> UpdateAccountAsync(this IDataStore store, AccountWithInfo acct)
    {
        ILiteCollectionAsync<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo);
        if (infos is null) return false;
        bool result = await infos.UpdateAsync(acct.Info);
        if (!result) return false;
        ILiteCollectionAsync<Account>? accts = store.GetCollection<Account>(CollectionNames.Account);
        if (accts is null) return false;
        result = await accts.UpdateAsync(new Account(acct));
        if (!result) return false;
        store.NotifyChange(typeof(Account), ChangeType.Updated);
        return result;
    }

    public static async Task<bool> DeleteAccountAsync(this IDataStore store, Guid accountId)
    {
        ILiteCollectionAsync<Account>? accts = store.GetCollection<Account>(CollectionNames.Account);
        if (accts is null) return false;
        bool result = await accts.DeleteAsync(accountId);
        if (!result) return false;
        ILiteCollectionAsync<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo);
        if (infos is null) return false;
        result = await infos.DeleteAsync(accountId);
        if (!result) return false;
        List<Transaction>? transactions = await store.TransactionsForAccountAsync(accountId);
        if (transactions is null) return false;
        foreach (Transaction t in transactions)
        {
            await store.DeleteTransactionAsync(t.Id);
        }
        store.NotifyChange(typeof(Account), ChangeType.Deleted);
        return result;
    }
}