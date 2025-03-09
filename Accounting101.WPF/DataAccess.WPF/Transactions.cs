using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess.WPF;

public static class Transactions
{
    public static async Task<Guid> CreateTransactionAsync(this IDataStore store, Transaction tx)
    {
        ILiteCollectionAsync<Account>? accts = store.GetCollection<Account>(CollectionNames.Account);
        if (accts is null)
        {
            return Guid.Empty;
        }

        Account credAcct = await accts.FindOneAsync(a => a.Id == tx.CreditedAccountId);
        Account debAcct = await accts.FindOneAsync(a => a.Id == tx.DebitedAccountId);
        Guid result = credAcct is null
                      || debAcct is null
                      || tx.When < credAcct.Created
                      || tx.When < debAcct.Created
            ? Guid.Empty
            : (await store.GetCollection<Transaction>(CollectionNames.Transaction)?.InsertAsync(tx)!).AsGuid;
        if (result != Guid.Empty) store.NotifyChange(typeof(Transaction), ChangeType.Created);
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
                Account credAcct = await accts.FindOneAsync(a => a.Id == t.CreditedAccountId);
                Account debAcct = await accts.FindOneAsync(a => a.Id == t.DebitedAccountId);
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
        if (result > 0) store.NotifyChange(typeof(Transactions), ChangeType.Created);
        return invalid;
    }

    public static async Task<List<Transaction>?> TransactionsForAccountAsync(this IDataStore store, Guid acct)
    {
        return (await store.GetCollection<Transaction>(CollectionNames.Transaction)
            ?.FindAsync(t => t.CreditedAccountId == acct || t.DebitedAccountId == acct)!).ToList();
    }

    public static async Task<List<Transaction>?> TransactionsForAccountByDateAsync(this IDataStore store, Guid acct, DateRange range)
    {
        return (await store.GetCollection<Transaction>(CollectionNames.Transaction)
                ?.FindAsync(t =>
                    (t.CreditedAccountId == acct || t.DebitedAccountId == acct)
                    && t.When >= range.Start
                    && t.When <= range.End)!)
            .ToList();
    }

    public static async Task<bool> UpdateTransactionAsync(this IDataStore store, Transaction tx)
    {
        ILiteCollectionAsync<Transaction>? collection = store.GetCollection<Transaction>(CollectionNames.Transaction);
        if (collection is null)
        {
            return false;
        }

        bool result = await collection.UpdateAsync(tx);
        if (result) store.NotifyChange(typeof(Transaction), ChangeType.Updated);
        return result;
    }

    public static async Task<bool> DeleteTransactionAsync(this IDataStore store, Guid id)
    {
        ILiteCollectionAsync<Transaction>? collection = store.GetCollection<Transaction>(CollectionNames.Transaction);
        if (collection is null)
        {
            return false;
        }
        bool result = await collection.DeleteAsync(id);
        if (result) store.NotifyChange(typeof(Transaction), ChangeType.Deleted);
        return result;
    }
}