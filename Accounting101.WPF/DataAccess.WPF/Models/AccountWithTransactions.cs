using System;
using System.Collections.Generic;
using System.Linq;
using DataAccess.WPF.Services.Interfaces;
using LiteDB.Async;
using Microsoft.VisualStudio.Threading;

#pragma warning disable VSTHRD002
#pragma warning disable CS8618, CS9264

namespace DataAccess.WPF.Models;

public class AccountWithTransactions : AccountWithInfo
{
    public List<Transaction> Transactions { get; }

    public AccountWithTransactions(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid accountId)
    {
        Id = accountId;
        Info = new AccountInfo();
        ILiteCollectionAsync<Transaction>? txDb = dataStore.GetCollection<Transaction>(CollectionNames.Transaction);
        AccountWithInfo? accountWithInfo = taskFactory.Run(() => dataStore.GetAccountWithInfoAsync(accountId));
        if (txDb is null || accountWithInfo is null) return;
        Info.Name = accountWithInfo.Info.Name;
        Info.CoAId = accountWithInfo.Info.CoAId;
        Info.Id = accountWithInfo.Info.Id;
        Type = accountWithInfo.Type;
        InfoId = accountWithInfo.InfoId;
        StartBalance = accountWithInfo.StartBalance;
        Created = accountWithInfo.Created;
        ClientId = accountWithInfo.ClientId;
        Transactions = taskFactory.Run(() => txDb.FindAllAsync()).Where(tx => tx.DebitedAccountId == Id || tx.CreditedAccountId == Id).ToList();
    }
}