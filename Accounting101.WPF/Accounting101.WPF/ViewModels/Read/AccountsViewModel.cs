using System.Collections.ObjectModel;
using Accounting101.WPF.Messages;
using Accounting101.WPF.Models;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF.ViewModels.Read;

public class AccountsViewModel : BaseViewModel
{
    public bool HasAccounts { get; private set; }

    public ReadOnlyObservableCollection<AccountsViewLine> Source { get; private set; }

    public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client)
    {
        List<AccountsViewLine> accounts = [];
        List<AccountWithInfo>? accountsWithInfo = taskFactory.Run(() => dataStore.AccountsForClientAsync(client.Id))?.ToList();
        if (accountsWithInfo is null)
        {
            return;
        }
        accountsWithInfo.ForEach(awi =>
        {
            AccountWithTransactions awt = new(dataStore, taskFactory, awi.Id);
            AccountsViewLine avl = new()
            {
                Id = awt.Id,
                CoAId = awt.Info.CoAId,
                Created = awt.Created,
                Name = awt.Info.Name,
                StartBalance = awt.StartBalance,
                Type = awt.Type,
                CurrentBalance = taskFactory.Run(() => dataStore.GetAccountBalanceAsync(awt.Id))
            };
            accounts.Add(avl);
        });
        Source = new ReadOnlyObservableCollection<AccountsViewLine>(new ObservableCollection<AccountsViewLine>(accounts.OrderBy(a => a.CoAId)));
        OnPropertyChanged(nameof(Source));
        HasAccounts = accounts.Count > 0;
    }

    public void ItemSelected(Guid accountId)
    {
        WeakReferenceMessenger.Default.Send(new ShowAccountTransactionEditor(
            new ShowAccountTransactionMessage { AccountId = accountId, Value = true }));
    }
}