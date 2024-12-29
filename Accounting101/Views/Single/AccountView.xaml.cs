using System.Collections;
using System.Windows;
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

#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.

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
                _lineBeingEdited.Opacity = 1.0;
                _lineBeingEdited = null;
            };
            _accountViewModel.SetColumnWidths += (s, widths) =>
            {
                DateBlock.Width = widths.DateWidth;
                DebitBlock.Width = widths.DebitWidth;
                CreditBlock.Width = widths.CreditWidth;
                BalanceBlock.Width = widths.BalanceWidth;
            };
        }

        public void Receive(PreviewKeyDownMessage message)
        {
            switch (message.Value)
            {
                case Key.E:
                    if (_currentTransaction is null) return;
                    FastEntryControl.SetForEditing(_currentTransaction);
                    break;

                case Key.Delete:
                    if (_currentTransaction is null || FastEntryControl.IsEditing) return;
                    WeakReferenceMessenger.Default.Send(new DeleteTransactionMessage(_currentTransaction));
                    break;

                case Key.Tab:
                    FastEntryControl.Focus();
                    break;

                case Key.Escape:
                    if (_lineBeingEdited is null) return;
                    _lineBeingEdited.Background = _originalBackground;
                    _lineBeingEdited.Opacity = 1.0;
                    _lineBeingEdited = null;
                    TransactionList.SelectedIndex = -1;
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
                (e.RemovedItems[0] as LedgerLineControl)!.Opacity = 1.0;
            }
            if (e.AddedItems.Count == 0) return;
            if (e.AddedItems[0] is not LedgerLineControl) return;
            _currentTransaction = GetTransaction(e.AddedItems);
            if (_currentTransaction is null || TransactionList.SelectedItem is not LedgerLineControl activeLine) return;
            activeLine.Background = Brushes.LightBlue;
            activeLine.Opacity = 0.5;
            _lineBeingEdited = activeLine;
        }

        private static Transaction? GetTransaction(IList items)
        {
            return (items[0] as LedgerLineControl)?.Transaction;
        }

        private void AccountViewUnloaded(object sender, RoutedEventArgs e)
        {
            _accountViewModel.Unregister();
            WeakReferenceMessenger.Default.Unregister<PreviewKeyDownMessage>(this);
        }
    }
}