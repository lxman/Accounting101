using System;
using System.Data;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.AccountGroups;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Accounting101.Angular.DataAccess;

public static class RootGroups
{
    public static async Task<RootGroup> InitializeRootGroupAsync(this IDataStore dataStore, string dbName, Guid clientId)
    {
        IMongoCollection<RootGroup>? rootGroups = dataStore.GetCollection<RootGroup>(dbName, CollectionNames.RootGroup);
        if (rootGroups is null)
        {
            throw new DataException("Error accessing the RootGroups collection.");
        }
        var rootGroup = new RootGroup { ClientId = clientId };
        await rootGroups.InsertOneAsync(rootGroup);
        return rootGroup;
    }

    public static async Task<RootGroup> GetRootGroupAsync(this IDataStore dataStore, string dbName, Guid clientId)
    {
        return await dataStore.GetCollection<RootGroup>(dbName, CollectionNames.RootGroup)
            .AsQueryable().FirstOrDefaultAsync(rg => rg.ClientId == clientId) ?? await dataStore.InitializeRootGroupAsync(dbName, clientId);
    }

    public static async Task<bool> SaveRootGroupAsync(this IDataStore dataStore, string dbName, Guid clientId, RootGroup rootGroup)
    {
        ReplaceOneResult result = await dataStore.GetCollection<RootGroup>(dbName, CollectionNames.RootGroup)
            .ReplaceOneAsync(rg => rg.ClientId == clientId, rootGroup, new ReplaceOptions { IsUpsert = true });
        return result.IsAcknowledged && result.ModifiedCount == 1;
    }

    public static async Task<bool> DeleteRootGroupAsync(this IDataStore dataStore, string dbName, Guid clientId)
    {
        DeleteResult result = await dataStore.GetCollection<RootGroup>(dbName, CollectionNames.RootGroup)
            .DeleteOneAsync(rg => rg.ClientId == clientId);
        return result.IsAcknowledged && result.DeletedCount == 1;
    }
}