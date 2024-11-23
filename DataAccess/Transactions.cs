using System;
using System.Collections.Generic;
using System.Linq;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using LiteDB;

namespace DataAccess
{
    public static class Transactions
    {
        public static Guid CreateTransaction(this IDataStore store, Transaction tx)
        {
            ILiteCollection<Account>? accts = store.GetCollection<Account>(CollectionNames.Account);
            if (accts is null)
            {
                return Guid.Empty;
            }

            Account credAcct = accts.FindOne(a => a.Id == tx.CreditAccountId);
            Account debAcct = accts.FindOne(a => a.Id == tx.DebitAccountIds);
            Guid result = credAcct is null
                || debAcct is null
                || tx.When < credAcct.Created
                || tx.When < debAcct.Created
                ? Guid.Empty
                : store.GetCollection<Transaction>(CollectionNames.Transaction)?.Insert(tx).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Transactions));
            return result;
        }

        public static List<Transaction> BulkInsertTransactions(this IDataStore store, List<Transaction> txs, bool verify = false)
        {
            ILiteCollection<Account>? accts = store.GetCollection<Account>(CollectionNames.Account);
            if (accts is null)
            {
                return txs;
            }

            List<Transaction> toInsert = [];
            List<Transaction> invalid = [];
            if (verify)
            {
                txs.ForEach(t =>
                {
                    Account credAcct = accts.FindOne(a => a.Id == t.CreditAccountId);
                    Account debAcct = accts.FindOne(a => a.Id == t.DebitAccountIds);
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
                });
            }
            else
            {
                toInsert.AddRange(txs);
            }
            int? result = (store.GetCollection<Transaction>(CollectionNames.Transaction)?.InsertBulk(toInsert));
            if (result > 0) store.NotifyChanged(typeof(Transactions));
            return invalid;
        }

        public static List<Transaction>? TransactionsForAccount(this IDataStore store, Guid acct)
        {
            return store.GetCollection<Transaction>(CollectionNames.Transaction)
                ?.Find(t => t.CreditAccountId == acct || t.DebitAccountIds == acct).ToList();
        }

        public static List<Transaction>? TransactionsForAccountByDate(this IDataStore store, Guid acct, DateTime start, DateTime end)
        {
            return store.GetCollection<Transaction>(CollectionNames.Transaction)
                ?.Find(t => (t.CreditAccountId == acct || t.DebitAccountIds == acct) && t.When >= start && t.When <= end).ToList();
        }
    }
}