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
        Guid result = await store.CreateOneGlobalScopeAsync(dbName, c);
        if (result != Guid.Empty) store.NotifyChange(typeof(Client), ChangeType.Created);
        return result;
    }

    public static async Task<ClientWithInfo?> GetClientWithInfoAsync(this IDataStore store, string dbName, Guid clientId)
    {
        Client? c = (await store.GetAllGlobalScopeAsync<Client>(dbName))!.FirstOrDefault(c => c.Id == clientId);
        return c is null ? null : new ClientWithInfo(store, dbName, c);
    }

    public static async Task<bool> ClientsExistAsync(this IDataStore store, string dbName)
    {
        return (await store.ReadAllGlobalScopeAsync<Client>(dbName))!.Count > 0;
    }

    public static async Task BulkInsertClientsAsync(this IDataStore store, string dbName, IEnumerable<Client> clients)
    {
        await store.CreateManyGlobalScopeAsync(dbName, clients);
        store.NotifyChange(typeof(Clients), ChangeType.Created);
    }

    public static async Task UpdateClientAsync(this IDataStore store, string dbName, ClientWithInfo client)
    {
        await store.UpdateOneGlobalScopeAsync<Client>(dbName, client);
        await store.UpdateOneGlobalScopeAsync(dbName, client.ContactName!);
        await store.UpdateAddressAsync(dbName, client.Address!);
        store.NotifyChange(typeof(ClientWithInfo), ChangeType.Updated);
    }

    public static async Task UpdateClientAsync(this IDataStore store, string dbName, Client client)
    {
        await store.UpdateOneGlobalScopeAsync(dbName, client);
        store.NotifyChange(typeof(Client), ChangeType.Updated);
    }

    public static async Task<Client?> FindClientByIdAsync(this IDataStore store, string dbName, Guid clientId)
    {
        return (await store.GetAllGlobalScopeAsync<Client>(dbName))!.FirstOrDefault(c => c.Id == clientId);
    }

    public static async Task<IEnumerable<Client>?> AllClientsAsync(this IDataStore store, string dbName)
    {
        return await store.ReadAllGlobalScopeAsync<Client>(dbName);
    }

    public static async Task<IEnumerable<ClientWithInfo>?> AllClientsWithInfosAsync(this IDataStore store, string dbName)
    {
        List<Client>? clients = (await store.AllClientsAsync(dbName))?.ToList();
        return clients?.Select(c => new ClientWithInfo(store, dbName, c));
    }

    public static async Task<bool?> DeleteClientAsync(this IDataStore store, string dbName, Guid clientId)
    {
        ClientWithInfo? client = await store.GetClientWithInfoAsync(dbName, clientId);
        if (client is null)
        {
            return false;
        }
        List<AccountWithInfo>? accounts = (await store.AccountsForClientAsync(dbName, clientId))?.ToList();
        if (accounts is not null)
        {
            foreach (AccountWithInfo account in accounts)
            {
                await store.DeleteAccountAsync(dbName, clientId, account.Id);
            }
        }
        CheckPoint? checkPoint = await store.GetCheckpointAsync(dbName, clientId);
        if (checkPoint is not null)
        {
            await store.ClearCheckpointAsync(dbName, checkPoint.Id);
        }

        await store.DeleteRootGroupAsync(dbName, clientId);

        await store.DeleteNameAsync(dbName, client.PersonNameId);

        await store.DeleteAddressAsync(dbName, client.AddressId);

        bool? result = await store.DeleteOneGlobalScopeAsync<Client>(dbName, clientId);
        store.NotifyChange(typeof(Client), ChangeType.Deleted);
        return result;
    }
}