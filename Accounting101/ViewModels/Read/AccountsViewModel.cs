using System.Collections.ObjectModel;
using Accounting101.Messages;
using Accounting101.Models;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels.Read
{
    public class AccountsViewModel : BaseViewModel
    {
        public bool HasAccounts { get; private set; }

        public ReadOnlyObservableCollection<AccountsViewLine> Source { get; private set; }

        public AccountsViewModel()
        {

        }

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
            Source = new ReadOnlyObservableCollection<AccountsViewLine>(new ObservableCollection<AccountsViewLine>(accounts));
            OnPropertyChanged(nameof(Source));
            HasAccounts = accounts.Count > 0;
        }

        public void ItemSelected(int index)
        {
            Guid selectedAccountId = Source[index].Id;
            WeakReferenceMessenger.Default.Send(new ShowAccountTransactionEditor(
                new ShowAccountTransactionMessage() { AccountId = selectedAccountId, Value = true }));
        }
    }
}
