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
            return store.GetCollection<Client>(CollectionNames.Clients)?.Insert(c).AsGuid ?? Guid.Empty;
        }

        public static void BulkInsert(this IDataStore store, IEnumerable<Client> clients)
        {
            store.GetCollection<Client>(CollectionNames.Clients)?.InsertBulk(clients);
        }

        public static Client? FindById(this IDataStore store, Guid id)
        {
            return store.GetCollection<Client>(CollectionNames.Clients)?.FindById(id);
        }
    }
}