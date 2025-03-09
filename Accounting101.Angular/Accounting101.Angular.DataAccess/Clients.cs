using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.DataAccess;

public static class Clients
{
    public static async Task<Guid> CreateClientAsync(this IDataStore store, string dbName, Client c)
    {
        Guid result = await store.CreateOneAsync(dbName, c);
        if (result != Guid.Empty) store.NotifyChange(typeof(Client), ChangeType.Created);
        return result;
    }

    public static async Task<ClientWithInfo?> GetClientWithInfoAsync(this IDataStore store, string dbName, Guid id)
    {
        Client? c = (await store.ReadOneAsync<Client>(dbName, id))!.FirstOrDefault();
        return c is null ? null : new ClientWithInfo(store, dbName, c);
    }

    public static async Task<bool> ClientsExistAsync(this IDataStore store, string dbName)
    {
        return (await store.ReadAllAsync<Client>(dbName))!.Count > 0;
    }

    public static async Task BulkInsertClientsAsync(this IDataStore store, string dbName, IEnumerable<Client> clients)
    {
        await store.CreateManyAsync(dbName, clients);
        store.NotifyChange(typeof(Clients), ChangeType.Created);
    }

    public static async Task UpdateClientAsync(this IDataStore store, string dbName, ClientWithInfo client)
    {
        await store.UpdateOneAsync<Client>(dbName, client);
        await store.UpdateOneAsync(dbName, client.Name!);
        await store.UpdateAddressAsync(dbName, client.Address!);
        store.NotifyChange(typeof(ClientWithInfo), ChangeType.Updated);
    }

    public static async Task UpdateClientAsync(this IDataStore store, string dbName, Client client)
    {
        await store.UpdateOneAsync(dbName, client);
        store.NotifyChange(typeof(Client), ChangeType.Updated);
    }

    public static async Task<Client?> FindClientByIdAsync(this IDataStore store, string dbName, Guid id)
    {
        return (await store.ReadOneAsync<Client>(dbName, id))!.FirstOrDefault();
    }

    public static async Task<IEnumerable<Client>?> AllClientsAsync(this IDataStore store, string dbName)
    {
        return await store.ReadAllAsync<Client>(dbName);
    }

    public static async Task<IEnumerable<ClientWithInfo>?> AllClientsWithInfosAsync(this IDataStore store, string dbName)
    {
        List<Client>? clients = (await store.AllClientsAsync(dbName))?.ToList();
        return clients?.Select(c => new ClientWithInfo(store, dbName, c));
    }

    public static async Task<bool?> DeleteClientAsync(this IDataStore store, string dbName, Guid id)
    {
        List<AccountWithInfo>? accounts = (await store.AccountsForClientAsync(dbName, id))?.ToList();
        if (accounts is not null)
        {
            foreach (AccountWithInfo account in accounts)
            {
                await store.DeleteAccountAsync(dbName, account.Id);
            }
        }
        CheckPoint? checkPoint = await store.GetCheckpointAsync(dbName, id);
        if (checkPoint is not null)
        {
            await store.ClearCheckpointAsync(dbName, checkPoint.Id);
        }

        bool? result = await store.DeleteOneAsync<Client>(dbName, id);
        store.NotifyChange(typeof(Client), ChangeType.Deleted);
        return result;
    }
}