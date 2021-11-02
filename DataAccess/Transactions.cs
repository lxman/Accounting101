using DataAccess.Models;
using DataAccess.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace DataAccess
{
    public static class Transactions
    {
        public static Guid Create(IDataStore store, Transaction tx)
        {
            ILiteCollection<Account>? accts = store.GetCollection<Account>(CollectionNames.Accounts);
            if (accts is null)
            {
                return Guid.Empty;
            }

            Account credAcct = accts.FindOne(a => a.Id == tx.CreditAccount);
            Account debAcct = accts.FindOne(a => a.Id == tx.DebitAccount);
            Guid result = credAcct is null
                || debAcct is null
                || tx.When < credAcct.Posted
                || tx.When < debAcct.Posted
                ? Guid.Empty
                : store.GetCollection<Transaction>(CollectionNames.Transactions)?.Insert(tx).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Transactions));
            return result;
        }

        public static List<Transaction> BulkInsert(IDataStore store, List<Transaction> txs, bool verify = false)
        {
            ILiteCollection<Account>? accts = store.GetCollection<Account>(CollectionNames.Accounts);
            if (accts is null)
            {
                return txs;
            }

            List<Transaction> toInsert = new();
            List<Transaction> invalid = new();
            if (verify)
            {
                txs.ForEach(t =>
                {
                    Account credAcct = accts.FindOne(a => a.Id == t.CreditAccount);
                    Account debAcct = accts.FindOne(a => a.Id == t.DebitAccount);
                    if (credAcct is null
                        || debAcct is null
                        || t.When < credAcct.Posted
                        || t.When < debAcct.Posted
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
            int? result = (store.GetCollection<Transaction>(CollectionNames.Transactions)?.InsertBulk(toInsert));
            if (result > 0) store.NotifyChanged(typeof(Transactions));
            return invalid;
        }

        public static List<Transaction>? ForAccount(IDataStore store, Guid acct)
        {
            return store.GetCollection<Transaction>(CollectionNames.Transactions)
                ?.Find(t => t.CreditAccount == acct || t.DebitAccount == acct).ToList();
        }
    }
}