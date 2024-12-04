using System;
using System.Collections.Generic;
using System.Linq;
using DataAccess.Services.Interfaces;
using LiteDB.Async;

#pragma warning disable CS8618, CS9264

namespace DataAccess.Models
{
    public class AccountWithTransactions : Account
    {
        public List<Transaction> Transactions { get; }

        public AccountWithTransactions(IDataStore dataStore, Guid id)
        {
            Id = id;
            ILiteCollectionAsync<Transaction>? txDb = dataStore.GetCollection<Transaction>(CollectionNames.Transaction);
            if (txDb is null) return;
            Transactions = txDb.FindAllAsync().GetAwaiter().GetResult().Where(tx => tx.DebitedAccountId == Id || tx.CreditedAccountId == Id).ToList();
        }
    }
}