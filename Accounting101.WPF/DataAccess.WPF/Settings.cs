using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess.WPF;

public static class Settings
{
    public static async Task<Guid> CreateSettingAsync(this IDataStore store, Setting setting)
    {
        ILiteCollectionAsync<Setting>? collection = store.GetCollection<Setting>(CollectionNames.Setting);
        Guid result = (await collection?.InsertAsync(setting)!).AsGuid;
        if (result != Guid.Empty) store.NotifyChange(typeof(Setting), ChangeType.Created);
        return result;
    }

    public static async Task<IEnumerable<Setting>> FindSettingAsync(this IDataStore store, string key)
    {
        ILiteCollectionAsync<Setting>? collection = store.GetCollection<Setting>(CollectionNames.Setting);
        return await collection?.FindAsync(s => s.Key == key)! ?? new List<Setting>();
    }

    public static async Task RemoveSettingAsync(this IDataStore store, string key)
    {
        ILiteCollectionAsync<Setting>? collection = store.GetCollection<Setting>(CollectionNames.Setting);
        int result = await collection?.DeleteManyAsync(s => s.Key == key)!;
        if (result > 0) store.NotifyChange(typeof(Setting), ChangeType.Deleted);
    }
}