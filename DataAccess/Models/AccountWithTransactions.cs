using System.Collections.Generic;
using System.Linq;
using DataAccess.Services.Interfaces;
using LiteDB;

namespace DataAccess.Models
{
    public class AccountWithTransactions : Account
    {
        public List<Transaction> Transactions { get; }

        private readonly IDataStore _dataStore;

        public AccountWithTransactions(IDataStore dataStore)
        {
            _dataStore = dataStore;
            ILiteCollection<Transaction>? txDb = _dataStore.GetCollection<Transaction>(CollectionNames.Transactions);
            if (txDb is null) return;
            Transactions = txDb.FindAll().Where(tx => tx.DebitAccount == Id || tx.CreditAccount == Id).ToList();
        }
    }
}
