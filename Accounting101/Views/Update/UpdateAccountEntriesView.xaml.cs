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
        private Guid _accountId;
        private List<AccountWithInfo>? _otherAccounts;

        public UpdateAccountEntriesView()
        {
            WeakReferenceMessenger.Default.RegisterAll(this);
            DataContext = this;
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client, AccountWithTransactions account)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _accountId = account.Id;
            _otherAccounts = taskFactory.Run(() => dataStore.AccountsForClientAsync(client.Id))?.ToList();
            if (_otherAccounts is null)
            {
                return;
            }

            _otherAccounts.Remove(account);
            FastEntryControl.SetAccountList(_otherAccounts);
            AccountHeaderView.SetInfo(new AccountWithInfo(account, account.Info));
            TransactionList.SetInfo(dataStore, taskFactory, account, _otherAccounts);
            FastEntryControl.EditingStateChanged += (sender, editing) => TransactionList.IsEnabled = !editing;
            UpdateAccountBalance();
        }

        private void UpdateAccountBalance()
        {
            AccountHeaderView.CurrentBalance = _taskFactory.Run(() => _dataStore.GetAccountBalanceAsync(_accountId));
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
                        ledgerLine.OtherAccountInfo);
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
                    Transaction t = new(wasCredited ? _accountId : otherAccount.Value, wasCredited ? otherAccount.Value : _accountId, amount, line.When);
                    _taskFactory.Run(() => _dataStore.CreateTransactionAsync(t));
                    WeakReferenceMessenger.Default.Send(new UpdateTransactionLayoutMessage(null));
                    break;
            }
        }
    }
}