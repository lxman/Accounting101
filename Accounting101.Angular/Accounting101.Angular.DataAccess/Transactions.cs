using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.DataAccess;

public static class Transactions
{
    public static async Task<Guid> CreateTransactionAsync(this IDataStore store, string dbName, Transaction tx)
    {
        Account? credAcct = (await store.GetAllClientScopeAsync<Account>(dbName, tx.CreditedAccountId))?.FirstOrDefault();
        Account? debAcct = (await store.GetAllClientScopeAsync<Account>(dbName, tx.DebitedAccountId))?.FirstOrDefault();
        Guid result = credAcct is null
                      || debAcct is null
                      || tx.When < credAcct.Created
                      || tx.When < debAcct.Created
            ? Guid.Empty
            : await store.CreateOneGlobalScopeAsync(dbName, tx);
        if (result != Guid.Empty) store.NotifyChange(typeof(Transaction), ChangeType.Created);
        return result;
    }

    public static async Task<List<Transaction>> BulkInsertTransactionsAsync(this IDataStore store, string dbName, List<Transaction> txs, bool verify = false)
    {
        List<Transaction> toInsert = [];
        List<Transaction> invalid = [];
        if (verify)
        {
            foreach (Transaction t in txs)
            {
                Account? credAcct = (await store.GetAllClientScopeAsync<Account>(dbName, t.CreditedAccountId))?.FirstOrDefault();
                Account? debAcct = (await store.GetAllClientScopeAsync<Account>(dbName, t.DebitedAccountId))?.FirstOrDefault();
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
        await store.CreateManyGlobalScopeAsync(dbName, toInsert);
        store.NotifyChange(typeof(Transactions), ChangeType.Created);
        return invalid;
    }

    public static async Task<List<Transaction>?> TransactionsForAccountAsync(this IDataStore store, string dbName, Guid acct)
    {
        return (await store.ReadAllGlobalScopeAsync<Transaction>(dbName))?.Where(t => t.CreditedAccountId == acct || t.DebitedAccountId == acct).ToList();
    }

    public static async Task<List<Transaction>?> TransactionsForAccountByDateAsync(this IDataStore store, string dbName, Guid acct, DateRange range)
    {
        return (await store.ReadAllGlobalScopeAsync<Transaction>(dbName))
            ?.Where(t => (t.CreditedAccountId == acct || t.DebitedAccountId == acct) && t.When >= range.Start && t.When <= range.End).ToList();
    }

    public static async Task<bool> UpdateTransactionAsync(this IDataStore store, string dbName, Transaction tx)
    {
        bool? result = await store.UpdateOneGlobalScopeAsync(dbName, tx);
        if (result.HasValue && result.Value) store.NotifyChange(typeof(Transaction), ChangeType.Updated);
        return result.HasValue && result.Value;
    }

    public static async Task<bool> DeleteTransactionAsync(this IDataStore store, string dbName, Guid txId)
    {
        bool? result = await store.DeleteOneGlobalScopeAsync<Transaction>(dbName, txId);
        if (result.HasValue && result.Value) store.NotifyChange(typeof(Transaction), ChangeType.Deleted);
        return result.HasValue && result.Value;
    }
}