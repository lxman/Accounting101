using System.Windows.Controls;
using Accounting101.ViewModels.Update;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Update
{
    public partial class UpdateAccountTransactionsView : UserControl
    {
        private readonly UpdateAccountTransactionsViewModel _viewModel = new();

        public UpdateAccountTransactionsView()
        {
            DataContext = _viewModel;
            InitializeComponent();
        }

        public void SetInfo(IDataStore dataStore, JoinableTaskFactory taskFactory, AccountWithTransactions account, List<AccountWithInfo> otherAccounts)
        {
            _viewModel.SetInfo(dataStore, taskFactory, account, otherAccounts);
        }
    }
}
