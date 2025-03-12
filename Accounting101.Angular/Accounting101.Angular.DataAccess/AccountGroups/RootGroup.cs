using System;
using System.Threading.Tasks;
using Accounting101.Angular.DataAccess.Models;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.DataAccess.AccountGroups;

public class RootGroup(IDataStore dataStore)
{
    public Guid ClientId { get; set; }

    public AccountGroup Assets { get; set; } = new();

    public AccountGroup Liabilities { get; set; } = new();

    public AccountGroup Equity { get; set; } = new();

    public AccountGroup Revenue { get; set; } = new();

    public AccountGroup Expenses { get; set; } = new();

    public AccountGroup Earnings { get; set; } = new();

    public async Task SaveLayoutAsync(Guid dbName, Guid clientId)
    {
        await dataStore.SaveRootGroupAsync(dbName.ToString(), clientId, this);
    }

    public async Task LoadLayoutAsync(Guid dbName, Guid clientId)
    {
        RootGroup rootGroup = await dataStore.GetRootGroupAsync(dbName.ToString(), clientId);
        Assets = rootGroup.Assets;
        Liabilities = rootGroup.Liabilities;
        Equity = rootGroup.Equity;
        Revenue = rootGroup.Revenue;
        Expenses = rootGroup.Expenses;
    }
}