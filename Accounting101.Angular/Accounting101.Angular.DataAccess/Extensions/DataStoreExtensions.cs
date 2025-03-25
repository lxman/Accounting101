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
        public static async Task<Guid> CreateOneGlobalScopeAsync<T>(this IDataStore store, string dbName, T item)
            where T : IGlobalItem
        {
            await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.InsertOneAsync(item)!;
            store.NotifyChange(typeof(T), ChangeType.Created);
            return item.Id;
        }

        public static async Task<Guid> CreateOneClientScopeAsync<T>(this IDataStore store, string dbName, T item)
            where T : IClientItem
        {
            await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.InsertOneAsync(item)!;
            store.NotifyChange(typeof(T), ChangeType.Created);
            return item.Id;
        }

        public static async Task CreateManyGlobalScopeAsync<T>(this IDataStore store, string dbName, IEnumerable<T> items)
            where T : IGlobalItem
        {
            await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.InsertManyAsync(items)!;
            store.NotifyChange(typeof(T), ChangeType.Created);
        }

        public static async Task CreateManyClientScopeAsync<T>(this IDataStore store, string dbName, IEnumerable<T> items)
            where T : IClientItem
        {
            await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.InsertManyAsync(items)!;
            store.NotifyChange(typeof(T), ChangeType.Created);
        }

        public static async Task<List<T>?> GetAllGlobalScopeAsync<T>(this IDataStore store, string dbName)
            where T : IGlobalItem
        {
            return await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.AsQueryable().ToListAsync()!;
        }

        public static async Task<List<T>?> GetAllClientScopeAsync<T>(this IDataStore store, string dbName, Guid clientId)
            where T : IClientItem
        {
            return (await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>()).AsQueryable().ToListAsync()).Where(x => x.ClientId == clientId).ToList();
        }

        public static async Task<List<T>?> ReadAllGlobalScopeAsync<T>(this IDataStore store, string dbName)
            where T : IGlobalItem
        {
            return await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.AsQueryable().ToListAsync()!;
        }

        public static async Task<List<T>?> ReadAllClientScopeAsync<T>(this IDataStore store, string dbName, Guid clientId)
            where T : IClientItem
        {
            return (await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>()).AsQueryable().ToListAsync()).Where(x => x.ClientId == clientId).ToList();
        }

        public static async Task<bool?> UpdateOneGlobalScopeAsync<T>(this IDataStore store, string dbName, T item)
            where T : IGlobalItem
        {
            FilterDefinition<T>? filter = Builders<T>.Filter.Eq(x => x.Id, item.Id);
            UpdateDefinition<T>? update = Builders<T>.Update.Set(x => x, item);
            UpdateResult result = await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.UpdateOneAsync(filter, update)!;
            if (result.IsAcknowledged) store.NotifyChange(typeof(T), ChangeType.Updated);
            return result.IsAcknowledged;
        }

        public static async Task<bool?> UpdateOneClientScopeAsync<T>(this IDataStore store, string dbName, T item)
            where T : IClientItem
        {
            FilterDefinition<T>? filter = Builders<T>.Filter.Eq(x => x.ClientId, item.ClientId);
            UpdateDefinition<T>? update = Builders<T>.Update.Set(x => x, item);
            UpdateResult result = await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.UpdateOneAsync(filter, update)!;
            if (result.IsAcknowledged) store.NotifyChange(typeof(T), ChangeType.Updated);
            return result.IsAcknowledged;
        }

        public static async Task<bool?> DeleteOneGlobalScopeAsync<T>(this IDataStore store, string dbName, Guid id)
            where T : IGlobalItem
        {
            FilterDefinition<T>? filter = Builders<T>.Filter.Eq(x => x.Id, id);
            DeleteResult result = await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.DeleteOneAsync(filter)!;
            if (result.IsAcknowledged) store.NotifyChange(typeof(T), ChangeType.Deleted);
            return result.IsAcknowledged;
        }

        public static async Task<bool?> DeleteOneClientScopeAsync<T>(this IDataStore store, string dbName, Guid clientId, Guid id)
            where T : IClientItem
        {
            FilterDefinition<T>? filter = Builders<T>.Filter.Eq(x => x.ClientId, clientId);
            filter &= Builders<T>.Filter.Eq(x => x.Id, id);
            DeleteResult result = await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.DeleteOneAsync(filter)!;
            if (result.IsAcknowledged) store.NotifyChange(typeof(T), ChangeType.Deleted);
            return result.IsAcknowledged;
        }

        public static async Task DeleteManyGlobalScopeAsync<T>(this IDataStore store, string dbName, IEnumerable<Guid> ids)
            where T : IGlobalItem
        {
            FilterDefinition<T>? filter = Builders<T>.Filter.In(x => x.Id, ids);
            await store.GetCollection<T>(dbName, CollectionNames.GetCollectionName<T>())?.DeleteManyAsync(filter)!;
            store.NotifyChange(typeof(T), ChangeType.Deleted);
        }

        public static async Task DropCollectionGlobalScopeAsync<T>(this IDataStore store, string dbName, string collection)
            where T : IGlobalItem
        {
            await store.Instance(dbName)!.DropCollectionAsync(collection);
            store.NotifyChange(typeof(T), ChangeType.Deleted);
        }

        public static async Task DropCollectionClientScopeAsync<T>(this IDataStore store, string dbName, string collection)
            where T : IClientItem
        {
            await store.Instance(dbName)!.DropCollectionAsync(collection);
            store.NotifyChange(typeof(T), ChangeType.Deleted);
        }
    }
}
