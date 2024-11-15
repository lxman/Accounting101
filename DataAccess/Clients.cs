using System;
using System.Collections.Generic;
using System.Linq;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace DataAccess
{
    public static class Clients
    {
        public static Guid CreateClient(this IDataStore store, Client c)
        {
            Guid result = store.GetCollection<Client>(CollectionNames.Clients)?.Insert(c).AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChanged(typeof(Clients));
            return result;
        }

        public static ClientWithInfo? GetClientWithInfo(this IDataStore store, Guid id)
        {
            Client? c = store.GetCollection<Client>(CollectionNames.Clients)?.FindById(id);
            return c is null ? null : new ClientWithInfo(store, c);
        }

        public static void BulkInsertClients(this IDataStore store, IEnumerable<Client> clients)
        {
            int? result = store.GetCollection<Client>(CollectionNames.Clients)?.InsertBulk(clients);
            if (result > 0) store.NotifyChanged(typeof(Clients));
        }

        public static Client? FindClientById(this IDataStore store, Guid id)
        {
            return store.GetCollection<Client>(CollectionNames.Clients)?.FindById(id);
        }

        public static IEnumerable<Client>? AllClients(this IDataStore? store)
        {
            return store.GetCollection<Client>(CollectionNames.Clients)?.FindAll();
        }

        public static IEnumerable<ClientWithInfo>? AllClientsWithInfos(this IDataStore store)
        {
            return store.GetCollection<Client>(CollectionNames.Clients)
                ?.FindAll()
                .Select(c => new ClientWithInfo(store, c));
        }

        public static bool? DeleteClient(this IDataStore store, Guid id)
        {
            return store.GetCollection<Client>(CollectionNames.Clients)?.Delete(id);
        }
    }
}