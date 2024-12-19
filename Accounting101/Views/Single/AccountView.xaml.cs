using System.Windows.Controls;
using System.Windows.Input;
using Accounting101.Controls;
using Accounting101.Models;
using Accounting101.ViewModels.Single;
using DataAccess;
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
        private readonly AccountViewModel _accountViewModel;

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
            _accountViewModel = new AccountViewModel(dataStore, taskFactory, a, f);
            DataContext = _accountViewModel;
            InitializeComponent();
            List<AccountWithInfo> accounts = GetAccounts();
            accounts.RemoveAll(acct => acct.Id == a.Id);
            FastEntryControl.LoadAccounts(accounts);
            FastEntryControl.SetActiveAccount(awi.Id);
        }

        private void AccountViewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _accountViewModel.ShowClientAccountsView();
        }

        private void AccountViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                FastEntryControl.Focus();
            }
        }

        private List<AccountWithInfo> GetAccounts()
        {
            return _taskFactory.Run(() => _dataStore.AccountsForClientAsync(_awi.ClientId))!.ToList();
        }

        private void ListViewSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Guid thisAccount = _awi.Id;
            if (e.AddedItems[0] is not LedgerLineControl ledgerLineControl) return;
            if (ledgerLineControl.OtherAccount.DataContext is not CollapsibleAccountViewModel collapsibleAccountViewModel) return;
            AccountWithInfo otherAccountInfo = collapsibleAccountViewModel.Account;
            bool otherAccountWasCredited = collapsibleAccountViewModel.Header.StartsWith("Credit");
            Transaction t = new(
                otherAccountWasCredited ? otherAccountInfo.Id : thisAccount,
                otherAccountWasCredited ? thisAccount : otherAccountInfo.Id,
                ledgerLineControl.Amount, ledgerLineControl.Date.ToDateTime(new TimeOnly()));
            FastEntryControl.SetForEditing(t);
        }
    }
}