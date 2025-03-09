using System;
using System.Threading.Tasks;
using DataAccess.WPF.CoATemplates;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;

namespace DataAccess.WPF;

public static class CreateCoA
{
    public static async Task CreateChartAsync(this IDataStore dataStore, AvailableCoAs type, Client c)
    {
        switch (type)
        {
            case AvailableCoAs.SmallBusiness:
                ChartOfAccounts accts = SmallBusiness.CreateCoA(c);
                foreach (AccountWithInfo a in accts.Accounts)
                {
                    await dataStore.CreateAccountAsync(a);
                }
                dataStore.NotifyChange(typeof(Accounts), ChangeType.Created);
                break;

            default:
                throw new ArgumentException($"CoA type {type} not found.");
        }
    }
}