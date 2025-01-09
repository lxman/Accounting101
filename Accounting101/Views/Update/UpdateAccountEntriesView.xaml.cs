using System.Windows.Controls;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
using Timer = System.Timers.Timer;

#pragma warning disable CS8618, CS9264

namespace Accounting101.Views.Update
{
    public partial class UpdateAccountEntriesView : UserControl
    {
        private IDataStore _dataStore;
        private JoinableTaskFactory _taskFactory;
        private Guid _accountId;
        private readonly Timer _t = new(500);

        public UpdateAccountEntriesView()
        {
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
            List<AccountWithInfo>? otherAccounts = taskFactory.Run(() => dataStore.AccountsForClientAsync(client.Id))?.ToList();
            if (otherAccounts is null)
            {
                return;
            }

            otherAccounts.Remove(account);
            AccountHeaderView.SetInfo(new AccountWithInfo(account, account.Info));
            TransactionList.SetInfo(dataStore, taskFactory, account, otherAccounts);
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
    }
}
