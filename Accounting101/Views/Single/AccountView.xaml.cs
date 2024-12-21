using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Accounting101.Controls;
using Accounting101.Messages;
using Accounting101.Models;
using Accounting101.ViewModels.Single;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services.Interfaces;
using Microsoft.VisualStudio.Threading;

// ReSharper disable SwitchStatementMissingSomeEnumCasesNoDefault

namespace Accounting101.Views.Single
{
    public partial class AccountView : UserControl, IRecipient<PreviewKeyDownMessage>
    {
        private readonly IDataStore _dataStore;
        private readonly JoinableTaskFactory _taskFactory;
        private readonly AccountWithInfo _awi;
        private readonly AccountViewModel _accountViewModel;
        private Transaction? _currentTransaction;
        private LedgerLineControl? _lineBeingEdited;
        private readonly Brush _originalBackground;

        public AccountView(
            IDataStore dataStore,
            JoinableTaskFactory taskFactory,
            AccountWithTransactions a,
            AccountWithInfoFlat f,
            AccountWithInfo awi)
        {
            WeakReferenceMessenger.Default.Register(this);
            _dataStore = dataStore;
            _taskFactory = taskFactory;
            _awi = awi;
            _accountViewModel = new AccountViewModel(dataStore, taskFactory, a, f);
            DataContext = _accountViewModel;
            InitializeComponent();
            _originalBackground = TransactionList.Background;
            List<AccountWithInfo> accounts = GetAccounts();
            accounts.RemoveAll(acct => acct.Id == a.Id);
            FastEntryControl.LoadAccounts(accounts);
            FastEntryControl.SetActiveAccount(awi.Id);
            FastEntryControl.RevertBackground += (sender, args) =>
            {
                if (_lineBeingEdited is null)
                {
                    return;
                }

                _lineBeingEdited.Background = _originalBackground;
                _lineBeingEdited = null;
            };
        }

        public void Receive(PreviewKeyDownMessage message)
        {
            switch (message.Value)
            {
                case Key.Tab:
                    FastEntryControl.Focus();
                    break;
            }
        }

        private void AccountViewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _accountViewModel.ShowClientAccountsView();
        }

        private List<AccountWithInfo> GetAccounts()
        {
            return _taskFactory.Run(() => _dataStore.AccountsForClientAsync(_awi.ClientId))!.ToList();
        }

        private void ListViewSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.RemovedItems.Count > 0)
            {
                (e.RemovedItems[0] as LedgerLineControl)!.Background = _originalBackground;
            }
            Guid thisAccount = _awi.Id;
            if (e.AddedItems[0] is not LedgerLineControl { OtherAccountInfo: { } accountWithInfo, Transaction: { } transaction } ledgerLineControl) return;
            bool otherAccountWasCredited = transaction.CreditedAccountId == accountWithInfo.Id;
            Transaction t = new(
                otherAccountWasCredited ? accountWithInfo.Id : thisAccount,
                otherAccountWasCredited ? thisAccount : accountWithInfo.Id,
                ledgerLineControl.Debit ?? ledgerLineControl.Credit ?? 0, ledgerLineControl.Date.ToDateTime(new TimeOnly()))
            { Id = ledgerLineControl.TransactionId };
            _currentTransaction = t;
            FastEntryControl.SetForEditing(_currentTransaction);
            if (TransactionList.SelectedItem is not LedgerLineControl activeLine) return;
            activeLine.Background = Brushes.GreenYellow;
            _lineBeingEdited = activeLine;
        }
    }
}