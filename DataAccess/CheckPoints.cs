using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using LiteDB.Async;

namespace DataAccess
{
    public static class CheckPoints
    {
        public static async Task<bool> SetCheckpointAsync(this IDataStore store, Guid clientId, DateOnly date)
        {
            CheckPoint checkPoint = new(clientId, date);
            IEnumerable<AccountWithInfo>? accounts = await store.AccountsForClientAsync(clientId);
            if (accounts is null)
            {
                return false;
            }

            List<AccountWithInfo> accountsList = accounts.ToList();
            for (int x = 0; x < accountsList.Count; x++)
            {
                AccountWithInfo a = accountsList.ElementAt(x);
                decimal balance = await store.GetAccountBalanceOnDateAsync(a.Id, date);
                checkPoint.AccountCheckpoints.Add(new AccountCheckpoint(a.Id, balance));
            }

            ILiteCollectionAsync<CheckPoint>? checkPoints = store.GetCollection<CheckPoint>(CollectionNames.CheckPoint);
            if (checkPoints is null)
            {
                return false;
            }
            if (await checkPoints.CountAsync() == 0)
            {
                await checkPoints.InsertAsync(checkPoint);
            }
            else
            {
                CheckPoint? existing = await checkPoints.FindOneAsync(cp => cp.ClientId == clientId);
                if (existing is not null)
                {
                    await checkPoints.DeleteAsync(existing.Id);
                }
                await checkPoints.InsertAsync(checkPoint);
            }
            return true;
        }

        public static async Task<CheckPoint?> GetCheckpointAsync(this IDataStore store, Guid clientId)
        {
            ILiteCollectionAsync<CheckPoint>? checkPoints = store.GetCollection<CheckPoint>(CollectionNames.CheckPoint);
            if (checkPoints is null)
            {
                return null;
            }
            return await checkPoints.FindOneAsync(cp => cp.ClientId == clientId);
        }

        public static async Task ClearCheckpointAsync(this IDataStore store, Guid clientId)
        {
            ILiteCollectionAsync<CheckPoint>? checkPoints = store.GetCollection<CheckPoint>(CollectionNames.CheckPoint);
            if (checkPoints is null)
            {
                return;
            }
            CheckPoint? existing = await checkPoints.FindOneAsync(cp => cp.ClientId == clientId);
            if (existing is not null)
            {
                await checkPoints.DeleteAsync(existing.Id);
            }
        }
    }
}
