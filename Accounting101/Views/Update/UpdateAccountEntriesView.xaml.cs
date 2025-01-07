using System.Windows.Controls;
using Accounting101.ViewModels.Update;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
#pragma warning disable CS8618, CS9264

namespace Accounting101.Views.Update
{
    public partial class UpdateAccountEntriesView : UserControl
    {
        private readonly UpdateAccountEntriesViewModel _viewModel = new();

        private IDataStore _dataStore;
        private JoinableTaskFactory _taskFactory;
        private Guid _accountId;

        public UpdateAccountEntriesView()
        {
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client, AccountWithTransactions account)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _accountId = account.Id;
            _viewModel.SetInfo(dataStore, taskFactory, client, account);
            AccountHeaderView.SetInfo(new AccountWithInfo(account, account.Info));
            UpdateAccountBalance();
        }

        private void UpdateAccountBalance()
        {
            AccountHeaderView.CurrentBalance = _taskFactory.Run(() => _dataStore.GetAccountBalanceAsync(_accountId));
        }
    }
}
