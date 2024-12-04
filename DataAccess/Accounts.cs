using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess
{
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
            if (result != Guid.Empty) store.NotifyChanged(typeof(Accounts));
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
            if (result != Guid.Empty) store.NotifyChanged(typeof(Accounts));
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
            if (result > 0) store.NotifyChanged(typeof(Accounts));
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

        public static async Task<IEnumerable<AccountWithInfo>?> AccountsForClientAsync(this IDataStore store, Guid id)
        {
            IEnumerable<Account>? accts = await store.GetCollection<Account>(CollectionNames.Account)
                ?.FindAsync(a => a.ClientId == id)!;
            if (accts is null) return null;
            ILiteCollectionAsync<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo);
            if (infos is null) return null;
            List<AccountWithInfo> acctsWInfos = [];
            accts.ToList().ForEach((a) =>
            {
                acctsWInfos.Add(new AccountWithInfo(a, infos.FindOneAsync(i => i.Id == a.InfoId).GetAwaiter().GetResult()));
            });
            return acctsWInfos;
        }

        public static async Task<decimal> GetAccountBalanceAsync(this IDataStore store, Guid id)
        {
            Account? acct = await store.GetCollection<Account>(CollectionNames.Account)?.FindByIdAsync(id)!;
            if (acct is null) return 0;
            List<Transaction>? transactions = await store.TransactionsForAccountAsync(acct.Id);
            if (transactions is null) return 0;
            decimal balance = 0;
            bool isDebit = acct.IsDebitAccount;
            transactions.ForEach((t) =>
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

        public static async Task<AccountWithInfo?> GetAccountWithInfoAsync(this IDataStore store, Guid id)
        {
            Account? acct = await store.GetCollection<Account>(CollectionNames.Account)?.FindByIdAsync(id)!;
            if (acct is null) return null;
            AccountInfo? info = await store.GetCollection<AccountInfo>(CollectionNames.AccountInfo)?.FindByIdAsync(acct.InfoId)!;
            return info is null
                ? null
                : new AccountWithInfo(acct, info);
        }
    }
}