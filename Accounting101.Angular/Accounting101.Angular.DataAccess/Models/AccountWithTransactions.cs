using System;
using System.Collections.Generic;
using System.Linq;
using Accounting101.Angular.DataAccess.Extensions;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable VSTHRD002
#pragma warning disable CS8618, CS9264

namespace Accounting101.Angular.DataAccess.Models;

public class AccountWithTransactions : AccountWithInfo
{
    public List<Transaction> Transactions { get; }

    public AccountWithTransactions(IDataStore dataStore, string dbName, JoinableTaskFactory taskFactory, Guid accountId)
    {
        Id = accountId;
        Info = new AccountInfo();
        AccountWithInfo? accountWithInfo = taskFactory.Run(() => dataStore.GetAccountWithInfoAsync(dbName, accountId));
        if (accountWithInfo is null) return;
        Info.Name = accountWithInfo.Info.Name;
        Info.CoAId = accountWithInfo.Info.CoAId;
        Info.Id = accountWithInfo.Info.Id;
        Type = accountWithInfo.Type;
        InfoId = accountWithInfo.InfoId;
        StartBalance = accountWithInfo.StartBalance;
        Created = accountWithInfo.Created;
        ClientId = accountWithInfo.ClientId;
        Transactions = taskFactory.Run(() => dataStore.ReadAllAsync<Transaction>(dbName))?.Where(tx => tx.DebitedAccountId == Id || tx.CreditedAccountId == Id).ToList()!;
    }
}