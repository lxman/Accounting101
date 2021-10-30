using DataAccess.Models;
using DataAccess.Services.Interfaces;
using LiteDB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DataAccess
{
    public static class Accounts
    {
        public static Guid Create(this IDataStore store, Account acct, AccountInfo info)
        {
            Guid infoId = store.GetCollection<AccountInfo>(CollectionNames.AccountInfos)?.Insert(info).AsGuid ?? Guid.Empty;
            if (infoId == Guid.Empty)
            {
                return Guid.Empty;
            }

            acct.Info = infoId;
            return store.GetCollection<Account>(CollectionNames.Accounts)?.Insert(acct).AsGuid ?? Guid.Empty;
        }

        public static Guid Create(this IDataStore store, AccountWInfo acct)
        {
            Guid infoId = store.GetCollection<AccountInfo>(CollectionNames.AccountInfos)?.Insert(acct.Info).AsGuid ?? Guid.Empty;
            if (infoId == Guid.Empty)
            {
                return Guid.Empty;
            }

            acct.Info.Id = infoId;
            return store.GetCollection<Account>(CollectionNames.Accounts)?.Insert(new Account(acct)).AsGuid ?? Guid.Empty;
        }

        public static void BulkInsert(this IDataStore store, List<AccountWInfo> accts)
        {
            ILiteCollection<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfos);
            accts.ForEach(a =>
            {
                a.Info.Id = infos?.Insert(a.Info).AsGuid ?? Guid.Empty;
            });
            _ = store.GetCollection<Account>(CollectionNames.Accounts)?.InsertBulk(accts.Select(a => new Account(a)));
        }

        public static AccountWInfo? FindByName(this IDataStore store, string name)
        {
            ILiteCollection<AccountInfo>? infos = store.GetCollection<AccountInfo>(CollectionNames.AccountInfos);
            AccountInfo? info = infos?.FindOne(i => i.Name == name) ?? null;
            if (info is null)
            {
                return null;
            }

            ILiteCollection<Account>? accts = store.GetCollection<Account>(CollectionNames.Accounts);
            Account? a = accts?.FindOne(a => a.Info == info.Id);
            if (a is null)
            {
                return null;
            }

            return new AccountWInfo(a, info);
        }
    }
}