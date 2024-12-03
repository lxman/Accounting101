using System.Windows.Controls;
using System.Windows.Input;
using Accounting101.Models;
using Accounting101.ViewModels;
using Accounting101.Views.Create;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

namespace Accounting101.Views.Single
{
    public partial class AccountView : UserControl
    {
        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;
        private readonly AccountWithInfo _awi;

        public AccountView(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            AccountWithTransactions a,
            AccountWithInfoFlat f,
            AccountWithInfo awi)
        {
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _awi = awi;
            DataContext = new AccountViewModel(a, f, awi);
            InitializeComponent();
        }

        private void AccountViewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            CreateTransactionView createTransactionView = new(_dataStore, _taskFactory, _awi.ClientId, _awi.Id);
            UtilityDialog utilityDialog = new(createTransactionView) { Height = 100 };
            utilityDialog.ShowDialog();
            if (utilityDialog.DialogResult == true)
            {
                AccountViewModel avm = (AccountViewModel)DataContext;
            }
        }
    }
}