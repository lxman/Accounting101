using System.Windows.Controls;
using System.Windows.Input;
using Accounting101.Messages;
using Accounting101.Models;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

#pragma warning disable CS8618, CS9264

namespace Accounting101.Views.Update
{
    public partial class UpdateAccountEntriesView : UserControl, IRecipient<KeyPressedMessage>
    {
        private IDataStore _dataStore;
        private JoinableTaskFactory _taskFactory;
        private List<AccountWithInfo>? _otherAccounts;
        private AccountWithEverything _account;

        public UpdateAccountEntriesView()
        {
            WeakReferenceMessenger.Default.RegisterAll(this);
            DataContext = this;
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client, AccountWithEverything account)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _account = account;
            _otherAccounts = taskFactory.Run(() => dataStore.AccountsForClientAsync(client.Id))?.ToList();
            if (_otherAccounts is null)
            {
                return;
            }

            _otherAccounts.Remove(new AccountWithInfo(account.Account, account.Info));
            FastEntryControl.SetAccountList(_otherAccounts);
            FastEntryControl.SetMinDate(account.Account.Created);
            AccountHeaderView.SetInfo(account);
            TransactionList.SetInfo(dataStore, taskFactory, account, _otherAccounts);
            FastEntryControl.EditingStateChanged += (sender, editing) => TransactionList.IsEnabled = !editing;
            FastEntryControl.ErrorOccurred +=
                (sender, error) => taskFactory.Run(() => dataStore.CreateAuditEntryAsync(error));
            UpdateAccountBalance();
        }

        private void UpdateAccountBalance()
        {
            AccountHeaderView.CurrentBalance = _taskFactory.Run(() => _dataStore.GetAccountBalanceAsync(_account.Account.Id));
        }

        public void Receive(KeyPressedMessage message)
        {
            switch (message.Value)
            {
                case Key.C:
                case Key.D:
                case Key.Tab:
                    FastEntryControl.KeyPressed(message.Value);
                    break;
                case Key.E:
                    TransactionInfoLine? ledgerLine = TransactionList.GetSelected();
                    if (ledgerLine is null)
                    {
                        return;
                    }

                    TransactionInfoLine til = new(
                        ledgerLine.Id,
                        ledgerLine.When,
                        ledgerLine.Credit,
                        ledgerLine.Debit,
                        ledgerLine.Balance,
                        ledgerLine.OtherAccountInfo,
                        true);
                    FastEntryControl.EditEntry(til);
                    break;

                case Key.N:
                    FastEntryControl.CreateNew();
                    break;

                case Key.Escape:
                    FastEntryControl.AbortEdit();
                    break;

                case Key.Enter:
                    TransactionInfoLine? line = FastEntryControl.EnterPressed();
                    if (line is null)
                    {
                        return;
                    }
                    Guid? otherAccount = _otherAccounts?
                        .FirstOrDefault(a => a.Info.CoAId == line.OtherAccountInfo.Split(' ')[0])?.Id;
                    if (otherAccount is null)
                    {
                        return;
                    }

                    bool wasCredited = line.Credit.HasValue;
                    decimal amount = line.Credit ?? line.Debit ?? 0;
                    Transaction t = new(wasCredited ? _account.Account.Id : otherAccount.Value, wasCredited ? otherAccount.Value : _account.Account.Id, amount, line.When);
                    _taskFactory.Run(() => _dataStore.CreateTransactionAsync(t));
                    UpdateAccountBalance();
                    WeakReferenceMessenger.Default.Send(new UpdateTransactionLayoutMessage(null));
                    break;
            }
        }
    }
}