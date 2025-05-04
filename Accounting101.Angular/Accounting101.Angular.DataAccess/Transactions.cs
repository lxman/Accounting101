using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Accounting101.Angular.DataAccess;

public static class Transactions
{
    public static async Task<Guid> CreateTransactionAsync(this IDataStore store, string dbName, string clientId, Transaction tx)
    {
        Account? credAcct = (await store.GetAllClientScopeAsync<Account>(dbName, clientId))?.FirstOrDefault(a => a.Id == Guid.Parse(tx.CreditedAccountId));
        Account? debAcct = (await store.GetAllClientScopeAsync<Account>(dbName, clientId))?.FirstOrDefault(a => a.Id == Guid.Parse(tx.DebitedAccountId));
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

    public static async IAsyncEnumerable<List<Transaction>> TransactionsForAccountAsync(this IDataStore store, string dbName, string accountId, int batchSize = 100)
    {
        IMongoCollection<BsonDocument>? collection = store.GetCollection<BsonDocument>(dbName, CollectionNames.Transaction);

        if (collection is null)
            yield break;

        // Create a filter for transactions related to the specified account
        FilterDefinition<BsonDocument>? filter = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Eq("CreditedAccountId", accountId),
            Builders<BsonDocument>.Filter.Eq("DebitedAccountId", accountId)
        );

        // Use Find with a cursor and process in batches
        using IAsyncCursor<BsonDocument>? cursor = await collection.FindAsync(filter, new FindOptions<BsonDocument> { BatchSize = batchSize });

        while (await cursor.MoveNextAsync())
        {
            List<Transaction> currentBatch = [];
            foreach (BsonDocument? document in cursor.Current)
            {
                // Convert BsonDocument to .NET types
                object? dotNetValue = BsonTypeMapper.MapToDotNetValue(document);

                if (dotNetValue is not Dictionary<string, object> dict)
                    continue;

                Guid id = dict["_id"] as Guid? ?? Guid.Empty;
                string creditedAccountId = dict["CreditedAccountId"] as string ?? string.Empty;
                string debitedAccountId = dict["DebitedAccountId"] as string ?? string.Empty;
                Decimal128 amount = dict["Amount"] as Decimal128? ?? 0;
                DateOnly when = DateOnly.FromDateTime(dict["When"] as DateTime? ?? DateTime.MinValue);

                currentBatch.Add(new Transaction(id, creditedAccountId, debitedAccountId, (decimal)amount, when));
            }

            if (currentBatch.Count > 0)
                yield return currentBatch;
        }
    }

    public static async Task<List<Transaction>?> TransactionsForAccountByDateAsync(this IDataStore store, string dbName, string accountId, DateRange range)
    {
        return (await store.ReadAllGlobalScopeAsync<Transaction>(dbName))
            ?.Where(t => (t.CreditedAccountId == accountId || t.DebitedAccountId == accountId) && t.When >= range.Start && t.When <= range.End).ToList();
    }

    public static async Task<bool> UpdateTransactionAsync(this IDataStore store, string dbName, Transaction tx)
    {
        bool? result = await store.ReplaceOneGlobalScopeAsync(dbName, tx);
        if (result.HasValue && result.Value)
        {
            store.NotifyChange(typeof(Transaction), ChangeType.Updated);
        }
        return result.HasValue && result.Value;
    }

    public static async Task<bool> DeleteTransactionAsync(this IDataStore store, string dbName, Guid txId)
    {
        bool? result = await store.DeleteOneGlobalScopeAsync<Transaction>(dbName, txId);
        if (result.HasValue && result.Value) store.NotifyChange(typeof(Transaction), ChangeType.Deleted);
        return result.HasValue && result.Value;
    }
}