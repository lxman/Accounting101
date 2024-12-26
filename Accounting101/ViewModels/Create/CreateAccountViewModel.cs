using System.Collections.ObjectModel;
using Accounting101.Messages;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels.Create
{
    public class CreateAccountViewModel : BaseViewModel, IRecipient<SaveMessage>
    {
        public string Name { get; set; } = string.Empty;

        public ObservableCollection<BaseAccountTypes> AccountTypes { get; }

        public BaseAccountTypes SelectedAccountType { get; set; }

        public string CoAId { get; set; } = string.Empty;

        public decimal StartBalance { get; set; }

        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;
        private readonly Guid _clientId;

        public CreateAccountViewModel(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid clientId)
        {
            Messenger.Register(this);
            _clientId = clientId;
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            AccountTypes = new ObservableCollection<BaseAccountTypes>(
            [
                BaseAccountTypes.Asset,
                BaseAccountTypes.Expense,
                BaseAccountTypes.Liability,
                BaseAccountTypes.Equity,
                BaseAccountTypes.Revenue,
                BaseAccountTypes.Earnings
            ]);
        }

        public void Receive(SaveMessage message)
        {
            if (message.Value != WindowType.CreateAccount)
            {
                return;
            }
            Messenger.Unregister<SaveMessage>(this);
            Account account = new()
            {
                StartBalance = StartBalance,
                ClientId = _clientId,
                Type = SelectedAccountType
            };
            AccountInfo info = new()
            {
                Name = Name,
                CoAId = CoAId
            };
            AccountWithInfo accountWithInfo = new(account, info);
            _taskFactory.Run(() => _dataStore.CreateAccountAsync(accountWithInfo));
            Messenger.Send(new ChangeScreenMessage(WindowType.ClientAccountList));
        }
    }
}