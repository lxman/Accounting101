using System.Windows.Controls;
using System.Windows.Input;
using Accounting101.Controls;
using Accounting101.Messages;
using Accounting101.Models;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
using Timer = System.Timers.Timer;

#pragma warning disable CS8618, CS9264

namespace Accounting101.Views.Update
{
    public partial class UpdateAccountEntriesView : UserControl, IRecipient<KeyPressedMessage>
    {
        private IDataStore _dataStore;
        private JoinableTaskFactory _taskFactory;
        private Guid _accountId;
        private readonly Timer _t = new(500);
        private List<AccountWithInfo>? _otherAccounts;

        public UpdateAccountEntriesView()
        {
            WeakReferenceMessenger.Default.RegisterAll(this);
            _t.Elapsed += TimerElapsed;
            DataContext = this;
            InitializeComponent();
            SizeChanged += (s, e) => PerformLayout();
        }

        private void TimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            _t.Stop();
            TransactionList.PerformLayout();
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
            UpdateAccountBalance();
        }

        private void PerformLayout()
        {
            if (_t.Enabled) return;
            _t.Start();
        }

        private void UpdateAccountBalance()
        {
            AccountHeaderView.CurrentBalance = _taskFactory.Run(() => _dataStore.GetAccountBalanceAsync(_accountId));
        }

        public void Receive(KeyPressedMessage message)
        {
            switch (message.Value)
            {
                case Key.E:
                    LedgerLineControl? ledgerLine = TransactionList.GetSelected();
                    if (ledgerLine is null)
                    {
                        return;
                    }

                    TransactionInfoLine til = new(
                        ledgerLine.Id,
                        DateOnly.Parse(ledgerLine.When),
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

                case Key.Tab:
                    FastEntryControl.TabPressed();
                    break;
            }
        }
    }
}