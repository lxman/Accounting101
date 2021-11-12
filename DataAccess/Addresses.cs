using DataAccess.Interfaces;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace DataAccess
{
    public static class Addresses
    {
        public static Guid Create(this IDataStore store, IAddress address)
        {
            Guid result = store.GetCollection<IAddress>(CollectionNames.Addresses)?.Insert(address).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Addresses));
            return result;
        }

        public static void BulkInsert(this IDataStore store, IEnumerable<IAddress> addresses)
        {
            int? result = store.GetCollection<IAddress>(CollectionNames.Addresses)?.InsertBulk(addresses);
            if (result > 0) store.NotifyChanged(typeof(Addresses));
        }

        public static IEnumerable<IAddress>? All(IDataStore store)
        {
            return store.GetCollection<IAddress>(CollectionNames.Addresses)?.FindAll();
        }

        public static IAddress? FindById(this IDataStore store, Guid id)
        {
            return store.GetCollection<IAddress>(CollectionNames.Addresses)?.FindById(id);
        }

        public static bool? Delete(this IDataStore store, Guid id)
        {
            bool? result = store.GetCollection<IAddress>(CollectionNames.Addresses)?.Delete(id);
            if (result is not null && (bool)result) store.NotifyChanged(typeof(Addresses));
            return result;
        }
    }
}