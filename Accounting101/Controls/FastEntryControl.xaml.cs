﻿using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Accounting101.Messages;
using Accounting101.Models;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.Models;
// ReSharper disable SwitchStatementHandlesSomeKnownEnumValuesWithDefault

// ReSharper disable SwitchStatementMissingSomeEnumCasesNoDefault

namespace Accounting101.Controls
{
    public partial class FastEntryControl : UserControl
    {
        public ObservableCollection<AccountPickerLine> Accounts { get; } = [];

        public static readonly DependencyProperty SelectedAccountProperty = DependencyProperty.Register(
            nameof(SelectedAccount), typeof(AccountPickerLine), typeof(FastEntryControl), new PropertyMetadata(default(AccountPickerLine)));

        public AccountPickerLine SelectedAccount
        {
            get => (AccountPickerLine)GetValue(SelectedAccountProperty);
            set => SetValue(SelectedAccountProperty, value);
        }

        public static readonly DependencyProperty CreditSelectedProperty = DependencyProperty.Register(
            nameof(CreditSelected), typeof(bool), typeof(FastEntryControl), new PropertyMetadata(false));

        public bool CreditSelected
        {
            get => (bool)GetValue(CreditSelectedProperty);
            set => SetValue(CreditSelectedProperty, value);
        }

        public static readonly DependencyProperty DebitSelectedProperty = DependencyProperty.Register(
            nameof(DebitSelected), typeof(bool), typeof(FastEntryControl), new PropertyMetadata(false));

        public bool DebitSelected
        {
            get => (bool)GetValue(DebitSelectedProperty);
            set => SetValue(DebitSelectedProperty, value);
        }

        public static readonly DependencyProperty DateProperty = DependencyProperty.Register(
            nameof(Date), typeof(DateTime), typeof(FastEntryControl), new PropertyMetadata(default(DateTime)));

        public DateTime Date
        {
            get => (DateTime)GetValue(DateProperty);
            set => SetValue(DateProperty, value);
        }

        public static readonly DependencyProperty AmountProperty = DependencyProperty.Register(
            nameof(Amount), typeof(string), typeof(FastEntryControl), new PropertyMetadata(default(string)));

        public string Amount
        {
            get => (string)GetValue(AmountProperty);
            set => SetValue(AmountProperty, value);
        }

        private string _actionType = string.Empty;
        private bool _watchForAction;
        private Guid _activeAccountId;

        public FastEntryControl()
        {
            DataContext = this;
            InitializeComponent();
        }

        public void SetForEditing(Transaction t)
        {
            Date = t.When;
            if (t.CreditedAccountId == _activeAccountId) CreditSelected = true;
            else DebitSelected = true;
            AccountPickerLine? account = Accounts.FirstOrDefault(a => a.Id == (t.CreditedAccountId != _activeAccountId ? t.CreditedAccountId : t.DebitedAccountId));
            if (account is null) return;
            SelectedAccount = account;
            decimal amount = t.Amount;
            string amtStr = amount.ToString(CultureInfo.InvariantCulture);
            Amount = amtStr;
        }

        public void LoadAccounts(List<AccountWithInfo> accounts)
        {
            Accounts.Clear();
            foreach (AccountWithInfo account in accounts)
            {
                Accounts.Add(new AccountPickerLine(account));
            }
        }

        public void SetActiveAccount(Guid activeAccountId)
        {
            _activeAccountId = activeAccountId;
        }

        private void RadioBoxGotFocus(object sender, RoutedEventArgs e)
        {
            _watchForAction = true;
        }

        private void RadioBoxLostFocus(object sender, RoutedEventArgs e)
        {
            _watchForAction = false;
        }

        private void RadioBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_watchForAction)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.C:
                    _actionType = "Credit";
                    CreditSelected = true;
                    break;
                case Key.D:
                    _actionType = "Debit";
                    DebitSelected = true;
                    break;
                default:
                    return;
            }
            AccountSelector.Focus();
        }

        private void AmountTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            e.Handled = true;
            SendTransaction();
        }

        private void AcceptButtonClick(object sender, RoutedEventArgs e)
        {
            SendTransaction();
        }

        private void SendTransaction()
        {
            Transaction t = new(_actionType == "Credit" ? _activeAccountId : SelectedAccount.Id,
                _actionType == "Credit" ? SelectedAccount.Id : _activeAccountId, Convert.ToDecimal(Amount), Date);
            WeakReferenceMessenger.Default.Send(new AddTransactionMessage(t));
            DatePicker.Focus();
        }
    }
}