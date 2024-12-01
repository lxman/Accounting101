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

        private readonly IDataStore _dataStore;

        public AccountWithTransactions(IDataStore dataStore, Guid id)
        {
            _dataStore = dataStore;
            Id = id;
            ILiteCollectionAsync<Transaction>? txDb = _dataStore.GetCollection<Transaction>(CollectionNames.Transaction);
            if (txDb is null) return;
            Transactions = txDb.FindAllAsync().GetAwaiter().GetResult().Where(tx => tx.DebitAccountId == Id || tx.CreditAccountId == Id).ToList();
        }
    }
}