using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess
{
    public static class Clients
    {
        public static async Task<Guid> CreateClientAsync(this IDataStore store, Client c)
        {
            ILiteCollectionAsync<Client>? collection = store.GetCollection<Client>(CollectionNames.Client);
            Guid result = (await collection?.InsertAsync(c)!)?.AsGuid ?? Guid.Empty;
            if (result != Guid.Empty) store.NotifyChange(typeof(Client), ChangeType.Created);
            return result;
        }

        public static async Task<ClientWithInfo?> GetClientWithInfoAsync(this IDataStore store, Guid id)
        {
            ILiteCollectionAsync<Client>? collection = store.GetCollection<Client>(CollectionNames.Client);
            Client? c = await collection?.FindByIdAsync(id)!;
            return c is null ? null : new ClientWithInfo(store, c);
        }

        public static async Task<bool> ClientsExistAsync(this IDataStore store)
        {
            int count = await store.GetCollection<Client>(CollectionNames.Client)?.CountAsync();
            return count > 0;
        }

        public static async Task BulkInsertClientsAsync(this IDataStore store, IEnumerable<Client> clients)
        {
            ILiteCollectionAsync<Client>? collection = store.GetCollection<Client>(CollectionNames.Client);
            int result = await collection?.InsertBulkAsync(clients)!;
            if (result > 0) store.NotifyChange(typeof(Clients), ChangeType.Created);
        }

        public static async Task<Client?> FindClientByIdAsync(this IDataStore store, Guid id)
        {
            ILiteCollectionAsync<Client>? collection = store.GetCollection<Client>(CollectionNames.Client);
            return await collection?.FindByIdAsync(id)!;
        }

        public static async Task<IEnumerable<Client>?> AllClientsAsync(this IDataStore store)
        {
            ILiteCollectionAsync<Client>? collection = store.GetCollection<Client>(CollectionNames.Client);
            return await collection?.FindAllAsync()!;
        }

        public static async Task<IEnumerable<ClientWithInfo>?> AllClientsWithInfosAsync(this IDataStore store)
        {
            ILiteCollectionAsync<Client>? collection = store.GetCollection<Client>(CollectionNames.Client);
            IEnumerable<Client>? clients = await collection?.FindAllAsync()!;
            return clients?.Select(c => new ClientWithInfo(store, c));
        }

        public static async Task<bool?> DeleteClientAsync(this IDataStore store, Guid id)
        {
            ILiteCollectionAsync<Client>? collection = store.GetCollection<Client>(CollectionNames.Client);
            store.NotifyChange(typeof(Client), ChangeType.Deleted);
            return await collection?.DeleteAsync(id)!;
        }
    }
}