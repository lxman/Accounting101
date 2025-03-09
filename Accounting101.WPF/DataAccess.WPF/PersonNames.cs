using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess.WPF;

public static class PersonNames
{
    public static async Task<Guid> CreateNameAsync(this IDataStore store, PersonName name)
    {
        ILiteCollectionAsync<PersonName>? collection = store.GetCollection<PersonName>(CollectionNames.PersonName);
        Guid result = (await collection?.InsertAsync(name)!)?.AsGuid ?? Guid.Empty;
        if (result != Guid.Empty) store.NotifyChange(typeof(PersonName), ChangeType.Created);
        return result;
    }

    public static async Task BulkInsertNamesAsync(this IDataStore store, IEnumerable<PersonName> names)
    {
        ILiteCollectionAsync<PersonName>? collection = store.GetCollection<PersonName>(CollectionNames.PersonName);
        int result = await collection?.InsertBulkAsync(names)!;
        if (result > 0) store.NotifyChange(typeof(PersonNames), ChangeType.Created);
    }

    public static async Task<IEnumerable<PersonName>?> AllNamesAsync(this IDataStore store)
    {
        ILiteCollectionAsync<PersonName>? collection = store.GetCollection<PersonName>(CollectionNames.PersonName);
        return await collection?.FindAllAsync()!;
    }

    public static async Task<PersonName?> FindNameByIdAsync(this IDataStore store, Guid id)
    {
        ILiteCollectionAsync<PersonName>? collection = store.GetCollection<PersonName>(CollectionNames.PersonName);
        return await collection?.FindByIdAsync(id)!;
    }

    public static async Task<bool> UpdateNameAsync(this IDataStore store, PersonName name)
    {
        ILiteCollectionAsync<PersonName>? collection = store.GetCollection<PersonName>(CollectionNames.PersonName);
        bool result = await collection?.UpdateAsync(name)!;
        if (result) store.NotifyChange(typeof(PersonName), ChangeType.Updated);
        return result;
    }
}