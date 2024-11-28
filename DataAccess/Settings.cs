using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess
{
    public static class Settings
    {
        public static async Task<Guid> CreateAsync(this IDataStore store, Setting setting)
        {
            ILiteCollectionAsync<Setting>? collection = store.GetCollection<Setting>(CollectionNames.Setting);
            Guid result = (await collection?.InsertAsync(setting)!).AsGuid;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Settings));
            return result;
        }

        public static async Task<IEnumerable<Setting>> FindAsync(this IDataStore store, string key)
        {
            ILiteCollectionAsync<Setting>? collection = store.GetCollection<Setting>(CollectionNames.Setting);
            return await collection?.FindAsync(s => s.Key == key)! ?? new List<Setting>();
        }

        public static async Task RemoveAsync(this IDataStore store, string key)
        {
            ILiteCollectionAsync<Setting>? collection = store.GetCollection<Setting>(CollectionNames.Setting);
            int result = await collection?.DeleteManyAsync(s => s.Key == key)!;
            if (result > 0) store.NotifyChanged(typeof(Settings));
        }
    }
}