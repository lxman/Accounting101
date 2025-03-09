using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Accounting101.WPF.Extensions;
using Accounting101.WPF.Messages;
using Accounting101.WPF.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using ControlzEx.Theming;
using DataAccess.WPF.Models;
using DataAccess.WPF.Models.Auditing;
using Timer = System.Timers.Timer;

// ReSharper disable MemberCanBeMadeStatic.Local
// ReSharper disable SwitchStatementMissingSomeEnumCasesNoDefault
#pragma warning disable CS0169 // Field is never used
#pragma warning disable CS8618, CS9264
#pragma warning disable VSTHRD110

namespace Accounting101.WPF.Controls;

[ObservableObject]
public partial class FastEntryControl
{
    public event EventHandler<AuditEntry>? ErrorOccurred;

    public event EventHandler<bool>? EditingStateChanged;

    public ReadOnlyObservableCollection<string>? OtherAccounts { get; private set; }

    public Brush DatePickerBackground
    {
        get => _datePickerBackground;
        set => SetProperty(ref _datePickerBackground, value);
    }

    public DateOnly When
    {
        get => _when;
        set
        {
            DateTime date = value.ToDateTime(new TimeOnly());
            if (date < DatePicker.DisplayDateStart)
            {
                DatePicker.DisplayDate = DatePicker.DisplayDateStart ?? DateTime.Today;
                DatePicker.Text = DatePicker.DisplayDate.ToShortDateString();
                FlashDateBackgroundAsync(date);
            }
            else if (date > DatePicker.DisplayDateEnd)
            {
                DatePicker.DisplayDate = DatePicker.DisplayDateEnd ?? DateTime.Today;
                DatePicker.Text = DatePicker.DisplayDate.ToShortDateString();
            }
            else
            {
                SetProperty(ref _when, value);
            }
        }
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

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public bool Editing { get; private set; }

    public bool EditingNew { get; private set; }

    private Guid _id;
    private DateOnly _when;
    private bool _credit;
    private bool _debit;
    private string? _otherAccount = string.Empty;
    private decimal _amount;
    private bool _enabled;
    private Brush _datePickerBackground;
    private readonly ToolTip _tooltip = new() { Content = "Transactions cannot predate the creation of the account" };

    public FastEntryControl()
    {
        DataContext = this;
        InitializeComponent();
        When = DateOnly.FromDateTime(DateTime.Now);
        TypeConverter brushConverter = new BrushConverter();
        Brush dark = (Brush)brushConverter.ConvertFromString("#FF252525")!;
        DatePickerBackground = LightTheme() ? Brushes.White : dark;
        DatePicker.ToolTip = _tooltip;
    }

    public Task FlashDateBackgroundAsync(DateTime date)
    {
        string accountCreationDate = DatePicker.DisplayDateStart.HasValue ? DatePicker.DisplayDateStart.Value.ToDateOnly().ToShortDateString() : "Unknown";
        string message =
            $"User attempted to create an entry dated {date.ToShortDateString()}. The date of account creation is {accountCreationDate}";
        ErrorOccurred?.Invoke(this, new AuditEntry { Message = message });
        int count = 0;
        Brush originalBackground = DatePicker.Background;
        _tooltip.IsOpen = true;
        Timer t = new(500);
        t.Elapsed += (_, _) =>
        {
            count++;
            if (DatePickerBackground == Brushes.Red)
            {
                DatePickerBackground = originalBackground;
                _tooltip.Dispatcher.Invoke(() => _tooltip.IsOpen = false);
            }
            else
            {
                DatePickerBackground = Brushes.Red;
                _tooltip.Dispatcher.Invoke(() => _tooltip.IsOpen = true);
            }
            if (count > 11)
            {
                t.Stop();
                t.Dispose();
                DatePickerBackground = originalBackground;
                _tooltip.Dispatcher.Invoke(() => _tooltip.IsOpen = false);
            }
        };
        DatePickerBackground = Brushes.Red;
        t.AutoReset = true;
        t.Start();
        return Task.CompletedTask;
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

    public void SetMinDate(DateOnly d)
    {
        DatePicker.DisplayDateStart = d.ToDateTime(new TimeOnly());
    }

    public void CreateNew()
    {
        if (Editing)
        {
            return;
        }
        SetEditingState(true, "creating");
        DatePicker.Focus();
    }

    public void EditEntry(TransactionInfoLine entry)
    {
        if (Editing)
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
        DatePicker.Focus();
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
                AccountSelector.Focus();
                break;

            case Key.D:
                Debit = true;
                AccountSelector.Focus();
                break;

            case Key.Tab:
                if (DatePicker.IsKeyboardFocusWithin)
                {
                    CreditDebitPanel.Focus();
                }
                else if (CreditDebitPanel.IsFocused)
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

            case Key.Delete:
                IInputElement focused = Keyboard.FocusedElement;
                SimulateKeyDownEvent(focused, Key.Delete);
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

    private static void SimulateKeyDownEvent(IInputElement c, Key k)
    {
        c.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, Keyboard.PrimaryDevice.ActiveSource!, 0, k) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private void SetEditingState(bool state, string type = "")
    {
        Enabled = state;
        EditingNew = type == "creating";
        EditingStateChanged?.Invoke(this, state);
        if (!state)
        {
            ClearControls();
        }
        Editing = state;
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

    private bool LightTheme()
    {
        Theme? theme = ThemeManager.Current.DetectTheme(Application.Current);
        if (theme is null)
        {
            return true;
        }
        return theme.Name.Split('.')[0] == "Light";
    }
}