using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Accounting101.Messages;
using Accounting101.Models;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.Models;

#pragma warning disable CS8618, CS9264

// ReSharper disable SwitchStatementHandlesSomeKnownEnumValuesWithDefault

// ReSharper disable SwitchStatementMissingSomeEnumCasesNoDefault

namespace Accounting101.Controls
{
    public partial class FastEntryControl : UserControl, IRecipient<PreviewKeyDownMessage>
    {
        public event EventHandler? RevertBackground;

        public bool IsEditing { get; private set; }

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
        private readonly Brush _background;
        private Transaction _transaction;

        public FastEntryControl()
        {
            WeakReferenceMessenger.Default.Register(this);
            DataContext = this;
            InitializeComponent();
            _background = RadioGroup.Background;
        }

        public void Receive(PreviewKeyDownMessage message)
        {
            switch (message.Value)
            {
                case Key.Tab:
                    if (IsEditing)
                    {
                        if (DatePicker.IsKeyboardFocusWithin) RadioGroup.Focus();
                        else if (RadioGroup.IsFocused) AccountSelector.Focus();
                        else if (AccountSelector.IsFocused) AmountEntry.Focus();
                        else if (AmountEntry.IsFocused) AcceptButton.Focus();
                        else if (AcceptButton.IsFocused) DatePicker.Focus();
                    }
                    else
                    {
                        switch (DatePicker.IsKeyboardFocusWithin)
                        {
                            case false
                                when !RadioGroup.IsFocused
                                     && !AccountSelector.IsFocused
                                     && !AmountEntry.IsFocused
                                     && !AcceptButton.IsFocused:
                                DatePicker.Focus();
                                break;

                            case true:
                                RadioGroup.Focus();
                                break;

                            default:
                                {
                                    if (RadioGroup.IsFocused) AccountSelector.Focus();
                                    else if (AccountSelector.IsFocused) AmountEntry.Focus();
                                    else if (AmountEntry.IsFocused) AcceptButton.Focus();
                                    else if (AcceptButton.IsFocused) DatePicker.Focus();
                                    break;
                                }
                        }
                    }
                    break;

                case Key.Escape:
                    if (IsEditing)
                    {
                        Background = _background;
                        ClearControls();
                        IsEditing = false;
                        RevertBackground?.Invoke(this, EventArgs.Empty);
                    }
                    break;

                case Key.Enter:
                    switch (IsEditing)
                    {
                        case true when AmountEntry.IsFocused:
                            SendUpdateTransaction();
                            Background = _background;
                            ClearControls();
                            IsEditing = false;
                            break;

                        case false when AmountEntry.IsFocused:
                            SendCreateTransaction();
                            break;
                    }
                    break;

                case Key.Delete:
                    if (IsEditing && AmountEntry.IsFocused)
                    {
                        Send(message.Value);
                    }
                    break;
            }
        }

        public void SetForEditing(Transaction t)
        {
            _transaction = t;
            Date = t.When.ToDateTime(new TimeOnly());
            if (t.CreditedAccountId == _activeAccountId) CreditSelected = true;
            else DebitSelected = true;
            AccountPickerLine? account = Accounts.FirstOrDefault(a => a.Id == (t.CreditedAccountId != _activeAccountId ? t.CreditedAccountId : t.DebitedAccountId));
            if (account is null) return;
            SelectedAccount = account;
            decimal amount = t.Amount;
            string amtStr = amount.ToString(CultureInfo.InvariantCulture);
            Amount = amtStr;
            Background = Brushes.LightBlue;
            IsEditing = true;
            DatePicker.Focus();
            _actionType = CreditSelected ? "Credit" : "Debit";
        }

        public void LoadAccounts(List<AccountWithInfo> accounts)
        {
            Accounts.Clear();
            foreach (AccountWithInfo account in accounts.OrderBy(a => a.Info.CoAId))
            {
                Accounts.Add(new AccountPickerLine(account));
            }
        }

        public void SetActiveAccount(Guid activeAccountId)
        {
            _activeAccountId = activeAccountId;
        }

        private void ClearControls()
        {
            CreditSelected = false;
            DebitSelected = false;
            AccountSelector.SelectedIndex = -1;
            Amount = string.Empty;
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
            if (e.Key != Key.Enter || IsEditing)
            {
                return;
            }
            e.Handled = true;
            SendCreateTransaction();
        }

        private void AcceptButtonClick(object sender, RoutedEventArgs e)
        {
            if (IsEditing)
            {
                SendUpdateTransaction();
                Background = _background;
                ClearControls();
                IsEditing = false;
            }
            else
            {
                SendCreateTransaction();
            }
        }

        private void SendCreateTransaction()
        {
            Transaction t = new(_actionType == "Credit" ? _activeAccountId : SelectedAccount.Id,
                _actionType == "Credit" ? SelectedAccount.Id : _activeAccountId, Convert.ToDecimal(Amount), DateOnly.FromDateTime(Date));
            WeakReferenceMessenger.Default.Send(new CreateTransactionMessage(t));
            ClearControls();
            DatePicker.Focus();
        }

        private void SendUpdateTransaction()
        {
            Transaction t = new(_actionType == "Credit" ? _activeAccountId : SelectedAccount.Id,
                _actionType == "Credit" ? SelectedAccount.Id : _activeAccountId, Convert.ToDecimal(Amount), DateOnly.FromDateTime(Date))
            {
                Id = _transaction.Id
            };
            WeakReferenceMessenger.Default.Send(new UpdateTransactionMessage(t));
            ClearControls();
        }

        public static void Send(Key key)
        {
            if (Keyboard.PrimaryDevice is null || Keyboard.PrimaryDevice.ActiveSource is null)
            {
                return;
            }

            KeyEventArgs e = new(Keyboard.PrimaryDevice, Keyboard.PrimaryDevice.ActiveSource, 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            InputManager.Current.ProcessInput(e);

            // Note: Based on your requirements you may also need to fire events for:
            // RoutedEvent = Keyboard.PreviewKeyDownEvent
            // RoutedEvent = Keyboard.KeyUpEvent
            // RoutedEvent = Keyboard.PreviewKeyUpEvent
        }
    }
}