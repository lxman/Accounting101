using System;
using System.Collections.Generic;
using System.Linq;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using LiteDB;

namespace DataAccess
{
    public static class Accounts
    {
        public static Guid CreateAccount(this IDataStore store, Account acct, AccountInfo info)
        {
            Guid infoId = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo)?.Insert(info).AsGuid ?? Guid.Empty;
            if (infoId == Guid.Empty)
            {
                return Guid.Empty;
            }

            acct.InfoId = infoId;
            Guid result = store.GetCollection<Account>(CollectionNames.Account)?.Insert(acct).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Accounts));
            return result;
        }

        public static Guid CreateAccount(this IDataStore store, AccountWithInfo acct)
        {
            Guid infoId = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo)?.Insert(acct.Info).AsGuid ?? Guid.Empty;
            if (infoId == Guid.Empty)
            {
                return Guid.Empty;
            }

            acct.Info.Id = infoId;
            Guid result = store.GetCollection<Account>(CollectionNames.Account)?.Insert(new Account(acct)).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Accounts));
            return result;
        }

        public static void BulkInsertAccounts(this IDataStore store, List<AccountWithInfo> accts)
        {
            ILiteCollection<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo);
            accts.ForEach(a =>
            {
                a.Info.Id = infos?.Insert(a.Info).AsGuid ?? Guid.Empty;
            });
            int? result = store.GetCollection<Account>(CollectionNames.Account)?.InsertBulk(accts.Select(a => new Account(a)));
            if (result > 0) store.NotifyChanged(typeof(Accounts));
        }

        public static AccountWithInfo? FindAccountByName(this IDataStore store, string name)
        {
            ILiteCollection<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo);
            AccountInfo? info = infos?.FindOne(i => i.Name == name) ?? null;
            if (info is null)
            {
                return null;
            }

            ILiteCollection<Account>? accts = store.GetCollection<Account>(CollectionNames.Account);
            Account? a = accts?.FindOne(a => a.InfoId == info.Id);
            if (a is null)
            {
                return null;
            }

            return new AccountWithInfo(a, info);
        }

        public static IEnumerable<AccountWithInfo>? AccountsForClient(this IDataStore store, Guid id)
        {
            IEnumerable<Account>? accts = store.GetCollection<Account>(CollectionNames.Account)
                ?.Find(a => a.ClientId == id);
            if (accts is null) return null;
            ILiteCollection<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfo);
            if (infos is null) return null;
            List<AccountWithInfo> acctsWInfos = [];
            accts.ToList().ForEach(a =>
            {
                acctsWInfos.Add(new AccountWithInfo(a, infos.FindOne(i => i.Id == a.InfoId)));
            });
            return acctsWInfos;
        }
    }
}