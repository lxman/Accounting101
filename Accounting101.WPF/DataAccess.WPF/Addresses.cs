using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DataAccess.WPF.Interfaces;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;

namespace DataAccess.WPF;

public static class Addresses
{
    public static async Task<Guid> CreateAddressAsync(this IDataStore store, IAddress address)
    {
        Guid result = (await store.GetCollection<IAddress>(CollectionNames.Address)?.InsertAsync(address)!).AsGuid;
        if (result != Guid.Empty) store.NotifyChange(typeof(IAddress), ChangeType.Created);
        return result;
    }

    public static async Task BulkInsertAddressesAsync(this IDataStore store, IEnumerable<IAddress> addresses)
    {
        int? result = await store.GetCollection<IAddress>(CollectionNames.Address)?.InsertBulkAsync(addresses)!;
        if (result > 0) store.NotifyChange(typeof(Addresses), ChangeType.Created);
    }

    public static async Task<IEnumerable<IAddress>?> AllAddressesAsync(IDataStore store)
    {
        return await store.GetCollection<IAddress>(CollectionNames.Address)?.FindAllAsync()!;
    }

    public static async Task<IAddress?> FindAddressByIdAsync(this IDataStore store, Guid id)
    {
        return await store.GetCollection<IAddress>(CollectionNames.Address)?.FindByIdAsync(id)!;
    }

    public static async Task<bool?> UpdateAddressAsync(this IDataStore store, IAddress address)
    {
        bool? result = await store.GetCollection<IAddress>(CollectionNames.Address)?.UpdateAsync(address)!;
        if (result is not null && (bool)result) store.NotifyChange(typeof(IAddress), ChangeType.Updated);
        return result;
    }

    public static async Task<bool?> DeleteAddressAsync(this IDataStore store, Guid id)
    {
        bool? result = await store.GetCollection<IAddress>(CollectionNames.Address)?.DeleteAsync(id)!;
        if (result is not null && (bool)result) store.NotifyChange(typeof(IAddress), ChangeType.Deleted);
        return result;
    }
}