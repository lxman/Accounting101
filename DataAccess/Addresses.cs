using System;
using System.Collections.Generic;
using DataAccess.Interfaces;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace DataAccess
{
    public static class Addresses
    {
        public static Guid CreateAddress(this IDataStore store, IAddress address)
        {
            Guid result = store.GetCollection<IAddress>(CollectionNames.Address)?.Insert(address).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Addresses));
            return result;
        }

        public static void BulkInsertAddresses(this IDataStore store, IEnumerable<IAddress> addresses)
        {
            int? result = store.GetCollection<IAddress>(CollectionNames.Address)?.InsertBulk(addresses);
            if (result > 0) store.NotifyChanged(typeof(Addresses));
        }

        public static IEnumerable<IAddress>? AllAddresses(IDataStore store)
        {
            return store.GetCollection<IAddress>(CollectionNames.Address)?.FindAll();
        }

        public static IAddress? FindAddressById(this IDataStore store, Guid id)
        {
            return store.GetCollection<IAddress>(CollectionNames.Address)?.FindById(id);
        }

        public static bool? DeleteAddress(this IDataStore store, Guid id)
        {
            bool? result = store.GetCollection<IAddress>(CollectionNames.Address)?.Delete(id);
            if (result is not null && (bool)result) store.NotifyChanged(typeof(Addresses));
            return result;
        }
    }
}