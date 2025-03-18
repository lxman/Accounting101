using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.DataAccess;

public static class PersonNames
{
    public static async Task<Guid> CreateNameAsync(this IDataStore store, string dbName, PersonName name)
    {
        Guid result = await store.CreateOneGlobalScopeAsync(dbName, name);
        if (result != Guid.Empty) store.NotifyChange(typeof(PersonName), ChangeType.Created);
        return result;
    }

    public static async Task BulkInsertNamesAsync(this IDataStore store, string dbName, IEnumerable<PersonName> names)
    {
        await store.CreateManyGlobalScopeAsync(dbName, names);
        store.NotifyChange(typeof(PersonNames), ChangeType.Created);
    }

    public static async Task<IEnumerable<PersonName>?> AllNamesAsync(this IDataStore store, string dbName)
    {
        return await store.ReadAllGlobalScopeAsync<PersonName>(dbName);
    }

    public static async Task<PersonName?> FindNameByIdAsync(this IDataStore store, string dbName, Guid id)
    {
        return (await store.GetAllGlobalScopeAsync<PersonName>(dbName))!.FirstOrDefault(pn => pn.Id == id);
    }

    public static async Task<bool> UpdateNameAsync(this IDataStore store, string dbName, PersonName name)
    {
        bool? result = await store.UpdateOneGlobalScopeAsync(dbName, name);
        return result.HasValue && result.Value;
    }
}