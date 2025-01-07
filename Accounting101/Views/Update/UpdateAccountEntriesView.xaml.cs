using System.Windows.Controls;
using Accounting101.ViewModels.Update;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update
{
    public partial class UpdateAccountEntriesView : UserControl
    {
        private readonly UpdateAccountEntriesViewModel _viewModel = new();

        public UpdateAccountEntriesView()
        {
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, ClientWithInfo client, AccountWithTransactions account)
        {
            _viewModel.SetInfo(dataStore, taskFactory, client, account);
            AccountHeaderView.SetInfo(new AccountWithInfo(account, account.Info));
        }
    }
}
