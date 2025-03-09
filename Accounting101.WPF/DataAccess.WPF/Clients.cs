using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.WPF.Interfaces;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess.WPF;

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
        int count = await store.GetCollection<Client>(CollectionNames.Client)?.CountAsync()!;
        return count > 0;
    }

    public static async Task BulkInsertClientsAsync(this IDataStore store, IEnumerable<Client> clients)
    {
        ILiteCollectionAsync<Client>? collection = store.GetCollection<Client>(CollectionNames.Client);
        int result = await collection?.InsertBulkAsync(clients)!;
        if (result > 0) store.NotifyChange(typeof(Clients), ChangeType.Created);
    }

    public static async Task UpdateClientAsync(this IDataStore store, ClientWithInfo client)
    {
        ILiteCollectionAsync<Client> clientCollection = store.GetCollection<Client>(CollectionNames.Client)!;
        ILiteCollectionAsync<PersonName?> personNameCollection = store.GetCollection<PersonName>(CollectionNames.PersonName)!;
        ILiteCollectionAsync<IAddress?> addressCollection = store.GetCollection<IAddress>(CollectionNames.Address)!;
        await clientCollection.UpdateAsync(new Client
        {
            AddressId = client.AddressId,
            BusinessName = client.BusinessName,
            Id = client.Id,
            PersonNameId = client.PersonNameId
        });
        await personNameCollection.UpdateAsync(client.Name);
        await addressCollection.UpdateAsync(client.Address);
    }

    public static async Task UpdateClientAsync(this IDataStore store, Client client)
    {
        ILiteCollectionAsync<Client>? collection = store.GetCollection<Client>(CollectionNames.Client);
        if (collection is null)
        {
            return;
        }
        await collection.UpdateAsync(new Client
        {
            AddressId = client.AddressId,
            BusinessName = client.BusinessName,
            CheckPointId = client.CheckPointId,
            Id = client.Id,
            PersonNameId = client.PersonNameId
        });
        store.NotifyChange(typeof(Client), ChangeType.Updated);
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
        ILiteCollectionAsync<Client>? clients = store.GetCollection<Client>(CollectionNames.Client);
        List<AccountWithInfo>? accounts = (await store.AccountsForClientAsync(id))?.ToList();
        if (accounts is not null)
        {
            foreach (AccountWithInfo account in accounts)
            {
                await store.DeleteAccountAsync(account.Id);
            }
        }
        CheckPoint? checkPoint = await store.GetCheckpointAsync(id);
        if (checkPoint is not null)
        {
            await store.ClearCheckpointAsync(checkPoint.Id);
        }
        bool result = await clients?.DeleteAsync(id)!;
        store.NotifyChange(typeof(Client), ChangeType.Deleted);
        return result;
    }
}