using DataAccess.Models;
using DataAccess.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace DataAccess
{
    public static class Settings
    {
        public static Guid Create(this IDataStore store, Setting setting)
        {
            Guid result = store.GetCollection<Setting>(CollectionNames.Settings)?.Insert(setting).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Settings));
            return result;
        }

        public static IEnumerable<Setting> Find(this IDataStore store, string key)
        {
            return store.GetCollection<Setting>(CollectionNames.Settings)?.Find(s => s.Key == key) ?? new List<Setting>();
        }

        public static void Remove(this IDataStore store, string key)
        {
            int? result = store.GetCollection<Setting>(CollectionNames.Settings)?.DeleteMany(s => s.Key == key);
            if (result > 0) store.NotifyChanged(typeof(Settings));
        }
    }
}