using System.Collections.Generic;
using System.Linq;
using DataAccess.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess.Models
{
    public class AccountWithTransactions : Account
    {
        public List<Transaction> Transactions { get; }

        private readonly IDataStore _dataStore;

        public AccountWithTransactions(IDataStore dataStore)
        {
            _dataStore = dataStore;
            ILiteCollectionAsync<Transaction>? txDb = _dataStore.GetCollection<Transaction>(CollectionNames.Transaction);
            if (txDb is null) return;
            Transactions = txDb.FindAllAsync().GetAwaiter().GetResult().Where(tx => tx.DebitAccountIds == Id || tx.CreditAccountId == Id).ToList();
        }
    }
}