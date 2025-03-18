using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.DataAccess;

public static class CheckPoints
{
    public static async Task<bool> SetCheckpointAsync(this IDataStore store, string dbName, Guid clientId, DateOnly date)
    {
        Client? c = await store.GetClientWithInfoAsync(dbName, clientId);
        CheckPoint checkPoint = new(clientId, date);
        IEnumerable<AccountWithInfo>? accounts = await store.AccountsForClientAsync(dbName, clientId);
        if (accounts is null || c is null)
        {
            return false;
        }

        List<AccountWithInfo> accountsList = accounts.ToList();
        for (int x = 0; x < accountsList.Count; x++)
        {
            AccountWithInfo a = accountsList.ElementAt(x);
            decimal balance = await store.GetAccountBalanceOnDateAsync(dbName, a.Id, date);
            checkPoint.AccountCheckpoints.Add(new AccountCheckpoint(clientId, a.Id, balance));
        }

        List<CheckPoint>? checkPoints = await store.ReadAllClientScopeAsync<CheckPoint>(dbName, clientId);
        List<AccountCheckpoint>? accountCheckpoints = await store.ReadAllGlobalScopeAsync<AccountCheckpoint>(dbName);
        if (checkPoints is null || accountCheckpoints is null)
        {
            return false;
        }

        Guid? id = null;
        if (checkPoints.Count == 0)
        {
            id = await store.CreateOneClientScopeAsync(dbName, checkPoint);
            accountCheckpoints.AddRange(checkPoint.AccountCheckpoints);
        }
        else
        {
            CheckPoint? existing = checkPoints.FirstOrDefault(cp => cp.ClientId == clientId);
            if (existing is not null)
            {
                await store.DeleteOneClientScopeAsync<CheckPoint>(dbName, existing.Id);
            }
            checkPoints.Add(checkPoint);
            id = await store.CreateOneClientScopeAsync(dbName, checkPoint);
            await store.CreateManyGlobalScopeAsync(dbName, checkPoint.AccountCheckpoints);
        }
        c.CheckPointId = id;
        await store.UpdateClientAsync(dbName, c);
        return true;
    }

    public static async Task<CheckPoint?> GetCheckpointAsync(this IDataStore store, string dbName, Guid clientId)
    {
        CheckPoint? checkPoint = (await store.GetAllClientScopeAsync<CheckPoint>(dbName, clientId))!.FirstOrDefault();
        List<AccountCheckpoint>? accountCheckpoints = (await store.ReadAllGlobalScopeAsync<AccountCheckpoint>(dbName))!;
        if (checkPoint is null || accountCheckpoints is null)
        {
            return null;
        }
        checkPoint.AccountCheckpoints.AddRange(accountCheckpoints.Where(ac => ac.ClientId == clientId));
        return checkPoint;
    }

    public static async Task ClearCheckpointAsync(this IDataStore store, string dbName, Guid clientId)
    {
        Client? c = await store.GetClientWithInfoAsync(dbName, clientId);
        CheckPoint? existing = (await store.GetAllClientScopeAsync<CheckPoint>(dbName, clientId))!.FirstOrDefault();
        if (c is null || existing is null)
        {
            return;
        }
        List<AccountCheckpoint>? accountCheckpoints = (await store.ReadAllGlobalScopeAsync<AccountCheckpoint>(dbName))!;
        if (accountCheckpoints is not null)
        {
            await store.DeleteManyGlobalScopeAsync<AccountCheckpoint>(dbName, accountCheckpoints.Where(ac => ac.ClientId == clientId).Select(ac => ac.Id));
            await store.DeleteOneClientScopeAsync<CheckPoint>(dbName, existing.Id);
            c.CheckPointId = null;
            await store.UpdateClientAsync(dbName, c);
        }
    }
}