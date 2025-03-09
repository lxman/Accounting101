using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Interfaces;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Accounting101.Angular.DataAccess.Extensions
{
    public static class DataStoreExtensions
    {
        public static async Task<Guid> CreateOneAsync<T>(this IDataStore store, string dbName, T item)
            where T : IModel
        {
            await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.InsertOneAsync(item)!;
            store.NotifyChange(typeof(T), ChangeType.Created);
            return item.Id;
        }

        public static async Task CreateManyAsync<T>(this IDataStore store, string dbName, IEnumerable<T> items)
            where T : IModel
        {
            await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.InsertManyAsync(items)!;
            store.NotifyChange(typeof(T), ChangeType.Created);
        }

        public static async Task<List<T>?> ReadOneAsync<T>(this IDataStore store, string dbName, Guid id)
            where T : IModel
        {
            return (await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>()).AsQueryable().ToListAsync()).Where(x => x.Id == id).ToList();
        }

        public static async Task<List<T>?> ReadAllAsync<T>(this IDataStore store, string dbName)
            where T : IModel
        {
            return await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.AsQueryable().ToListAsync()!;
        }

        public static async Task<bool?> UpdateOneAsync<T>(this IDataStore store, string dbName, T item)
            where T : IModel
        {
            FilterDefinition<T>? filter = Builders<T>.Filter.Eq(x => x.Id, item.Id);
            UpdateDefinition<T>? update = Builders<T>.Update.Set(x => x, item);
            UpdateResult result = await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.UpdateOneAsync(filter, update)!;
            if (result.IsAcknowledged) store.NotifyChange(typeof(T), ChangeType.Updated);
            return result.IsAcknowledged;
        }

        public static async Task<bool?> DeleteOneAsync<T>(this IDataStore store, string dbName, Guid id)
            where T : IModel
        {
            FilterDefinition<T>? filter = Builders<T>.Filter.Eq(x => x.Id, id);
            DeleteResult result = await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.DeleteOneAsync(filter)!;
            if (result.IsAcknowledged) store.NotifyChange(typeof(T), ChangeType.Deleted);
            return result.IsAcknowledged;
        }

        public static async Task DeleteManyAsync<T>(this IDataStore store, string dbName, IEnumerable<Guid> ids)
            where T : IModel
        {
            FilterDefinition<T>? filter = Builders<T>.Filter.In(x => x.Id, ids);
            await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.DeleteManyAsync(filter)!;
            store.NotifyChange(typeof(T), ChangeType.Deleted);
        }

        public static async Task DropCollectionAsync<T>(this IDataStore store, string dbName, string collection)
            where T : IModel
        {
            await store.Instance(dbName)!.DropCollectionAsync(collection);
            store.NotifyChange(typeof(T), ChangeType.Deleted);
        }
    }
}
