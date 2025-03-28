using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.AccountGroups;
using Accounting101.Angular.DataAccess.CoATemplates;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.DataAccess;

public static class CreateCoA
{
    public static async Task<bool> CreateChartAsync(this IDataStore dataStore, string dbName, AvailableCoAs type, Client c)
    {
        switch (type)
        {
            case AvailableCoAs.SmallBusiness:
                RootGroup group = await dataStore.GetRootGroupAsync(dbName, c.Id.ToString());
                await SmallBusiness.CreateCoAAsync(dataStore, dbName, c, group);
                dataStore.NotifyChange(typeof(Accounts), ChangeType.Created);
                return true;

            default:
                return false;
        }
    }
}