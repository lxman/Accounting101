using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess
{
    public static class Transactions
    {
        public static async Task<Guid> CreateTransactionAsync(this IDataStore store, Transaction tx)
        {
            ILiteCollectionAsync<Account>? accts = store.GetCollection<Account>(CollectionNames.Account);
            if (accts is null)
            {
                return Guid.Empty;
            }

            Account credAcct = await accts.FindOneAsync(a => a.Id == tx.CreditAccountId);
            Account debAcct = await accts.FindOneAsync(a => a.Id == tx.DebitAccountIds);
            Guid result = credAcct is null
                || debAcct is null
                || tx.When < credAcct.Created
                || tx.When < debAcct.Created
                ? Guid.Empty
                : (await store.GetCollection<Transaction>(CollectionNames.Transaction)?.InsertAsync(tx)!).AsGuid;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Transactions));
            return result;
        }

        public static async Task<List<Transaction>> BulkInsertTransactionsAsync(this IDataStore store, List<Transaction> txs, bool verify = false)
        {
            ILiteCollectionAsync<Account>? accts = store.GetCollection<Account>(CollectionNames.Account);
            if (accts is null)
            {
                return txs;
            }

            List<Transaction> toInsert = [];
            List<Transaction> invalid = [];
            if (verify)
            {
                foreach (Transaction t in txs)
                {
                    Account credAcct = await accts.FindOneAsync(a => a.Id == t.CreditAccountId);
                    Account debAcct = await accts.FindOneAsync(a => a.Id == t.DebitAccountIds);
                    if (credAcct is null
                        || debAcct is null
                        || t.When < credAcct.Created
                        || t.When < debAcct.Created
                    )
                    {
                        invalid.Add(t);
                    }
                    else
                    {
                        toInsert.Add(t);
                    }
                }
            }
            else
            {
                toInsert.AddRange(txs);
            }
            int? result = await store.GetCollection<Transaction>(CollectionNames.Transaction)?.InsertBulkAsync(toInsert)!;
            if (result > 0) store.NotifyChanged(typeof(Transactions));
            return invalid;
        }

        public static async Task<List<Transaction>?> TransactionsForAccountAsync(this IDataStore store, Guid acct)
        {
            return (await store.GetCollection<Transaction>(CollectionNames.Transaction)
                ?.FindAsync(t => t.CreditAccountId == acct || t.DebitAccountIds == acct)!).ToList();
        }

        public static async Task<List<Transaction>?> TransactionsForAccountByDateAsync(this IDataStore store, Guid acct, DateTime start, DateTime end)
        {
            return (await store.GetCollection<Transaction>(CollectionNames.Transaction)
                ?.FindAsync(t => (t.CreditAccountId == acct || t.DebitAccountIds == acct) && t.When >= start && t.When <= end)!).ToList();
        }
    }
}