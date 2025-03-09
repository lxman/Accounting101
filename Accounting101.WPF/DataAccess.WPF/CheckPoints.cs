using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess.WPF;

public static class CheckPoints
{
    public static async Task<bool> SetCheckpointAsync(this IDataStore store, Guid clientId, DateOnly date)
    {
        Client? c = await store.GetClientWithInfoAsync(clientId);
        CheckPoint checkPoint = new(clientId, date);
        IEnumerable<AccountWithInfo>? accounts = await store.AccountsForClientAsync(clientId);
        if (accounts is null || c is null)
        {
            return false;
        }

        List<AccountWithInfo> accountsList = accounts.ToList();
        for (int x = 0; x < accountsList.Count; x++)
        {
            AccountWithInfo a = accountsList.ElementAt(x);
            decimal balance = await store.GetAccountBalanceOnDateAsync(a.Id, date);
            checkPoint.AccountCheckpoints.Add(new AccountCheckpoint(clientId, a.Id, balance));
        }

        ILiteCollectionAsync<CheckPoint>? checkPoints = store.GetCollection<CheckPoint>(CollectionNames.CheckPoint);
        ILiteCollectionAsync<AccountCheckpoint>? accountCheckpoints = store.GetCollection<AccountCheckpoint>(CollectionNames.AccountCheckpoint);
        if (checkPoints is null || accountCheckpoints is null)
        {
            return false;
        }

        Guid? id = null;
        if (await checkPoints.CountAsync() == 0)
        {
            id = (await checkPoints.InsertAsync(checkPoint)).AsGuid;
            await accountCheckpoints.InsertBulkAsync(checkPoint.AccountCheckpoints);
        }
        else
        {
            CheckPoint? existing = await checkPoints.FindOneAsync(cp => cp.ClientId == clientId);
            if (existing is not null)
            {
                await checkPoints.DeleteAsync(existing.Id);
            }
            id = (await checkPoints.InsertAsync(checkPoint)).AsGuid;
            await accountCheckpoints.InsertBulkAsync(checkPoint.AccountCheckpoints);
        }
        c.CheckPointId = id;
        await store.UpdateClientAsync(c);
        return true;
    }

    public static async Task<CheckPoint?> GetCheckpointAsync(this IDataStore store, Guid clientId)
    {
        ILiteCollectionAsync<CheckPoint>? checkPoints = store.GetCollection<CheckPoint>(CollectionNames.CheckPoint);
        ILiteCollectionAsync<AccountCheckpoint>? accountCheckpoints = store.GetCollection<AccountCheckpoint>(CollectionNames.AccountCheckpoint);
        if (checkPoints is null || accountCheckpoints is null)
        {
            return null;
        }

        CheckPoint? checkPoint = await checkPoints.FindOneAsync(cp => cp.ClientId == clientId);
        if (checkPoint is null)
        {
            return null;
        }
        checkPoint.AccountCheckpoints.AddRange(await accountCheckpoints.FindAsync(ac => ac.ClientId == clientId));
        return checkPoint;
    }

    public static async Task ClearCheckpointAsync(this IDataStore store, Guid clientId)
    {
        Client? c = await store.GetClientWithInfoAsync(clientId);
        ILiteCollectionAsync<CheckPoint>? checkPoints = store.GetCollection<CheckPoint>(CollectionNames.CheckPoint);
        if (checkPoints is null || c is null)
        {
            return;
        }
        CheckPoint? existing = await checkPoints.FindOneAsync(cp => cp.ClientId == clientId);
        ILiteCollectionAsync<AccountCheckpoint>? accountCheckpoints = store.GetCollection<AccountCheckpoint>(CollectionNames.AccountCheckpoint);
        if (existing is not null && accountCheckpoints is not null)
        {
            await accountCheckpoints.DeleteManyAsync(ac => ac.ClientId == clientId);
            await checkPoints.DeleteAsync(existing.Id);
            c.CheckPointId = null;
            await store.UpdateClientAsync(c);
        }
    }
}