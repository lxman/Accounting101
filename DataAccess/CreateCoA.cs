using System;
using DataAccess.CoATemplates;
using DataAccess.Models;
using DataAccess.Services.Interfaces;

namespace DataAccess
{
    public static class CreateCoA
    {
        public static void CreateChart(this IDataStore dataStore, AvailableCoAs type, Client c)
        {
            switch (type)
            {
                case AvailableCoAs.SmallBusiness:
                    ChartOfAccounts accts = SmallBusiness.CreateCoA(c);
                    accts.Accounts.ForEach(a => dataStore.CreateAccountAsync(a));
                    break;
                default:
                    throw new ArgumentException($"CoA type {type} not found.");
                    break;
            }
        }
    }
}
