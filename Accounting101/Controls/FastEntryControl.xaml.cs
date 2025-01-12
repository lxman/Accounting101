using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Accounting101.Messages;
using Accounting101.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.Models;

// ReSharper disable SwitchStatementMissingSomeEnumCasesNoDefault
#pragma warning disable CS0169 // Field is never used
#pragma warning disable CS8618, CS9264

namespace Accounting101.Controls
{
    [ObservableObject]
    public partial class FastEntryControl : UserControl
    {
        public event EventHandler<bool>? EditingStateChanged;

        public ReadOnlyObservableCollection<string>? OtherAccounts { get; private set; }

        public DateOnly When
        {
            get => _when;
            set => SetProperty(ref _when, value);
        }

        public bool Credit
        {
            get => _credit;
            set => SetProperty(ref _credit, value);
        }

        public bool Debit
        {
            get => _debit;
            set => SetProperty(ref _debit, value);
        }

        public string? OtherAccount
        {
            get => _otherAccount;
            set => SetProperty(ref _otherAccount, value);
        }

        public string Amount
        {
            get => _amount.ToString(CultureInfo.CurrentCulture);
            set
            {
                if (decimal.TryParse(value, out decimal result))
                {
                    SetProperty(ref _amount, result);
                }
                else
                {
                    SetProperty(ref _amount, 0);
                }
            }
        }

        private Guid _id;
        private DateOnly _when;
        private bool _credit;
        private bool _debit;
        private string? _otherAccount = string.Empty;
        private decimal _amount;
        private bool _editing;

        public FastEntryControl()
        {
            DataContext = this;
            InitializeComponent();
            When = DateOnly.FromDateTime(DateTime.Now);
        }

        public void SetAccountList(List<AccountWithInfo> otherAccounts)
        {
            List<string> others = [];
            otherAccounts.ForEach(a =>
            {
                others.Add($"{a.Info.CoAId} {a.Info.Name} {a.Type}");
            });
            others = others.Order().ToList();
            OtherAccounts ??= new ReadOnlyObservableCollection<string>(new ObservableCollection<string>(others));
            OnPropertyChanged(nameof(OtherAccounts));
        }

        public void CreateNew()
        {
            if (_editing)
            {
                return;
            }
            DatePicker.Focus();
            SetEditingState(true, "creating");
        }

        public void EditEntry(TransactionInfoLine entry)
        {
            if (_editing)
            {
                return;
            }
            _id = entry.Id;
            When = entry.When;
            Credit = entry.Credit.HasValue;
            Debit = entry.Debit.HasValue;
            OtherAccount = entry.OtherAccountInfo;
            Amount = entry.Credit.HasValue ? entry.Credit.Value.ToString(CultureInfo.CurrentCulture) : entry.Debit.HasValue ? entry.Debit.Value.ToString(CultureInfo.CurrentCulture) : "0";
            SetEditingState(true);
        }

        public void AbortEdit()
        {
            ClearControls();
            SetEditingState(false);
        }

        public void KeyPressed(Key key)
        {
            switch (key)
            {
                case Key.C:
                    Credit = true;
                    break;

                case Key.D:
                    Debit = true;
                    break;

                case Key.Tab:
                    if (DatePicker.IsKeyboardFocusWithin)
                    {
                        CreditButton.Focus();
                    }
                    else if (CreditButton.IsFocused)
                    {
                        DebitButton.Focus();
                    }
                    else if (DebitButton.IsFocused)
                    {
                        AccountSelector.Focus();
                    }
                    else if (AccountSelector.IsFocused)
                    {
                        AmountBox.Focus();
                    }
                    else if (AmountBox.IsFocused)
                    {
                        AcceptButton.Focus();
                    }
                    else if (AcceptButton.IsFocused)
                    {
                        DatePicker.Focus();
                    }
                    break;
            }
        }

        public TransactionInfoLine? EnterPressed()
        {
            if (AmountBox.IsFocused || AcceptButton.IsFocused)
            {
                TransactionInfoLine line = new(_id, When, Credit ? _amount : null, Debit ? _amount : null, 0, OtherAccount ?? string.Empty, true);
                SetEditingState(false);
                return line;
            }

            if (DatePicker.IsFocused)
            {
                DatePicker.IsDropDownOpen = true;
            }

            if (AccountSelector.IsFocused)
            {
                AccountSelector.IsDropDownOpen = true;
            }
            return null;
        }

        private void SetEditingState(bool state, string type = "")
        {
            EditingStateChanged?.Invoke(this, state);
            if (!state)
            {
                ClearControls();
            }
            _editing = state;
            Background = state ? type == "creating" ? Brushes.LightGreen : Brushes.PaleVioletRed : Brushes.Transparent;
            WeakReferenceMessenger.Default.Send(new EditingTransactionMessage(state));
        }

        private void ClearControls()
        {
            When = DateOnly.FromDateTime(DateTime.Now);
            Credit = false;
            Debit = false;
            OtherAccount = null;
            Amount = string.Empty;
        }
    }
}