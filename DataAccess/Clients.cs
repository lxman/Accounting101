using DataAccess.Models;
using DataAccess.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace DataAccess
{
    public static class Clients
    {
        public static Guid Create(this IDataStore store, Client c)
        {
            Guid result = store.GetCollection<Client>(CollectionNames.Clients)?.Insert(c).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Clients));
            return result;
        }

        public static void BulkInsert(this IDataStore store, IEnumerable<Client> clients)
        {
            int? result = store.GetCollection<Client>(CollectionNames.Clients)?.InsertBulk(clients);
            if (result > 0) store.NotifyChanged(typeof(Clients));
        }

        public static Client? FindById(this IDataStore store, Guid id)
        {
            return store.GetCollection<Client>(CollectionNames.Clients)?.FindById(id);
        }

        public static IEnumerable<Client>? All(this IDataStore store)
        {
            return store.GetCollection<Client>(CollectionNames.Clients)?.FindAll();
        }

        public static bool? Delete(this IDataStore store, Guid id)
        {
            return store.GetCollection<Client>(CollectionNames.Clients)?.Delete(id);
        }
    }
}