using System;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.CoATemplates;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.DataAccess;

public static class CreateCoA
{
    public static async Task CreateChartAsync(this IDataStore dataStore, string dbName, AvailableCoAs type, Client c)
    {
        switch (type)
        {
            case AvailableCoAs.SmallBusiness:
                ChartOfAccounts accts = SmallBusiness.CreateCoA(c);
                foreach (AccountWithInfo a in accts.Accounts)
                {
                    await dataStore.CreateAccountAsync(dbName, a);
                }
                dataStore.NotifyChange(typeof(Accounts), ChangeType.Created);
                break;

            default:
                throw new ArgumentException($"CoA type {type} not found.");
        }
    }
}