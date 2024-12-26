using System.Collections.ObjectModel;
using Accounting101.Messages;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
#pragma warning disable CS8618, CS9264

namespace Accounting101.ViewModels.Update
{
    public class UpdateAccountViewModel : BaseViewModel, IRecipient<SaveMessage>
    {
        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<BaseAccountTypes> AccountTypes { get; }

        public BaseAccountTypes SelectedAccountType
        {
            get => _selectedAccountType;
            set
            {
                _selectedAccountType = value;
                OnPropertyChanged();
            }
        }

        public string CoAId
        {
            get => _coaId;
            set
            {
                _coaId = value;
                OnPropertyChanged();
            }
        }

        public decimal StartBalance
        {
            get => _startBalance;
            set
            {
                _startBalance = value;
                OnPropertyChanged();
            }
        }

        private decimal _startBalance;
        private string _coaId;
        private BaseAccountTypes _selectedAccountType;
        private string _name;
        private IDataStore _dataStore;
        private JoinableTaskFactory _taskFactory;
        private Guid _accountId;
        private Guid _clientId;
        private Guid _infoId;
        private DateOnly _created;

        public UpdateAccountViewModel()
        {
            Messenger.Register(this);
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

        public void SetAccount(IDataStore dataStore, JoinableTaskFactory taskFactory, Guid accountId)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _accountId = accountId;
            AccountWithInfo? accountWithInfo = _taskFactory.Run(() => _dataStore.GetAccountWithInfoAsync(accountId));
            if (accountWithInfo is null) return;
            Name = accountWithInfo.Info.Name;
            SelectedAccountType = accountWithInfo.Type;
            CoAId = accountWithInfo.Info.CoAId;
            StartBalance = accountWithInfo.StartBalance;
            _accountId = accountId;
            _clientId = accountWithInfo.ClientId;
            _infoId = accountWithInfo.InfoId;
            _created = accountWithInfo.Created;
        }

        public void Receive(SaveMessage message)
        {
            if (message.Value != WindowType.EditAccount)
            {
                return;
            }
            Messenger.Unregister<SaveMessage>(this);
            Account account = new()
            {
                Id = _accountId,
                ClientId = _clientId,
                InfoId = _infoId,
                StartBalance = StartBalance,
                Type = SelectedAccountType,
                Created = _created
            };
            AccountInfo info = new()
            {
                Id = _infoId,
                Name = Name,
                CoAId = CoAId
            };
            AccountWithInfo accountWithInfo = new(account, info);
            _taskFactory.Run(() => _dataStore.UpdateAccountAsync(accountWithInfo));
            Messenger.Send(new ChangeScreenMessage(WindowType.ClientAccountList));
        }
    }
}