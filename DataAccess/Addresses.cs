using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.Extensions;
using DataAccess.Interfaces;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace DataAccess;

public static class Addresses
{
    public static async Task<Guid> CreateAddressAsync(this IDataStore store, string dbName, IAddress address)
    {
        Guid result = await store.CreateOneAsync(dbName, address);
        store.NotifyChange(typeof(IAddress), ChangeType.Created);
        return result;
    }

    public static async Task BulkInsertAddressesAsync(this IDataStore store, string dbName, IEnumerable<IAddress> addresses)
    {
        await store.GetCollection<IAddress>(dbName, CollectionNames.Address)?.InsertManyAsync(addresses)!;
        store.NotifyChange(typeof(Addresses), ChangeType.Created);
    }

    public static async Task<IEnumerable<IAddress>?> AllAddressesAsync(IDataStore store, string dbName)
    {
        return await store.ReadAllAsync<IAddress>(dbName);
    }

    public static async Task<IAddress?> FindAddressByIdAsync(this IDataStore store, string dbName, Guid id)
    {
        return (await store.ReadOneAsync<IAddress>(dbName, id))!.FirstOrDefault();
    }

    public static async Task<bool?> UpdateAddressAsync(this IDataStore store, string dbName, IAddress address)
    {
        bool? result = await store.UpdateOneAsync(dbName, address);
        if (result.HasValue && result.Value) store.NotifyChange(typeof(IAddress), ChangeType.Updated);
        return result;
    }

    public static async Task<bool?> DeleteAddressAsync(this IDataStore store, string dbName, Guid id)
    {
        bool? result = await store.DeleteOneAsync<IAddress>(dbName, id);
        if (result.HasValue && result.Value) store.NotifyChange(typeof(IAddress), ChangeType.Deleted);
        return result;
    }
}