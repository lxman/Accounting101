using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Interfaces;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Accounting101.Angular.DataAccess;

public static class Addresses
{
    public static async Task<Guid> CreateAddressAsync(this IDataStore store, string dbName, IAddress address)
    {
        await store.GetCollection<IAddress>(dbName, CollectionNames.Address)?.InsertOneAsync(address)!;
        store.NotifyChange(typeof(IAddress), ChangeType.Created);
        return address.Id;
    }

    public static async Task BulkInsertAddressesAsync(this IDataStore store, string dbName, IEnumerable<IAddress> addresses)
    {
        await store.GetCollection<IAddress>(dbName, CollectionNames.Address)?.InsertManyAsync(addresses)!;
        store.NotifyChange(typeof(Addresses), ChangeType.Created);
    }

    public static async Task<IEnumerable<IAddress>?> AllAddressesAsync(IDataStore store, string dbName)
    {
        return await store.GetCollection<IAddress>(dbName, CollectionNames.Address)?.AsQueryable().ToListAsync()!;
    }

    public static IAddress? FindAddressById(this IDataStore store, string dbName, Guid id)
    {
        return (store.GetCollection<IAddress>(dbName, CollectionNames.Address)?.AsQueryable().Where(x => x.Id == id) ?? null)?.FirstOrDefault();
    }

    public static async Task<bool?> UpdateAddressAsync(this IDataStore store, string dbName, IAddress address)
    {
        FilterDefinition<IAddress> filter = Builders<IAddress>.Filter.Eq(x => x.Id, address.Id);
        UpdateDefinition<IAddress> update = Builders<IAddress>.Update.Set(x => x, address);
        UpdateResult? result =
            await store.GetCollection<IAddress>(dbName, CollectionNames.Address)!.UpdateOneAsync(filter, update);
        if (result.IsAcknowledged) store.NotifyChange(typeof(IAddress), ChangeType.Updated);
        return result.IsAcknowledged;
    }

    public static async Task<bool?> DeleteAddressAsync(this IDataStore store, string dbName, Guid addressId)
    {
        FilterDefinition<IAddress> filter = Builders<IAddress>.Filter.Eq(x => x.Id, addressId);
        DeleteResult? result = await store.GetCollection<IAddress>(dbName, CollectionNames.Address)!.DeleteOneAsync(filter);
        if (result.IsAcknowledged) store.NotifyChange(typeof(IAddress), ChangeType.Deleted);
        return result.IsAcknowledged;
    }
}