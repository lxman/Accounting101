using System;
using System.Linq;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.DataAccess;

public static class Settings
{
    public static async Task<Guid> CreateSettingAsync(this IDataStore store, string dbName, Setting setting)
    {
        Guid result = await store.CreateOneGlobalScopeAsync(dbName, setting);
        store.NotifyChange(typeof(Setting), ChangeType.Created);
        return result;
    }

    public static async Task<Setting?> FindSettingAsync(this IDataStore store, string dbName, string key)
    {
        return (await store.ReadAllGlobalScopeAsync<Setting>(dbName))!.FirstOrDefault(s => s.Key == key);
    }

    public static async Task RemoveSettingAsync(this IDataStore store, string dbName, string key)
    {
        Setting? setting = (await store.ReadAllGlobalScopeAsync<Setting>(dbName))!.FirstOrDefault(s => s.Key == key);
        if (setting is null) return;
        await store.DeleteOneGlobalScopeAsync<Setting>(dbName, setting.Id);
        store.NotifyChange(typeof(Setting), ChangeType.Deleted);
    }
}