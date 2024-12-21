using System.Collections.ObjectModel;
using Accounting101.Controls;
using Accounting101.Messages;
using Accounting101.Models;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.ViewModels.Single
{
    public class AccountViewModel : BaseViewModel, IRecipient<AddTransactionMessage>, IRecipient<UpdateTransactionMessage>
    {
        public AccountHeaderControl AccountHeaderControl { get; }

        public ObservableCollection<LedgerLineControl> Transactions { get; } = [];

        private readonly JoinableTaskFactory _taskFactory;
        private readonly IDataStore _dataStore;
        private readonly AccountWithTransactions _f;
        private readonly AccountWithInfoFlat _a;

        public AccountViewModel(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            AccountWithTransactions f,
            AccountWithInfoFlat a)
        {
            Messenger.Register<AddTransactionMessage>(this);
            Messenger.Register<UpdateTransactionMessage>(this);
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _f = f;
            _a = a;
            AccountHeaderControl = new AccountHeaderControl(a);
            PopulateTransactionsList();
        }

        public void Receive(AddTransactionMessage message)
        {
            _taskFactory.Run(() => _dataStore.CreateTransactionAsync(message.Value));
            _f.Transactions.Add(message.Value);
            PopulateTransactionsList();
        }

        public void Receive(UpdateTransactionMessage message)
        {
            _taskFactory.Run(() => _dataStore.UpdateTransactionAsync(message.Value));
            _f.Transactions.RemoveAll(t => t.Id == message.Value.Id);
            _f.Transactions.Add(message.Value);
            PopulateTransactionsList();
        }

        public void ShowClientAccountsView()
        {
            Messenger.Send(new ChangeScreenMessage(WindowType.ClientAccountList));
        }

        private void PopulateTransactionsList()
        {
            Transactions.Clear();
            decimal balance = _a.StartBalance;
            List<LedgerLineControl> lines = [];
            _f.Transactions.OrderBy(t => t.When).ToList().ForEach(t =>
            {
                Guid otherAccountId = t.DebitedAccountId == _a.Id ? t.CreditedAccountId : t.DebitedAccountId;
                AccountWithInfo otherAccount = _taskFactory.Run(() => _dataStore.GetAccountWithInfoAsync(otherAccountId))!;
                if (t.CreditedAccountId == _a.Id)
                {
                    if (_a.IsDebitAccount) balance -= t.Amount;
                    else balance += t.Amount;
                }
                else
                {
                    if (_a.IsDebitAccount) balance += t.Amount;
                    else balance -= t.Amount;
                }
                lines.Add(new LedgerLineControl(t, balance, otherAccount));
            });
            lines.ForEach(l => Transactions.Add(l));
        }
    }
}