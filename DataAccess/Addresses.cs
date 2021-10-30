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
            return store.GetCollection<IAddress>(CollectionNames.Addresses)?.Insert(address).AsGuid ?? Guid.Empty;
        }

        public static void BulkInsert(this IDataStore store, IEnumerable<IAddress> addresses)
        {
            store.GetCollection<IAddress>(CollectionNames.Addresses)?.InsertBulk(addresses);
        }

        public static IAddress? FindById(this IDataStore store, Guid id)
        {
            return store.GetCollection<IAddress>(CollectionNames.Addresses)?.FindById(id);
        }

        public static bool? Delete(this IDataStore store, Guid id)
        {
            return store.GetCollection<IAddress>(CollectionNames.Addresses)?.Delete(id);
        }
    }
}